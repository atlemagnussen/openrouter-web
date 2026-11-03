using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Kernel;

/// <summary>
/// Watches every physical interface's carrier_changes every few seconds and records when the link goes
/// down or up, so the Health page can say when it happened instead of only "since boot".
/// A change while the app wasn't running is recorded when the app next starts.
/// </summary>
public sealed class LinkMonitor(
    IOptions<RouterOptions> options,
    KernelReader kernel,
    LinkStore store,
    TimeProvider time,
    ILogger<LinkMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sample data has no real links to watch.
        if (options.Value.SampleDir is not null)
            return;
        try
        {
            var bootId = await kernel.GetBootIdAsync(stoppingToken);
            var known = new Dictionary<string, long>(await store.GetKnownAsync(bootId, stoppingToken));
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = time.GetUtcNow();
                var current = await kernel.GetCarrierChangesAsync(kernel.GetPhysicalInterfaces(), stoppingToken);
                foreach (var (name, changes) in current)
                {
                    var before = known.GetValueOrDefault(name, -1);
                    if (changes == before)
                        continue;
                    // First sight in this boot is a baseline; after that, an increase is a link change.
                    var increase = before >= 0 && changes > before ? changes - before : 0;
                    await store.RecordAsync(bootId, name, changes, increase, now, stoppingToken);
                    if (increase > 0)
                        logger.LogInformation("Link on {Interface} went down or up ({Changes} changes since boot)", name, changes);
                    known[name] = changes;
                }
                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Link monitoring stopped");
        }
    }
}
