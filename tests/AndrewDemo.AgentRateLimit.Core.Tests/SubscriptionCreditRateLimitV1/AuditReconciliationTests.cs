using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Acceptance tests for the Audit And Reconciliation section (TC-AUDIT-001 .. TC-AUDIT-004).
/// </summary>
public class AuditReconciliationTests
{
    [Fact]
    public async Task TC_AUDIT_001_AcceptedUsageHasCompleteAuditRecord()
    {
        // TC-AUDIT-001: accepted usage 有完整 audit record
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 0);

        // Given: user-a consumes 20 credits.
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(
            credits: 20,
            idempotencyKey: "k-audit-001",
            correlationId: "corr-audit-001"));

        // When: decision result is accepted.
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.NotNull(decision.AuditReference);

        // Then: the audit trail contains that usage (the decision's audit reference
        // resolves to exactly one record).
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        var record = Assert.Single(audit, r => r.AuditId == decision.AuditReference);
        Assert.Equal(AuditRecordType.UsageDecision, record.RecordType);

        // And: the audit record contains user id, subscription id, requested credits,
        // covered by subscription allowance, covered by extra pool, decision time,
        // correlation id, idempotency key and decision result.
        Assert.Equal("user-a", record.UserId);
        Assert.Equal("sub-a", record.SubscriptionId);
        Assert.Equal(20, record.Credits);
        Assert.Equal(20, record.CoveredBySubscriptionAllowance);
        Assert.Equal(0, record.CoveredByExtraPool);
        Assert.Equal(SubscriptionCreditServiceFixture.DefaultStart, decision.DecisionTime);
        Assert.Equal(decision.DecisionTime, record.OccurredAt);
        Assert.Equal("corr-audit-001", record.CorrelationId);
        Assert.Equal("k-audit-001", record.IdempotencyKey);
        Assert.Equal(UsageDecisionResult.Accepted, record.DecisionResult);
    }

    [Fact]
    public async Task TC_AUDIT_002_RejectedUsageHasCompleteAuditRecord()
    {
        // TC-AUDIT-002: rejected usage 有完整 audit record
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 10, limit7d: 10, extraPool: 0);

        // Given: user-a consumes 20 credits and the decision result is rejected
        // (only 10 credits are coverable, no extra pool).
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(
            credits: 20,
            idempotencyKey: "k-audit-002",
            correlationId: "corr-audit-002"));
        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, decision.Reason);
        Assert.NotNull(decision.AuditReference);

        // Then: the audit trail contains that rejected usage.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        var record = Assert.Single(audit, r => r.AuditId == decision.AuditReference);
        Assert.Equal(AuditRecordType.UsageDecision, record.RecordType);
        Assert.Equal(UsageDecisionResult.Rejected, record.DecisionResult);
        Assert.Equal("user-a", record.UserId);
        Assert.Equal("sub-a", record.SubscriptionId);
        Assert.Equal(20, record.Credits);

        // And: the audit record contains the rejection reason.
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, record.Reason);

        // And: the reconciliation report does not count that request as accepted credits.
        var report = await fixture.Usage.ExportReconciliationReportAsync(
            SubscriptionCreditServiceFixture.DefaultStart,
            SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromDays(1));
        var row = Assert.Single(report.Subscriptions);
        Assert.Equal("sub-a", row.SubscriptionId);
        Assert.Equal(0, row.AcceptedCredits);
        Assert.Equal(0, row.AcceptedRequestCount);
        Assert.Equal(20, row.RejectedCredits);
        Assert.Equal(1, row.RejectedRequestCount);
    }

    [Fact]
    public async Task TC_AUDIT_003_ManualCorrectionDoesNotOverwriteOriginalRecord()
    {
        // TC-AUDIT-003: 帳務修正不可覆蓋原始紀錄
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 0);

        // Given: the audit trail already has one accepted usage.
        var accepted = await fixture.Usage.ConsumeAsync(fixture.Request(
            credits: 20,
            idempotencyKey: "k-audit-003",
            correlationId: "corr-audit-003"));
        Assert.Equal(UsageDecisionResult.Accepted, accepted.Result);
        Assert.NotNull(accepted.AuditReference);

        // When: an authorized actor records a manual correction referencing the
        // original accepted usage.
        var correction = await fixture.Admin.RecordManualCorrectionAsync(new ManualCorrection
        {
            SubscriptionId = "sub-a",
            Credits = 20,
            Actor = "ops-admin",
            Reason = "billing-correction",
            RelatedAuditId = accepted.AuditReference,
        });

        // Then: the audit trail retains BOTH the original accepted usage and the
        // correction record.
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });

        var original = Assert.Single(audit, r => r.AuditId == accepted.AuditReference);
        Assert.Equal(AuditRecordType.UsageDecision, original.RecordType);
        Assert.Equal(UsageDecisionResult.Accepted, original.DecisionResult);
        Assert.Equal(20, original.Credits);
        Assert.Equal("user-a", original.UserId);
        Assert.Equal("sub-a", original.SubscriptionId);

        var correctionRecord = Assert.Single(audit, r => r.RecordType == AuditRecordType.ManualCorrection);
        Assert.Equal(correction.AuditId, correctionRecord.AuditId);
        Assert.NotEqual(original.AuditId, correctionRecord.AuditId);
        Assert.Equal(accepted.AuditReference, correctionRecord.RelatedAuditId);
        Assert.Equal("ops-admin", correctionRecord.Actor);
        Assert.Equal("billing-correction", correctionRecord.Reason);
        Assert.Equal(20, correctionRecord.Credits);

        // And: the reconciliation report lists the original usage and the correction
        // separately — accepted totals are unchanged and the correction is counted.
        var report = await fixture.Usage.ExportReconciliationReportAsync(
            SubscriptionCreditServiceFixture.DefaultStart,
            SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromDays(1));
        var row = Assert.Single(report.Subscriptions);
        Assert.Equal("sub-a", row.SubscriptionId);
        Assert.Equal(1, row.ManualCorrectionCount);
        Assert.Equal(20, row.AcceptedCredits);
        Assert.Equal(1, row.AcceptedRequestCount);
        Assert.Equal(20, row.CoveredBySubscriptionAllowanceCredits);
        Assert.Equal(0, row.CoveredByExtraPoolCredits);
    }

    [Fact]
    public async Task TC_AUDIT_004_ReconciliationReportReconstructsPeriodCreditMovement()
    {
        // TC-AUDIT-004: reconciliation report 可重建期間 credit 變化
        using var fixture = new SubscriptionCreditServiceFixture();

        // Subscription is provisioned BEFORE the report period so that the extra pool
        // seed of 30 forms the period's beginning balance rather than added credits.
        await fixture.CreateSubscriptionAsync(limit5h: 50, limit7d: 1000, extraPool: 30);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        var periodFrom = SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromHours(1);
        var periodTo = SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromHours(2);

        // Given (in period): an accepted usage fully covered by the allowance.
        var accepted1 = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 40, idempotencyKey: "k-audit-004"));
        Assert.Equal(UsageDecisionResult.Accepted, accepted1.Result);
        Assert.Equal(40, accepted1.CoveredBySubscriptionAllowance);
        Assert.Equal(0, accepted1.CoveredByExtraPool);
        Assert.NotNull(accepted1.AuditReference);

        // And: an idempotency conflict (same key, different payload).
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var conflict = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 41, idempotencyKey: "k-audit-004"));
        Assert.Equal(UsageDecisionResult.Conflict, conflict.Result);
        Assert.Equal(UsageDecisionReasons.IdempotencyKeyPayloadMismatch, conflict.Reason);

        // And: an invalid request (fractional credits) attributed to sub-a.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var invalid = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 2.5m));
        Assert.Equal(UsageDecisionResult.Invalid, invalid.Result);
        Assert.Equal(UsageDecisionReasons.CreditsNotInteger, invalid.Reason);

        // And: a rejected usage (coverable is min(10, 960) + 30 = 40 < 100).
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var rejected = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 100));
        Assert.Equal(UsageDecisionResult.Rejected, rejected.Result);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, rejected.Reason);

        // And: an extra pool top-up of 50 (pool 30 -> 80).
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
        {
            SubscriptionId = "sub-a",
            DeltaCredits = 50,
            Actor = "ops-admin",
            Reason = "manual-top-up",
        });

        // And: an accepted usage that consumes the extra pool
        // (5h remaining 10, extra covers 15; pool 80 -> 65).
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var accepted2 = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 25));
        Assert.Equal(UsageDecisionResult.Accepted, accepted2.Result);
        Assert.Equal(10, accepted2.CoveredBySubscriptionAllowance);
        Assert.Equal(15, accepted2.CoveredByExtraPool);
        Assert.Equal(65, accepted2.RemainingExtraPoolCreditsAfterDecision);

        // And: a negative extra pool adjustment of 5 (pool 65 -> 60).
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
        {
            SubscriptionId = "sub-a",
            DeltaCredits = -5,
            Actor = "ops-admin",
            Reason = "reclaim-promotional-credits",
        });

        // And: a manual correction referencing the first accepted usage.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Admin.RecordManualCorrectionAsync(new ManualCorrection
        {
            SubscriptionId = "sub-a",
            Credits = 40,
            Actor = "ops-admin",
            Reason = "billing-correction",
            RelatedAuditId = accepted1.AuditReference,
        });

        // When: the reconciliation report is exported for the half-open period.
        var report = await fixture.Usage.ExportReconciliationReportAsync(periodFrom, periodTo);
        Assert.Equal(periodFrom, report.PeriodFromInclusive);
        Assert.Equal(periodTo, report.PeriodToExclusive);
        var row = Assert.Single(report.Subscriptions);
        Assert.Equal("sub-a", row.SubscriptionId);
        Assert.Equal("user-a", row.UserId);

        // Then: accepted credits total (40 + 25).
        Assert.Equal(65, row.AcceptedCredits);
        Assert.Equal(2, row.AcceptedRequestCount);

        // And: rejected credits total.
        Assert.Equal(100, row.RejectedCredits);
        Assert.Equal(1, row.RejectedRequestCount);

        // And: subscription allowance covered credits total (40 + 10) and extra pool
        // covered credits total (15).
        Assert.Equal(50, row.CoveredBySubscriptionAllowanceCredits);
        Assert.Equal(15, row.CoveredByExtraPoolCredits);

        // And: extra pool beginning balance, added, consumed, adjusted and ending balance.
        Assert.Equal(30, row.ExtraPoolBeginningBalance);
        Assert.Equal(50, row.ExtraPoolAddedCredits);
        Assert.Equal(15, row.ExtraPoolConsumedCredits);
        Assert.Equal(-5, row.ExtraPoolAdjustedCredits);
        Assert.Equal(60, row.ExtraPoolEndingBalance);
        Assert.Equal(
            row.ExtraPoolBeginningBalance + row.ExtraPoolAddedCredits + row.ExtraPoolAdjustedCredits - row.ExtraPoolConsumedCredits,
            row.ExtraPoolEndingBalance);

        // And: conflict, invalid request and manual correction counts.
        Assert.Equal(1, row.ConflictCount);
        Assert.Equal(1, row.InvalidRequestCount);
        Assert.Equal(1, row.ManualCorrectionCount);
        Assert.Equal(0, report.UnattributedInvalidRequestCount);
    }
}
