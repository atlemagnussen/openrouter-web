using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Dns;
using OpenRouter.Web.Health;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Traffic;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Tests;

public class DnsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_sample_bind_options()
    {
        var options = BindConfigParser.ParseOptions(Samples.Read("named.conf.options"));

        Assert.Equal(["8.8.8.8", "1.1.1.1", "2001:4860:4860::8888"], options.Forwarders);
        Assert.Equal(["192.168.1.1", "192.168.2.1"], options.ListenOn);   // two listen-on statements
        Assert.Equal(["fd00:1234:5678:9abc::1"], options.ListenOnV6);
        Assert.Equal("auto", options.DnssecValidation);
        Assert.Equal(["trusted"], options.AllowRecursion);
        Assert.Equal(["inet 127.0.0.1 port 8053 allow"], options.StatisticsChannels);
    }

    [Fact]
    public void Bind_parser_handles_comments_strings_and_ports()
    {
        var statements = BindConfigParser.Parse("""
            # hash comment
            /* block
               comment { ; } */
            zone "example.org" { type master; file "/etc/bind/db.example"; };  // trailing
            options { listen-on port 53 { 10.0.0.1; }; forwarders { 9.9.9.9 port 853; }; }
            """);

        var zone = statements.Single(s => s.Name == "zone");
        Assert.Equal(["zone", "example.org"], zone.Words);
        Assert.Equal(["file", "/etc/bind/db.example"], zone.Children("file").Single().Words);

        var options = BindConfigParser.ParseOptions("options { listen-on port 53 { 10.0.0.1; }; forwarders { 9.9.9.9 port 853; }; };");
        Assert.Equal(["10.0.0.1"], options.ListenOn);
        Assert.Equal(["9.9.9.9 port 853"], options.Forwarders);
    }

    [Theory]
    [InlineData("options { forwarders { 1.1.1.1; };")]
    [InlineData("options { }; };")]
    [InlineData("zone \"x { };")]
    public void Bind_parser_rejects_broken_config(string text) =>
        Assert.ThrowsAny<FormatException>(() => BindConfigParser.Parse(text));

    [Fact]
    public void Parses_sample_bind_statistics_without_the_internal_view()
    {
        var stats = BindStatsParser.Parse(Samples.Read("bind-stats.json"));
        var metrics = DnsMetrics.From(stats);

        Assert.Equal("9.20.11-4-Debian", stats.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 14, 0, 0, 123, TimeSpan.Zero), stats.BootTime);
        Assert.Equal(1611002 + 213309, metrics.Queries);
        Assert.Equal(4120, metrics.ServFail);
        Assert.Equal(541022 + 82011, metrics.UpstreamQueries);   // _bind's 7 left out
        Assert.Equal(2102, metrics.UpstreamTimeouts);
        Assert.Equal(921003, metrics.CacheHits);
        Assert.Equal([201022L, 330122, 81022, 4102, 2310, 1365], metrics.Rtt);
        Assert.Equal(3, metrics.RecursiveClients);
    }

    [Fact]
    public void Bind_statistics_must_look_like_bind()
    {
        Assert.Throws<FormatException>(() => BindStatsParser.Parse("""{"hello": 1}"""));
    }

    private static DnsMetrics Metrics(long queries, long servfail, long clients = 0) =>
        new(queries, queries - servfail, 0, servfail, 0, 0, 0, 0, 0, 0, 0, 0, 0, [0, 0, 0, 0, 0, 0], clients);

    [Fact]
    public void Metric_deltas_and_restarts()
    {
        var delta = DnsMetrics.Delta(Metrics(1000, 10, 2), Metrics(1300, 12, 5));
        Assert.Equal((300L, 2L, 5L), (delta.Queries, delta.ServFail, delta.RecursiveClients));

        // bind restarted: the counters went down, so everything since is new.
        var restarted = DnsMetrics.Delta(Metrics(1000, 10), Metrics(40, 1));
        Assert.Equal((40L, 1L), (restarted.Queries, restarted.ServFail));
    }

    [Fact]
    public void Probe_targets_are_client_dns_addresses_then_forwarders()
    {
        var targets = DnsProbeTargets.From(
            KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf")),
            RadvdParser.Parse(Samples.Read("radvd.conf")),
            BindConfigParser.ParseOptions("options { forwarders { 8.8.8.8; 192.168.1.1; 2001:4860:4860:0:0:0:0:8888; 9.9.9.9 port 853; bogus; }; };"),
            Interfaces);

        Assert.Equal(
        [
            new("192.168.1.1", "bind"), new("192.168.2.1", "bind"), new("fd00:1234:5678:9abc::1", "bind"),
            new("8.8.8.8", "forwarder"), new("2001:4860:4860::8888", "forwarder"), new("9.9.9.9", "forwarder"),
        ], targets);
    }

    [Fact]
    public void Dns_address_clients_are_given_must_be_listened_on()
    {
        var kea = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var radvd = RadvdParser.Parse(Samples.Read("radvd.conf"));
        var bind = BindConfigParser.ParseOptions("options { listen-on { 192.168.1.1; }; listen-on-v6 { fd00:1234:5678:9abc:0:0:0:1; }; };");

        var check = DnsHealth.Check(bind, null, null, kea, radvd, Interfaces, Now).Single(c => c.Title == "Listening where clients look");

        Assert.Equal(Problem, check.Status);
        Assert.Equal(["DHCP on LAN-GUEST gives out 192.168.2.1, but listen-on doesn't include it."], check.Details);  // the v6 one matches despite the zeros
    }

    [Fact]
    public void Dns_server_elsewhere_on_the_network_is_noted_not_flagged()
    {
        // The guest network uses a DNS server on the LAN instead of the router.
        var sample = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var guest = sample.Subnets[1] with { Options = [new("routers", "192.168.2.1"), new("domain-name-servers", "192.168.1.2")] };
        var kea = sample with { Subnets = [sample.Subnets[0], guest] };
        var bind = BindConfigParser.ParseOptions("options { listen-on { 192.168.1.1; }; };");

        var check = DnsHealth.Check(bind, null, null, kea, null, Interfaces, Now).Single(c => c.Title == "Listening where clients look");
        Assert.Equal(Ok, check.Status);
        Assert.Contains(check.Details!, d => d.StartsWith("192.168.1.2 (LAN-GUEST) is another server"));

        var targets = DnsProbeTargets.From(kea, null, bind, Interfaces);
        Assert.Equal([new("192.168.1.1", "bind"), new("192.168.1.2", "other")], targets);
    }

    private static IReadOnlyList<NetInterface> Interfaces => IpJsonParser.ParseInterfaces(Samples.Read("ip-addr.json"));

    [Fact]
    public void Missing_statistics_channel_and_failure_rates()
    {
        var bind = BindOptions.Empty;
        var totals = new DnsTotals(60, 10_000, 9_000, 400, 600, 0, 0, 1_000, 30, 0, 0);
        var checks = DnsHealth.Check(bind, null, totals, null, null, Interfaces, Now).ToList();

        Assert.Equal(Info, checks.Single(c => c.Title == "Statistics channel").Status);
        Assert.Equal(Warning, checks.Single(c => c.Title == "SERVFAIL answers").Status);   // 6%
        Assert.Equal(Warning, checks.Single(c => c.Title == "Upstream timeouts").Status);  // 3%
        Assert.Equal(Info, checks.Single(c => c.Title == "Forwarders").Status);
    }

    [Fact]
    public async Task Store_writes_series_totals_and_probe_summaries()
    {
        var dir = Directory.CreateTempSubdirectory("openrouter-dns-");
        try
        {
            var traffic = new TrafficStore(Options.Create(new RouterOptions { TrafficDatabasePath = Path.Combine(dir.FullName, "history.db") }));
            var store = new DnsStore(traffic);
            var ct = CancellationToken.None;
            Assert.True(await store.IsEmptyAsync(ct));

            var minute = Now.ToUnixTimeSeconds();
            var metrics = new DnsMetrics(100, 90, 6, 4, 0, 80, 60, 40, 40, 2, 1, 0, 1, [10, 20, 5, 1, 1, 1], 3);
            await store.WriteAsync(
            [
                (minute, metrics, [new DnsProbeResult("192.168.1.1", "bind", 0.5, 0, null), new DnsProbeResult("8.8.8.8", "forwarder", null, null, "timeout")]),
                (minute + 60, metrics with { Queries = 200, NoError = 190 }, [new DnsProbeResult("8.8.8.8", "forwarder", 12, 0, null)]),
            ], ct);

            var totals = await store.GetTotalsAsync(minute, minute + 120, ct);
            Assert.Equal((2, 300L, 8L), (totals.Minutes, totals.Queries, totals.ServFail));
            Assert.Equal(0.6, totals.CacheHitRatio);

            var series = await store.GetSeriesAsync(minute, minute + 180, 120, ct);
            Assert.Equal([minute], series.Timestamps);   // one whole bucket fits
            Assert.Equal(140.0, series.Outcomes.Single(o => o.Name == "NOERROR").PerMinute[0]);  // (90 + 190) / 2 minutes
            Assert.Equal(2.0, series.Upstream.Single(u => u.Name == "500 ms–1.6 s").PerMinute[0]);  // rtt800 + rtt1600
            Assert.Equal(["192.168.1.1", "8.8.8.8"], series.Probes.Select(p => p.Target));   // bind first
            Assert.Equal(12.0, series.Probes[1].Milliseconds[0]);   // the failed lookup doesn't count in the average

            var summary = (await store.GetProbeSummaryAsync(minute, minute + 120, ct)).Single(p => p.Target == "8.8.8.8");
            Assert.Equal((2, 1, "timeout"), (summary.Count, summary.Failures, summary.LastFailure));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }
}
