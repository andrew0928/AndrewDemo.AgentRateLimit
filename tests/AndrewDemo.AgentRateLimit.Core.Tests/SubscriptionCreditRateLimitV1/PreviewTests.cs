using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the Preview section of the V1 test cases:
/// preview evaluates the same decision pipeline as consume without changing any
/// usage totals, window usage or extra pool balance, and preview decisions are
/// never accounting records (no audit reference).
/// </summary>
public class PreviewTests
{
    [Fact]
    public async Task TC_PREVIEW_001_PreviewAcceptedDoesNotChangeUsageStatus()
    {
        // TC-PREVIEW-001: preview accepted 不改變 usage status
        using var fixture = new SubscriptionCreditServiceFixture();

        // Given: sub-a 5h remaining 100, 7d remaining 1000 (fresh subscription, no usage yet).
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000);

        // When: preview usage request with requested credits 20.
        var decision = await fixture.Usage.PreviewAsync(fixture.Request(credits: 20));

        // Then: decision result is accepted.
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // And: preview reports 5h remaining after decision = 80.
        Assert.Equal(80, decision.Remaining5hCreditsAfterDecision);

        // And: preview reports 7d remaining after decision = 980.
        Assert.Equal(980, decision.Remaining7dCreditsAfterDecision);

        // Previews are not accounting records: no audit reference.
        Assert.Null(decision.AuditReference);

        // When: query usage status again.
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);

        // Then: actual 5h remaining is still 100.
        Assert.Equal(100, status.Window5h.RemainingCredits);
        Assert.Equal(0, status.Window5h.UsedCredits);

        // And: actual 7d remaining is still 1000.
        Assert.Equal(1000, status.Window7d.RemainingCredits);
        Assert.Equal(0, status.Window7d.UsedCredits);
    }

    [Fact]
    public async Task TC_PREVIEW_002_PreviewRejectedLeavesNoAccountingRecord()
    {
        // TC-PREVIEW-002: preview rejected 不產生帳務扣款
        using var fixture = new SubscriptionCreditServiceFixture();

        // Given: sub-a 5h remaining 0, 7d remaining 0, extra pool remaining 0.
        await fixture.CreateSubscriptionAsync(limit5h: 0, limit7d: 0, extraPool: 0);

        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusBefore);

        // When: preview usage request with requested credits 1.
        var decision = await fixture.Usage.PreviewAsync(fixture.Request(credits: 1));

        // Then: decision result is rejected.
        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);

        // And: rejection reason is insufficient-credits.
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, decision.Reason);

        // Previews are not accounting records: no audit reference.
        Assert.Null(decision.AuditReference);

        // And: usage status is unchanged.
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(statusBefore.Window5h, statusAfter.Window5h);
        Assert.Equal(statusBefore.Window7d, statusAfter.Window7d);
        Assert.Equal(statusBefore.ExtraPoolRemainingCredits, statusAfter.ExtraPoolRemainingCredits);
        Assert.Equal(statusBefore.Enabled, statusAfter.Enabled);

        // And (accounting): the preview must not appear as a rejected request in the
        // reconciliation report. The provisioned subscription always has a row; it
        // must carry no rejected usage.
        var report = await fixture.Usage.ExportReconciliationReportAsync(
            SubscriptionCreditServiceFixture.DefaultStart,
            SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromDays(1));
        var row = Assert.Single(report.Subscriptions, r => r.SubscriptionId == "sub-a");
        Assert.Equal(0, row.RejectedRequestCount);
        Assert.Equal(0, row.RejectedCredits);
    }
}
