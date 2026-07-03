namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Filter for querying the audit trail of a user or subscription, per spec 3.4.
/// At least one of <see cref="UserId"/> or <see cref="SubscriptionId"/> must be set,
/// so that one caller cannot enumerate other users' usage detail (spec section 5).
/// Filters combine with AND: setting both user and subscription returns only records
/// carrying both identities. To see every record touching a subscription (including
/// other users' rejected attempts against it), query by subscription id alone.
/// </summary>
public sealed record AuditTrailQuery
{
    public string? UserId { get; init; }

    public string? SubscriptionId { get; init; }

    /// <summary>Inclusive lower bound on record time.</summary>
    public DateTimeOffset? FromInclusive { get; init; }

    /// <summary>Exclusive upper bound on record time.</summary>
    public DateTimeOffset? ToExclusive { get; init; }

    /// <summary>Maximum number of records returned, oldest first.</summary>
    public int Limit { get; init; } = 1000;
}
