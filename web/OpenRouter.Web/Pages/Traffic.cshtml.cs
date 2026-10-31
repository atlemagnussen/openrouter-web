using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Traffic;

namespace OpenRouter.Web.Pages;

public sealed class TrafficModel(
    TrafficStore store,
    TrafficStatus status,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<TrafficModel> logger) : RouterPageModel(logger)
{
    private const int TopClients = 7;

    [BindProperty(SupportsGet = true)]
    public string? Range { get; set; }

    /// <summary>"down" or "up": which direction the per-device chart shows.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Direction { get; set; }

    public TrafficRange SelectedRange => TrafficRange.Find(Range);
    public TrafficDirection SelectedDirection => Direction == "up" ? TrafficDirection.Up : TrafficDirection.Down;
    public IReadOnlyList<ClientTotal> Totals { get; private set; } = [];
    public TrafficStoreInfo? Info { get; private set; }
    public TrafficStatus.Snapshot Status => status.Current;
    public RouterOptions Options => options.Value;
    public bool IsSampleData => options.Value.SampleDir is not null;
    public DateTimeOffset Now { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Now = time.GetUtcNow();
        var (from, to) = SelectedRange.Window(Now);
        Totals = await TryLoadAsync("the traffic database", () => store.GetClientTotalsAsync(from, to, cancellationToken)) ?? [];
        Info = await TryLoadAsync("the traffic database", () => store.GetInfoAsync(cancellationToken));
    }

    /// <summary>Chart data for the page's script: <c>?handler=Series&amp;range=24h&amp;direction=down</c>.</summary>
    public async Task<IActionResult> OnGetSeriesAsync(CancellationToken cancellationToken)
    {
        var (from, to) = SelectedRange.Window(time.GetUtcNow());
        try
        {
            return new JsonResult(await store.GetSeriesAsync(from, to, SelectedRange.BucketSeconds, SelectedDirection, TopClients, cancellationToken));
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the traffic database");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }

    /// <summary>Downloads a consistent copy of the live traffic database, e.g. to analyse on another machine.</summary>
    public async Task<IActionResult> OnPostSnapshotAsync(CancellationToken cancellationToken)
    {
        var name = $"traffic-snapshot-{time.GetUtcNow():yyyyMMdd-HHmmss}.db";
        var path = Path.Combine(options.Value.TrafficArchiveDir, name);
        try
        {
            Directory.CreateDirectory(options.Value.TrafficArchiveDir);
            await store.SnapshotAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not snapshot the traffic database to {Path}", path);
            Errors.Add($"Could not create a snapshot in {options.Value.TrafficArchiveDir}: {ex.Message}");
            await OnGetAsync(cancellationToken);
            return Page();
        }

        // The snapshot is only a vehicle for the download; it goes away once sent.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return File(stream, "application/vnd.sqlite3", name);
    }

    public string LinkTo(string? range = null, string? direction = null) =>
        Url.Page("/Traffic", new { range = range ?? SelectedRange.Key, direction = direction ?? Direction ?? "down" })!;
}
