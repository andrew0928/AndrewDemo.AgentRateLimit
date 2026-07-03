using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the Credit Validation cases (TC-CREDIT-001 .. TC-CREDIT-007)
/// of spec/testcases/subscription-credit-rate-limit-v1.md.
/// </summary>
public class CreditValidationTests
{
    [Fact]
    public async Task TC_CREDIT_001_PositiveIntegerCreditsAccepted()
    {
        // TC-CREDIT-001 正整數 credit 可接受
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a", limit5h: 100, limit7d: 1000);

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 10));

        // Then: decision result is accepted.
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        // And: every returned credit field is an integer. The contract types them as
        // long/long?, so integrality is guaranteed at compile time; assert the exact
        // integral values here.
        Assert.Equal(10, decision.RequestedCredits);
        Assert.Equal(10, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(0, decision.CoveredByExtraPool);
        Assert.Equal(90, decision.Remaining5hCreditsAfterDecision);
        Assert.Equal(990, decision.Remaining7dCreditsAfterDecision);
        Assert.Equal(0, decision.RemainingExtraPoolCreditsAfterDecision);
    }

    [Fact]
    public async Task TC_CREDIT_002_FractionalCreditsInvalid()
    {
        // TC-CREDIT-002 小數 credit 不合法
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a");

        var before = await fixture.Usage.GetUsageStatusAsync("sub-a");

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 1.5m));

        // Then: decision result is invalid with reason credits-not-integer.
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.CreditsNotInteger, decision.Reason);

        // And: usage status is unchanged.
        var after = await fixture.Usage.GetUsageStatusAsync("sub-a");
        AssertUsageStatusUnchanged(before, after);

        // Spec 6: the invalid decision remains queryable in the audit trail.
        Assert.NotNull(decision.AuditReference);
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.AuditId == decision.AuditReference
                                    && r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Invalid
                                    && r.Reason == UsageDecisionReasons.CreditsNotInteger);
    }

    [Fact]
    public async Task TC_CREDIT_003_ZeroCreditsInvalid()
    {
        // TC-CREDIT-003 zero credit 不合法
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a");

        var before = await fixture.Usage.GetUsageStatusAsync("sub-a");

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 0));

        // Then: decision result is invalid with reason credits-not-positive.
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.CreditsNotPositive, decision.Reason);

        // And: usage status is unchanged.
        var after = await fixture.Usage.GetUsageStatusAsync("sub-a");
        AssertUsageStatusUnchanged(before, after);

        // Spec 6: the invalid decision remains queryable in the audit trail.
        Assert.NotNull(decision.AuditReference);
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.AuditId == decision.AuditReference
                                    && r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Invalid
                                    && r.Reason == UsageDecisionReasons.CreditsNotPositive);
    }

    [Fact]
    public async Task TC_CREDIT_004_NegativeCreditsInvalid()
    {
        // TC-CREDIT-004 negative credit 不合法
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a");

        var before = await fixture.Usage.GetUsageStatusAsync("sub-a");

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: -1));

        // Then: decision result is invalid with reason credits-not-positive.
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.CreditsNotPositive, decision.Reason);

        // And: usage status is unchanged.
        var after = await fixture.Usage.GetUsageStatusAsync("sub-a");
        AssertUsageStatusUnchanged(before, after);

        // Spec 6: the invalid decision remains queryable in the audit trail.
        Assert.NotNull(decision.AuditReference);
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.AuditId == decision.AuditReference
                                    && r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Invalid
                                    && r.Reason == UsageDecisionReasons.CreditsNotPositive);
    }

    [Fact]
    public async Task TC_CREDIT_005_MissingUserIdInvalid()
    {
        // TC-CREDIT-005 缺少 user id 不合法
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a");

        var before = await fixture.Usage.GetUsageStatusAsync("sub-a");

        var request = new UsageRequest
        {
            UserId = null,
            SubscriptionId = "sub-a",
            RequestedCredits = 10,
            IdempotencyKey = Guid.NewGuid().ToString("n"),
            CorrelationId = Guid.NewGuid().ToString("n"),
        };
        var decision = await fixture.Usage.ConsumeAsync(request);

        // Then: decision result is invalid with reason missing-user-id.
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.MissingUserId, decision.Reason);

        // And: usage status is unchanged.
        var after = await fixture.Usage.GetUsageStatusAsync("sub-a");
        AssertUsageStatusUnchanged(before, after);

        // Spec 6: the invalid decision remains queryable in the audit trail (the
        // request carried a subscription id to query by).
        Assert.NotNull(decision.AuditReference);
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.AuditId == decision.AuditReference
                                    && r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Invalid
                                    && r.Reason == UsageDecisionReasons.MissingUserId);
    }

    [Fact]
    public async Task TC_CREDIT_006_MissingSubscriptionIdInvalid()
    {
        // TC-CREDIT-006 缺少 subscription id 不合法
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a");

        var before = await fixture.Usage.GetUsageStatusAsync("sub-a");

        var request = new UsageRequest
        {
            UserId = "user-a",
            SubscriptionId = null,
            RequestedCredits = 10,
            IdempotencyKey = Guid.NewGuid().ToString("n"),
            CorrelationId = Guid.NewGuid().ToString("n"),
        };
        var decision = await fixture.Usage.ConsumeAsync(request);

        // Then: decision result is invalid with reason missing-subscription-id.
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.MissingSubscriptionId, decision.Reason);

        // And: usage status is unchanged (checked against the only existing subscription).
        var after = await fixture.Usage.GetUsageStatusAsync("sub-a");
        AssertUsageStatusUnchanged(before, after);

        // Spec 6: the invalid decision remains observable after the fact. Without a
        // subscription id there is nothing to query the audit trail by, so verify it
        // through the reconciliation report's unattributed invalid request count.
        Assert.NotNull(decision.AuditReference);
        var report = await fixture.Usage.ExportReconciliationReportAsync(
            SubscriptionCreditServiceFixture.DefaultStart,
            SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromDays(1));
        Assert.Equal(1, report.UnattributedInvalidRequestCount);
    }

    [Fact]
    public async Task TC_CREDIT_007_MissingIdempotencyKeyInvalid()
    {
        // TC-CREDIT-007 缺少 idempotency key 不合法
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-a", subscriptionId: "sub-a");

        var before = await fixture.Usage.GetUsageStatusAsync("sub-a");

        var request = new UsageRequest
        {
            UserId = "user-a",
            SubscriptionId = "sub-a",
            RequestedCredits = 10,
            IdempotencyKey = null,
            CorrelationId = Guid.NewGuid().ToString("n"),
        };
        var decision = await fixture.Usage.ConsumeAsync(request);

        // Then: decision result is invalid with reason missing-idempotency-key.
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.MissingIdempotencyKey, decision.Reason);

        // And: usage status is unchanged.
        var after = await fixture.Usage.GetUsageStatusAsync("sub-a");
        AssertUsageStatusUnchanged(before, after);

        // Spec 6: the invalid decision remains queryable in the audit trail.
        Assert.NotNull(decision.AuditReference);
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.Contains(audit, r => r.AuditId == decision.AuditReference
                                    && r.RecordType == AuditRecordType.UsageDecision
                                    && r.DecisionResult == UsageDecisionResult.Invalid
                                    && r.Reason == UsageDecisionReasons.MissingIdempotencyKey);
    }

    /// <summary>
    /// Asserts the "usage status 不變" clauses: window limits, used and remaining
    /// credits, the extra pool balance and the enabled flag are all unchanged.
    /// </summary>
    private static void AssertUsageStatusUnchanged(SubscriptionUsageStatus? before, SubscriptionUsageStatus? after)
    {
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.Window5h.LimitCredits, after.Window5h.LimitCredits);
        Assert.Equal(before.Window5h.UsedCredits, after.Window5h.UsedCredits);
        Assert.Equal(before.Window5h.RemainingCredits, after.Window5h.RemainingCredits);
        Assert.Equal(before.Window7d.LimitCredits, after.Window7d.LimitCredits);
        Assert.Equal(before.Window7d.UsedCredits, after.Window7d.UsedCredits);
        Assert.Equal(before.Window7d.RemainingCredits, after.Window7d.RemainingCredits);
        Assert.Equal(before.ExtraPoolRemainingCredits, after.ExtraPoolRemainingCredits);
        Assert.Equal(before.Enabled, after.Enabled);
    }
}
