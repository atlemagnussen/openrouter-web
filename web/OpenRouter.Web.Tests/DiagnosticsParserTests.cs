using OpenRouter.Web.Kea;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Probes;
using OpenRouter.Web.Systemd;

namespace OpenRouter.Web.Tests;

public class DiagnosticsParserTests
{
    [Fact]
    public void Parses_sample_kea_statistics_newest_first_skipping_non_integers()
    {
        var stats = KeaStatisticsParser.Parse(Samples.Read("kea-statistics.json"), TimeZoneInfo.Utc);

        Assert.Equal(37, stats.Latest("pkt4-nak-sent"));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 13, 31, 44, TimeSpan.Zero).AddMicroseconds(502118), stats.LastChanged("pkt4-nak-sent"));
        Assert.Equal(150, stats.Latest("subnet[1].total-addresses"));
        Assert.Null(stats.Latest("subnet[1].cumulative-assigned-addresses"));
        Assert.Null(stats.Latest("no-such-statistic"));
    }

    [Fact]
    public void Kea_statistics_error_result_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KeaStatisticsParser.Parse("""[{"result": 1, "text": "unknown command"}]""", TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("192.168.1.101 - 192.168.1.250", "192.168.1.101", true, 150)]
    [InlineData("192.168.1.101-192.168.1.250", "192.168.1.250", true, 150)]
    [InlineData("192.168.1.101 - 192.168.1.250", "192.168.1.100", false, 150)]
    [InlineData("192.168.1.128/25", "192.168.1.200", true, 128)]
    [InlineData("192.168.1.128/25", "192.168.1.127", false, 128)]
    public void Pool_ranges(string pool, string address, bool contains, long size)
    {
        Assert.True(KeaPoolRange.TryParse(pool, out var range));
        Assert.Equal(contains, range.Value.Contains(address));
        Assert.Equal(size, range.Value.Size);
    }

    [Theory]
    [InlineData("192.168.1.250 - 192.168.1.101")]
    [InlineData("fd00::1 - fd00::9")]
    [InlineData("nonsense")]
    public void Pool_ranges_reject_invalid(string pool) => Assert.False(KeaPoolRange.TryParse(pool, out _));

    [Fact]
    public void Parses_sample_link_stats_and_routes()
    {
        var links = IpJsonParser.ParseLinkStats(Samples.Read("ip-link.json"));
        var guest = links.Single(l => l.Name == "eth2");
        Assert.Equal(1500, guest.Mtu);
        Assert.Equal(2, guest.Rx.Errors);
        Assert.Equal(31022, guest.Tx.Dropped);

        Assert.Equal([new DefaultRoute("203.0.113.1", "eth0")], IpJsonParser.ParseDefaultRoutes(Samples.Read("ip-route.json")));
        Assert.Empty(IpJsonParser.ParseDefaultRoutes("[]"));
    }

    [Fact]
    public void Multipath_default_route_lists_each_gateway()
    {
        var routes = IpJsonParser.ParseDefaultRoutes("""
            [{"dst":"default","nexthops":[{"gateway":"fe80::1","dev":"eth0"},{"gateway":"fe80::2","dev":"eth3"}]}]
            """);
        Assert.Equal([new DefaultRoute("fe80::1", "eth0"), new DefaultRoute("fe80::2", "eth3")], routes);
    }

    [Fact]
    public void Parses_proc_files()
    {
        var softnet = KernelParser.ParseSoftnetStat("0000000a 00000002 00000010 0 0\n0000000b 00000001 00000001 0 0\n");
        Assert.Equal(new SoftnetTotals(21, 3, 17), softnet);

        Assert.Equal(new MemoryInfo(3884512, 3012004), KernelParser.ParseMeminfo(Samples.Read("proc/meminfo")));
        Assert.Equal(0.18, KernelParser.ParseLoadavg(Samples.Read("proc/loadavg")));
        Assert.Equal(TimeSpan.FromSeconds(302411.27), KernelParser.ParseUptime(Samples.Read("proc/uptime")));
    }

    [Fact]
    public void Parses_systemctl_show()
    {
        var units = SystemctlParser.Parse(Samples.Read("systemctl-show.txt"));

        Assert.Equal(5, units.Count);
        var named = units.Single(u => u.Id == "named.service");
        Assert.Equal(2, named.Restarts);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792310400), named.ActiveSince);
        var dhcpcd = units.Single(u => u.Id == "dhcpcd.service");
        Assert.False(dhcpcd.IsInstalled);
        Assert.Null(dhcpcd.ActiveSince);
    }

    [Fact]
    public void Dns_query_has_header_and_question()
    {
        var query = DnsMessage.BuildQuery(0x1234, "debian.org", DnsMessage.TypeA);

        byte[] expected =
        [
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            6, (byte)'d', (byte)'e', (byte)'b', (byte)'i', (byte)'a', (byte)'n', 3, (byte)'o', (byte)'r', (byte)'g', 0,
            0x00, 0x01, 0x00, 0x01,
        ];
        Assert.Equal(expected, query);
    }

    [Fact]
    public void Dns_response_code_and_answers()
    {
        byte[] servfail = [0x12, 0x34, 0x81, 0x82, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal((2, 0), DnsMessage.ParseResponse(servfail, 0x1234));

        byte[] answered = [0x12, 0x34, 0x81, 0x80, 0x00, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal((0, 2), DnsMessage.ParseResponse(answered, 0x1234));

        Assert.Throws<FormatException>(() => DnsMessage.ParseResponse(answered, 0x9999));
        Assert.Throws<FormatException>(() => DnsMessage.ParseResponse(DnsMessage.BuildQuery(1, "a.b", 1), 1));
    }
}
