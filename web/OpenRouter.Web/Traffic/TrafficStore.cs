using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Traffic;

/// <summary>One minute of traffic, as written by the sampler.</summary>
/// <param name="Minute">Start of the minute, Unix seconds (UTC).</param>
public sealed record MinuteSample(long Minute, IReadOnlyList<ClientTraffic> Clients, IReadOnlyList<LinkTraffic> Links);

/// <summary>
/// Traffic history in SQLite: one row per client (and per router interface) per minute.
/// Older months can be moved into self-contained archive files with the same schema,
/// and a consistent copy of the live database can be taken at any time (<see cref="SnapshotAsync"/>).
/// </summary>
public sealed class TrafficStore(IOptions<RouterOptions> options)
{
    private const int SchemaVersion = 1;

    // minute: Unix seconds at the start of the minute (UTC). Bytes are per minute.
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS {0}clients (
            id INTEGER PRIMARY KEY,
            key TEXT NOT NULL UNIQUE,      -- MAC address, or 'unknown'
            name TEXT,
            network TEXT,
            first_seen INTEGER NOT NULL,
            last_seen INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS {0}client_minutes (
            minute INTEGER NOT NULL,
            client_id INTEGER NOT NULL REFERENCES clients(id),
            down_bytes INTEGER NOT NULL,
            up_bytes INTEGER NOT NULL,
            PRIMARY KEY (minute, client_id)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS {0}links (
            interface TEXT PRIMARY KEY,
            role TEXT NOT NULL             -- 'WAN', or the network's name
        );
        CREATE TABLE IF NOT EXISTS {0}link_minutes (
            minute INTEGER NOT NULL,
            interface TEXT NOT NULL,
            rx_bytes INTEGER NOT NULL,
            tx_bytes INTEGER NOT NULL,
            PRIMARY KEY (minute, interface)
        ) WITHOUT ROWID;
        """;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public string DatabasePath => options.Value.TrafficDatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = await OpenAsync(initialize: false, cancellationToken);
            // WAL lets the page read while the sampler writes.
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
            await ExecuteAsync(connection, string.Format(CultureInfo.InvariantCulture, Schema, ""), cancellationToken);
            await ExecuteAsync(connection, $"PRAGMA user_version = {SchemaVersion};", cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task WriteAsync(IReadOnlyList<MinuteSample> samples, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var upsertClient = Command(connection, """
            INSERT INTO clients (key, name, network, first_seen, last_seen) VALUES ($key, $name, $network, $minute, $minute)
            ON CONFLICT (key) DO UPDATE SET
                name = coalesce(excluded.name, name),
                network = coalesce(excluded.network, network),
                last_seen = max(last_seen, excluded.last_seen)
            RETURNING id;
            """);
        await using var insertClientMinute = Command(connection, """
            INSERT INTO client_minutes (minute, client_id, down_bytes, up_bytes) VALUES ($minute, $client, $down, $up)
            ON CONFLICT (minute, client_id) DO UPDATE SET
                down_bytes = down_bytes + excluded.down_bytes,
                up_bytes = up_bytes + excluded.up_bytes;
            """);
        await using var insertLinkMinute = Command(connection, """
            INSERT INTO link_minutes (minute, interface, rx_bytes, tx_bytes) VALUES ($minute, $interface, $rx, $tx)
            ON CONFLICT (minute, interface) DO UPDATE SET
                rx_bytes = rx_bytes + excluded.rx_bytes,
                tx_bytes = tx_bytes + excluded.tx_bytes;
            """);

        foreach (var sample in samples)
        {
            foreach (var client in sample.Clients)
            {
                upsertClient.Parameters.Clear();
                upsertClient.Parameters.AddWithValue("$key", client.Key);
                upsertClient.Parameters.AddWithValue("$name", (object?)client.Name ?? DBNull.Value);
                upsertClient.Parameters.AddWithValue("$network", (object?)client.Network ?? DBNull.Value);
                upsertClient.Parameters.AddWithValue("$minute", sample.Minute);
                var id = (long)(await upsertClient.ExecuteScalarAsync(cancellationToken))!;

                insertClientMinute.Parameters.Clear();
                insertClientMinute.Parameters.AddWithValue("$minute", sample.Minute);
                insertClientMinute.Parameters.AddWithValue("$client", id);
                insertClientMinute.Parameters.AddWithValue("$down", client.DownBytes);
                insertClientMinute.Parameters.AddWithValue("$up", client.UpBytes);
                await insertClientMinute.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var link in sample.Links)
            {
                insertLinkMinute.Parameters.Clear();
                insertLinkMinute.Parameters.AddWithValue("$minute", sample.Minute);
                insertLinkMinute.Parameters.AddWithValue("$interface", link.Interface);
                insertLinkMinute.Parameters.AddWithValue("$rx", link.RxBytes);
                insertLinkMinute.Parameters.AddWithValue("$tx", link.TxBytes);
                await insertLinkMinute.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records what each sampled interface is (WAN, LAN, ...), so charts can find the WAN.</summary>
    public async Task SetLinkRolesAsync(IEnumerable<(string Interface, string Role)> links, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Command(connection, """
            INSERT INTO links (interface, role) VALUES ($interface, $role)
            ON CONFLICT (interface) DO UPDATE SET role = excluded.role;
            """);
        foreach (var (name, role) in links)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$interface", name);
            command.Parameters.AddWithValue("$role", role);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        await using var command = Command(connection, "SELECT NOT EXISTS (SELECT 1 FROM link_minutes) AND NOT EXISTS (SELECT 1 FROM client_minutes);");
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// Traffic rates for [<paramref name="from"/>, <paramref name="to"/>) in buckets of <paramref name="bucketSeconds"/>:
    /// the WAN's download/upload and the <paramref name="top"/> busiest clients in <paramref name="direction"/>,
    /// with everyone else summed into one "other" series. Buckets the sampler didn't cover are null.
    /// </summary>
    public async Task<TrafficSeries> GetSeriesAsync(
        long from, long to, int bucketSeconds, TrafficDirection direction, int top, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        var bytesColumn = direction == TrafficDirection.Down ? "down_bytes" : "up_bytes";

        var links = new List<TrafficSeriesBuilder.LinkRow>();
        await using (var command = Command(connection, """
            SELECT (m.minute - $from) / $bucket, m.interface, coalesce(l.role, ''), sum(m.rx_bytes), sum(m.tx_bytes), count(*)
            FROM link_minutes m LEFT JOIN links l ON l.interface = m.interface
            WHERE m.minute >= $from AND m.minute < $to
            GROUP BY 1, 2;
            """, ("$from", from), ("$to", to), ("$bucket", bucketSeconds)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                links.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5)));
        }

        var clients = new List<TrafficSeriesBuilder.ClientRow>();
        await using (var command = Command(connection, $"""
            SELECT (m.minute - $from) / $bucket, c.key, c.name, c.network, sum(m.{bytesColumn})
            FROM client_minutes m JOIN clients c ON c.id = m.client_id
            WHERE m.minute >= $from AND m.minute < $to
            GROUP BY 1, 2;
            """, ("$from", from), ("$to", to), ("$bucket", bucketSeconds)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                clients.Add(new(reader.GetInt32(0), reader.GetString(1), Nullable(reader, 2), Nullable(reader, 3), reader.GetInt64(4)));
        }

        return TrafficSeriesBuilder.Build(from, to, bucketSeconds, links, clients, top);
    }

    /// <summary>Per-client totals over [<paramref name="from"/>, <paramref name="to"/>), busiest first.</summary>
    public async Task<IReadOnlyList<ClientTotal>> GetClientTotalsAsync(long from, long to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        await using var command = Command(connection, """
            SELECT c.key, c.name, c.network, sum(m.down_bytes), sum(m.up_bytes), max(m.down_bytes), max(m.up_bytes), c.last_seen
            FROM client_minutes m JOIN clients c ON c.id = m.client_id
            WHERE m.minute >= $from AND m.minute < $to
            GROUP BY c.id
            ORDER BY sum(m.down_bytes) + sum(m.up_bytes) DESC;
            """, ("$from", from), ("$to", to));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var totals = new List<ClientTotal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            totals.Add(new ClientTotal(
                reader.GetString(0), Nullable(reader, 1), Nullable(reader, 2),
                reader.GetInt64(3), reader.GetInt64(4),
                PeakDownBitsPerSecond: reader.GetInt64(5) * 8 / 60.0,
                PeakUpBitsPerSecond: reader.GetInt64(6) * 8 / 60.0,
                LastSeen: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7))));
        }
        return totals;
    }

    public async Task<TrafficStoreInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        await using var command = Command(connection, """
            SELECT (SELECT count(*) FROM client_minutes), (SELECT count(*) FROM link_minutes), (SELECT count(*) FROM clients),
                   (SELECT min(minute) FROM link_minutes), (SELECT max(minute) FROM link_minutes);
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        DateTimeOffset? Time(int column) =>
            reader.IsDBNull(column) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(column));

        var file = new FileInfo(DatabasePath);
        var wal = new FileInfo(DatabasePath + "-wal");
        return new TrafficStoreInfo(
            DatabasePath,
            SizeBytes: (file.Exists ? file.Length : 0) + (wal.Exists ? wal.Length : 0),
            ClientRows: reader.GetInt64(0),
            LinkRows: reader.GetInt64(1),
            Clients: reader.GetInt64(2),
            Oldest: Time(3),
            Newest: Time(4));
    }

