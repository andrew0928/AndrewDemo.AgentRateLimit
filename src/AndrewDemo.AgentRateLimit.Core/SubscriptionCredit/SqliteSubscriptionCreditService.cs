using System.Data;
using System.Globalization;
using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using Microsoft.Data.Sqlite;

namespace AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;

/// <summary>
/// SQLite-backed implementation of the subscription credit rate limit V1 spec.
/// <para>
/// Consistency model: every state-changing operation runs inside a single
/// <c>BEGIN IMMEDIATE</c> transaction, so concurrent requests against the same
/// database serialize on SQLite's write lock and their observable results are
/// equivalent to that serial order (spec section 6). Reads run inside deferred
/// transactions for a consistent snapshot. Durability uses WAL journal mode with
/// <c>synchronous=FULL</c> so committed decisions survive restart (spec section 6).
/// </para>
/// </summary>
public sealed partial class SqliteSubscriptionCreditService
    : ISubscriptionCreditUsageService, ISubscriptionCreditAdministrationService
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;

    public SqliteSubscriptionCreditService(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;

        using var connection = OpenConnection();
        SubscriptionCreditSchema.EnsureCreated(connection);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static SqliteTransaction BeginWrite(SqliteConnection connection)
        => connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

    private static SqliteTransaction BeginRead(SqliteConnection connection)
        => connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);

    private static string NewAuditId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// Effective accounting time for a subscription: the wall clock, clamped so it
    /// never precedes the subscription's newest ledger record. Decision time is read
    /// inside the write transaction, but the wall clock itself can step backwards
    /// (NTP); without the clamp, committed usage with a later timestamp would be
    /// invisible to the window query (<c>occurred_at &lt;= now</c>) and accounting
    /// would no longer be equivalent to a serial order (spec section 6).
    /// </summary>
    private static long ClampToLedgerTime(
        SqliteConnection connection, SqliteTransaction transaction, string subscriptionId, long nowUnixMs)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(occurred_at_unix_ms) FROM audit_records WHERE subscription_id = @sid";
        command.Parameters.AddWithValue("@sid", subscriptionId);
        var result = command.ExecuteScalar();
        return result is long maxMs && maxMs > nowUnixMs ? maxMs : nowUnixMs;
    }

    /// <summary>Overflow-safe addition of non-negative credit amounts.</summary>
    private static long SaturatingAdd(long a, long b)
    {
        unchecked
        {
            long sum = a + b;
            return sum < a ? long.MaxValue : sum;
        }
    }

    private static long ToUnixMs(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnixMs(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    /// <summary>
    /// Idempotency payload fingerprint per spec 4.6. The payload identity is
    /// (user id, subscription id, requested credits); the correlation id is tracing
    /// metadata and intentionally not part of the fingerprint. Credits are normalized
    /// to their integer value so numerically equal requests (20 vs 20.0) fingerprint
    /// identically; only validated integral requests reach fingerprinting.
    /// </summary>
    private static string ComputePayloadFingerprint(UsageRequest request, long requestedCredits)
        => string.Join('\n',
            request.UserId,
            request.SubscriptionId,
            requestedCredits.ToString(CultureInfo.InvariantCulture));

    private sealed record SubscriptionRow(
        string SubscriptionId,
        string UserId,
        bool Enabled,
        long Limit5h,
        long Limit7d,
        long ExtraPoolBalance);

    private static SubscriptionRow? LoadSubscription(
        SqliteConnection connection, SqliteTransaction transaction, string subscriptionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT subscription_id, user_id, enabled, limit_5h_credits, limit_7d_credits, extra_pool_balance
            FROM subscriptions WHERE subscription_id = @sid
            """;
        command.Parameters.AddWithValue("@sid", subscriptionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SubscriptionRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private sealed record WindowUsage(long Used5h, long Used7d, long? Oldest5hUnixMs, long? Oldest7dUnixMs);

    /// <summary>
    /// Window usage at decision time <paramref name="nowUnixMs"/>: accepted usage with
    /// <c>now - window &lt; usage time &lt;= now</c> (spec 4.2). Usage exactly one full
    /// window old is excluded. The 5h window is a subset of the 7d window, so one scan
    /// over the 7d range computes both.
    /// </summary>
    private static WindowUsage QueryWindowUsage(
        SqliteConnection connection, SqliteTransaction transaction, string subscriptionId, long nowUnixMs)
    {
        var lower5h = nowUnixMs - (long)SubscriptionCreditWindows.FiveHours.TotalMilliseconds;
        var lower7d = nowUnixMs - (long)SubscriptionCreditWindows.SevenDays.TotalMilliseconds;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN occurred_at_unix_ms > @lower5h THEN credits END), 0) AS used_5h,
                COALESCE(SUM(credits), 0) AS used_7d,
                MIN(CASE WHEN occurred_at_unix_ms > @lower5h THEN occurred_at_unix_ms END) AS oldest_5h,
                MIN(occurred_at_unix_ms) AS oldest_7d
            FROM audit_records
            WHERE subscription_id = @sid
              AND record_type = 'usage-decision'
              AND decision_result = 'accepted'
              AND occurred_at_unix_ms > @lower7d
              AND occurred_at_unix_ms <= @now
            """;
        command.Parameters.AddWithValue("@sid", subscriptionId);
        command.Parameters.AddWithValue("@lower5h", lower5h);
        command.Parameters.AddWithValue("@lower7d", lower7d);
        command.Parameters.AddWithValue("@now", nowUnixMs);

        using var reader = command.ExecuteReader();
        reader.Read();
        return new WindowUsage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private sealed record AuditRecordFields
    {
        public required string AuditId { get; init; }
        public required string RecordType { get; init; }
        public required long OccurredAtUnixMs { get; init; }
        public string? UserId { get; init; }
        public string? SubscriptionId { get; init; }
        public long? Credits { get; init; }
        public long? CoveredByAllowance { get; init; }
        public long? CoveredByExtra { get; init; }
        public string? DecisionResult { get; init; }
        public string? Reason { get; init; }
        public string? CorrelationId { get; init; }
        public string? IdempotencyKey { get; init; }
        public string? Actor { get; init; }
        public long? ExtraPoolDelta { get; init; }
        public long? ExtraPoolBalanceAfter { get; init; }
        public string? RelatedAuditId { get; init; }
    }

    private static void InsertAuditRecord(
        SqliteConnection connection, SqliteTransaction transaction, AuditRecordFields fields)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_records (
                audit_id, record_type, occurred_at_unix_ms, user_id, subscription_id,
                credits, covered_by_allowance, covered_by_extra, decision_result, reason,
                correlation_id, idempotency_key, actor, extra_pool_delta,
                extra_pool_balance_after, related_audit_id)
            VALUES (
                @audit_id, @record_type, @occurred_at, @user_id, @subscription_id,
                @credits, @covered_by_allowance, @covered_by_extra, @decision_result, @reason,
                @correlation_id, @idempotency_key, @actor, @extra_pool_delta,
                @extra_pool_balance_after, @related_audit_id)
            """;
        command.Parameters.AddWithValue("@audit_id", fields.AuditId);
        command.Parameters.AddWithValue("@record_type", fields.RecordType);
        command.Parameters.AddWithValue("@occurred_at", fields.OccurredAtUnixMs);
        command.Parameters.AddWithValue("@user_id", (object?)fields.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@subscription_id", (object?)fields.SubscriptionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@credits", (object?)fields.Credits ?? DBNull.Value);
        command.Parameters.AddWithValue("@covered_by_allowance", (object?)fields.CoveredByAllowance ?? DBNull.Value);
        command.Parameters.AddWithValue("@covered_by_extra", (object?)fields.CoveredByExtra ?? DBNull.Value);
        command.Parameters.AddWithValue("@decision_result", (object?)fields.DecisionResult ?? DBNull.Value);
        command.Parameters.AddWithValue("@reason", (object?)fields.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@correlation_id", (object?)fields.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@idempotency_key", (object?)fields.IdempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@actor", (object?)fields.Actor ?? DBNull.Value);
        command.Parameters.AddWithValue("@extra_pool_delta", (object?)fields.ExtraPoolDelta ?? DBNull.Value);
        command.Parameters.AddWithValue("@extra_pool_balance_after", (object?)fields.ExtraPoolBalanceAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("@related_audit_id", (object?)fields.RelatedAuditId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
