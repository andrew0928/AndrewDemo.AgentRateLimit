using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the Extra Pool section (TC-EXTRA-001 .. TC-EXTRA-003).
/// </summary>
public class ExtraPoolTests
{
    [Fact]
    public async Task TC_EXTRA_001_ExtraPoolDoesNotRefillOnRollingWindowReset()
    {
        // TC-EXTRA-001: extra pool 不因 rolling window reset 自動恢復
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 5, limit7d: 500, extraPool: 50);

        // Given: extra pool remaining is originally 50, and an accepted usage while the
        // 5h allowance is short consumes 15 extra credits (5 allowance + 15 extra = 20).
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Equal(5, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(15, decision.CoveredByExtraPool);
        Assert.Equal(35, decision.RemainingExtraPoolCreditsAfterDecision);

        // When: the 5h window has expired and usage status is queried.
        fixture.Clock.Advance(TimeSpan.FromHours(5));
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);

        // Sanity: the 5h window really did reset (the accepted usage left the window).
        Assert.Equal(0, status.Window5h.UsedCredits);
        Assert.Equal(5, status.Window5h.RemainingCredits);

        // Then: extra pool remaining is still 35 — the pool does not refill on reset.
        Assert.Equal(35, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_EXTRA_002_ExactlySufficientExtraPoolAcceptedAndDrainedToZero()
    {
        // TC-EXTRA-002: extra pool 剛好足夠時 accepted 並歸零
        using var fixture = new SubscriptionCreditServiceFixture();

        // Given: 5h remaining 0, 7d remaining 0, extra pool remaining 20.
        await fixture.CreateSubscriptionAsync(limit5h: 0, limit7d: 0, extraPool: 20);

        // When: user-a consumes 20 credits.
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));

        // Then: decision result is accepted.
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // And: credits covered by extra pool is 20 (nothing from the window allowance).
        Assert.Equal(0, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(20, decision.CoveredByExtraPool);

        // And: extra pool remaining becomes 0.
        Assert.Equal(0, decision.RemainingExtraPoolCreditsAfterDecision);
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(0, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_EXTRA_003_ExtraPoolAdjustmentAppearsInAuditTrail()
    {
        // TC-EXTRA-003: extra pool 調整必須出現在 audit trail
        using var fixture = new SubscriptionCreditServiceFixture();

        // Given: sub-a's extra pool remaining is 0.
        await fixture.CreateSubscriptionAsync(extraPool: 0);

        // When: an authorized actor adds 100 extra credits with reason "manual-top-up".
        await fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
        {
            SubscriptionId = "sub-a",
            DeltaCredits = 100,
            Actor = "ops-admin",
            Reason = "manual-top-up",
        });

        // Then: usage status shows extra pool remaining 100.
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(100, status.ExtraPoolRemainingCredits);

        // And: the audit trail contains one extra pool change record for the adjustment.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        var adjustment = Assert.Single(audit, r => r.RecordType == AuditRecordType.ExtraPoolAdjustment);

        // And: the audit record contains actor, reason, changed credits and the
        // resulting extra pool remaining.
        Assert.Equal("ops-admin", adjustment.Actor);
        Assert.Equal("manual-top-up", adjustment.Reason);
        Assert.Equal(100, adjustment.ExtraPoolDelta);
        Assert.Equal(100, adjustment.ExtraPoolBalanceAfter);
    }
}
