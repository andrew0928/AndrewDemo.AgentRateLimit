namespace AndrewDemo.AgentRateLimit.Core.Storage;

public sealed record SubscriptionCreditAccountSeed(
    string SubscriptionId,
    string UserId,
    string Status,
    int FiveHourLimit,
    int SevenDayLimit,
    DateTimeOffset? FiveHourOpenedUtc,
    DateTimeOffset? FiveHourExpiresUtc,
    int FiveHourUsedCredits,
    DateTimeOffset? SevenDayOpenedUtc,
    DateTimeOffset? SevenDayExpiresUtc,
    int SevenDayUsedCredits,
    int ExtraPoolRemainingCredits);

public sealed record SubscriptionCreditAccountSnapshot(
    string SubscriptionId,
    string UserId,
    string Status,
    int FiveHourLimit,
    int SevenDayLimit,
    DateTimeOffset? FiveHourOpenedUtc,
    DateTimeOffset? FiveHourExpiresUtc,
    int FiveHourUsedCredits,
    int FiveHourRemainingCredits,
    DateTimeOffset? SevenDayOpenedUtc,
    DateTimeOffset? SevenDayExpiresUtc,
    int SevenDayUsedCredits,
    int SevenDayRemainingCredits,
    int ExtraPoolRemainingCredits);
