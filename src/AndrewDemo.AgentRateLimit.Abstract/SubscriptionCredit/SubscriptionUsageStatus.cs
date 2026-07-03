namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Current usage status of one subscription, per spec section 3.3.
/// </summary>
public sealed record SubscriptionUsageStatus
{
    public required string SubscriptionId { get; init; }

    public required string UserId { get; init; }

    public required bool Enabled { get; init; }

    public required UsageWindowStatus Window5h { get; init; }

    public required UsageWindowStatus Window7d { get; init; }

    public required long ExtraPoolRemainingCredits { get; init; }

    /// <summary>Time at which the status was evaluated.</summary>
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
/// State of one rolling window at observation time.
/// <see cref="UsedCredits"/> can exceed <see cref="LimitCredits"/> because accepted
/// usage covered by the extra pool still counts toward window usage (spec 4.2);
/// <see cref="RemainingCredits"/> is floored at zero.
/// </summary>
public sealed record UsageWindowStatus
{
    public required long LimitCredits { get; init; }

    public required long UsedCredits { get; init; }

    public required long RemainingCredits { get; init; }

    /// <summary>
    /// Time at which the oldest in-window accepted usage leaves the window, i.e. the
    /// next moment used credits decreases. <c>null</c> when the window has no usage.
    /// </summary>
    public DateTimeOffset? NextResetTime { get; init; }
}