    /// <summary>Writes a consistent copy of the live database to <paramref name="path"/> (SQLite's VACUUM INTO).</summary>
    public async Task SnapshotAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        await using var command = Command(connection, "VACUUM INTO $path;", ("$path", path));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Moves every minute before <paramref name="cutoff"/> into one archive file per calendar month (UTC),
    /// <c>traffic-YYYY-MM.db</c> in <paramref name="archiveDir"/>. Each file has the same schema and the
    /// clients it refers to, so it can be opened on its own. Returns the files written to.
    /// </summary>
    public async Task<IReadOnlyList<string>> ArchiveAsync(DateTimeOffset cutoff, string archiveDir, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(initialize: true, cancellationToken);
        var cutoffSeconds = cutoff.ToUnixTimeSeconds();

        long? oldest;
        await using (var command = Command(connection,
            "SELECT min(m) FROM (SELECT min(minute) AS m FROM client_minutes UNION ALL SELECT min(minute) FROM link_minutes);"))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            oldest = value is long l ? l : null;
        }
        if (oldest is null || oldest >= cutoffSeconds)
            return [];

        Directory.CreateDirectory(archiveDir);
        var written = new List<string>();
        var month = new DateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(oldest.Value).Year, DateTimeOffset.FromUnixTimeSeconds(oldest.Value).Month, 1, 0, 0, 0, TimeSpan.Zero);
        while (month.ToUnixTimeSeconds() < cutoffSeconds)
        {
            var next = month.AddMonths(1);
            var start = month.ToUnixTimeSeconds();
            var end = Math.Min(next.ToUnixTimeSeconds(), cutoffSeconds);
            var path = Path.Combine(archiveDir, $"traffic-{month:yyyy-MM}.db");

            await ExecuteAsync(connection, "ATTACH DATABASE $path AS archive;", cancellationToken, ("$path", path));
            try
            {
                await ExecuteAsync(connection, string.Format(CultureInfo.InvariantCulture, Schema, "archive."), cancellationToken);
                await ExecuteAsync(connection, $"PRAGMA archive.user_version = {SchemaVersion};", cancellationToken);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                (string, object)[] range = [("$start", start), ("$end", end)];
                await ExecuteAsync(connection, """
                    INSERT OR REPLACE INTO archive.clients SELECT * FROM clients
                    WHERE id IN (SELECT DISTINCT client_id FROM client_minutes WHERE minute >= $start AND minute < $end);
                    INSERT OR REPLACE INTO archive.links SELECT * FROM links;
                    INSERT OR IGNORE INTO archive.client_minutes SELECT * FROM client_minutes WHERE minute >= $start AND minute < $end;
                    INSERT OR IGNORE INTO archive.link_minutes SELECT * FROM link_minutes WHERE minute >= $start AND minute < $end;
                    DELETE FROM client_minutes WHERE minute >= $start AND minute < $end;
                    DELETE FROM link_minutes WHERE minute >= $start AND minute < $end;
                    """, cancellationToken, range);
                await transaction.CommitAsync(cancellationToken);
                written.Add(path);
            }
            finally
            {
                await ExecuteAsync(connection, "DETACH DATABASE archive;", CancellationToken.None);
            }
            month = next;
        }
        return written;
    }

    /// <summary>An open connection to the history database, for other stores that keep their tables in it (DNS).</summary>
    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken) => OpenAsync(initialize: true, cancellationToken);

    private async Task<SqliteConnection> OpenAsync(bool initialize, CancellationToken cancellationToken)
    {
        if (initialize)
            await InitializeAsync(cancellationToken);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;", cancellationToken);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Nullable(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
}

public sealed record ClientTotal(
    string Key,
    string? Name,
    string? Network,
    long DownBytes,
    long UpBytes,
    double PeakDownBitsPerSecond,
    double PeakUpBitsPerSecond,
    DateTimeOffset LastSeen);

public sealed record TrafficStoreInfo(
    string Path,
    long SizeBytes,
    long ClientRows,
    long LinkRows,
    long Clients,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest);
