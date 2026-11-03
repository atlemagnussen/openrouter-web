using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Dns;
using OpenRouter.Web.Health;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Traffic;

namespace OpenRouter.Web.Pages;

public sealed class DnsModel(
    BindService bind,
    DnsStore store,
    KeaService kea,
    RadvdService radvd,
    IpCommand ip,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<DnsModel> logger) : RouterPageModel(logger)
{
    [BindProperty(SupportsGet = true)]
    public string? Range { get; set; }

    public TrafficRange SelectedRange => TrafficRange.Find(Range);
    public BindOptions? Bind { get; private set; }
    public BindStats? Stats { get; private set; }
    public DnsTotals? Totals { get; private set; }
    public IReadOnlyList<DnsProbeSummary> Probes { get; private set; } = [];
    public IReadOnlyList<HealthCheck> Checks { get; private set; } = [];
    public bool IsSampleData => options.Value.SampleDir is not null;
    public string ProbeName => options.Value.ProbeDnsName;
    public DateTimeOffset Now { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadSummaryAsync(cancellationToken);
        Bind = await TryLoadAsync("named.conf.options", () => bind.GetOptionsAsync(cancellationToken));
        try
        {
            Stats = await bind.GetStatsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException)
        {
            // Reported by the "Statistics channel" check.
            logger.LogDebug(ex, "bind statistics unavailable");
        }

        var nowSeconds = Now.ToUnixTimeSeconds();
        var lastHour = await TryLoadAsync("the DNS history", () => store.GetTotalsAsync(nowSeconds - 3600, nowSeconds, cancellationToken));
        var config = await TryLoadAsync("the Kea config", () => kea.GetConfigAsync(cancellationToken));
        var radvdConfig = await TryLoadAsync("radvd.conf", () => radvd.GetConfigAsync(cancellationToken));
        var interfaces = await TryLoadAsync("interface addresses", () => ip.GetInterfacesAsync(cancellationToken));
        Checks = DnsHealth.Check(Bind, Stats, lastHour, config, radvdConfig, interfaces, Now).ToList();
    }

    /// <summary>The tiles and probe table on their own, swapped in by the page's script when a new minute is stored.</summary>
    public async Task<IActionResult> OnGetSummaryAsync(CancellationToken cancellationToken)
    {
        await LoadSummaryAsync(cancellationToken);
        return Partial("_DnsSummary", this);
    }

    public async Task<IActionResult> OnGetSeriesAsync(CancellationToken cancellationToken)
    {
        var (from, to) = SelectedRange.Window(time.GetUtcNow());
        try
        {
            return new JsonResult(await store.GetSeriesAsync(from, to, SelectedRange.BucketSeconds, cancellationToken));
        }
        catch (Exception ex) when (ex is DbException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the DNS history");
            return new ObjectResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
    }

    private async Task LoadSummaryAsync(CancellationToken cancellationToken)
    {
        Now = time.GetUtcNow();
        var (from, to) = SelectedRange.Window(Now);
        Totals = await TryLoadAsync("the DNS history", () => store.GetTotalsAsync(from, to, cancellationToken));
        Probes = await TryLoadAsync("the DNS history", () => store.GetProbeSummaryAsync(from, to, cancellationToken)) ?? [];
    }
}
