using System.Collections;
using System.Net;
using Xunit;

namespace AndrewDemo.AgentRateLimit.Api.Tests;

public sealed class SubscriptionCreditApiRunOutcomeTests
{
    [Theory]
    [ClassData(typeof(RunOutcomeCases))]
    public async Task End_to_end_run_outcome_table_cases_are_observable_through_http_api(RunOutcomeCase testCase)
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(testCase.Now);
        await server.SeedSubscriptionAsync(
            fiveHourRemaining: testCase.FiveHourRemaining,
            sevenDayRemaining: testCase.SevenDayRemaining,
            extraPoolRemaining: testCase.ExtraPoolRemaining,
            fiveHourOpenedUtc: testCase.FiveHourOpenedUtc,
            fiveHourExpiresUtc: testCase.FiveHourExpiresUtc,
            sevenDayOpenedUtc: testCase.SevenDayOpenedUtc,
            sevenDayExpiresUtc: testCase.SevenDayExpiresUtc);

        using var admission = await server.PostDecideAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson(
                testCase.IdempotencyKey,
                extraPoolAuthorization: testCase.ExtraPoolAuthorization));

        Assert.Equal(HttpStatusCode.OK, admission.StatusCode);
        Assert.Equal("decide-only", admission.String("mode"));
        Assert.Equal(testCase.ExpectedAdmissionResult, admission.String("result"));
        Assert.Equal(testCase.ExpectedAdmissionRejectionReason, admission.String("rejectionReason"));

        if (testCase.ExpectedAdmissionResult == "rejected")
        {
            return;
        }

        using var consumed = await server.PostConsumeAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson(
                testCase.IdempotencyKey,
                requestedCredits: testCase.ActualCredits,
                creditAmountMode: "exact-credits",
                extraPoolAuthorization: testCase.ExtraPoolAuthorization));

        Assert.Equal(HttpStatusCode.OK, consumed.StatusCode);
        Assert.Equal("consume", consumed.String("mode"));
        Assert.Equal("accepted", consumed.String("result"));
        Assert.Equal(testCase.ExpectedAllowance, consumed.Int32("creditsCoveredBySubscriptionAllowance"));
        Assert.Equal(testCase.ExpectedExtra, consumed.Int32("creditsCoveredByExtraPool"));
        Assert.Equal(testCase.ExpectedAbsorbed, consumed.Int32("creditsAbsorbedBySystem"));
        Assert.Equal(testCase.ExpectedFiveHourRemaining, consumed.WindowRemaining("fiveHourWindowAfterDecision"));
        Assert.Equal(testCase.ExpectedSevenDayRemaining, consumed.WindowRemaining("sevenDayWindowAfterDecision"));
        Assert.Equal(testCase.ExpectedExtraRemaining, consumed.Int32("extraPoolRemainingAfterDecision"));

        if (testCase.ExpectedFiveHourResetUtc is not null)
        {
            Assert.Equal(
                testCase.ExpectedFiveHourResetUtc.Value,
                DateTimeOffset.Parse(consumed.WindowNextReset("fiveHourWindowAfterDecision")!));
        }

        if (testCase.ExpectedSevenDayResetUtc is not null)
        {
            Assert.Equal(
                testCase.ExpectedSevenDayResetUtc.Value,
                DateTimeOffset.Parse(consumed.WindowNextReset("sevenDayWindowAfterDecision")!));
        }
    }
}

public sealed record RunOutcomeCase(
    string CaseId,
    DateTimeOffset Now,
    int FiveHourRemaining,
    int SevenDayRemaining,
    int ExtraPoolRemaining,
    string ExtraPoolAuthorization,
    int ActualCredits,
    string ExpectedAdmissionResult,
    string? ExpectedAdmissionRejectionReason,
    int ExpectedAllowance,
    int ExpectedExtra,
    int ExpectedAbsorbed,
    int ExpectedFiveHourRemaining,
    int ExpectedSevenDayRemaining,
    int ExpectedExtraRemaining,
    DateTimeOffset? FiveHourOpenedUtc = null,
    DateTimeOffset? FiveHourExpiresUtc = null,
    DateTimeOffset? SevenDayOpenedUtc = null,
    DateTimeOffset? SevenDayExpiresUtc = null,
    DateTimeOffset? ExpectedFiveHourResetUtc = null,
    DateTimeOffset? ExpectedSevenDayResetUtc = null)
{
    public string IdempotencyKey => CaseId.ToLowerInvariant().Replace('_', '-');

    public override string ToString() => CaseId;
}

public sealed class RunOutcomeCases : IEnumerable<object[]>
{
    private static readonly DateTimeOffset DefaultNow =
        DateTimeOffset.Parse("2026-07-01T23:01:23Z");

    private static readonly DateTimeOffset FiveHourExpiredNow =
        DateTimeOffset.Parse("2026-07-02T08:00:00Z");

    private static readonly DateTimeOffset SevenDayExpiredNow =
        DateTimeOffset.Parse("2026-07-08T08:00:00Z");

