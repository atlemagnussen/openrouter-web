namespace OpenRouter.Web.Dns;

/// <summary>
/// bind's counters from its statistics channel (<c>/json/v1</c>), cumulative since <see cref="BootTime"/>.
/// <see cref="Server"/> is <c>nsstats</c> (queries clients sent), <see cref="Rcodes"/> the answers sent back,
/// <see cref="Resolver"/> and <see cref="Cache"/> are summed over the views (queries bind sent upstream).
/// </summary>
public sealed record BindStats(
    string? Version,
    DateTimeOffset? BootTime,
    IReadOnlyDictionary<string, long> Server,
    IReadOnlyDictionary<string, long> Rcodes,
    IReadOnlyDictionary<string, long> Resolver,
    IReadOnlyDictionary<string, long> Cache);

/// <summary>The DNS numbers the app keeps. As counters they're cumulative; as a minute, the increase in it.</summary>
/// <param name="Queries">Queries from clients.</param>
/// <param name="Recursion">Client queries that needed recursion (not answered from bind's own zones).</param>
/// <param name="UpstreamQueries">Queries bind sent to forwarders or authoritative servers.</param>
/// <param name="Rtt">Upstream answers by round-trip time: under 10, 100, 500, 800, 1600 ms, and slower.</param>
/// <param name="RecursiveClients">Recursions in progress at the moment of reading (a gauge, not a counter).</param>
public sealed record DnsMetrics(
    long Queries,
    long NoError,
    long NxDomain,
    long ServFail,
    long Refused,
    long Recursion,
    long CacheHits,
    long CacheMisses,
    long UpstreamQueries,
    long UpstreamTimeouts,
    long UpstreamServFail,
    long Lame,
    long ValidationFailures,
    IReadOnlyList<long> Rtt,
    long RecursiveClients)
{
    private static readonly string[] RttKeys = ["QryRTT10", "QryRTT100", "QryRTT500", "QryRTT800", "QryRTT1600", "QryRTT1600+"];

    public static DnsMetrics From(BindStats stats)
    {
        long Server(string key) => stats.Server.GetValueOrDefault(key);
        long Resolver(string key) => stats.Resolver.GetValueOrDefault(key);
        return new DnsMetrics(
            Queries: Server("Requestv4") + Server("Requestv6"),
            NoError: stats.Rcodes.GetValueOrDefault("NOERROR"),
            NxDomain: stats.Rcodes.GetValueOrDefault("NXDOMAIN"),
            ServFail: stats.Rcodes.GetValueOrDefault("SERVFAIL"),
            Refused: stats.Rcodes.GetValueOrDefault("REFUSED"),
            Recursion: Server("QryRecursion"),
            CacheHits: stats.Cache.GetValueOrDefault("QueryHits"),
            CacheMisses: stats.Cache.GetValueOrDefault("QueryMisses"),
            UpstreamQueries: Resolver("Queryv4") + Resolver("Queryv6"),
            UpstreamTimeouts: Resolver("QueryTimeout"),
            UpstreamServFail: Resolver("SERVFAIL"),
            Lame: Resolver("Lame"),
            ValidationFailures: Resolver("ValFail"),
            Rtt: RttKeys.Select(Resolver).ToList(),
            RecursiveClients: Server("RecursClients"));
    }

    /// <summary>
    /// The increase from <paramref name="previous"/> to <paramref name="current"/>. If any counter went down,
    /// bind restarted and everything in <paramref name="current"/> is new.
    /// </summary>
    public static DnsMetrics Delta(DnsMetrics previous, DnsMetrics current)
    {
        var a = previous.Counters().ToArray();
        var b = current.Counters().ToArray();
        var restarted = a.Zip(b).Any(p => p.Second < p.First);
        var d = restarted ? b : a.Zip(b, (x, y) => y - x).ToArray();
        return new DnsMetrics(d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7], d[8], d[9], d[10], d[11], d[12],
            d[13..19], current.RecursiveClients);
    }

    private IEnumerable<long> Counters() =>
    [
        Queries, NoError, NxDomain, ServFail, Refused, Recursion, CacheHits, CacheMisses,
        UpstreamQueries, UpstreamTimeouts, UpstreamServFail, Lame, ValidationFailures, .. Rtt,
    ];
}
