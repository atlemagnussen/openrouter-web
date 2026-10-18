using OpenRouter.Web.Network;
using OpenRouter.Web.Probes;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>Is the router online: default routes, ping and DNS as clients would use it.</summary>
public static class ConnectivityHealth
{
    private static readonly TimeSpan SlowGateway = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan SlowInternet = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HighJitter = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SlowDns = TimeSpan.FromMilliseconds(500);

    public static IEnumerable<HealthCheck> Check(
        IReadOnlyList<DefaultRoute>? routes4, IReadOnlyList<DefaultRoute>? routes6, ProbeResults? probes)
    {
        if (routes4 is not null)
        {
            yield return routes4.Count == 0
                ? new("IPv4 default route", Problem, "The router has no IPv4 default route, so there is no IPv4 internet.",
                    "Check the WAN cable and look for the WAN DHCP client's messages in the journal.")
                : new("IPv4 default route", Ok, Describe(routes4));
        }
        if (routes6 is not null)
        {
            yield return routes6.Count == 0
                ? new("IPv6 default route", Info, "The router has no IPv6 default route, so there is no IPv6 internet.",
                    "Fine if the ISP doesn't offer IPv6. See the IPv6 section for whether clients are told otherwise.")
                : new("IPv6 default route", Ok, Describe(routes6));
        }

        if (probes is null)
        {
            yield return new("Live tests", Info, "Ping and DNS tests are skipped when running on sample data.");
            yield break;
        }

        if (probes.Gateway is { } gateway)
            yield return Ping("Ping ISP gateway", gateway, SlowGateway,
                "The ISP's gateway is the first hop outside your network. Loss or delay here points at the line or the modem, not your network. " +
                "Some ISP routers answer pings at low priority, so weigh this against the internet test.");
        if (probes.Internet4 is { } internet4)
            yield return Ping("Ping internet (IPv4)", internet4, SlowInternet,
                "Loss or highly varying latency here while the gateway test is clean points at the ISP. " +
                "If it only happens while someone uploads or downloads, the line is saturated (bufferbloat): a CAKE queue on the WAN fixes that.");
        if (probes.Internet6 is { } internet6)
            yield return Ping("Ping internet (IPv6)", internet6, SlowInternet,
                "Phones prefer IPv6. If IPv6 is lossy or slow while IPv4 is fine, apps stall before falling back to IPv4.");

        foreach (var dns in probes.Dns)
            yield return Dns(dns);
    }

    private static string Describe(IReadOnlyList<DefaultRoute> routes) =>
        string.Join(", ", routes.Select(r => r.Gateway is null ? $"on {r.Device}" : $"via {r.Gateway} on {r.Device}"));

    private static HealthCheck Ping(string title, PingResult result, TimeSpan slow, string advice)
    {
        if (result.Error is not null)
            return new(title, Problem, $"Could not ping {result.Target}: {result.Error}",
                "The app's user may not be allowed to send pings (sysctl net.ipv4.ping_group_range).");
        if (result.Rtts.Count == 0)
            return new(title, Problem, $"No replies from {result.Target} ({result.Sent} sent).", advice);

        var issues = new List<(HealthStatus Status, string Text)>();
        if (result.Lost > 0)
            issues.Add((result.LossPercent >= 40 ? Problem : Warning, $"{result.Lost} of {result.Sent} pings lost."));
        if (result.Average > slow)
            issues.Add((Warning, $"Average round trip {Ms(result.Average.Value)} (expected under {Ms(slow)})."));
        if (result.Max - result.Min > HighJitter)
            issues.Add((Warning, $"Round trips varied from {Ms(result.Min!.Value)} to {Ms(result.Max!.Value)}."));

        var summary = $"{result.Target}: {Ms(result.Average!.Value)} average ({Ms(result.Min!.Value)}–{Ms(result.Max!.Value)}), " +
            $"{result.Rtts.Count} of {result.Sent} replies";
        return issues.Count == 0
            ? new(title, Ok, summary)
            : new(title, issues.Max(i => i.Status), summary, advice, issues.Select(i => i.Text).ToList());
    }

    private static HealthCheck Dns(DnsResult result)
    {
        var title = $"DNS server {result.Server}";
        if (result.Error is not null)
            return new(title, Problem, $"No answer for {result.Name}: {result.Error}.",
                "Clients are told to use this server, so every lookup they make waits for it. Check that named is running and listens on this address.");
        if (result.ResponseCode is not 0)
        {
            var code = DnsMessage.ResponseCodeName(result.ResponseCode!.Value);
            var advice = result.ResponseCode switch
            {
                2 => "SERVFAIL: the server couldn't get an answer from upstream. Check its forwarders and the WAN; failed DNSSEC validation also shows as SERVFAIL.",
                5 => "REFUSED: the server doesn't allow queries from the router itself. Check allow-query and allow-recursion in named.conf.",
                _ => null,
            };
            return new(title, Problem, $"Answered {code} for {result.Name} in {Ms(result.Elapsed!.Value)}.", advice);
        }
        if (result.Elapsed > SlowDns)
            return new(title, Warning, $"Resolved {result.Name} in {Ms(result.Elapsed.Value)}.",
                "Slow lookups feel like lag in every app. The first lookup of a name can be slow while the cache is cold, so reload to test again; " +
                "if it stays slow, check the forwarders in named.conf.");
        return new(title, Ok, $"Resolved {result.Name} in {Ms(result.Elapsed!.Value)}.");
    }

    private static string Ms(TimeSpan span) => $"{span.TotalMilliseconds:0} ms";
}
