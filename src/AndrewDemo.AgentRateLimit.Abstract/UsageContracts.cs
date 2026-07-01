namespace AndrewDemo.AgentRateLimit.Abstract;

public static class DecisionResults
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid";
}

public static class RejectionReasons
{
    public const string InsufficientCredits = "insufficient-credits";
    public const string SubscriptionNotFound = "subscription-not-found";
    public const string SubscriptionDisabled = "subscription-disabled";
    public const string UserSubscriptionMismatch = "user-subscription-mismatch";
}

public static class InvalidReasons
{
    public const string CreditsNotInteger = "credits-not-integer";
    public const string CreditsNotPositive = "credits-not-positive";
    public const string MissingUserId = "missing-user-id";
    public const string MissingSubscriptionId = "missing-subscription-id";
    public const string MissingIdempotencyKey = "missing-idempotency-key";
}

public static class ConflictReasons
{
    public const string IdempotencyKeyPayloadMismatch = "idempotency-key-payload-mismatch";
}

public sealed record UsageRequest(
    string? UserId,
    string? SubscriptionId,
    decimal RequestedCredits,
    string? IdempotencyKey,
    string? CorrelationId);

public sealed record UsageDecision(
    string Result,
    decimal RequestedCredits,
    long CreditsCoveredBySubscriptionWindowAllowance,
    long CreditsCoveredByExtraPool,
    long RemainingFiveHourCreditsAfterDecision,
    long RemainingSevenDayCreditsAfterDecision,
    long RemainingExtraPoolCreditsAfterDecision,
    string? Reason,
    string? AuditReference);

public sealed record UsageStatus(
    string UserId,
    string SubscriptionId,
    long FiveHourWindowLimit,
    long FiveHourWindowUsedCredits,
    long FiveHourWindowRemainingCredits,
    DateTimeOffset? FiveHourNextResetTime,
    long SevenDayWindowLimit,
    long SevenDayWindowUsedCredits,
    long SevenDayWindowRemainingCredits,
    DateTimeOffset? SevenDayNextResetTime,
    long ExtraPoolRemainingCredits);

public sealed record AuditRecord(
    long AuditId,
    DateTimeOffset Time,
    string RecordType,
    string? UserId,
    string? SubscriptionId,
    long? RequestedCredits,
    long CreditsCoveredBySubscriptionWindowAllowance,
    long CreditsCoveredByExtraPool,
    string? DecisionResult,
    string? Reason,
    string? CorrelationId,
    string? IdempotencyKey,
    string? Actor,
    string? Source,
    long ChangedCredits,
    long? ResultingExtraPoolCredits);

public sealed record ReconciliationReport(
    string SubscriptionId,
    DateTimeOffset From,
    DateTimeOffset To,
    long AcceptedCreditsTotal,
    long RejectedCreditsTotal,
    long SubscriptionAllowanceCoveredCreditsTotal,
    long ExtraPoolCoveredCreditsTotal,
    long ExtraPoolBeginningBalance,
    long ExtraPoolAddedCredits,
    long ExtraPoolConsumedCredits,
    long ExtraPoolAdjustedCredits,
    long ExtraPoolEndingBalance,
    long ConflictCount,
    long InvalidRequestCount,
    long ManualCorrectionCount);
