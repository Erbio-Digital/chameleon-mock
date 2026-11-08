using Chameleon.Engine;

namespace Chameleon.Host.Http;

// Bodies of the control API (/_chameleon/*): HTTP contracts, so they live in the Host, not the Engine.
public sealed record ClockAdvanceRequest(double Seconds);
/// From: an existing sandbox to copy; the copy takes its clock mode, so ClockMode is ignored.
/// Permanent: protected (no TTL, never deleted, reset only with confirmation).
public sealed record CreateSandboxRequest(string Id, int? TtlSeconds = null, ClockMode? ClockMode = null, string? From = null, bool Permanent = false);
public sealed record SetClockModeRequest(ClockMode Mode);
public sealed record ResendWebhookRequest(string? Url = null);
public sealed record ArmFaultRequest(
    string Connector, string Route, FaultKind Kind,
    int? Status = null, int? DelayMs = null, int? Times = null, int? DurationSeconds = null);
