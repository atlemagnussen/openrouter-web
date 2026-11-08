using Microsoft.Data.Sqlite;

namespace OpenRouter.Client.Storage;

/// <summary>Measurements over a time range, in equal buckets; null where nothing was measured (asleep, collector off).</summary>
public sealed record Timeline(
    DateTimeOffset From,
    TimeSpan Bucket,
    IReadOnlyList<DateTimeOffset> Times,
    double?[] SignalDbm,
    double?[] TxMbps,
    double?[] RouterMs,
    double?[] InternetMs,
    double?[] RouterLossPercent,
    double?[] InternetLossPercent);

/// <summary>The last couple of minutes, for the status bar and the tray icon.</summary>
public sealed record LiveStatus(
    WifiSample? Wifi,
    double? RouterMs,
    double? RouterLossPercent,
    double? InternetMs,
    double? InternetLossPercent,
    DateTimeOffset? LastSample);

/// <summary>
/// Reads the collector's database for the desktop app: across runs, by time. Opens its own connection, so it
/// can read while the collector writes (the database is in WAL mode).
/// </summary>
public sealed class HistoryReader : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private HistoryReader(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens the database, creating it (empty) if the collector hasn't run yet.</summary>
    public static async Task<HistoryReader> OpenAsync(string path, CancellationToken cancellationToken)
    {
        // The store creates the file and schema the same way the collector does.
        await (await ResultStore.OpenAsync(path, cancellationToken)).DisposeAsync();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        return new HistoryReader(connection);
    }

    /// <summary>A bucket size that gives a few hundred points across <paramref name="range"/>, at least 2 s (the Wi-Fi interval).</summary>
    public static TimeSpan BucketFor(TimeSpan range) =>
        TimeSpan.FromSeconds(Math.Max(2, Math.Ceiling(range.TotalSeconds / 600 / 2) * 2));

    public async Task<Timeline> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken cancellationToken)
    {
        var bucketMs = (long)bucket.TotalMilliseconds;
        var start = from.ToUnixTimeMilliseconds() / bucketMs * bucketMs;
        var count = (int)((to.ToUnixTimeMilliseconds() - start) / bucketMs) + 1;
        var times = Enumerable.Range(0, count).Select(i => DateTimeOffset.FromUnixTimeMilliseconds(start + i * bucketMs)).ToList();
        var signal = new double?[count];
        var tx = new double?[count];
        var routerMs = new double?[count];
        var internetMs = new double?[count];
        var routerLoss = new double?[count];
        var internetLoss = new double?[count];

        int? Index(long bucketStart) => (bucketStart - start) / bucketMs is var i && i >= 0 && i < count ? (int)i : null;

        await foreach (var r in QueryAsync("""
            SELECT time_ms / $b * $b, avg(signal_dbm), avg(tx_mbps)
            FROM wifi WHERE time_ms >= $from AND time_ms < $to AND connected = 1
            GROUP BY 1;
            """, cancellationToken, ("$b", bucketMs), ("$from", start), ("$to", to.ToUnixTimeMilliseconds())))
        {
            if (Index(r.GetInt64(0)) is not { } i)
                continue;
            signal[i] = Double(r, 1);
            tx[i] = Double(r, 2);
        }

        // Router: the IPv4 gateway. Internet: every internet target together, IPv4 and IPv6.
        await foreach (var r in QueryAsync("""
            SELECT time_ms / $b * $b, label = 'gateway', avg(ms), 100.0 * sum(ms IS NULL) / count(*)
            FROM pings WHERE time_ms >= $from AND time_ms < $to AND (label = 'gateway' OR label LIKE 'internet%')
            GROUP BY 1, 2;
            """, cancellationToken, ("$b", bucketMs), ("$from", start), ("$to", to.ToUnixTimeMilliseconds())))
        {
            if (Index(r.GetInt64(0)) is not { } i)
                continue;
            if (r.GetInt64(1) == 1)
                (routerMs[i], routerLoss[i]) = (Double(r, 2), Double(r, 3));
            else
                (internetMs[i], internetLoss[i]) = (Double(r, 2), Double(r, 3));
        }

        return new Timeline(DateTimeOffset.FromUnixTimeMilliseconds(start), bucket, times, signal, tx, routerMs, internetMs, routerLoss, internetLoss);
    }

    public async Task<IReadOnlyList<NetEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var events = new List<NetEvent>();
        await foreach (var r in QueryAsync("SELECT time_ms, kind, detail FROM events WHERE time_ms >= $from AND time_ms < $to ORDER BY time_ms;",
            cancellationToken, ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())))
            events.Add(new NetEvent(Time(r, 0), r.GetString(1), r.GetString(2)));
        return events;
    }

    /// <summary>Every network heard in scans within the range.</summary>
    public async Task<IReadOnlyList<ScanSample>> GetScansAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var scans = new List<ScanSample>();
        await foreach (var r in QueryAsync("""
            SELECT time_ms, bssid, ssid, freq_mhz, signal_dbm, associated, width_mhz, center_mhz
            FROM scans WHERE time_ms >= $from AND time_ms < $to ORDER BY time_ms;
            """, cancellationToken, ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())))
            scans.Add(new ScanSample(Time(r, 0), r.GetString(1), Text(r, 2), r.GetDouble(3), Int(r, 4), r.GetInt64(5) == 1)
            {
                WidthMhz = Int(r, 6),
                CenterMhz = r.IsDBNull(7) ? null : r.GetDouble(7),
            });
        return scans;
    }

    /// <summary>When each network was first heard, in all the history kept.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetFirstHeardAsync(CancellationToken cancellationToken)
    {
        var first = new Dictionary<string, DateTimeOffset>();
        await foreach (var r in QueryAsync("SELECT bssid, min(time_ms) FROM scans GROUP BY bssid;", cancellationToken))
            first[r.GetString(0)] = Time(r, 1);
        return first;
    }

    public async Task<LiveStatus> GetStatusAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken)
    {
        WifiSample? wifi = null;
        await foreach (var r in QueryAsync("""
            SELECT time_ms, interface, connected, ssid, bssid, freq_mhz, signal_dbm, signal_avg_dbm, tx_mbps, rx_mbps,
                   tx_packets, tx_failed, tx_retries, beacon_loss
            FROM wifi ORDER BY time_ms DESC LIMIT 1;
            """, cancellationToken))
            wifi = new WifiSample(Time(r, 0), r.GetString(1), r.GetInt64(2) == 1, Text(r, 3), Text(r, 4), Double(r, 5), Int(r, 6), Int(r, 7),
                Double(r, 8), Double(r, 9), Long(r, 10), Long(r, 11), Long(r, 12), Long(r, 13));

        double? routerMs = null, routerLoss = null, internetMs = null, internetLoss = null;
        await foreach (var r in QueryAsync("""
            SELECT label = 'gateway', avg(ms), 100.0 * sum(ms IS NULL) / count(*)
            FROM pings WHERE time_ms >= $from AND (label = 'gateway' OR label LIKE 'internet%')
            GROUP BY 1;
            """, cancellationToken, ("$from", (now - window).ToUnixTimeMilliseconds())))
        {
            if (r.GetInt64(0) == 1)
                (routerMs, routerLoss) = (Double(r, 1), Double(r, 2));
            else
                (internetMs, internetLoss) = (Double(r, 1), Double(r, 2));
        }

        DateTimeOffset? last = null;
        await foreach (var r in QueryAsync("SELECT max(time_ms) FROM pings;", cancellationToken))
            last = r.IsDBNull(0) ? null : Time(r, 0);
        return new LiveStatus(wifi, routerMs, routerLoss, internetMs, internetLoss, last);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async IAsyncEnumerable<SqliteDataReader> QueryAsync(string sql,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return reader;
    }

    private static DateTimeOffset Time(SqliteDataReader r, int i) => DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
    private static double? Double(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
