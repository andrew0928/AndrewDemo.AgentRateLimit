using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the "Consistency And Persistence" group
/// (TC-CONSISTENCY-001 .. TC-CONSISTENCY-003).
/// </summary>
public class ConsistencyPersistenceTests
{
    [Fact]
    public async Task TC_CONSISTENCY_001_ConcurrentRequestsCannotOveracceptBeyondRemaining()
    {
        // TC-CONSISTENCY-001 同時請求不可造成超額 accepted
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 10, limit7d: 10, extraPool: 0);

        var requestOne = fixture.Request(credits: 10, idempotencyKey: "k-conc-a");
        var requestTwo = fixture.Request(credits: 10, idempotencyKey: "k-conc-b");

        // When: two 10-credit consume requests with different idempotency keys are
        // sent concurrently. A start gate keeps both tasks queued until released, so
        // the thread pool cannot run them back-to-back sequentially.
        var gate = new TaskCompletionSource();
        var taskOne = Task.Run(async () => { await gate.Task; return await fixture.Usage.ConsumeAsync(requestOne); });
        var taskTwo = Task.Run(async () => { await gate.Task; return await fixture.Usage.ConsumeAsync(requestTwo); });
        gate.SetResult();
        var decisions = await Task.WhenAll(taskOne, taskTwo);

        // Then: at most one decision is accepted (here: exactly one, because the first
        // request in any serial order fits the remaining 10 credits).
        var acceptedDecisions = decisions.Where(d => d.Result == UsageDecisionResult.Accepted).ToList();
        var rejectedDecisions = decisions.Where(d => d.Result == UsageDecisionResult.Rejected).ToList();
        Assert.Single(acceptedDecisions);

        // And: the other decision is rejected.
        Assert.Single(rejectedDecisions);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, rejectedDecisions[0].Reason);

        // And: accepted credits total does not exceed 10, both on the decisions and in usage status.
        var acceptedTotal = acceptedDecisions.Sum(d => d.RequestedCredits!.Value);
        Assert.True(acceptedTotal <= 10, $"accepted credits total {acceptedTotal} exceeds 10");

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(acceptedTotal, status.Window5h.UsedCredits);
        Assert.Equal(acceptedTotal, status.Window7d.UsedCredits);
        Assert.True(status.Window5h.UsedCredits <= 10);
        Assert.True(status.Window7d.UsedCredits <= 10);
        Assert.Equal(0, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_CONSISTENCY_001_ManyConcurrentSingleCreditRequestsAcceptExactlyRemaining()
    {
        // TC-CONSISTENCY-001 同時請求不可造成超額 accepted (heavier variant:
        // 20 concurrent 1-credit requests against remaining 10 => exactly 10 accepted).
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 10, limit7d: 10, extraPool: 0);

        var requests = Enumerable.Range(0, 20)
            .Select(i => fixture.Request(credits: 1, idempotencyKey: $"k-conc-{i:d2}"))
            .ToList();

        // When: all 20 consume requests are sent concurrently, released together by a
        // start gate to maximize real overlap.
        var gate = new TaskCompletionSource();
        var tasks = requests
            .Select(request => Task.Run(async () => { await gate.Task; return await fixture.Usage.ConsumeAsync(request); }))
            .ToArray();
        gate.SetResult();
        var decisions = await Task.WhenAll(tasks);

        // Then: exactly 10 requests are accepted and the other 10 are rejected.
        var acceptedDecisions = decisions.Where(d => d.Result == UsageDecisionResult.Accepted).ToList();
        var rejectedDecisions = decisions.Where(d => d.Result == UsageDecisionResult.Rejected).ToList();
        Assert.Equal(10, acceptedDecisions.Count);
        Assert.Equal(10, rejectedDecisions.Count);
        Assert.All(rejectedDecisions, d => Assert.Equal(UsageDecisionReasons.InsufficientCredits, d.Reason));

        // And: accepted credits total does not exceed the remaining 10 credits.
        var acceptedTotal = acceptedDecisions.Sum(d => d.RequestedCredits!.Value);
        Assert.Equal(10, acceptedTotal);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(10, status.Window5h.UsedCredits);
        Assert.Equal(0, status.Window5h.RemainingCredits);
        Assert.Equal(10, status.Window7d.UsedCredits);
        Assert.Equal(0, status.Window7d.RemainingCredits);
        Assert.Equal(0, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task TC_CONSISTENCY_002_AcceptedUsageStillQueryableAfterRestart()
    {
        // TC-CONSISTENCY-002 重啟後 accepted usage 仍可查詢
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 0);

        // Given: user-a consumed 20 credits and the decision is accepted (at 2026-07-01T00:00:00Z).
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20, idempotencyKey: "k-persist-accepted"));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.NotNull(decision.AuditReference);

        // When: the service restarts over the same database persistence.
        fixture.Restart();

        // Then: usage status still includes the 20 credits in both windows.
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(20, status.Window5h.UsedCredits);
        Assert.Equal(80, status.Window5h.RemainingCredits);
        Assert.Equal(20, status.Window7d.UsedCredits);
        Assert.Equal(980, status.Window7d.RemainingCredits);

        // And: the audit trail still contains the accepted usage record.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Accepted
                                    && r.AuditId == decision.AuditReference
                                    && r.Credits == 20
                                    && r.UserId == "user-a"
                                    && r.SubscriptionId == "sub-a");

        // And: the usage stays counted until the rolling window expires.
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 1, 4, 59, 59, TimeSpan.Zero));
        var beforeExpiry = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(beforeExpiry);
        Assert.Equal(20, beforeExpiry.Window5h.UsedCredits);
        Assert.Equal(20, beforeExpiry.Window7d.UsedCredits);

        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero));
        var afterExpiry = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(afterExpiry);
        Assert.Equal(0, afterExpiry.Window5h.UsedCredits);
        Assert.Equal(20, afterExpiry.Window7d.UsedCredits);
    }

    [Fact]
    public async Task TC_CONSISTENCY_003_RejectedUsageStillTraceableAfterRestart()
    {
        // TC-CONSISTENCY-003 重啟後 rejected usage 仍可回溯
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 5, limit7d: 1000, extraPool: 0);

        // Given: user-a consumed 20 credits and the decision is rejected (insufficient credits).
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20, idempotencyKey: "k-persist-rejected"));
        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, decision.Reason);
        Assert.NotNull(decision.AuditReference);

        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusBefore);

        // When: the service restarts over the same database persistence.
        fixture.Restart();

        // Then: the audit trail still contains the rejected usage record.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Rejected
                                    && r.AuditId == decision.AuditReference
                                    && r.Reason == UsageDecisionReasons.InsufficientCredits
                                    && r.Credits == 20
                                    && r.UserId == "user-a"
                                    && r.SubscriptionId == "sub-a");

        // And: usage status did not count the rejected request as used credits.
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(0, statusAfter.Window5h.UsedCredits);
        Assert.Equal(0, statusAfter.Window7d.UsedCredits);
        Assert.Equal(statusBefore.Window5h.UsedCredits, statusAfter.Window5h.UsedCredits);
        Assert.Equal(statusBefore.Window7d.UsedCredits, statusAfter.Window7d.UsedCredits);
        Assert.Equal(statusBefore.ExtraPoolRemainingCredits, statusAfter.ExtraPoolRemainingCredits);
    }
}
