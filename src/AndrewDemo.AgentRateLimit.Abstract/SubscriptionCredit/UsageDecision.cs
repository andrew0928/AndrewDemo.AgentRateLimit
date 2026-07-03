namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Result of a consume or preview usage request, per spec section 3.1.
/// All credit values are integers per spec 4.1; <see cref="RequestedCredits"/> is
/// <c>null</c> when the request value could not be represented as an integer.
/// Balance fields are <c>null</c> when the decision was made before the
/// subscription state was consulted (for example an invalid request).
/// </summary>
public sealed record UsageDecision
{
    public required UsageDecisionResult Result { get; init; }

    /// <summary>Rejection, invalid or conflict reason; <c>null</c> when accepted.</summary>
    public string? Reason { get; init; }

    public string? UserId { get; init; }

    public string? SubscriptionId { get; init; }

    public long? RequestedCredits { get; init; }

    /// <summary>Credits covered by the subscription window allowance. Zero unless accepted.</summary>
    public long CoveredBySubscriptionAllowance { get; init; }

    /// <summary>Credits covered by the extra pool. Zero unless accepted.</summary>
    public long CoveredByExtraPool { get; init; }

    public long? Remaining5hCreditsAfterDecision { get; init; }

    public long? Remaining7dCreditsAfterDecision { get; init; }

    public long? RemainingExtraPoolCreditsAfterDecision { get; init; }

    /// <summary>Time at which the decision was evaluated.</summary>
    public DateTimeOffset DecisionTime { get; init; }

    /// <summary>
    /// Reference to the audit record backing this decision. For an idempotent replay
    /// this is the original decision's audit reference. Preview decisions carry no
    /// audit reference because previews are not accounting records.
    /// </summary>
    public string? AuditReference { get; init; }

    /// <summary>
    /// True when this response replays a previously stored decision for the same
    /// idempotency key and payload, per spec 4.6.
    /// </summary>
    public bool IsIdempotentReplay { get; init; }

    public string? CorrelationId { get; init; }

    public string? IdempotencyKey { get; init; }
}
