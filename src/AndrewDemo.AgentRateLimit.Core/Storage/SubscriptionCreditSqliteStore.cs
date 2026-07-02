using System.Globalization;
using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;

namespace AndrewDemo.AgentRateLimit.Core.Storage;

public sealed class SubscriptionCreditSqliteStore
{
    private readonly SubscriptionCreditUsageOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SubscriptionCreditSqliteStore(SubscriptionCreditUsageOptions options)
    {
        _options = options;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenDatabase();
            CreateSchema(database);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SeedAccountAsync(
        SubscriptionCreditAccountSeed seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            database.BeginImmediateTransaction();
            try
            {
                UpsertAccount(database, seed);

                if (seed.ExtraPoolRemainingCredits > 0)
                {
                    AppendExtraPoolRecord(
                        database,
                        "extra-seed-" + seed.SubscriptionId,
                        seed.SubscriptionId,
                        seed.UserId,
                        seed.ExtraPoolRemainingCredits,
                        "seed",
                        "test-seed",
                        DateTimeOffset.UtcNow);
                }

                database.Commit();
            }
            catch
            {
                database.Rollback();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SeedAccessTokenAsync(
        string token,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            database.BeginImmediateTransaction();
            try
            {
                UpsertAccessToken(database, token, subscriptionId);
                database.Commit();
            }
            catch
            {
                database.Rollback();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<string?> ResolveAccessTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            using var statement = database.Prepare(
                "select subscription_id from subscription_access_token where token = ?1;");
            statement.BindText(1, token);

            return statement.Step()
                ? statement.ColumnText(0)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SubscriptionCreditAccountSnapshot?> GetAccountSnapshotAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            var account = LoadAccount(database, subscriptionId);
            return account?.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> CountConsumeRecordsAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            using var statement = database.Prepare(
                "select count(*) from subscription_consume_record where subscription_id = ?1;");
            statement.BindText(1, subscriptionId);

            return statement.Step()
                ? statement.ColumnInt(0)
                : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<UsageCreditDecision> DecideAsync(
        UsageCreditRequest request,
        CreditAmount requestedCredits,
        DateTimeOffset decisionTimeUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            database.BeginImmediateTransaction();
            try
            {
                var account = LoadAccount(database, request.SubscriptionId!.Value.Value);
                var rejected = ValidateAccountForUse(
                    account,
                    request.UserId!.Value.Value,
                    UsageDecisionMode.DecideOnly,
                    request.CreditAmountMode,
                    requestedCredits,
                    decisionTimeUtc);

                if (rejected is not null)
                {
                    database.Commit();
                    return rejected;
                }

                account = RenewExpiredWindows(account!, decisionTimeUtc);
                UpdateAccount(database, account);

                var decision = DecideFromAccount(request, requestedCredits, account, decisionTimeUtc);
                database.Commit();

                return decision;
            }
            catch
            {
                database.Rollback();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<UsageCreditDecision> ConsumeAsync(
        UsageCreditRequest request,
        CreditAmount requestedCredits,
        DateTimeOffset decisionTimeUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var database = OpenInitializedDatabase();
            database.BeginImmediateTransaction();
            try
            {
                var account = LoadAccount(database, request.SubscriptionId!.Value.Value);
                var rejected = ValidateAccountForUse(
                    account,
                    request.UserId!.Value.Value,
                    UsageDecisionMode.Consume,
                    request.CreditAmountMode,
                    requestedCredits,
                    decisionTimeUtc);

                if (rejected is not null)
                {
                    database.Commit();
                    return rejected;
                }

                var existing = LoadConsumeRecord(
                    database,
                    request.SubscriptionId!.Value.Value,
                    request.IdempotencyKey!.Value.Value);
                var fingerprint = SubscriptionCreditUsageService.CreateConsumeFingerprint(request, requestedCredits);

                if (existing is not null)
                {
                    database.Commit();
                    return existing.RequestFingerprint == fingerprint
                        ? existing.ToDecision()
                        : ConflictDecision(request, requestedCredits, account!, decisionTimeUtc);
                }

                account = RenewExpiredWindows(account!, decisionTimeUtc);

                var allocation = AllocateConsume(request, requestedCredits.Value, account);
                var updatedAccount = account with
                {
                    FiveHourUsedCredits = account.FiveHourUsedCredits + requestedCredits.Value,
                    SevenDayUsedCredits = account.SevenDayUsedCredits + requestedCredits.Value,
                    ExtraPoolRemainingCredits = account.ExtraPoolRemainingCredits - allocation.ExtraPoolCovered,
                    StateVersion = account.StateVersion + 1,
                    UpdatedUtc = decisionTimeUtc
                };

                var consumeId = "consume-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                var record = ConsumeRecord.FromSettlement(
                    consumeId,
                    request,
                    requestedCredits.Value,
                    fingerprint,
                    decisionTimeUtc,
                    account,
                    updatedAccount,
                    allocation);

                InsertConsumeRecord(database, record);
                UpdateAccount(database, updatedAccount);
                database.Commit();

                return record.ToDecision();
            }
            catch
            {
                database.Rollback();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static UsageCreditDecision DecideFromAccount(
        UsageCreditRequest request,
        CreditAmount requestedCredits,
        AccountState account,
        DateTimeOffset decisionTimeUtc)
    {
        var fiveHourRemaining = account.FiveHourRemainingCredits;
        var sevenDayRemaining = account.SevenDayRemainingCredits;
        var allowanceRemaining = Math.Min(fiveHourRemaining, sevenDayRemaining);
        var shortage = Math.Max(0, requestedCredits.Value - allowanceRemaining);
        var extraPoolAvailable = account.ExtraPoolRemainingCredits >= shortage;

        if (shortage > 0 && request.ExtraPoolAuthorization == UsageExtraPoolAuthorization.NotAuthorized && extraPoolAvailable)
        {
            return RejectedDecision(
                UsageDecisionMode.DecideOnly,
                request.CreditAmountMode,
                requestedCredits,
                account,
                UsageRejectionReason.ExtraPoolAuthorizationRequired,
                decisionTimeUtc);
        }

        if (shortage > 0 && !extraPoolAvailable)
        {
            return RejectedDecision(
                UsageDecisionMode.DecideOnly,
                request.CreditAmountMode,
                requestedCredits,
                account,
                UsageRejectionReason.InsufficientCredits,
                decisionTimeUtc);
        }

        var projectedCredits = request.CreditAmountMode == UsageCreditAmountMode.ExactCredits
            ? requestedCredits.Value
            : 0;
        var projectedFiveHourUsed = account.FiveHourUsedCredits + projectedCredits;
        var projectedSevenDayUsed = account.SevenDayUsedCredits + projectedCredits;
        var projectedExtraPoolRemaining = account.ExtraPoolRemainingCredits;

        if (request.CreditAmountMode == UsageCreditAmountMode.ExactCredits && shortage > 0)
        {
            projectedExtraPoolRemaining -= shortage;
        }

        return new UsageCreditDecision(
            Mode: UsageDecisionMode.DecideOnly,
            CreditAmountMode: request.CreditAmountMode,
            Result: UsageDecisionResult.Accepted,
            RequestedCredits: requestedCredits,
            CreditsCoveredBySubscriptionAllowance: CreditAmount.Zero,
            CreditsCoveredByExtraPool: CreditAmount.Zero,
            CreditsAbsorbedBySystem: CreditAmount.Zero,
            FiveHourWindowAfterDecision: Balance(
                UsageWindowKind.FiveHours,
                account.FiveHourLimit,
                projectedFiveHourUsed,
                account.FiveHourExpiresUtc),
            SevenDayWindowAfterDecision: Balance(
                UsageWindowKind.SevenDays,
                account.SevenDayLimit,
                projectedSevenDayUsed,
                account.SevenDayExpiresUtc),
            ExtraPoolRemainingAfterDecision: new CreditAmount(projectedExtraPoolRemaining),
            RejectionReason: null,
            InvalidReason: null,
            ConflictReason: null,
            AuditReference: null,
            DecisionTimeUtc: decisionTimeUtc);
    }

    private static ConsumeAllocation AllocateConsume(
        UsageCreditRequest request,
        int actualCredits,
        AccountState account)
    {
        var allowance = Math.Min(
            actualCredits,
            Math.Min(account.FiveHourRemainingCredits, account.SevenDayRemainingCredits));
        var shortage = actualCredits - allowance;
        var extraPoolCovered = request.ExtraPoolAuthorization == UsageExtraPoolAuthorization.Authorized
            ? Math.Min(shortage, account.ExtraPoolRemainingCredits)
            : 0;
        var absorbed = actualCredits - allowance - extraPoolCovered;

        return new ConsumeAllocation(
            SubscriptionAllowanceCovered: allowance,
            ExtraPoolCovered: extraPoolCovered,
            SystemAbsorbed: absorbed);
    }

    private static UsageCreditDecision? ValidateAccountForUse(
        AccountState? account,
        string userId,
        UsageDecisionMode mode,
        UsageCreditAmountMode creditAmountMode,
        CreditAmount requestedCredits,
        DateTimeOffset decisionTimeUtc)
    {
        if (account is null)
        {
            return RejectedDecision(
                mode,
                creditAmountMode,
                requestedCredits,
                null,
                UsageRejectionReason.SubscriptionNotFound,
                decisionTimeUtc);
        }

        if (!StringComparer.Ordinal.Equals(account.UserId, userId))
        {
            return RejectedDecision(
                mode,
                creditAmountMode,
                requestedCredits,
                account,
                UsageRejectionReason.UserSubscriptionMismatch,
                decisionTimeUtc);
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(account.Status, "active"))
        {
            return RejectedDecision(
                mode,
                creditAmountMode,
                requestedCredits,
                account,
                UsageRejectionReason.SubscriptionDisabled,
                decisionTimeUtc);
        }

        return null;
    }

    private static AccountState RenewExpiredWindows(AccountState account, DateTimeOffset now)
    {
        if (account.FiveHourExpiresUtc is null || now >= account.FiveHourExpiresUtc.Value)
        {
            account = account with
            {
                FiveHourOpenedUtc = now,
                FiveHourExpiresUtc = now.AddHours(5),
                FiveHourUsedCredits = 0,
                StateVersion = account.StateVersion + 1,
                UpdatedUtc = now
            };
        }

        if (account.SevenDayExpiresUtc is null || now >= account.SevenDayExpiresUtc.Value)
        {
            account = account with
            {
                SevenDayOpenedUtc = now,
                SevenDayExpiresUtc = now.AddDays(7),
                SevenDayUsedCredits = 0,
                StateVersion = account.StateVersion + 1,
                UpdatedUtc = now
            };
        }

        return account;
    }

    private SqliteDatabase OpenInitializedDatabase()
    {
        var database = OpenDatabase();
        if (!_initialized)
        {
            CreateSchema(database);
            _initialized = true;
        }

        return database;
    }

    private SqliteDatabase OpenDatabase()
    {
        return SqliteDatabase.Open(
            SqliteConnectionString.GetDataSource(_options.SqliteConnectionString));
    }

    private static void CreateSchema(SqliteDatabase database)
    {
        database.Execute("""
            pragma foreign_keys = on;

            create table if not exists subscription_account (
                subscription_id text primary key,
                user_id text not null,
                status text not null,
                five_hour_limit integer not null,
                seven_day_limit integer not null,
                five_hour_opened_utc text null,
                five_hour_expires_utc text null,
                five_hour_used_credits integer not null,
                seven_day_opened_utc text null,
                seven_day_expires_utc text null,
                seven_day_used_credits integer not null,
                extra_pool_remaining_credits integer not null,
                state_version integer not null,
                updated_utc text not null
            );

            create table if not exists subscription_consume_record (
                consume_id text primary key,
                subscription_id text not null,
                user_id text not null,
                idempotency_key text not null,
                request_fingerprint text not null,
                correlation_id text not null,
                source text not null,
                consumed_utc text not null,
                recorded_utc text not null,
                actual_credits integer not null,
                credits_covered_by_subscription_allowance integer not null,
                credits_covered_by_extra_pool integer not null,
                credits_absorbed_by_system integer not null,
                extra_pool_authorization text not null,
                five_hour_limit_snapshot integer not null,
                seven_day_limit_snapshot integer not null,
                five_hour_window_opened_utc text null,
                five_hour_window_expires_utc text null,
                five_hour_used_before integer not null,
                five_hour_used_after integer not null,
                seven_day_window_opened_utc text null,
                seven_day_window_expires_utc text null,
                seven_day_used_before integer not null,
                seven_day_used_after integer not null,
                extra_pool_before integer not null,
                extra_pool_after integer not null
            );

            create unique index if not exists ux_subscription_consume_record_idempotency
                on subscription_consume_record (subscription_id, idempotency_key);

            create table if not exists subscription_extra_pool_record (
                record_id text primary key,
                subscription_id text not null,
                user_id text not null,
                record_kind text not null,
                credits_delta integer not null,
                reason text not null,
                actor text not null,
                occurred_utc text not null,
                recorded_utc text not null,
                correlation_id text null,
                external_reference text null
            );

            create table if not exists subscription_access_token (
                token text primary key,
                subscription_id text not null
            );
            """);
    }

    private static void UpsertAccessToken(
        SqliteDatabase database,
        string token,
        string subscriptionId)
    {
        using var statement = database.Prepare("""
            insert into subscription_access_token (
                token,
                subscription_id)
            values (?1, ?2)
            on conflict(token) do update set
                subscription_id = excluded.subscription_id;
            """);

        statement.BindText(1, token);
        statement.BindText(2, subscriptionId);
        statement.Execute();
    }

    private static void UpsertAccount(SqliteDatabase database, SubscriptionCreditAccountSeed seed)
    {
        using var statement = database.Prepare("""
            insert into subscription_account (
                subscription_id,
                user_id,
                status,
                five_hour_limit,
                seven_day_limit,
                five_hour_opened_utc,
                five_hour_expires_utc,
                five_hour_used_credits,
                seven_day_opened_utc,
                seven_day_expires_utc,
                seven_day_used_credits,
                extra_pool_remaining_credits,
                state_version,
                updated_utc)
            values (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, 0, ?13)
            on conflict(subscription_id) do update set
                user_id = excluded.user_id,
                status = excluded.status,
                five_hour_limit = excluded.five_hour_limit,
                seven_day_limit = excluded.seven_day_limit,
                five_hour_opened_utc = excluded.five_hour_opened_utc,
                five_hour_expires_utc = excluded.five_hour_expires_utc,
                five_hour_used_credits = excluded.five_hour_used_credits,
                seven_day_opened_utc = excluded.seven_day_opened_utc,
                seven_day_expires_utc = excluded.seven_day_expires_utc,
                seven_day_used_credits = excluded.seven_day_used_credits,
                extra_pool_remaining_credits = excluded.extra_pool_remaining_credits,
                state_version = subscription_account.state_version + 1,
                updated_utc = excluded.updated_utc;
            """);

        statement.BindText(1, seed.SubscriptionId);
        statement.BindText(2, seed.UserId);
        statement.BindText(3, seed.Status);
        statement.BindInt(4, seed.FiveHourLimit);
        statement.BindInt(5, seed.SevenDayLimit);
        statement.BindTextOrNull(6, FormatUtc(seed.FiveHourOpenedUtc));
        statement.BindTextOrNull(7, FormatUtc(seed.FiveHourExpiresUtc));
        statement.BindInt(8, seed.FiveHourUsedCredits);
        statement.BindTextOrNull(9, FormatUtc(seed.SevenDayOpenedUtc));
        statement.BindTextOrNull(10, FormatUtc(seed.SevenDayExpiresUtc));
        statement.BindInt(11, seed.SevenDayUsedCredits);
        statement.BindInt(12, seed.ExtraPoolRemainingCredits);
        statement.BindText(13, FormatUtc(DateTimeOffset.UtcNow)!);
        statement.Execute();
    }

    private static AccountState? LoadAccount(SqliteDatabase database, string subscriptionId)
    {
        using var statement = database.Prepare("""
            select
                subscription_id,
                user_id,
                status,
                five_hour_limit,
                seven_day_limit,
                five_hour_opened_utc,
                five_hour_expires_utc,
                five_hour_used_credits,
                seven_day_opened_utc,
                seven_day_expires_utc,
                seven_day_used_credits,
                extra_pool_remaining_credits,
                state_version,
                updated_utc
            from subscription_account
            where subscription_id = ?1;
            """);

        statement.BindText(1, subscriptionId);
        if (!statement.Step())
        {
            return null;
        }

        return new AccountState(
            SubscriptionId: statement.ColumnText(0),
            UserId: statement.ColumnText(1),
            Status: statement.ColumnText(2),
            FiveHourLimit: statement.ColumnInt(3),
            SevenDayLimit: statement.ColumnInt(4),
            FiveHourOpenedUtc: statement.ColumnDateTimeOffsetOrNull(5),
            FiveHourExpiresUtc: statement.ColumnDateTimeOffsetOrNull(6),
            FiveHourUsedCredits: statement.ColumnInt(7),
            SevenDayOpenedUtc: statement.ColumnDateTimeOffsetOrNull(8),
            SevenDayExpiresUtc: statement.ColumnDateTimeOffsetOrNull(9),
            SevenDayUsedCredits: statement.ColumnInt(10),
            ExtraPoolRemainingCredits: statement.ColumnInt(11),
            StateVersion: statement.ColumnInt(12),
            UpdatedUtc: statement.ColumnDateTimeOffset(13));
    }

    private static void UpdateAccount(SqliteDatabase database, AccountState account)
    {
        using var statement = database.Prepare("""
            update subscription_account
            set
                user_id = ?2,
                status = ?3,
                five_hour_limit = ?4,
                seven_day_limit = ?5,
                five_hour_opened_utc = ?6,
                five_hour_expires_utc = ?7,
                five_hour_used_credits = ?8,
                seven_day_opened_utc = ?9,
                seven_day_expires_utc = ?10,
                seven_day_used_credits = ?11,
                extra_pool_remaining_credits = ?12,
                state_version = ?13,
                updated_utc = ?14
            where subscription_id = ?1;
            """);

        statement.BindText(1, account.SubscriptionId);
        statement.BindText(2, account.UserId);
        statement.BindText(3, account.Status);
        statement.BindInt(4, account.FiveHourLimit);
        statement.BindInt(5, account.SevenDayLimit);
        statement.BindTextOrNull(6, FormatUtc(account.FiveHourOpenedUtc));
        statement.BindTextOrNull(7, FormatUtc(account.FiveHourExpiresUtc));
        statement.BindInt(8, account.FiveHourUsedCredits);
        statement.BindTextOrNull(9, FormatUtc(account.SevenDayOpenedUtc));
        statement.BindTextOrNull(10, FormatUtc(account.SevenDayExpiresUtc));
        statement.BindInt(11, account.SevenDayUsedCredits);
        statement.BindInt(12, account.ExtraPoolRemainingCredits);
        statement.BindInt(13, account.StateVersion);
        statement.BindText(14, FormatUtc(account.UpdatedUtc)!);
        statement.Execute();
    }

    private static ConsumeRecord? LoadConsumeRecord(
        SqliteDatabase database,
        string subscriptionId,
        string idempotencyKey)
    {
        using var statement = database.Prepare("""
            select
                consume_id,
                subscription_id,
                user_id,
                idempotency_key,
                request_fingerprint,
                correlation_id,
                source,
                consumed_utc,
                recorded_utc,
                actual_credits,
                credits_covered_by_subscription_allowance,
                credits_covered_by_extra_pool,
                credits_absorbed_by_system,
                extra_pool_authorization,
                five_hour_limit_snapshot,
                seven_day_limit_snapshot,
                five_hour_window_opened_utc,
                five_hour_window_expires_utc,
                five_hour_used_before,
                five_hour_used_after,
                seven_day_window_opened_utc,
                seven_day_window_expires_utc,
                seven_day_used_before,
                seven_day_used_after,
                extra_pool_before,
                extra_pool_after
            from subscription_consume_record
            where subscription_id = ?1
                and idempotency_key = ?2;
            """);

        statement.BindText(1, subscriptionId);
        statement.BindText(2, idempotencyKey);

        if (!statement.Step())
        {
            return null;
        }

        return ConsumeRecord.FromStatement(statement);
    }

    private static void InsertConsumeRecord(SqliteDatabase database, ConsumeRecord record)
    {
        using var statement = database.Prepare("""
            insert into subscription_consume_record (
                consume_id,
                subscription_id,
                user_id,
                idempotency_key,
                request_fingerprint,
                correlation_id,
                source,
                consumed_utc,
                recorded_utc,
                actual_credits,
                credits_covered_by_subscription_allowance,
                credits_covered_by_extra_pool,
                credits_absorbed_by_system,
                extra_pool_authorization,
                five_hour_limit_snapshot,
                seven_day_limit_snapshot,
                five_hour_window_opened_utc,
                five_hour_window_expires_utc,
                five_hour_used_before,
                five_hour_used_after,
                seven_day_window_opened_utc,
                seven_day_window_expires_utc,
                seven_day_used_before,
                seven_day_used_after,
                extra_pool_before,
                extra_pool_after)
            values (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?22, ?23, ?24, ?25, ?26);
            """);

        statement.BindText(1, record.ConsumeId);
        statement.BindText(2, record.SubscriptionId);
        statement.BindText(3, record.UserId);
        statement.BindText(4, record.IdempotencyKey);
        statement.BindText(5, record.RequestFingerprint);
        statement.BindText(6, record.CorrelationId);
        statement.BindText(7, record.Source);
        statement.BindText(8, FormatUtc(record.ConsumedUtc)!);
        statement.BindText(9, FormatUtc(record.RecordedUtc)!);
        statement.BindInt(10, record.ActualCredits);
        statement.BindInt(11, record.CreditsCoveredBySubscriptionAllowance);
        statement.BindInt(12, record.CreditsCoveredByExtraPool);
        statement.BindInt(13, record.CreditsAbsorbedBySystem);
        statement.BindText(14, record.ExtraPoolAuthorization);
        statement.BindInt(15, record.FiveHourLimitSnapshot);
        statement.BindInt(16, record.SevenDayLimitSnapshot);
        statement.BindTextOrNull(17, FormatUtc(record.FiveHourWindowOpenedUtc));
        statement.BindTextOrNull(18, FormatUtc(record.FiveHourWindowExpiresUtc));
        statement.BindInt(19, record.FiveHourUsedBefore);
        statement.BindInt(20, record.FiveHourUsedAfter);
        statement.BindTextOrNull(21, FormatUtc(record.SevenDayWindowOpenedUtc));
        statement.BindTextOrNull(22, FormatUtc(record.SevenDayWindowExpiresUtc));
        statement.BindInt(23, record.SevenDayUsedBefore);
        statement.BindInt(24, record.SevenDayUsedAfter);
        statement.BindInt(25, record.ExtraPoolBefore);
        statement.BindInt(26, record.ExtraPoolAfter);
        statement.Execute();
    }

    private static void AppendExtraPoolRecord(
        SqliteDatabase database,
        string recordId,
        string subscriptionId,
        string userId,
        int creditsDelta,
        string reason,
        string actor,
        DateTimeOffset occurredUtc)
    {
        using var statement = database.Prepare("""
            insert or ignore into subscription_extra_pool_record (
                record_id,
                subscription_id,
                user_id,
                record_kind,
                credits_delta,
                reason,
                actor,
                occurred_utc,
                recorded_utc,
                correlation_id,
                external_reference)
            values (?1, ?2, ?3, 'top-up', ?4, ?5, ?6, ?7, ?8, null, null);
            """);

        statement.BindText(1, recordId);
        statement.BindText(2, subscriptionId);
        statement.BindText(3, userId);
        statement.BindInt(4, creditsDelta);
        statement.BindText(5, reason);
        statement.BindText(6, actor);
        statement.BindText(7, FormatUtc(occurredUtc)!);
        statement.BindText(8, FormatUtc(DateTimeOffset.UtcNow)!);
        statement.Execute();
    }

    private static UsageCreditDecision ConflictDecision(
        UsageCreditRequest request,
        CreditAmount requestedCredits,
        AccountState account,
        DateTimeOffset decisionTimeUtc)
    {
        return new UsageCreditDecision(
            Mode: UsageDecisionMode.Consume,
            CreditAmountMode: request.CreditAmountMode,
            Result: UsageDecisionResult.Conflict,
            RequestedCredits: requestedCredits,
            CreditsCoveredBySubscriptionAllowance: CreditAmount.Zero,
            CreditsCoveredByExtraPool: CreditAmount.Zero,
            CreditsAbsorbedBySystem: CreditAmount.Zero,
            FiveHourWindowAfterDecision: Balance(
                UsageWindowKind.FiveHours,
                account.FiveHourLimit,
                account.FiveHourUsedCredits,
                account.FiveHourExpiresUtc),
            SevenDayWindowAfterDecision: Balance(
                UsageWindowKind.SevenDays,
                account.SevenDayLimit,
                account.SevenDayUsedCredits,
                account.SevenDayExpiresUtc),
            ExtraPoolRemainingAfterDecision: new CreditAmount(account.ExtraPoolRemainingCredits),
            RejectionReason: null,
            InvalidReason: null,
            ConflictReason: UsageConflictReason.IdempotencyKeyPayloadMismatch,
            AuditReference: new AuditReference("conflict-" + request.SubscriptionId!.Value.Value + "-" + request.IdempotencyKey!.Value.Value),
            DecisionTimeUtc: decisionTimeUtc);
    }

    private static UsageCreditDecision RejectedDecision(
        UsageDecisionMode mode,
        UsageCreditAmountMode creditAmountMode,
        CreditAmount requestedCredits,
        AccountState? account,
        UsageRejectionReason reason,
        DateTimeOffset decisionTimeUtc)
    {
        return new UsageCreditDecision(
            Mode: mode,
            CreditAmountMode: creditAmountMode,
            Result: UsageDecisionResult.Rejected,
            RequestedCredits: requestedCredits,
            CreditsCoveredBySubscriptionAllowance: CreditAmount.Zero,
            CreditsCoveredByExtraPool: CreditAmount.Zero,
            CreditsAbsorbedBySystem: CreditAmount.Zero,
            FiveHourWindowAfterDecision: account is null
                ? SubscriptionCreditUsageService.EmptyBalance(UsageWindowKind.FiveHours)
                : Balance(UsageWindowKind.FiveHours, account.FiveHourLimit, account.FiveHourUsedCredits, account.FiveHourExpiresUtc),
            SevenDayWindowAfterDecision: account is null
                ? SubscriptionCreditUsageService.EmptyBalance(UsageWindowKind.SevenDays)
                : Balance(UsageWindowKind.SevenDays, account.SevenDayLimit, account.SevenDayUsedCredits, account.SevenDayExpiresUtc),
            ExtraPoolRemainingAfterDecision: new CreditAmount(account?.ExtraPoolRemainingCredits ?? 0),
            RejectionReason: reason,
            InvalidReason: null,
            ConflictReason: null,
            AuditReference: mode == UsageDecisionMode.Consume
                ? new AuditReference("rejected-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
                : null,
            DecisionTimeUtc: decisionTimeUtc);
    }

    private static UsageWindowBalance Balance(
        UsageWindowKind kind,
        int limit,
        int used,
        DateTimeOffset? nextResetTimeUtc)
    {
        return new UsageWindowBalance(
            Kind: kind,
            Limit: new CreditAmount(limit),
            Used: new CreditAmount(used),
            Remaining: new CreditAmount(Math.Max(0, limit - used)),
            NextResetTimeUtc: nextResetTimeUtc);
    }

    internal static string? FormatUtc(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    internal static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private sealed record AccountState(
        string SubscriptionId,
        string UserId,
        string Status,
        int FiveHourLimit,
        int SevenDayLimit,
        DateTimeOffset? FiveHourOpenedUtc,
        DateTimeOffset? FiveHourExpiresUtc,
        int FiveHourUsedCredits,
        DateTimeOffset? SevenDayOpenedUtc,
        DateTimeOffset? SevenDayExpiresUtc,
        int SevenDayUsedCredits,
        int ExtraPoolRemainingCredits,
        int StateVersion,
        DateTimeOffset UpdatedUtc)
    {
        public int FiveHourRemainingCredits => Math.Max(0, FiveHourLimit - FiveHourUsedCredits);

        public int SevenDayRemainingCredits => Math.Max(0, SevenDayLimit - SevenDayUsedCredits);

        public SubscriptionCreditAccountSnapshot ToSnapshot()
        {
            return new SubscriptionCreditAccountSnapshot(
                SubscriptionId,
                UserId,
                Status,
                FiveHourLimit,
                SevenDayLimit,
                FiveHourOpenedUtc,
                FiveHourExpiresUtc,
                FiveHourUsedCredits,
                FiveHourRemainingCredits,
                SevenDayOpenedUtc,
                SevenDayExpiresUtc,
                SevenDayUsedCredits,
                SevenDayRemainingCredits,
                ExtraPoolRemainingCredits);
        }
    }

    private sealed record ConsumeAllocation(
        int SubscriptionAllowanceCovered,
        int ExtraPoolCovered,
        int SystemAbsorbed);

    private sealed record ConsumeRecord(
        string ConsumeId,
        string SubscriptionId,
        string UserId,
        string IdempotencyKey,
        string RequestFingerprint,
        string CorrelationId,
        string Source,
        DateTimeOffset ConsumedUtc,
        DateTimeOffset RecordedUtc,
        int ActualCredits,
        int CreditsCoveredBySubscriptionAllowance,
        int CreditsCoveredByExtraPool,
        int CreditsAbsorbedBySystem,
        string ExtraPoolAuthorization,
        int FiveHourLimitSnapshot,
        int SevenDayLimitSnapshot,
        DateTimeOffset? FiveHourWindowOpenedUtc,
        DateTimeOffset? FiveHourWindowExpiresUtc,
        int FiveHourUsedBefore,
        int FiveHourUsedAfter,
        DateTimeOffset? SevenDayWindowOpenedUtc,
        DateTimeOffset? SevenDayWindowExpiresUtc,
        int SevenDayUsedBefore,
        int SevenDayUsedAfter,
        int ExtraPoolBefore,
        int ExtraPoolAfter)
    {
        public static ConsumeRecord FromSettlement(
            string consumeId,
            UsageCreditRequest request,
            int actualCredits,
            string fingerprint,
            DateTimeOffset now,
            AccountState before,
            AccountState after,
            ConsumeAllocation allocation)
        {
            return new ConsumeRecord(
                ConsumeId: consumeId,
                SubscriptionId: before.SubscriptionId,
                UserId: before.UserId,
                IdempotencyKey: request.IdempotencyKey!.Value.Value,
                RequestFingerprint: fingerprint,
                CorrelationId: request.CorrelationId.Value,
                Source: request.Source,
                ConsumedUtc: now,
                RecordedUtc: now,
                ActualCredits: actualCredits,
                CreditsCoveredBySubscriptionAllowance: allocation.SubscriptionAllowanceCovered,
                CreditsCoveredByExtraPool: allocation.ExtraPoolCovered,
                CreditsAbsorbedBySystem: allocation.SystemAbsorbed,
                ExtraPoolAuthorization: request.ExtraPoolAuthorization.ToString(),
                FiveHourLimitSnapshot: before.FiveHourLimit,
                SevenDayLimitSnapshot: before.SevenDayLimit,
                FiveHourWindowOpenedUtc: before.FiveHourOpenedUtc,
                FiveHourWindowExpiresUtc: before.FiveHourExpiresUtc,
                FiveHourUsedBefore: before.FiveHourUsedCredits,
                FiveHourUsedAfter: after.FiveHourUsedCredits,
                SevenDayWindowOpenedUtc: before.SevenDayOpenedUtc,
                SevenDayWindowExpiresUtc: before.SevenDayExpiresUtc,
                SevenDayUsedBefore: before.SevenDayUsedCredits,
                SevenDayUsedAfter: after.SevenDayUsedCredits,
                ExtraPoolBefore: before.ExtraPoolRemainingCredits,
                ExtraPoolAfter: after.ExtraPoolRemainingCredits);
        }

        public static ConsumeRecord FromStatement(SqliteStatement statement)
        {
            return new ConsumeRecord(
                ConsumeId: statement.ColumnText(0),
                SubscriptionId: statement.ColumnText(1),
                UserId: statement.ColumnText(2),
                IdempotencyKey: statement.ColumnText(3),
                RequestFingerprint: statement.ColumnText(4),
                CorrelationId: statement.ColumnText(5),
                Source: statement.ColumnText(6),
                ConsumedUtc: statement.ColumnDateTimeOffset(7),
                RecordedUtc: statement.ColumnDateTimeOffset(8),
                ActualCredits: statement.ColumnInt(9),
                CreditsCoveredBySubscriptionAllowance: statement.ColumnInt(10),
                CreditsCoveredByExtraPool: statement.ColumnInt(11),
                CreditsAbsorbedBySystem: statement.ColumnInt(12),
                ExtraPoolAuthorization: statement.ColumnText(13),
                FiveHourLimitSnapshot: statement.ColumnInt(14),
                SevenDayLimitSnapshot: statement.ColumnInt(15),
                FiveHourWindowOpenedUtc: statement.ColumnDateTimeOffsetOrNull(16),
                FiveHourWindowExpiresUtc: statement.ColumnDateTimeOffsetOrNull(17),
                FiveHourUsedBefore: statement.ColumnInt(18),
                FiveHourUsedAfter: statement.ColumnInt(19),
                SevenDayWindowOpenedUtc: statement.ColumnDateTimeOffsetOrNull(20),
                SevenDayWindowExpiresUtc: statement.ColumnDateTimeOffsetOrNull(21),
                SevenDayUsedBefore: statement.ColumnInt(22),
                SevenDayUsedAfter: statement.ColumnInt(23),
                ExtraPoolBefore: statement.ColumnInt(24),
                ExtraPoolAfter: statement.ColumnInt(25));
        }

        public UsageCreditDecision ToDecision()
        {
            return new UsageCreditDecision(
                Mode: UsageDecisionMode.Consume,
                CreditAmountMode: UsageCreditAmountMode.ExactCredits,
                Result: UsageDecisionResult.Accepted,
                RequestedCredits: new CreditAmount(ActualCredits),
                CreditsCoveredBySubscriptionAllowance: new CreditAmount(CreditsCoveredBySubscriptionAllowance),
                CreditsCoveredByExtraPool: new CreditAmount(CreditsCoveredByExtraPool),
                CreditsAbsorbedBySystem: new CreditAmount(CreditsAbsorbedBySystem),
                FiveHourWindowAfterDecision: Balance(
                    UsageWindowKind.FiveHours,
                    FiveHourLimitSnapshot,
                    FiveHourUsedAfter,
                    FiveHourWindowExpiresUtc),
                SevenDayWindowAfterDecision: Balance(
                    UsageWindowKind.SevenDays,
                    SevenDayLimitSnapshot,
                    SevenDayUsedAfter,
                    SevenDayWindowExpiresUtc),
                ExtraPoolRemainingAfterDecision: new CreditAmount(ExtraPoolAfter),
                RejectionReason: null,
                InvalidReason: null,
                ConflictReason: null,
                AuditReference: new AuditReference(ConsumeId),
                DecisionTimeUtc: ConsumedUtc);
        }
    }
}
