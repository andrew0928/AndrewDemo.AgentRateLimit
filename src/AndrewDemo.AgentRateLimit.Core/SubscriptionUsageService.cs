using System.Security.Cryptography;
using System.Text;
using AndrewDemo.AgentRateLimit.Abstract;
using AndrewDemo.AgentRateLimit.Core.Storage;

namespace AndrewDemo.AgentRateLimit.Core;

public sealed class SubscriptionUsageService
{
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    private readonly string _databasePath;

    public SubscriptionUsageService(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        _databasePath = databasePath;
        Initialize();
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath)) ?? ".");
        using var database = SqliteDatabase.Open(_databasePath);
        database.ExecuteScript(Schema.Script);
    }

    public void CreateOrReplaceSubscription(
        string userId,
        string subscriptionId,
        long fiveHourLimit,
        long sevenDayLimit,
        long extraPoolCredits,
        bool disabled = false,
        DateTimeOffset? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (fiveHourLimit < 0 || sevenDayLimit < 0 || extraPoolCredits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraPoolCredits), "Limits and extra pool must be non-negative.");
        }

        var now = ToTicks(time ?? DateTimeOffset.UtcNow);
        using var database = SqliteDatabase.Open(_databasePath);
        database.ExecuteNonQuery("BEGIN IMMEDIATE;");
        try
        {
            database.ExecuteNonQuery(
                """
                INSERT INTO subscriptions (
                    subscription_id, user_id, state, five_hour_limit, seven_day_limit, extra_pool_remaining
                )
                VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(subscription_id) DO UPDATE SET
                    user_id = excluded.user_id,
                    state = excluded.state,
                    five_hour_limit = excluded.five_hour_limit,
                    seven_day_limit = excluded.seven_day_limit,
                    extra_pool_remaining = excluded.extra_pool_remaining;
                """,
                subscriptionId,
                userId,
                disabled ? "disabled" : "active",
                fiveHourLimit,
                sevenDayLimit,
                extraPoolCredits);

            InsertAudit(
                database,
                now,
                "extra-pool-change",
                userId,
                subscriptionId,
                null,
                0,
                0,
                null,
                "initial-balance",
                null,
                null,
                "system",
                "seed",
                extraPoolCredits,
                extraPoolCredits,
                null);

            database.ExecuteNonQuery("COMMIT;");
        }
        catch
        {
            database.ExecuteNonQuery("ROLLBACK;");
            throw;
        }
    }

    public UsageDecision ConsumeUsage(UsageRequest request, DateTimeOffset? decisionTime = null)
    {
        var now = ToTicks(decisionTime ?? DateTimeOffset.UtcNow);
        using var database = SqliteDatabase.Open(_databasePath);
        database.ExecuteNonQuery("BEGIN IMMEDIATE;");
        try
        {
            var decision = DecideUsage(database, request, now, persist: true);
            database.ExecuteNonQuery("COMMIT;");
            return decision;
        }
        catch
        {
            database.ExecuteNonQuery("ROLLBACK;");
            throw;
        }
    }

    public UsageDecision PreviewUsage(UsageRequest request, DateTimeOffset? decisionTime = null)
    {
        var now = ToTicks(decisionTime ?? DateTimeOffset.UtcNow);
        using var database = SqliteDatabase.Open(_databasePath);
        return DecideUsage(database, request, now, persist: false);
    }

    public UsageStatus GetUsageStatus(string userId, string subscriptionId, DateTimeOffset? asOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var now = ToTicks(asOf ?? DateTimeOffset.UtcNow);
        using var database = SqliteDatabase.Open(_databasePath);
        var subscription = LoadSubscription(database, subscriptionId);
        if (subscription is null)
        {
            throw new InvalidOperationException($"Subscription not found: {subscriptionId}");
        }

        if (!string.Equals(subscription.UserId, userId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("User and subscription do not match.");
        }

        return BuildUsageStatus(database, subscription, now);
    }

    public IReadOnlyList<AuditRecord> QueryAuditTrail(
        string? userId = null,
        string? subscriptionId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        using var database = SqliteDatabase.Open(_databasePath);
        var fromTicks = from is null ? long.MinValue : ToTicks(from.Value);
        var toTicks = to is null ? long.MaxValue : ToTicks(to.Value);
        return database.Query(
            """
            SELECT audit_id, time_ticks, record_type, user_id, subscription_id, requested_credits,
                   covered_by_allowance, covered_by_extra, decision_result, reason, correlation_id,
                   idempotency_key, actor, source, changed_credits, resulting_extra_pool, payload_hash
            FROM audit_records
            WHERE time_ticks >= ?
              AND time_ticks <= ?
              AND (? IS NULL OR user_id = ?)
              AND (? IS NULL OR subscription_id = ?)
            ORDER BY audit_id;
            """,
            row => MapAuditRecord(row),
            fromTicks,
            toTicks,
            string.IsNullOrWhiteSpace(userId) ? null : userId,
            string.IsNullOrWhiteSpace(userId) ? null : userId,
            string.IsNullOrWhiteSpace(subscriptionId) ? null : subscriptionId,
            string.IsNullOrWhiteSpace(subscriptionId) ? null : subscriptionId);
    }

    public AuditRecord AddExtraPoolCredits(
        string userId,
        string subscriptionId,
        long credits,
        string actor,
        string reason,
        DateTimeOffset? time = null)
    {
        if (credits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(credits), "Added credits must be positive.");
        }

        return ChangeExtraPool(
            userId,
            subscriptionId,
            credits,
            actor,
            reason,
            "extra-pool-change",
            time ?? DateTimeOffset.UtcNow);
    }

    public AuditRecord RecordManualCorrection(
        string userId,
        string subscriptionId,
        long creditDelta,
        string actor,
        string reason,
        DateTimeOffset? time = null)
    {
        if (creditDelta == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(creditDelta), "Correction delta must not be zero.");
        }

        return ChangeExtraPool(
            userId,
            subscriptionId,
            creditDelta,
            actor,
            reason,
            "manual-correction",
            time ?? DateTimeOffset.UtcNow);
    }

    public ReconciliationReport ExportReconciliationReport(
        string subscriptionId,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (to < from)
        {
            throw new ArgumentException("Report end time must be greater than or equal to start time.", nameof(to));
        }

        var fromTicks = ToTicks(from);
        var toTicks = ToTicks(to);
        using var database = SqliteDatabase.Open(_databasePath);

        var acceptedCredits = SumLong(
            database,
            "SELECT COALESCE(SUM(requested_credits), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var rejectedCredits = SumLong(
            database,
            "SELECT COALESCE(SUM(requested_credits), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'rejected' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var allowanceCovered = SumLong(
            database,
            "SELECT COALESCE(SUM(covered_by_allowance), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var extraCovered = SumLong(
            database,
            "SELECT COALESCE(SUM(covered_by_extra), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var beginning = SumLong(
            database,
            "SELECT COALESCE(SUM(changed_credits), 0) FROM audit_records WHERE subscription_id = ? AND time_ticks < ?;",
            subscriptionId,
            fromTicks);
        var added = SumLong(
            database,
            "SELECT COALESCE(SUM(changed_credits), 0) FROM audit_records WHERE subscription_id = ? AND record_type = 'extra-pool-change' AND changed_credits > 0 AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var consumed = -SumLong(
            database,
            "SELECT COALESCE(SUM(changed_credits), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND changed_credits < 0 AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var adjusted = SumLong(
            database,
            "SELECT COALESCE(SUM(changed_credits), 0) FROM audit_records WHERE subscription_id = ? AND record_type = 'manual-correction' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var conflictCount = SumLong(
            database,
            "SELECT COUNT(*) FROM audit_records WHERE subscription_id = ? AND decision_result = 'conflict' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var invalidCount = SumLong(
            database,
            "SELECT COUNT(*) FROM audit_records WHERE subscription_id = ? AND decision_result = 'invalid' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);
        var manualCorrectionCount = SumLong(
            database,
            "SELECT COUNT(*) FROM audit_records WHERE subscription_id = ? AND record_type = 'manual-correction' AND time_ticks >= ? AND time_ticks <= ?;",
            subscriptionId,
            fromTicks,
            toTicks);

        return new ReconciliationReport(
            subscriptionId,
            from,
            to,
            acceptedCredits,
            rejectedCredits,
            allowanceCovered,
            extraCovered,
            beginning,
            added,
            consumed,
            adjusted,
            beginning + added - consumed + adjusted,
            conflictCount,
            invalidCount,
            manualCorrectionCount);
    }

    private UsageDecision DecideUsage(SqliteDatabase database, UsageRequest request, long now, bool persist)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return Invalid(database, request, now, InvalidReasons.MissingUserId, persist);
        }

        if (string.IsNullOrWhiteSpace(request.SubscriptionId))
        {
            return Invalid(database, request, now, InvalidReasons.MissingSubscriptionId, persist);
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Invalid(database, request, now, InvalidReasons.MissingIdempotencyKey, persist);
        }

        if (request.RequestedCredits != decimal.Truncate(request.RequestedCredits))
        {
            return Invalid(database, request, now, InvalidReasons.CreditsNotInteger, persist);
        }

        if (request.RequestedCredits <= 0)
        {
            return Invalid(database, request, now, InvalidReasons.CreditsNotPositive, persist);
        }

        var requestedCredits = decimal.ToInt64(request.RequestedCredits);
        var payloadHash = ComputePayloadHash(request);

        if (persist)
        {
            var previous = LoadUsageRequest(database, request.SubscriptionId, request.IdempotencyKey);
            if (previous is not null)
            {
                if (string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    return previous.Decision;
                }

                var conflictAuditId = InsertAudit(
                    database,
                    now,
                    "usage-decision",
                    request.UserId,
                    request.SubscriptionId,
                    requestedCredits,
                    0,
                    0,
                    DecisionResults.Conflict,
                    ConflictReasons.IdempotencyKeyPayloadMismatch,
                    request.CorrelationId,
                    request.IdempotencyKey,
                    null,
                    "consume",
                    0,
                    null,
                    payloadHash);

                return new UsageDecision(
                    DecisionResults.Conflict,
                    request.RequestedCredits,
                    0,
                    0,
                    previous.Decision.RemainingFiveHourCreditsAfterDecision,
                    previous.Decision.RemainingSevenDayCreditsAfterDecision,
                    previous.Decision.RemainingExtraPoolCreditsAfterDecision,
                    ConflictReasons.IdempotencyKeyPayloadMismatch,
                    conflictAuditId.ToString());
            }
        }

        var subscription = LoadSubscription(database, request.SubscriptionId);
        if (subscription is null)
        {
            return Reject(database, request, now, requestedCredits, RejectionReasons.SubscriptionNotFound, persist, payloadHash);
        }

        if (!string.Equals(subscription.UserId, request.UserId, StringComparison.Ordinal))
        {
            return Reject(database, request, now, requestedCredits, RejectionReasons.UserSubscriptionMismatch, persist, payloadHash, subscription);
        }

        if (string.Equals(subscription.State, "disabled", StringComparison.Ordinal))
        {
            return Reject(database, request, now, requestedCredits, RejectionReasons.SubscriptionDisabled, persist, payloadHash, subscription);
        }

        var statusBefore = BuildUsageStatus(database, subscription, now);
        var allowance = Math.Min(
            statusBefore.FiveHourWindowRemainingCredits,
            statusBefore.SevenDayWindowRemainingCredits);
        var coveredByAllowance = Math.Min(requestedCredits, allowance);
        var coveredByExtra = requestedCredits - coveredByAllowance;
        if (coveredByExtra > statusBefore.ExtraPoolRemainingCredits)
        {
            return Reject(database, request, now, requestedCredits, RejectionReasons.InsufficientCredits, persist, payloadHash, subscription);
        }

        var remainingExtra = statusBefore.ExtraPoolRemainingCredits - coveredByExtra;
        var remainingFiveHour = Math.Max(0, statusBefore.FiveHourWindowRemainingCredits - requestedCredits);
        var remainingSevenDay = Math.Max(0, statusBefore.SevenDayWindowRemainingCredits - requestedCredits);
        long? auditId = null;

        if (persist)
        {
            database.ExecuteNonQuery(
                "UPDATE subscriptions SET extra_pool_remaining = ? WHERE subscription_id = ?;",
                remainingExtra,
                request.SubscriptionId);

            auditId = InsertAudit(
                database,
                now,
                "usage-decision",
                request.UserId,
                request.SubscriptionId,
                requestedCredits,
                coveredByAllowance,
                coveredByExtra,
                DecisionResults.Accepted,
                null,
                request.CorrelationId,
                request.IdempotencyKey,
                null,
                "consume",
                -coveredByExtra,
                remainingExtra,
                payloadHash);
        }

        var decision = new UsageDecision(
            DecisionResults.Accepted,
            request.RequestedCredits,
            coveredByAllowance,
            coveredByExtra,
            remainingFiveHour,
            remainingSevenDay,
            remainingExtra,
            null,
            auditId?.ToString());

        if (persist)
        {
            InsertUsageRequest(database, request, payloadHash, decision);
        }

        return decision;
    }

    private UsageDecision Invalid(SqliteDatabase database, UsageRequest request, long now, string reason, bool persist)
    {
        long? auditId = null;
        if (persist)
        {
            auditId = InsertAudit(
                database,
                now,
                "usage-decision",
                request.UserId,
                request.SubscriptionId,
                null,
                0,
                0,
                DecisionResults.Invalid,
                reason,
                request.CorrelationId,
                request.IdempotencyKey,
                null,
                "consume",
                0,
                null,
                null);
        }

        return new UsageDecision(
            DecisionResults.Invalid,
            request.RequestedCredits,
            0,
            0,
            0,
            0,
            0,
            reason,
            auditId?.ToString());
    }

    private UsageDecision Reject(
        SqliteDatabase database,
        UsageRequest request,
        long now,
        long requestedCredits,
        string reason,
        bool persist,
        string payloadHash,
        SubscriptionRow? subscription = null)
    {
        var remainingFive = 0L;
        var remainingSeven = 0L;
        var remainingExtra = 0L;
        if (subscription is not null)
        {
            var status = BuildUsageStatus(database, subscription, now);
            remainingFive = status.FiveHourWindowRemainingCredits;
            remainingSeven = status.SevenDayWindowRemainingCredits;
            remainingExtra = status.ExtraPoolRemainingCredits;
        }

        long? auditId = null;
        if (persist)
        {
            auditId = InsertAudit(
                database,
                now,
                "usage-decision",
                request.UserId,
                request.SubscriptionId,
                requestedCredits,
                0,
                0,
                DecisionResults.Rejected,
                reason,
                request.CorrelationId,
                request.IdempotencyKey,
                null,
                "consume",
                0,
                remainingExtra,
                payloadHash);
        }

        var decision = new UsageDecision(
            DecisionResults.Rejected,
            request.RequestedCredits,
            0,
            0,
            remainingFive,
            remainingSeven,
            remainingExtra,
            reason,
            auditId?.ToString());

        if (persist && subscription is not null && !string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            InsertUsageRequest(database, request, payloadHash, decision);
        }

        return decision;
    }

    private AuditRecord ChangeExtraPool(
        string userId,
        string subscriptionId,
        long creditDelta,
        string actor,
        string reason,
        string recordType,
        DateTimeOffset time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = ToTicks(time);
        using var database = SqliteDatabase.Open(_databasePath);
        database.ExecuteNonQuery("BEGIN IMMEDIATE;");
        try
        {
            var subscription = LoadSubscription(database, subscriptionId)
                ?? throw new InvalidOperationException($"Subscription not found: {subscriptionId}");
            if (!string.Equals(subscription.UserId, userId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("User and subscription do not match.");
            }

            var resulting = subscription.ExtraPoolRemaining + creditDelta;
            if (resulting < 0)
            {
                throw new InvalidOperationException("Extra pool cannot become negative.");
            }

            database.ExecuteNonQuery(
                "UPDATE subscriptions SET extra_pool_remaining = ? WHERE subscription_id = ?;",
                resulting,
                subscriptionId);
            var auditId = InsertAudit(
                database,
                now,
                recordType,
                userId,
                subscriptionId,
                null,
                0,
                0,
                null,
                reason,
                null,
                null,
                actor,
                recordType,
                creditDelta,
                resulting,
                null);
            database.ExecuteNonQuery("COMMIT;");

            return QueryAuditTrail(userId, subscriptionId)
                .Single(record => record.AuditId == auditId);
        }
        catch
        {
            database.ExecuteNonQuery("ROLLBACK;");
            throw;
        }
    }

    private static UsageStatus BuildUsageStatus(SqliteDatabase database, SubscriptionRow subscription, long now)
    {
        var fiveHourStart = now - FiveHourWindow.Ticks;
        var sevenDayStart = now - SevenDayWindow.Ticks;
        var fiveHourUsed = SumLong(
            database,
            "SELECT COALESCE(SUM(requested_credits), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks > ? AND time_ticks <= ?;",
            subscription.SubscriptionId,
            fiveHourStart,
            now);
        var sevenDayUsed = SumLong(
            database,
            "SELECT COALESCE(SUM(requested_credits), 0) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks > ? AND time_ticks <= ?;",
            subscription.SubscriptionId,
            sevenDayStart,
            now);
        var fiveHourNextReset = MinNullableLong(
            database,
            "SELECT MIN(time_ticks) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks > ? AND time_ticks <= ?;",
            subscription.SubscriptionId,
            fiveHourStart,
            now);
        var sevenDayNextReset = MinNullableLong(
            database,
            "SELECT MIN(time_ticks) FROM audit_records WHERE subscription_id = ? AND decision_result = 'accepted' AND time_ticks > ? AND time_ticks <= ?;",
            subscription.SubscriptionId,
            sevenDayStart,
            now);

        return new UsageStatus(
            subscription.UserId,
            subscription.SubscriptionId,
            subscription.FiveHourLimit,
            fiveHourUsed,
            Math.Max(0, subscription.FiveHourLimit - fiveHourUsed),
            fiveHourNextReset is null ? null : FromTicks(fiveHourNextReset.Value + FiveHourWindow.Ticks),
            subscription.SevenDayLimit,
            sevenDayUsed,
            Math.Max(0, subscription.SevenDayLimit - sevenDayUsed),
            sevenDayNextReset is null ? null : FromTicks(sevenDayNextReset.Value + SevenDayWindow.Ticks),
            subscription.ExtraPoolRemaining);
    }

    private static SubscriptionRow? LoadSubscription(SqliteDatabase database, string subscriptionId)
    {
        return database.Query(
            """
            SELECT subscription_id, user_id, state, five_hour_limit, seven_day_limit, extra_pool_remaining
            FROM subscriptions
            WHERE subscription_id = ?;
            """,
            row => new SubscriptionRow(
                row.GetString("subscription_id"),
                row.GetString("user_id"),
                row.GetString("state"),
                row.GetInt64("five_hour_limit"),
                row.GetInt64("seven_day_limit"),
                row.GetInt64("extra_pool_remaining")),
            subscriptionId).SingleOrDefault();
    }

    private static StoredUsageRequest? LoadUsageRequest(
        SqliteDatabase database,
        string subscriptionId,
        string idempotencyKey)
    {
        return database.Query(
            """
            SELECT payload_hash, decision_result, requested_credits, covered_by_allowance, covered_by_extra,
                   remaining_five_hour, remaining_seven_day, remaining_extra_pool, reason, audit_id
            FROM usage_requests
            WHERE subscription_id = ?
              AND idempotency_key = ?;
            """,
            row => new StoredUsageRequest(
                row.GetString("payload_hash"),
                new UsageDecision(
                    row.GetString("decision_result"),
                    row.GetInt64("requested_credits"),
                    row.GetInt64("covered_by_allowance"),
                    row.GetInt64("covered_by_extra"),
                    row.GetInt64("remaining_five_hour"),
                    row.GetInt64("remaining_seven_day"),
                    row.GetInt64("remaining_extra_pool"),
                    row.GetNullableString("reason"),
                    row.GetInt64("audit_id").ToString())),
            subscriptionId,
            idempotencyKey).SingleOrDefault();
    }

    private static void InsertUsageRequest(
        SqliteDatabase database,
        UsageRequest request,
        string payloadHash,
        UsageDecision decision)
    {
        database.ExecuteNonQuery(
            """
            INSERT INTO usage_requests (
                subscription_id, idempotency_key, user_id, payload_hash, decision_result, requested_credits,
                covered_by_allowance, covered_by_extra, remaining_five_hour, remaining_seven_day,
                remaining_extra_pool, reason, audit_id
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """,
            request.SubscriptionId,
            request.IdempotencyKey,
            request.UserId,
            payloadHash,
            decision.Result,
            decimal.ToInt64(decision.RequestedCredits),
            decision.CreditsCoveredBySubscriptionWindowAllowance,
            decision.CreditsCoveredByExtraPool,
            decision.RemainingFiveHourCreditsAfterDecision,
            decision.RemainingSevenDayCreditsAfterDecision,
            decision.RemainingExtraPoolCreditsAfterDecision,
            decision.Reason,
            long.Parse(decision.AuditReference ?? "0"));
    }

    private static long InsertAudit(
        SqliteDatabase database,
        long timeTicks,
        string recordType,
        string? userId,
        string? subscriptionId,
        long? requestedCredits,
        long coveredByAllowance,
        long coveredByExtra,
        string? decisionResult,
        string? reason,
        string? correlationId,
        string? idempotencyKey,
        string? actor,
        string? source,
        long changedCredits,
        long? resultingExtraPool,
        string? payloadHash)
    {
        database.ExecuteNonQuery(
            """
            INSERT INTO audit_records (
                time_ticks, record_type, user_id, subscription_id, requested_credits,
                covered_by_allowance, covered_by_extra, decision_result, reason, correlation_id,
                idempotency_key, actor, source, changed_credits, resulting_extra_pool, payload_hash
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """,
            timeTicks,
            recordType,
            userId,
            subscriptionId,
            requestedCredits,
            coveredByAllowance,
            coveredByExtra,
            decisionResult,
            reason,
            correlationId,
            idempotencyKey,
            actor,
            source,
            changedCredits,
            resultingExtraPool,
            payloadHash);
        return database.LastInsertRowId;
    }

    private static AuditRecord MapAuditRecord(SqliteRow row)
    {
        return new AuditRecord(
            row.GetInt64("audit_id"),
            FromTicks(row.GetInt64("time_ticks")),
            row.GetString("record_type"),
            row.GetNullableString("user_id"),
            row.GetNullableString("subscription_id"),
            row.GetNullableInt64("requested_credits"),
            row.GetInt64("covered_by_allowance"),
            row.GetInt64("covered_by_extra"),
            row.GetNullableString("decision_result"),
            row.GetNullableString("reason"),
            row.GetNullableString("correlation_id"),
            row.GetNullableString("idempotency_key"),
            row.GetNullableString("actor"),
            row.GetNullableString("source"),
            row.GetInt64("changed_credits"),
            row.GetNullableInt64("resulting_extra_pool"));
    }

    private static string ComputePayloadHash(UsageRequest request)
    {
        var payload = string.Join(
            "|",
            request.UserId?.Trim() ?? string.Empty,
            request.SubscriptionId?.Trim() ?? string.Empty,
            request.RequestedCredits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static long SumLong(SqliteDatabase database, string sql, params object?[] parameters)
    {
        return database.Query(sql, row => row.GetInt64(0), parameters).Single();
    }

    private static long? MinNullableLong(SqliteDatabase database, string sql, params object?[] parameters)
    {
        return database.Query(sql, row => row.GetNullableInt64(0), parameters).Single();
    }

    private static long ToTicks(DateTimeOffset time)
    {
        return time.ToUniversalTime().Ticks;
    }

    private static DateTimeOffset FromTicks(long ticks)
    {
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private sealed record SubscriptionRow(
        string SubscriptionId,
        string UserId,
        string State,
        long FiveHourLimit,
        long SevenDayLimit,
        long ExtraPoolRemaining);

    private sealed record StoredUsageRequest(string PayloadHash, UsageDecision Decision);
}
