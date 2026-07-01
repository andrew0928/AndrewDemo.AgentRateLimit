using AndrewDemo.AgentRateLimit.Abstract;
using AndrewDemo.AgentRateLimit.Core;

var databasePath = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "andrewdemo-agent-rate-limit-smoke.db");

if (File.Exists(databasePath))
{
    File.Delete(databasePath);
}

var service = new SubscriptionUsageService(databasePath);
var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
service.CreateOrReplaceSubscription("user-a", "sub-a", 100, 500, 50, time: now);

var accepted = service.ConsumeUsage(
    new UsageRequest("user-a", "sub-a", 20, "cli-001", "cli-smoke"),
    now.AddMinutes(1));
var status = service.GetUsageStatus("user-a", "sub-a", now.AddMinutes(1));

Console.WriteLine($"database={databasePath}");
Console.WriteLine($"decision={accepted.Result}");
Console.WriteLine($"remaining5h={status.FiveHourWindowRemainingCredits}");
Console.WriteLine($"remaining7d={status.SevenDayWindowRemainingCredits}");
Console.WriteLine($"extraPool={status.ExtraPoolRemainingCredits}");
