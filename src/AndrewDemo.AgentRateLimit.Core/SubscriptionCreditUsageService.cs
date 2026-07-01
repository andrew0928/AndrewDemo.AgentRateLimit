using System.Globalization;
using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;
using AndrewDemo.AgentRateLimit.Core.Storage;

namespace AndrewDemo.AgentRateLimit.Core;

internal sealed class SubscriptionCreditUsageService : ISubscriptionCreditUsageService
{
    private readonly SubscriptionCreditSqliteStore _store;
    private readonly TimeProvider _timeProvider;

    public SubscriptionCreditUsageService(
        SubscriptionCreditSqliteStore store,
        TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async ValueTask<UsageCreditDecision> DecideAsync(
        UsageCreditRequest request,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var validation = Validate(request, UsageDecisionMode.DecideOnly, now);
        if (validation is not null)
        {
            return validation;
        }

        var requestedCredits = ParseRequestedCredits(request.RequestedCredits.RawValue).CreditAmount!.Value;
        return await _store.DecideAsync(request, requestedCredits, now, cancellationToken);
    }

    public async ValueTask<UsageCreditDecision> ConsumeAsync(
        UsageCreditRequest request,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var validation = Validate(request, UsageDecisionMode.Consume, now);
        if (validation is not null)
        {
            return validation;
        }

        var requestedCredits = ParseRequestedCredits(request.RequestedCredits.RawValue).CreditAmount!.Value;
        return await _store.ConsumeAsync(request, requestedCredits, now, cancellationToken);
    }

    internal static string CreateConsumeFingerprint(UsageCreditRequest request, CreditAmount requestedCredits)
    {
        var userId = request.UserId?.Value ?? string.Empty;
        var subscriptionId = request.SubscriptionId?.Value ?? string.Empty;
        var idempotencyKey = request.IdempotencyKey?.Value ?? string.Empty;

        return string.Join(
            "|",
            userId,
            subscriptionId,
            requestedCredits.Value.ToString(CultureInfo.InvariantCulture),
            request.CreditAmountMode,
            request.ExtraPoolAuthorization,
            idempotencyKey,
            request.Source);
    }

    private static UsageCreditDecision? Validate(
        UsageCreditRequest request,
        UsageDecisionMode mode,
        DateTimeOffset now)
    {
        if (request.UserId is null || string.IsNullOrWhiteSpace(request.UserId.Value.Value))
        {
            return InvalidDecision(mode, request.CreditAmountMode, UsageInvalidReason.MissingUserId, now);
        }

        if (request.SubscriptionId is null || string.IsNullOrWhiteSpace(request.SubscriptionId.Value.Value))
        {
            return InvalidDecision(mode, request.CreditAmountMode, UsageInvalidReason.MissingSubscriptionId, now);
        }

        if (request.IdempotencyKey is null || string.IsNullOrWhiteSpace(request.IdempotencyKey.Value.Value))
        {
            return InvalidDecision(mode, request.CreditAmountMode, UsageInvalidReason.MissingIdempotencyKey, now);
        }

        var parsedCredits = ParseRequestedCredits(request.RequestedCredits.RawValue);
        if (parsedCredits.InvalidReason is not null)
        {
            return InvalidDecision(mode, request.CreditAmountMode, parsedCredits.InvalidReason.Value, now);
        }

        return null;
    }

    private static (CreditAmount? CreditAmount, UsageInvalidReason? InvalidReason) ParseRequestedCredits(string rawValue)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return (null, UsageInvalidReason.CreditsNotInteger);
        }

        if (value <= 0)
        {
            return (null, UsageInvalidReason.CreditsNotPositive);
        }

        return (new CreditAmount(value), null);
    }

    private static UsageCreditDecision InvalidDecision(
        UsageDecisionMode mode,
        UsageCreditAmountMode creditAmountMode,
        UsageInvalidReason reason,
        DateTimeOffset now)
    {
        return new UsageCreditDecision(
            Mode: mode,
            CreditAmountMode: creditAmountMode,
            Result: UsageDecisionResult.Invalid,
            RequestedCredits: null,
            CreditsCoveredBySubscriptionAllowance: CreditAmount.Zero,
            CreditsCoveredByExtraPool: CreditAmount.Zero,
            CreditsAbsorbedBySystem: CreditAmount.Zero,
            FiveHourWindowAfterDecision: EmptyBalance(UsageWindowKind.FiveHours),
            SevenDayWindowAfterDecision: EmptyBalance(UsageWindowKind.SevenDays),
            ExtraPoolRemainingAfterDecision: CreditAmount.Zero,
            RejectionReason: null,
            InvalidReason: reason,
            ConflictReason: null,
            AuditReference: null,
            DecisionTimeUtc: now);
    }

    internal static UsageWindowBalance EmptyBalance(UsageWindowKind kind)
    {
        return new UsageWindowBalance(
            Kind: kind,
            Limit: CreditAmount.Zero,
            Used: CreditAmount.Zero,
            Remaining: CreditAmount.Zero,
            NextResetTimeUtc: null);
    }
}
