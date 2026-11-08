using System.Reflection;

namespace Chameleon.Host.Plugins;

/// A plugin package whose main assembly was loaded and registered.
public sealed record LoadedPackage(Assembly Assembly, string PackageId, string Version, bool Verified);

/// One IConnector built by DI from a plugin package. Name is the connector's own name (IConnector.Name).
public sealed record LoadedConnector(string Name, string PackageId, string Version, bool Verified);
