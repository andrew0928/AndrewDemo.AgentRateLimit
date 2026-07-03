using AndrewDemo.AgentRateLimit.Abstract.SubscriptionCredit;
using Microsoft.Data.Sqlite;

namespace AndrewDemo.AgentRateLimit.Core.SubscriptionCredit;

public sealed partial class SqliteSubscriptionCreditService
{
    public async Task<UsageDecision> ConsumeAsync(UsageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = OpenConnection();
        await using var transaction = BeginWrite(connection);

        // Decision time is read while the write lock is held: no other writer can
        // commit usage that this decision would then fail to observe.
        var now = _timeProvider.GetUtcNow();
        var decision = Evaluate(connection, transaction, request, now, persist: true);

        transaction.Commit();
        return decision;
    }

    public async Task<UsageDecision> PreviewAsync(UsageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = OpenConnection();
        await using var transaction = BeginRead(connection);

        // Same decision pipeline as consume, with persistence disabled: previews change
        // no usage totals, no extra pool balance, no idempotency bindings, and are not
        // accounting records (spec 3.2), so they carry no audit reference.
        var now = _timeProvider.GetUtcNow();
        var decision = Evaluate(connection, transaction, request, now, persist: false);

        transaction.Commit();
        return decision;
    }

    /// <summary>
    /// The usage decision pipeline, evaluated in this order:
    /// 1. request validation (invalid decisions, spec 4.1 / 7.3);
    /// 2. idempotency lookup (replay or conflict, spec 4.6) — before subscription state
    ///    checks so a stored decision replays even after the state changed;
    /// 3. ownership checks (not-found / mismatch rejections, spec 4.5) — these bind no
    ///    idempotency key and disclose no balances;
    /// 4. ledger-clamped decision time, then disabled check and capacity check against
    ///    windows and extra pool (spec 4.3 / 4.4).
    /// When <paramref name="persist"/> is true, decisions write audit records and
    /// ownership-verified accepted/rejected outcomes bind the idempotency key.
    /// </summary>
    private UsageDecision Evaluate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageRequest request,
        DateTimeOffset now,
        bool persist)
    {
        var nowMs = ToUnixMs(now);

        var invalidReason = ValidateRequest(request);
        if (invalidReason is not null)
        {
            return MakeInvalidDecision(connection, transaction, request, now, invalidReason, persist);
        }

        var requested = (long)request.RequestedCredits;
        var subscriptionId = request.SubscriptionId!;
        var fingerprint = ComputePayloadFingerprint(request, requested);

        var stored = LookupIdempotencyRecord(connection, transaction, subscriptionId, request.IdempotencyKey!);
        if (stored is not null)
        {
            if (stored.PayloadFingerprint == fingerprint)
            {
                return ReplayStoredDecision(stored, request);
            }

            return MakeConflictDecision(connection, transaction, request, now, requested, persist);
        }

        var subscription = LoadSubscription(connection, transaction, subscriptionId);
        if (subscription is null)
        {
            // Not bound to the idempotency key: there is no subscription whose
            // keyspace this caller may occupy.
            return MakeRejectedDecision(
                connection, transaction, request, now, requested,
                UsageDecisionReasons.SubscriptionNotFound,
                remaining5h: null, remaining7d: null, extraPool: null,
                fingerprint: null, persist);
        }

        if (subscription.UserId != request.UserId)
        {
            // No idempotency binding and no balance disclosure: the caller has no
            // ownership relation to this subscription (spec section 5), and binding
            // would let a non-owner poison the owner's key space.
            return MakeRejectedDecision(
                connection, transaction, request, now, requested,
                UsageDecisionReasons.UserSubscriptionMismatch,
                remaining5h: null, remaining7d: null, extraPool: null,
                fingerprint: null, persist);
        }

        // Ownership verified: from here decisions bind the idempotency key and
        // accounting uses the ledger-clamped decision time.
        nowMs = ClampToLedgerTime(connection, transaction, subscriptionId, nowMs);
        now = FromUnixMs(nowMs);

        var usage = QueryWindowUsage(connection, transaction, subscriptionId, nowMs);
        long remaining5h = Math.Max(0, subscription.Limit5h - usage.Used5h);
        long remaining7d = Math.Max(0, subscription.Limit7d - usage.Used7d);

        if (!subscription.Enabled)
        {
            return MakeRejectedDecision(
                connection, transaction, request, now, requested,
                UsageDecisionReasons.SubscriptionDisabled,
                remaining5h, remaining7d, subscription.ExtraPoolBalance,
                fingerprint, persist);
        }

        // Subscription allowance is limited by both windows: the smaller remaining wins
        // (spec 4.3). The shortfall must be fully coverable by the extra pool (4.4);
        // otherwise the request is rejected and consumes nothing (4.5).
        long allowanceCoverable = Math.Min(remaining5h, remaining7d);
        long coveredByAllowance = Math.Min(requested, allowanceCoverable);
        long shortfall = requested - coveredByAllowance;

        if (shortfall > subscription.ExtraPoolBalance)
        {
            return MakeRejectedDecision(
                connection, transaction, request, now, requested,
                UsageDecisionReasons.InsufficientCredits,
                remaining5h, remaining7d, subscription.ExtraPoolBalance,
                fingerprint, persist);
        }

        long coveredByExtra = shortfall;
        long newExtraBalance = subscription.ExtraPoolBalance - coveredByExtra;
        long remaining5hAfter = Math.Max(0, subscription.Limit5h - SaturatingAdd(usage.Used5h, requested));
        long remaining7dAfter = Math.Max(0, subscription.Limit7d - SaturatingAdd(usage.Used7d, requested));

        var auditId = NewAuditId();
        var decision = new UsageDecision
        {
            Result = UsageDecisionResult.Accepted,
            UserId = request.UserId,
            SubscriptionId = subscriptionId,
            RequestedCredits = requested,
            CoveredBySubscriptionAllowance = coveredByAllowance,
            CoveredByExtraPool = coveredByExtra,
            Remaining5hCreditsAfterDecision = remaining5hAfter,
            Remaining7dCreditsAfterDecision = remaining7dAfter,
            RemainingExtraPoolCreditsAfterDecision = newExtraBalance,
            DecisionTime = now,
            AuditReference = persist ? auditId : null,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
        };

        if (persist)
        {
            if (coveredByExtra > 0)
            {
                UpdateExtraPoolBalance(connection, transaction, subscriptionId, newExtraBalance);
            }

            InsertAuditRecord(connection, transaction, new AuditRecordFields
            {
                AuditId = auditId,
                RecordType = AuditRecordTypeNames.UsageDecision,
                OccurredAtUnixMs = nowMs,
                UserId = request.UserId,
                SubscriptionId = subscriptionId,
                Credits = requested,
                CoveredByAllowance = coveredByAllowance,
                CoveredByExtra = coveredByExtra,
                DecisionResult = UsageDecisionResultNames.Accepted,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                Actor = CallerActor(request),
                ExtraPoolDelta = coveredByExtra > 0 ? -coveredByExtra : null,
                ExtraPoolBalanceAfter = coveredByExtra > 0 ? newExtraBalance : null,
            });

            InsertIdempotencyRecord(connection, transaction, decision, fingerprint, auditId);
        }

        return decision;
    }

    /// <summary>
    /// Validation order: identity fields first, then credit format (spec 7.3).
    /// Whitespace-only identifiers count as missing.
    /// </summary>
    private static string? ValidateRequest(UsageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return UsageDecisionReasons.MissingUserId;
        }

        if (string.IsNullOrWhiteSpace(request.SubscriptionId))
        {
            return UsageDecisionReasons.MissingSubscriptionId;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return UsageDecisionReasons.MissingIdempotencyKey;
        }

        if (decimal.Truncate(request.RequestedCredits) != request.RequestedCredits)
        {
            return UsageDecisionReasons.CreditsNotInteger;
        }

        if (request.RequestedCredits <= 0)
        {
            return UsageDecisionReasons.CreditsNotPositive;
        }

        if (request.RequestedCredits > SubscriptionCreditBounds.MaxCreditAmount)
        {
            return UsageDecisionReasons.CreditsOutOfRange;
        }

        return null;
    }

    /// <summary>Actor recorded on usage-decision audit records: the requesting user.</summary>
    private static string CallerActor(UsageRequest request)
        => NormalizeId(request.UserId) ?? "unauthenticated-caller";

    private static long? EchoRequestedCredits(UsageRequest request)
    {
        var credits = request.RequestedCredits;
        if (decimal.Truncate(credits) != credits || credits > long.MaxValue || credits < long.MinValue)
        {
            return null;
        }

        return (long)credits;
    }

    private UsageDecision MakeInvalidDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageRequest request,
        DateTimeOffset now,
        string reason,
        bool persist)
    {
        var auditId = NewAuditId();
        if (persist)
        {
            InsertAuditRecord(connection, transaction, new AuditRecordFields
            {
                AuditId = auditId,
                RecordType = AuditRecordTypeNames.UsageDecision,
                OccurredAtUnixMs = ToUnixMs(now),
                UserId = NormalizeId(request.UserId),
                SubscriptionId = NormalizeId(request.SubscriptionId),
                Credits = EchoRequestedCredits(request),
                DecisionResult = UsageDecisionResultNames.Invalid,
                Reason = reason,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = NormalizeId(request.IdempotencyKey),
                Actor = CallerActor(request),
            });
        }

        return new UsageDecision
        {
            Result = UsageDecisionResult.Invalid,
            Reason = reason,
            UserId = request.UserId,
            SubscriptionId = request.SubscriptionId,
            RequestedCredits = EchoRequestedCredits(request),
            DecisionTime = now,
            AuditReference = persist ? auditId : null,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
        };
    }

    private static string? NormalizeId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id;

    private UsageDecision MakeConflictDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageRequest request,
        DateTimeOffset now,
        long requested,
        bool persist)
    {
        // A conflict changes no usage total and no extra pool balance (spec 4.6); the
        // decision reports the current remainings only when the caller owns the
        // subscription — a non-owner must not learn another user's balances (spec
        // section 5).
        var subscription = LoadSubscription(connection, transaction, request.SubscriptionId!);
        long? remaining5h = null, remaining7d = null, extraPool = null;
        if (subscription is not null && subscription.UserId == request.UserId)
        {
            var nowMs = ClampToLedgerTime(connection, transaction, subscription.SubscriptionId, ToUnixMs(now));
            var usage = QueryWindowUsage(connection, transaction, subscription.SubscriptionId, nowMs);
            remaining5h = Math.Max(0, subscription.Limit5h - usage.Used5h);
            remaining7d = Math.Max(0, subscription.Limit7d - usage.Used7d);
            extraPool = subscription.ExtraPoolBalance;
        }

        var auditId = NewAuditId();
        if (persist)
        {
            InsertAuditRecord(connection, transaction, new AuditRecordFields
            {
                AuditId = auditId,
                RecordType = AuditRecordTypeNames.UsageDecision,
                OccurredAtUnixMs = ToUnixMs(now),
                UserId = request.UserId,
                SubscriptionId = request.SubscriptionId,
                Credits = requested,
                DecisionResult = UsageDecisionResultNames.Conflict,
                Reason = UsageDecisionReasons.IdempotencyKeyPayloadMismatch,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                Actor = CallerActor(request),
            });
        }

        return new UsageDecision
        {
            Result = UsageDecisionResult.Conflict,
            Reason = UsageDecisionReasons.IdempotencyKeyPayloadMismatch,
            UserId = request.UserId,
            SubscriptionId = request.SubscriptionId,
            RequestedCredits = requested,
            Remaining5hCreditsAfterDecision = remaining5h,
            Remaining7dCreditsAfterDecision = remaining7d,
            RemainingExtraPoolCreditsAfterDecision = extraPool,
            DecisionTime = now,
            AuditReference = persist ? auditId : null,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
        };
    }

    private UsageDecision MakeRejectedDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageRequest request,
        DateTimeOffset now,
        long requested,
        string reason,
        long? remaining5h,
        long? remaining7d,
        long? extraPool,
        string? fingerprint,
        bool persist)
    {
        var auditId = NewAuditId();
        var decision = new UsageDecision
        {
            Result = UsageDecisionResult.Rejected,
            Reason = reason,
            UserId = request.UserId,
            SubscriptionId = request.SubscriptionId,
            RequestedCredits = requested,
            Remaining5hCreditsAfterDecision = remaining5h,
            Remaining7dCreditsAfterDecision = remaining7d,
            RemainingExtraPoolCreditsAfterDecision = extraPool,
            DecisionTime = now,
            AuditReference = persist ? auditId : null,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
        };

        if (persist)
        {
            InsertAuditRecord(connection, transaction, new AuditRecordFields
            {
                AuditId = auditId,
                RecordType = AuditRecordTypeNames.UsageDecision,
                OccurredAtUnixMs = ToUnixMs(now),
                UserId = request.UserId,
                SubscriptionId = request.SubscriptionId,
                Credits = requested,
                CoveredByAllowance = 0,
                CoveredByExtra = 0,
                DecisionResult = UsageDecisionResultNames.Rejected,
                Reason = reason,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                Actor = CallerActor(request),
            });

            // Ownership-verified rejections bind the idempotency key: spec 4.6
            // requires a resend with the same key and payload to return the original
            // decision, whatever that decision was. Callers with no ownership relation
            // (not-found, mismatch) pass a null fingerprint and bind nothing.
            if (fingerprint is not null)
            {
                InsertIdempotencyRecord(connection, transaction, decision, fingerprint, auditId);
            }
        }

        return decision;
    }

    private sealed record StoredIdempotencyRecord(
        string PayloadFingerprint,
        string UserId,
        string DecisionResult,
        string? Reason,
        long RequestedCredits,
        long CoveredByAllowance,
        long CoveredByExtra,
        long? Remaining5hAfter,
        long? Remaining7dAfter,
        long? ExtraPoolAfter,
        string? CorrelationId,
        long DecisionTimeUnixMs,
        string AuditId);

    private static StoredIdempotencyRecord? LookupIdempotencyRecord(
        SqliteConnection connection, SqliteTransaction transaction, string subscriptionId, string idempotencyKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_fingerprint, user_id, decision_result, reason, requested_credits,
                   covered_by_allowance, covered_by_extra, remaining_5h_after,
                   remaining_7d_after, extra_pool_after, correlation_id,
                   decision_time_unix_ms, audit_id
            FROM idempotency_records
            WHERE subscription_id = @sid AND idempotency_key = @key
            """;
        command.Parameters.AddWithValue("@sid", subscriptionId);
        command.Parameters.AddWithValue("@key", idempotencyKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StoredIdempotencyRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11),
            reader.GetString(12));
    }

    /// <summary>
    /// Replays the original decision for a same-key same-payload resend (spec 4.6):
    /// original values, original decision time, original audit reference. No new
    /// charge and no new accounting record is produced.
    /// </summary>
    private static UsageDecision ReplayStoredDecision(StoredIdempotencyRecord stored, UsageRequest request)
        => new()
        {
            Result = UsageDecisionResultNames.Parse(stored.DecisionResult),
            Reason = stored.Reason,
            UserId = stored.UserId,
            SubscriptionId = request.SubscriptionId,
            RequestedCredits = stored.RequestedCredits,
            CoveredBySubscriptionAllowance = stored.CoveredByAllowance,
            CoveredByExtraPool = stored.CoveredByExtra,
            Remaining5hCreditsAfterDecision = stored.Remaining5hAfter,
            Remaining7dCreditsAfterDecision = stored.Remaining7dAfter,
            RemainingExtraPoolCreditsAfterDecision = stored.ExtraPoolAfter,
            DecisionTime = FromUnixMs(stored.DecisionTimeUnixMs),
            AuditReference = stored.AuditId,
            IsIdempotentReplay = true,
            CorrelationId = stored.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
        };

    private static void InsertIdempotencyRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageDecision decision,
        string fingerprint,
        string auditId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO idempotency_records (
                subscription_id, idempotency_key, payload_fingerprint, user_id,
                decision_result, reason, requested_credits, covered_by_allowance,
                covered_by_extra, remaining_5h_after, remaining_7d_after,
                extra_pool_after, correlation_id, decision_time_unix_ms, audit_id)
            VALUES (
                @sid, @key, @fingerprint, @user_id,
                @result, @reason, @requested, @covered_by_allowance,
                @covered_by_extra, @remaining_5h, @remaining_7d,
                @extra_pool, @correlation_id, @decision_time, @audit_id)
            """;
        command.Parameters.AddWithValue("@sid", decision.SubscriptionId!);
        command.Parameters.AddWithValue("@key", decision.IdempotencyKey!);
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.Parameters.AddWithValue("@user_id", decision.UserId!);
        command.Parameters.AddWithValue("@result", UsageDecisionResultNames.ToWireName(decision.Result));
        command.Parameters.AddWithValue("@reason", (object?)decision.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@requested", decision.RequestedCredits!.Value);
        command.Parameters.AddWithValue("@covered_by_allowance", decision.CoveredBySubscriptionAllowance);
        command.Parameters.AddWithValue("@covered_by_extra", decision.CoveredByExtraPool);
        command.Parameters.AddWithValue("@remaining_5h", (object?)decision.Remaining5hCreditsAfterDecision ?? DBNull.Value);
        command.Parameters.AddWithValue("@remaining_7d", (object?)decision.Remaining7dCreditsAfterDecision ?? DBNull.Value);
        command.Parameters.AddWithValue("@extra_pool", (object?)decision.RemainingExtraPoolCreditsAfterDecision ?? DBNull.Value);
        command.Parameters.AddWithValue("@correlation_id", (object?)decision.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@decision_time", ToUnixMs(decision.DecisionTime));
        command.Parameters.AddWithValue("@audit_id", auditId);
        command.ExecuteNonQuery();
    }

    private static void UpdateExtraPoolBalance(
        SqliteConnection connection, SqliteTransaction transaction, string subscriptionId, long newBalance)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE subscriptions SET extra_pool_balance = @balance WHERE subscription_id = @sid";
        command.Parameters.AddWithValue("@balance", newBalance);
        command.Parameters.AddWithValue("@sid", subscriptionId);
        command.ExecuteNonQuery();
    }
}
