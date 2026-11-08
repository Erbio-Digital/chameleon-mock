using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Chameleon.Engine;

/// Retry policy of a webhook delivery: attempts in total, wait before the 2nd attempt (doubling
/// after that) and the timeout of each attempt.
public sealed record WebhookPolicy(int MaxAttempts = 5, int BaseDelayMs = 2000, int TimeoutMs = 10000)
{
    public static readonly WebhookPolicy Default = new();

    public string? Invalid() =>
        MaxAttempts is < 1 or > 20 ? "maxAttempts entre 1 e 20"
        : BaseDelayMs is < 0 or > 600_000 ? "baseDelayMs entre 0 e 600000"
        : TimeoutMs is < 100 or > 30_000 ? "timeoutMs entre 100 e 30000"
        : null;
}

/// A header the sandbox adds to webhooks sent to Host. Value is a secret: never shown back nor journaled.
public sealed partial record WebhookHeader(string Host, string Name, string Value)
{
    public const int Max = 10;

    /// Headers the transport owns; overriding them would break the request.
    private static readonly string[] Reserved = ["host", "content-type", "content-length", "transfer-encoding", "connection"];

    public string? Invalid() =>
        !HostPattern().IsMatch(Host ?? "") ? $"invalid host: {Host} (the name only, no scheme, port or path)"
        : !NamePattern().IsMatch(Name ?? "") ? $"invalid header name: {Name}"
        : Reserved.Contains(Name!.ToLowerInvariant()) ? $"header reservado: {Name}"
        : string.IsNullOrEmpty(Value) || Value.Length > 2048 || Value.Any(char.IsControl) ? $"invalid value for {Name}"
        : null;

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial System.Text.RegularExpressions.Regex HostPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();
}

public enum DeliveryStatus { Pending, Delivering, Delivered, DeadLettered }

/// One notification to the app under test (the outbox row). Created in the same commit as the
/// transition that caused it; delivered by the worker with retries.
public sealed class WebhookDelivery
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string SandboxId { get; init; }
    public required string Connector { get; init; }
    public required string EntityId { get; init; }
    public string? CorrelationId { get; init; }
    public required string Url { get; set; }
    public required string Payload { get; init; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public int BaseDelayMs { get; init; }
    public int TimeoutMs { get; init; }
    /// Fault applied when it was queued (delay, reorder, duplicate), shown with the delivery.
    public string? Note { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }

    public WebhookDelivery Copy() => (WebhookDelivery)MemberwiseClone();
}

public sealed record WebhookAttempt(
    string DeliveryId, int Number, DateTimeOffset At, string Url,
    int? StatusCode, long DurationMs, string? ResponseBody, string? Error, bool Blocked);

/// Wakes the delivery of a webhook once it is due. The outbox stays the source of truth:
/// a lost signal only waits for the next sweep. Locally the worker polls, so the signal does nothing.
public interface IWebhookSignal
{
    void Signal(WebhookDelivery delivery);
}

public sealed class NoWebhookSignal : IWebhookSignal
{
    public void Signal(WebhookDelivery delivery) { }
}

/// Persistence port for webhook deliveries (in-memory, Postgres).
public interface IWebhookOutbox
{
    void Enqueue(IReadOnlyCollection<WebhookDelivery> deliveries);

    /// Takes deliveries that are due (or whose lease expired) with a fresh lease token.
    IReadOnlyList<WebhookDelivery> Lease(DateTimeOffset utcNow, TimeSpan leaseFor, int max);

    /// Same as Lease, for one delivery (the queue names it): null when it is not due or someone holds it.
    WebhookDelivery? LeaseById(string sandboxId, string id, DateTimeOffset utcNow, TimeSpan leaseFor);

    /// Records the attempt and the delivery's new state, only if the caller still holds the lease.
    bool Complete(WebhookDelivery delivery, WebhookAttempt attempt, Interaction journal);

    /// Back to Pending with no attempt (held delivery), only if the caller still holds the lease.
    bool Postpone(WebhookDelivery delivery, DateTimeOffset nextAttemptAt);

    IReadOnlyList<WebhookDelivery> Find(string sandboxId, string? entityId = null, DeliveryStatus? status = null);
    WebhookDelivery? Get(string sandboxId, string id);
    IReadOnlyList<WebhookAttempt> Attempts(string deliveryId);

    /// One more attempt, now, optionally to another URL. False if it is still in flight.
    bool Resend(string sandboxId, string id, string? url, DateTimeOffset utcNow);

    void DeleteSandbox(string sandboxId);
    int DeleteCreatedBefore(DateTimeOffset before);
}

public sealed class InMemoryWebhookOutbox(IInteractionStore interactions) : IWebhookOutbox
{
    private readonly Dictionary<string, WebhookDelivery> _deliveries = new();
    private readonly List<WebhookAttempt> _attempts = new();
    private readonly object _gate = new();

