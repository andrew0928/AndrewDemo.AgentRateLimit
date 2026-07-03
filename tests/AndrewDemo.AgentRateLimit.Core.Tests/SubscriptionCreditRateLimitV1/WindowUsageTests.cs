using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the Window Usage cases (TC-WINDOW-001 .. TC-WINDOW-007) of
/// spec/testcases/subscription-credit-rate-limit-v1.md.
/// </summary>
public class WindowUsageTests
{
    [Fact]
    public async Task TC_WINDOW_001_BothWindowsSufficientAcceptedWithoutExtraPool()
    {
        // TC-WINDOW-001: 5h 與 7d 額度都足夠時 accepted 且不消耗 extra pool
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 500, extraPool: 50);

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));

        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Equal(20, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(0, decision.CoveredByExtraPool);
        Assert.Equal(80, decision.Remaining5hCreditsAfterDecision);
        Assert.Equal(480, decision.Remaining7dCreditsAfterDecision);
        Assert.Equal(50, decision.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(80, status.Window5h.RemainingCredits);
        Assert.Equal(480, status.Window7d.RemainingCredits);
        Assert.Equal(50, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_WINDOW_002_Insufficient5hToppedUpFromExtraPool()
    {
        // TC-WINDOW-002: 5h 額度不足時使用 extra pool 補足
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 5, limit7d: 500, extraPool: 50);

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));

        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Equal(5, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(15, decision.CoveredByExtraPool);
        Assert.Equal(35, decision.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(35, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_WINDOW_003_Insufficient7dToppedUpFromExtraPool()
    {
        // TC-WINDOW-003: 7d 額度不足時使用 extra pool 補足
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 8, extraPool: 50);

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));

        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Equal(8, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(12, decision.CoveredByExtraPool);
        Assert.Equal(38, decision.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(38, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_WINDOW_004_BothWindowsInsufficientAllowanceIsSmallerRemaining()
    {
        // TC-WINDOW-004: 5h 與 7d 都不足時以較小 remaining 為 subscription allowance
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 12, limit7d: 7, extraPool: 50);

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));

        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Equal(7, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(13, decision.CoveredByExtraPool);
        Assert.Equal(37, decision.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(37, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_WINDOW_005_AllowancePlusExtraPoolStillInsufficientRejected()
    {
        // TC-WINDOW-005: subscription allowance 加 extra pool 仍不足時 rejected
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 5, limit7d: 8, extraPool: 10);

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));

        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, decision.Reason);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(5, status.Window5h.RemainingCredits);
        Assert.Equal(8, status.Window7d.RemainingCredits);
        Assert.Equal(10, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_WINDOW_006_FiveHourRollingWindowExpiryReleases5hRemaining()
    {
        // TC-WINDOW-006: 5h rolling window 到期後釋放 5h remaining
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000);

        // Accepted usage of 30 credits at exactly 2026-07-01T00:00:00Z (fixture default start).
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // At 2026-07-01T04:59:59Z the usage is still inside the 5h window.
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 1, 4, 59, 59, TimeSpan.Zero));
        var statusInside = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusInside);
        Assert.Equal(30, statusInside.Window5h.UsedCredits);

        // At 2026-07-01T05:00:00Z the usage is exactly 5h old and is excluded.
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero));
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(0, statusAfter.Window5h.UsedCredits);
    }

    [Fact]
    public async Task TC_WINDOW_007_SevenDayRollingWindowExpiryReleases7dRemaining()
    {
        // TC-WINDOW-007: 7d rolling window 到期後釋放 7d remaining
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000);

        // Accepted usage of 30 credits at exactly 2026-07-01T00:00:00Z (fixture default start).
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // At 2026-07-07T23:59:59Z the usage is still inside the 7d window.
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 7, 23, 59, 59, TimeSpan.Zero));
        var statusInside = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusInside);
        Assert.Equal(30, statusInside.Window7d.UsedCredits);

        // At 2026-07-08T00:00:00Z the usage is exactly 7d old and is excluded.
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero));
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(0, statusAfter.Window7d.UsedCredits);
    }
}
