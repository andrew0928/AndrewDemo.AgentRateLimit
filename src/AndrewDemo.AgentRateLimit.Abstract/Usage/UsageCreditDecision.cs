using AndrewDemo.AgentRateLimit.Abstract.Credits;

namespace AndrewDemo.AgentRateLimit.Abstract.Usage;

public sealed record UsageCreditDecision(
    UsageDecisionMode Mode,
    UsageCreditAmountMode CreditAmountMode,
    UsageDecisionResult Result,
    CreditAmount? RequestedCredits,
    CreditAmount CreditsCoveredBySubscriptionAllowance,
    CreditAmount CreditsCoveredByExtraPool,
    CreditAmount CreditsAbsorbedBySystem,
    UsageWindowBalance FiveHourWindowAfterDecision,
    UsageWindowBalance SevenDayWindowAfterDecision,
    CreditAmount ExtraPoolRemainingAfterDecision,
    UsageRejectionReason? RejectionReason,
    UsageInvalidReason? InvalidReason,
    UsageConflictReason? ConflictReason,
    AuditReference? AuditReference,
    DateTimeOffset DecisionTimeUtc);
