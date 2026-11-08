using System.Text.Json.Serialization;
using Chameleon.Engine;
using Chameleon.Host.Auth;
using Chameleon.Host.Http;
using Chameleon.Host.Live;
using Chameleon.Host.Mcp;
using Chameleon.Host;
using Chameleon.Host.Cli;
using Chameleon.Host.Plugins;
using Chameleon.Storage.Postgres;
using Scalar.AspNetCore;

// `chameleon install|list|search` run here and exit; anything else starts the web host.
var cliConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
if (await ChameleonCli.TryRunAsync(args, cliConfig, Console.Out, CancellationToken.None) is { } exitCode)
    return exitCode;

var builder = WebApplication.CreateBuilder(args);
using var bootLog = LoggerFactory.Create(b => b.AddConsole());
// The instance never starts half configured: one critical line and exit code 1, like a connector that does not load.
try { ChameleonAuth.Validate(builder.Configuration); }
catch (InvalidOperationException ex)
{
    bootLog.CreateLogger("Chameleon.Auth").LogCritical("Chameleon cannot start: {Reason}", ex.Message);
    return 1;
}
// Local dev: secret key and CA generated once into a volume when not configured (nothing versioned).
DevSecrets.Apply(builder.Configuration);
// Bind from ASPNETCORE_URLS if set (Docker), else default to localhost:5000 (local dev).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://localhost:5000");

