using OpenRouter.Web.Dns;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Pages;
using OpenRouter.Web.Radvd;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>bind: does it answer on the addresses clients are given, and how did it do over the last hour.</summary>
public static class DnsHealth
{
    /// <param name="interfaces">The router's addresses, to tell its own bind apart from other DNS servers clients may be given.</param>
    public static IEnumerable<HealthCheck> Check(
        BindOptions? bind, BindStats? stats, DnsTotals? lastHour, KeaConfig? kea, RadvdConfig? radvd,
        IReadOnlyList<NetInterface>? interfaces, DateTimeOffset now)
    {
        if (bind is null)
            yield break;

        if (interfaces is not null)
            yield return ListenAddresses(bind, kea, radvd, interfaces);

        if (stats is not null)
        {
            var up = stats.BootTime is { } boot ? $", running for {Format.Duration(now - boot)}" : "";
            yield return new("Statistics channel", Ok, $"bind {stats.Version ?? "(unknown version)"}{up}.");
        }
        else
        {
            yield return bind.StatisticsChannels.Count == 0
                ? new("Statistics channel", Info, "Not enabled, so there are no query, failure or upstream numbers.",
                    "Add statistics-channels { inet 127.0.0.1 port 8053 allow { 127.0.0.1; }; }; to named.conf (see README).")
                : new("Statistics channel", Warning, $"Configured ({string.Join("; ", bind.StatisticsChannels)}) but not answering.",
                    "Check that named was reloaded after adding it, and that the address and port match Router:BindStatsUrl.");
        }

        yield return bind.Forwarders.Count == 0
            ? new("Forwarders", Info, "None: bind resolves from the root servers itself.")
            : new("Forwarders", Ok, string.Join(", ", bind.Forwarders));

        yield return bind.DnssecValidation?.ToLowerInvariant() is "no"
            ? new("DNSSEC validation", Info, "Off.")
            : new("DNSSEC validation", Ok, $"{bind.DnssecValidation ?? "auto"} (failed validations reach clients as SERVFAIL).");

        if (lastHour is { Minutes: > 0, Queries: > 0 } totals)
        {
            var servfail = totals.Share(totals.ServFail) ?? 0;
            var summary = $"{totals.ServFail:N0} of {totals.Queries:N0} queries in the last hour ({servfail:P1}).";
            yield return servfail < 0.02
                ? new("SERVFAIL answers", Ok, summary)
                : new("SERVFAIL answers", servfail >= 0.1 ? Problem : Warning, summary,
                    "Clients got \"server failure\" and the app or page waiting for it stalls or errors. Check upstream timeouts below, " +
                    "and DNSSEC validation failures; the Upstream chart on the DNS page shows when it happened.");
        }
        if (lastHour is { Minutes: > 0, UpstreamQueries: > 0 } upstream)
        {
            var timeouts = upstream.TimeoutShare ?? 0;
            var summary = $"{upstream.UpstreamTimeouts:N0} of {upstream.UpstreamQueries:N0} upstream queries timed out in the last hour ({timeouts:P1}).";
            yield return timeouts < 0.02
                ? new("Upstream timeouts", Ok, summary)
                : new("Upstream timeouts", timeouts >= 0.1 ? Problem : Warning, summary,
                    "bind retries another forwarder after a timeout, so each one adds a delay the client feels. " +
                    "Compare the forwarder probes on the DNS page; one bad forwarder (often an IPv6 one) can be removed.");
        }
    }

    private static HealthCheck ListenAddresses(
        BindOptions bind, KeaConfig? kea, RadvdConfig? radvd, IReadOnlyList<NetInterface> interfaces)
    {
        const string title = "Listening where clients look";
        var routerAddresses = interfaces.SelectMany(i => i.Addresses).Select(a => a.Address).ToList();
        bool OnRouter(string address) => routerAddresses.Any(a => SameAddress(a, address));

        // Which networks are given which server, for the messages.
        var given = new List<(string Address, string Network, bool Ipv6)>();
        foreach (var subnet in kea?.Subnets ?? [])
        {
            foreach (var address in (subnet.Option("domain-name-servers") ?? "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                given.Add((address, subnet.Name ?? subnet.Subnet, false));
        }
        foreach (var config in radvd?.Interfaces ?? [])
        {
            foreach (var address in config.Rdnss.SelectMany(r => r.Arguments))
                given.Add((address, config.Name, true));
        }

        var missing = new List<string>();
        var elsewhere = new List<string>();
        foreach (var group in given.GroupBy(g => (g.Address, g.Ipv6)))
        {
            var (address, ipv6) = group.Key;
            var networks = string.Join(", ", group.Select(g => g.Network).Distinct());
            if (!OnRouter(address))
            {
                elsewhere.Add($"{address} ({networks}) is another server, not this router; the probes on the DNS page test it.");
                continue;
            }
            // No listen-on means all addresses; "any" too.
            var listen = ipv6 ? bind.ListenOnV6 : bind.ListenOn;
            if (listen.Count > 0 && !listen.Contains("any") && !listen.Any(a => SameAddress(a, address)))
                missing.Add(ipv6
                    ? $"Router advertisements on {networks} give out {address}, but listen-on-v6 doesn't include it."
                    : $"DHCP on {networks} gives out {address}, but listen-on doesn't include it.");
        }

        if (missing.Count > 0)
            return new(title, Problem,
                $"{missing.Count} of the router's DNS addresses that clients are given {(missing.Count == 1 ? "has" : "have")} nothing listening.",
                "Clients try that server first and wait for it to time out before using another, which feels like every new page loading slowly.",
                [.. missing, .. elsewhere]);
        return new(title, Ok,
            "bind listens on every address of this router that DHCP and router advertisements give out as DNS server.",
            Details: elsewhere.Count > 0 ? elsewhere : null);
    }

    // fda9:9699:0faa::1 and fda9:9699:faa::1 are the same address.
    private static bool SameAddress(string a, string b) =>
        System.Net.IPAddress.TryParse(a, out var x) && System.Net.IPAddress.TryParse(b, out var y) ? x.Equals(y) : a == b;
}
