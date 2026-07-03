using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the "Status Output" test cases (TC-STATUS-001, TC-STATUS-002).
/// </summary>
public class StatusOutputTests
{
    [Fact]
    public async Task TC_STATUS_001_UsageStatusShowsWindowLimitsUsedRemainingAndExtraPool()
    {
        // TC-STATUS-001: usage status 顯示 5h 與 7d window 狀態
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 50);

        // Given: sub-a has accepted usage.
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // When: query usage status.
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);

        // Then: response contains 5h window limit, used and remaining.
        Assert.Equal(100, status.Window5h.LimitCredits);
        Assert.Equal(30, status.Window5h.UsedCredits);
        Assert.Equal(70, status.Window5h.RemainingCredits);

        // And: response contains 7d window limit, used and remaining.
        Assert.Equal(1000, status.Window7d.LimitCredits);
        Assert.Equal(30, status.Window7d.UsedCredits);
        Assert.Equal(970, status.Window7d.RemainingCredits);

        // And: response contains extra pool remaining.
        Assert.Equal(50, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_STATUS_002_UsageStatusShowsNextResetTimePerWindow()
    {
        // TC-STATUS-002: usage status 顯示 next reset time
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 0);

        // With zero usage, neither window can report a next reset time.
        var emptyStatus = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(emptyStatus);
        Assert.Equal(0, emptyStatus.Window5h.UsedCredits);
        Assert.Null(emptyStatus.Window5h.NextResetTime);
        Assert.Equal(0, emptyStatus.Window7d.UsedCredits);
        Assert.Null(emptyStatus.Window7d.NextResetTime);

        // Given: sub-a has accepted usage inside the rolling windows, at a known instant.
        var consumeTime = fixture.Clock.GetUtcNow();
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // When: query usage status later, while the usage is still inside both windows.
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);

        // Then: response contains the 5h next reset time, since 5h used credits > 0.
        Assert.True(status.Window5h.UsedCredits > 0);
        Assert.Equal<DateTimeOffset?>(
            consumeTime + SubscriptionCreditWindows.FiveHours,
            status.Window5h.NextResetTime);

        // And: response contains the 7d next reset time, since 7d used credits > 0.
        Assert.True(status.Window7d.UsedCredits > 0);
        Assert.Equal<DateTimeOffset?>(
            consumeTime + SubscriptionCreditWindows.SevenDays,
            status.Window7d.NextResetTime);

        // Once the 5h window has expired the usage, the 5h window is back to zero usage
        // and reports no next reset time, while the 7d window still does.
        fixture.Clock.SetUtcNow(consumeTime + SubscriptionCreditWindows.FiveHours);
        var afterExpiry = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(afterExpiry);
        Assert.Equal(0, afterExpiry.Window5h.UsedCredits);
        Assert.Null(afterExpiry.Window5h.NextResetTime);
        Assert.True(afterExpiry.Window7d.UsedCredits > 0);
        Assert.Equal<DateTimeOffset?>(
            consumeTime + SubscriptionCreditWindows.SevenDays,
            afterExpiry.Window7d.NextResetTime);
    }
}
