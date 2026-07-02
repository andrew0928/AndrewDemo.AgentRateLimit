using System.Text.Json;
using System.Text.Json.Serialization;
using AndrewDemo.AgentRateLimit.Abstract.Credits;
using AndrewDemo.AgentRateLimit.Abstract.Usage;
using AndrewDemo.AgentRateLimit.Core.DependencyInjection;
using AndrewDemo.AgentRateLimit.Core.Storage;

var builder = WebApplication.CreateBuilder(args);
SubscriptionCreditApi.ConfigureBuilder(builder);
var app = SubscriptionCreditApi.Build(builder);
await SubscriptionCreditApi.InitializeAsync(app.Services);
await app.RunAsync();

public static class SubscriptionCreditApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static void ConfigureBuilder(WebApplicationBuilder builder)
    {
        var connectionString =
            builder.Configuration["SubscriptionCredit:SqliteConnectionString"] ??
            builder.Configuration["SUBSCRIPTION_CREDIT_SQLITE"] ??
            "Data Source=subscription-credit.db";

        builder.Services.AddSubscriptionCreditUsage(subscriptionBuilder =>
        {
            subscriptionBuilder.UseSqlite(connectionString);

            var fixedUtcNow = builder.Configuration["SubscriptionCredit:FixedUtcNow"];
            if (!string.IsNullOrWhiteSpace(fixedUtcNow) &&
                DateTimeOffset.TryParse(fixedUtcNow, out var parsedUtcNow))
            {
                subscriptionBuilder.UseTimeProvider(new FixedTimeProvider(parsedUtcNow));
            }
        });
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/v1/subscription-credit/decide", HandleDecideAsync);
        app.MapPost("/v1/subscription-credit/consume", HandleConsumeAsync);

        return app;
    }

    public static async ValueTask InitializeAsync(IServiceProvider services)
    {
        await services.GetRequiredService<SubscriptionCreditSqliteStore>().InitializeAsync();
    }

    private static Task<IResult> HandleDecideAsync(
        HttpContext context,
        SubscriptionCreditSqliteStore store,
        ISubscriptionCreditUsageService usage,
        CancellationToken cancellationToken)
    {
        return HandleUsageAsync(
            context,
            store,
            usage,
            UsageDecisionMode.DecideOnly,
            cancellationToken);
    }

    private static Task<IResult> HandleConsumeAsync(
        HttpContext context,
        SubscriptionCreditSqliteStore store,
        ISubscriptionCreditUsageService usage,
        CancellationToken cancellationToken)
    {
        return HandleUsageAsync(
            context,
            store,
            usage,
            UsageDecisionMode.Consume,
            cancellationToken);
    }

    private static async Task<IResult> HandleUsageAsync(
        HttpContext context,
        SubscriptionCreditSqliteStore store,
        ISubscriptionCreditUsageService usage,
        UsageDecisionMode mode,
        CancellationToken cancellationToken)
    {
        var principal = await AuthenticateAsync(context, store, cancellationToken);
        if (principal.Error is not null)
        {
            return principal.Error;
        }

        if (!IsJsonContentType(context.Request.ContentType))
        {
            return ApiError(
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported-media-type",
                "Request content type must be application/json.",
                correlationId: null);
        }

        UsageHttpRequest? body;
        try
        {
            body = await ReadUsageRequestAsync(context.Request.Body, cancellationToken);
        }
        catch (JsonException)
        {
            return ApiError(
                StatusCodes.Status400BadRequest,
                "malformed-json",
                "Request body is not valid JSON.",
                correlationId: null);
        }

        if (body.ForbiddenScopeFieldPresent)
        {
            return ApiError(
                StatusCodes.Status400BadRequest,
                "subscription-scope-overridden",
                "Request body must not include subscriptionId, userId, or accessToken.",
                body.CorrelationId);
        }

        if (!TryParseCreditAmountMode(body.CreditAmountMode, out var creditAmountMode) ||
            !TryParseExtraPoolAuthorization(body.ExtraPoolAuthorization, out var extraPoolAuthorization))
        {
            return ApiError(
                StatusCodes.Status400BadRequest,
                "malformed-json",
                "Request body contains unsupported enum values.",
                body.CorrelationId);
        }

        var request = new UsageCreditRequest(
            UserId: new UserId(principal.UserId!),
            SubscriptionId: new SubscriptionId(principal.SubscriptionId!),
            RequestedCredits: new RequestedCreditsInput(body.RequestedCredits ?? string.Empty),
            CreditAmountMode: creditAmountMode,
            ExtraPoolAuthorization: extraPoolAuthorization,
            IdempotencyKey: string.IsNullOrWhiteSpace(body.IdempotencyKey)
                ? null
                : new IdempotencyKey(body.IdempotencyKey),
            CorrelationId: new CorrelationId(body.CorrelationId ?? string.Empty),
            Source: body.Source ?? string.Empty);

        var decision = mode == UsageDecisionMode.DecideOnly
            ? await usage.DecideAsync(request, cancellationToken)
            : await usage.ConsumeAsync(request, cancellationToken);

        return Results.Json(
            UsageDecisionHttpResponse.FromDecision(decision),
            JsonOptions,
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<AuthResult> AuthenticateAsync(
        HttpContext context,
        SubscriptionCreditSqliteStore store,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return AuthResult.Fail(ApiError(
                StatusCodes.Status401Unauthorized,
                "missing-authorization",
                "Authorization header is required.",
                correlationId: null));
        }

        var parts = authorization.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !StringComparer.Ordinal.Equals(parts[0], "Bearer"))
        {
            return AuthResult.Fail(ApiError(
                StatusCodes.Status401Unauthorized,
                "invalid-authorization-scheme",
                "Authorization scheme must be Bearer.",
                correlationId: null));
        }

        if (parts.Length != 2 || !IsUppercaseUuid32(parts[1]))
        {
            return AuthResult.Fail(ApiError(
                StatusCodes.Status401Unauthorized,
                "invalid-access-token-format",
                "Authorization token must be 32 uppercase hexadecimal characters without hyphen.",
                correlationId: null));
        }

        var subscriptionId = await store.ResolveAccessTokenAsync(parts[1], cancellationToken);
        if (subscriptionId is null)
        {
            return AuthResult.Fail(ApiError(
                StatusCodes.Status401Unauthorized,
                "invalid-access-token",
                "Authorization token is missing, malformed, or not recognized.",
                correlationId: null));
        }

        var account = await store.GetAccountSnapshotAsync(subscriptionId, cancellationToken);
        if (account is null)
        {
            return AuthResult.Fail(ApiError(
                StatusCodes.Status401Unauthorized,
                "invalid-access-token",
                "Authorization token is missing, malformed, or not recognized.",
                correlationId: null));
        }

        return AuthResult.Success(account.SubscriptionId, account.UserId);
    }

    private static async ValueTask<UsageHttpRequest> ReadUsageRequestAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Request body must be an object.");
        }

        var request = new UsageHttpRequest();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (IsForbiddenScopeField(property.Name))
            {
                request.ForbiddenScopeFieldPresent = true;
                continue;
            }

            var value = property.Value.ValueKind == JsonValueKind.Null
                ? null
                : property.Value.GetString();

            switch (property.Name)
            {
                case "requestedCredits":
                    request.RequestedCredits = value;
                    break;
                case "creditAmountMode":
                    request.CreditAmountMode = value;
                    break;
                case "extraPoolAuthorization":
                    request.ExtraPoolAuthorization = value;
                    break;
                case "idempotencyKey":
                    request.IdempotencyKey = value;
                    break;
                case "correlationId":
                    request.CorrelationId = value;
                    break;
                case "source":
                    request.Source = value;
                    break;
            }
        }

        return request;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        return contentType is not null &&
            contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenScopeField(string propertyName)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(propertyName, "subscriptionId") ||
            StringComparer.OrdinalIgnoreCase.Equals(propertyName, "userId") ||
            StringComparer.OrdinalIgnoreCase.Equals(propertyName, "accessToken");
    }

    private static bool IsUppercaseUuid32(string value)
    {
        if (value.Length != 32)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isDigit = character is >= '0' and <= '9';
            var isUpperHex = character is >= 'A' and <= 'F';
            if (!isDigit && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCreditAmountMode(
        string? value,
        out UsageCreditAmountMode mode)
    {
        switch (value)
        {
            case "exact-credits":
                mode = UsageCreditAmountMode.ExactCredits;
                return true;
            case "minimum-available-balance":
                mode = UsageCreditAmountMode.MinimumAvailableBalance;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static bool TryParseExtraPoolAuthorization(
        string? value,
        out UsageExtraPoolAuthorization authorization)
    {
        switch (value)
        {
            case "not-authorized":
                authorization = UsageExtraPoolAuthorization.NotAuthorized;
                return true;
            case "authorized":
                authorization = UsageExtraPoolAuthorization.Authorized;
                return true;
            default:
                authorization = default;
                return false;
        }
    }

    private static IResult ApiError(
        int statusCode,
        string error,
        string message,
        string? correlationId)
    {
        return Results.Json(
            new ApiErrorResponse(error, message, correlationId),
            JsonOptions,
            statusCode: statusCode);
    }

    private sealed record AuthResult(
        string? SubscriptionId,
        string? UserId,
        IResult? Error)
    {
        public static AuthResult Success(string subscriptionId, string userId) =>
            new(subscriptionId, userId, Error: null);

        public static AuthResult Fail(IResult error) =>
            new(SubscriptionId: null, UserId: null, error);
    }

    private sealed record ApiErrorResponse(
        string Error,
        string Message,
        string? CorrelationId);

    private sealed class UsageHttpRequest
    {
        public string? RequestedCredits { get; set; }

        public string? CreditAmountMode { get; set; }

        public string? ExtraPoolAuthorization { get; set; }

        public string? IdempotencyKey { get; set; }

        public string? CorrelationId { get; set; }

        public string? Source { get; set; }

        public bool ForbiddenScopeFieldPresent { get; set; }
    }

    private sealed record UsageDecisionHttpResponse(
        string Mode,
        string CreditAmountMode,
        string Result,
        int? RequestedCredits,
        int CreditsCoveredBySubscriptionAllowance,
        int CreditsCoveredByExtraPool,
        int CreditsAbsorbedBySystem,
        UsageWindowHttpResponse FiveHourWindowAfterDecision,
        UsageWindowHttpResponse SevenDayWindowAfterDecision,
        int ExtraPoolRemainingAfterDecision,
        string? RejectionReason,
        string? InvalidReason,
        string? ConflictReason,
        string? AuditReference,
        DateTimeOffset DecisionTimeUtc)
    {
        public static UsageDecisionHttpResponse FromDecision(UsageCreditDecision decision)
        {
            return new UsageDecisionHttpResponse(
                Mode: ToWire(decision.Mode),
                CreditAmountMode: ToWire(decision.CreditAmountMode),
                Result: ToWire(decision.Result),
                RequestedCredits: decision.RequestedCredits?.Value,
                CreditsCoveredBySubscriptionAllowance: decision.CreditsCoveredBySubscriptionAllowance.Value,
                CreditsCoveredByExtraPool: decision.CreditsCoveredByExtraPool.Value,
                CreditsAbsorbedBySystem: decision.CreditsAbsorbedBySystem.Value,
                FiveHourWindowAfterDecision: UsageWindowHttpResponse.FromBalance(decision.FiveHourWindowAfterDecision),
                SevenDayWindowAfterDecision: UsageWindowHttpResponse.FromBalance(decision.SevenDayWindowAfterDecision),
                ExtraPoolRemainingAfterDecision: decision.ExtraPoolRemainingAfterDecision.Value,
                RejectionReason: decision.RejectionReason is null ? null : ToWire(decision.RejectionReason.Value),
                InvalidReason: decision.InvalidReason is null ? null : ToWire(decision.InvalidReason.Value),
                ConflictReason: decision.ConflictReason is null ? null : ToWire(decision.ConflictReason.Value),
                AuditReference: decision.AuditReference?.Value,
                DecisionTimeUtc: decision.DecisionTimeUtc);
        }
    }

    private sealed record UsageWindowHttpResponse(
        string Kind,
        int Limit,
        int Used,
        int Remaining,
        DateTimeOffset? NextResetTimeUtc)
    {
        public static UsageWindowHttpResponse FromBalance(UsageWindowBalance balance)
        {
            return new UsageWindowHttpResponse(
                Kind: ToWire(balance.Kind),
                Limit: balance.Limit.Value,
                Used: balance.Used.Value,
                Remaining: balance.Remaining.Value,
                NextResetTimeUtc: balance.NextResetTimeUtc);
        }
    }

    private static string ToWire(UsageDecisionMode mode) => mode switch
    {
        UsageDecisionMode.DecideOnly => "decide-only",
        UsageDecisionMode.Consume => "consume",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static string ToWire(UsageCreditAmountMode mode) => mode switch
    {
        UsageCreditAmountMode.ExactCredits => "exact-credits",
        UsageCreditAmountMode.MinimumAvailableBalance => "minimum-available-balance",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static string ToWire(UsageDecisionResult result) => result switch
    {
        UsageDecisionResult.Accepted => "accepted",
        UsageDecisionResult.Rejected => "rejected",
        UsageDecisionResult.Conflict => "conflict",
        UsageDecisionResult.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
    };

    private static string ToWire(UsageRejectionReason reason) => reason switch
    {
        UsageRejectionReason.InsufficientCredits => "insufficient-credits",
        UsageRejectionReason.ExtraPoolAuthorizationRequired => "extra-pool-authorization-required",
        UsageRejectionReason.SubscriptionNotFound => "subscription-not-found",
        UsageRejectionReason.SubscriptionDisabled => "subscription-disabled",
        UsageRejectionReason.UserSubscriptionMismatch => "user-subscription-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static string ToWire(UsageInvalidReason reason) => reason switch
    {
        UsageInvalidReason.CreditsNotInteger => "credits-not-integer",
        UsageInvalidReason.CreditsNotPositive => "credits-not-positive",
        UsageInvalidReason.MissingUserId => "missing-user-id",
        UsageInvalidReason.MissingSubscriptionId => "missing-subscription-id",
        UsageInvalidReason.MissingIdempotencyKey => "missing-idempotency-key",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static string ToWire(UsageConflictReason reason) => reason switch
    {
        UsageConflictReason.IdempotencyKeyPayloadMismatch => "idempotency-key-payload-mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static string ToWire(UsageWindowKind kind) => kind switch
    {
        UsageWindowKind.FiveHours => "five-hours",
        UsageWindowKind.SevenDays => "seven-days",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
