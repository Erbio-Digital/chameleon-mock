namespace Chameleon.Sdk;

/// Brazilian providers date things in Brasilia time (UTC-3, no daylight saving since 2019).
public static class Brasilia
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-3);

    public static DateOnly DateOf(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Offset).DateTime);
}
