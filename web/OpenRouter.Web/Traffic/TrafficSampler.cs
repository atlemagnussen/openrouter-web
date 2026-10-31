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
/// Once a minute: reads the interface byte counters and the nftables per-address counters, turns the
/// increase since the last minute into per-client traffic and stores it. Also archives old months daily
/// when <see cref="RouterOptions.TrafficArchiveAfterDays"/> is set.
/// </summary>
public sealed class TrafficSampler(
    IOptions<RouterOptions> options,
    TrafficStore store,
    TrafficStatus status,
    NftService nft,
    IpCommand ip,
    KeaService kea,
    RadvdService radvd,
    KernelReader kernel,
    TimeProvider time,
    ILogger<TrafficSampler> logger) : BackgroundService
{
    /// <summary>Readings further apart than this (app was stopped, a source failed) only start a new baseline.</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(150);

    private Reading<IReadOnlyDictionary<(string, TrafficDirection), long>>? _addresses;
    private Reading<IReadOnlyDictionary<string, (long Rx, long Tx)>>? _links;
    private string? _linkRoles;
    private DateOnly? _archivedOn;
    private (string Message, DateTimeOffset At)? _loggedError;

    private sealed record Reading<T>(T Values, DateTimeOffset At);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.InitializeAsync(stoppingToken);
            if (options.Value.SampleDir is not null)
            {
                await SampleTrafficSeeder.SeedIfEmptyAsync(store, time.GetUtcNow(), stoppingToken);
                return;
            }
            if (!options.Value.TrafficSampling)
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Just after each whole minute, so a reading covers the minute that just ended.
                var now = time.GetUtcNow();
                var next = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 60 + 1) * 60).AddSeconds(1);
                await Task.Delay(next - now, time, stoppingToken);

                await SampleAsync(stoppingToken);
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

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60 * 60 - 60;
        var errors = new List<string>();

        async Task<T?> Try<T>(string source, Func<Task<T>> load) where T : class
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

        var config = await Try("Kea config", () => kea.GetConfigAsync(cancellationToken)) ?? KeaConfig.Empty;
        var radvdConfig = await Try("radvd.conf", () => radvd.GetConfigAsync(cancellationToken));
        var routes = await Try("routes", () => ip.GetDefaultRoutesAsync(ipv6: false, cancellationToken));
        var ports = HealthReport.Ports(routes, config, radvdConfig);
        await UpdateLinkRolesAsync(ports, cancellationToken);

        var links = new List<LinkTraffic>();
        if (await Try("interface counters", () => kernel.GetInterfaceBytesAsync(ports.Select(p => p.Name), cancellationToken)) is { } linkBytes)
        {
            if (_links is { } previous && now - previous.At <= MaxGap)
                links.AddRange(TrafficAttribution.LinkDeltas(previous.Values, linkBytes));
            _links = new(linkBytes, now);
        }

        IReadOnlyList<ClientTraffic> clients = [];
        int? addresses = null;
        if (await Try("nftables accounting", () => nft.GetCountersAsync(cancellationToken)) is { } counters)
        {
            addresses = counters.Count;
            if (_addresses is { } previous && now - previous.At <= MaxGap)
            {
                var deltas = TrafficAttribution.Deltas(previous.Values, counters);
                if (deltas.Count > 0)
                {
                    var neighbours = await Try("neighbour table", () => ip.GetNeighboursAsync(cancellationToken)) ?? [];
                    var leases = await Try("Kea leases", () => kea.GetLeasesAsync(cancellationToken)) ?? [];
                    var lanDevices = config.InterfaceNames.Concat(radvdConfig?.Interfaces.Select(i => i.Name) ?? []).ToHashSet();
                    var directory = ClientDirectory.Build(neighbours, config, leases, lanDevices, now);
                    clients = TrafficAttribution.ByClient(deltas, directory, config, leases, now);
                }
            }
            _addresses = new(counters.ToDictionary(c => (c.Address, c.Direction), c => c.Bytes), now);
        }

        if (links.Count > 0 || clients.Count > 0)
            await store.WriteAsync([new MinuteSample(minute, clients, links)], cancellationToken);

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        status.Sampled(now, clients.Count, addresses, error);
        LogError(error, now);
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

    /// <summary>A failing source would otherwise log every minute: log when the message changes, and hourly while it lasts.</summary>
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
}
