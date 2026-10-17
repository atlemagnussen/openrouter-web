using OpenRouter.Web.Network;

namespace OpenRouter.Web.Tests;

public class NetworkTests
{
    [Fact]
    public void Parses_sample_neighbours()
    {
        var neighbours = IpJsonParser.ParseNeighbours(Samples.Read("ip-neigh.json"));

        var failed = neighbours.Single(n => n.Address == "192.168.1.137");
        Assert.Null(failed.Mac);
        Assert.False(failed.IsUsable);

        var desktop = neighbours.First(n => n.Address == "192.168.1.10");
        Assert.Equal("eth1", desktop.Device);
        Assert.Equal("02:00:00:00:01:10", desktop.Mac);
        Assert.Equal(["REACHABLE"], desktop.States);
        Assert.True(desktop.IsUsable);
        Assert.False(desktop.IsIpv6);
    }

    [Fact]
    public void Parses_sample_interfaces_with_forever_lifetimes()
    {
        var interfaces = IpJsonParser.ParseInterfaces(Samples.Read("ip-addr.json"));

        var lan = interfaces.Single(i => i.Name == "eth1");
        Assert.Equal("UP", lan.OperState);
        var delegated = lan.Addresses.Single(a => a.Address == "2001:db8:1234:e701::1");
        Assert.True(delegated.IsIpv6);
        Assert.Equal(64, delegated.PrefixLength);
        Assert.Equal("global", delegated.Scope);
        Assert.Equal(TimeSpan.FromSeconds(3540), delegated.ValidLifetime);
        Assert.Null(lan.Addresses.Single(a => a.Address == "192.168.1.1").ValidLifetime);
    }

    [Theory]
    [InlineData("2001:db8:1234:e701::/64", "2001:db8:1234:e701::1", true)]
    [InlineData("2001:db8:1234:e701::/64", "2001:db8:1234:e702::1", false)]
    [InlineData("2001:db8:1234:e700::/56", "2001:db8:1234:e7ff::1", true)]
    [InlineData("2001:db8:1234:e700::/55", "2001:db8:1234:e6ff::1", true)]  // a /55 spans e600-e7ff
    [InlineData("2001:db8:1234:e700::/55", "2001:db8:1234:e5ff::1", false)]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("192.168.1.0/24", "fd00::1", false)]
    [InlineData("::/0", "2001:db8::1", true)]
    public void Prefix_contains(string prefix, string address, bool expected)
    {
        Assert.True(IpPrefix.TryParse(prefix, out var parsed));
        Assert.Equal(expected, parsed.Value.Contains(address));
    }

    [Theory]
    [InlineData("2001:db8::")]
    [InlineData("2001:db8::/129")]
    [InlineData("192.168.1.0/33")]
    [InlineData("nonsense/24")]
    public void Prefix_rejects_invalid_input(string text)
    {
        Assert.False(IpPrefix.TryParse(text, out _));
    }

    [Fact]
    public void Addresses_sort_numerically_with_ipv4_first()
    {
        string[] addresses = ["fd00::1", "192.168.1.100", "garbage", "192.168.1.9", "10.0.0.1", "2001:db8::1"];

        Assert.Equal(
            ["10.0.0.1", "192.168.1.9", "192.168.1.100", "2001:db8::1", "fd00::1", "garbage"],
            addresses.OrderBy(IpAddressOrder.Key));
    }
}
