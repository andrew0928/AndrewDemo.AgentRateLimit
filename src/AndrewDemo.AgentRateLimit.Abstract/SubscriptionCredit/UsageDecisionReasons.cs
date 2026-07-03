namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Reason codes per spec sections 7.2, 7.3 and 7.4. The spec allows additional
/// implementation reasons beyond the required minimum set.
/// </summary>
public static class UsageDecisionReasons
{
    // Rejection reasons (spec 7.2)
    public const string InsufficientCredits = "insufficient-credits";
    public const string SubscriptionNotFound = "subscription-not-found";
    public const string SubscriptionDisabled = "subscription-disabled";
    public const string UserSubscriptionMismatch = "user-subscription-mismatch";

    // Invalid reasons (spec 7.3)
    public const string CreditsNotInteger = "credits-not-integer";
    public const string CreditsNotPositive = "credits-not-positive";
    public const string MissingUserId = "missing-user-id";
    public const string MissingSubscriptionId = "missing-subscription-id";
    public const string MissingIdempotencyKey = "missing-idempotency-key";

    /// <summary>
    /// Implementation-defined invalid reason: requested credits is a positive integer
    /// but exceeds the Int64 accounting range supported by this implementation.
    /// </summary>
    public const string CreditsOutOfRange = "credits-out-of-range";

    // Conflict reasons (spec 7.4)
    public const string IdempotencyKeyPayloadMismatch = "idempotency-key-payload-mismatch";
}
