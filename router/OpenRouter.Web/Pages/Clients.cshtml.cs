using OpenRouter.Web.Clients;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Radvd;

namespace OpenRouter.Web.Pages;

public sealed class ClientsModel(
    KeaService kea,
    RadvdService radvd,
    IpCommand ip,
    TimeProvider time,
    ILogger<ClientsModel> logger) : RouterPageModel(logger)
{
    public IReadOnlyList<NetworkClient> Clients { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var neighbours = await TryLoadAsync("the neighbour table", () => ip.GetNeighboursAsync(cancellationToken));
        var config = await TryLoadAsync("the Kea config", () => kea.GetConfigAsync(cancellationToken));
        var leases = await TryLoadAsync("Kea leases", () => kea.GetLeasesAsync(cancellationToken));
        var radvdConfig = await TryLoadAsync("radvd.conf", () => radvd.GetConfigAsync(cancellationToken));
        if (neighbours is null)
            return;

        // LAN = whatever Kea or radvd serve; this keeps the ISP's gateway on the WAN out of the list.
        var lanDevices = (config?.InterfaceNames ?? [])
            .Concat(radvdConfig?.Interfaces.Select(i => i.Name) ?? [])
            .ToHashSet();

        Clients = ClientDirectory.Build(neighbours, config ?? KeaConfig.Empty, leases ?? [], lanDevices, time.GetUtcNow());
    }
}
