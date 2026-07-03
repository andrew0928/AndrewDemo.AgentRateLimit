namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Kind of an append-only audit record.
/// </summary>
public enum AuditRecordType
{
    /// <summary>A consume usage decision: accepted, rejected, conflict or invalid.</summary>
    UsageDecision,

    /// <summary>Initial extra pool balance provisioned at subscription creation.</summary>
    ExtraPoolSeed,

    /// <summary>Manual extra pool top-up or reduction by an authorized actor.</summary>
    ExtraPoolAdjustment,

    /// <summary>Subscription enabled/disabled by an authorized actor.</summary>
    SubscriptionStatusChange,

    /// <summary>Manual accounting correction; never overwrites the original record.</summary>
    ManualCorrection,
}

/// <summary>
/// Wire names for <see cref="AuditRecordType"/> used in persistence and reports.
/// </summary>
public static class AuditRecordTypeNames
{
    public const string UsageDecision = "usage-decision";
    public const string ExtraPoolSeed = "extra-pool-seed";
    public const string ExtraPoolAdjustment = "extra-pool-adjustment";
    public const string SubscriptionStatusChange = "subscription-status-change";
    public const string ManualCorrection = "manual-correction";

    public static string ToWireName(AuditRecordType type) => type switch
    {
        AuditRecordType.UsageDecision => UsageDecision,
        AuditRecordType.ExtraPoolSeed => ExtraPoolSeed,
        AuditRecordType.ExtraPoolAdjustment => ExtraPoolAdjustment,
        AuditRecordType.SubscriptionStatusChange => SubscriptionStatusChange,
        AuditRecordType.ManualCorrection => ManualCorrection,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static AuditRecordType Parse(string wireName) => wireName switch
    {
        UsageDecision => AuditRecordType.UsageDecision,
        ExtraPoolSeed => AuditRecordType.ExtraPoolSeed,
        ExtraPoolAdjustment => AuditRecordType.ExtraPoolAdjustment,
        SubscriptionStatusChange => AuditRecordType.SubscriptionStatusChange,
        ManualCorrection => AuditRecordType.ManualCorrection,
        _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, null),
    };
}
