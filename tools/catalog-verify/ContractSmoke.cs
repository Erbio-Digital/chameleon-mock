using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Chameleon.Host.Plugins;

namespace Chameleon.CatalogVerify;

/// Starts the real Host as a process with one connector: install first, then an offline start whose HTTP
/// proxy is a local listener that counts connections. Any connection during startup fails the check.
// ponytail: only detects egress through HttpClient (HTTP_PROXY); a raw socket is not counted, human review covers that case.
public static class ContractSmoke
{
    public static async Task<IReadOnlyList<VerifyFinding>> RunAsync(CatalogEntry entry, string hostDll, string packagesDir,
        string catalogPath, TimeSpan timeout, CancellationToken ct)
    {
        var findings = new List<VerifyFinding>();
        void Add(string message) => findings.Add(new(entry.Name, "contract", message));
        var work = Directory.CreateTempSubdirectory("chameleon-verify-").FullName;
        var env = new Dictionary<string, string>
        {
            ["CHAMELEON_PLUGINS_DIR"] = Path.Combine(work, "plugins"),
            ["CHAMELEON_NUGET_SOURCES"] = Path.GetFullPath(packagesDir),
            ["CHAMELEON_CATALOG_URL"] = new Uri(Path.GetFullPath(catalogPath)).AbsoluteUri,
            ["CHAMELEON_DEV_SECRETS_FILE"] = Path.Combine(work, "dev-secrets.json"),
            ["CHAMELEON_AUTH"] = "none",
        };
        try
        {
            var install = await RunToEndAsync(hostDll, ["install", entry.Name], env, timeout, ct);
            if (install.ExitCode != 0) { Add($"install failed: {install.Output.Trim()}"); return findings; }

            using var proxy = new ConnectionCounter();
            var port = FreePort();
            var run = new Dictionary<string, string>(env)
            {
                ["CHAMELEON_CONNECTORS"] = entry.Name,
                ["CHAMELEON_OFFLINE"] = "true",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
                // Both cases: on Linux .NET reads the lowercase names first (Windows names are case-insensitive).
                ["HTTP_PROXY"] = proxy.Url,
                ["HTTPS_PROXY"] = proxy.Url,
                ["NO_PROXY"] = "",
                ["http_proxy"] = proxy.Url,
                ["https_proxy"] = proxy.Url,
                ["no_proxy"] = "",
            };
            using var host = Start(hostDll, [], run);
            var output = host.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = host.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
                if (!await WaitHealthyAsync(http, host, timeout, ct))
                {
                    Kill(host);
                    var text = await output + await error;
                    Add($"the Host did not start: {(text.Length > 2000 ? text[^2000..] : text).Trim()}");
                    return findings;
                }

                using var connectors = JsonDocument.Parse(await http.GetStringAsync("/_chameleon/connectors", ct));
                var described = connectors.RootElement.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == entry.Name);
                if (described.ValueKind == JsonValueKind.Undefined) Add($"no connector named '{entry.Name}' after loading {entry.PackageId}");
                else
                {
                    if (string.IsNullOrWhiteSpace(described.GetProperty("title").GetString())) Add("Describe() has no title");
                    if (described.GetProperty("routes").GetArrayLength() == 0) Add("no provider routes mapped");
                    if (!described.GetProperty("verified").GetBoolean()) Add("loaded as unverified: the catalog hashes did not match");
                }
                if (proxy.Count > 0) Add($"{proxy.Count} outbound connection(s) during load");
            }
            finally
            {
                Kill(host);
            }
        }
        finally
        {
            // Best effort: on Windows a just-killed process may still hold the loaded dlls for a moment.
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return findings;
    }

    private static Process Start(string hostDll, string[] args, IDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(hostDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Inherited variables that would route around (ALL_PROXY) or disable (REQUEST_METHOD, CGI) the counting proxy.
        foreach (var name in new[] { "ALL_PROXY", "all_proxy", "REQUEST_METHOD" }) psi.Environment.Remove(name);
        // Inherited settings that change what is checked: an external store, or loading packages the catalog did not verify.
        foreach (var name in new[] { "CHAMELEON_STORE", "CHAMELEON_ALLOW_UNVERIFIED" }) psi.Environment.Remove(name);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start the Host");
    }

    private static async Task<(int ExitCode, string Output)> RunToEndAsync(string hostDll, string[] args, IDictionary<string, string> env, TimeSpan timeout, CancellationToken ct)
    {
        using var p = Start(hostDll, args, env);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Kill(p);
            if (ct.IsCancellationRequested) throw;
            return (-1, $"timed out after {timeout.TotalSeconds:0} s");
        }
        return (p.ExitCode, await output + await error);
    }

    private static async Task<bool> WaitHealthyAsync(HttpClient http, Process host, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !host.HasExited)
        {
            try
            {
                using var response = await http.GetAsync("/_chameleon/health", ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct);
        }
        return false;
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(5000); // releases the loaded dlls before the work dir is deleted
        }
        catch (Exception) { } // best effort: never hides the error that brought us here
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}

/// A loopback listener used as HTTP proxy: counts every connection and closes it without answering.
public sealed class ConnectionCounter : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private int count;

    public ConnectionCounter()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = AcceptAsync();
    }

    public string Url { get; }
    public int Count => Volatile.Read(ref count);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref count);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
