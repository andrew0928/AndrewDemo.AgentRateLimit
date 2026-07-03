namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Externally observable outcome of a usage request, per spec section 7.1.
/// </summary>
public enum UsageDecisionResult
{
    Accepted,
    Rejected,
    Conflict,
    Invalid,
}

/// <summary>
/// Wire names for <see cref="UsageDecisionResult"/> used in persistence, audit trail and reports.
/// </summary>
public static class UsageDecisionResultNames
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid";

    public static string ToWireName(UsageDecisionResult result) => result switch
    {
        UsageDecisionResult.Accepted => Accepted,
        UsageDecisionResult.Rejected => Rejected,
        UsageDecisionResult.Conflict => Conflict,
        UsageDecisionResult.Invalid => Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    public static UsageDecisionResult Parse(string wireName) => wireName switch
    {
        Accepted => UsageDecisionResult.Accepted,
        Rejected => UsageDecisionResult.Rejected,
        Conflict => UsageDecisionResult.Conflict,
        Invalid => UsageDecisionResult.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(wireName), wireName, null),
    };
}
