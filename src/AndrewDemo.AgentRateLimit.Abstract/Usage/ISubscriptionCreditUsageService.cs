namespace AndrewDemo.AgentRateLimit.Abstract.Usage;

public interface ISubscriptionCreditUsageService
{
    ValueTask<UsageCreditDecision> DecideAsync(
        UsageCreditRequest request,
        CancellationToken cancellationToken);

    ValueTask<UsageCreditDecision> ConsumeAsync(
        UsageCreditRequest request,
        CancellationToken cancellationToken);
}
