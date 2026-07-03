namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// One append-only audit record, per spec section 3.4. Records are never mutated;
/// corrections appear as separate records referencing the original.
/// </summary>
public sealed record AuditRecord
{
    /// <summary>Stable public identifier, usable as an audit reference.</summary>
    public required string AuditId { get; init; }

    public required AuditRecordType RecordType { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? UserId { get; init; }

    public string? SubscriptionId { get; init; }

    /// <summary>Requested credits for usage decisions; correction credits for manual corrections.</summary>
    public long? Credits { get; init; }

    public long? CoveredBySubscriptionAllowance { get; init; }

    public long? CoveredByExtraPool { get; init; }

    /// <summary>Decision outcome; only set for usage decision records.</summary>
    public UsageDecisionResult? DecisionResult { get; init; }

    public string? Reason { get; init; }

    public string? CorrelationId { get; init; }

    public string? IdempotencyKey { get; init; }

    /// <summary>Actor or source of the record: an operator identity or a system source.</summary>
    public string? Actor { get; init; }

    /// <summary>Signed extra pool change caused by this record, when the pool changed.</summary>
    public long? ExtraPoolDelta { get; init; }

    /// <summary>Extra pool balance after this record, when the pool changed.</summary>
    public long? ExtraPoolBalanceAfter { get; init; }

    /// <summary>Audit id of a related record, e.g. the original record a correction refers to.</summary>
    public string? RelatedAuditId { get; init; }
}
