namespace Chameleon.Host.Plugins;

public sealed record PackageRef(string Id, string Version, string? Sha512 = null);

/// MainAssemblies: the .dll file names the main package contributed to Directory.
public sealed record ResolvedConnector(PackageRef Main, IReadOnlyList<PackageRef> Closure, string Directory, IReadOnlyList<string> MainAssemblies);
