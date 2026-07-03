namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Manual accounting correction, per spec section 6 and TC-AUDIT-003. A correction
/// is expressed as a new audit record; it never mutates or hides the original record.
/// In V1 a correction is an evidence record only: it does not change window usage or
/// the extra pool balance (pool changes go through <see cref="ExtraPoolAdjustment"/>).
/// </summary>
public sealed record ManualCorrection
{
    public required string SubscriptionId { get; init; }

    /// <summary>Credits the correction refers to.</summary>
    public required long Credits { get; init; }

    public required string Actor { get; init; }

    public required string Reason { get; init; }

    /// <summary>Audit id of the original record being corrected, when applicable.</summary>
    public string? RelatedAuditId { get; init; }

    public string? CorrelationId { get; init; }
}