    public void Enqueue(IReadOnlyCollection<WebhookDelivery> deliveries)
    {
        lock (_gate)
            foreach (var d in deliveries)
                _deliveries[d.Id] = d.Copy();
    }

    public IReadOnlyList<WebhookDelivery> Lease(DateTimeOffset utcNow, TimeSpan leaseFor, int max)
    {
        lock (_gate)
        {
            var due = _deliveries.Values
                .Where(d => IsDue(d, utcNow))
                .OrderBy(d => d.NextAttemptAt)
                .Take(max)
                .ToList();
            return due.Select(d => Take(d, utcNow, leaseFor)).ToList();
        }
    }

    public WebhookDelivery? LeaseById(string sandboxId, string id, DateTimeOffset utcNow, TimeSpan leaseFor)
    {
        lock (_gate)
            return _deliveries.TryGetValue(id, out var d) && d.SandboxId == sandboxId && IsDue(d, utcNow)
                ? Take(d, utcNow, leaseFor)
                : null;
    }

    private static bool IsDue(WebhookDelivery d, DateTimeOffset utcNow) =>
        (d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= utcNow)
        || (d.Status == DeliveryStatus.Delivering && d.LeaseUntil < utcNow);

    private static WebhookDelivery Take(WebhookDelivery d, DateTimeOffset utcNow, TimeSpan leaseFor)
    {
        d.Status = DeliveryStatus.Delivering;
        d.LeaseUntil = utcNow + leaseFor;
        d.LeaseToken = Guid.NewGuid().ToString("N");
        return d.Copy();
    }

    public bool Complete(WebhookDelivery delivery, WebhookAttempt attempt, Interaction journal)
    {
        lock (_gate)
        {
            if (!_deliveries.TryGetValue(delivery.Id, out var current)
                || current.Status != DeliveryStatus.Delivering || current.LeaseToken != delivery.LeaseToken)
                return false;

            _deliveries[delivery.Id] = delivery.Copy();
            _attempts.Add(attempt);
            interactions.Append([journal]);
            return true;
        }
    }

    public bool Postpone(WebhookDelivery delivery, DateTimeOffset nextAttemptAt)
    {
        lock (_gate)
        {
            if (!_deliveries.TryGetValue(delivery.Id, out var current)
                || current.Status != DeliveryStatus.Delivering || current.LeaseToken != delivery.LeaseToken)
                return false;
            current.Status = DeliveryStatus.Pending;
            current.NextAttemptAt = nextAttemptAt;
            current.LeaseUntil = null;
            return true;
        }
    }

    public IReadOnlyList<WebhookDelivery> Find(string sandboxId, string? entityId = null, DeliveryStatus? status = null)
    {
        lock (_gate)
            return _deliveries.Values
                .Where(d => d.SandboxId == sandboxId && (entityId is null || d.EntityId == entityId) && (status is null || d.Status == status))
                .OrderBy(d => d.CreatedAt)
                .Select(d => d.Copy())
                .ToList();
    }

    public WebhookDelivery? Get(string sandboxId, string id)
    {
        lock (_gate)
            return _deliveries.TryGetValue(id, out var d) && d.SandboxId == sandboxId ? d.Copy() : null;
    }

    public IReadOnlyList<WebhookAttempt> Attempts(string deliveryId)
    {
        lock (_gate) return _attempts.Where(a => a.DeliveryId == deliveryId).OrderBy(a => a.Number).ToList();
    }

    public bool Resend(string sandboxId, string id, string? url, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            if (!_deliveries.TryGetValue(id, out var d) || d.SandboxId != sandboxId) return false;
            if (d.Status is DeliveryStatus.Pending or DeliveryStatus.Delivering) return false;
            d.Status = DeliveryStatus.Pending;
            d.MaxAttempts = d.Attempts + 1;
            d.NextAttemptAt = utcNow;
            d.LeaseToken = null;
            if (url is not null) d.Url = url;
            return true;
        }
    }

    public void DeleteSandbox(string sandboxId)
    {
        lock (_gate)
        {
            var ids = _deliveries.Values.Where(d => d.SandboxId == sandboxId).Select(d => d.Id).ToHashSet();
            foreach (var id in ids) _deliveries.Remove(id);
            _attempts.RemoveAll(a => ids.Contains(a.DeliveryId));
        }
    }

    public int DeleteCreatedBefore(DateTimeOffset before)
    {
        lock (_gate)
        {
            var ids = _deliveries.Values.Where(d => d.CreatedAt < before).Select(d => d.Id).ToHashSet();
            foreach (var id in ids) _deliveries.Remove(id);
            _attempts.RemoveAll(a => ids.Contains(a.DeliveryId));
            return ids.Count;
        }
    }
}

/// A destination the policy refuses: no connection is attempted.
public sealed class WebhookBlockedException(string reason) : Exception(reason);

