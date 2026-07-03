using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the Idempotency section of the V1 test cases:
/// resending the same idempotency key with the same payload replays the original
/// decision without a second charge, and resending the same key with a different
/// payload yields a conflict that changes nothing but leaves an audit record.
/// </summary>
public class IdempotencyTests
{
    [Fact]
    public async Task TC_IDEMP_001_SameKeySamePayloadReplaysFirstDecisionWithoutDoubleCharge()
    {
        // TC-IDEMP-001: 相同 idempotency key 與相同 payload 重送不重複扣款
        using var fixture = new SubscriptionCreditServiceFixture();

        // limit5h 25 leaves only 5 allowance after the first 20-credit consume, so a
        // real (non-idempotent) second 20-credit charge would have to dip 15 into the
        // extra pool. The replay must leave the pool untouched.
        await fixture.CreateSubscriptionAsync(limit5h: 25, limit7d: 1000, extraPool: 50);

        // Given: user-a consumes 20 credits with idempotency key k-001 (one request
        // object reused so the resend payload is identical).
        var request = fixture.Request(credits: 20, idempotencyKey: "k-001");

        // And: first decision result is accepted.
        var first = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Accepted, first.Result);
        Assert.False(first.IsIdempotentReplay);
        Assert.Equal(20, first.CoveredBySubscriptionAllowance);
        Assert.Equal(0, first.CoveredByExtraPool);
        Assert.Equal(5, first.Remaining5hCreditsAfterDecision);
        Assert.Equal(980, first.Remaining7dCreditsAfterDecision);
        Assert.Equal(50, first.RemainingExtraPoolCreditsAfterDecision);
        Assert.NotNull(first.AuditReference);

        // When: the exact same request is resent with the same key k-001.
        var replay = await fixture.Usage.ConsumeAsync(request);

        // Then: the first decision is returned (same result, covered amounts,
        // remainings and audit reference), flagged as an idempotent replay.
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.Result, replay.Result);
        Assert.Equal(first.RequestedCredits, replay.RequestedCredits);
        Assert.Equal(first.CoveredBySubscriptionAllowance, replay.CoveredBySubscriptionAllowance);
        Assert.Equal(first.CoveredByExtraPool, replay.CoveredByExtraPool);
        Assert.Equal(first.Remaining5hCreditsAfterDecision, replay.Remaining5hCreditsAfterDecision);
        Assert.Equal(first.Remaining7dCreditsAfterDecision, replay.Remaining7dCreditsAfterDecision);
        Assert.Equal(first.RemainingExtraPoolCreditsAfterDecision, replay.RemainingExtraPoolCreditsAfterDecision);
        Assert.Equal(first.AuditReference, replay.AuditReference);

        // And: usage status reflects only one 20-credit consumption.
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(20, status.Window5h.UsedCredits);
        Assert.Equal(5, status.Window5h.RemainingCredits);
        Assert.Equal(20, status.Window7d.UsedCredits);
        Assert.Equal(980, status.Window7d.RemainingCredits);

        // And: the extra pool is not consumed again by the resend.
        Assert.Equal(50, status.ExtraPoolRemainingCredits);

        // Supporting evidence for the single charge: the audit trail carries exactly
        // one accepted usage decision for key k-001.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Single(audit, r => r.RecordType == AuditRecordType.UsageDecision
                                  && r.DecisionResult == UsageDecisionResult.Accepted
                                  && r.IdempotencyKey == "k-001");
    }

    [Fact]
    public async Task TC_IDEMP_002_SameKeyDifferentPayloadReturnsConflict()
    {
        // TC-IDEMP-002: 相同 idempotency key 但不同 payload 回傳 conflict
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 50);

        // Given: user-a consumes 20 credits with idempotency key k-002.
        var first = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20, idempotencyKey: "k-002"));

        // And: first decision result is accepted.
        Assert.Equal(UsageDecisionResult.Accepted, first.Result);

        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusBefore);

        // When: resend with the same key k-002 but requested credits changed to 25.
        var conflict = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 25, idempotencyKey: "k-002"));

        // Then: decision result is conflict.
        Assert.Equal(UsageDecisionResult.Conflict, conflict.Result);

        // And: conflict reason is idempotency-key-payload-mismatch.
        Assert.Equal(UsageDecisionReasons.IdempotencyKeyPayloadMismatch, conflict.Reason);
        Assert.False(conflict.IsIdempotentReplay);

        // And: usage status is unchanged by the conflict.
        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(statusBefore.Window5h, statusAfter.Window5h);
        Assert.Equal(statusBefore.Window7d, statusAfter.Window7d);
        Assert.Equal(statusBefore.ExtraPoolRemainingCredits, statusAfter.ExtraPoolRemainingCredits);
        Assert.Equal(statusBefore.Enabled, statusAfter.Enabled);

        // And: the audit trail contains the conflict record.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        var conflictRecord = Assert.Single(audit, r => r.RecordType == AuditRecordType.UsageDecision
                                                       && r.DecisionResult == UsageDecisionResult.Conflict);
        Assert.Equal(UsageDecisionReasons.IdempotencyKeyPayloadMismatch, conflictRecord.Reason);
        Assert.Equal("k-002", conflictRecord.IdempotencyKey);
        Assert.Equal("sub-a", conflictRecord.SubscriptionId);
    }
}
