namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// A usage request submitted by an external caller, per spec section 3.1.
/// <para>
/// <see cref="RequestedCredits"/> is intentionally a <see cref="decimal"/> so that
/// non-integer inputs can be represented and rejected as <c>credits-not-integer</c>
/// rather than being silently coerced at the contract boundary.
/// </para>
/// </summary>
public sealed record UsageRequest
{
    public string? UserId { get; init; }

    public string? SubscriptionId { get; init; }

    public decimal RequestedCredits { get; init; }

    /// <summary>Caller-provided key used to prevent double charging, per spec 4.6.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Tracing identifier. Not part of the idempotency payload fingerprint.</summary>
    public string? CorrelationId { get; init; }
}
