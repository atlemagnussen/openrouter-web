using System.Net;
using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;

namespace OpenRouter.Web.Clients;

public static class ClientDirectory
{
    /// <summary>
    /// Groups usable neighbour entries on <paramref name="lanDevices"/> by MAC and names them from
    /// the Kea reservation, falling back to the hostname in the device's active lease.
    /// Link-local IPv6 addresses are left out. An empty <paramref name="lanDevices"/> means all devices.
    /// </summary>
    public static IReadOnlyList<NetworkClient> Build(
        IEnumerable<Neighbour> neighbours,
        KeaConfig config,
        IEnumerable<KeaLease> leases,
        IReadOnlySet<string> lanDevices,
        DateTimeOffset now)
    {
        var reservations = config.AllReservations
            .Where(r => r.HwAddress is not null)
            .DistinctBy(r => r.HwAddress)
            .ToDictionary(r => r.HwAddress!);

        var leaseNames = leases
            .Where(l => l.HwAddress is not null && l.Hostname is not null && l.IsActive(now))
            .GroupBy(l => l.HwAddress!)
            .ToDictionary(g => g.Key, g => g.MaxBy(l => l.Expires)!.Hostname!);

        var networks = config.Subnets
            .Where(s => s.Interface is not null)
            .DistinctBy(s => s.Interface)
            .ToDictionary(s => s.Interface!, s => s.Name ?? s.Subnet);

        return neighbours
            .Where(n => n.IsUsable && (lanDevices.Count == 0 || lanDevices.Contains(n.Device)))
            .GroupBy(n => n.Mac!)
            .Select(g =>
            {
                var mac = g.Key;
                var device = g.First().Device;
                reservations.TryGetValue(mac, out var reservation);
                return new NetworkClient(
                    Mac: mac,
                    Name: reservation?.Hostname ?? leaseNames.GetValueOrDefault(mac),
                    HasReservation: reservation is not null,
                    Ipv4: g.Where(n => !n.IsIpv6).Select(n => n.Address).OrderBy(IpAddressOrder.Key).ToList(),
                    Ipv6: g.Where(n => n.IsIpv6 && !IsLinkLocal(n.Address)).Select(n => n.Address).OrderBy(IpAddressOrder.Key).ToList(),
                    Device: device,
                    Network: networks.GetValueOrDefault(device),
                    Status: g.Select(n => Status(n.States)).Min());
            })
            .OrderBy(c => IpAddressOrder.Key(c.Ipv4.FirstOrDefault()))
            .ThenBy(c => c.Mac)
            .ToList();
    }

    private static NeighbourStatus Status(IReadOnlyList<string> states) =>
        states.Any(s => s is "REACHABLE" or "PERMANENT") ? NeighbourStatus.Reachable
        : states.Any(s => s is "DELAY" or "PROBE") ? NeighbourStatus.Checking
        : NeighbourStatus.Stale;

    private static bool IsLinkLocal(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.IsIPv6LinkLocal;
}
