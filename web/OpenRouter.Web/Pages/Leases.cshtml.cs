using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;

namespace OpenRouter.Web.Pages;

public sealed class LeasesModel(KeaService kea, TimeProvider time, ILogger<LeasesModel> logger) : RouterPageModel(logger)
{
    public sealed record LeaseRow(KeaLease Lease, string? ReservedName, string? Network);

    public DateTimeOffset Now { get; private set; }
    public IReadOnlyList<LeaseRow> Active { get; private set; } = [];
    public IReadOnlyList<LeaseRow> Inactive { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Now = time.GetUtcNow();
        var leases = await TryLoadAsync("Kea leases", () => kea.GetLeasesAsync(cancellationToken));
        var config = await TryLoadAsync("the Kea config", () => kea.GetConfigAsync(cancellationToken)) ?? KeaConfig.Empty;
        if (leases is null)
            return;

        var reservations = config.AllReservations
            .Where(r => r.HwAddress is not null)
            .DistinctBy(r => r.HwAddress)
            .ToDictionary(r => r.HwAddress!);

        var rows = leases
            .OrderBy(l => IpAddressOrder.Key(l.IpAddress))
            .Select(l => new LeaseRow(
                l,
                l.HwAddress is not null && reservations.TryGetValue(l.HwAddress, out var r) ? r.Hostname ?? "" : null,
                config.SubnetById(l.SubnetId) is { } s ? s.Name ?? s.Subnet : null))
            .ToList();

        Active = rows.Where(r => r.Lease.IsActive(Now)).ToList();
        Inactive = rows.Where(r => !r.Lease.IsActive(Now)).OrderByDescending(r => r.Lease.Expires).ToList();
    }
}
