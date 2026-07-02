using System.Net;
using Xunit;

namespace AndrewDemo.AgentRateLimit.Api.Tests;

public sealed class SubscriptionCreditApiSecurityTests
{
    private static readonly DateTimeOffset DefaultNow =
        DateTimeOffset.Parse("2026-07-01T23:10:00Z");

    [Fact]
    public async Task API_AUTH_001_missing_authorization_returns_401()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync();

        using var response = await server.PostDecideWithoutAuthorizationAsync(
            SubscriptionCreditApiTestServer.UsageJson("api-auth-001"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("missing-authorization", response.String("error"));
    }

    [Fact]
    public async Task API_AUTH_002_hyphenated_token_returns_401()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync();

        using var response = await server.PostDecideAsync(
            "01234567-89AB-CDEF-FEDC-BA9876543210",
            SubscriptionCreditApiTestServer.UsageJson("api-auth-002"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid-access-token-format", response.String("error"));
    }

    [Fact]
    public async Task API_AUTH_003_lowercase_token_returns_401()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync();

        using var response = await server.PostDecideAsync(
            "0123456789abcdeffedcba9876543210",
            SubscriptionCreditApiTestServer.UsageJson("api-auth-003"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid-access-token-format", response.String("error"));
    }

    [Fact]
    public async Task API_AUTH_004_well_formed_unknown_token_returns_401()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);

        using var response = await server.PostDecideAsync(
            SubscriptionCreditApiTestServer.UnknownToken,
            SubscriptionCreditApiTestServer.UsageJson("api-auth-004"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid-access-token", response.String("error"));
    }

    [Fact]
    public async Task API_AUTH_005_valid_token_maps_request_to_resolved_subscription_scope()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync(
            subscriptionId: "sub-api-auth-scope",
            userId: "user-api-auth-scope",
            token: "11111111111111111111111111111111",
            fiveHourRemaining: 7,
            sevenDayRemaining: 900,
            extraPoolRemaining: 0);

        using var response = await server.PostConsumeAsync(
            "11111111111111111111111111111111",
            SubscriptionCreditApiTestServer.UsageJson(
                "api-auth-005",
                requestedCredits: 5,
                creditAmountMode: "exact-credits"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted", response.String("result"));
        Assert.Equal(5, response.Int32("creditsCoveredBySubscriptionAllowance"));
        Assert.Equal(2, response.WindowRemaining("fiveHourWindowAfterDecision"));
        Assert.Equal(895, response.WindowRemaining("sevenDayWindowAfterDecision"));
    }

    [Fact]
    public async Task API_SCOPE_001_subscription_id_in_body_returns_400()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync();

        using var response = await server.PostDecideAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson(
                "api-scope-001",
                extraFields: """
                "subscriptionId": "sub-a"
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("subscription-scope-overridden", response.String("error"));
    }

    [Fact]
    public async Task API_SCOPE_002_body_cannot_switch_subscription_scope()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync(subscriptionId: "sub-a", token: SubscriptionCreditApiTestServer.PrimaryToken);
        await server.SeedSubscriptionAsync(
            subscriptionId: "sub-b",
            userId: "user-b",
            token: "22222222222222222222222222222222",
            fiveHourRemaining: 100,
            sevenDayRemaining: 1000,
            extraPoolRemaining: 0);

        using var response = await server.PostDecideAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson(
                "api-scope-002",
                extraFields: """
                "subscriptionId": "sub-b"
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("subscription-scope-overridden", response.String("error"));
    }

    [Fact]
    public async Task API_USAGE_001_valid_decide_minimum_balance_probe_returns_accepted_decision()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync();

        using var response = await server.PostDecideAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson("api-usage-001"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("decide-only", response.String("mode"));
        Assert.Equal("accepted", response.String("result"));
        Assert.Null(response.String("auditReference"));
    }

    [Fact]
    public async Task API_USAGE_002_consume_over_allowance_without_extra_auth_is_absorbed_by_system()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync(fiveHourRemaining: 100, sevenDayRemaining: 1000, extraPoolRemaining: 1000);

        using var response = await server.PostConsumeAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson(
                "api-usage-002",
                requestedCredits: 120,
                creditAmountMode: "exact-credits"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("consume", response.String("mode"));
        Assert.Equal("accepted", response.String("result"));
        Assert.Equal(100, response.Int32("creditsCoveredBySubscriptionAllowance"));
        Assert.Equal(0, response.Int32("creditsCoveredByExtraPool"));
        Assert.Equal(20, response.Int32("creditsAbsorbedBySystem"));
        Assert.Equal(1000, response.Int32("extraPoolRemainingAfterDecision"));
    }

    [Fact]
    public async Task API_USAGE_003_valid_token_but_insufficient_allowance_returns_200_rejected_decision()
    {
        await using var server = await SubscriptionCreditApiTestServer.CreateAsync(DefaultNow);
        await server.SeedSubscriptionAsync(fiveHourRemaining: 0, sevenDayRemaining: 1000, extraPoolRemaining: 0);

        using var response = await server.PostDecideAsync(
            SubscriptionCreditApiTestServer.PrimaryToken,
            SubscriptionCreditApiTestServer.UsageJson("api-usage-003"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("rejected", response.String("result"));
        Assert.Equal("insufficient-credits", response.String("rejectionReason"));
    }
}
