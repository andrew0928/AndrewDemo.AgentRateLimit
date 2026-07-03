using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for spec section 5 (multi user and subscription isolation):
/// TC-ISOLATION-001 through TC-ISOLATION-005.
/// </summary>
public class IsolationTests
{
    [Fact]
    public async Task TC_ISOLATION_001_UserAUsageDoesNotAffectUserB()
    {
        // TC-ISOLATION-001: user A 用量不影響 user B
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a", limit5h: 100);
        await fixture.CreateSubscriptionAsync(userId: "user-b", subscriptionId: "sub-b", limit5h: 100);

        var decision = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-a", credits: 80));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        var statusA = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusA);
        Assert.Equal(20, statusA.Window5h.RemainingCredits);

        var statusB = await fixture.Usage.GetUsageStatusAsync("sub-b");
        Assert.NotNull(statusB);
        Assert.Equal(100, statusB.Window5h.RemainingCredits);
        Assert.Equal(0, statusB.Window5h.UsedCredits);
    }

    [Fact]
    public async Task TC_ISOLATION_002_SubscriptionsOfSameUserAreIsolated()
    {
        // TC-ISOLATION-002: 同一 user 的不同 subscription 彼此隔離
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a1", limit5h: 500, limit7d: 500);
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a2", limit5h: 500, limit7d: 500);

        var decision = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-a1", credits: 100));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        var statusA1 = await fixture.Usage.GetUsageStatusAsync("sub-a1");
        Assert.NotNull(statusA1);
        Assert.Equal(400, statusA1.Window7d.RemainingCredits);

        var statusA2 = await fixture.Usage.GetUsageStatusAsync("sub-a2");
        Assert.NotNull(statusA2);
        Assert.Equal(500, statusA2.Window7d.RemainingCredits);
        Assert.Equal(0, statusA2.Window7d.UsedCredits);
    }

    [Fact]
    public async Task TC_ISOLATION_003_UserSubscriptionMismatchRejected()
    {
        // TC-ISOLATION-003: user 與 subscription 不匹配時 rejected
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-b", subscriptionId: "sub-b", limit5h: 100, limit7d: 1000);

        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-b");
        Assert.NotNull(statusBefore);

        var decision = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-b", credits: 10));

        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(UsageDecisionReasons.UserSubscriptionMismatch, decision.Reason);

        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-b");
        Assert.NotNull(statusAfter);
        Assert.Equal(statusBefore.Window5h.UsedCredits, statusAfter.Window5h.UsedCredits);
        Assert.Equal(statusBefore.Window5h.RemainingCredits, statusAfter.Window5h.RemainingCredits);
        Assert.Equal(statusBefore.Window7d.UsedCredits, statusAfter.Window7d.UsedCredits);
        Assert.Equal(statusBefore.Window7d.RemainingCredits, statusAfter.Window7d.RemainingCredits);
        Assert.Equal(statusBefore.ExtraPoolRemainingCredits, statusAfter.ExtraPoolRemainingCredits);
        Assert.Equal(statusBefore.Enabled, statusAfter.Enabled);
    }

    [Fact]
    public async Task TC_ISOLATION_004_SubscriptionNotFoundRejected()
    {
        // TC-ISOLATION-004: subscription 不存在時 rejected
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a", limit5h: 100, limit7d: 1000);

        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusBefore);

        var decision = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-missing", credits: 10));

        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(UsageDecisionReasons.SubscriptionNotFound, decision.Reason);

        // The other subscription's usage status is unchanged.
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(statusBefore.Window5h.UsedCredits, statusAfter.Window5h.UsedCredits);
        Assert.Equal(statusBefore.Window5h.RemainingCredits, statusAfter.Window5h.RemainingCredits);
        Assert.Equal(statusBefore.Window7d.UsedCredits, statusAfter.Window7d.UsedCredits);
        Assert.Equal(statusBefore.Window7d.RemainingCredits, statusAfter.Window7d.RemainingCredits);
        Assert.Equal(statusBefore.ExtraPoolRemainingCredits, statusAfter.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_ISOLATION_005_DisabledSubscriptionRejected()
    {
        // TC-ISOLATION-005: subscription 停用時 rejected
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(
            userId: "user-a", subscriptionId: "sub-disabled", limit5h: 100, limit7d: 1000, enabled: false);

        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-disabled");
        Assert.NotNull(statusBefore);
        Assert.False(statusBefore.Enabled);

        var decision = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-disabled", credits: 10));

        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(UsageDecisionReasons.SubscriptionDisabled, decision.Reason);

        // The rejected request must not increase used credits.
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-disabled");
        Assert.NotNull(statusAfter);
        Assert.Equal(statusBefore.Window5h.UsedCredits, statusAfter.Window5h.UsedCredits);
        Assert.Equal(statusBefore.Window7d.UsedCredits, statusAfter.Window7d.UsedCredits);
        Assert.Equal(0, statusAfter.Window5h.UsedCredits);
        Assert.Equal(0, statusAfter.Window7d.UsedCredits);
    }
}
