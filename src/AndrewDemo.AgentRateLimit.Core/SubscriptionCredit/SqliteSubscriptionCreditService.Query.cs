using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using Microsoft.Data.Sqlite;

namespace AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;

public sealed partial class SqliteSubscriptionCreditService
{
    public async Task<SubscriptionUsageStatus?> GetUsageStatusAsync(
        string subscriptionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        await using var connection = OpenConnection();
        await using var transaction = BeginRead(connection);

        var subscription = LoadSubscription(connection, transaction, subscriptionId);
        if (subscription is null)
        {
            return null;
        }

        // Observation time is ledger-clamped like the decision paths, so committed
        // usage can never be invisible to the status view under clock regressions.
        var nowMs = ClampToLedgerTime(
            connection, transaction, subscriptionId, ToUnixMs(_timeProvider.GetUtcNow()));
        var now = FromUnixMs(nowMs);

        var usage = QueryWindowUsage(connection, transaction, subscriptionId, nowMs);
        transaction.Commit();

        return new SubscriptionUsageStatus
        {
            SubscriptionId = subscription.SubscriptionId,
            UserId = subscription.UserId,
            Enabled = subscription.Enabled,
            Window5h = BuildWindowStatus(
                subscription.Limit5h, usage.Used5h, usage.Oldest5hUnixMs, SubscriptionCreditWindows.FiveHours),
            Window7d = BuildWindowStatus(
                subscription.Limit7d, usage.Used7d, usage.Oldest7dUnixMs, SubscriptionCreditWindows.SevenDays),
            ExtraPoolRemainingCredits = subscription.ExtraPoolBalance,
            ObservedAt = now,
        };
    }

    private static UsageWindowStatus BuildWindowStatus(
        long limit, long used, long? oldestUsageUnixMs, TimeSpan window)
        => new()
        {
            LimitCredits = limit,
            UsedCredits = used,
            RemainingCredits = Math.Max(0, limit - used),
            // The next moment used credits decreases: the oldest in-window usage plus
            // the window length (spec 3.3, TC-STATUS-002). Null when nothing is in the
            // window.
            NextResetTime = used > 0 && oldestUsageUnixMs is not null
                ? FromUnixMs(oldestUsageUnixMs.Value) + window
                : null,
        };

