using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

namespace AndrewDemo.AgentRateLimit.Core.Tests.SubscriptionCreditRateLimitV1;

/// <summary>
/// Regression tests for defects surfaced by the adversarial spec audit. Each test
/// names the behavior it pins down.
/// </summary>
public class AuditRegressionTests
{
    private static readonly DateTimeOffset Start = SubscriptionCreditServiceFixture.DefaultStart;

    /// <summary>
    /// Non-monotonic clock: unlike ManualTimeProvider this one can step backwards,
    /// modeling wall-clock regressions (NTP) in production.
    /// </summary>
    private sealed class SkewableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _unixMs = start.ToUnixTimeMilliseconds();

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _unixMs));

        public void SetUnixMs(long unixMs) => Interlocked.Exchange(ref _unixMs, unixMs);
    }

    /// <summary>
    /// Advances one millisecond on every read, so concurrent transactions observe
    /// distinct timestamps — the regime in which the pre-fix time-capture race lived.
    /// </summary>
    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _unixMs = start.ToUnixTimeMilliseconds() - 1;

        public override DateTimeOffset GetUtcNow()
            => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _unixMs));
    }

    private static string TempDbPath(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "agent-rate-limit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "subscription-credit.db");
    }

    private static void Cleanup(string directory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task REG_BackwardsClockCannotHideCommittedUsage()
    {
        // Audit finding (critical): decision time must be ledger-clamped so committed
        // usage can never become invisible to the window query when the wall clock
        // steps backwards between two decisions.
        var dbPath = TempDbPath(out var dir);
        try
        {
            var clock = new SkewableTimeProvider(Start);
            var service = new SqliteSubscriptionCreditService(dbPath, clock);
            await ((ISubscriptionCreditAdministrationService)service).CreateSubscriptionAsync(new SubscriptionDefinition
            {
                UserId = "user-a",
                SubscriptionId = "sub-a",
                Limit5hCredits = 10,
                Limit7dCredits = 10,
                InitialExtraPoolCredits = 0,
            });

            var usage = (ISubscriptionCreditUsageService)service;
            var first = await usage.ConsumeAsync(new UsageRequest
            {
                UserId = "user-a",
                SubscriptionId = "sub-a",
                RequestedCredits = 10,
                IdempotencyKey = "k-1",
            });
            Assert.Equal(UsageDecisionResult.Accepted, first.Result);

            // The clock regresses 5 seconds. An unclamped implementation would compute
            // the window as (T-5s-5h, T-5s], exclude the just-committed usage at T,
            // and over-accept another 10 credits.
            clock.SetUnixMs(Start.ToUnixTimeMilliseconds() - 5000);

            var second = await usage.ConsumeAsync(new UsageRequest
            {
                UserId = "user-a",
                SubscriptionId = "sub-a",
                RequestedCredits = 10,
                IdempotencyKey = "k-2",
            });
            Assert.Equal(UsageDecisionResult.Rejected, second.Result);
            Assert.Equal(UsageDecisionReasons.InsufficientCredits, second.Reason);

            // Status under the regressed clock still shows the committed usage.
            var status = await usage.GetUsageStatusAsync("sub-a");
            Assert.NotNull(status);
            Assert.Equal(10, status.Window5h.UsedCredits);
            Assert.Equal(0, status.Window5h.RemainingCredits);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task REG_ConcurrentRequestsWithAdvancingClockNeverOveraccept()
    {
        // Audit finding (critical): with per-call advancing timestamps (the production
        // regime), concurrent consumes must still never over-accept. Pre-fix, a writer
        // blocked on the SQLite lock decided with a stale 'now' and could miss the
        // winner's committed usage.
        var dbPath = TempDbPath(out var dir);
        try
        {
            var clock = new SteppingTimeProvider(Start);
            var service = new SqliteSubscriptionCreditService(dbPath, clock);
            await ((ISubscriptionCreditAdministrationService)service).CreateSubscriptionAsync(new SubscriptionDefinition
            {
                UserId = "user-a",
                SubscriptionId = "sub-a",
                Limit5hCredits = 10,
                Limit7dCredits = 10,
                InitialExtraPoolCredits = 0,
            });

            var usage = (ISubscriptionCreditUsageService)service;
            var gate = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, 20)
                .Select(i => Task.Run(async () =>
                {
                    await gate.Task;
                    return await usage.ConsumeAsync(new UsageRequest
                    {
                        UserId = "user-a",
                        SubscriptionId = "sub-a",
                        RequestedCredits = 1,
                        IdempotencyKey = $"k-step-{i:d2}",
                    });
                }))
                .ToArray();
            gate.SetResult();
            var decisions = await Task.WhenAll(tasks);

            Assert.Equal(10, decisions.Count(d => d.Result == UsageDecisionResult.Accepted));
            Assert.Equal(10, decisions.Count(d => d.Result == UsageDecisionResult.Rejected));

            var status = await usage.GetUsageStatusAsync("sub-a");
            Assert.NotNull(status);
            Assert.Equal(10, status.Window5h.UsedCredits);
            Assert.Equal(0, status.Window5h.RemainingCredits);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task REG_AllowanceIsLimitedBy7dRemainingWithRealUsageHistory()
    {
        // Audit finding: no test exercised min(remaining5h, remaining7d) with real
        // in-window usage shaping the 7d side. An implementation using limit7d
        // instead of remaining7d would grant allowance 20 here instead of 5.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 50, extraPool: 50);

        var first = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 45));
        Assert.Equal(UsageDecisionResult.Accepted, first.Result);
        Assert.Equal(45, first.CoveredBySubscriptionAllowance);
        Assert.Equal(0, first.CoveredByExtraPool);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        // remaining5h = 55, remaining7d = 5 => allowance min(55, 5) = 5, extra 15.
        var second = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));
        Assert.Equal(UsageDecisionResult.Accepted, second.Result);
        Assert.Equal(5, second.CoveredBySubscriptionAllowance);
        Assert.Equal(15, second.CoveredByExtraPool);
        Assert.Equal(35, second.RemainingExtraPoolCreditsAfterDecision);
        Assert.Equal(35, second.Remaining5hCreditsAfterDecision);
        Assert.Equal(0, second.Remaining7dCreditsAfterDecision);
    }

    [Fact]
    public async Task REG_PreviewComputesRemainingsFromRealUsageHistory()
    {
        // Audit finding: preview remaining math was only tested on usage-free
        // subscriptions, where remaining == limit.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 0);

        var consumed = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 30));
        Assert.Equal(UsageDecisionResult.Accepted, consumed.Result);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        var preview = await fixture.Usage.PreviewAsync(fixture.Request(credits: 20));
        Assert.Equal(UsageDecisionResult.Accepted, preview.Result);
        Assert.Equal(50, preview.Remaining5hCreditsAfterDecision);
        Assert.Equal(950, preview.Remaining7dCreditsAfterDecision);
        Assert.Null(preview.AuditReference);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(30, status.Window5h.UsedCredits);
    }

    [Fact]
    public async Task REG_DisabledRejectionReportsRemainingsToOwner()
    {
        // Audit finding: spec 3.1 lists the remaining fields on every decision; for a
        // disabled-subscription rejection the caller owns the subscription and the
        // values are computable, so they must be present.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(limit5h: 100, limit7d: 1000, extraPool: 50);

        var consumed = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 20));
        Assert.Equal(UsageDecisionResult.Accepted, consumed.Result);

        await fixture.Admin.SetSubscriptionEnabledAsync("sub-a", enabled: false, actor: "ops-admin");

        var rejected = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 10, idempotencyKey: "k-disabled"));
        Assert.Equal(UsageDecisionResult.Rejected, rejected.Result);
        Assert.Equal(UsageDecisionReasons.SubscriptionDisabled, rejected.Reason);
        Assert.Equal(80, rejected.Remaining5hCreditsAfterDecision);
        Assert.Equal(980, rejected.Remaining7dCreditsAfterDecision);
        Assert.Equal(50, rejected.RemainingExtraPoolCreditsAfterDecision);

        // The owner-verified rejection binds the key: a same-payload resend replays it.
        var replay = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 10, idempotencyKey: "k-disabled"));
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(rejected.AuditReference, replay.AuditReference);
    }

    [Fact]
    public async Task REG_MismatchRejectionDoesNotBindKeyOrDiscloseBalances()
    {
        // Audit finding: a non-owner's rejected request must not poison the owner's
        // idempotency key space and must not reveal the subscription's balances.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-b", subscriptionId: "sub-b", limit5h: 100, limit7d: 1000, extraPool: 50);

        var attack = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-b", credits: 10, idempotencyKey: "k9"));
        Assert.Equal(UsageDecisionResult.Rejected, attack.Result);
        Assert.Equal(UsageDecisionReasons.UserSubscriptionMismatch, attack.Reason);
        Assert.Null(attack.Remaining5hCreditsAfterDecision);
        Assert.Null(attack.Remaining7dCreditsAfterDecision);
        Assert.Null(attack.RemainingExtraPoolCreditsAfterDecision);

        // The owner can use the same key unaffected.
        var owner = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-b", subscriptionId: "sub-b", credits: 10, idempotencyKey: "k9"));
        Assert.Equal(UsageDecisionResult.Accepted, owner.Result);
        Assert.False(owner.IsIdempotentReplay);

        // The attack attempt still left a queryable audit record (spec 4.5).
        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-b" });
        Assert.Contains(audit, r => r.DecisionResult == UsageDecisionResult.Rejected
                                    && r.Reason == UsageDecisionReasons.UserSubscriptionMismatch
                                    && r.UserId == "user-a");
    }

    [Fact]
    public async Task REG_ConflictAsNonOwnerDisclosesNoBalances()
    {
        // Audit finding: a conflict on another user's bound key must not reveal that
        // subscription's remaining credits; the owner's own conflict still does.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync(userId: "user-b", subscriptionId: "sub-b", limit5h: 100, limit7d: 1000, extraPool: 50);

        var bound = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-b", subscriptionId: "sub-b", credits: 20, idempotencyKey: "k-owner"));
        Assert.Equal(UsageDecisionResult.Accepted, bound.Result);

        var intruder = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-a", subscriptionId: "sub-b", credits: 20, idempotencyKey: "k-owner"));
        Assert.Equal(UsageDecisionResult.Conflict, intruder.Result);
        Assert.Null(intruder.Remaining5hCreditsAfterDecision);
        Assert.Null(intruder.Remaining7dCreditsAfterDecision);
        Assert.Null(intruder.RemainingExtraPoolCreditsAfterDecision);

        var ownerConflict = await fixture.Usage.ConsumeAsync(
            fixture.Request(userId: "user-b", subscriptionId: "sub-b", credits: 25, idempotencyKey: "k-owner"));
        Assert.Equal(UsageDecisionResult.Conflict, ownerConflict.Result);
        Assert.Equal(80, ownerConflict.Remaining5hCreditsAfterDecision);
        Assert.Equal(980, ownerConflict.Remaining7dCreditsAfterDecision);
        Assert.Equal(50, ownerConflict.RemainingExtraPoolCreditsAfterDecision);
    }

    [Fact]
    public async Task REG_CreditBoundsAreEnforcedWithoutOverflow()
    {
        // Audit finding: unchecked/checked arithmetic could throw after the accept
        // decision under absurd limits. Amounts are now bounded by
        // SubscriptionCreditBounds.MaxCreditAmount and accepted paths saturate.
        using var fixture = new SubscriptionCreditServiceFixture();
        var max = SubscriptionCreditBounds.MaxCreditAmount;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Admin.CreateSubscriptionAsync(new SubscriptionDefinition
            {
                UserId = "user-a",
                SubscriptionId = "sub-huge",
                Limit5hCredits = max + 1,
                Limit7dCredits = max,
            }));

        await fixture.CreateSubscriptionAsync(limit5h: max, limit7d: max, extraPool: 10);

        var first = await fixture.Usage.ConsumeAsync(fixture.Request(credits: max, idempotencyKey: "k-max"));
        Assert.Equal(UsageDecisionResult.Accepted, first.Result);
        Assert.Equal(0, first.Remaining5hCreditsAfterDecision);

        // The follow-up dips into the extra pool; no arithmetic overflow anywhere.
        var second = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 10, idempotencyKey: "k-extra"));
        Assert.Equal(UsageDecisionResult.Accepted, second.Result);
        Assert.Equal(10, second.CoveredByExtraPool);
        Assert.Equal(0, second.RemainingExtraPoolCreditsAfterDecision);

        var status = await fixture.Usage.GetUsageStatusAsync("sub-a");
        Assert.NotNull(status);
        Assert.Equal(max + 10, status.Window5h.UsedCredits);
        Assert.Equal(0, status.Window5h.RemainingCredits);

        // Requests beyond the bound are invalid with the implementation reason.
        var tooBig = await fixture.Usage.ConsumeAsync(fixture.Request(credits: (decimal)max + 1));
        Assert.Equal(UsageDecisionResult.Invalid, tooBig.Result);
        Assert.Equal(UsageDecisionReasons.CreditsOutOfRange, tooBig.Reason);

        // Adjustment deltas beyond the bound are refused outright.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Admin.AdjustExtraPoolAsync(new ExtraPoolAdjustment
            {
                SubscriptionId = "sub-a",
                DeltaCredits = max + 1,
                Actor = "ops-admin",
                Reason = "too-big",
            }));
    }

    [Fact]
    public async Task REG_UsageDecisionAuditRecordsCarryTheCallerAsActor()
    {
        // Audit finding: spec 3.4 requires actor-or-source on every audit record;
        // usage decisions now record the requesting user.
        using var fixture = new SubscriptionCreditServiceFixture();
        await fixture.CreateSubscriptionAsync();

        var decision = await fixture.Usage.ConsumeAsync(fixture.Request(credits: 10));
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);

        var audit = await fixture.Usage.QueryAuditTrailAsync(new AuditTrailQuery { SubscriptionId = "sub-a" });
        var record = Assert.Single(audit, r => r.AuditId == decision.AuditReference);
        Assert.Equal("user-a", record.Actor);
    }
}