/// SSRF policy for webhook destinations. Scheme and allowlist are checked before the
/// request; addresses are checked at connect time, after DNS resolution (no rebinding window).
public sealed class WebhookDestinationPolicy(IReadOnlyCollection<string> allowedHosts)
{
    public const string AllowedHostsKey = "CHAMELEON_WEBHOOK_ALLOWED_HOSTS";

    public static WebhookDestinationPolicy FromConfiguration(IConfiguration configuration) => new(
        (configuration[AllowedHostsKey] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public bool IsAllowedHost(string host) =>
        allowedHosts.Count == 0 || allowedHosts.Any(a => a.StartsWith('.')
            ? host.EndsWith(a, StringComparison.OrdinalIgnoreCase)
            : host.Equals(a, StringComparison.OrdinalIgnoreCase));

    private bool IsListed(string host) => allowedHosts.Count > 0 && IsAllowedHost(host);

    /// Null when the URL may be attempted, else the reason it is refused.
    public string? Refuse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "invalid destination: use http or https";
        if (!IsAllowedHost(uri.IdnHost)) return $"destination outside the allowlist: {uri.IdnHost}";
        return null;
    }

    /// Link-local (cloud metadata), unspecified, multicast and reserved addresses are never reachable;
    /// loopback and private ranges only for a listed host. NAT64 (64:ff9b::/96) is judged by its IPv4.
    public string? RefuseAddress(string host, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b
            && b[4..12].All(x => x == 0))
        {
            address = new IPAddress(b[12..]);
            b = address.GetAddressBytes();
        }
        var v4 = address.AddressFamily == AddressFamily.InterNetwork;

        if (v4 && b[0] == 169 && b[1] == 254 || address.IsIPv6LinkLocal)
            return $"link-local address blocked: {address}";
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6Multicast || v4 && b[0] >= 224)
            return $"non-routable address blocked: {address}";
        if (IsListed(host)) return null;
        if (IPAddress.IsLoopback(address)) return $"loopback address blocked: {address}";
        if (v4 && (b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19)))
            return $"private network address blocked: {address}";
        if (!v4 && (address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal))
            return $"private network address blocked: {address}";
        return null;
    }

    /// The transport the dispatcher uses: resolves, checks every address, then connects. No redirects.
    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        // A proxy would make the connect check judge the proxy instead of the destination.
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
            foreach (var address in addresses)
                if (RefuseAddress(host, address) is { } reason)
                    throw new WebhookBlockedException(reason);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

public sealed record DispatchResult(int? StatusCode, long DurationMs, string? ResponseBody, string? Error, bool Blocked);

/// HTTP out to the app under test (I/O boundary).
public interface IWebhookDispatcher
{
    /// clientCertificate: presented in the TLS handshake (mTLS webhook).
    Task<DispatchResult> SendAsync(string url, string payload, TimeSpan timeout, CancellationToken ct, X509Certificate2? clientCertificate = null,
        IReadOnlyList<WebhookHeader>? headers = null);
}

/// Builds the transport that presents a client certificate; tests swap it for a capturing handler.
public delegate HttpMessageHandler CertificateHandlerFactory(X509Certificate2 certificate);

/// One client per certificate: the connection pool is per destination, so a shared client would
/// reuse one connector's certificate for another's webhook to the same host.
public sealed class CertificateWebhookClients(CertificateHandlerFactory factory)
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public HttpClient For(X509Certificate2 certificate) => _clients.GetOrAdd(certificate.Thumbprint,
        _ => new HttpClient(factory(certificate), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan });

    public static HttpMessageHandler Default(WebhookDestinationPolicy policy, X509Certificate2 certificate)
    {
        var handler = policy.CreateHandler();
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        return handler;
    }
}

public sealed class HttpWebhookDispatcher(HttpClient http, WebhookDestinationPolicy policy, CertificateWebhookClients? certificateClients = null) : IWebhookDispatcher
{
    public async Task<DispatchResult> SendAsync(string url, string payload, TimeSpan timeout, CancellationToken ct,
        X509Certificate2? clientCertificate = null, IReadOnlyList<WebhookHeader>? headers = null)
    {
        var client = clientCertificate is not null && certificateClients is not null ? certificateClients.For(clientCertificate) : http;
        if (policy.Refuse(url) is { } refused) return new DispatchResult(null, 0, null, refused, Blocked: true);

        var watch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Serialized up front so the request carries Content-Length, like a real PSP (no chunked body).
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            foreach (var h in headers ?? []) request.Headers.TryAddWithoutValidation(h.Name, h.Value);
            using var resp = await client.SendAsync(request, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return new DispatchResult((int)resp.StatusCode, watch.ElapsedMilliseconds, body, null, false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebhookBlockedException blocked)
        {
            return new DispatchResult(null, watch.ElapsedMilliseconds, null, blocked.Message, Blocked: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            var error = ex is OperationCanceledException ? $"timeout apos {timeout.TotalMilliseconds} ms" : ex.Message;
            return new DispatchResult(null, watch.ElapsedMilliseconds, null, error, false);
        }
    }
}
