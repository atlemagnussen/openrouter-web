using System.Text;
using OpenRouter.Client;
using OpenRouter.Client.Probes;
using OpenRouter.Client.Reporting;
using OpenRouter.Client.Storage;

namespace OpenRouter.Client.Tests;

public class ClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    // Real output shape from a Fedora laptop (addresses made up).
    private const string IwLink = """
        Connected to 02:94:ed:0f:87:45 (on wld0)
        	SSID: HomeNet
        	freq: 2427.0
        	RX: 1064644285 bytes (487263 packets)
        	TX: 101559883 bytes (450568 packets)
        	signal: -51 dBm
        	rx bitrate: 144.4 MBit/s
        	tx bitrate: 130.0 MBit/s
        	bss flags: short-preamble short-slot-time
        	dtim period: 4
        	beacon int: 200
        """;

    private const string IwStation = """
        Station 02:94:ed:0f:87:45 (on wld0)
        	authorized:	yes
        	tx packets:	450568
        	tx failed:	3
        	signal:  	-51 [-53, -50] dBm
        	signal avg:	-57 [-61, -54] dBm
        	tx bitrate:	130.0 MBit/s
        	beacon loss:	2
        """;

    [Fact]
    public void Parses_iw_link_and_station_dump()
    {
        var link = IwParser.ParseLink(IwLink);
        Assert.Equal(new IwParser.Link(true, "02:94:ed:0f:87:45", "HomeNet", 2427, -51, 130.0, 144.4), link);

        var station = IwParser.ParseStation(IwStation);
        Assert.Equal(new IwParser.Station(-57, 450568, 3, null, 2), station);

        Assert.False(IwParser.ParseLink("Not connected.\n").Connected);
    }

    [Theory]
    [InlineData(2427, "2.4 GHz", 4)]
    [InlineData(2484, "2.4 GHz", 14)]
    [InlineData(5180, "5 GHz", 36)]
    [InlineData(5955, "6 GHz", 1)]
    public void Wifi_band_and_channel(double mhz, string band, int channel)
    {
        var sample = new WifiSample(T0, "wlan0", true, null, null, mhz, null, null, null, null, null, null, null, null);
        Assert.Equal((band, channel), (sample.Band, sample.Channel));
    }

    [Fact]
    public void Stats_percentiles_jitter_and_loss()
    {
        var stats = Stats.Of([10, 20, null, 30, 40, null, 50, 60, 70, 80, 90, 100]);

        Assert.Equal((12, 2), (stats.Count, stats.Lost));
        Assert.Equal(55, stats.Average);
        Assert.Equal(50, stats.Median);
        Assert.Equal(100, stats.P95);
        Assert.Equal(10, stats.Jitter);
        Assert.Equal(100.0 * 2 / 12, stats.LossPercent);
        Assert.Null(Stats.Of([null, null]).Average);
    }

    [Fact]
    public void Outages_are_three_or_more_failures_in_a_row()
    {
        bool[] failed = [false, true, true, false, true, true, true, true, false, true, true, true];
        var outages = Outage.Find(failed.Select((f, i) => (T0.AddSeconds(i), f)));

        Assert.Equal(2, outages.Count);
        Assert.Equal((T0.AddSeconds(4), T0.AddSeconds(8), 4), (outages[0].Start, outages[0].End, outages[0].Attempts));
        Assert.Equal(TimeSpan.FromSeconds(2), outages[1].Duration);   // still going at the end of the run
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("15m", 900)]
    [InlineData("4h", 14400)]
    [InlineData("1h30m", 5400)]
    [InlineData("45", 45)]
    public void Durations(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), RunOptions.ParseDuration(text));

    [Fact]
    public void Options_replace_defaults_and_reject_unknown_ones()
    {
        var options = RunOptions.Parse(["-d", "0", "--ping", "9.9.9.9, 8.8.4.4", "--no-gateway-dns", "--note", "kitchen"]);

        Assert.Null(options.Duration);
        Assert.Equal(["9.9.9.9", "8.8.4.4"], options.PingTargets);
        Assert.True(options.NoGatewayDns);
        Assert.Equal("kitchen", options.Note);
        Assert.Equal(new RunOptions().DnsServers, options.DnsServers);

        Assert.Throws<FormatException>(() => RunOptions.Parse(["--bogus"]));
        Assert.Throws<FormatException>(() => RunOptions.Parse(["--duration"]));
        Assert.Throws<FormatException>(() => RunOptions.ParseDuration("4x"));
    }

    [Fact]
    public void Http_status_line()
    {
        Assert.Equal(204, HttpProbe.StatusCode(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\n")));
        Assert.Null(HttpProbe.StatusCode(Encoding.ASCII.GetBytes("garbage")));
    }

    private static RunData Run(IEnumerable<PingSample> pings, IEnumerable<WifiSample>? wifi = null, IEnumerable<NetEvent>? events = null) =>
        new(new RunInfo(1, T0, T0.AddHours(1), "laptop", "wlan0", null, "192.168.1.50", "192.168.1.1", null),
            pings.ToList(), [], [], (wifi ?? []).ToList(), (events ?? []).ToList());

    private static IEnumerable<PingSample> Pings(string label, string target, int count, Func<int, double?> ms) =>
        Enumerable.Range(0, count).Select(i => new PingSample(T0.AddSeconds(i), target, label, ms(i)));

    [Fact]
    public void Loss_to_the_router_points_at_wifi_and_internet_only_loss_at_the_wan()
    {
        var local = Run([.. Pings("gateway", "192.168.1.1", 100, i => i % 20 == 0 ? null : 3), .. Pings("internet", "1.1.1.1", 100, i => i % 20 == 0 ? null : 12)]);
        Assert.Contains(RunReport.Hints(local, new Dictionary<(string, string), IReadOnlyList<Outage>>()), h => h.Contains("between this laptop and the router"));

        var wan = Run([.. Pings("gateway", "192.168.1.1", 100, _ => 3), .. Pings("internet", "1.1.1.1", 100, i => i % 10 == 0 ? null : 12)]);
        Assert.Contains(RunReport.Hints(wan, new Dictionary<(string, string), IReadOnlyList<Outage>>()), h => h.Contains("WAN side"));
    }

    [Fact]
    public void Outages_next_to_a_roam_are_blamed_on_roaming()
    {
        var pings = Pings("gateway", "192.168.1.1", 60, i => i is >= 30 and < 36 ? null : 3).ToList();
        var data = Run(pings, events: [new NetEvent(T0.AddSeconds(28), "roam", "a → b")]);
        var outages = new Dictionary<(string, string), IReadOnlyList<Outage>>
        {
            [("gateway", "192.168.1.1")] = Outage.Find(pings.Select(p => (p.Time, p.Milliseconds is null))),
        };

        Assert.Contains(RunReport.Hints(data, outages), h => h.StartsWith("1 of 1 outages started within 15 s of switching access point"));
    }

    [Fact]
    public async Task Store_round_trips_a_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netcheck-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = await ResultStore.OpenAsync(path, CancellationToken.None))
            {
                var id = await store.StartRunAsync(T0, "laptop", "wlan0", "02:00:00:00:00:01", "192.168.1.50", "192.168.1.1", "test", CancellationToken.None);
                await store.WriteAsync(id,
                [
                    new PingSample(T0, "192.168.1.1", "gateway", 3.5),
                    new PingSample(T0.AddSeconds(1), "192.168.1.1", "gateway", null),
                    new DnsSample(T0, "192.168.1.1", "debian.org", 12, 0, null),
                    new HttpSample(T0, "https://example.org/", 5, 10, 20, 30, 200, null),
                    new WifiSample(T0, "wlan0", true, "HomeNet", "02:94:ed:0f:87:45", 5180, -60, -61, 400, 300, 100, 1, null, 0),
                    new NetEvent(T0, "roam", "a → b"),
                ], CancellationToken.None);
                await store.EndRunAsync(id, T0.AddMinutes(5), CancellationToken.None);

                var data = (await store.GetRunAsync(id, CancellationToken.None))!;
                Assert.Equal(T0.AddMinutes(5), data.Run.Ended);
                Assert.Equal([3.5, null], data.Pings.Select(p => p.Milliseconds));
                Assert.Equal(65, data.Http.Single().TotalMs);
                Assert.Equal("5 GHz", data.Wifi.Single().Band);
                Assert.Contains("Access point 02:94:ed:0f:87:45 5 GHz channel 36", RunReport.Build(data, TimeZoneInfo.Utc));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }
}
