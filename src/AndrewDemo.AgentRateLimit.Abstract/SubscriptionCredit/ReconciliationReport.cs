namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Reconciliation report for a time period, per spec section 3.5. The period is
/// half-open: <c>FromInclusive &lt;= record time &lt; ToExclusive</c>.
/// </summary>
public sealed record ReconciliationReport
{
    public required DateTimeOffset PeriodFromInclusive { get; init; }

    public required DateTimeOffset PeriodToExclusive { get; init; }

    /// <summary>Per-subscription rows, ordered by subscription id.</summary>
    public required IReadOnlyList<ReconciliationSubscriptionRow> Subscriptions { get; init; }

    /// <summary>
    /// Count of invalid requests in the period that could not be attributed to any
    /// subscription (for example requests missing a subscription id).
    /// </summary>
    public required int UnattributedInvalidRequestCount { get; init; }
}

/// <summary>
/// Credit movement for one subscription within the report period. The extra pool
/// figures satisfy: ending = beginning + added + adjusted - consumed.
/// </summary>
public sealed record ReconciliationSubscriptionRow
{
    public required string SubscriptionId { get; init; }

    public string? UserId { get; init; }

    public required long AcceptedCredits { get; init; }

    public required long RejectedCredits { get; init; }

    public required long CoveredBySubscriptionAllowanceCredits { get; init; }

    public required long CoveredByExtraPoolCredits { get; init; }

    public required long ExtraPoolBeginningBalance { get; init; }

    /// <summary>Credits added to the extra pool in the period (seed and positive adjustments).</summary>
    public required long ExtraPoolAddedCredits { get; init; }

    /// <summary>Credits consumed from the extra pool by accepted usage in the period.</summary>
    public required long ExtraPoolConsumedCredits { get; init; }

    /// <summary>Net negative adjustments applied to the extra pool in the period (zero or negative).</summary>
    public required long ExtraPoolAdjustedCredits { get; init; }

    public required long ExtraPoolEndingBalance { get; init; }

    public required int AcceptedRequestCount { get; init; }

    public required int RejectedRequestCount { get; init; }

    public required int ConflictCount { get; init; }

    public required int InvalidRequestCount { get; init; }

    public required int ManualCorrectionCount { get; init; }
}
