namespace Chameleon.Sdk;

/// What a provider signs on each webhook attempt: the exact body sent, the instant of the attempt,
/// the delivery note (an action can ask for a broken signature through it) and the issued
/// credentials of the subscription (null when absent, e.g. the default sandbox).
public sealed record WebhookSigningInput(string Body, DateTimeOffset UtcNow, string? Note, Func<string, string?> Credential);
