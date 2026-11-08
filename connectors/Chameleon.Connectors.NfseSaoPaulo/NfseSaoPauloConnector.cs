using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Sdk;
using Chameleon.Engine;

namespace Chameleon.Connectors.NfseSaoPaulo;

/// Sao Paulo city hall NFS-e reference connector — the second connector, proving the
/// SDK holds for a different lifecycle (async processing + TTL + reject).
/// Lifecycle: Recebido -> Processando -> {Autorizada, Rejeitada}.
public sealed class NfseSaoPauloConnector : IConnector
{
    public const string ConnectorName = "nfse-sp";

    internal static readonly Lifecycle<Nfse, NfseState> Nota =
        new Lifecycle<Nfse, NfseState>("nfse", NfseState.Recebido, n => n.Rps, n => n.State, (n, s) => n.State = s)
            .Allow(NfseState.Recebido, NfseState.Processando, "receive")
            .Allow(NfseState.Processando, NfseState.Autorizada, "authorize")
            .Allow(NfseState.Processando, NfseState.Rejeitada, "reject");

    private static readonly ConnectorAction Authorize = Nota.Action("authorize", "Authorize invoice",
        async (ctx, nfse, _) =>
        {
            await Autorizar(ctx, nfse, "action");
            return Results.Ok(new { rps = nfse.Rps, status = nfse.State.ToString(), nfse.Protocolo });
        });

    private static readonly ConnectorAction Reject = Nota.Action("reject", "Reject invoice", Rejeitar,
        new ActionParameter("reason"));

    public string Name => ConnectorName;

    // ponytail: the city does not publish a retry schedule; 3 attempts doubling from 5 s is our default.
    private static readonly WebhookPolicy Policy = new(MaxAttempts: 3, BaseDelayMs: 5000, TimeoutMs: 10000);

    private const string CallbackSetting = "callbackUrl";

    public ConnectorDescriptor Describe() => new(Name, [Nota.Describe("rps")], [Authorize, Reject], Policy)
    {
        Title = "NFS-e Sao Paulo",
        Category = "fiscal",
        Description = "NFS-e issuance by RPS, with authorization or rejection.",
        // The real NFS-e authenticates with a digital certificate (out of scope): user and password stand in for it.
        Credentials = [new("USERNAME", "User", CredentialKind.Id), new("PASSWORD", "Password", CredentialKind.Secret)],
        Settings = [new(CallbackSetting, "Callback URL", "url", Required: true, Help: "Receives the result of the invoice authorization or rejection.")],
        TestEvents = ctx => ctx.Setting(CallbackSetting) is { } url
            ? [new TestEvent(url, new { rps = "test-event", evento = "nfse.teste", em = ctx.Now })]
            : [],
    };

    /// Subscribed: Basic with the issued user and password. Without a subscription (only "default"
    /// gets here) the note stays open, as before.
    private static bool Authorized(HttpContext http, ConnectorContext ctx)
    {
        if (ctx.Subscription is null) return true;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }
        var split = decoded.IndexOf(':');
        return split > 0 && ctx.CredentialMatches("USERNAME", decoded[..split]) && ctx.CredentialMatches("PASSWORD", decoded[(split + 1)..]);
    }

    /// Subscribed: only the callback URL the service filled in; else the simulator-only field.
    private static string? Destino(ConnectorContext ctx, Nfse nfse) =>
        ctx.Subscription is null ? nfse.WebhookUrl : ctx.Setting(CallbackSetting);

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/nfse-sp/nfse", EmitirNfse);
        routes.MapGet("/nfse-sp/nfse/{rps}", ConsultarNfse);

        // Legacy action routes, aliases of the generic action route.
        routes.MapPost("/_chameleon/nfse-sp/nfse/{rps}/authorize", (string rps, ConnectorContext ctx) => Authorize.Run(ctx, rps, null));
        routes.MapPost("/_chameleon/nfse-sp/nfse/{rps}/reject", async (string rps, HttpContext http, ConnectorContext ctx) =>
            await Reject.Run(ctx, rps, await http.Request.ReadActionArgsAsync()));
    }

    /// TTL: any Processando past ProcessaAte auto-authorizes (the happy path over virtual time).
    public async Task OnClockAdvancedAsync(ConnectorContext ctx)
    {
        foreach (var n in ctx.Due<Nfse>().Where(n => n.State == NfseState.Processando))
            await Autorizar(ctx, n, "ttl");
    }

    private static IResult EmitirNfse(EmitirNfseRequest req, HttpContext http, ConnectorContext ctx)
    {
        if (!Authorized(http, ctx)) return Results.Unauthorized();
        var rps = string.IsNullOrWhiteSpace(req.Rps) ? Guid.NewGuid().ToString("N") : req.Rps;
        http.Annotate(entityId: rps);

        // Same RPS -> the same note, never a duplicate.
        if (ctx.Store.Get<Nfse>(rps) is { } existing)
        {
            http.Annotate(note: "idempotent hit");
            return Results.Ok(new { rps, status = existing.State.ToString() });
        }

        // Recebido is instantaneous: the note is created already Processando (async, like the real one).
        var nfse = new Nfse
        {
            Rps = rps,
            Valor = req.Valor,
            WebhookUrl = req.WebhookUrl,
            RecebidaEm = ctx.Now,
            ProcessaAte = ctx.Now.AddMinutes(5),
            State = NfseState.Processando,
        };
        ctx.Create(Nota, nfse);

        return Results.Ok(new { rps, status = nfse.State.ToString() });
    }

    private static IResult ConsultarNfse(string rps, HttpContext http, ConnectorContext ctx) =>
        !Authorized(http, ctx) ? Results.Unauthorized()
        : ctx.Store.Get<Nfse>(rps) is { } nfse
            ? Results.Ok(new { nfse.Rps, status = nfse.State.ToString(), nfse.Valor, nfse.Protocolo, nfse.MotivoRejeicao })
            : Results.NotFound();

    private static async Task<IResult> Rejeitar(ConnectorContext ctx, Nfse nfse, JsonElement? args)
    {
        var motivo = args is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("reason", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "rejeitada pelo simulador";

        ctx.Move(Nota, nfse, NfseState.Rejeitada, motivo);
        nfse.MotivoRejeicao = motivo;
        await ctx.NotifyAsync(nfse.Rps, Destino(ctx, nfse), new { rps = nfse.Rps, evento = "nfse.rejeitada", motivo, em = ctx.Now });

        return Results.Ok(new { rps = nfse.Rps, status = nfse.State.ToString(), motivo });
    }

    /// Shared authorize path used by both the action and the TTL auto-authorize.
    private static async Task Autorizar(ConnectorContext ctx, Nfse nfse, string reason)
    {
        ctx.Move(Nota, nfse, NfseState.Autorizada, reason);
        nfse.Protocolo = $"SP-{nfse.Rps}-{ctx.Now.Ticks}";
        await ctx.NotifyAsync(nfse.Rps, Destino(ctx, nfse),
            new { rps = nfse.Rps, evento = "nfse.autorizada", protocolo = nfse.Protocolo, em = ctx.Now });
    }
}

public sealed record EmitirNfseRequest(decimal Valor, string? WebhookUrl = null, string? Rps = null);
