using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AndrewDemo.AgentRateLimit.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AndrewDemo.AgentRateLimit.Api.Tests;

internal sealed class SubscriptionCreditApiTestServer : IAsyncDisposable
{
    public const string PrimaryToken = "0123456789ABCDEFFEDCBA9876543210";
    public const string UnknownToken = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private readonly WebApplication _app;

    private SubscriptionCreditApiTestServer(
        WebApplication app,
        string databasePath,
        HttpClient client,
        SubscriptionCreditSqliteStore store)
    {
        _app = app;
        DatabasePath = databasePath;
        Client = client;
        Store = store;
    }

    public string DatabasePath { get; }

    public HttpClient Client { get; }

    public SubscriptionCreditSqliteStore Store { get; }

    public static async ValueTask<SubscriptionCreditApiTestServer> CreateAsync(DateTimeOffset utcNow)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "andrewdemo-agent-rate-limit-api-" + Guid.NewGuid().ToString("N") + ".db");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SubscriptionCredit:SqliteConnectionString"] = "Data Source=" + databasePath,
            ["SubscriptionCredit:FixedUtcNow"] = utcNow.ToString("O")
        });

        global::SubscriptionCreditApi.ConfigureBuilder(builder);
        var app = global::SubscriptionCreditApi.Build(builder);
        await global::SubscriptionCreditApi.InitializeAsync(app.Services);

        var store = app.Services.GetRequiredService<SubscriptionCreditSqliteStore>();
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        var client = new HttpClient
        {
            BaseAddress = new Uri(address)
        };

        return new SubscriptionCreditApiTestServer(app, databasePath, client, store);
    }

    public async ValueTask SeedSubscriptionAsync(
        string subscriptionId = "sub-a",
        string userId = "user-a",
        string token = PrimaryToken,
        string status = "active",
        int fiveHourRemaining = 100,
        int sevenDayRemaining = 1000,
        int extraPoolRemaining = 1000,
        DateTimeOffset? fiveHourOpenedUtc = null,
        DateTimeOffset? fiveHourExpiresUtc = null,
        DateTimeOffset? sevenDayOpenedUtc = null,
        DateTimeOffset? sevenDayExpiresUtc = null)
    {
        await Store.SeedAccountAsync(CreateSeed(
            subscriptionId,
            userId,
            status,
            fiveHourRemaining,
            sevenDayRemaining,
            extraPoolRemaining,
            fiveHourOpenedUtc,
            fiveHourExpiresUtc,
            sevenDayOpenedUtc,
            sevenDayExpiresUtc));
        await Store.SeedAccessTokenAsync(token, subscriptionId);
    }

    public async ValueTask<UsageApiResponse> PostDecideAsync(
        string token,
        string json,
        string? scheme = "Bearer")
    {
        return await PostJsonAsync("/v1/subscription-credit/decide", token, json, scheme);
    }

    public async ValueTask<UsageApiResponse> PostConsumeAsync(
        string token,
        string json,
        string? scheme = "Bearer")
    {
        return await PostJsonAsync("/v1/subscription-credit/consume", token, json, scheme);
    }

    public async ValueTask<UsageApiResponse> PostDecideWithoutAuthorizationAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1/subscription-credit/decide", content);
        return await UsageApiResponse.ReadAsync(response);
    }

    private async ValueTask<UsageApiResponse> PostJsonAsync(
        string path,
        string token,
        string json,
        string? scheme)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (scheme is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        }

        using var response = await Client.SendAsync(request);
        return await UsageApiResponse.ReadAsync(response);
    }

    public static string UsageJson(
        string idempotencyKey,
        int requestedCredits = 1,
        string creditAmountMode = "minimum-available-balance",
        string extraPoolAuthorization = "not-authorized",
        string? extraFields = null)
    {
        var extra = string.IsNullOrWhiteSpace(extraFields)
            ? string.Empty
            : "," + extraFields;

        return $$"""
            {
              "requestedCredits": "{{requestedCredits}}",
              "creditAmountMode": "{{creditAmountMode}}",
              "extraPoolAuthorization": "{{extraPoolAuthorization}}",
              "idempotencyKey": "{{idempotencyKey}}",
              "correlationId": "corr-{{idempotencyKey}}",
              "source": "api-blackbox-test"{{extra}}
            }
            """;
    }

    public static SubscriptionCreditAccountSeed CreateSeed(
        string subscriptionId,
        string userId,
        string status,
        int fiveHourRemaining,
        int sevenDayRemaining,
        int extraPoolRemaining,
        DateTimeOffset? fiveHourOpenedUtc = null,
        DateTimeOffset? fiveHourExpiresUtc = null,
        DateTimeOffset? sevenDayOpenedUtc = null,
        DateTimeOffset? sevenDayExpiresUtc = null)
    {
        return new SubscriptionCreditAccountSeed(
            SubscriptionId: subscriptionId,
            UserId: userId,
            Status: status,
            FiveHourLimit: 100,
            SevenDayLimit: 1000,
            FiveHourOpenedUtc: fiveHourOpenedUtc ?? DateTimeOffset.Parse("2026-07-01T22:01:23Z"),
            FiveHourExpiresUtc: fiveHourExpiresUtc ?? DateTimeOffset.Parse("2026-07-02T04:01:23Z"),
            FiveHourUsedCredits: 100 - fiveHourRemaining,
            SevenDayOpenedUtc: sevenDayOpenedUtc ?? DateTimeOffset.Parse("2026-07-01T23:01:23Z"),
            SevenDayExpiresUtc: sevenDayExpiresUtc ?? DateTimeOffset.Parse("2026-07-08T23:01:23Z"),
            SevenDayUsedCredits: 1000 - sevenDayRemaining,
            ExtraPoolRemainingCredits: extraPoolRemaining);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();

        try
        {
            if (File.Exists(DatabasePath))
            {
                File.Delete(DatabasePath);
            }
        }
        catch
        {
            // Temp database cleanup should not hide the test assertion result.
        }
    }
}

internal sealed class UsageApiResponse : IDisposable
{
    private UsageApiResponse(HttpStatusCode statusCode, string content, JsonDocument json)
    {
        StatusCode = statusCode;
        Content = content;
        Json = json;
    }

    public HttpStatusCode StatusCode { get; }

    public string Content { get; }

    public JsonDocument Json { get; }

    public static async ValueTask<UsageApiResponse> ReadAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return new UsageApiResponse(response.StatusCode, content, JsonDocument.Parse(content));
    }

    public string? String(string propertyName)
    {
        var property = Json.RootElement.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    public int Int32(string propertyName)
    {
        return Json.RootElement.GetProperty(propertyName).GetInt32();
    }

    public int WindowRemaining(string propertyName)
    {
        return Json.RootElement
            .GetProperty(propertyName)
            .GetProperty("remaining")
            .GetInt32();
    }

    public string? WindowNextReset(string propertyName)
    {
        var property = Json.RootElement
            .GetProperty(propertyName)
            .GetProperty("nextResetTimeUtc");
        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    public void Dispose()
    {
        Json.Dispose();
    }
}
