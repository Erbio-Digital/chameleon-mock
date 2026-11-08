namespace Chameleon.Engine;

/// The current sandbox's clock. Tests advance it explicitly instead of waiting on wall time.
public sealed class SandboxClock(SandboxContext context)
{
    public DateTimeOffset Now => context.Sandbox.Now(DateTimeOffset.UtcNow);

    public void Advance(TimeSpan by)
    {
        context.Sandbox.Advance(by, DateTimeOffset.UtcNow);
        context.Changed = true;
    }
}
