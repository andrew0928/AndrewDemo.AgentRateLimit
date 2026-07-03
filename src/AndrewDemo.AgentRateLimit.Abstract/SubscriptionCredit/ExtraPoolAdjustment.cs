namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Manual extra pool change by an authorized actor, per spec 4.4 and TC-EXTRA-003.
/// </summary>
public sealed record ExtraPoolAdjustment
{
    public required string SubscriptionId { get; init; }

    /// <summary>Signed credit change. Must be non-zero; the resulting balance must not go negative.</summary>
    public required long DeltaCredits { get; init; }

    public required string Actor { get; init; }

    public required string Reason { get; init; }

    public string? CorrelationId { get; init; }
}
