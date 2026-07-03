using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Implementation edge cases beyond the numbered acceptance TCs: idempotency across
/// restart and across decision outcomes, preview vs idempotency bindings, extra pool
/// guard rails, out-of-range credits, validation order, audit query targeting, and
/// the reconciliation extra pool balance invariant.
/// </summary>
public class EdgeCaseTests
{
    [Fact]
    public async Task EDGE_IdempotentReplayAcrossRestart()
    {
        // EDGE: an accepted decision replays after a service restart over the same
        // database persistence (spec 4.6 + section 6) without double charging. The
        // subscription is short on the 5h window so the charge dips into the extra
        // pool, making the replayed extra pool snapshot a meaningful assertion.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 15, limit7d: 1000, extraPool: 30);

        var request = fixture.Request(credits: 20, idempotencyKey: "k-restart");
        var first = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Accepted, first.Result);
        Assert.False(first.IsIdempotentReplay);
        Assert.NotNull(first.AuditReference);
        Assert.Equal(15, first.CoveredBySubscriptionAllowance);
        Assert.Equal(5, first.CoveredByExtraPool);
        Assert.Equal(25, first.RemainingExtraPoolCreditsAfterDecision);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Restart();

        var replay = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Accepted, replay.Result);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.AuditReference, replay.AuditReference);
        Assert.Equal(first.RequestedCredits, replay.RequestedCredits);
        Assert.Equal(first.CoveredBySubscriptionAllowance, replay.CoveredBySubscriptionAllowance);
        Assert.Equal(first.CoveredByExtraPool, replay.CoveredByExtraPool);
        Assert.Equal(first.Remaining5hCreditsAfterDecision, replay.Remaining5hCreditsAfterDecision);
        Assert.Equal(first.Remaining7dCreditsAfterDecision, replay.Remaining7dCreditsAfterDecision);
        Assert.Equal(first.RemainingExtraPoolCreditsAfterDecision, replay.RemainingExtraPoolCreditsAfterDecision);
        Assert.Equal(first.DecisionTime, replay.DecisionTime);

        // No double charge: the status reflects exactly one 20-credit consumption and
        // exactly one 5-credit extra pool dip.
        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(20, status.Window5h.UsedCredits);
        Assert.Equal(0, status.Window5h.RemainingCredits);
        Assert.Equal(20, status.Window7d.UsedCredits);
        Assert.Equal(25, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task EDGE_RejectedDecisionBindsIdempotencyKey()
    {
        // EDGE: a rejected decision binds the idempotency key too — spec 4.6 requires a
        // same-key same-payload resend to return the original decision, whatever it
        // was, even after a top-up would let a fresh evaluation accept.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 5, limit7d: 1000, extraPool: 0);

        var request = fixture.Request(credits: 20, idempotencyKey: "k-rejected");
        var first = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Rejected, first.Result);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, first.Reason);
        Assert.NotNull(first.AuditReference);

        // Top up so that a fresh evaluation of the same payload WOULD now be accepted
        // (allowance 5 + extra pool 100 >= 20).
        await fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
        {
            SubscriptionId = "sub-a",
            DeltaCredits = 100,
            Actor = "ops-admin",
            Reason = "manual-top-up",
        });

        var resend = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Rejected, resend.Result);
        Assert.Equal(UsageDecisionReasons.InsufficientCredits, resend.Reason);
        Assert.True(resend.IsIdempotentReplay);
        Assert.Equal(first.AuditReference, resend.AuditReference);

        // The replay consumed nothing.
        var statusAfterResend = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfterResend);
        Assert.Equal(0, statusAfterResend.Window5h.UsedCredits);
        Assert.Equal(0, statusAfterResend.Window7d.UsedCredits);
        Assert.Equal(100, statusAfterResend.ExtraPoolRemainingCredits);

        // A different key with the same payload gets a fresh evaluation: accepted.
        var fresh = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20, idempotencyKey: "k-rejected-2"));
        Assert.Equal(UsageDecisionResult.Accepted, fresh.Result);
        Assert.False(fresh.IsIdempotentReplay);
        Assert.Equal(5, fresh.CoveredBySubscriptionAllowance);
        Assert.Equal(15, fresh.CoveredByExtraPool);
        Assert.Equal(85, fresh.RemainingExtraPoolCreditsAfterDecision);
    }

    [Fact]
    public async Task EDGE_PreviewDoesNotBindIdempotencyKey()
    {
        // EDGE: preview evaluates the decision pipeline without binding the idempotency
        // key (spec 3.2), so a later consume with the same key and payload is a real
        // first decision, not a replay.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync();

        var request = fixture.Request(credits: 20, idempotencyKey: "k-preview");
        var preview = await fixture.Usage.PreviewAsync(request);
        Assert.Equal(UsageDecisionResult.Accepted, preview.Result);
        Assert.False(preview.IsIdempotentReplay);
        Assert.Null(preview.AuditReference); // previews are not accounting records

        var statusAfterPreview = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfterPreview);
        Assert.Equal(0, statusAfterPreview.Window5h.UsedCredits);
        Assert.Equal(0, statusAfterPreview.Window7d.UsedCredits);

        var consume = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Accepted, consume.Result);
        Assert.False(consume.IsIdempotentReplay);
        Assert.NotNull(consume.AuditReference);

        var statusAfterConsume = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfterConsume);
        Assert.Equal(20, statusAfterConsume.Window5h.UsedCredits);
        Assert.Equal(20, statusAfterConsume.Window7d.UsedCredits);
    }

    [Fact]
    public async Task EDGE_PreviewMirrorsIdempotentReplay()
    {
        // EDGE: previewing a key already bound by an accepted consume mirrors the
        // stored decision (preview runs the same pipeline as consume, spec 3.2 + 4.6)
        // and changes nothing.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 0);

        var request = fixture.Request(credits: 20, idempotencyKey: "k-mirror");
        var original = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Accepted, original.Result);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var statusBefore = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusBefore);

        var mirrored = await fixture.Usage.PreviewAsync(request);
        Assert.True(mirrored.IsIdempotentReplay);
        Assert.Equal(UsageDecisionResult.Accepted, mirrored.Result);
        Assert.Equal(original.AuditReference, mirrored.AuditReference);
        Assert.Equal(original.RequestedCredits, mirrored.RequestedCredits);
        Assert.Equal(original.CoveredBySubscriptionAllowance, mirrored.CoveredBySubscriptionAllowance);
        Assert.Equal(original.CoveredByExtraPool, mirrored.CoveredByExtraPool);
        Assert.Equal(original.Remaining5hCreditsAfterDecision, mirrored.Remaining5hCreditsAfterDecision);
        Assert.Equal(original.Remaining7dCreditsAfterDecision, mirrored.Remaining7dCreditsAfterDecision);
        Assert.Equal(original.RemainingExtraPoolCreditsAfterDecision, mirrored.RemainingExtraPoolCreditsAfterDecision);
        Assert.Equal(original.DecisionTime, mirrored.DecisionTime);

        var statusAfter = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(statusAfter);
        Assert.Equal(statusBefore.Window5h.UsedCredits, statusAfter.Window5h.UsedCredits);
        Assert.Equal(statusBefore.Window5h.RemainingCredits, statusAfter.Window5h.RemainingCredits);
        Assert.Equal(statusBefore.Window7d.UsedCredits, statusAfter.Window7d.UsedCredits);
        Assert.Equal(statusBefore.ExtraPoolRemainingCredits, statusAfter.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task EDGE_NegativeAdjustmentBelowZeroRefused()
    {
        // EDGE: the extra pool must never go negative (spec 4.4); an adjustment that
        // would cross zero is refused, the balance stays unchanged, and no adjustment
        // audit record is produced.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(extraPool: 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Admin.AdjustExtraPoolAsync(
            new ExtraPoolAdjustment
            {
                SubscriptionId = "sub-a",
                DeltaCredits = -20,
                Actor = "ops-admin",
                Reason = "attempted-over-reduction",
            }));

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(10, status.ExtraPoolRemainingCredits);

        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        Assert.DoesNotContain(audit, r => r.RecordType == AuditRecordType.ExtraPoolAdjustment);
    }

    [Fact]
    public async Task EDGE_WindowUsedCanExceedLimitViaExtraPool()
    {
        // EDGE: accepted usage covered by the extra pool still counts toward window
        // usage (spec 4.2), so window used credits can exceed the window limit while
        // remaining credits floors at zero.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 10, limit7d: 1000, extraPool: 50);

        var fill = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 10));
        Assert.Equal(UsageDecisionResult.Accepted, fill.Result);
        Assert.Equal(10, fill.CoveredBySubscriptionAllowance);
        Assert.Equal(0, fill.CoveredByExtraPool);

        // 5h remaining is now 0, so the allowance min(0, 990) = 0 covers nothing and
        // the extra pool covers the whole 20.
        var overflow = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));
        Assert.Equal(UsageDecisionResult.Accepted, overflow.Result);
        Assert.Equal(0, overflow.CoveredBySubscriptionAllowance);
        Assert.Equal(20, overflow.CoveredByExtraPool);
        Assert.Equal(30, overflow.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(10, status.Window5h.LimitCredits);
        Assert.Equal(30, status.Window5h.UsedCredits);
        Assert.True(status.Window5h.UsedCredits > status.Window5h.LimitCredits);
        Assert.Equal(0, status.Window5h.RemainingCredits);
        Assert.Equal(30, status.Window7d.UsedCredits);
        Assert.Equal(30, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public async Task EDGE_CreditsOutOfRangeInvalid()
    {
        // EDGE: a positive integer beyond the Int64 accounting range is invalid with
        // the implementation-defined reason credits-out-of-range and charges nothing.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync();

        // long.MaxValue (9223372036854775807) + 1
        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 9223372036854775808m));
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.CreditsOutOfRange, decision.Reason);
        Assert.Null(decision.RequestedCredits); // not representable as an Int64
        Assert.Equal(0, decision.CoveredBySubscriptionAllowance);
        Assert.Equal(0, decision.CoveredByExtraPool);
        Assert.Null(decision.Remaining5hCreditsAfterDecision);
        Assert.Null(decision.Remaining7dCreditsAfterDecision);
        Assert.Null(decision.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(0, status.Window5h.UsedCredits);
        Assert.Equal(0, status.Window7d.UsedCredits);
    }

    [Fact]
    public async Task EDGE_ValidationOrderMissingUserIdFirst()
    {
        // EDGE: identity fields are validated before credit format, so a request that
        // is missing the user id AND has fractional credits reports missing-user-id,
        // not credits-not-integer.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync();

        var request = fixture.Request(credits: 1.5m) with { UserId = null };
        var decision = await fixture.Usage.ConsumeAsync(request);
        Assert.Equal(UsageDecisionResult.Invalid, decision.Result);
        Assert.Equal(UsageDecisionReasons.MissingUserId, decision.Reason);
        Assert.Null(decision.RequestedCredits); // 1.5 is not representable as an integer

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(0, status.Window5h.UsedCredits);
        Assert.Equal(0, status.Window7d.UsedCredits);
    }

    [Fact]
    public async Task EDGE_AuditQueryRequiresTarget()
    {
        // EDGE: audit trail queries must target a user or a subscription (spec section
        // 5 privacy boundary); an untargeted query is refused instead of enumerating
        // every user's records.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery()));

        // Time filters alone do not make the query targeted.
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery
            {
                FromInclusive = SubscriptionCreditServiceFixture.DefaultStart,
                ToExclusive = SubscriptionCreditServiceFixture.DefaultStart + TimeSpan.FromDays(1),
            }));
    }

    [Fact]
    public async Task EDGE_ReconciliationPoolInvariantAcrossOperations()
    {
        // EDGE: reconciliation extra pool figures satisfy
        // ending = beginning + added + adjusted - consumed across seed, top-up,
        // extra-covered usage and negative adjustment; a later period with no movement
        // carries the balance through unchanged.
        using var fixture = new SubscriptionCreditServiceFixture();
        var start = SubscriptionCreditServiceFixture.DefaultStart;
        await fixture.CreateSubscriptionAsync(limit5h: 10, limit7d: 1000, extraPool: 30); // seed +30 -> 30

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
        {
            SubscriptionId = "sub-a",
            DeltaCredits = 50,
            Actor = "ops-admin",
            Reason = "manual-top-up",
        }); // balance 80

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        var consume = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 25, idempotencyKey: "k-recon"));
        Assert.Equal(UsageDecisionResult.Accepted, consume.Result);
        Assert.Equal(10, consume.CoveredBySubscriptionAllowance);
        Assert.Equal(15, consume.CoveredByExtraPool); // balance 65

        fixture.Clock.Advance(TimeSpan.FromHours(1));
        await fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
        {
            SubscriptionId = "sub-a",
            DeltaCredits = -10,
            Actor = "ops-admin",
            Reason = "manual-reduction",
        }); // balance 55

        var report = await fixture.Usage.ExportReconciliationReportAsync(start, start + TimeSpan.FromDays(1));
        var row = Assert.Single(report.Subscriptions);
        Assert.Equal("sub-a", row.SubscriptionId);
        Assert.Equal(0, row.ExtraPoolBeginningBalance); // the subscription was seeded inside the period
        Assert.Equal(80, row.ExtraPoolAddedCredits);    // seed 30 + top-up 50
        Assert.Equal(15, row.ExtraPoolConsumedCredits);
        Assert.Equal(-10, row.ExtraPoolAdjustedCredits);
        Assert.Equal(55, row.ExtraPoolEndingBalance);
        Assert.Equal(
            row.ExtraPoolBeginningBalance + row.ExtraPoolAddedCredits
                + row.ExtraPoolAdjustedCredits - row.ExtraPoolConsumedCredits,
            row.ExtraPoolEndingBalance);
        Assert.Equal(25, row.AcceptedCredits);
        Assert.Equal(10, row.CoveredBySubscriptionAllowanceCredits);
        Assert.Equal(15, row.CoveredByExtraPoolCredits);
        Assert.Equal(1, row.AcceptedRequestCount);

        // A later period with no pool movement: beginning == ending == current balance,
        // zero added / consumed / adjusted.
        fixture.Clock.SetUtcNow(start + TimeSpan.FromDays(3));
        var emptyPeriod = await fixture.Usage.ExportReconciliationReportAsync(
            start + TimeSpan.FromDays(2), start + TimeSpan.FromDays(3));
        var emptyRow = Assert.Single(emptyPeriod.Subscriptions);
        Assert.Equal("sub-a", emptyRow.SubscriptionId);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(55, status.ExtraPoolRemainingCredits);
        Assert.Equal(status.ExtraPoolRemainingCredits, emptyRow.ExtraPoolBeginningBalance);
        Assert.Equal(status.ExtraPoolRemainingCredits, emptyRow.ExtraPoolEndingBalance);
        Assert.Equal(0, emptyRow.ExtraPoolAddedCredits);
        Assert.Equal(0, emptyRow.ExtraPoolConsumedCredits);
        Assert.Equal(0, emptyRow.ExtraPoolAdjustedCredits);
        Assert.Equal(0, emptyRow.AcceptedCredits);
        Assert.Equal(0, emptyRow.AcceptedRequestCount);
    }
}
