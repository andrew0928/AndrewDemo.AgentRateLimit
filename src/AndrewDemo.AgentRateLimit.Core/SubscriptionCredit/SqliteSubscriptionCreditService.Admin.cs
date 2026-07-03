using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using Microsoft.Data.Sqlite;

namespace AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;

public sealed partial class SqliteSubscriptionCreditService
{
    public async Task CreateSubscriptionAsync(
        SubscriptionDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.SubscriptionId);
        ArgumentOutOfRangeException.ThrowIfNegative(definition.Limit5hCredits);
        ArgumentOutOfRangeException.ThrowIfNegative(definition.Limit7dCredits);
        ArgumentOutOfRangeException.ThrowIfNegative(definition.InitialExtraPoolCredits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(definition.Limit5hCredits, SubscriptionCreditBounds.MaxCreditAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(definition.Limit7dCredits, SubscriptionCreditBounds.MaxCreditAmount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(definition.InitialExtraPoolCredits, SubscriptionCreditBounds.MaxCreditAmount);

        await using var connection = OpenConnection();
        await using var transaction = BeginWrite(connection);
        var now = _timeProvider.GetUtcNow();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO subscriptions (
                    subscription_id, user_id, enabled, limit_5h_credits, limit_7d_credits,
                    extra_pool_balance, created_at_unix_ms)
                VALUES (@sid, @user_id, @enabled, @limit_5h, @limit_7d, @extra_pool, @created_at)
                """;
            command.Parameters.AddWithValue("@sid", definition.SubscriptionId);
            command.Parameters.AddWithValue("@user_id", definition.UserId);
            command.Parameters.AddWithValue("@enabled", definition.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("@limit_5h", definition.Limit5hCredits);
            command.Parameters.AddWithValue("@limit_7d", definition.Limit7dCredits);
            command.Parameters.AddWithValue("@extra_pool", definition.InitialExtraPoolCredits);
            command.Parameters.AddWithValue("@created_at", ToUnixMs(now));

            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
            {
                throw new InvalidOperationException(
                    $"Subscription '{definition.SubscriptionId}' already exists.", ex);
            }
        }

        // Seed record: gives reconciliation a balance baseline from the beginning of
        // the subscription's life, even when the initial pool is zero.
        InsertAuditRecord(connection, transaction, new AuditRecordFields
        {
            AuditId = NewAuditId(),
            RecordType = AuditRecordTypeNames.ExtraPoolSeed,
            OccurredAtUnixMs = ToUnixMs(now),
            UserId = definition.UserId,
            SubscriptionId = definition.SubscriptionId,
            Actor = definition.Actor,
            ExtraPoolDelta = definition.InitialExtraPoolCredits,
            ExtraPoolBalanceAfter = definition.InitialExtraPoolCredits,
        });

        transaction.Commit();
    }

    public async Task SetSubscriptionEnabledAsync(
        string subscriptionId,
        bool enabled,
        string actor,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await using var connection = OpenConnection();
        await using var transaction = BeginWrite(connection);
        var now = _timeProvider.GetUtcNow();

        var subscription = LoadSubscription(connection, transaction, subscriptionId)
            ?? throw new InvalidOperationException($"Subscription '{subscriptionId}' does not exist.");

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE subscriptions SET enabled = @enabled WHERE subscription_id = @sid";
            command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("@sid", subscriptionId);
            command.ExecuteNonQuery();
        }

        InsertAuditRecord(connection, transaction, new AuditRecordFields
        {
            AuditId = NewAuditId(),
            RecordType = AuditRecordTypeNames.SubscriptionStatusChange,
            OccurredAtUnixMs = ToUnixMs(now),
            UserId = subscription.UserId,
            SubscriptionId = subscriptionId,
            Reason = reason ?? (enabled ? "enabled" : "disabled"),
            Actor = actor,
        });

        transaction.Commit();
    }

    public async Task<AuditRecord> AdjustExtraPoolAsync(
        ExtraPoolAdjustment adjustment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        ArgumentException.ThrowIfNullOrWhiteSpace(adjustment.SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adjustment.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(adjustment.Reason);
        if (adjustment.DeltaCredits == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adjustment), "Extra pool adjustment delta must be non-zero.");
        }

        // A delta beyond the credit bound can never yield a valid balance (which
        // lives in [0, MaxCreditAmount]); bounding it here also keeps the balance
        // addition below free of Int64 overflow.
        if (Math.Abs(adjustment.DeltaCredits) > SubscriptionCreditBounds.MaxCreditAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adjustment), adjustment.DeltaCredits,
                $"Extra pool adjustment delta must be within ±{SubscriptionCreditBounds.MaxCreditAmount}.");
        }

        await using var connection = OpenConnection();
        await using var transaction = BeginWrite(connection);
        var now = _timeProvider.GetUtcNow();

        var subscription = LoadSubscription(connection, transaction, adjustment.SubscriptionId)
            ?? throw new InvalidOperationException(
                $"Subscription '{adjustment.SubscriptionId}' does not exist.");

        // The extra pool must never go negative and stays within the credit bound
        // (spec 4.4 plus the implementation cap); a refused adjustment changes nothing.
        long newBalance = subscription.ExtraPoolBalance + adjustment.DeltaCredits;
        if (newBalance < 0)
        {
            throw new InvalidOperationException(
                $"Adjustment of {adjustment.DeltaCredits} would make the extra pool of " +
                $"'{adjustment.SubscriptionId}' negative (current balance {subscription.ExtraPoolBalance}).");
        }

        if (newBalance > SubscriptionCreditBounds.MaxCreditAmount)
        {
            throw new InvalidOperationException(
                $"Adjustment of {adjustment.DeltaCredits} would push the extra pool of " +
                $"'{adjustment.SubscriptionId}' above the supported maximum of " +
                $"{SubscriptionCreditBounds.MaxCreditAmount}.");
        }

        UpdateExtraPoolBalance(connection, transaction, adjustment.SubscriptionId, newBalance);

        var nowMs = ClampToLedgerTime(
            connection, transaction, adjustment.SubscriptionId, ToUnixMs(now));
        now = FromUnixMs(nowMs);

        var auditId = NewAuditId();
        InsertAuditRecord(connection, transaction, new AuditRecordFields
        {
            AuditId = auditId,
            RecordType = AuditRecordTypeNames.ExtraPoolAdjustment,
            OccurredAtUnixMs = nowMs,
            UserId = subscription.UserId,
            SubscriptionId = adjustment.SubscriptionId,
            Reason = adjustment.Reason,
            CorrelationId = adjustment.CorrelationId,
            Actor = adjustment.Actor,
            ExtraPoolDelta = adjustment.DeltaCredits,
            ExtraPoolBalanceAfter = newBalance,
        });

        transaction.Commit();

        return new AuditRecord
        {
            AuditId = auditId,
            RecordType = AuditRecordType.ExtraPoolAdjustment,
            OccurredAt = now,
            UserId = subscription.UserId,
            SubscriptionId = adjustment.SubscriptionId,
            Reason = adjustment.Reason,
            CorrelationId = adjustment.CorrelationId,
            Actor = adjustment.Actor,
            ExtraPoolDelta = adjustment.DeltaCredits,
            ExtraPoolBalanceAfter = newBalance,
        };
    }

    public async Task<AuditRecord> RecordManualCorrectionAsync(
        ManualCorrection correction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentException.ThrowIfNullOrWhiteSpace(correction.SubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correction.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(correction.Reason);

        await using var connection = OpenConnection();
        await using var transaction = BeginWrite(connection);
        var now = _timeProvider.GetUtcNow();

        var subscription = LoadSubscription(connection, transaction, correction.SubscriptionId)
            ?? throw new InvalidOperationException(
                $"Subscription '{correction.SubscriptionId}' does not exist.");

        var auditId = NewAuditId();
        InsertAuditRecord(connection, transaction, new AuditRecordFields
        {
            AuditId = auditId,
            RecordType = AuditRecordTypeNames.ManualCorrection,
            OccurredAtUnixMs = ToUnixMs(now),
            UserId = subscription.UserId,
            SubscriptionId = correction.SubscriptionId,
            Credits = correction.Credits,
            Reason = correction.Reason,
            CorrelationId = correction.CorrelationId,
            Actor = correction.Actor,
            RelatedAuditId = correction.RelatedAuditId,
        });

        transaction.Commit();

        return new AuditRecord
        {
            AuditId = auditId,
            RecordType = AuditRecordType.ManualCorrection,
            OccurredAt = now,
            UserId = subscription.UserId,
            SubscriptionId = correction.SubscriptionId,
            Credits = correction.Credits,
            Reason = correction.Reason,
            CorrelationId = correction.CorrelationId,
            Actor = correction.Actor,
            RelatedAuditId = correction.RelatedAuditId,
        };
    }
}
