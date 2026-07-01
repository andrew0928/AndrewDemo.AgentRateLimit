using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;
using AndrewDemo.AgentRateLimit.Core.DependencyInjection;
using AndrewDemo.AgentRateLimit.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

await UsageDecisionDeveloperExperienceTests
    .TcSettle002_BalanceProbeThenActualOverage_RecordsSystemAbsorption();

Console.WriteLine("Abstract developer-experience test sketch completed.");

internal static class UsageDecisionDeveloperExperienceTests
{
    public static async Task TcSettle002_BalanceProbeThenActualOverage_RecordsSystemAbsorption()
    {
        // TC-SETTLE-002 from spec/testcases/subscription-credit-rate-limit-v1.md:
        // Given sub-a has 5h remaining 100 and 7d remaining 500.
        // When the caller does not know final credits before execution,
        // Then DecideAsync should use a minimum balance probe, not requested credits = 0.
        //
        // Important DX decision:
        // - requested credits = 0 remains invalid because zero means "no charge", not "unknown".
        // - requested credits = 1 is acceptable only when CreditAmountMode says it is a minimum
        //   available-balance threshold, not the final expected cost.
        // - final actual credits are sent later through ConsumeAsync as ExactCredits.

        await using var fixture = await UsageServiceFixture.CreateAsync(
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        await fixture.Store.SeedAccountAsync(new SubscriptionCreditAccountSeed(
            SubscriptionId: "sub-a",
            UserId: "user-a",
            Status: "active",
            FiveHourLimit: 100,
            SevenDayLimit: 500,
            FiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            FiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-01T05:00:00Z"),
            FiveHourUsedCredits: 0,
            SevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            SevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z"),
            SevenDayUsedCredits: 0,
            ExtraPoolRemainingCredits: 0));

        ISubscriptionCreditUsageService usage = fixture.Usage;

        var balanceProbe = new UsageCreditRequest(
            UserId: new UserId("user-a"),
            SubscriptionId: new SubscriptionId("sub-a"),
            RequestedCredits: RequestedCreditsInput.FromInt32(1),
            CreditAmountMode: UsageCreditAmountMode.MinimumAvailableBalance,
            ExtraPoolAuthorization: UsageExtraPoolAuthorization.NotAuthorized,
            IdempotencyKey: new IdempotencyKey("tc-settle-002"),
            CorrelationId: new CorrelationId("corr-tc-settle-002-probe"),
            Source: "abstract-dx-test");

        var admitted = await usage.DecideAsync(balanceProbe, CancellationToken.None);

        Assert.Equal(UsageDecisionMode.DecideOnly, admitted.Mode);
        Assert.Equal(UsageCreditAmountMode.MinimumAvailableBalance, admitted.CreditAmountMode);
        Assert.Equal(UsageDecisionResult.Accepted, admitted.Result);
        Assert.Equal(new CreditAmount(1), admitted.RequestedCredits);
        Assert.Equal(CreditAmount.Zero, admitted.CreditsCoveredBySubscriptionAllowance);
        Assert.Equal(CreditAmount.Zero, admitted.CreditsCoveredByExtraPool);
        Assert.Equal(CreditAmount.Zero, admitted.CreditsAbsorbedBySystem);
        Assert.Equal(new CreditAmount(100), admitted.FiveHourWindowAfterDecision.Remaining);
        Assert.Equal(new CreditAmount(500), admitted.SevenDayWindowAfterDecision.Remaining);
        Assert.Null(admitted.AuditReference);

        var actualUsage = balanceProbe with
        {
            RequestedCredits = RequestedCreditsInput.FromInt32(120),
            CreditAmountMode = UsageCreditAmountMode.ExactCredits,
            CorrelationId = new CorrelationId("corr-tc-settle-002-consume")
        };

        var consumed = await usage.ConsumeAsync(actualUsage, CancellationToken.None);

        Assert.Equal(UsageDecisionMode.Consume, consumed.Mode);
        Assert.Equal(UsageCreditAmountMode.ExactCredits, consumed.CreditAmountMode);
        Assert.Equal(UsageDecisionResult.Accepted, consumed.Result);
        Assert.Equal(new CreditAmount(120), consumed.RequestedCredits);
        Assert.Equal(new CreditAmount(100), consumed.CreditsCoveredBySubscriptionAllowance);
        Assert.Equal(CreditAmount.Zero, consumed.CreditsCoveredByExtraPool);
        Assert.Equal(new CreditAmount(20), consumed.CreditsAbsorbedBySystem);
        Assert.Equal(CreditAmount.Zero, consumed.FiveHourWindowAfterDecision.Remaining);
        Assert.Equal(new CreditAmount(380), consumed.SevenDayWindowAfterDecision.Remaining);
        Assert.Equal(CreditAmount.Zero, consumed.ExtraPoolRemainingAfterDecision);
        Assert.NotNull(consumed.AuditReference);
    }
}

internal sealed class UsageServiceFixture : IAsyncDisposable
{
    private UsageServiceFixture(
        string databasePath,
        ServiceProvider serviceProvider,
        SubscriptionCreditSqliteStore store,
        ISubscriptionCreditUsageService usage)
    {
        DatabasePath = databasePath;
        ServiceProvider = serviceProvider;
        Store = store;
        Usage = usage;
    }

    public string DatabasePath { get; }

    public ServiceProvider ServiceProvider { get; }

    public SubscriptionCreditSqliteStore Store { get; }

    public ISubscriptionCreditUsageService Usage { get; }

    public static async ValueTask<UsageServiceFixture> CreateAsync(DateTimeOffset utcNow)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "andrewdemo-agent-rate-limit-abstract-" + Guid.NewGuid().ToString("N") + ".db");

        var services = new ServiceCollection();
        services.AddSubscriptionCreditUsage(builder => builder
            .UseSqlite("Data Source=" + databasePath)
            .UseTimeProvider(new FixedTimeProvider(utcNow)));

        var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<SubscriptionCreditSqliteStore>();
        await store.InitializeAsync();

        return new UsageServiceFixture(
            databasePath,
            serviceProvider,
            store,
            serviceProvider.GetRequiredService<ISubscriptionCreditUsageService>());
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
            // Cleanup failure should not mask the developer-experience assertion.
        }
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void Null(object? actual)
    {
        if (actual is not null)
        {
            throw new InvalidOperationException($"Expected null, got {actual}.");
        }
    }

    public static void NotNull(object? actual)
    {
        if (actual is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }
}
