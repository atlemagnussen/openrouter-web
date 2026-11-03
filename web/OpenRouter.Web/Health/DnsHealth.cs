using OpenRouter.Web.Dns;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Pages;
using OpenRouter.Web.Radvd;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>bind: does it answer on the addresses clients are given, and how did it do over the last hour.</summary>
public static class DnsHealth
{
    public static IEnumerable<HealthCheck> Check(
        BindOptions? bind, BindStats? stats, DnsTotals? lastHour, KeaConfig? kea, RadvdConfig? radvd, DateTimeOffset now)
    {
        if (bind is null)
            yield break;

        yield return ListenAddresses(bind, kea, radvd);

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

    private static HealthCheck ListenAddresses(BindOptions bind, KeaConfig? kea, RadvdConfig? radvd)
    {
        const string title = "Listening where clients look";
        var missing = new List<string>();
        var ipv4 = (kea?.Subnets ?? [])
            .SelectMany(s => (s.Option("domain-name-servers") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct();
        var ipv6 = (radvd?.Interfaces ?? []).SelectMany(i => i.Rdnss).SelectMany(r => r.Arguments).Distinct();

        // No listen-on means all addresses; "any" too.
        foreach (var address in ipv4)
        {
            if (bind.ListenOn.Count > 0 && !bind.ListenOn.Contains("any") && !bind.ListenOn.Contains(address))
                missing.Add($"DHCP gives out {address}, but listen-on doesn't include it.");
        }
        foreach (var address in ipv6)
        {
            if (bind.ListenOnV6.Count > 0 && !bind.ListenOnV6.Contains("any") && !bind.ListenOnV6.Any(a => SameAddress(a, address)))
                missing.Add($"Router advertisements give out {address}, but listen-on-v6 doesn't include it.");
        }

        return missing.Count == 0
            ? new(title, Ok, "bind listens on every DNS address that DHCP and router advertisements give out.")
            : new(title, Problem, $"{missing.Count} DNS address{(missing.Count == 1 ? "" : "es")} clients are given {(missing.Count == 1 ? "has" : "have")} nothing listening.",
                "Clients try that server first and wait for it to time out before using another, which feels like every new page loading slowly.",
                missing);
    }

    // fda9:9699:0faa::1 and fda9:9699:faa::1 are the same address.
    private static bool SameAddress(string a, string b) =>
        System.Net.IPAddress.TryParse(a, out var x) && System.Net.IPAddress.TryParse(b, out var y) ? x.Equals(y) : a == b;
}
