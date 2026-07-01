using AndrewDemo.AgentRateLimit.Abstract;
using AndrewDemo.AgentRateLimit.Core;
using Xunit;

namespace AndrewDemo.AgentRateLimit.Core.Tests;

public sealed class SubscriptionUsageServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Consume_AcceptsPositiveIntegerCredits()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);

        var decision = service.ConsumeUsage(
            new UsageRequest("user-a", "sub-a", 10, "k-001", "corr-001"),
            T0.AddMinutes(1));

        Assert.Equal(DecisionResults.Accepted, decision.Result);
        Assert.Equal(10, decision.RequestedCredits);
        Assert.Equal(90, decision.RemainingFiveHourCreditsAfterDecision);
        Assert.Equal(990, decision.RemainingSevenDayCreditsAfterDecision);
    }

    [Theory]
    [InlineData(1.5, InvalidReasons.CreditsNotInteger)]
    [InlineData(0, InvalidReasons.CreditsNotPositive)]
    [InlineData(-1, InvalidReasons.CreditsNotPositive)]
    public void Consume_RejectsInvalidCreditAmounts(decimal credits, string expectedReason)
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);

        var before = service.GetUsageStatus("user-a", "sub-a", T0);
        var decision = service.ConsumeUsage(
            new UsageRequest("user-a", "sub-a", credits, "k-invalid", "corr-invalid"),
            T0.AddMinutes(1));
        var after = service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(1));

        Assert.Equal(DecisionResults.Invalid, decision.Result);
        Assert.Equal(expectedReason, decision.Reason);
        Assert.Equal(before.FiveHourWindowRemainingCredits, after.FiveHourWindowRemainingCredits);
        Assert.Equal(before.SevenDayWindowRemainingCredits, after.SevenDayWindowRemainingCredits);
    }

    [Theory]
    [InlineData(null, "sub-a", "k-001", InvalidReasons.MissingUserId)]
    [InlineData("user-a", null, "k-001", InvalidReasons.MissingSubscriptionId)]
    [InlineData("user-a", "sub-a", null, InvalidReasons.MissingIdempotencyKey)]
    public void Consume_RejectsMissingRequiredFields(
        string? userId,
        string? subscriptionId,
        string? idempotencyKey,
        string expectedReason)
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);

        var decision = service.ConsumeUsage(
            new UsageRequest(userId, subscriptionId, 10, idempotencyKey, "corr-invalid"),
            T0.AddMinutes(1));

        Assert.Equal(DecisionResults.Invalid, decision.Result);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Fact]
    public void Consume_UsesWindowAllowanceBeforeExtraPool()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 500, 50, time: T0);

        var decision = service.ConsumeUsage(
            new UsageRequest("user-a", "sub-a", 20, "k-001", "corr-001"),
            T0.AddMinutes(1));

        Assert.Equal(DecisionResults.Accepted, decision.Result);
        Assert.Equal(20, decision.CreditsCoveredBySubscriptionWindowAllowance);
        Assert.Equal(0, decision.CreditsCoveredByExtraPool);
        Assert.Equal(80, decision.RemainingFiveHourCreditsAfterDecision);
        Assert.Equal(480, decision.RemainingSevenDayCreditsAfterDecision);
        Assert.Equal(50, decision.RemainingExtraPoolCreditsAfterDecision);
    }

    [Theory]
    [InlineData(5, 500, 15, 35)]
    [InlineData(100, 8, 12, 38)]
    [InlineData(12, 7, 13, 37)]
    public void Consume_UsesExtraPoolWhenEitherWindowIsShort(
        long fiveHourRemaining,
        long sevenDayRemaining,
        long expectedExtraCredits,
        long expectedExtraRemaining)
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        PrepareSubscriptionWithRemaining(
            service,
            fiveHourRemaining,
            sevenDayRemaining,
            extraPoolRemaining: 50);

        var decision = service.ConsumeUsage(
            new UsageRequest("user-a", "sub-a", 20, "k-extra", "corr-extra"),
            T0);

        Assert.Equal(DecisionResults.Accepted, decision.Result);
        Assert.Equal(expectedExtraCredits, decision.CreditsCoveredByExtraPool);
        Assert.Equal(expectedExtraRemaining, decision.RemainingExtraPoolCreditsAfterDecision);
    }

    [Fact]
    public void Consume_RejectsWhenAllowanceAndExtraPoolAreInsufficient()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 100, 10, time: T0);
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 95, "seed", "seed"), T0.AddMinutes(1));

        var decision = service.ConsumeUsage(
            new UsageRequest("user-a", "sub-a", 20, "k-reject", "corr-reject"),
            T0.AddMinutes(2));
        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(2));

        Assert.Equal(DecisionResults.Rejected, decision.Result);
        Assert.Equal(RejectionReasons.InsufficientCredits, decision.Reason);
        Assert.Equal(5, status.FiveHourWindowRemainingCredits);
        Assert.Equal(5, status.SevenDayWindowRemainingCredits);
        Assert.Equal(10, status.ExtraPoolRemainingCredits);
    }

    [Fact]
    public void Status_UsesRollingWindowBoundaries()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 30, "k-001", "corr"), T0);

        var beforeFiveHourExpiry = service.GetUsageStatus("user-a", "sub-a", T0.AddHours(5).AddSeconds(-1));
        var atFiveHourExpiry = service.GetUsageStatus("user-a", "sub-a", T0.AddHours(5));
        var beforeSevenDayExpiry = service.GetUsageStatus("user-a", "sub-a", T0.AddDays(7).AddSeconds(-1));
        var atSevenDayExpiry = service.GetUsageStatus("user-a", "sub-a", T0.AddDays(7));

        Assert.Equal(30, beforeFiveHourExpiry.FiveHourWindowUsedCredits);
        Assert.Equal(0, atFiveHourExpiry.FiveHourWindowUsedCredits);
        Assert.Equal(30, beforeSevenDayExpiry.SevenDayWindowUsedCredits);
        Assert.Equal(0, atSevenDayExpiry.SevenDayWindowUsedCredits);
    }

    [Fact]
    public void ExtraPool_DoesNotResetWithRollingWindow()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 10, 1000, 50, time: T0);
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 25, "k-extra", "corr"), T0.AddMinutes(1));

        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddHours(6));

        Assert.Equal(35, status.ExtraPoolRemainingCredits);
        Assert.Equal(0, status.FiveHourWindowUsedCredits);
    }

    [Fact]
    public void ExtraPool_CanBeToppedUpAndAudited()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 10, 1000, 0, time: T0);

        var audit = service.AddExtraPoolCredits(
            "user-a",
            "sub-a",
            100,
            "ops",
            "manual-top-up",
            T0.AddMinutes(1));
        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(1));

        Assert.Equal(100, status.ExtraPoolRemainingCredits);
        Assert.Equal("ops", audit.Actor);
        Assert.Equal("manual-top-up", audit.Reason);
        Assert.Equal(100, audit.ChangedCredits);
        Assert.Equal(100, audit.ResultingExtraPoolCredits);
    }

    [Fact]
    public void Preview_DoesNotChangeUsageStatus()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);

        var preview = service.PreviewUsage(
            new UsageRequest("user-a", "sub-a", 20, "preview", "corr"),
            T0.AddMinutes(1));
        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(1));

        Assert.Equal(DecisionResults.Accepted, preview.Result);
        Assert.Equal(80, preview.RemainingFiveHourCreditsAfterDecision);
        Assert.Equal(980, preview.RemainingSevenDayCreditsAfterDecision);
        Assert.Equal(100, status.FiveHourWindowRemainingCredits);
        Assert.Equal(1000, status.SevenDayWindowRemainingCredits);
    }

    [Fact]
    public void Idempotency_ReplaysSameDecisionAndDetectsPayloadMismatch()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);

        var first = service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 20, "k-001", "corr-1"), T0.AddMinutes(1));
        var replay = service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 20, "k-001", "corr-2"), T0.AddMinutes(2));
        var conflict = service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 25, "k-001", "corr-3"), T0.AddMinutes(3));
        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(3));

        Assert.Equal(DecisionResults.Accepted, first.Result);
        Assert.Equal(first, replay);
        Assert.Equal(DecisionResults.Conflict, conflict.Result);
        Assert.Equal(ConflictReasons.IdempotencyKeyPayloadMismatch, conflict.Reason);
        Assert.Equal(20, status.FiveHourWindowUsedCredits);
    }

    [Fact]
    public void Usage_IsIsolatedAcrossUsersAndSubscriptions()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 500, 0, time: T0);
        service.CreateOrReplaceSubscription("user-b", "sub-b", 100, 500, 0, time: T0);
        service.CreateOrReplaceSubscription("user-a", "sub-a2", 100, 500, 0, time: T0);

        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 80, "k-a", "corr"), T0.AddMinutes(1));
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a2", 100, "k-a2", "corr"), T0.AddMinutes(1));

        Assert.Equal(20, service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(1)).FiveHourWindowRemainingCredits);
        Assert.Equal(100, service.GetUsageStatus("user-b", "sub-b", T0.AddMinutes(1)).FiveHourWindowRemainingCredits);
        Assert.Equal(400, service.GetUsageStatus("user-a", "sub-a2", T0.AddMinutes(1)).SevenDayWindowRemainingCredits);
    }

    [Fact]
    public void Consume_RejectsMissingMismatchedOrDisabledSubscriptions()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-b", "sub-b", 100, 500, 0, time: T0);
        service.CreateOrReplaceSubscription("user-a", "sub-disabled", 100, 500, 0, disabled: true, time: T0);

        var missing = service.ConsumeUsage(new UsageRequest("user-a", "sub-missing", 10, "missing", "corr"), T0);
        var mismatch = service.ConsumeUsage(new UsageRequest("user-a", "sub-b", 10, "mismatch", "corr"), T0);
        var disabled = service.ConsumeUsage(new UsageRequest("user-a", "sub-disabled", 10, "disabled", "corr"), T0);

        Assert.Equal(RejectionReasons.SubscriptionNotFound, missing.Reason);
        Assert.Equal(RejectionReasons.UserSubscriptionMismatch, mismatch.Reason);
        Assert.Equal(RejectionReasons.SubscriptionDisabled, disabled.Reason);
    }

    [Fact]
    public async Task Consume_SerializesConcurrentRequestsForSameSubscription()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 10, 10, 0, time: T0);

        var task1 = Task.Run(() => fixture.CreateService().ConsumeUsage(
            new UsageRequest("user-a", "sub-a", 10, "k-1", "corr-1"),
            T0.AddMinutes(1)));
        var task2 = Task.Run(() => fixture.CreateService().ConsumeUsage(
            new UsageRequest("user-a", "sub-a", 10, "k-2", "corr-2"),
            T0.AddMinutes(1)));

        var decisions = await Task.WhenAll(task1, task2);

        Assert.Single(decisions, decision => decision.Result == DecisionResults.Accepted);
        Assert.Single(decisions, decision => decision.Result == DecisionResults.Rejected);
        Assert.Equal(10, service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(1)).FiveHourWindowUsedCredits);
    }

    [Fact]
    public void Decisions_PersistAcrossServiceRestart()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 0, time: T0);
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 20, "accepted", "corr"), T0.AddMinutes(1));
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 2000, "rejected", "corr"), T0.AddMinutes(2));

        var restarted = fixture.CreateService();
        var status = restarted.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(3));
        var audit = restarted.QueryAuditTrail("user-a", "sub-a");

        Assert.Equal(20, status.FiveHourWindowUsedCredits);
        Assert.Contains(audit, record => record.DecisionResult == DecisionResults.Accepted);
        Assert.Contains(audit, record => record.DecisionResult == DecisionResults.Rejected);
    }

    [Fact]
    public void AuditAndReconciliation_ReconstructCreditChanges()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 50, time: T0);

        var reportStart = T0.AddMinutes(1);
        service.AddExtraPoolCredits("user-a", "sub-a", 100, "ops", "manual-top-up", reportStart.AddMinutes(1));
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 120, "accepted", "corr"), reportStart.AddMinutes(2));
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 2000, "rejected", "corr"), reportStart.AddMinutes(3));
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 125, "accepted", "corr-conflict"), reportStart.AddMinutes(4));
        service.RecordManualCorrection("user-a", "sub-a", 5, "ops", "rounding-fix", reportStart.AddMinutes(5));

        var report = service.ExportReconciliationReport("sub-a", reportStart, reportStart.AddMinutes(10));
        var audit = service.QueryAuditTrail("user-a", "sub-a", reportStart, reportStart.AddMinutes(10));

        Assert.Contains(audit, record => record.DecisionResult == DecisionResults.Accepted);
        Assert.Contains(audit, record => record.DecisionResult == DecisionResults.Rejected);
        Assert.Contains(audit, record => record.RecordType == "manual-correction");
        Assert.Equal(120, report.AcceptedCreditsTotal);
        Assert.Equal(2000, report.RejectedCreditsTotal);
        Assert.Equal(100, report.SubscriptionAllowanceCoveredCreditsTotal);
        Assert.Equal(20, report.ExtraPoolCoveredCreditsTotal);
        Assert.Equal(50, report.ExtraPoolBeginningBalance);
        Assert.Equal(100, report.ExtraPoolAddedCredits);
        Assert.Equal(20, report.ExtraPoolConsumedCredits);
        Assert.Equal(5, report.ExtraPoolAdjustedCredits);
        Assert.Equal(135, report.ExtraPoolEndingBalance);
        Assert.Equal(1, report.ConflictCount);
        Assert.Equal(1, report.ManualCorrectionCount);
    }

    [Fact]
    public void Status_IncludesWindowLimitsRemainingAndNextResetTime()
    {
        using var fixture = TestStore.Create();
        var service = fixture.CreateService();
        service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 1000, 10, time: T0);
        service.ConsumeUsage(new UsageRequest("user-a", "sub-a", 20, "k-001", "corr"), T0.AddMinutes(1));

        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddMinutes(2));

        Assert.Equal(100, status.FiveHourWindowLimit);
        Assert.Equal(20, status.FiveHourWindowUsedCredits);
        Assert.Equal(80, status.FiveHourWindowRemainingCredits);
        Assert.Equal(T0.AddHours(5).AddMinutes(1), status.FiveHourNextResetTime);
        Assert.Equal(1000, status.SevenDayWindowLimit);
        Assert.Equal(20, status.SevenDayWindowUsedCredits);
        Assert.Equal(980, status.SevenDayWindowRemainingCredits);
        Assert.Equal(T0.AddDays(7).AddMinutes(1), status.SevenDayNextResetTime);
        Assert.Equal(10, status.ExtraPoolRemainingCredits);
    }

    private static void PrepareSubscriptionWithRemaining(
        SubscriptionUsageService service,
        long fiveHourRemaining,
        long sevenDayRemaining,
        long extraPoolRemaining)
    {
        const long fiveHourLimit = 100;
        const long sevenDayLimit = 1000;
        service.CreateOrReplaceSubscription("user-a", "sub-a", fiveHourLimit, sevenDayLimit, 5000, time: T0.AddDays(-1));

        var insideFiveHourUsage = fiveHourLimit - fiveHourRemaining;
        var totalSevenDayUsage = sevenDayLimit - sevenDayRemaining;
        var outsideFiveHourUsage = totalSevenDayUsage - insideFiveHourUsage;

        if (insideFiveHourUsage < 0 || outsideFiveHourUsage < 0)
        {
            throw new InvalidOperationException("Requested remaining credits are not reachable for this test helper.");
        }

        if (outsideFiveHourUsage > 0)
        {
            service.ConsumeUsage(
                new UsageRequest("user-a", "sub-a", outsideFiveHourUsage, "seed-7d", "seed"),
                T0.AddHours(-6));
        }

        if (insideFiveHourUsage > 0)
        {
            service.ConsumeUsage(
                new UsageRequest("user-a", "sub-a", insideFiveHourUsage, "seed-5h", "seed"),
                T0.AddMinutes(-1));
        }

        var status = service.GetUsageStatus("user-a", "sub-a", T0.AddSeconds(-1));
        var delta = extraPoolRemaining - status.ExtraPoolRemainingCredits;
        if (delta != 0)
        {
            service.RecordManualCorrection(
                "user-a",
                "sub-a",
                delta,
                "test",
                "set-extra-pool-for-scenario",
                T0.AddSeconds(-1));
        }
    }

    private sealed class TestStore : IDisposable
    {
        private readonly string _databasePath;

        private TestStore(string databasePath)
        {
            _databasePath = databasePath;
        }

        public static TestStore Create()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"andrewdemo-agent-rate-limit-{Guid.NewGuid():N}.db");
            return new TestStore(path);
        }

        public SubscriptionUsageService CreateService()
        {
            return new SubscriptionUsageService(_databasePath);
        }

        public void Dispose()
        {
            TryDelete(_databasePath);
            TryDelete(_databasePath + "-wal");
            TryDelete(_databasePath + "-shm");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
