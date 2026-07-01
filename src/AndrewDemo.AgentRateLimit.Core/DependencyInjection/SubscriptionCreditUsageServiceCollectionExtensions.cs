using AndrewDemo.AgentRateLimit.Abstract.Usage;
using Microsoft.Extensions.DependencyInjection;

namespace AndrewDemo.AgentRateLimit.Core.DependencyInjection;

public static class SubscriptionCreditUsageServiceCollectionExtensions
{
    public static SubscriptionCreditUsageBuilder AddSubscriptionCreditUsage(
        this IServiceCollection services,
        Action<SubscriptionCreditUsageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton(TimeProvider.System);

        var builder = new SubscriptionCreditUsageBuilder(services);
        configure(builder);

        return builder;
    }

    public static IServiceCollection AddSubscriptionCreditUsageService(
        this IServiceCollection services,
        string sqliteConnectionString)
    {
        services.AddSubscriptionCreditUsage(builder => builder.UseSqlite(sqliteConnectionString));
        return services;
    }
}
