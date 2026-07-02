using AndrewDemo.AgentRateLimit.Core;
using AndrewDemo.AgentRateLimit.Core.Storage;

var connectionString =
    Environment.GetEnvironmentVariable("SUBSCRIPTION_CREDIT_SQLITE") ??
    (args.Length > 0 ? args[0] : null) ??
    "Data Source=subscription-credit.db";

var store = new SubscriptionCreditSqliteStore(new SubscriptionCreditUsageOptions(connectionString));
await store.InitializeAsync();

foreach (var seed in LocalSubscriptionCreditSeeds.All)
{
    await store.SeedAccountAsync(seed.ToAccountSeed());
    await store.SeedAccessTokenAsync(seed.AccessToken, seed.SubscriptionId);
}

Console.WriteLine($"Initialized subscription credit database with {LocalSubscriptionCreditSeeds.All.Length} subscriptions.");

internal sealed record LocalSubscriptionSeed(
    string Purpose,
    string AccessToken,
    string SubscriptionId,
    string UserId,
    string Status,
    int FiveHourRemaining,
    int SevenDayRemaining,
    int ExtraPoolRemaining,
    DateTimeOffset FiveHourOpenedUtc,
    DateTimeOffset FiveHourExpiresUtc,
    DateTimeOffset SevenDayOpenedUtc,
    DateTimeOffset SevenDayExpiresUtc)
{
    public SubscriptionCreditAccountSeed ToAccountSeed()
    {
        return new SubscriptionCreditAccountSeed(
            SubscriptionId: SubscriptionId,
            UserId: UserId,
            Status: Status,
            FiveHourLimit: 100,
            SevenDayLimit: 1000,
            FiveHourOpenedUtc: FiveHourOpenedUtc,
            FiveHourExpiresUtc: FiveHourExpiresUtc,
            FiveHourUsedCredits: 100 - FiveHourRemaining,
            SevenDayOpenedUtc: SevenDayOpenedUtc,
            SevenDayExpiresUtc: SevenDayExpiresUtc,
            SevenDayUsedCredits: 1000 - SevenDayRemaining,
            ExtraPoolRemainingCredits: ExtraPoolRemaining);
    }
}

internal static class LocalSubscriptionCreditSeeds
{
    private static readonly DateTimeOffset BaseOpenedUtc =
        DateTimeOffset.Parse("2026-07-01T23:01:23Z");

    private static readonly DateTimeOffset ActiveFiveHourExpiresUtc =
        DateTimeOffset.Parse("2026-07-02T04:01:23Z");

    private static readonly DateTimeOffset ActiveSevenDayExpiresUtc =
        DateTimeOffset.Parse("2026-07-08T23:01:23Z");

    public static readonly LocalSubscriptionSeed[] All =
    [
        Create(
            Purpose: "primary manual sample: 5h 100, 7d 1000, extra 1000",
            AccessToken: "0123456789ABCDEFFEDCBA9876543210",
            SubscriptionId: "sub-a",
            UserId: "user-a",
            Status: "active",
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 1000),
        Create(
            Purpose: "API-AUTH-005 token scope mapping",
            AccessToken: "11111111111111111111111111111111",
            SubscriptionId: "sub-api-auth-scope",
            UserId: "user-api-auth-scope",
            Status: "active",
            FiveHourRemaining: 7,
            SevenDayRemaining: 900,
            ExtraPoolRemaining: 0),
        Create(
            Purpose: "insufficient 5h without extra pool",
            AccessToken: "22222222222222222222222222222222",
            SubscriptionId: "sub-no-extra-5h-empty",
            UserId: "user-no-extra-5h-empty",
            Status: "active",
            FiveHourRemaining: 0,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0),
        Create(
            Purpose: "extra pool authorization required",
            AccessToken: "33333333333333333333333333333333",
            SubscriptionId: "sub-extra-authorization-required",
            UserId: "user-extra-authorization-required",
            Status: "active",
            FiveHourRemaining: 0,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 1000),
        Create(
            Purpose: "authorized extra pool covers 5h shortage",
            AccessToken: "44444444444444444444444444444444",
            SubscriptionId: "sub-extra-authorized-5h",
            UserId: "user-extra-authorized-5h",
            Status: "active",
            FiveHourRemaining: 0,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 1000),
        Create(
            Purpose: "authorized extra pool covers 7d shortage",
            AccessToken: "55555555555555555555555555555555",
            SubscriptionId: "sub-extra-authorized-7d",
            UserId: "user-extra-authorized-7d",
            Status: "active",
            FiveHourRemaining: 100,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 1000),
        Create(
            Purpose: "unknown actual exceeds 5h without authorization",
            AccessToken: "66666666666666666666666666666666",
            SubscriptionId: "sub-overrun-5h",
            UserId: "user-overrun-5h",
            Status: "active",
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 1000),
        Create(
            Purpose: "unknown actual exceeds 7d without authorization",
            AccessToken: "77777777777777777777777777777777",
            SubscriptionId: "sub-overrun-7d",
            UserId: "user-overrun-7d",
            Status: "active",
            FiveHourRemaining: 100,
            SevenDayRemaining: 10,
            ExtraPoolRemaining: 1000),
        Create(
            Purpose: "expired 5h lazy renew sample",
            AccessToken: "88888888888888888888888888888888",
            SubscriptionId: "sub-expired-5h",
            UserId: "user-expired-5h",
            Status: "active",
            FiveHourRemaining: 0,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0,
            FiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-02T04:01:23Z")),
        Create(
            Purpose: "expired 7d lazy renew sample",
            AccessToken: "99999999999999999999999999999999",
            SubscriptionId: "sub-expired-7d",
            UserId: "user-expired-7d",
            Status: "active",
            FiveHourRemaining: 100,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 0,
            SevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z")),
        Create(
            Purpose: "disabled subscription rejection sample",
            AccessToken: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            SubscriptionId: "sub-disabled",
            UserId: "user-disabled",
            Status: "disabled",
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0)
    ];

    private static LocalSubscriptionSeed Create(
        string Purpose,
        string AccessToken,
        string SubscriptionId,
        string UserId,
        string Status,
        int FiveHourRemaining,
        int SevenDayRemaining,
        int ExtraPoolRemaining,
        DateTimeOffset? FiveHourExpiresUtc = null,
        DateTimeOffset? SevenDayExpiresUtc = null)
    {
        return new LocalSubscriptionSeed(
            Purpose,
            AccessToken,
            SubscriptionId,
            UserId,
            Status,
            FiveHourRemaining,
            SevenDayRemaining,
            ExtraPoolRemaining,
            BaseOpenedUtc,
            FiveHourExpiresUtc ?? ActiveFiveHourExpiresUtc,
            BaseOpenedUtc,
            SevenDayExpiresUtc ?? ActiveSevenDayExpiresUtc);
    }
}
