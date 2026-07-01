using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;
using Xunit;

namespace AndrewDemo.AgentRateLimit.Core.Tests;

public sealed class SubscriptionCreditRunOutcomeTests
{
    private static readonly DateTimeOffset DefaultNow =
        DateTimeOffset.Parse("2026-07-01T23:01:23Z");

    [Fact]
    public async Task TC_RUN_001_R1_allowance_covers_all_actual_credits()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 1000, extraPoolRemaining: 0);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-001");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 50),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 50, extra: 0, absorbed: 0, fiveRemaining: 50, sevenRemaining: 950, extraRemaining: 0);
    }

    [Fact]
    public async Task TC_RUN_002_R2_5h_expired_then_lazy_renews_before_run()
    {
        var now = DateTimeOffset.Parse("2026-07-02T08:00:00Z");
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(now);
        await fixture.SeedActiveAccountAsync(
            fiveHourRemaining: 0,
            sevenDayRemaining: 1000,
            extraPoolRemaining: 0,
            fiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            fiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-02T04:01:23Z"),
            sevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            sevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T23:01:23Z"));

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-002");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 50),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        Assert.Equal(now.AddHours(5), admission.FiveHourWindowAfterDecision.NextResetTimeUtc);
        AssertAcceptedConsumption(consumed, allowance: 50, extra: 0, absorbed: 0, fiveRemaining: 50, sevenRemaining: 950, extraRemaining: 0);
    }

    [Fact]
    public async Task TC_RUN_003_R3_7d_expired_then_lazy_renews_before_run()
    {
        var now = DateTimeOffset.Parse("2026-07-08T08:00:00Z");
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(now);
        await fixture.SeedActiveAccountAsync(
            fiveHourRemaining: 100,
            sevenDayRemaining: 0,
            extraPoolRemaining: 0,
            fiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-08T07:00:00Z"),
            fiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            sevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            sevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-003");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 50),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        Assert.Equal(now.AddDays(7), admission.SevenDayWindowAfterDecision.NextResetTimeUtc);
        AssertAcceptedConsumption(consumed, allowance: 50, extra: 0, absorbed: 0, fiveRemaining: 50, sevenRemaining: 950, extraRemaining: 0);
    }

    [Fact]
    public async Task TC_RUN_004_R4_both_windows_expired_then_lazy_renew_before_run()
    {
        var now = DateTimeOffset.Parse("2026-07-08T08:00:00Z");
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(now);
        await fixture.SeedActiveAccountAsync(
            fiveHourRemaining: 0,
            sevenDayRemaining: 0,
            extraPoolRemaining: 0,
            fiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            fiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-02T04:01:23Z"),
            sevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            sevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-004");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 50),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        Assert.Equal(now.AddHours(5), admission.FiveHourWindowAfterDecision.NextResetTimeUtc);
        Assert.Equal(now.AddDays(7), admission.SevenDayWindowAfterDecision.NextResetTimeUtc);
        AssertAcceptedConsumption(consumed, allowance: 50, extra: 0, absorbed: 0, fiveRemaining: 50, sevenRemaining: 950, extraRemaining: 0);
    }

    [Fact]
    public async Task TC_RUN_005_R5_5h_quota_unavailable_and_no_extra_pool()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 0, sevenDayRemaining: 1000, extraPoolRemaining: 0);

        var admission = await fixture.Usage.DecideAsync(
            fixture.CreateAdmissionRequest("tc-run-005"),
            CancellationToken.None);

        AssertRejectedAdmission(admission, UsageRejectionReason.InsufficientCredits);
        Assert.Equal(0, await fixture.Store.CountConsumeRecordsAsync("sub-a"));
    }

    [Fact]
    public async Task TC_RUN_006_R6_7d_quota_unavailable_and_no_extra_pool()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 0, extraPoolRemaining: 0);

        var admission = await fixture.Usage.DecideAsync(
            fixture.CreateAdmissionRequest("tc-run-006"),
            CancellationToken.None);

        AssertRejectedAdmission(admission, UsageRejectionReason.InsufficientCredits);
        Assert.Equal(0, await fixture.Store.CountConsumeRecordsAsync("sub-a"));
    }

    [Fact]
    public async Task TC_RUN_007_R7_5h_quota_unavailable_and_extra_pool_needs_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 0, sevenDayRemaining: 0, extraPoolRemaining: 1000);

        var admission = await fixture.Usage.DecideAsync(
            fixture.CreateAdmissionRequest("tc-run-007"),
            CancellationToken.None);

        AssertRejectedAdmission(admission, UsageRejectionReason.ExtraPoolAuthorizationRequired);
        Assert.Equal(0, await fixture.Store.CountConsumeRecordsAsync("sub-a"));
    }

    [Fact]
    public async Task TC_RUN_008_R8_7d_quota_unavailable_and_extra_pool_needs_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 0, extraPoolRemaining: 1000);

        var admission = await fixture.Usage.DecideAsync(
            fixture.CreateAdmissionRequest("tc-run-008"),
            CancellationToken.None);

        AssertRejectedAdmission(admission, UsageRejectionReason.ExtraPoolAuthorizationRequired);
        Assert.Equal(0, await fixture.Store.CountConsumeRecordsAsync("sub-a"));
    }

    [Fact]
    public async Task TC_RUN_009_R9_5h_quota_unavailable_but_extra_pool_authorized()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 0, sevenDayRemaining: 1000, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-009", UsageExtraPoolAuthorization.Authorized);
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 20),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 0, extra: 20, absorbed: 0, fiveRemaining: 0, sevenRemaining: 980, extraRemaining: 980);
    }

    [Fact]
    public async Task TC_RUN_010_R10_7d_quota_unavailable_but_extra_pool_authorized()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 0, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-010", UsageExtraPoolAuthorization.Authorized);
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 20),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 0, extra: 20, absorbed: 0, fiveRemaining: 80, sevenRemaining: 0, extraRemaining: 980);
    }

    [Fact]
    public async Task TC_RUN_011_R11_unknown_actual_exceeds_5h_without_extra_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 1000, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-011");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 120),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 100, extra: 0, absorbed: 20, fiveRemaining: 0, sevenRemaining: 880, extraRemaining: 1000);
    }

    [Fact]
    public async Task TC_RUN_012_R12_unknown_actual_exceeds_7d_without_extra_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 10, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-012");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 20),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 10, extra: 0, absorbed: 10, fiveRemaining: 80, sevenRemaining: 0, extraRemaining: 1000);
    }

    [Fact]
    public async Task TC_RUN_013_R13_unknown_actual_exceeds_both_windows_without_extra_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 10, sevenDayRemaining: 10, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-013");
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 20),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 10, extra: 0, absorbed: 10, fiveRemaining: 0, sevenRemaining: 0, extraRemaining: 1000);
    }

    [Fact]
    public async Task TC_RUN_014_R14_actual_exceeds_5h_with_extra_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 1000, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-014", UsageExtraPoolAuthorization.Authorized);
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 120),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 100, extra: 20, absorbed: 0, fiveRemaining: 0, sevenRemaining: 880, extraRemaining: 980);
    }

    [Fact]
    public async Task TC_RUN_015_R15_actual_exceeds_7d_with_extra_authorization()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 10, extraPoolRemaining: 1000);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-015", UsageExtraPoolAuthorization.Authorized);
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 20),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 10, extra: 10, absorbed: 0, fiveRemaining: 80, sevenRemaining: 0, extraRemaining: 990);
    }

    [Fact]
    public async Task TC_RUN_016_R16_actual_exceeds_allowance_and_authorized_extra_pool_is_unavailable()
    {
        await using var fixture = await SubscriptionCreditUsageFixture.CreateAsync(DefaultNow);
        await fixture.SeedActiveAccountAsync(fiveHourRemaining: 100, sevenDayRemaining: 1000, extraPoolRemaining: 0);

        var admissionRequest = fixture.CreateAdmissionRequest("tc-run-016", UsageExtraPoolAuthorization.Authorized);
        var admission = await fixture.Usage.DecideAsync(admissionRequest, CancellationToken.None);
        var consumed = await fixture.Usage.ConsumeAsync(
            fixture.CreateSettlementRequest(admissionRequest, actualCredits: 120),
            CancellationToken.None);

        AssertAdmissionAccepted(admission);
        AssertAcceptedConsumption(consumed, allowance: 100, extra: 0, absorbed: 20, fiveRemaining: 0, sevenRemaining: 880, extraRemaining: 0);
    }

    private static void AssertAdmissionAccepted(UsageCreditDecision decision)
    {
        Assert.Equal(UsageDecisionMode.DecideOnly, decision.Mode);
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Null(decision.RejectionReason);
        Assert.Null(decision.AuditReference);
    }

    private static void AssertRejectedAdmission(
        UsageCreditDecision decision,
        UsageRejectionReason expectedReason)
    {
        Assert.Equal(UsageDecisionMode.DecideOnly, decision.Mode);
        Assert.Equal(UsageDecisionResult.Rejected, decision.Result);
        Assert.Equal(expectedReason, decision.RejectionReason);
        Assert.Equal(CreditAmount.Zero, decision.CreditsCoveredBySubscriptionAllowance);
        Assert.Equal(CreditAmount.Zero, decision.CreditsCoveredByExtraPool);
        Assert.Equal(CreditAmount.Zero, decision.CreditsAbsorbedBySystem);
        Assert.Null(decision.AuditReference);
    }

    private static void AssertAcceptedConsumption(
        UsageCreditDecision decision,
        int allowance,
        int extra,
        int absorbed,
        int fiveRemaining,
        int sevenRemaining,
        int extraRemaining)
    {
        Assert.Equal(UsageDecisionMode.Consume, decision.Mode);
        Assert.Equal(UsageDecisionResult.Accepted, decision.Result);
        Assert.Equal(new CreditAmount(allowance), decision.CreditsCoveredBySubscriptionAllowance);
        Assert.Equal(new CreditAmount(extra), decision.CreditsCoveredByExtraPool);
        Assert.Equal(new CreditAmount(absorbed), decision.CreditsAbsorbedBySystem);
        Assert.Equal(new CreditAmount(fiveRemaining), decision.FiveHourWindowAfterDecision.Remaining);
        Assert.Equal(new CreditAmount(sevenRemaining), decision.SevenDayWindowAfterDecision.Remaining);
        Assert.Equal(new CreditAmount(extraRemaining), decision.ExtraPoolRemainingAfterDecision);
        Assert.NotNull(decision.AuditReference);
    }
}
