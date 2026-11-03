using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OpenRouter.Client.Storage;

/// <summary>A measurement run: when, on which machine and interface.</summary>
public sealed record RunInfo(
    long Id, DateTimeOffset Started, DateTimeOffset? Ended, string Host, string Interface, string? Mac,
    string? Ipv4, string? Gateway, string? Note);

/// <summary>Everything recorded in one run, for the report.</summary>
public sealed record RunData(
    RunInfo Run,
    IReadOnlyList<PingSample> Pings,
    IReadOnlyList<DnsSample> Dns,
    IReadOnlyList<HttpSample> Http,
    IReadOnlyList<WifiSample> Wifi,
    IReadOnlyList<NetEvent> Events);

/// <summary>
/// SQLite storage for runs. Times are Unix milliseconds (UTC) in <c>time_ms</c>, so rows line up with the router's
/// history (Unix seconds per minute: <c>time_ms / 60000 * 60</c>).
/// </summary>
public sealed class ResultStore(string path) : IAsyncDisposable
{
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY,
            started_ms INTEGER NOT NULL,
            ended_ms INTEGER,
            host TEXT NOT NULL,
            interface TEXT NOT NULL,
            mac TEXT,                 -- the laptop's MAC on that interface: the router's Traffic history uses it as the device key
            ipv4 TEXT,
            gateway TEXT,
            note TEXT
        );
        CREATE TABLE IF NOT EXISTS pings (
            run_id INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            target TEXT NOT NULL,
            label TEXT NOT NULL,      -- gateway, internet, ...
            ms REAL                   -- null: no reply within 2 s
        );
        CREATE TABLE IF NOT EXISTS dns (
            run_id INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            server TEXT NOT NULL,
            name TEXT NOT NULL,
            ms REAL,
            rcode INTEGER,
            error TEXT
        );
        CREATE TABLE IF NOT EXISTS http (
            run_id INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            url TEXT NOT NULL,
            dns_ms REAL, connect_ms REAL, tls_ms REAL, first_byte_ms REAL,
            status INTEGER,
            error TEXT
        );
        CREATE TABLE IF NOT EXISTS wifi (
            run_id INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            interface TEXT NOT NULL,
            connected INTEGER NOT NULL,
            ssid TEXT, bssid TEXT,    -- bssid: which access point (or mesh node, or band of it)
            freq_mhz REAL,
            signal_dbm INTEGER, signal_avg_dbm INTEGER,
            tx_mbps REAL, rx_mbps REAL,
            tx_packets INTEGER, tx_failed INTEGER, tx_retries INTEGER, beacon_loss INTEGER
        );
        CREATE TABLE IF NOT EXISTS events (
            run_id INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            kind TEXT NOT NULL,
            detail TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS pings_run ON pings (run_id, time_ms);
        CREATE INDEX IF NOT EXISTS dns_run ON dns (run_id, time_ms);
        CREATE INDEX IF NOT EXISTS http_run ON http (run_id, time_ms);
        CREATE INDEX IF NOT EXISTS wifi_run ON wifi (run_id, time_ms);
        CREATE INDEX IF NOT EXISTS events_run ON events (run_id, time_ms);
        """;

    private SqliteConnection? _connection;

    public static async Task<ResultStore> OpenAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var store = new ResultStore(path);
        store._connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await store._connection.OpenAsync(cancellationToken);
        await store.ExecuteAsync(Schema, cancellationToken);
        return store;
    }

    public string DatabasePath => path;

    private SqliteConnection Connection => _connection ?? throw new InvalidOperationException("Store is not open.");

    public async Task<long> StartRunAsync(DateTimeOffset started, string host, string iface, string? mac, string? ipv4, string? gateway,
        string? note, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO runs (started_ms, host, interface, mac, ipv4, gateway, note) VALUES ($started, $host, $iface, $mac, $ipv4, $gateway, $note)
            RETURNING id;
            """, ("$started", started.ToUnixTimeMilliseconds()), ("$host", host), ("$iface", iface), ("$mac", mac), ("$ipv4", ipv4),
            ("$gateway", gateway), ("$note", note));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task EndRunAsync(long runId, DateTimeOffset ended, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE runs SET ended_ms = $ended WHERE id = $id;", ("$ended", ended.ToUnixTimeMilliseconds()), ("$id", runId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes a batch of samples (any of the sample records) in one transaction.</summary>
    public async Task WriteAsync(long runId, IReadOnlyList<object> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
            return;
        await using var transaction = (SqliteTransaction)await Connection.BeginTransactionAsync(cancellationToken);
        foreach (var sample in samples)
        {
            await using var command = sample switch
            {
                PingSample p => Command("INSERT INTO pings VALUES ($run, $t, $target, $label, $ms);",
                    ("$run", runId), ("$t", Ms(p.Time)), ("$target", p.Target), ("$label", p.Label), ("$ms", p.Milliseconds)),
                DnsSample d => Command("INSERT INTO dns VALUES ($run, $t, $server, $name, $ms, $rcode, $error);",
                    ("$run", runId), ("$t", Ms(d.Time)), ("$server", d.Server), ("$name", d.Name), ("$ms", d.Milliseconds),
                    ("$rcode", d.ResponseCode), ("$error", d.Error)),
                HttpSample h => Command("INSERT INTO http VALUES ($run, $t, $url, $dns, $connect, $tls, $first, $status, $error);",
                    ("$run", runId), ("$t", Ms(h.Time)), ("$url", h.Url), ("$dns", h.DnsMs), ("$connect", h.ConnectMs), ("$tls", h.TlsMs),
                    ("$first", h.FirstByteMs), ("$status", h.Status), ("$error", h.Error)),
                WifiSample w => Command("""
                    INSERT INTO wifi VALUES ($run, $t, $iface, $connected, $ssid, $bssid, $freq, $signal, $avg, $tx, $rx, $txp, $txf, $txr, $beacon);
                    """,
                    ("$run", runId), ("$t", Ms(w.Time)), ("$iface", w.Interface), ("$connected", w.Connected ? 1 : 0), ("$ssid", w.Ssid),
                    ("$bssid", w.Bssid), ("$freq", w.FrequencyMhz), ("$signal", w.SignalDbm), ("$avg", w.SignalAverageDbm),
                    ("$tx", w.TxBitrateMbps), ("$rx", w.RxBitrateMbps), ("$txp", w.TxPackets), ("$txf", w.TxFailed),
                    ("$txr", w.TxRetries), ("$beacon", w.BeaconLoss)),
                NetEvent e => Command("INSERT INTO events VALUES ($run, $t, $kind, $detail);",
                    ("$run", runId), ("$t", Ms(e.Time)), ("$kind", e.Kind), ("$detail", e.Detail)),
                _ => throw new ArgumentException($"Unknown sample {sample.GetType().Name}."),
            };
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunInfo>> GetRunsAsync(CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT id, started_ms, ended_ms, host, interface, mac, ipv4, gateway, note FROM runs ORDER BY id;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var runs = new List<RunInfo>();
        while (await reader.ReadAsync(cancellationToken))
            runs.Add(ReadRun(reader));
        return runs;
    }

    public async Task<RunData?> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        var run = (await GetRunsAsync(cancellationToken)).FirstOrDefault(r => r.Id == runId);
        if (run is null)
            return null;

        async Task<List<T>> Read<T>(string sql, Func<SqliteDataReader, T> map)
        {
            await using var command = Command(sql, ("$run", runId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(map(reader));
            return rows;
        }

        return new RunData(
            run,
            await Read("SELECT time_ms, target, label, ms FROM pings WHERE run_id = $run ORDER BY time_ms;",
                r => new PingSample(Time(r, 0), r.GetString(1), r.GetString(2), Double(r, 3))),
            await Read("SELECT time_ms, server, name, ms, rcode, error FROM dns WHERE run_id = $run ORDER BY time_ms;",
                r => new DnsSample(Time(r, 0), r.GetString(1), r.GetString(2), Double(r, 3), Int(r, 4), Text(r, 5))),
            await Read("SELECT time_ms, url, dns_ms, connect_ms, tls_ms, first_byte_ms, status, error FROM http WHERE run_id = $run ORDER BY time_ms;",
                r => new HttpSample(Time(r, 0), r.GetString(1), Double(r, 2), Double(r, 3), Double(r, 4), Double(r, 5), Int(r, 6), Text(r, 7))),
            await Read("""
                SELECT time_ms, interface, connected, ssid, bssid, freq_mhz, signal_dbm, signal_avg_dbm, tx_mbps, rx_mbps,
                       tx_packets, tx_failed, tx_retries, beacon_loss
                FROM wifi WHERE run_id = $run ORDER BY time_ms;
                """,
                r => new WifiSample(Time(r, 0), r.GetString(1), r.GetInt32(2) == 1, Text(r, 3), Text(r, 4), Double(r, 5), Int(r, 6), Int(r, 7),
                    Double(r, 8), Double(r, 9), Long(r, 10), Long(r, 11), Long(r, 12), Long(r, 13))),
            await Read("SELECT time_ms, kind, detail FROM events WHERE run_id = $run ORDER BY time_ms;",
                r => new NetEvent(Time(r, 0), r.GetString(1), r.GetString(2))));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private static RunInfo ReadRun(SqliteDataReader r) => new(
        r.GetInt64(0), Time(r, 1), r.IsDBNull(2) ? null : Time(r, 2), r.GetString(3), r.GetString(4), Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8));

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static DateTimeOffset Time(SqliteDataReader r, int i) => DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i));
    private static double? Double(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
