using OpenRouter.Web.Clients;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;

namespace OpenRouter.Web.Tests;

public class ClientDirectoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static readonly KeaConfig Config = KeaConfig.Empty with
    {
        Interfaces = ["eth1", "eth2"],
        Subnets =
        [
            new KeaSubnet(1, "192.168.1.0/24", "eth1", "LAN", [], [], null, null,
                [new KeaReservation("desktop", "02:00:00:00:00:10", "192.168.1.10")]),
            new KeaSubnet(2, "192.168.2.0/24", "eth2", null, [], [], null, null, []),
        ],
    };

    private static Neighbour N(string address, string mac, string state = "REACHABLE", string device = "eth1") =>
        new(address, device, mac, [state]);

    private static IReadOnlyList<NetworkClient> Build(IEnumerable<Neighbour> neighbours, IEnumerable<KeaLease>? leases = null) =>
        ClientDirectory.Build(neighbours, Config, leases ?? [], new HashSet<string> { "eth1", "eth2" }, Now);

    [Fact]
    public void Groups_ipv4_and_ipv6_by_mac_and_drops_link_local()
    {
        var client = Assert.Single(Build(
        [
            N("192.168.1.10", "02:00:00:00:00:10"),
            N("fd00::10", "02:00:00:00:00:10", "STALE"),
            N("2001:db8::10", "02:00:00:00:00:10", "STALE"),
            N("fe80::10", "02:00:00:00:00:10", "STALE"),
        ]));

        Assert.Equal(["192.168.1.10"], client.Ipv4);
        Assert.Equal(["2001:db8::10", "fd00::10"], client.Ipv6);
        Assert.Equal(NeighbourStatus.Reachable, client.Status);
    }

    [Fact]
    public void Names_come_from_reservation_then_active_lease()
    {
        KeaLease Lease(string ip, string mac, string name, DateTimeOffset expires) =>
            new(ip, mac, null, name, 1, 600, expires, 0);

        var clients = Build(
            [
                N("192.168.1.10", "02:00:00:00:00:10"),
                N("192.168.1.120", "02:00:00:00:00:20"),
                N("192.168.1.130", "02:00:00:00:00:30"),
            ],
            [
                Lease("192.168.1.10", "02:00:00:00:00:10", "client-says-pc", Now.AddMinutes(5)),
                Lease("192.168.1.120", "02:00:00:00:00:20", "speaker", Now.AddMinutes(5)),
                Lease("192.168.1.130", "02:00:00:00:00:30", "gone-phone", Now.AddMinutes(-5)),
            ]);

        Assert.Equal(["desktop", "speaker", null], clients.Select(c => c.Name));
        Assert.Equal([true, false, false], clients.Select(c => c.HasReservation));
    }

    [Fact]
    public void Skips_unresolved_entries_and_non_lan_devices()
    {
        var clients = Build(
        [
            new Neighbour("192.168.1.99", "eth1", null, ["FAILED"]),
            N("203.0.113.1", "02:00:00:00:00:fe", device: "eth0"),
            N("192.168.2.101", "02:00:00:00:00:31", device: "eth2"),
        ]);

        var guest = Assert.Single(clients);
        Assert.Equal("eth2", guest.Device);
        Assert.Equal("192.168.2.0/24", guest.Network); // subnet without a name falls back to the subnet
    }

    [Fact]
    public void Orders_by_ipv4_then_ipv6_only_devices_last()
    {
        var clients = Build(
        [
            N("fd00::77", "02:00:00:00:00:77"),
            N("192.168.1.100", "02:00:00:00:00:01"),
            N("192.168.1.9", "02:00:00:00:00:02"),
        ]);

        Assert.Equal(["02:00:00:00:00:02", "02:00:00:00:00:01", "02:00:00:00:00:77"], clients.Select(c => c.Mac));
    }

    [Theory]
    [InlineData(new[] { "STALE", "DELAY" }, NeighbourStatus.Checking)]
    [InlineData(new[] { "STALE", "STALE" }, NeighbourStatus.Stale)]
    [InlineData(new[] { "STALE", "REACHABLE" }, NeighbourStatus.Reachable)]
    public void Status_is_the_best_across_addresses(string[] states, NeighbourStatus expected)
    {
        var client = Assert.Single(Build(states.Select((s, i) => N($"192.168.1.{i + 1}", "02:00:00:00:00:01", s))));

        Assert.Equal(expected, client.Status);
    }
}
