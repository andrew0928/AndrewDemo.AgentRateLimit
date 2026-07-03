namespace AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

/// <summary>
/// Forward-only controllable clock. Tests advance time explicitly instead of using
/// realtime sleep, per the repo test rules.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset startUtc)
    {
        _utcNow = startUtc.ToUniversalTime();
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "The clock is forward-only.");
        }

        _utcNow += delta;
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        utcNow = utcNow.ToUniversalTime();
        if (utcNow < _utcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(utcNow), utcNow, "The clock is forward-only.");
        }

        _utcNow = utcNow;
    }
}
