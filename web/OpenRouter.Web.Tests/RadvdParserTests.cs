using OpenRouter.Web.Radvd;

namespace OpenRouter.Web.Tests;

public class RadvdParserTests
{
    [Fact]
    public void Parses_sample_config()
    {
        var config = RadvdParser.Parse(Samples.Read("radvd.conf"));

        var lan = Assert.Single(config.Interfaces);
        Assert.Equal("eth1", lan.Name);
        Assert.Equal("on", lan.Option("AdvSendAdvert"));
        Assert.Equal("10", lan.Option("maxrtradvinterval"));

        var prefixes = lan.Prefixes.ToList();
        Assert.Equal(2, prefixes.Count);
        Assert.Equal(["2001:db8:1234:e701::/64"], prefixes[0].Arguments);
        Assert.Equal("3600", prefixes[0].Option("AdvValidLifetime"));
        Assert.Null(prefixes[1].Option("AdvValidLifetime"));

        var rdnss = Assert.Single(lan.Rdnss);
        Assert.Equal(["fd00:1234:5678:9abc::1"], rdnss.Arguments);
        Assert.Empty(rdnss.Options);
    }

    [Fact]
    public void Handles_several_interfaces_multi_word_options_and_compact_layout()
    {
        var config = RadvdParser.Parse("""
            interface eth1 { AdvSendAdvert on; prefix ::/64 { AdvOnLink on; }; RDNSS fd00::1 fd00::2 { AdvRDNSSLifetime 600; }; };
            interface eth2{AdvSendAdvert off;DNSSL home.arpa lan{};clients{fe80::1;fe80::2;};}
            """);

        Assert.Equal(["eth1", "eth2"], config.Interfaces.Select(i => i.Name));
        Assert.Equal(["fd00::1", "fd00::2"], Assert.Single(config.Interfaces[0].Rdnss).Arguments);
        Assert.Equal("600", config.Interfaces[0].Rdnss.Single().Option("AdvRDNSSLifetime"));

        var eth2 = config.Interfaces[1];
        Assert.Equal(["home.arpa", "lan"], Assert.Single(eth2.Dnssl).Arguments);
        var clients = eth2.Sections.Single(s => s.Kind == "clients");
        Assert.Equal(["fe80::1", "fe80::2"], clients.Options.Select(o => o.Name));
    }

    [Fact]
    public void Ignores_comments_including_inside_blocks()
    {
        var config = RadvdParser.Parse("""
            # top
            interface eth1 # trailing
            {
                # AdvSendAdvert off;
                AdvSendAdvert on; # on
                RDNSS fd00::1
                {
                    #AdvRDNSSLifetime 1200;
                };
            };
            """);

        var eth1 = Assert.Single(config.Interfaces);
        Assert.Equal([new RadvdOption("AdvSendAdvert", "on")], eth1.Options);
        Assert.Empty(eth1.Rdnss.Single().Options);
    }

    [Theory]
    [InlineData("interface eth1 {\n  AdvSendAdvert on;\n", 3)]                        // unclosed interface
    [InlineData("interface eth1 {\n  prefix ::/64 {\n    AdvOnLink on\n  };\n};", 4)] // missing ';' inside section
    [InlineData("prefix ::/64 { };", 1)]                                              // not inside an interface
    public void Reports_line_of_syntax_errors(string text, int line)
    {
        var error = Assert.Throws<RadvdParseException>(() => RadvdParser.Parse(text));
        Assert.Equal(line, error.Line);
    }
}
