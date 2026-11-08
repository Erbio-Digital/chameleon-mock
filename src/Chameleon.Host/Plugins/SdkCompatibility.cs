using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Chameleon.Host.Plugins;

public static class SdkCompatibility
{
    /// 0.x: same major and minor; >= 1.0: same major.
    public static bool IsCompatible(Version hostSdk, Version connectorSdk) =>
        hostSdk.Major == connectorSdk.Major && (hostSdk.Major >= 1 || hostSdk.Minor == connectorSdk.Minor);

    /// The compatible range of an SDK version, as people read it: "0.1.x" in 0.x, "1.x" from 1.0.
    public static string Range(Version v) => v.Major >= 1 ? $"{v.Major}.x" : $"{v.Major}.{v.Minor}.x";

    /// The Chameleon.Sdk version referenced by the connector's main assembly (read from metadata, without loading it).
    public static Version? ReferencedSdk(string assemblyPath)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        if (!pe.HasMetadata) return null;
        var md = pe.GetMetadataReader();
        foreach (var handle in md.AssemblyReferences)
        {
            var r = md.GetAssemblyReference(handle);
            if (md.GetString(r.Name) == "Chameleon.Sdk") return r.Version;
        }
        return null;
    }
}
