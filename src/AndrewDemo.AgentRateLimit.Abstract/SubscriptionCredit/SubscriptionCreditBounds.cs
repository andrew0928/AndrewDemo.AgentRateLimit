namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Implementation bounds on credit amounts. The spec does not bound credit values;
/// this implementation caps window limits, extra pool balances and requested credits
/// at <see cref="MaxCreditAmount"/> so that accounting arithmetic (including SQL
/// aggregation over a full 7d window) stays far away from Int64 overflow.
/// Requests above the cap are invalid with reason <c>credits-out-of-range</c>.
/// </summary>
public static class SubscriptionCreditBounds
{
    public const long MaxCreditAmount = 1_000_000_000_000_000; // 10^15
}
