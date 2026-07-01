using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;

await UsageDecisionDeveloperExperienceTests
    .TcSettle002_BalanceProbeThenActualOverage_RecordsSystemAbsorption();

Console.WriteLine("Abstract developer-experience test sketch completed.");

internal static class UsageDecisionDeveloperExperienceTests
{
    public static async Task TcSettle002_BalanceProbeThenActualOverage_RecordsSystemAbsorption()
    {
        // TC-SETTLE-002 from spec/testcases/subscription-credit-rate-limit-v1.md:
        // Given sub-a has 5h remaining 100 and 7d remaining 500.
        // When the caller does not know final credits before execution,
        // Then DecideAsync should use a minimum balance probe, not requested credits = 0.
        //
        // Important DX decision:
        // - requested credits = 0 remains invalid because zero means "no charge", not "unknown".
        // - requested credits = 1 is acceptable only when CreditAmountMode says it is a minimum
        //   available-balance threshold, not the final expected cost.
        // - final actual credits are sent later through ConsumeAsync as ExactCredits.

        // TODO: Replace this fake with AndrewDemo.AgentRateLimit.Core once the Core/storage slice exists.
        // Expected future setup:
        // - create a controllable clock fixed at 2026-07-01T00:00:00Z
        // - seed user-a/sub-a as active
        // - seed 5h limit 100, 7d limit 500, extra pool 0
        // - call the real ISubscriptionCreditUsageService implementation
        ISubscriptionCreditUsageService usage = new ContractOnlyUsageService();

        var balanceProbe = new UsageCreditRequest(
            UserId: new UserId("user-a"),
            SubscriptionId: new SubscriptionId("sub-a"),
            RequestedCredits: RequestedCreditsInput.FromInt32(1),
            CreditAmountMode: UsageCreditAmountMode.MinimumAvailableBalance,
            ExtraPoolAuthorization: UsageExtraPoolAuthorization.NotAuthorized,
            IdempotencyKey: new IdempotencyKey("tc-settle-002"),
            CorrelationId: new CorrelationId("corr-tc-settle-002-probe"),
            Source: "abstract-dx-test");

        var admitted = await usage.DecideAsync(balanceProbe, CancellationToken.None);

        Assert.Equal(UsageDecisionMode.DecideOnly, admitted.Mode);
        Assert.Equal(UsageCreditAmountMode.MinimumAvailableBalance, admitted.CreditAmountMode);
        Assert.Equal(UsageDecisionResult.Accepted, admitted.Result);
        Assert.Equal(new CreditAmount(1), admitted.RequestedCredits);
        Assert.Equal(CreditAmount.Zero, admitted.CreditsCoveredBySubscriptionAllowance);
        Assert.Equal(CreditAmount.Zero, admitted.CreditsCoveredByExtraPool);
        Assert.Equal(CreditAmount.Zero, admitted.CreditsAbsorbedBySystem);
        Assert.Equal(new CreditAmount(100), admitted.FiveHourWindowAfterDecision.Remaining);
        Assert.Equal(new CreditAmount(500), admitted.SevenDayWindowAfterDecision.Remaining);
        Assert.Null(admitted.AuditReference);

        var actualUsage = balanceProbe with
        {
            RequestedCredits = RequestedCreditsInput.FromInt32(120),
            CreditAmountMode = UsageCreditAmountMode.ExactCredits,
            CorrelationId = new CorrelationId("corr-tc-settle-002-consume")
        };

        var consumed = await usage.ConsumeAsync(actualUsage, CancellationToken.None);

        Assert.Equal(UsageDecisionMode.Consume, consumed.Mode);
        Assert.Equal(UsageCreditAmountMode.ExactCredits, consumed.CreditAmountMode);
        Assert.Equal(UsageDecisionResult.Accepted, consumed.Result);
        Assert.Equal(new CreditAmount(120), consumed.RequestedCredits);
        Assert.Equal(new CreditAmount(100), consumed.CreditsCoveredBySubscriptionAllowance);
        Assert.Equal(CreditAmount.Zero, consumed.CreditsCoveredByExtraPool);
        Assert.Equal(new CreditAmount(20), consumed.CreditsAbsorbedBySystem);
        Assert.Equal(CreditAmount.Zero, consumed.FiveHourWindowAfterDecision.Remaining);
        Assert.Equal(new CreditAmount(380), consumed.SevenDayWindowAfterDecision.Remaining);
        Assert.Equal(CreditAmount.Zero, consumed.ExtraPoolRemainingAfterDecision);
        Assert.NotNull(consumed.AuditReference);
    }
}

