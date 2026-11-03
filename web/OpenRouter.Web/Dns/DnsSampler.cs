using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Health;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Probes;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Traffic;

namespace OpenRouter.Web.Dns;

/// <summary>
/// Once a minute: reads bind's statistics and stores the increase, and looks up the probe name through
/// every address clients use for DNS and straight at each of bind's forwarders, so slow lookups can be
/// pinned on bind or on the upstream.
/// </summary>
public sealed class DnsSampler(
    IOptions<RouterOptions> options,
    DnsStore store,
    BindService bind,
    KeaService kea,
    RadvdService radvd,
    ProbeService probes,
    TrafficEvents events,
    TimeProvider time,
    ILogger<DnsSampler> logger) : BackgroundService
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(150);

    private (DnsMetrics Metrics, DateTimeOffset At)? _previous;
    private string? _loggedError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (options.Value.SampleDir is not null)
            {
                await SampleDnsSeeder.SeedIfEmptyAsync(store, time.GetUtcNow(), stoppingToken);
                return;
            }
            if (!options.Value.DnsSampling)
                return;

            while (!stoppingToken.IsCancellationRequested)
            {
                // A few seconds after each minute, clear of the traffic sampler.
                var now = time.GetUtcNow();
                var next = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 60 + 1) * 60).AddSeconds(3);
                await Task.Delay(next - now, time, stoppingToken);
                await SampleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DNS sampling stopped");
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60 * 60 - 60;
        var errors = new List<string>();

        DnsMetrics? delta = null;
        if (await TryAsync(errors, "bind statistics", () => bind.GetStatsAsync(cancellationToken), cancellationToken) is { } stats)
        {
            var metrics = DnsMetrics.From(stats);
            if (_previous is { } previous && now - previous.At <= MaxGap)
                delta = DnsMetrics.Delta(previous.Metrics, metrics);
            _previous = (metrics, now);
        }

        var config = await TryAsync(errors, "Kea config", () => kea.GetConfigAsync(cancellationToken), cancellationToken);
        var radvdConfig = await TryAsync(errors, "radvd.conf", () => radvd.GetConfigAsync(cancellationToken), cancellationToken);
        var bindOptions = await TryAsync(errors, "named.conf.options", () => bind.GetOptionsAsync(cancellationToken), cancellationToken);
        var results = await Task.WhenAll(DnsProbeTargets.From(config, radvdConfig, bindOptions)
            .Select(async t => ToResult(t, await probes.QueryAsync(t.Address, options.Value.ProbeDnsName, cancellationToken))));

        if (delta is not null || results.Length > 0)
        {
            await store.WriteAsync([(minute, delta, results)], cancellationToken);
            events.PublishDnsMinute(minute);
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        if (error != _loggedError)
        {
            if (error is null)
                logger.LogInformation("DNS sampling recovered");
            else
                logger.LogWarning("DNS sampling: {Error}", error);
            _loggedError = error;
        }
    }

    private static DnsProbeResult ToResult(DnsProbeTargets.Target target, DnsResult result) =>
        new(target.Address, target.Kind, result.Elapsed?.TotalMilliseconds, result.ResponseCode, result.Error);

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
}

/// <summary>Where the DNS probes go: every address clients are told to use, then each of bind's forwarders.</summary>
public static class DnsProbeTargets
{
    public sealed record Target(string Address, string Kind);

    public static IReadOnlyList<Target> From(KeaConfig? kea, RadvdConfig? radvd, BindOptions? bind)
    {
        var targets = HealthReport.DnsServers(kea, radvd).Select(a => new Target(a, "bind")).ToList();
        foreach (var forwarder in bind?.Forwarders ?? [])
        {
            // Entries can carry a port ("1.1.1.1 port 53"); only plain addresses are probed.
            var address = forwarder.Split(' ')[0];
            if (System.Net.IPAddress.TryParse(address, out var ip) && targets.All(t => t.Address != ip.ToString()))
                targets.Add(new Target(ip.ToString(), "forwarder"));
        }
        return targets;
    }
}
