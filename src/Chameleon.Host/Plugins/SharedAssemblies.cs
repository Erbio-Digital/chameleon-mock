using System.Reflection;

namespace Chameleon.Host.Plugins;

public static class SharedAssemblies
{
    private static readonly string[] Exact = ["Chameleon.Engine", "Chameleon.Sdk", "netstandard", "mscorlib"];
    private static readonly string[] Prefixes = ["Microsoft.AspNetCore.", "Microsoft.Extensions.", "System."];

    /// True when the assembly must come from the host (default context) so contract types match.
    public static bool IsShared(AssemblyName name) =>
        name.Name is { } n && (Exact.Contains(n) || Prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));
}
