namespace AndrewDemo.AgentRateLimit.Abstract.Usage;

public enum UsageDecisionMode
{
    DecideOnly,
    Consume
}

public enum UsageCreditAmountMode
{
    ExactCredits,
    MinimumAvailableBalance
}

public enum UsageExtraPoolAuthorization
{
    NotAuthorized,
    Authorized
}

public enum UsageDecisionResult
{
    Accepted,
    Rejected,
    Conflict,
    Invalid
}

public enum UsageRejectionReason
{
    InsufficientCredits,
    ExtraPoolAuthorizationRequired,
    SubscriptionNotFound,
    SubscriptionDisabled,
    UserSubscriptionMismatch
}

public enum UsageInvalidReason
{
    CreditsNotInteger,
    CreditsNotPositive,
    MissingUserId,
    MissingSubscriptionId,
    MissingIdempotencyKey
}

public enum UsageConflictReason
{
    IdempotencyKeyPayloadMismatch
}

public enum UsageWindowKind
{
    FiveHours,
    SevenDays
}
