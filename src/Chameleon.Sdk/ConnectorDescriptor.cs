using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Chameleon.Engine;

namespace Chameleon.Sdk;

/// What a connector simulates, in a shape any client (panel, tests) can render without knowing
/// the connector: entity lifecycles and the actions that drive them.
public sealed record ConnectorDescriptor(
    string Name,
    IReadOnlyList<EntityDescriptor> Entities,
    IReadOnlyList<ConnectorAction> Actions,
    // Default webhook retry policy, close to the real provider's; a sandbox can override it.
    WebhookPolicy? Webhooks = null,
    // Null when the provider has no registrations (the URL comes in each request).
    WebhookRegistrations? Registrations = null)
{
    /// Catalog card: display name, kind of provider (banking, fiscal, ...) and one line on what it simulates.
    public string? Title { get; init; }
    public string Category { get; init; } = "other";
    public string? Description { get; init; }

    /// Issued on subscription besides BASE_URL: ids, secrets, URLs under the base.
    public IReadOnlyList<IssuedCredential> Credentials { get; init; } = [];

    /// What the service under test must fill in.
    public IReadOnlyList<ConnectorSetting> Settings { get; init; } = [];

    /// Sample notifications to the configured destinations; null = no test event.
    [JsonIgnore] public Func<ConnectorContext, IReadOnlyList<TestEvent>>? TestEvents { get; init; }
    public bool HasTestEvent => TestEvents is not null;

    /// Banking connector: keeps sandbox accounts, managed by the generic account routes.
    public bool HasAccounts { get; init; }

    /// The real provider requires a client certificate (mTLS): so does the sandbox, outside default.
    public bool ClientCertificate { get; init; }

    /// The real provider calls the app's webhook with its own certificate: so does the simulator.
    public bool WebhookCertificate { get; init; }

    /// Headers the real provider computes per attempt (e.g. an HMAC of timestamp + body). Run at send
    /// time, so a retry carries a fresh timestamp, as the provider does.
    [JsonIgnore] public Func<WebhookSigningInput, IReadOnlyDictionary<string, string>>? WebhookSigner { get; init; }

    /// Outgoing operations settle asynchronously: the "settlement" fault route applies.
    public bool Settlement { get; init; }
}

public enum CredentialKind { Id, Secret, Url }

/// Env = the variable name the service reads (CLIENT_ID); Url values are BASE_URL + Path.
public sealed record IssuedCredential(string Env, string Label, CredentialKind Kind, string? Path = null);

public sealed record ConnectorSetting(string Key, string Label, string Kind = "text", bool Required = false, string? Help = null);

public sealed record TestEvent(string Url, object Payload);

public sealed record EntityDescriptor(
    string Type,
    string Key,
    string Initial,
    IReadOnlyList<string> States,
    IReadOnlyList<TransitionDescriptor> Transitions,
    // How the control API finds and reads these entities; not part of the published contract.
    [property: JsonIgnore] Type? ClrType = null,
    [property: JsonIgnore] Func<object, string>? StateOf = null);

public sealed record TransitionDescriptor(string From, string To, string Trigger);

public sealed record ActionParameter(string Name, string Type = "string", bool Required = false);

public sealed record ConnectorAction(
    string Name,
    string EntityType,
    string Label,
    IReadOnlyList<string> AllowedFrom,
    IReadOnlyList<ActionParameter> Parameters,
    [property: JsonIgnore] Func<ConnectorContext, string, JsonElement?, Task<IResult>> Run)
{
    /// Runs on a key that does not exist yet and creates the entity: the panel offers it before
    /// the app ever touched that key (a lookup cache in the app means arming before the first call).
    public bool CreatesMissing { get; init; }
}
