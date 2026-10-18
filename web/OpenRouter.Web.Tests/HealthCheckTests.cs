using OpenRouter.Web.Health;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Probes;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Systemd;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Tests;

public class HealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static HealthCheck Single(IEnumerable<HealthCheck> checks, string title) =>
        checks.Single(c => c.Title == title);

    // --- IPv6 ---

    [Fact]
    public void Sample_radvd_has_frequent_adverts_and_lifetimes_android_ignores()
    {
        var checks = Ipv6Health.Check(
            RadvdParser.Parse(Samples.Read("radvd.conf")),
            IpJsonParser.ParseInterfaces(Samples.Read("ip-addr.json")),
            IpJsonParser.ParseDefaultRoutes(Samples.Read("ip-route6.json"))).ToList();

        Assert.Equal(Warning, Single(checks, "Router advertisements on eth1").Status);
        var lifetimes = Single(checks, "Advertised lifetimes on eth1");
        Assert.Equal(Warning, lifetimes.Status);
        Assert.Contains(lifetimes.Details!, d => d.StartsWith("Router lifetime") && d.EndsWith("30 s"));
        Assert.Equal(Ok, Single(checks, "Advertised prefixes on eth1").Status);
        Assert.Equal(Ok, Single(checks, "IPv6 internet for clients").Status);
    }

    [Fact]
    public void Explicit_long_lifetimes_pass()
    {
        var radvd = RadvdParser.Parse("""
            interface eth1 {
                AdvSendAdvert on; MaxRtrAdvInterval 600; AdvDefaultLifetime 1800;
                prefix fd00::/64 { AdvValidLifetime 3600; AdvPreferredLifetime 1800; };
                RDNSS fd00::1 { AdvRDNSSLifetime 1800; };
            };
            """);
        var checks = Ipv6Health.Check(radvd, null, null).ToList();

        Assert.Equal(Ok, Single(checks, "Router advertisements on eth1").Status);
        Assert.Equal(Ok, Single(checks, "Advertised lifetimes on eth1").Status);
    }

    [Fact]
    public void Public_prefix_without_ipv6_route_is_a_problem()
    {
        var checks = Ipv6Health.Check(
            RadvdParser.Parse(Samples.Read("radvd.conf")),
            IpJsonParser.ParseInterfaces(Samples.Read("ip-addr.json")),
            routes6: []).ToList();

        Assert.Equal(Problem, Single(checks, "IPv6 internet for clients").Status);
    }

    [Fact]
    public void Ula_only_without_ipv6_route_is_fine()
    {
        var radvd = RadvdParser.Parse("interface eth1 { AdvSendAdvert on; prefix fd00:1234:5678:9abc::/64 { }; };");
        var checks = Ipv6Health.Check(radvd, IpJsonParser.ParseInterfaces(Samples.Read("ip-addr.json")), routes6: []).ToList();

        Assert.DoesNotContain(checks, c => c.Title == "IPv6 internet for clients");
    }

    [Fact]
    public void Prefix_the_router_doesnt_have_is_a_problem()
    {
        var radvd = RadvdParser.Parse("interface eth1 { AdvSendAdvert on; prefix 2001:db8:9999:1::/64 { }; };");
        var check = Single(
            Ipv6Health.Check(radvd, IpJsonParser.ParseInterfaces(Samples.Read("ip-addr.json")), null),
            "Advertised prefixes on eth1");

        Assert.Equal(Problem, check.Status);
    }

    // --- DHCP ---

    private static KeaLease Lease(string ip, string mac, int state = 0, int expiresInMinutes = 5) =>
        new(ip, mac, null, null, 1, 600, Now.AddMinutes(expiresInMinutes), state);

    private static Neighbour Neighbour(string ip, string mac, string state = "REACHABLE") => new(ip, "eth1", mac, [state]);

    [Fact]
    public void Sample_dhcp_is_healthy()
    {
        var checks = DhcpHealth.Check(
            KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf")),
            KeaLeaseParser.ParseCsv(Samples.Read("kea-leases4.csv")),
            KeaStatisticsParser.Parse(Samples.Read("kea-statistics.json"), TimeZoneInfo.Utc),
            IpJsonParser.ParseNeighbours(Samples.Read("ip-neigh.json")),
            Now).ToList();

        Assert.DoesNotContain(checks, c => c.Status is Warning or Problem);
        Assert.Equal(Info, Single(checks, "Refused requests (NAK)").Status);
    }

    [Fact]
    public void Ip_answered_by_another_mac_than_the_lease_is_a_conflict()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var check = Single(DhcpHealth.Check(config,
            [Lease("192.168.1.150", "02:00:00:00:00:aa")], null,
            [Neighbour("192.168.1.150", "02:00:00:00:00:bb")], Now), "Address conflicts");

        Assert.Equal(Problem, check.Status);
        Assert.Contains("02:00:00:00:00:bb", Assert.Single(check.Details!));
    }

    [Fact]
    public void Stale_neighbour_entries_are_not_trusted_for_conflicts()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var checks = DhcpHealth.Check(config,
            [Lease("192.168.1.150", "02:00:00:00:00:aa")], null,
            [Neighbour("192.168.1.150", "02:00:00:00:00:bb", "STALE")], Now).ToList();

        Assert.Equal(Ok, Single(checks, "Address conflicts").Status);
    }

    [Fact]
    public void Reserved_ip_used_by_another_device_is_a_conflict()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var check = Single(DhcpHealth.Check(config, [], null,
            [Neighbour("192.168.1.10", "02:00:00:00:00:cc")], Now), "Address conflicts");

        Assert.Equal(Problem, check.Status);
    }

    [Fact]
    public void Declined_address_is_reported()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var check = Single(DhcpHealth.Check(config,
            [Lease("192.168.1.120", "02:00:00:00:00:aa", state: 1, expiresInMinutes: 600)], null, null, Now), "Address conflicts");

        Assert.Equal(Warning, check.Status);
    }

    [Fact]
    public void Device_in_pool_without_lease_is_flagged()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));
        var check = Single(DhcpHealth.Check(config,
            [Lease("192.168.1.150", "02:00:00:00:00:aa", expiresInMinutes: -5)], null,
            [Neighbour("192.168.1.150", "02:00:00:00:00:aa"), Neighbour("192.168.1.50", "02:00:00:00:00:dd")], Now),
            "Devices without a lease");

        Assert.Equal(Warning, check.Status);
        Assert.Equal(["192.168.1.150 (02:00:00:00:00:aa) on eth1"], check.Details);
    }

    [Fact]
    public void Nearly_full_pool_is_a_problem()
    {
        var config = KeaConfigParser.Parse("""
            {"Dhcp4": {"subnet4": [{"id": 1, "subnet": "10.0.0.0/24", "pools": [{"pool": "10.0.0.10 - 10.0.0.19"}]}]}}
            """);
        var leases = Enumerable.Range(10, 10).Select(i => Lease($"10.0.0.{i}", $"02:00:00:00:00:{i:x2}")).ToList();
        var check = Single(DhcpHealth.Check(config, leases, null, null, Now), "Address pool: 10.0.0.0/24");

        Assert.Equal(Problem, check.Status);
        Assert.StartsWith("10 of 10", check.Summary);
    }

    [Fact]
    public void Recent_allocation_failure_is_a_problem()
    {
        var stats = new KeaStatistics(new Dictionary<string, IReadOnlyList<KeaStatSample>>
        {
            ["v4-allocation-fail"] = [new(3, Now.AddMinutes(-10))],
        });
        var check = Single(DhcpHealth.Check(null, null, stats, null, Now), "Address allocation");

        Assert.Equal(Problem, check.Status);
    }

    // --- Links, system, services, firewall, connectivity ---

    [Fact]
    public void Sample_guest_link_is_slow_flapping_and_dropping()
    {
        var ports = new[] { new LinkHealth.Port("eth1", "LAN"), new LinkHealth.Port("eth2", "Guest"), new LinkHealth.Port("eth9", "Missing") };
        NicInfo[] nics = [new("eth1", 1000, "full", 2), new("eth2", 100, "full", 11)];
        var checks = LinkHealth.Check(ports, IpJsonParser.ParseLinkStats(Samples.Read("ip-link.json")), nics).ToList();

        Assert.Equal(Ok, checks[0].Status);
        Assert.Equal(Warning, checks[1].Status);
        Assert.Equal(3, checks[1].Details!.Count(d => d.Contains("100 Mbit/s") || d.Contains("dropped") || d.Contains("down and up")));
        Assert.Equal(Problem, checks[2].Status);
    }

    [Fact]
    public void Ports_are_wan_then_kea_networks()
    {
        var ports = HealthReport.Ports(
            [new DefaultRoute("203.0.113.1", "eth0")],
            KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf")),
            RadvdParser.Parse(Samples.Read("radvd.conf")));

        Assert.Equal(
            [new LinkHealth.Port("eth0", "WAN"), new LinkHealth.Port("eth1", "LAN"), new LinkHealth.Port("eth2", "LAN-GUEST")],
            ports);
    }

    [Fact]
    public void Dns_servers_come_from_kea_and_radvd()
    {
        Assert.Equal(
            ["192.168.1.1", "192.168.2.1", "fd00:1234:5678:9abc::1"],
            HealthReport.DnsServers(KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf")), RadvdParser.Parse(Samples.Read("radvd.conf"))));
    }

    [Theory]
    [InlineData(1000, Ok)]
    [InlineData(52000, Warning)]
    [InlineData(64000, Problem)]
    public void Conntrack_thresholds(long count, HealthStatus expected)
    {
        var kernel = new KernelSnapshot(count, 65536, null, null, null, null, null, null, 4);
        Assert.Equal(expected, Single(SystemHealth.Check(kernel, null), "Connection tracking table").Status);
    }

    [Fact]
    public void Softnet_drops_are_a_warning()
    {
        var kernel = new KernelSnapshot(null, null, new SoftnetTotals(1000, 5, 0), null, null, null, null, null, 4);
        Assert.Equal(Warning, Single(SystemHealth.Check(kernel, null), "Kernel packet queue").Status);
    }

    [Fact]
    public void Service_states()
    {
        UnitStatus Unit(string id, string active, int restarts = 0, string load = "loaded") =>
            new(id + ".service", load, active, "running", "success", restarts, Now.AddDays(-1));

        var checks = ServiceHealth.Check(
            [Unit("kea", "active"), Unit("radvd", "failed"), Unit("named", "active", restarts: 3), Unit("dhcpcd", "inactive", load: "not-found")],
            Now).ToList();

        Assert.Equal([Ok, Problem, Warning, Info], checks.Select(c => c.Status));
        Assert.Equal(["kea", "radvd", "named", "dhcpcd"], checks.Select(c => c.Title));
    }

    [Fact]
    public void Sample_firewall_passes()
    {
        Assert.All(FirewallHealth.Check(Samples.Read("nftables.conf"), 1500), c => Assert.Equal(Ok, c.Status));
    }

    [Fact]
    public void Firewall_without_icmpv6_or_mss_clamping()
    {
        const string ruleset = """
            table inet filter {
                chain input { type filter hook input priority 0; policy drop;
                    # meta l4proto ipv6-icmp accept   (commented out)
                    ct state established,related accept
                }
            }
            """;
        var checks = FirewallHealth.Check(ruleset, wanMtu: 1492).ToList();

        Assert.Equal(Problem, Single(checks, "ICMPv6").Status);
        Assert.Equal(Warning, Single(checks, "WAN MTU").Status);
        Assert.DoesNotContain(checks, c => c.Title == "Related traffic");
    }

    [Fact]
    public void Ping_results()
    {
        static ProbeResults Probe(params double[] rtts) => new(
            new PingResult("203.0.113.1", 5, rtts.Select(TimeSpan.FromMilliseconds).ToList(), null), null, null, []);

        Assert.Equal(Ok, Single(ConnectivityHealth.Check(null, null, Probe(2, 3, 2, 4, 3)), "Ping ISP gateway").Status);
        Assert.Equal(Warning, Single(ConnectivityHealth.Check(null, null, Probe(2, 3, 2, 4)), "Ping ISP gateway").Status);
        Assert.Equal(Warning, Single(ConnectivityHealth.Check(null, null, Probe(2, 3, 2, 4, 90)), "Ping ISP gateway").Status);
        Assert.Equal(Problem, Single(ConnectivityHealth.Check(null, null, Probe()), "Ping ISP gateway").Status);
    }

    [Fact]
    public void Dns_results()
    {
        static HealthStatus Status(DnsResult result) =>
            ConnectivityHealth.Check(null, null, new ProbeResults(null, null, null, [result])).Single().Status;

        Assert.Equal(Ok, Status(new("192.168.1.1", "debian.org", TimeSpan.FromMilliseconds(12), 0, 1, null)));
        Assert.Equal(Warning, Status(new("192.168.1.1", "debian.org", TimeSpan.FromMilliseconds(900), 0, 1, null)));
        Assert.Equal(Problem, Status(new("192.168.1.1", "debian.org", TimeSpan.FromMilliseconds(30), 2, 0, null)));
        Assert.Equal(Problem, Status(new("192.168.1.1", "debian.org", null, null, 0, "no answer within 3 s")));
    }
}
