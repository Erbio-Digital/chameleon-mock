using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chameleon.Engine;

public static class ChameleonEngineExtensions
{
    /// Engine services, in-memory by default. A storage adapter (AddPostgresStorage) called
    /// afterwards overrides the stores and the committer.
    public static IServiceCollection AddChameleonEngine(this IServiceCollection services)
    {
        services.AddSingleton<ISandboxStore, InMemorySandboxStore>();
        services.AddSingleton<IEntityStore, InMemoryEntityStore>();
        services.AddSingleton<IInteractionStore, InMemoryInteractionStore>();
        services.AddSingleton<IFaultStore, InMemoryFaultStore>();
        services.AddSingleton<IWebhookOutbox, InMemoryWebhookOutbox>();
        services.AddSingleton<IChangeCommitter, StoreChangeCommitter>();
        services.AddSingleton<IAccessTokenStore, InMemoryAccessTokenStore>();

        // One scope per request: the sandbox it runs in, its clock, and what connectors
        // read/write through, committed at the end.
        services.AddScoped<SandboxContext>();
        services.AddScoped<SandboxClock>();
        services.AddScoped<RequestScope>();

        // Webhook transport: per-attempt timeout comes from the policy; the guard owns the socket (SSRF).
        services.AddSingleton(sp => WebhookDestinationPolicy.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<IWebhookDispatcher, HttpWebhookDispatcher>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<WebhookDestinationPolicy>().CreateHandler());
        services.AddSingleton<CertificateHandlerFactory>(sp =>
            certificate => CertificateWebhookClients.Default(sp.GetRequiredService<WebhookDestinationPolicy>(), certificate));
        services.AddSingleton<CertificateWebhookClients>();
        services.AddSingleton<IWebhookSignal, NoWebhookSignal>();
        return services;
    }
}
