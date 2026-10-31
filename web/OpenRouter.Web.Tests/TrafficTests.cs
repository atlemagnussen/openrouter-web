using Microsoft.Extensions.Options;
using OpenRouter.Web.Clients;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Traffic;
using static OpenRouter.Web.Traffic.TrafficDirection;

namespace OpenRouter.Web.Tests;

public class TrafficTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_sample_nft_accounting_table()
    {
        var counters = NftAcctParser.Parse(Samples.Read("nft-acct.json"));

        Assert.Equal(9, counters.Count);
        Assert.Contains(new AddressCounter("192.168.1.10", Down, 3012298710), counters);
        Assert.Contains(new AddressCounter("192.168.1.10", Up, 301229871), counters);
        Assert.Contains(new AddressCounter("2001:db8:1234:e701:dead:beef:0:1", Down, 9001), counters);
    }

    [Fact]
    public void Nft_elements_without_counters_are_skipped()
    {
        var counters = NftAcctParser.Parse("""
            {"nftables": [{"set": {"name": "up4", "elem": ["192.168.1.5", {"elem": {"val": "192.168.1.6"}}]}}]}
            """);
        Assert.Empty(counters);
    }

    [Fact]
    public void Deltas_handle_new_addresses_and_resets()
    {
        var previous = new Dictionary<(string, TrafficDirection), long>
        {
            [("10.0.0.1", Down)] = 1000,
            [("10.0.0.2", Down)] = 5000,
            [("10.0.0.3", Down)] = 700,
        };
        var deltas = TrafficAttribution.Deltas(previous,
        [
            new("10.0.0.1", Down, 1500), // grew by 500
            new("10.0.0.2", Down, 300),  // reset: all 300 is new
            new("10.0.0.3", Down, 700),  // unchanged: left out
            new("10.0.0.4", Up, 42),     // new address
        ]);

        Assert.Equal([new("10.0.0.1", Down, 500), new("10.0.0.2", Down, 300), new("10.0.0.4", Up, 42)], deltas);
    }

    [Fact]
    public void Link_deltas_need_a_previous_reading()
    {
        var deltas = TrafficAttribution.LinkDeltas(
            new Dictionary<string, (long, long)> { ["eth0"] = (100, 50) },
            new Dictionary<string, (long, long)> { ["eth0"] = (400, 20), ["eth1"] = (9, 9) });

        Assert.Equal([new LinkTraffic("eth0", 300, 20)], deltas);
    }

    [Fact]
    public void Traffic_is_attributed_to_devices_by_neighbour_table_then_lease()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var leases = KeaLeaseParser.ParseCsv(Samples.Read("kea-leases4.csv"));
        var clients = ClientDirectory.Build(
            IpJsonParser.ParseNeighbours(Samples.Read("ip-neigh.json")), config, leases, new HashSet<string> { "eth1", "eth2" }, Now);
        // Leases in the sample expire around 12:05 UTC, so they're active "now".
        var traffic = TrafficAttribution.ByClient(
        [
            new("192.168.1.10", Down, 1000),
            new("2001:db8:1234:e701:a1b2:c3d4:e5f6:1010", Down, 500), // the desktop's IPv6 address
            new("192.168.1.10", Up, 10),
            new("192.168.2.101", Down, 70),                           // guest, via neighbour table
            new("2001:db8:1234:e701:dead:beef:0:1", Down, 9),         // gone privacy address
        ], clients, config, leases, Now);

        var desktop = traffic.Single(t => t.Key == "02:00:00:00:01:10");
        Assert.Equal(("desktop", "LAN", 1500L, 10L), (desktop.Name, desktop.Network, desktop.DownBytes, desktop.UpBytes));
        Assert.Equal("LAN-GUEST", traffic.Single(t => t.Key == "02:00:00:00:03:01").Network);
        Assert.Equal(9, traffic.Single(t => t.Key == TrafficAttribution.UnknownKey).DownBytes);
    }

    [Fact]
    public void Address_without_neighbour_entry_falls_back_to_its_lease()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        KeaLease[] leases = [new("192.168.1.150", "02:00:00:00:00:aa", null, "tablet", 1, 600, Now.AddMinutes(5), 0)];

        var traffic = Assert.Single(TrafficAttribution.ByClient([new("192.168.1.150", Up, 5)], [], config, leases, Now));
        Assert.Equal(new ClientTraffic("02:00:00:00:00:aa", "tablet", "LAN", 0, 5), traffic);
    }

    [Fact]
    public void Series_rates_cover_sampled_time_and_fold_the_tail_into_other()
    {
        TrafficSeriesBuilder.LinkRow[] links =
        [
            new(0, "eth0", "WAN", RxBytes: 7_500_000, TxBytes: 750_000, Minutes: 5),
            new(1, "eth0", "WAN", RxBytes: 750_000, TxBytes: 0, Minutes: 1), // only one minute sampled
            new(0, "eth1", "LAN", 1, 1, 5),
        ];
        TrafficSeriesBuilder.ClientRow[] clients =
        [
            new(0, "a", "big", "LAN", 6_000_000),
            new(1, "a", "big", "LAN", 600_000),
            new(0, "b", null, "LAN", 1_000_000),
            new(0, "c", "small", "LAN", 400_000),
            new(1, "d", "tiny", "LAN", 100_000),
        ];

        var series = TrafficSeriesBuilder.Build(from: 0, to: 900, bucketSeconds: 300, links, clients, top: 2);

        Assert.Equal([0L, 300, 600], series.Timestamps);
        Assert.Equal("eth0", series.WanInterface);
        Assert.Equal([200_000.0, 100_000.0, null], series.WanDown); // bits per second over the sampled minutes
        Assert.Equal(["a", "b", TrafficSeriesBuilder.OtherKey], series.Clients.Select(c => c.Key));
        Assert.Equal("b", series.Clients[1].Name);                  // no name: the key
        Assert.Equal("Other (2 devices)", series.Clients[2].Name);
        Assert.Equal([400_000 * 8 / 300.0, 100_000 * 8 / 60.0, null], series.Clients[2].BitsPerSecond);
    }

    [Fact]
    public void Range_window_ends_after_the_current_bucket()
    {
        var range = TrafficRange.Find("24h");
        var (from, to) = range.Window(Now.AddSeconds(10));

        Assert.Equal(Now.ToUnixTimeSeconds() + 300, to);
        Assert.Equal(86_400, to - from);
        Assert.Same(TrafficRange.Find("24h"), TrafficRange.Find("nonsense"));
    }

    [Fact]
    public async Task Store_writes_queries_archives_and_snapshots()
    {
        var dir = Directory.CreateTempSubdirectory("openrouter-traffic-");
        try
        {
            var store = new TrafficStore(Options.Create(new RouterOptions { TrafficDatabasePath = Path.Combine(dir.FullName, "traffic.db") }));
            var ct = CancellationToken.None;
            Assert.True(await store.IsEmptyAsync(ct));

            await store.SetLinkRolesAsync([("eth0", "WAN")], ct);
            var september = new DateTimeOffset(2026, 9, 30, 23, 58, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var october = new DateTimeOffset(2026, 10, 18, 11, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            await store.WriteAsync(
            [
                new(september, [new("02:00:00:00:00:01", "old", "LAN", 100, 10)], [new("eth0", 100, 10)]),
                new(october, [new("02:00:00:00:00:01", null, null, 600, 60), new("02:00:00:00:00:02", "new", "LAN", 60, 0)], [new("eth0", 660, 60)]),
                // The same minute again adds up rather than replacing.
                new(october, [new("02:00:00:00:00:01", "renamed", null, 60, 0)], [new("eth0", 60, 0)]),
            ], ct);

            var totals = await store.GetClientTotalsAsync(october, october + 60, ct);
            Assert.Equal(["02:00:00:00:00:01", "02:00:00:00:00:02"], totals.Select(t => t.Key));
            Assert.Equal(("renamed", "LAN", 660L), (totals[0].Name, totals[0].Network, totals[0].DownBytes));

            var series = await store.GetSeriesAsync(october, october + 120, 60, Down, 7, ct);
            Assert.Equal([720 * 8 / 60.0, null], series.WanDown);
            Assert.Equal(660 * 8 / 60.0, series.Clients[0].BitsPerSecond[0]);

            var archiveDir = Path.Combine(dir.FullName, "archive");
            var files = await store.ArchiveAsync(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), archiveDir, ct);
            Assert.Equal([Path.Combine(archiveDir, "traffic-2026-09.db")], files);
            var info = await store.GetInfoAsync(ct);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(october), info.Oldest);

            // The archive file is a database of its own, with the same schema.
            var archived = new TrafficStore(Options.Create(new RouterOptions { TrafficDatabasePath = files[0] }));
            var old = Assert.Single(await archived.GetClientTotalsAsync(september, september + 60, ct));
            Assert.Equal(100, old.DownBytes);

            var snapshot = Path.Combine(dir.FullName, "snapshot.db");
            await store.SnapshotAsync(snapshot, ct);
            var copy = new TrafficStore(Options.Create(new RouterOptions { TrafficDatabasePath = snapshot }));
            Assert.Equal(2, (await copy.GetInfoAsync(ct)).Clients);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }
}
