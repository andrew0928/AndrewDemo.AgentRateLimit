using AndrewDemo.AgentRateLimit.Abstract.Usage;
using AndrewDemo.AgentRateLimit.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AndrewDemo.AgentRateLimit.Core.DependencyInjection;

public sealed class SubscriptionCreditUsageBuilder
{
    internal SubscriptionCreditUsageBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    public SubscriptionCreditUsageBuilder UseSqlite(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Services.AddSingleton(new SubscriptionCreditUsageOptions(connectionString));
        Services.AddSingleton<SubscriptionCreditSqliteStore>();
        Services.AddSingleton<ISubscriptionCreditUsageService, SubscriptionCreditUsageService>();

        return this;
    }

    public SubscriptionCreditUsageBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        Services.AddSingleton(timeProvider);
        return this;
    }
}
