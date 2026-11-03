using Microsoft.Data.Sqlite;
using OpenRouter.Web.Traffic;

namespace OpenRouter.Web.Dns;

/// <summary>One probe: a lookup of the probe name through bind or straight at one of its forwarders.</summary>
/// <param name="Kind"><c>bind</c> (an address clients use) or <c>forwarder</c>.</param>
/// <param name="Milliseconds">Null when no answer came back.</param>
public sealed record DnsProbeResult(string Target, string Kind, double? Milliseconds, int? ResponseCode, string? Error);

/// <summary>DNS history, in the same SQLite file as the traffic history: bind's counters per minute and the probes.</summary>
public sealed class DnsStore(TrafficStore database)
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS dns_minutes (
            minute INTEGER PRIMARY KEY,    -- Unix seconds at the start of the minute (UTC); counts are per minute
            queries INTEGER NOT NULL,
            noerror INTEGER NOT NULL,
            nxdomain INTEGER NOT NULL,
            servfail INTEGER NOT NULL,
            refused INTEGER NOT NULL,
            recursion INTEGER NOT NULL,
            cache_hits INTEGER NOT NULL,
            cache_misses INTEGER NOT NULL,
            upstream_queries INTEGER NOT NULL,
            upstream_timeouts INTEGER NOT NULL,
            upstream_servfail INTEGER NOT NULL,
            lame INTEGER NOT NULL,
            validation_failures INTEGER NOT NULL,
            rtt10 INTEGER NOT NULL,        -- upstream answers by round-trip time
            rtt100 INTEGER NOT NULL,
            rtt500 INTEGER NOT NULL,
            rtt800 INTEGER NOT NULL,
            rtt1600 INTEGER NOT NULL,
            rtt_slow INTEGER NOT NULL,
            recursive_clients INTEGER NOT NULL  -- gauge at the end of the minute
        );
        CREATE TABLE IF NOT EXISTS dns_probes (
            minute INTEGER NOT NULL,
            target TEXT NOT NULL,          -- server address
            kind TEXT NOT NULL,            -- 'bind' or 'forwarder'
            ms REAL,                       -- null: no answer
            rcode INTEGER,
            error TEXT,
            PRIMARY KEY (minute, target)
        ) WITHOUT ROWID;
        """;

    private const string MetricColumns =
        "queries, noerror, nxdomain, servfail, refused, recursion, cache_hits, cache_misses, upstream_queries, " +
        "upstream_timeouts, upstream_servfail, lame, validation_failures, rtt10, rtt100, rtt500, rtt800, rtt1600, rtt_slow, recursive_clients";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM dns_minutes) AND NOT EXISTS (SELECT 1 FROM dns_probes);";
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task WriteAsync(
        IReadOnlyList<(long Minute, DnsMetrics? Metrics, IReadOnlyList<DnsProbeResult> Probes)> minutes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var insertMetrics = connection.CreateCommand();
        insertMetrics.CommandText = $"""
            INSERT OR REPLACE INTO dns_minutes (minute, {MetricColumns})
            VALUES ($minute, $p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p11, $p12, $p13, $p14, $p15, $p16, $p17, $p18, $p19);
            """;
        await using var insertProbe = connection.CreateCommand();
        insertProbe.CommandText = """
            INSERT OR REPLACE INTO dns_probes (minute, target, kind, ms, rcode, error) VALUES ($minute, $target, $kind, $ms, $rcode, $error);
            """;

        foreach (var (minute, metrics, probes) in minutes)
        {
            if (metrics is not null)
            {
                long[] values =
                [
                    metrics.Queries, metrics.NoError, metrics.NxDomain, metrics.ServFail, metrics.Refused, metrics.Recursion,
                    metrics.CacheHits, metrics.CacheMisses, metrics.UpstreamQueries, metrics.UpstreamTimeouts, metrics.UpstreamServFail,
                    metrics.Lame, metrics.ValidationFailures, .. metrics.Rtt, metrics.RecursiveClients,
                ];
                insertMetrics.Parameters.Clear();
                insertMetrics.Parameters.AddWithValue("$minute", minute);
                for (var i = 0; i < values.Length; i++)
                    insertMetrics.Parameters.AddWithValue($"$p{i}", values[i]);
                await insertMetrics.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var probe in probes)
            {
                insertProbe.Parameters.Clear();
                insertProbe.Parameters.AddWithValue("$minute", minute);
                insertProbe.Parameters.AddWithValue("$target", probe.Target);
                insertProbe.Parameters.AddWithValue("$kind", probe.Kind);
                insertProbe.Parameters.AddWithValue("$ms", (object?)probe.Milliseconds ?? DBNull.Value);
                insertProbe.Parameters.AddWithValue("$rcode", (object?)probe.ResponseCode ?? DBNull.Value);
                insertProbe.Parameters.AddWithValue("$error", (object?)probe.Error ?? DBNull.Value);
                await insertProbe.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Chart series for [<paramref name="from"/>, <paramref name="to"/>) in buckets; counts are averages per minute.</summary>
    public async Task<DnsSeries> GetSeriesAsync(long from, long to, int bucketSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var count = (int)((to - from) / bucketSeconds);
        var timestamps = Enumerable.Range(0, count).Select(i => from + (long)i * bucketSeconds).ToList();

        // Columns summed per bucket, in the order DnsSeries expects.
        // The chart merges 500–800 and 800–1600 ms into one band.
        string[] columns =
        [
            "noerror", "nxdomain", "queries - noerror - nxdomain - servfail", "servfail",
            "rtt10", "rtt100", "rtt500", "rtt800 + rtt1600", "rtt_slow", "upstream_timeouts",
        ];
        string[] upstreamBands = ["< 10 ms", "10–100 ms", "100–500 ms", "500 ms–1.6 s", "> 1.6 s", "Timed out"];
        var sums = columns.Select(_ => new double?[count]).ToArray();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT (minute - $from) / $bucket, count(*), {string.Join(", ", columns.Select(c => $"sum(max({c}, 0))"))}
                FROM dns_minutes WHERE minute >= $from AND minute < $to GROUP BY 1;
                """;
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            command.Parameters.AddWithValue("$bucket", bucketSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bucket = reader.GetInt32(0);
                if (bucket < 0 || bucket >= count)
                    continue;
                var minutes = reader.GetInt32(1);
                for (var c = 0; c < columns.Length; c++)
                    sums[c][bucket] = reader.GetDouble(c + 2) / minutes;
            }
        }

        var probes = new Dictionary<string, (string Kind, double?[] Ms)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT target, kind, (minute - $from) / $bucket, avg(ms)
                FROM dns_probes WHERE minute >= $from AND minute < $to GROUP BY 1, 3 ORDER BY kind, target;
                """;
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            command.Parameters.AddWithValue("$bucket", bucketSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bucket = reader.GetInt32(2);
                if (bucket < 0 || bucket >= count)
                    continue;
                var target = reader.GetString(0);
                if (!probes.TryGetValue(target, out var series))
                    probes[target] = series = (reader.GetString(1), new double?[count]);
                series.Ms[bucket] = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            }
        }

        return new DnsSeries(
            from, bucketSeconds, timestamps,
            Outcomes: [new("NOERROR", sums[0]), new("NXDOMAIN", sums[1]), new("Other", sums[2]), new("SERVFAIL", sums[3])],
            Upstream: upstreamBands.Select((band, i) => new DnsCountSeries(band, sums[4 + i])).ToList(),
            Probes: probes
                .OrderBy(p => p.Value.Kind == "bind" ? 0 : 1)
                .Select(p => new DnsProbeSeries(p.Key, p.Value.Kind, p.Value.Ms))
                .ToList());
    }

    public async Task<DnsTotals> GetTotalsAsync(long from, long to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*), coalesce(sum(queries), 0), coalesce(sum(noerror), 0), coalesce(sum(nxdomain), 0), coalesce(sum(servfail), 0),
                   coalesce(sum(cache_hits), 0), coalesce(sum(cache_misses), 0), coalesce(sum(upstream_queries), 0),
                   coalesce(sum(upstream_timeouts), 0), coalesce(sum(validation_failures), 0), coalesce(max(recursive_clients), 0)
            FROM dns_minutes WHERE minute >= $from AND minute < $to;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DnsTotals(
            Minutes: reader.GetInt32(0), Queries: reader.GetInt64(1), NoError: reader.GetInt64(2), NxDomain: reader.GetInt64(3),
            ServFail: reader.GetInt64(4), CacheHits: reader.GetInt64(5), CacheMisses: reader.GetInt64(6),
            UpstreamQueries: reader.GetInt64(7), UpstreamTimeouts: reader.GetInt64(8), ValidationFailures: reader.GetInt64(9),
            PeakRecursiveClients: reader.GetInt64(10));
    }

    /// <summary>Per probe target over the range: average and worst time, and how many lookups got no usable answer.</summary>
    public async Task<IReadOnlyList<DnsProbeSummary>> GetProbeSummaryAsync(long from, long to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target, kind, count(*), avg(ms), max(ms), sum(ms IS NULL OR coalesce(rcode, 0) <> 0),
                   (SELECT coalesce(error, 'rcode ' || rcode) FROM dns_probes p2
                    WHERE p2.target = p.target AND p2.minute >= $from AND p2.minute < $to AND (p2.ms IS NULL OR coalesce(p2.rcode, 0) <> 0)
                    ORDER BY p2.minute DESC LIMIT 1)
            FROM dns_probes p WHERE minute >= $from AND minute < $to
            GROUP BY target ORDER BY kind, target;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new List<DnsProbeSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new DnsProbeSummary(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return summaries;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await database.OpenConnectionAsync(cancellationToken);
        if (!_initialized)
        {
            await _initLock.WaitAsync(cancellationToken);
            try
            {
                if (!_initialized)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = Schema;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }
        return connection;
    }
}

/// <summary>DNS chart data. Counts are per minute (averaged over the bucket); null where nothing was recorded.</summary>
public sealed record DnsSeries(
    long From,
    int BucketSeconds,
    IReadOnlyList<long> Timestamps,
    IReadOnlyList<DnsCountSeries> Outcomes,
    IReadOnlyList<DnsCountSeries> Upstream,
    IReadOnlyList<DnsProbeSeries> Probes);

public sealed record DnsCountSeries(string Name, IReadOnlyList<double?> PerMinute);

public sealed record DnsProbeSeries(string Target, string Kind, IReadOnlyList<double?> Milliseconds);

public sealed record DnsTotals(
    int Minutes, long Queries, long NoError, long NxDomain, long ServFail, long CacheHits, long CacheMisses,
    long UpstreamQueries, long UpstreamTimeouts, long ValidationFailures, long PeakRecursiveClients)
{
    public double? Share(long part) => Queries > 0 ? (double)part / Queries : null;
    public double? CacheHitRatio => CacheHits + CacheMisses > 0 ? (double)CacheHits / (CacheHits + CacheMisses) : null;
    public double? TimeoutShare => UpstreamQueries > 0 ? (double)UpstreamTimeouts / UpstreamQueries : null;
}

public sealed record DnsProbeSummary(
    string Target, string Kind, int Count, double? AverageMs, double? MaxMs, int Failures, string? LastFailure);
