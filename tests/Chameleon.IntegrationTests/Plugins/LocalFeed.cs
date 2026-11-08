namespace Chameleon.IntegrationTests.Plugins;

/// The run's local feed (packed once by TestPlugins) and fresh plugin directories for resolver tests.
public sealed class LocalFeed
{
    public string Path => TestPlugins.Feed;

    public string NewPluginsDir() => TestPlugins.NewPluginsDir();
}