    public IEnumerator<object[]> GetEnumerator()
    {
        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_001_R1_allowance_covers_all_actual_credits",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 50,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 50,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 50,
            ExpectedSevenDayRemaining: 950,
            ExpectedExtraRemaining: 0));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_002_R2_5h_expired_then_lazy_renews_before_run",
            Now: FiveHourExpiredNow,
            FiveHourRemaining: 0,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 50,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 50,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 50,
            ExpectedSevenDayRemaining: 950,
            ExpectedExtraRemaining: 0,
            FiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            FiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-02T04:01:23Z"),
            SevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            SevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T23:01:23Z"),
            ExpectedFiveHourResetUtc: FiveHourExpiredNow.AddHours(5)));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_003_R3_7d_expired_then_lazy_renews_before_run",
            Now: SevenDayExpiredNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 50,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 50,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 50,
            ExpectedSevenDayRemaining: 950,
            ExpectedExtraRemaining: 0,
            FiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-08T07:00:00Z"),
            FiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            SevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            SevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z"),
            ExpectedSevenDayResetUtc: SevenDayExpiredNow.AddDays(7)));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_004_R4_both_windows_expired_then_lazy_renew_before_run",
            Now: SevenDayExpiredNow,
            FiveHourRemaining: 0,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 50,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 50,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 50,
            ExpectedSevenDayRemaining: 950,
            ExpectedExtraRemaining: 0,
            FiveHourOpenedUtc: DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            FiveHourExpiresUtc: DateTimeOffset.Parse("2026-07-02T04:01:23Z"),
            SevenDayOpenedUtc: DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            SevenDayExpiresUtc: DateTimeOffset.Parse("2026-07-08T00:00:00Z"),
            ExpectedFiveHourResetUtc: SevenDayExpiredNow.AddHours(5),
            ExpectedSevenDayResetUtc: SevenDayExpiredNow.AddDays(7)));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_005_R5_5h_quota_unavailable_and_no_extra_pool",
            Now: DefaultNow,
            FiveHourRemaining: 0,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 0,
            ExpectedAdmissionResult: "rejected",
            ExpectedAdmissionRejectionReason: "insufficient-credits",
            ExpectedAllowance: 0,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 1000,
            ExpectedExtraRemaining: 0));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_006_R6_7d_quota_unavailable_and_no_extra_pool",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 0,
            ExpectedAdmissionResult: "rejected",
            ExpectedAdmissionRejectionReason: "insufficient-credits",
            ExpectedAllowance: 0,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 100,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 0));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_007_R7_5h_quota_unavailable_and_extra_pool_needs_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 0,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 0,
            ExpectedAdmissionResult: "rejected",
            ExpectedAdmissionRejectionReason: "extra-pool-authorization-required",
            ExpectedAllowance: 0,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 1000));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_008_R8_7d_quota_unavailable_and_extra_pool_needs_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 0,
            ExpectedAdmissionResult: "rejected",
            ExpectedAdmissionRejectionReason: "extra-pool-authorization-required",
            ExpectedAllowance: 0,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 100,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 1000));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_009_R9_5h_quota_unavailable_but_extra_pool_authorized",
            Now: DefaultNow,
            FiveHourRemaining: 0,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "authorized",
            ActualCredits: 20,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 0,
            ExpectedExtra: 20,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 980,
            ExpectedExtraRemaining: 980));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_010_R10_7d_quota_unavailable_but_extra_pool_authorized",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 0,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "authorized",
            ActualCredits: 20,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 0,
            ExpectedExtra: 20,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 80,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 980));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_011_R11_unknown_actual_exceeds_5h_without_extra_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 120,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 100,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 20,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 880,
            ExpectedExtraRemaining: 1000));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_012_R12_unknown_actual_exceeds_7d_without_extra_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 10,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 20,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 10,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 10,
            ExpectedFiveHourRemaining: 80,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 1000));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_013_R13_unknown_actual_exceeds_both_windows_without_extra_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 10,
            SevenDayRemaining: 10,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "not-authorized",
            ActualCredits: 20,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 10,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 10,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 1000));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_014_R14_actual_exceeds_5h_with_extra_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "authorized",
            ActualCredits: 120,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 100,
            ExpectedExtra: 20,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 880,
            ExpectedExtraRemaining: 980));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_015_R15_actual_exceeds_7d_with_extra_authorization",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 10,
            ExtraPoolRemaining: 1000,
            ExtraPoolAuthorization: "authorized",
            ActualCredits: 20,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 10,
            ExpectedExtra: 10,
            ExpectedAbsorbed: 0,
            ExpectedFiveHourRemaining: 80,
            ExpectedSevenDayRemaining: 0,
            ExpectedExtraRemaining: 990));

        yield return Row(new RunOutcomeCase(
            CaseId: "TC_RUN_016_R16_actual_exceeds_allowance_and_authorized_extra_pool_is_unavailable",
            Now: DefaultNow,
            FiveHourRemaining: 100,
            SevenDayRemaining: 1000,
            ExtraPoolRemaining: 0,
            ExtraPoolAuthorization: "authorized",
            ActualCredits: 120,
            ExpectedAdmissionResult: "accepted",
            ExpectedAdmissionRejectionReason: null,
            ExpectedAllowance: 100,
            ExpectedExtra: 0,
            ExpectedAbsorbed: 20,
            ExpectedFiveHourRemaining: 0,
            ExpectedSevenDayRemaining: 880,
            ExpectedExtraRemaining: 0));
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static object[] Row(RunOutcomeCase testCase) => [testCase];
}
