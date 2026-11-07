using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;

namespace OpenRouter.Web.Pages;

public sealed class DhcpModel(KeaService kea, ILogger<DhcpModel> logger) : RouterPageModel(logger)
{
    public KeaConfig? Config { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Config = await TryLoadAsync("the Kea config", () => kea.GetConfigAsync(cancellationToken));
    }

    public static IEnumerable<KeaReservation> ByAddress(IEnumerable<KeaReservation> reservations) =>
        reservations.OrderBy(r => IpAddressOrder.Key(r.IpAddress));
}
