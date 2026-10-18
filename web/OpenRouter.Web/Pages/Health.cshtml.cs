using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Firewall;
using OpenRouter.Web.Health;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Probes;
using OpenRouter.Web.Radvd;
using OpenRouter.Web.Systemd;

namespace OpenRouter.Web.Pages;

public sealed class HealthModel(
    KeaService kea,
    RadvdService radvd,
    IpCommand ip,
    KernelReader kernel,
    SystemdService systemd,
    FirewallService firewall,
    ProbeService probes,
    IOptions<RouterOptions> options,
    TimeProvider time,
    ILogger<HealthModel> logger) : RouterPageModel(logger)
{
    public IReadOnlyList<HealthArea> Areas { get; private set; } = [];
    public DateTimeOffset CheckedAt { get; private set; }

    public int Count(HealthStatus status) => Areas.Sum(a => a.Checks.Count(c => c.Status == status));

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Independent sources, read in parallel.
        var keaConfig = TryLoadAsync("the Kea config", () => kea.GetConfigAsync(cancellationToken));
        var leases = TryLoadAsync("Kea leases", () => kea.GetLeasesAsync(cancellationToken));
        var statistics = TryLoadAsync("Kea statistics", () => kea.GetStatisticsAsync(cancellationToken));
        var radvdConfig = TryLoadAsync("radvd.conf", () => radvd.GetConfigAsync(cancellationToken));
        var neighbours = TryLoadAsync("the neighbour table", () => ip.GetNeighboursAsync(cancellationToken));
        var interfaces = TryLoadAsync("interface addresses", () => ip.GetInterfacesAsync(cancellationToken));
        var links = TryLoadAsync("interface counters", () => ip.GetLinkStatsAsync(cancellationToken));
        var routes4 = TryLoadAsync("IPv4 routes", () => ip.GetDefaultRoutesAsync(ipv6: false, cancellationToken));
        var routes6 = TryLoadAsync("IPv6 routes", () => ip.GetDefaultRoutesAsync(ipv6: true, cancellationToken));
        var kernelSnapshot = TryLoadAsync("kernel counters", () => kernel.GetSnapshotAsync(cancellationToken));
        var units = TryLoadAsync("service status", () => systemd.GetUnitsAsync(options.Value.Services, cancellationToken));
        var ruleset = TryLoadAsync("nftables.conf", () => firewall.GetConfigTextAsync(cancellationToken));
        await Task.WhenAll(keaConfig, leases, statistics, radvdConfig, neighbours, interfaces, links, routes4, routes6,
            kernelSnapshot, units, ruleset);

        // These need to know which interfaces, gateway and DNS servers the router uses.
        var ports = HealthReport.Ports(routes4.Result, keaConfig.Result, radvdConfig.Result);
        var nics = TryLoadAsync("link details", () => kernel.GetNicsAsync(ports.Select(p => p.Name), cancellationToken));
        var probeResults = options.Value.SampleDir is not null
            ? Task.FromResult<ProbeResults?>(null)
            : RunProbesAsync(routes4.Result, routes6.Result, keaConfig.Result, radvdConfig.Result, cancellationToken);
        await Task.WhenAll(nics, probeResults);

        CheckedAt = time.GetUtcNow();
        Areas = HealthReport.Build(new HealthInput(
            keaConfig.Result, leases.Result, statistics.Result, radvdConfig.Result, neighbours.Result, interfaces.Result,
            links.Result, nics.Result, routes4.Result, routes6.Result, kernelSnapshot.Result, units.Result, ruleset.Result,
            probeResults.Result, CheckedAt));
    }

    private async Task<ProbeResults?> RunProbesAsync(
        IReadOnlyList<DefaultRoute>? routes4, IReadOnlyList<DefaultRoute>? routes6, KeaConfig? keaConfig, RadvdConfig? radvdConfig,
        CancellationToken cancellationToken)
    {
        var hasIpv4 = routes4 is { Count: > 0 };
        var hasIpv6 = routes6 is { Count: > 0 };
        return await probes.RunAsync(
            gateway: routes4?.FirstOrDefault()?.Gateway,
            internet4: hasIpv4 ? options.Value.ProbeIpv4Host : null,
            internet6: hasIpv6 ? options.Value.ProbeIpv6Host : null,
            dnsServers: HealthReport.DnsServers(keaConfig, radvdConfig),
            dnsName: options.Value.ProbeDnsName,
            cancellationToken);
    }
}
