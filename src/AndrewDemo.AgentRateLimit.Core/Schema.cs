namespace AndrewDemo.AgentRateLimit.Core;

internal static class Schema
{
    public const string Script =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS subscriptions (
            subscription_id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL,
            state TEXT NOT NULL,
            five_hour_limit INTEGER NOT NULL,
            seven_day_limit INTEGER NOT NULL,
            extra_pool_remaining INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS usage_requests (
            subscription_id TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            user_id TEXT NOT NULL,
            payload_hash TEXT NOT NULL,
            decision_result TEXT NOT NULL,
            requested_credits INTEGER NOT NULL,
            covered_by_allowance INTEGER NOT NULL,
            covered_by_extra INTEGER NOT NULL,
            remaining_five_hour INTEGER NOT NULL,
            remaining_seven_day INTEGER NOT NULL,
            remaining_extra_pool INTEGER NOT NULL,
            reason TEXT NULL,
            audit_id INTEGER NOT NULL,
            PRIMARY KEY (subscription_id, idempotency_key)
        );

        CREATE TABLE IF NOT EXISTS audit_records (
            audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
            time_ticks INTEGER NOT NULL,
            record_type TEXT NOT NULL,
            user_id TEXT NULL,
            subscription_id TEXT NULL,
            requested_credits INTEGER NULL,
            covered_by_allowance INTEGER NOT NULL DEFAULT 0,
            covered_by_extra INTEGER NOT NULL DEFAULT 0,
            decision_result TEXT NULL,
            reason TEXT NULL,
            correlation_id TEXT NULL,
            idempotency_key TEXT NULL,
            actor TEXT NULL,
            source TEXT NULL,
            changed_credits INTEGER NOT NULL DEFAULT 0,
            resulting_extra_pool INTEGER NULL,
            payload_hash TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_audit_subscription_time
            ON audit_records(subscription_id, time_ticks);

        CREATE INDEX IF NOT EXISTS ix_audit_user_time
            ON audit_records(user_id, time_ticks);
        """;
}
