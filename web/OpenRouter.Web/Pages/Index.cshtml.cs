using OpenRouter.Web.Clients;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;

namespace OpenRouter.Web.Pages;

public sealed class IndexModel(
    KeaService kea,
    RadvdService radvd,
    IpCommand ip,
    TimeProvider time,
    ILogger<IndexModel> logger) : RouterPageModel(logger)
{
    public int? Clients { get; private set; }
    public int? ActiveLeases { get; private set; }
    public int? Reservations { get; private set; }
    public IReadOnlyList<KeaSubnet> Subnets { get; private set; } = [];
    public IReadOnlyList<string> Ipv6Prefixes { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var config = await TryLoadAsync("the Kea config", () => kea.GetConfigAsync(cancellationToken));
        var leases = await TryLoadAsync("Kea leases", () => kea.GetLeasesAsync(cancellationToken));
        var radvdConfig = await TryLoadAsync("radvd.conf", () => radvd.GetConfigAsync(cancellationToken));
        var neighbours = await TryLoadAsync("the neighbour table", () => ip.GetNeighboursAsync(cancellationToken));

        if (config is not null)
        {
            Subnets = config.Subnets;
            Reservations = config.AllReservations.Count();
        }
        ActiveLeases = leases?.Count(l => l.IsActive(now));
        Ipv6Prefixes = radvdConfig?.Interfaces.SelectMany(i => i.Prefixes).Select(p => p.Arguments.FirstOrDefault() ?? "").ToList() ?? [];

        if (neighbours is not null)
        {
            var lanDevices = (config?.InterfaceNames ?? [])
                .Concat(radvdConfig?.Interfaces.Select(i => i.Name) ?? [])
                .ToHashSet();
            Clients = ClientDirectory.Build(neighbours, config ?? KeaConfig.Empty, leases ?? [], lanDevices, now).Count;
        }
    }
}
