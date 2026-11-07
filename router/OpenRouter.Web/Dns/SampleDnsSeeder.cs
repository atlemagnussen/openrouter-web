namespace OpenRouter.Web.Dns;

/// <summary>Development only: fills empty DNS tables with a week of made-up statistics and probes.</summary>
public static class SampleDnsSeeder
{
    private static readonly (string Address, string Kind, double Ms)[] Targets =
    [
        ("192.168.1.1", "bind", 0.6),
        ("192.168.2.1", "bind", 0.7),
        ("fd00:1234:5678:9abc::1", "bind", 0.8),
        ("8.8.8.8", "forwarder", 9),
        ("1.1.1.1", "forwarder", 7),
        ("2001:4860:4860::8888", "forwarder", 11),
    ];

    public static async Task SeedIfEmptyAsync(DnsStore store, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await store.IsEmptyAsync(cancellationToken))
            return;

        var random = new Random(11);
        var end = now.ToUnixTimeSeconds() / 60 * 60;
        var minutes = new List<(long, DnsMetrics?, IReadOnlyList<DnsProbeResult>)>();
        for (var minute = end - (long)TimeSpan.FromDays(8).TotalSeconds; minute < end; minute += 60)
        {
            var hour = DateTimeOffset.FromUnixTimeSeconds(minute).ToOffset(TimeSpan.FromHours(1)).Hour;
            var busy = hour is >= 7 and <= 23 ? 1.0 : 0.25;
            // Now and then an upstream hiccup: timeouts, SERVFAILs and slow answers for a few minutes.
            var trouble = random.NextDouble() < 0.003 ? 1 : 0;

            var queries = (long)(busy * (180 + random.Next(120)));
            var servfail = (long)(queries * (0.002 + trouble * 0.08));
            var nxdomain = (long)(queries * (0.03 + random.NextDouble() * 0.02));
            var upstream = (long)(queries * 0.35);
            var timeouts = (long)(upstream * (0.001 + trouble * 0.15));
            var answered = upstream - timeouts;
            long[] rtt = trouble == 1
                ? [answered / 10, answered / 4, answered / 4, answered / 10, answered / 10, answered / 5]
                : [answered / 3, answered / 2, answered / 8, answered / 60, answered / 200, 0];
            var metrics = new DnsMetrics(
                Queries: queries, NoError: queries - servfail - nxdomain, NxDomain: nxdomain, ServFail: servfail, Refused: 0,
                Recursion: (long)(queries * 0.9), CacheHits: queries - upstream, CacheMisses: upstream,
                UpstreamQueries: upstream, UpstreamTimeouts: timeouts, UpstreamServFail: servfail / 2, Lame: 0,
                ValidationFailures: trouble * random.Next(3), Rtt: rtt, RecursiveClients: random.Next(1, 6) + trouble * 20);

            var probes = Targets.Select(t =>
            {
                var failed = trouble == 1 && t.Kind == "forwarder" && random.NextDouble() < 0.3;
                var ms = t.Ms * (0.7 + random.NextDouble() * 0.8) * (trouble == 1 ? 6 : 1);
                return failed
                    ? new DnsProbeResult(t.Address, t.Kind, null, null, "no answer within 3 s")
                    : new DnsProbeResult(t.Address, t.Kind, Math.Round(ms, 1), 0, null);
            }).ToList();
            minutes.Add((minute, metrics, probes));
        }
        await store.WriteAsync(minutes, cancellationToken);
    }
}
