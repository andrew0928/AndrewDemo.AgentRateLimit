namespace AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;

/// <summary>
/// Rolling window durations fixed by the V1 spec. At decision time T a window of
/// duration D counts accepted usage where <c>T - D &lt; usage time &lt;= T</c>;
/// usage exactly D old is excluded.
/// </summary>
public static class SubscriptionCreditWindows
{
    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);
}
