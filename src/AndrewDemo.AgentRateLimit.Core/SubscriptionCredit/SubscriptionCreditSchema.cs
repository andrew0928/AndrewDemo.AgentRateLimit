using Microsoft.Data.Sqlite;

namespace AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;

/// <summary>
/// SQLite schema for the subscription credit rate limit. Three tables:
/// <c>subscriptions</c> holds limits and the current extra pool balance;
/// <c>audit_records</c> is the append-only accounting ledger (window usage and
/// reconciliation are derived from it, so audit and accounting cannot drift apart);
/// <c>idempotency_records</c> stores the decision snapshot replayed on resend.
/// </summary>
internal static class SubscriptionCreditSchema
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS subscriptions (
            subscription_id TEXT NOT NULL PRIMARY KEY,
            user_id TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            limit_5h_credits INTEGER NOT NULL CHECK (limit_5h_credits >= 0),
            limit_7d_credits INTEGER NOT NULL CHECK (limit_7d_credits >= 0),
            extra_pool_balance INTEGER NOT NULL CHECK (extra_pool_balance >= 0),
            created_at_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS audit_records (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            audit_id TEXT NOT NULL UNIQUE,
            record_type TEXT NOT NULL,
            occurred_at_unix_ms INTEGER NOT NULL,
            user_id TEXT NULL,
            subscription_id TEXT NULL,
            credits INTEGER NULL,
            covered_by_allowance INTEGER NULL,
            covered_by_extra INTEGER NULL,
            decision_result TEXT NULL,
            reason TEXT NULL,
            correlation_id TEXT NULL,
            idempotency_key TEXT NULL,
            actor TEXT NULL,
            extra_pool_delta INTEGER NULL,
            extra_pool_balance_after INTEGER NULL,
            related_audit_id TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_audit_subscription_time
            ON audit_records (subscription_id, occurred_at_unix_ms);

        CREATE INDEX IF NOT EXISTS ix_audit_user_time
            ON audit_records (user_id, occurred_at_unix_ms);

        CREATE INDEX IF NOT EXISTS ix_audit_time
            ON audit_records (occurred_at_unix_ms);

        CREATE INDEX IF NOT EXISTS ix_audit_accepted_usage
            ON audit_records (subscription_id, occurred_at_unix_ms)
            WHERE record_type = 'usage-decision' AND decision_result = 'accepted';

        CREATE INDEX IF NOT EXISTS ix_audit_pool_balance
            ON audit_records (subscription_id, occurred_at_unix_ms)
            WHERE extra_pool_balance_after IS NOT NULL;

        CREATE TABLE IF NOT EXISTS idempotency_records (
            subscription_id TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            payload_fingerprint TEXT NOT NULL,
            user_id TEXT NOT NULL,
            decision_result TEXT NOT NULL,
            reason TEXT NULL,
            requested_credits INTEGER NOT NULL,
            covered_by_allowance INTEGER NOT NULL,
            covered_by_extra INTEGER NOT NULL,
            remaining_5h_after INTEGER NULL,
            remaining_7d_after INTEGER NULL,
            extra_pool_after INTEGER NULL,
            correlation_id TEXT NULL,
            decision_time_unix_ms INTEGER NOT NULL,
            audit_id TEXT NOT NULL,
            PRIMARY KEY (subscription_id, idempotency_key)
        );
        """;

    internal static void EnsureCreated(SqliteConnection connection)
    {
        using var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode=WAL;";
        walCommand.ExecuteScalar();

        using var ddlCommand = connection.CreateCommand();
        ddlCommand.CommandText = Ddl;
        ddlCommand.ExecuteNonQuery();
    }
}
