using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Chameleon.Connectors.Pix.Common;
using Chameleon.Engine;
using Chameleon.Sdk;

namespace Chameleon.Connectors.Pix;

/// The Pix API as published by the Brazilian Central Bank (cob, cobv, pix, refund, webhook per key),
/// without the extensions of any bank. A PSP decides OAuth and webhook transport; see catalog/providers/pix.md.
public sealed class PixConnector : IConnector
{
    public const string ConnectorName = "pix";

    private static readonly HashSet<string> KnownScopes =
        ["cob.read", "cob.write", "cobv.read", "cobv.write", "pix.read", "pix.write", "webhook.read", "webhook.write"];

    internal static readonly PixCobApi<PixCharge, PixKeyWebhookEntry, PixRefundEntry> Pix =
        new(new PixOptions("/pix/v2")
        {
            CobV = true, CobVRead = "cobv.read", CobVWrite = "cobv.write", Devolucao = true, PixLookup = true,
        });

    // ponytail: the Central Bank does not publish a retry schedule; 5 attempts doubling from 2 s, as the Bradesco.
    private static readonly WebhookPolicy Policy = new(MaxAttempts: 5, BaseDelayMs: 2000, TimeoutMs: 10000);

    private static readonly WebhookRegistrations Registrations = new("Pix key", Pix.ListWebhooks, Pix.SetWebhook, Pix.UnsetWebhook);

    public string Name => ConnectorName;

    public ConnectorDescriptor Describe() => new(Name, [Pix.Describe(), Pix.DescribeRefund()],
        [Pix.Pay, Pix.Remove, .. Pix.RefundActions], Policy, Registrations)
    {
        Title = "Pix",
        Category = "banking",
        Description = "The Central Bank Pix API: cob, cobv, pix lookup, refund and webhook per key, with OAuth and mTLS.",
        Credentials = SimulatedOAuth.Credentials("/oauth/token"),
        TestEvents = ctx => Pix.ListWebhooks(ctx.Store).Select(w => new TestEvent(w.Url, new { pix = Array.Empty<object>() })).ToList(),
        Settings = [PixRefund.Mode],
        HasAccounts = true,
        ClientCertificate = true,
        WebhookCertificate = true,
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // client_credentials; the client in HTTP Basic or in the form.
        routes.MapPost("/pix/oauth/token", async (HttpContext http, ConnectorContext ctx) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "invalid_request", error_description = "use application/x-www-form-urlencoded" });
            var form = await http.Request.ReadFormAsync();
            var (basicId, basicSecret) = SimulatedOAuth.BasicClient(http.Request);
            var requested = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var granted = requested.Length == 0 ? KnownScopes.ToList() : requested.Where(KnownScopes.Contains).Distinct().ToList();
            if (granted.Count == 0) return Results.BadRequest(new { error = "invalid_scope" });
            return SimulatedOAuth.Issue(ctx, form["grant_type"], basicId ?? form["client_id"], basicSecret ?? form["client_secret"], string.Join(' ', granted));
        });

        Pix.Map(routes, SimulatedOAuth.Refuse);
    }

    public Task OnClockAdvancedAsync(ConnectorContext ctx) => Pix.OnClockAdvancedAsync(ctx);
}
