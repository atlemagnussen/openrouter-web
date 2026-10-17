using OpenRouter.Web.Kea;

namespace OpenRouter.Web.Tests;

public class KeaLeaseParserTests
{
    private const string Header = "address,hwaddr,client_id,valid_lifetime,expire,subnet_id,fqdn_fwd,fqdn_rev,hostname,state,user_context,pool_id";

    [Fact]
    public void Parses_sample_lease_file()
    {
        var leases = KeaLeaseParser.ParseCsv(Samples.Read("kea-leases4.csv"));

        Assert.Equal(5, leases.Count);
        var printer = leases.Single(l => l.IpAddress == "192.168.1.20");
        Assert.Equal("printer", printer.Hostname);
        Assert.Equal("02:00:00:00:01:20", printer.HwAddress);
        Assert.Equal(1, printer.SubnetId);
        Assert.Null(leases.Single(l => l.IpAddress == "192.168.1.104").ClientId);
    }

    [Fact]
    public void Last_row_for_an_address_wins()
    {
        var leases = KeaLeaseParser.ParseCsv($"""
            {Header}
            192.168.1.50,02:00:00:00:00:01,,600,1000,1,0,0,old-name,0,,0
            192.168.1.50,02:00:00:00:00:02,,600,2000,1,0,0,new-name,0,,0
            """);

        var lease = Assert.Single(leases);
        Assert.Equal("new-name", lease.Hostname);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000), lease.Expires);
    }

    [Fact]
    public void Unescapes_commas_normalizes_hostnames_and_skips_partial_lines()
    {
        var leases = KeaLeaseParser.ParseCsv($"""
            {Header}
            192.168.1.51,02:00:00:00:00:03,,600,1000,1,0,0,a&#x2cb,0,,0
            192.168.1.52,02:00:00:00:00:04,,600,1000,1,0,0,Printer.lan.,0,,0
            192.168.1.53,02:00:00:00:00:05,,600,1000,1,0,0,,0,,0

            192.168.1.54,02:00:00:00:00
            """);

        Assert.Equal(["a,b", "Printer.lan", null], leases.Select(l => l.Hostname));
    }

    [Fact]
    public void Lease_is_active_only_while_valid_and_in_default_state()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(10_000);
        var lease = new KeaLease("192.168.1.10", "02:00:00:00:00:01", null, null, 1, 600, now.AddMinutes(5), State: 0);

        Assert.True(lease.IsActive(now));
        Assert.False((lease with { Expires = now.AddSeconds(-1) }).IsActive(now));
        Assert.False((lease with { State = 1 }).IsActive(now));       // declined
        Assert.False((lease with { ValidLifetime = 0 }).IsActive(now)); // released
    }

    [Fact]
    public void Parses_lease4_get_all_response()
    {
        var leases = KeaLeaseParser.ParseCommandResponse("""
            {
              "result": 0,
              "text": "2 IPv4 lease(s) found.",
              "arguments": { "leases": [
                { "ip-address": "192.168.1.10", "hw-address": "02:00:00:00:01:10", "hostname": "desktop.",
                  "cltt": 1000, "valid-lft": 600, "subnet-id": 1, "state": 0, "client-id": "01:02:00:00:00:01:10" },
                { "ip-address": "192.168.2.101", "hw-address": "02:00:00:00:03:01", "hostname": "",
                  "cltt": 2000, "valid-lft": 600, "subnet-id": 2, "state": 0 }
              ] }
            }
            """);

        Assert.Equal(2, leases.Count);
        Assert.Equal("desktop", leases[0].Hostname);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600), leases[0].Expires);
        Assert.Null(leases[1].Hostname);
        Assert.Null(leases[1].ClientId);
        Assert.Equal(2, leases[1].SubnetId);
    }

    [Fact]
    public void Empty_result_means_no_leases_and_errors_throw()
    {
        Assert.Empty(KeaLeaseParser.ParseCommandResponse("""{ "result": 3, "text": "0 IPv4 lease(s) found." }"""));
        Assert.Empty(KeaLeaseParser.ParseCommandResponse("""[ { "result": 3 } ]"""));

        var error = Assert.Throws<InvalidOperationException>(() =>
            KeaLeaseParser.ParseCommandResponse("""{ "result": 2, "text": "'lease4-get-all' command not supported." }"""));
        Assert.Contains("not supported", error.Message);
    }
}