    public async Task<IReadOnlyList<AuditRecord>> QueryAuditTrailAsync(
        AuditTrailQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.UserId) && string.IsNullOrWhiteSpace(query.SubscriptionId))
        {
            throw new ArgumentException(
                "Audit trail queries must target a user or a subscription (spec section 5).",
                nameof(query));
        }

        if (query.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "Limit must be positive.");
        }

        await using var connection = OpenConnection();

        using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            conditions.Add("user_id = @user_id");
            command.Parameters.AddWithValue("@user_id", query.UserId);
        }

        if (!string.IsNullOrWhiteSpace(query.SubscriptionId))
        {
            conditions.Add("subscription_id = @subscription_id");
            command.Parameters.AddWithValue("@subscription_id", query.SubscriptionId);
        }

        if (query.FromInclusive is not null)
        {
            conditions.Add("occurred_at_unix_ms >= @from");
            command.Parameters.AddWithValue("@from", ToUnixMs(query.FromInclusive.Value));
        }

        if (query.ToExclusive is not null)
        {
            conditions.Add("occurred_at_unix_ms < @to");
            command.Parameters.AddWithValue("@to", ToUnixMs(query.ToExclusive.Value));
        }

        command.Parameters.AddWithValue("@limit", query.Limit);
        command.CommandText = $"""
            SELECT audit_id, record_type, occurred_at_unix_ms, user_id, subscription_id,
                   credits, covered_by_allowance, covered_by_extra, decision_result,
                   reason, correlation_id, idempotency_key, actor, extra_pool_delta,
                   extra_pool_balance_after, related_audit_id
            FROM audit_records
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY occurred_at_unix_ms, id
            LIMIT @limit
            """;

        var records = new List<AuditRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new AuditRecord
            {
                AuditId = reader.GetString(0),
                RecordType = AuditRecordTypeNames.Parse(reader.GetString(1)),
                OccurredAt = FromUnixMs(reader.GetInt64(2)),
                UserId = reader.IsDBNull(3) ? null : reader.GetString(3),
                SubscriptionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Credits = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                CoveredBySubscriptionAllowance = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                CoveredByExtraPool = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                DecisionResult = reader.IsDBNull(8) ? null : UsageDecisionResultNames.Parse(reader.GetString(8)),
                Reason = reader.IsDBNull(9) ? null : reader.GetString(9),
                CorrelationId = reader.IsDBNull(10) ? null : reader.GetString(10),
                IdempotencyKey = reader.IsDBNull(11) ? null : reader.GetString(11),
                Actor = reader.IsDBNull(12) ? null : reader.GetString(12),
                ExtraPoolDelta = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                ExtraPoolBalanceAfter = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                RelatedAuditId = reader.IsDBNull(15) ? null : reader.GetString(15),
            });
        }

        return records;
    }

    public async Task<ReconciliationReport> ExportReconciliationReportAsync(
        DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken cancellationToken = default)
    {
        if (toExclusive <= fromInclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toExclusive), toExclusive, "Report period must be non-empty.");
        }

        var fromMs = ToUnixMs(fromInclusive);
        var toMs = ToUnixMs(toExclusive);

        await using var connection = OpenConnection();
        await using var transaction = BeginRead(connection);

        var rows = new Dictionary<string, MutableRow>(StringComparer.Ordinal);

        MutableRow Row(string subscriptionId)
        {
            if (!rows.TryGetValue(subscriptionId, out var row))
            {
                row = new MutableRow { SubscriptionId = subscriptionId };
                rows[subscriptionId] = row;
            }

            return row;
        }

        // Universe of rows: every provisioned subscription, plus any subscription id
        // that only appears in audit records (e.g. rejected not-found requests).
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT subscription_id, user_id FROM subscriptions";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Row(reader.GetString(0)).UserId = reader.GetString(1);
            }
        }

        var unattributedInvalidCount = 0;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT subscription_id,
                       COALESCE(SUM(CASE WHEN decision_result = 'accepted' THEN credits END), 0),
                       COALESCE(SUM(CASE WHEN decision_result = 'rejected' THEN credits END), 0),
                       COALESCE(SUM(CASE WHEN decision_result = 'accepted' THEN covered_by_allowance END), 0),
                       COALESCE(SUM(CASE WHEN decision_result = 'accepted' THEN covered_by_extra END), 0),
                       SUM(CASE WHEN decision_result = 'accepted' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN decision_result = 'rejected' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN decision_result = 'conflict' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN decision_result = 'invalid' THEN 1 ELSE 0 END)
                FROM audit_records
                WHERE record_type = 'usage-decision'
                  AND occurred_at_unix_ms >= @from AND occurred_at_unix_ms < @to
                GROUP BY subscription_id
                """;
            command.Parameters.AddWithValue("@from", fromMs);
            command.Parameters.AddWithValue("@to", toMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    unattributedInvalidCount = (int)reader.GetInt64(8);
                    continue;
                }

                var row = Row(reader.GetString(0));
                row.AcceptedCredits = reader.GetInt64(1);
                row.RejectedCredits = reader.GetInt64(2);
                row.CoveredByAllowance = reader.GetInt64(3);
                row.CoveredByExtra = reader.GetInt64(4);
                row.AcceptedCount = (int)reader.GetInt64(5);
                row.RejectedCount = (int)reader.GetInt64(6);
                row.ConflictCount = (int)reader.GetInt64(7);
                row.InvalidCount = (int)reader.GetInt64(8);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT subscription_id, COUNT(*)
                FROM audit_records
                WHERE record_type = 'manual-correction'
                  AND occurred_at_unix_ms >= @from AND occurred_at_unix_ms < @to
                GROUP BY subscription_id
                """;
            command.Parameters.AddWithValue("@from", fromMs);
            command.Parameters.AddWithValue("@to", toMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    Row(reader.GetString(0)).ManualCorrectionCount = (int)reader.GetInt64(1);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT subscription_id,
                       COALESCE(SUM(CASE WHEN record_type IN ('extra-pool-seed', 'extra-pool-adjustment')
                                          AND extra_pool_delta > 0 THEN extra_pool_delta END), 0),
                       COALESCE(SUM(CASE WHEN record_type = 'extra-pool-adjustment'
                                          AND extra_pool_delta < 0 THEN extra_pool_delta END), 0)
                FROM audit_records
                WHERE occurred_at_unix_ms >= @from AND occurred_at_unix_ms < @to
                  AND subscription_id IS NOT NULL
                GROUP BY subscription_id
                """;
            command.Parameters.AddWithValue("@from", fromMs);
            command.Parameters.AddWithValue("@to", toMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = Row(reader.GetString(0));
                row.ExtraPoolAdded = reader.GetInt64(1);
                row.ExtraPoolAdjusted = reader.GetInt64(2);
            }
        }

        // Balance boundaries derive from the last pool-affecting record before the
        // period start/end; every pool change carries extra_pool_balance_after.
        foreach (var (boundaryMs, setBeginning) in new[] { (fromMs, true), (toMs, false) })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT subscription_id, extra_pool_balance_after
                FROM (
                    SELECT subscription_id, extra_pool_balance_after,
                           ROW_NUMBER() OVER (
                               PARTITION BY subscription_id
                               ORDER BY occurred_at_unix_ms DESC, id DESC) AS rn
                    FROM audit_records
                    WHERE extra_pool_balance_after IS NOT NULL
                      AND occurred_at_unix_ms < @boundary
                      AND subscription_id IS NOT NULL)
                WHERE rn = 1
                """;
            command.Parameters.AddWithValue("@boundary", boundaryMs);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = Row(reader.GetString(0));
                if (setBeginning)
                {
                    row.ExtraPoolBeginning = reader.GetInt64(1);
                }
                else
                {
                    row.ExtraPoolEnding = reader.GetInt64(1);
                }
            }
        }

        transaction.Commit();

        var subscriptionRows = rows.Values
            .OrderBy(r => r.SubscriptionId, StringComparer.Ordinal)
            .Select(r => new ReconciliationSubscriptionRow
            {
                SubscriptionId = r.SubscriptionId,
                UserId = r.UserId,
                AcceptedCredits = r.AcceptedCredits,
                RejectedCredits = r.RejectedCredits,
                CoveredBySubscriptionAllowanceCredits = r.CoveredByAllowance,
                CoveredByExtraPoolCredits = r.CoveredByExtra,
                ExtraPoolBeginningBalance = r.ExtraPoolBeginning,
                ExtraPoolAddedCredits = r.ExtraPoolAdded,
                ExtraPoolConsumedCredits = r.CoveredByExtra,
                ExtraPoolAdjustedCredits = r.ExtraPoolAdjusted,
                // A subscription with no pool-affecting record before the period end
                // keeps its beginning balance through the whole period.
                ExtraPoolEndingBalance = r.ExtraPoolEnding ?? r.ExtraPoolBeginning,
                AcceptedRequestCount = r.AcceptedCount,
                RejectedRequestCount = r.RejectedCount,
                ConflictCount = r.ConflictCount,
                InvalidRequestCount = r.InvalidCount,
                ManualCorrectionCount = r.ManualCorrectionCount,
            })
            .ToList();

        return new ReconciliationReport
        {
            PeriodFromInclusive = fromInclusive,
            PeriodToExclusive = toExclusive,
            Subscriptions = subscriptionRows,
            UnattributedInvalidRequestCount = unattributedInvalidCount,
        };
    }

    private sealed class MutableRow
    {
        public required string SubscriptionId { get; init; }
        public string? UserId { get; set; }
        public long AcceptedCredits { get; set; }
        public long RejectedCredits { get; set; }
        public long CoveredByAllowance { get; set; }
        public long CoveredByExtra { get; set; }
        public long ExtraPoolBeginning { get; set; }
        public long ExtraPoolAdded { get; set; }
        public long ExtraPoolAdjusted { get; set; }
        public long? ExtraPoolEnding { get; set; }
        public int AcceptedCount { get; set; }
        public int RejectedCount { get; set; }
        public int ConflictCount { get; set; }
        public int InvalidCount { get; set; }
        public int ManualCorrectionCount { get; set; }
    }
}
