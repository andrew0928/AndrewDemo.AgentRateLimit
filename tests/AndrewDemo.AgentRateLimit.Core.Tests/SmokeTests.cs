using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests;

/// <summary>
/// End-to-end smoke of the main flows; the per-TC acceptance tests live in the
/// SubscriptionCreditRateLimitV1 test classes.
/// </summary>
public class SmokeTests
{
    [Fact]
    public async Task ConsumeStatusAuditAndReconciliationWorkTogether()
    {
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 50);

        var accepted = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30, idempotencyKey: "k-smoke"));
        Assert.Equal(UsageDecisionResult.Accepted, accepted.Result);
        Assert.Equal(30, accepted.CoveredBySubscriptionAllowance);
        Assert.Equal(0, accepted.CoveredByExtraPool);
        Assert.Equal(70, accepted.Remaining5hCreditsAfterDecision);
        Assert.Equal(970, accepted.Remaining7dCreditsAfterDecision);
        Assert.Equal(50, accepted.RemainingExtraPoolCreditsAfterDecision);
        Assert.NotNull(accepted.AuditReference);

        var replay = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30, idempotencyKey: "k-smoke"));
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(accepted.AuditReference, replay.AuditReference);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(30, status.Window5h.UsedCredits);
        Assert.Equal(70, status.Window5h.RemainingCredits);
        Assert.Equal(30, status.Window7d.UsedCredits);
        Assert.Equal(50, status.ExtraPoolRemainingCredits);

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Accepted);
        Assert.Contains(audit, r => r.RecordType == AuditRecordType.ExtraPoolSeed);

        var report = await fixture.Usage.ExportReconciliationReportAsync(
            SubscriptionCreditServiceFixture.DefaultStart,
            SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromDays(1));
        var row = Assert.Single(report.Subscriptions);
        Assert.Equal("sub-a", row.SubscriptionId);
        Assert.Equal(30, row.AcceptedCredits);
        Assert.Equal(1, row.AcceptedRequestCount);
        Assert.Equal(50, row.ExtraPoolEndingBalance);
    }
}
