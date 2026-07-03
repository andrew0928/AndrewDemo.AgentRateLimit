namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Definition used to provision a subscription for acceptance and administration.
/// </summary>
public sealed record SubscriptionDefinition
{
    public required string UserId { get; init; }

    public required string SubscriptionId { get; init; }

    public required long Limit5hCredits { get; init; }

    public required long Limit7dCredits { get; init; }

    public long InitialExtraPoolCredits { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Actor recorded on the provisioning audit records.</summary>
    public string Actor { get; init; } = "system";
}
