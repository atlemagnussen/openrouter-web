using OpenRouter.Web.Kea;

namespace OpenRouter.Web.Tests;

public class KeaConfigParserTests
{
    [Fact]
    public void Parses_sample_config()
    {
        var config = KeaConfigParser.Parse(Samples.Read("kea-dhcp4.conf"));

        Assert.Equal(["eth1", "eth2"], config.Interfaces);
        Assert.Equal(600, config.ValidLifetime);
        Assert.Equal(7200, config.MaxValidLifetime);
        Assert.Equal(2, config.Subnets.Count);

        var lan = config.Subnets[0];
        Assert.Equal(1, lan.Id);
        Assert.Equal("192.168.1.0/24", lan.Subnet);
        Assert.Equal("eth1", lan.Interface);
        Assert.Equal("LAN", lan.Name);
        Assert.Equal(["192.168.1.101 - 192.168.1.250"], lan.Pools);
        Assert.Equal("192.168.1.1", lan.Option("routers"));
        Assert.Equal("192.168.1.1", lan.Option("domain-name-servers"));
        Assert.Equal(5, lan.Reservations.Count);
        Assert.Equal(new KeaReservation("nas", "02:00:00:00:01:03", "192.168.1.3"), lan.Reservations[0]);

        Assert.Empty(config.Subnets[1].Reservations);
        Assert.Equal(5, config.AllReservations.Count());
    }

    [Fact]
    public void Accepts_comments_and_trailing_commas_and_lowercases_macs()
    {
        var config = KeaConfigParser.Parse("""
            // written by hand
            {
              "Dhcp4": {
                /* block comment */
                "subnet4": [
                  { "id": 7, "subnet": "10.0.0.0/24",
                    "reservations": [ { "hw-address": "AA:BB:CC:DD:EE:FF", "ip-address": "10.0.0.5", }, ],
                  },
                ],
                "reservations": [ { "client-id": "01:02:03", "ip-address": "10.0.0.9" } ],
              },
            }
            """);

        var subnet = Assert.Single(config.Subnets);
        Assert.Null(subnet.Interface);
        Assert.Null(subnet.Name);
        Assert.Equal("aa:bb:cc:dd:ee:ff", Assert.Single(subnet.Reservations).HwAddress);
        Assert.Null(Assert.Single(config.GlobalReservations).HwAddress);
        Assert.Null(config.ValidLifetime);
    }

    [Fact]
    public void Strips_address_and_wildcard_from_interface_names()
    {
        var config = KeaConfig.Empty with { Interfaces = ["eth1/192.168.1.1", "eth2", "*"] };

        Assert.Equal(["eth1", "eth2"], config.InterfaceNames);
    }

    [Fact]
    public void Rejects_config_without_dhcp4()
    {
        Assert.Throws<FormatException>(() => KeaConfigParser.Parse("""{ "Dhcp6": {} }"""));
    }
}
