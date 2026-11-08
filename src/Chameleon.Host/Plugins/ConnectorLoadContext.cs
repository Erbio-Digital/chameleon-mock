using System.Reflection;
using System.Runtime.Loader;

namespace Chameleon.Host.Plugins;

/// One isolated context per connector. Probes its own directory (the package closure, flattened),
/// delegates shared assemblies to the default context.
public sealed class ConnectorLoadContext(string name, string directory) : AssemblyLoadContext(name, isCollectible: false)
{
    public Assembly LoadMain(string assemblyFileName) => LoadFromAssemblyPath(Path.Combine(directory, assemblyFileName));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (SharedAssemblies.IsShared(assemblyName)) return null; // falls back to the default context
        var path = Path.Combine(directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
