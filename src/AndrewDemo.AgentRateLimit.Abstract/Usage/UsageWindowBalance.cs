using AndrewDemo.AgentRateLimit.Abstract.Credits;

namespace AndrewDemo.AgentRateLimit.Abstract.Usage;

public sealed record UsageWindowBalance(
    UsageWindowKind Kind,
    CreditAmount Limit,
    CreditAmount Used,
    CreditAmount Remaining,
    DateTimeOffset? NextResetTimeUtc);
