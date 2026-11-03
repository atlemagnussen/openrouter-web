using OpenRouter.Web.Traffic;

namespace OpenRouter.Web.Kernel;

/// <summary>What the link monitor knows about one interface in the current boot.</summary>
/// <param name="MonitoredSince">When the monitor first saw the interface in this boot; changes before that have no time.</param>
/// <param name="LastChange">The last time the link went down or up while being watched.</param>
/// <param name="RecentChanges">Link changes (down or up each count one) in the window asked for.</param>
public sealed record LinkHistory(DateTimeOffset MonitoredSince, long CarrierChangesAtStart, DateTimeOffset? LastChange, long RecentChanges);

/// <summary>
/// Link up/down history, in the history SQLite file: the kernel only counts carrier changes since boot,
/// so <see cref="LinkMonitor"/> records when the count goes up. Rows are per boot (kernel boot id).
/// </summary>
public sealed class LinkStore(TrafficStore database)
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS link_state (
            boot_id TEXT NOT NULL,
            interface TEXT NOT NULL,
            first_seen INTEGER NOT NULL,         -- Unix seconds when the monitor first saw it in this boot
            first_changes INTEGER NOT NULL,      -- carrier_changes at that moment
            changes INTEGER NOT NULL,            -- carrier_changes last seen
            PRIMARY KEY (boot_id, interface)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS link_events (
            boot_id TEXT NOT NULL,
            interface TEXT NOT NULL,
            time INTEGER NOT NULL,               -- when the monitor noticed, Unix seconds
            changes INTEGER NOT NULL,            -- how much carrier_changes went up
            PRIMARY KEY (boot_id, interface, time)
        ) WITHOUT ROWID;
        """;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>The last carrier_changes count seen per interface in this boot.</summary>
    public async Task<IReadOnlyDictionary<string, long>> GetKnownAsync(string bootId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT interface, changes FROM link_state WHERE boot_id = $boot;";
        command.Parameters.AddWithValue("$boot", bootId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var known = new Dictionary<string, long>();
        while (await reader.ReadAsync(cancellationToken))
            known[reader.GetString(0)] = reader.GetInt64(1);
        return known;
    }

    /// <summary>Records the current count; <paramref name="increase"/> above zero also records a link change at <paramref name="at"/>.</summary>
    public async Task RecordAsync(string bootId, string name, long changes, long increase, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO link_state (boot_id, interface, first_seen, first_changes, changes) VALUES ($boot, $name, $at, $changes, $changes)
                ON CONFLICT (boot_id, interface) DO UPDATE SET changes = excluded.changes;
                """;
            command.Parameters.AddWithValue("$boot", bootId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$changes", changes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (increase > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO link_events (boot_id, interface, time, changes) VALUES ($boot, $name, $at, $increase)
                ON CONFLICT (boot_id, interface, time) DO UPDATE SET changes = changes + excluded.changes;
                """;
            command.Parameters.AddWithValue("$boot", bootId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$increase", increase);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Per interface in this boot: when watching started, the last change, and changes since <paramref name="since"/>.</summary>
    public async Task<IReadOnlyDictionary<string, LinkHistory>> GetHistoryAsync(
        string bootId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.interface, s.first_seen, s.first_changes,
                   (SELECT max(time) FROM link_events e WHERE e.boot_id = s.boot_id AND e.interface = s.interface),
                   (SELECT coalesce(sum(changes), 0) FROM link_events e WHERE e.boot_id = s.boot_id AND e.interface = s.interface AND e.time >= $since)
            FROM link_state s WHERE s.boot_id = $boot;
            """;
        command.Parameters.AddWithValue("$boot", bootId);
        command.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var history = new Dictionary<string, LinkHistory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            history[reader.GetString(0)] = new LinkHistory(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                reader.GetInt64(4));
        }
        return history;
    }

    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync(CancellationToken cancellationToken)
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
