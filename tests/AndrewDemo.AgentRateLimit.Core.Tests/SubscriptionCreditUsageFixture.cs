using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;
using AndrewDemo.AgentRateLimit.Core.DependencyInjection;
using AndrewDemo.AgentRateLimit.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AndrewDemo.AgentRateLimit.Core.Tests;

internal sealed class SubscriptionCreditUsageFixture : IAsyncDisposable
{
    private SubscriptionCreditUsageFixture(
        string databasePath,
        ServiceProvider serviceProvider,
        ManualTimeProvider clock,
        SubscriptionCreditSqliteStore store,
        ISubscriptionCreditUsageService usage)
    {
        DatabasePath = databasePath;
        ServiceProvider = serviceProvider;
        Clock = clock;
        Store = store;
        Usage = usage;
    }

    public string DatabasePath { get; }

    public ServiceProvider ServiceProvider { get; }

    public ManualTimeProvider Clock { get; }

    public SubscriptionCreditSqliteStore Store { get; }

    public ISubscriptionCreditUsageService Usage { get; }

    public static async ValueTask<SubscriptionCreditUsageFixture> CreateAsync(DateTimeOffset utcNow)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "andrewdemo-agent-rate-limit-" + Guid.NewGuid().ToString("N") + ".db");
        var clock = new ManualTimeProvider(utcNow);

        var services = new ServiceCollection();
        services.AddSubscriptionCreditUsage(builder => builder
            .UseSqlite("Data Source=" + databasePath)
            .UseTimeProvider(clock));

        var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<SubscriptionCreditSqliteStore>();
        await store.InitializeAsync();

        return new SubscriptionCreditUsageFixture(
            databasePath,
            serviceProvider,
            clock,
            store,
            serviceProvider.GetRequiredService<ISubscriptionCreditUsageService>());
    }

    public async ValueTask SeedActiveAccountAsync(
        int fiveHourRemaining,
        int sevenDayRemaining,
        int extraPoolRemaining,
        DateTimeOffset? fiveHourOpenedUtc = null,
        DateTimeOffset? fiveHourExpiresUtc = null,
        DateTimeOffset? sevenDayOpenedUtc = null,
        DateTimeOffset? sevenDayExpiresUtc = null,
        string subscriptionId = "sub-a",
        string userId = "user-a")
    {
        var now = Clock.GetUtcNow();
        fiveHourOpenedUtc ??= now.AddHours(-1);
        fiveHourExpiresUtc ??= now.AddHours(4);
        sevenDayOpenedUtc ??= now.AddDays(-1);
        sevenDayExpiresUtc ??= now.AddDays(6);

        await Store.SeedAccountAsync(new SubscriptionCreditAccountSeed(
            SubscriptionId: subscriptionId,
            UserId: userId,
            Status: "active",
            FiveHourLimit: 100,
            SevenDayLimit: 1000,
            FiveHourOpenedUtc: fiveHourOpenedUtc,
            FiveHourExpiresUtc: fiveHourExpiresUtc,
            FiveHourUsedCredits: 100 - fiveHourRemaining,
            SevenDayOpenedUtc: sevenDayOpenedUtc,
            SevenDayExpiresUtc: sevenDayExpiresUtc,
            SevenDayUsedCredits: 1000 - sevenDayRemaining,
            ExtraPoolRemainingCredits: extraPoolRemaining));
    }

    public UsageCreditRequest CreateAdmissionRequest(
        string idempotencyKey,
        UsageExtraPoolAuthorization authorization = UsageExtraPoolAuthorization.NotAuthorized)
    {
        return CreateRequest(
            idempotencyKey,
            credits: 1,
            mode: UsageCreditAmountMode.MinimumAvailableBalance,
            authorization: authorization,
            correlationId: "corr-" + idempotencyKey + "-admission");
    }

    public UsageCreditRequest CreateSettlementRequest(
        UsageCreditRequest admissionRequest,
        int actualCredits)
    {
        return admissionRequest with
        {
            RequestedCredits = RequestedCreditsInput.FromInt32(actualCredits),
            CreditAmountMode = UsageCreditAmountMode.ExactCredits,
            CorrelationId = new CorrelationId(admissionRequest.CorrelationId.Value + "-settlement")
        };
    }

    private static UsageCreditRequest CreateRequest(
        string idempotencyKey,
        int credits,
        UsageCreditAmountMode mode,
        UsageExtraPoolAuthorization authorization,
        string correlationId)
    {
        return new UsageCreditRequest(
            UserId: new UserId("user-a"),
            SubscriptionId: new SubscriptionId("sub-a"),
            RequestedCredits: RequestedCreditsInput.FromInt32(credits),
            CreditAmountMode: mode,
            ExtraPoolAuthorization: authorization,
            IdempotencyKey: new IdempotencyKey(idempotencyKey),
            CorrelationId: new CorrelationId(correlationId),
            Source: "core-e2e-test");
    }

    public async ValueTask DisposeAsync()
    {
        await ServiceProvider.DisposeAsync();

        try
        {
            if (File.Exists(DatabasePath))
            {
                File.Delete(DatabasePath);
            }
        }
        catch
        {
            // Temp database cleanup should not hide the test assertion result.
        }
    }
}
