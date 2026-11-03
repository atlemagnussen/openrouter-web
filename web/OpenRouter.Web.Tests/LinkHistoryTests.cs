using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Health;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Traffic;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Tests;

public class LinkHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);
    private static readonly LinkHealth.Port[] Ports = [new("eth0", "WAN")];
    private static readonly LinkStats[] Links = [new("eth0", "UP", 1500, new(1000, 10, 0, 0), new(1000, 10, 0, 0))];
    private static readonly NicInfo[] Nics = [new("eth0", 1000, "full", 6)];

    private static HealthCheck Check(LinkHistory? history) =>
        LinkHealth.Check(Ports, Links, Nics, history is null ? null : new Dictionary<string, LinkHistory> { ["eth0"] = history }, Now).Single();

    [Fact]
    public void Recent_drop_is_a_warning_with_how_long_ago()
    {
        var check = Check(new LinkHistory(Now.AddDays(-3), 2, LastChange: Now.AddMinutes(-12), RecentChanges: 2));

        Assert.Equal(Warning, check.Status);
        Assert.StartsWith("The link went down once in the last 24 h, last 12 min ago.", Assert.Single(check.Details!));
    }

    [Fact]
    public void Old_drop_is_only_mentioned()
    {
        var check = Check(new LinkHistory(Now.AddDays(-5), 2, LastChange: Now.AddDays(-3), RecentChanges: 0));

        Assert.Equal(Ok, check.Status);
        Assert.EndsWith("last link drop 3 d ago", check.Summary);
    }

    [Fact]
    public void Drops_before_monitoring_are_a_note_until_a_day_has_passed()
    {
        // carrier_changes was 6 when monitoring started: two drops after boot, time unknown.
        var fresh = Check(new LinkHistory(Now.AddHours(-2), 6, null, 0));
        Assert.Equal(Info, fresh.Status);
        Assert.StartsWith("The link went down 2 times after boot, before monitoring started 2 h ago", Assert.Single(fresh.Details!));

        var settled = Check(new LinkHistory(Now.AddDays(-2), 6, null, 0));
        Assert.Equal(Ok, settled.Status);
        Assert.EndsWith("no link drops in the last 2 d", settled.Summary);
    }

    [Fact]
    public void Without_history_the_count_since_boot_is_a_note()
    {
        var check = Check(null);

        Assert.Equal(Info, check.Status);
        Assert.StartsWith("The link has gone down and up 2 times since boot (when isn't known).", Assert.Single(check.Details!));
    }

    [Fact]
    public async Task Store_records_baselines_and_changes_per_boot()
    {
        var dir = Directory.CreateTempSubdirectory("openrouter-links-");
        try
        {
            var store = new LinkStore(new TrafficStore(Options.Create(new RouterOptions { TrafficDatabasePath = Path.Combine(dir.FullName, "history.db") })));
            var ct = CancellationToken.None;

            await store.RecordAsync("boot-1", "eth0", 4, 0, Now.AddHours(-5), ct);   // baseline
            await store.RecordAsync("boot-1", "eth0", 6, 2, Now.AddHours(-3), ct);   // one drop
            await store.RecordAsync("boot-1", "eth0", 8, 2, Now.AddMinutes(-10), ct); // another
            await store.RecordAsync("boot-2", "eth0", 2, 0, Now, ct);                // after a reboot

            Assert.Equal(8, (await store.GetKnownAsync("boot-1", ct))["eth0"]);
            var history = (await store.GetHistoryAsync("boot-1", Now.AddHours(-1), ct))["eth0"];
            Assert.Equal(new LinkHistory(Now.AddHours(-5), 4, Now.AddMinutes(-10), 2), history);
            Assert.Null((await store.GetHistoryAsync("boot-2", Now.AddHours(-1), ct))["eth0"].LastChange);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }
}
