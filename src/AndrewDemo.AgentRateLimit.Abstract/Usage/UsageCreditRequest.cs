using AndrewDemo.AgentRateLimit.Abstract.Credits;

namespace AndrewDemo.AgentRateLimit.Abstract.Usage;

public sealed record UsageCreditRequest(
    UserId? UserId,
    SubscriptionId? SubscriptionId,
    RequestedCreditsInput RequestedCredits,
    UsageCreditAmountMode CreditAmountMode,
    IdempotencyKey? IdempotencyKey,
    CorrelationId CorrelationId,
    string Source);
