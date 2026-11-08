namespace Chameleon.IntegrationTests;

/// Timing-sensitive and MCP tests run alone, after the parallel classes, so CPU starvation cannot break them.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Serial
{
    public const string Name = "serial";
}
