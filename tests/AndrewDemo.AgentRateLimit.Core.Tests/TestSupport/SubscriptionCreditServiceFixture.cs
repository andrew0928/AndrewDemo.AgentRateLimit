using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;

namespace AndrewDemo.AgentRateLimit.Core.Tests.TestSupport;

/// <summary>
/// Per-test harness: a temp-file SQLite database, a manual forward-only clock, and a
/// service instance. <see cref="Restart"/> creates a fresh service over the same
/// database file to model a service restart with the same persistence.
/// </summary>
public sealed class SubscriptionCreditServiceFixture : IDisposable
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory;

    public SubscriptionCreditServiceFixture(DateTimeOffset? startUtc = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "agent-rate-limit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "subscription-credit.db");
        Clock = new ManualTimeProvider(startUtc ?? DefaultStart);
        Service = new SqliteSubscriptionCreditService(DatabasePath, Clock);
    }

    public string DatabasePath { get; }

    public ManualTimeProvider Clock { get; }

    public SqliteSubscriptionCreditService Service { get; private set; }

    public ISubscriptionCreditUsageService Usage => Service;

    public ISubscriptionCreditAdministrationService Admin => Service;

    /// <summary>
    /// Simulates a service restart over the same database persistence. Clearing the
    /// SQLite connection pool forces the new instance to reopen the database from
    /// disk instead of reusing the previous instance's pooled native handles.
    /// </summary>
    public void Restart()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Service = new SqliteSubscriptionCreditService(DatabasePath, Clock);
    }

    public Task CreateSubscriptionAsync(
        string userId = "user-a",
        string subscriptionId = "sub-a",
        long limit5h = 100,
        long limit7d = 1000,
        long extraPool = 0,
        bool enabled = true)
        => Admin.CreateSubscriptionAsync(new SubscriptionDefinition
        {
            UserId = userId,
            SubscriptionId = subscriptionId,
            Limit5hCredits = limit5h,
            Limit7dCredits = limit7d,
            InitialExtraPoolCredits = extraPool,
            Enabled = enabled,
        });

    public UsageRequest Request(
        string userId = "user-a",
        string subscriptionId = "sub-a",
        decimal credits = 10,
        string? idempotencyKey = null,
        string? correlationId = null)
        => new()
        {
            UserId = userId,
            SubscriptionId = subscriptionId,
            RequestedCredits = credits,
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("n"),
            CorrelationId = correlationId ?? Guid.NewGuid().ToString("n"),
        };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; leftover temp files are not a test failure.
        }
    }
}
