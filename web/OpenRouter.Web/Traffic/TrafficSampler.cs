using Microsoft.Extensions.Options;
using OpenRouter.Web.Clients;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Health;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;

namespace OpenRouter.Web.Traffic;

/// <summary>
/// Reads the interface byte counters and the nftables per-address counters every few seconds
/// (<see cref="RouterOptions.TrafficLiveSeconds"/>), publishes the rates as live points, and adds the
/// increases up into whole minutes that are attributed to devices and written to the database.
/// Also archives old months daily when <see cref="RouterOptions.TrafficArchiveAfterDays"/> is set.
/// </summary>
public sealed class TrafficSampler(
    IOptions<RouterOptions> options,
    TrafficStore store,
    TrafficStatus status,
    TrafficEvents events,
    NftService nft,
    IpCommand ip,
    KeaService kea,
    RadvdService radvd,
    KernelReader kernel,
    TimeProvider time,
    ILogger<TrafficSampler> logger) : BackgroundService
{
    private Reading<IReadOnlyDictionary<(string, TrafficDirection), long>>? _addresses;
    private Reading<IReadOnlyDictionary<string, (long Rx, long Tx)>>? _links;
    private MinuteBuffer? _minute;
    private Context? _context;
    private int? _lastMinuteClients;
    private string? _linkRoles;
    private DateOnly? _archivedOn;
    private (string Message, DateTimeOffset At)? _loggedError;

    private sealed record Reading<T>(T Values, DateTimeOffset At);

    /// <summary>What's needed to name devices and find the WAN; refreshed once a minute.</summary>
    private sealed record Context(
        KeaConfig Config, IReadOnlyList<KeaLease> Leases, IReadOnlyList<NetworkClient> Directory,
        IReadOnlyList<LinkHealth.Port> Ports, string? Wan);

    private TimeSpan Interval => options.Value.TrafficLiveSeconds > 0
        ? TimeSpan.FromSeconds(options.Value.TrafficLiveSeconds)
        : TimeSpan.FromMinutes(1);

    /// <summary>Readings further apart than this (app was stopped, a source failed) only start a new baseline.</summary>
    private TimeSpan MaxGap => Interval * 2.5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.InitializeAsync(stoppingToken);
            if (options.Value.SampleDir is not null)
            {
                await SampleTrafficSeeder.SeedIfEmptyAsync(store, time.GetUtcNow(), stoppingToken);
                await SampleTrafficSeeder.PublishLiveAsync(events, Interval, time, stoppingToken);
                return;
            }
            if (!options.Value.TrafficSampling)
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                // A second after each interval boundary, so whole minutes line up with the clock.
                var now = time.GetUtcNow();
                var seconds = (long)Interval.TotalSeconds;
                var next = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / seconds + 1) * seconds).AddSeconds(1);
                await Task.Delay(next - now, time, stoppingToken);

                await TickAsync(stoppingToken);
                await ArchiveIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Never take the web app down with the sampler.
            logger.LogError(ex, "Traffic sampling stopped");
            status.Sampled(time.GetUtcNow(), null, null, $"Sampling stopped: {ex.Message}");
        }
    }

    /// <summary>The minute a reading taken at <paramref name="at"/> mostly covers (the interval before it).</summary>
    public static long MinuteOf(DateTimeOffset at, TimeSpan interval) =>
        (at - interval / 2).ToUnixTimeSeconds() / 60 * 60;

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var errors = new List<string>();
        _context ??= await LoadContextAsync(errors, now, cancellationToken);

        IReadOnlyList<LinkTraffic> linkDeltas = [];
        double? seconds = null;
        if (await TryAsync(errors, "interface counters",
                () => kernel.GetInterfaceBytesAsync(_context.Ports.Select(p => p.Name), cancellationToken), cancellationToken) is { } linkBytes)
        {
            if (_links is { } previous && now - previous.At <= MaxGap)
            {
                linkDeltas = TrafficAttribution.LinkDeltas(previous.Values, linkBytes);
                seconds = (now - previous.At).TotalSeconds;
            }
            _links = new(linkBytes, now);
        }

        IReadOnlyList<AddressCounter> addressDeltas = [];
        int? addresses = null;
        if (await TryAsync(errors, "nftables accounting", () => nft.GetCountersAsync(cancellationToken), cancellationToken) is { } counters)
        {
            addresses = counters.Count;
            if (_addresses is { } previous && now - previous.At <= MaxGap)
            {
                addressDeltas = TrafficAttribution.Deltas(previous.Values, counters);
                seconds ??= (now - previous.At).TotalSeconds;
            }
            _addresses = new(counters.ToDictionary(c => (c.Address, c.Direction), c => c.Bytes), now);
        }

        if (options.Value.TrafficLiveSeconds > 0 && seconds is { } elapsed)
        {
            var clients = TrafficAttribution.ByClient(addressDeltas, _context.Directory, _context.Config, _context.Leases, now);
            events.PublishLive(LivePoint.Build(now, elapsed, _context.Wan, linkDeltas, clients));
        }

        // Add the increases up per minute; write the minute once the next reading belongs to the next one.
        var minute = MinuteOf(now, Interval);
        if (_minute is { } stale && stale.Minute != minute)
            await FlushAsync(errors, now, cancellationToken);
        (_minute ??= new MinuteBuffer(minute)).Add(linkDeltas, addressDeltas);
        if (MinuteOf(now + Interval, Interval) != minute)
            await FlushAsync(errors, now, cancellationToken);

        var error = errors.Count > 0 ? string.Join("; ", errors.Distinct()) : null;
        status.Sampled(now, _lastMinuteClients, addresses, error);
        LogError(error, now);
    }

    private async Task FlushAsync(List<string> errors, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_minute is not { } buffer)
            return;
        _minute = null;

        // Fresh names and neighbours for what goes into the database; live points reuse them for the next minute.
        _context = await LoadContextAsync(errors, now, cancellationToken);
        var clients = TrafficAttribution.ByClient(buffer.Addresses, _context.Directory, _context.Config, _context.Leases, now);
        if (buffer.Links.Count > 0 || clients.Count > 0)
        {
            await store.WriteAsync([new MinuteSample(buffer.Minute, clients, buffer.Links)], cancellationToken);
            events.PublishMinute(buffer.Minute);
        }
        _lastMinuteClients = clients.Count;
    }

    private async Task<Context> LoadContextAsync(List<string> errors, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var config = await TryAsync(errors, "Kea config", () => kea.GetConfigAsync(cancellationToken), cancellationToken) ?? KeaConfig.Empty;
        var radvdConfig = await TryAsync(errors, "radvd.conf", () => radvd.GetConfigAsync(cancellationToken), cancellationToken);
        var routes = await TryAsync(errors, "routes", () => ip.GetDefaultRoutesAsync(ipv6: false, cancellationToken), cancellationToken);
        var neighbours = await TryAsync(errors, "neighbour table", () => ip.GetNeighboursAsync(cancellationToken), cancellationToken) ?? [];
        var leases = await TryAsync(errors, "Kea leases", () => kea.GetLeasesAsync(cancellationToken), cancellationToken) ?? [];

        var ports = HealthReport.Ports(routes, config, radvdConfig);
        await UpdateLinkRolesAsync(ports, cancellationToken);
        var lanDevices = config.InterfaceNames.Concat(radvdConfig?.Interfaces.Select(i => i.Name) ?? []).ToHashSet();
        return new Context(config, leases, ClientDirectory.Build(neighbours, config, leases, lanDevices, now), ports,
            ports.FirstOrDefault(p => p.Role == "WAN")?.Name);
    }

    private static async Task<T?> TryAsync<T>(List<string> errors, string source, Func<Task<T>> load, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            errors.Add($"{source}: {ex.Message}");
            return null;
        }
    }

    private async Task UpdateLinkRolesAsync(IReadOnlyList<LinkHealth.Port> ports, CancellationToken cancellationToken)
    {
        var roles = string.Join(",", ports.Select(p => $"{p.Name}={p.Role}"));
        if (roles == _linkRoles)
            return;
        await store.SetLinkRolesAsync(ports.Select(p => (p.Name, p.Role)), cancellationToken);
        _linkRoles = roles;
    }

    private async Task ArchiveIfDueAsync(CancellationToken cancellationToken)
    {
        if (options.Value.TrafficArchiveAfterDays is not { } days)
            return;
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (_archivedOn == today)
            return;

        var files = await store.ArchiveAsync(now.AddDays(-days), options.Value.TrafficArchiveDir, cancellationToken);
        if (files.Count > 0)
            logger.LogInformation("Archived traffic older than {Days} days to {Files}", days, files);
        _archivedOn = today;
    }

    /// <summary>A failing source would otherwise log on every reading: log when the message changes, and hourly while it lasts.</summary>
    private void LogError(string? error, DateTimeOffset now)
    {
        if (error is null)
        {
            if (_loggedError is not null)
                logger.LogInformation("Traffic sampling recovered");
            _loggedError = null;
            return;
        }
        if (_loggedError is { } last && last.Message == error && now - last.At < TimeSpan.FromHours(1))
            return;
        logger.LogWarning("Traffic sampling: {Error}", error);
        _loggedError = (error, now);
    }

    /// <summary>Increases collected during one minute.</summary>
    private sealed class MinuteBuffer(long minute)
    {
        private readonly Dictionary<(string, TrafficDirection), long> _addresses = [];
        private readonly Dictionary<string, (long Rx, long Tx)> _links = [];

        public long Minute => minute;

        public IReadOnlyList<AddressCounter> Addresses =>
            _addresses.Select(a => new AddressCounter(a.Key.Item1, a.Key.Item2, a.Value)).ToList();

        public IReadOnlyList<LinkTraffic> Links =>
            _links.Select(l => new LinkTraffic(l.Key, l.Value.Rx, l.Value.Tx)).OrderBy(l => l.Interface).ToList();

        public void Add(IEnumerable<LinkTraffic> links, IEnumerable<AddressCounter> addresses)
        {
            foreach (var link in links)
            {
                var (rx, tx) = _links.GetValueOrDefault(link.Interface);
                _links[link.Interface] = (rx + link.RxBytes, tx + link.TxBytes);
            }
            foreach (var address in addresses)
                _addresses[(address.Address, address.Direction)] = _addresses.GetValueOrDefault((address.Address, address.Direction)) + address.Bytes;
        }
    }
}