// Optional local mTLS port: CHAMELEON_MTLS_PORT=8443 in the compose.
if (int.TryParse(Environment.GetEnvironmentVariable(MtlsEndpoint.PortVariable), out var mtlsPort))
    builder.WebHost.ConfigureKestrel(kestrel =>
        MtlsEndpoint.ListenAll(kestrel, mtlsPort, Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000"));

// CHAMELEON_AUTH=oidc: login by any OIDC provider, personal tokens and the CI key; none (default): no login.
builder.Services.AddChameleonAuth(builder.Configuration);

// Accept enums as strings in request/response JSON (e.g. fault "kind": "Error").
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// OpenAPI doc (Swashbuckle) feeding the Scalar UI at /scalar.
// net8 has no built-in OpenAPI generator (that shipped in net9), so Swashbuckle generates it.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Engine (clock, journal, state store, webhook sender) — in-memory by default.
builder.Services.AddChameleonEngine();

// CHAMELEON_STORE=postgres swaps every store for Postgres (must come after the engine).
// Anything else (unset/"memory") keeps the ephemeral in-memory defaults.
var postgres = string.Equals(builder.Configuration["CHAMELEON_STORE"], "postgres", StringComparison.OrdinalIgnoreCase);
if (postgres)
{
    var connection = builder.Configuration["CHAMELEON_POSTGRES_CONNECTION"]
        ?? throw new InvalidOperationException("CHAMELEON_STORE=postgres requires CHAMELEON_POSTGRES_CONNECTION");
    builder.Services.AddPostgresStorage(connection);
}

// Issued connector secrets are sealed with this key; the Host does not start without it.
builder.Services.AddSingleton(sp => new SecretBox(sp.GetRequiredService<IConfiguration>()[SecretBox.KeyVariable]));
// The Chameleon CA signs the mTLS certificates; the Host does not start without it.
builder.Services.AddSingleton(sp => new CertificateAuthority(
    sp.GetRequiredService<IConfiguration>()[CertificateAuthority.CertVariable],
    sp.GetRequiredService<IConfiguration>()[CertificateAuthority.KeyVariable]));

// Journal retention (CHAMELEON_RETENTION_DAYS, default 7), webhook delivery with retries and the realtime clock.
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<WebhookWorker>();
builder.Services.AddHostedService<ClockTicker>();

// Live panel channel: events per sandbox, sent in batches after commit.
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<SignalRLiveEventBus>();
builder.Services.AddSingleton<ILiveEventBus>(sp => sp.GetRequiredService<SignalRLiveEventBus>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalRLiveEventBus>());

// MCP for developers and testers at /_chameleon/mcp: tools over this Host's own control API (stateless).
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<ControlApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "chameleon", Version = "2.0.0" };
        o.ServerInstructions = ChameleonTools.Instructions;
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<ChameleonTools>();

// Connectors are plugins: CHAMELEON_CONNECTORS resolved through the catalog, downloaded as NuGet packages,
// verified and loaded each into its own context. Any requested connector that does not load stops the startup
// with one critical log line and exit code 1.
var pluginLog = bootLog.CreateLogger("Chameleon.Plugins");
WebApplication app;
try
{
    var pluginOptions = PluginOptions.From(builder.Configuration);
    var catalog = new Catalog([]);
    if (pluginOptions.Requested.Count > 0)
    {
        using var http = new HttpClient();
        catalog = await Catalog.LoadAsync(pluginOptions.CatalogUrl, pluginOptions.Offline, http, pluginLog, CancellationToken.None);
    }
    var startupCatalog = pluginOptions.Requested.Count > 0 ? catalog : null;
    builder.Services.AddSingleton(pluginOptions);
    builder.Services.AddSingleton(sp => new MarketplaceCatalog(pluginOptions, startupCatalog, sp.GetRequiredService<ILogger<MarketplaceCatalog>>()));
    builder.Services.AddSingleton(new CommunitySearch(pluginOptions));
    await PluginLoader.LoadAsync(pluginOptions, catalog, builder.Services, pluginLog, CancellationToken.None);
    app = builder.Build();
    // Names come from the connectors DI builds; two with the same name stop the startup too.
    PluginLoader.Verify(app.Services);
}
catch (PluginLoadException ex)
{
    pluginLog.LogCritical("Chameleon cannot start: {Reason}", ex.Message);
    return 1;
}


if (!ChameleonAuth.IsEnabled(app.Configuration))
    app.Logger.LogWarning("Authentication is off (CHAMELEON_AUTH=none): anyone who reaches this instance is an operator. Use CHAMELEON_AUTH=oidc outside a local machine.");

// Any exception the endpoints do not map becomes a 500 with the standard body, never the exception text.
app.UseExceptionHandler(error => error.Run(http =>
{
    http.Response.StatusCode = StatusCodes.Status500InternalServerError;
    return http.Response.WriteAsJsonAsync(new { message = "Chameleon internal error" });
}));

// Local and CI only.
if (postgres && string.Equals(app.Configuration["CHAMELEON_MIGRATE_ON_START"], "true", StringComparison.OrdinalIgnoreCase))
    app.Services.MigratePostgres();

_ = app.Services.GetRequiredService<SecretBox>(); // fail fast on a missing or invalid key
_ = app.Services.GetRequiredService<CertificateAuthority>();

// The only sandbox that always exists: default (local/tests).
SandboxEndpoints.Seed(app.Services.GetRequiredService<ISandboxStore>());

// OpenAPI JSON (Swashbuckle) at /swagger/v1/swagger.json + Scalar UI at /scalar.
app.UseSwagger();
app.MapScalarApiReference(o => o.WithOpenApiRoutePattern("/swagger/v1/swagger.json"));

// Liveness: outside any sandbox, never touches the database.
app.MapWhen(http => http.Request.Path == $"{ChameleonEndpoints.ControlPrefix}/health",
    branch => branch.Run(http => http.Response.WriteAsJsonAsync(new { status = "ok" })));

// Live panel at /ui (and / redirects there), outside any sandbox and the journal.
app.UsePanel();

// Sandbox first (strips the /s/{id} prefix), then routing on the remaining path.
app.UseMiddleware<SandboxMiddleware>();
app.UseChameleonAuth();
app.UseRouting();

// Correlation id, capture of every connector call and commit of the request scope.
app.UseMiddleware<CaptureMiddleware>();

// Control endpoints (/_chameleon/*) and every registered connector's routes (with fault filter).
app.MapChameleon();
app.MapMarketplace();
app.MapAuth();
app.MapHub<LiveHub>(LiveHub.Path);
app.MapMcp("/_chameleon/mcp");

app.Run();
return 0;

// Exposes the implicit Program type to WebApplicationFactory<Program> in the integration tests.
public partial class Program;