internal sealed class ContractOnlyUsageService : ISubscriptionCreditUsageService
{
    private static readonly DateTimeOffset DecisionTime =
        DateTimeOffset.Parse("2026-07-01T00:00:00Z");

    public ValueTask<UsageCreditDecision> DecideAsync(
        UsageCreditRequest request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(CreateDecision(UsageDecisionMode.DecideOnly, auditReference: null));
    }

    public ValueTask<UsageCreditDecision> ConsumeAsync(
        UsageCreditRequest request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(CreateSettlementDecision(
            new AuditReference("audit-tc-settle-002")));
    }

    private static UsageCreditDecision CreateDecision(
        UsageDecisionMode mode,
        AuditReference? auditReference)
    {
        return new UsageCreditDecision(
            Mode: mode,
            CreditAmountMode: UsageCreditAmountMode.MinimumAvailableBalance,
            Result: UsageDecisionResult.Accepted,
            RequestedCredits: new CreditAmount(1),
            CreditsCoveredBySubscriptionAllowance: CreditAmount.Zero,
            CreditsCoveredByExtraPool: CreditAmount.Zero,
            CreditsAbsorbedBySystem: CreditAmount.Zero,
            FiveHourWindowAfterDecision: new UsageWindowBalance(
                Kind: UsageWindowKind.FiveHours,
                Limit: new CreditAmount(100),
                Used: CreditAmount.Zero,
                Remaining: new CreditAmount(100),
                NextResetTimeUtc: null),
            SevenDayWindowAfterDecision: new UsageWindowBalance(
                Kind: UsageWindowKind.SevenDays,
                Limit: new CreditAmount(500),
                Used: CreditAmount.Zero,
                Remaining: new CreditAmount(500),
                NextResetTimeUtc: null),
            ExtraPoolRemainingAfterDecision: CreditAmount.Zero,
            RejectionReason: null,
            InvalidReason: null,
            ConflictReason: null,
            AuditReference: auditReference,
            DecisionTimeUtc: DecisionTime);
    }

    private static UsageCreditDecision CreateSettlementDecision(AuditReference auditReference)
    {
        return new UsageCreditDecision(
            Mode: UsageDecisionMode.Consume,
            CreditAmountMode: UsageCreditAmountMode.ExactCredits,
            Result: UsageDecisionResult.Accepted,
            RequestedCredits: new CreditAmount(120),
            CreditsCoveredBySubscriptionAllowance: new CreditAmount(100),
            CreditsCoveredByExtraPool: CreditAmount.Zero,
            CreditsAbsorbedBySystem: new CreditAmount(20),
            FiveHourWindowAfterDecision: new UsageWindowBalance(
                Kind: UsageWindowKind.FiveHours,
                Limit: new CreditAmount(100),
                Used: new CreditAmount(120),
                Remaining: CreditAmount.Zero,
                NextResetTimeUtc: null),
            SevenDayWindowAfterDecision: new UsageWindowBalance(
                Kind: UsageWindowKind.SevenDays,
                Limit: new CreditAmount(500),
                Used: new CreditAmount(120),
                Remaining: new CreditAmount(380),
                NextResetTimeUtc: null),
            ExtraPoolRemainingAfterDecision: CreditAmount.Zero,
            RejectionReason: null,
            InvalidReason: null,
            ConflictReason: null,
            AuditReference: auditReference,
            DecisionTimeUtc: DecisionTime);
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void Null(object? actual)
    {
        if (actual is not null)
        {
            throw new InvalidOperationException($"Expected null, got {actual}.");
        }
    }

    public static void NotNull(object? actual)
    {
        if (actual is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }
}
