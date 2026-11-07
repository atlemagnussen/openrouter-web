using OpenRouter.Client.Probes;
using OpenRouter.Client.Reporting;
using OpenRouter.Client.Settings;
using OpenRouter.Client.Storage;

namespace OpenRouter.Client.Tests;

public sealed class ScanTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

    // Shaped like `iw dev wlan0 scan dump`; addresses and names are made up.
    private const string Dump = """
        BSS 02:00:5e:10:00:01(on wlan0) -- associated
        	last seen: 1305055.550s [boottime]
        	freq: 5180.0
        	beacon interval: 200 TUs
        	signal: -59.00 dBm
        	last seen: 1200 ms ago
        	SSID: HomeNet
        	HT capabilities:
        		Capabilities: 0x9ef
        			SM Power Save disabled
        BSS 06:00:5e:10:00:02(on wlan0)
        	freq: 5180.0
        	signal: -64.00 dBm
        	last seen: 900 ms ago
        	SSID: \x00\x00\x00\x00
        BSS 00:1a:11:20:00:03(on wlan0)
        	freq: 2412.0
        	signal: -81.00 dBm
        	last seen: 3000 ms ago
        	SSID: Caf\xc3\xa9
        BSS 00:1a:11:20:00:04(on wlan0)
        	freq: 2437.0
        	signal: -90.00 dBm
        	last seen: 95000 ms ago
        	SSID: Stale
        """;

    [Fact]
    public void Scan_dump_gives_one_entry_per_network_and_skips_stale_ones()
    {
        var networks = ScanParser.Parse(Dump.ReplaceLineEndings("\n"), T0, TimeSpan.FromSeconds(30));

        Assert.Equal(3, networks.Count);
        var home = networks[0];
        Assert.Equal(("02:00:5e:10:00:01", "HomeNet", 5180.0, -59, true, 36), (home.Bssid, home.Ssid, home.FrequencyMhz, home.SignalDbm, home.Associated, home.Channel));
        Assert.Null(networks[1].Ssid); // hidden: zero bytes
        Assert.False(networks[1].Associated);
        Assert.Equal(("Café", 1, "2.4 GHz"), (networks[2].Ssid, networks[2].Channel, networks[2].Band));
    }

    [Fact]
    public void Vendors_come_from_the_oui_list_and_mesh_siblings_share_one()
    {
        var vendors = Vendors.Parse([
            "OUI/MA-L                                                    Organization",
            "02-00-5E   (hex)		Example Mesh Inc.",
            "02005E     (base 16)		Example Mesh Inc.",
            "00-1A-11   (hex)		Google, Inc.",
        ]);

        Assert.Equal("Google, Inc.", vendors.Lookup("00:1a:11:20:00:03"));
        Assert.Null(vendors.Lookup("00:aa:bb:00:00:01"));
        Assert.Equal("(random address)", vendors.Lookup("ce:aa:bb:00:00:01"));

        // 3e:… isn't 3c:… either, but it differs from a known one only in the first and last byte.
        var all = vendors.LookupAll(["00:1a:11:20:00:03", "3e:1a:11:20:00:07", "ce:aa:bb:00:00:01"]);
        Assert.Equal("Google, Inc.", all["3e:1a:11:20:00:07"]);
        Assert.Equal("(random address)", all["ce:aa:bb:00:00:01"]);
    }

    [Fact]
    public void Loud_foreign_networks_on_the_same_channel_are_reported()
    {
        ScanSample S(int minute, string bssid, string? ssid, double freq, int dbm, bool associated = false) =>
            new(T0.AddMinutes(minute), bssid, ssid, freq, dbm, associated);
        var scans = new[]
        {
            S(0, "02:00:5e:10:00:01", "HomeNet", 5180, -55, associated: true),
            S(2, "02:00:5e:10:00:01", "HomeNet", 5180, -57, associated: true),
            S(0, "02:00:5e:10:00:09", "HomeNet", 5180, -66),       // own mesh node: left out
            S(0, "00:1a:11:20:00:05", "Guest", 5180, -60),
            S(2, "00:1a:11:20:00:05", "Guest", 5180, -62),
            S(0, "06:00:5e:10:00:02", null, 5180, -64),             // hidden backhaul: kept
            S(0, "00:1a:11:20:00:06", "FarAway", 5180, -85),        // too weak to matter
            S(0, "00:1a:11:20:00:08", "OtherChannel", 5745, -50),
        };

        var heard = Neighbourhood.Summarise(scans);
        Assert.Equal("00:1a:11:20:00:08", heard[0].Bssid); // strongest first
        var home = Assert.Single(heard, n => n.Associated);
        Assert.Equal((2, -55), (home.TimesHeard, home.StrongestDbm));

        var sharing = Neighbourhood.SharingChannel(heard, 5180, "HomeNet");
        Assert.Equal(["Guest", null], sharing.Select(n => n.Ssid));

        var data = new RunData(new RunInfo(1, T0, T0.AddMinutes(5), "laptop", "wlan0", null, null, null, null), [], [], [],
            [new WifiSample(T0, "wlan0", true, "HomeNet", "02:00:5e:10:00:01", 5180, -55, null, 400, 300, null, null, null, null)], [])
        {
            Scans = scans,
        };
        Assert.Contains(RunReport.Hints(data, new Dictionary<(string, string), IReadOnlyList<Outage>>()),
            h => h.StartsWith("2 other networks at −70 dBm or stronger share 5 GHz channel 36"));
    }

    [Fact]
    public void Networks_marked_as_mine_and_their_hidden_links_are_told_apart()
    {
        var heard = Neighbourhood.Summarise([
            new ScanSample(T0, "02:00:5e:10:00:01", "HomeNet", 5180, -55, true),
            new ScanSample(T0, "06:00:5e:10:00:02", null, 5560, -58, false),   // the connected network's hidden backhaul
            new ScanSample(T0, "00:1a:11:20:00:05", "Guest", 5180, -53, false),
            new ScanSample(T0, "02:1a:11:20:00:06", null, 5180, -60, false),   // Guest's hidden link
            new ScanSample(T0, "00:aa:bb:00:00:01", "Neighbour", 5180, -80, false),
        ]);
        var settings = new ClientSettings().With("Guest", "00:1a:11:20:00:05", true);

        var owners = Neighbourhood.Classify(heard, "HomeNet", settings);

        Assert.Equal(Ownership.Connected, owners["02:00:5e:10:00:01"]);
        Assert.Equal(Ownership.Connected, owners["06:00:5e:10:00:02"]);
        Assert.Equal(Ownership.Mine, owners["00:1a:11:20:00:05"]);
        Assert.Equal(Ownership.Mine, owners["02:1a:11:20:00:06"]);
        Assert.Equal(Ownership.Other, owners["00:aa:bb:00:00:01"]);
        Assert.Equal(Ownership.Other, Neighbourhood.Classify(heard, "HomeNet", new())["00:1a:11:20:00:05"]);
    }

    [Fact]
    public void Settings_round_trip_and_unmark()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netcheck-settings-{Guid.NewGuid():N}", "settings.json");
        try
        {
            Assert.Empty(ClientSettings.Load(path).MyNetworks); // no file yet

            new ClientSettings().With("Guest", "", true).With(null, "AA:BB:CC:00:00:01", true).Save(path);
            var loaded = ClientSettings.Load(path);
            Assert.Equal(["Guest"], loaded.MyNetworks);
            Assert.True(loaded.IsMine(null, "aa:bb:cc:00:00:01"));

            Assert.Empty(loaded.With("Guest", "", false).MyNetworks);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task Old_measurements_are_deleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netcheck-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var store = await ResultStore.OpenAsync(path, CancellationToken.None))
            {
                var old = await store.StartRunAsync(T0.AddDays(-40), "laptop", "wlan0", null, null, null, null, CancellationToken.None);
                await store.WriteAsync(old, [new PingSample(T0.AddDays(-40), "192.0.2.1", "gateway", 3)], CancellationToken.None);
                await store.EndRunAsync(old, T0.AddDays(-39), CancellationToken.None);
                var recent = await store.StartRunAsync(T0, "laptop", "wlan0", null, null, null, null, CancellationToken.None);
                await store.WriteAsync(recent, [new PingSample(T0, "192.0.2.1", "gateway", 3), new ScanSample(T0, "02:00:5e:10:00:01", "HomeNet", 5180, -55, true)],
                    CancellationToken.None);

                await store.DeleteBeforeAsync(T0.AddDays(-30), CancellationToken.None);

                Assert.Equal([recent], (await store.GetRunsAsync(CancellationToken.None)).Select(r => r.Id));
                var data = await store.GetRunAsync(recent, CancellationToken.None);
                Assert.Single(data!.Pings);
                Assert.Single(data.Scans);
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                File.Delete(file);
        }
    }
}
