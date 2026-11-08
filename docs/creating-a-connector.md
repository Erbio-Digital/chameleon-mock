# Creating a connector

A connector is a small .NET library that simulates one provider's public API. It is packaged as a NuGet package, and the Chameleon host loads it at startup. You do not edit the engine or the host.

## Start from the template

```bash
dotnet new install Chameleon.Mock.Templates
dotnet new chameleon-connector -n Acme.Chameleon.Widgets --connectorName widgets --title "Acme Widgets"
```

- `-n` is the project name and the NuGet package id. It must not start with `Chameleon.Mock.`, which is reserved for the official packages.
- `--connectorName` is the short name used in `CHAMELEON_CONNECTORS` and as the route prefix. Use lowercase letters, digits and hyphens (`^[a-z0-9][a-z0-9-]*$`).
- `--title` is shown in the panel. It must not contain `"`, `&` or `<`.

The generated project references `Chameleon.Mock.Sdk` with the range `[0.1.0, 0.2.0)`, compile-only: the host provides the SDK at runtime, so it is not shipped in your package. In 0.x a minor version is a breaking SDK change, which is why the range stops at the next minor.

## The `IConnector` contract

```csharp
public interface IConnector
{
    string Name { get; }
    ConnectorDescriptor Describe();
    void MapRoutes(IEndpointRouteBuilder routes);
    Task OnClockAdvancedAsync(ConnectorContext ctx) => Task.CompletedTask;
}
```

- `Name` is the connector name above.
- `Describe()` returns the entities, lifecycles and actions the panel and the generic action route use, plus the title, category, description, issued credentials and webhook retry policy.
- `MapRoutes` maps the provider's surface as ordinary ASP.NET Core minimal API routes. Capture in the journal and fault injection apply to every route you map, with no code from you.
- Inject `ConnectorContext` into a handler to reach the sandbox: `ctx.Store` (state), `ctx.Now` (the sandbox clock), `ctx.Create` and `ctx.Move` (lifecycle changes), `ctx.Save`, `ctx.NotifyAsync` and `ctx.CredentialMatches`.
- Optional: implement `IConnectorModule` in the same assembly to register extra services.

## Entities and actions

Model each thing the provider tracks as a class with a `Lifecycle<TEntity, TState>`: an initial state and the allowed transitions. A transition that is not allowed becomes a `409` for the caller and the request's writes are discarded.

```csharp
private static readonly Lifecycle<Item, ItemStatus> ItemLifecycle =
    new Lifecycle<Item, ItemStatus>("item", ItemStatus.ACTIVE, i => i.Key, i => i.Status, (i, s) => i.Status = s)
        .Allow(ItemStatus.ACTIVE, ItemStatus.ARCHIVED, "archive");
```

`ItemLifecycle.Describe("id")` lists the entity in the descriptor and `ItemLifecycle.Action(...)` adds an action. An action is a button in the panel, an MCP tool call and a route you get for free:

```
POST /_chameleon/connectors/<name>/entities/<entity>/<key>/actions/<action>
```

Use actions for what the real provider does on its own: a payment settles, a ticket is solved, an invoice is rejected.

## Simulated OAuth

If the provider uses OAuth2 client credentials, add a token route and let the SDK handle it:

- `SimulatedOAuth.Credentials("/oauth/token")` in the descriptor declares `CLIENT_ID`, `CLIENT_SECRET` and the token URL that a sandbox issues on subscription (the path is relative to the connector's base URL).
- In the token route call `SimulatedOAuth.Issue(ctx, grantType, clientId, clientSecret, scope)`; read the client from HTTP Basic with `SimulatedOAuth.BasicClient(request)` if the provider allows it.
- In the other routes call `SimulatedOAuth.Refuse(ctx, http, "scope")`: it returns `null` when the bearer is valid and has the scope, otherwise 401 or 403.
- In the `default` sandbox any client id is accepted; other sandboxes check the issued credentials.

## Webhooks

After a state change, call `ctx.NotifyAsync(entityId, url, payload)`. Chameleon delivers it with retries, journals each attempt, and lets the user resend it. Set the default retry policy in the descriptor (`WebhookPolicy`), the way the real provider retries. If the provider has per-key or per-account registrations, give the descriptor a `WebhookRegistrations`. If the provider signs its webhooks, set `WebhookSigner`.

## Clock

The sandbox clock is `ctx.Now`, never `DateTime.UtcNow`: users freeze and advance it in tests. For anything that ages (a charge that expires, an invoice processed after a delay), override `OnClockAdvancedAsync` and move entities whose time has come, using `ctx.Due<T>()` or your own check against `ctx.Now`.

## Rules for connectors

- **Be faithful to the provider.** Same paths, status codes, field names, error shapes and lifecycle as the provider documents. A simulator that is "close enough" teaches your application the wrong thing.
- **The contract comes from a public source.** Build from the provider's published documentation. Do not copy proprietary or confidential material, and never include real credentials, real personal data or traffic captured from production.
- **Say what you cover.** List the operations and the source in the provider sheet (see [Verified badge](verified-badge.md)).
- **No outgoing calls from routes.** A connector answers from its own state. The only traffic leaving Chameleon is webhooks, sent through `ctx.NotifyAsync`.
- **Respect trademarks.** Describe the connector as a simulation for testing, not affiliated with the provider.

## Pack, test locally, publish

```bash
dotnet pack -c Release -o ./feed

# Run the official image against your local feed. Unverified packages need CHAMELEON_ALLOW_UNVERIFIED.
docker run -p 5000:8080 -v "$PWD/feed:/feed:ro" \
  -e CHAMELEON_CONNECTORS=Acme.Chameleon.Widgets@0.1.0 \
  -e CHAMELEON_NUGET_SOURCES=/feed \
  -e CHAMELEON_ALLOW_UNVERIFIED=true \
  ghcr.io/erbio-digital/chameleon-mock:v0.1.0
```

`CHAMELEON_NUGET_SOURCES` is a comma-separated list of folders or feed URLs searched for packages; with `Id@version` the connector is loaded as unverified. Exercise it from the panel at <http://localhost:5000/ui> or with curl, and write your own tests against its routes.

To publish, push the package to nuget.org with the tag `chameleon-connector` in `<PackageTags>` (the template already sets it, together with `chameleon-name:<connectorName>`) and a repository URL in the package metadata. Tagged packages appear in the panel's community search and in `chameleon search`, marked as unverified and loadable with `CHAMELEON_ALLOW_UNVERIFIED=true`. To get the verified badge, follow [Verified badge](verified-badge.md).
