using System.Security.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Chameleon.Engine;

namespace Chameleon.Host.Http;

/// The mTLS surface: a TLS port that requires a client certificate chained
/// to the Chameleon CA, checked in the handshake. Which sandbox the certificate belongs to is checked
/// later, in the connector filter.
public static class MtlsEndpoint
{
    public const string PortVariable = "CHAMELEON_MTLS_PORT";
    public const string HostnamesVariable = "CHAMELEON_MTLS_HOSTNAMES";

    public static IReadOnlyCollection<string> Hostnames() =>
        (Environment.GetEnvironmentVariable(HostnamesVariable) ?? "localhost")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static void Configure(ListenOptions listen, CertificateAuthority ca, IReadOnlyCollection<string> hostnames) =>
        listen.UseHttps(https =>
        {
            https.ServerCertificate = ca.IssueServer(hostnames);
            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => ca.Issued(certificate);
        });

    /// Listening on the mTLS port replaces ASPNETCORE_URLS, so the plain HTTP ones are listened to here too.
    public static void ListenAll(KestrelServerOptions kestrel, int mtlsPort, string urls)
    {
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var address = BindingAddress.Parse(url);
            if (IsAnyHost(address.Host)) kestrel.ListenAnyIP(address.Port);
            else kestrel.ListenLocalhost(address.Port);
        }
        kestrel.ListenAnyIP(mtlsPort, listen =>
            Configure(listen, kestrel.ApplicationServices.GetRequiredService<CertificateAuthority>(), Hostnames()));
    }

    /// The wildcard hosts ASP.NET Core accepts in ASPNETCORE_URLS (the image uses http://+:8080).
    public static bool IsAnyHost(string host) => host is "+" or "*" or "0.0.0.0" or "[::]" or "::";
}
