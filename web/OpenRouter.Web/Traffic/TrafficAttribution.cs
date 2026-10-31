using OpenRouter.Web.Clients;
using OpenRouter.Web.Kea;

namespace OpenRouter.Web.Traffic;

/// <summary>Turns cumulative per-address counters into per-client traffic.</summary>
public static class TrafficAttribution
{
    /// <summary>Client key for traffic whose address no device could be found for (e.g. an IPv6 privacy address that has gone).</summary>
    public const string UnknownKey = "unknown";

    /// <summary>
    /// Bytes since the previous reading. A counter that went down was reset (ruleset reload, element expired),
    /// so all of its current value is new. Addresses seen for the first time count from zero.
    /// </summary>
    public static IReadOnlyList<AddressCounter> Deltas(
        IReadOnlyDictionary<(string Address, TrafficDirection Direction), long> previous,
        IReadOnlyList<AddressCounter> current) =>
        current
            .Select(c => previous.TryGetValue((c.Address, c.Direction), out var before)
                ? c with { Bytes = c.Bytes >= before ? c.Bytes - before : c.Bytes }
                : c)
            .Where(c => c.Bytes > 0)
            .ToList();

    /// <summary>Bytes per interface since the previous reading; a counter that went down (reboot, driver reload) counts from zero.</summary>
    public static IReadOnlyList<LinkTraffic> LinkDeltas(
        IReadOnlyDictionary<string, (long Rx, long Tx)> previous,
        IReadOnlyDictionary<string, (long Rx, long Tx)> current) =>
        current
            .Where(c => previous.ContainsKey(c.Key))
            .Select(c =>
            {
                var before = previous[c.Key];
                return new LinkTraffic(c.Key,
                    c.Value.Rx >= before.Rx ? c.Value.Rx - before.Rx : c.Value.Rx,
                    c.Value.Tx >= before.Tx ? c.Value.Tx - before.Tx : c.Value.Tx);
            })
            .OrderBy(l => l.Interface)
            .ToList();

    /// <summary>
    /// Sums deltas by device. An address is matched to a device through the neighbour table (via <paramref name="clients"/>),
    /// then through Kea's active leases; whatever is left is <see cref="UnknownKey"/>.
    /// </summary>
    public static IReadOnlyList<ClientTraffic> ByClient(
        IEnumerable<AddressCounter> deltas,
        IReadOnlyList<NetworkClient> clients,
        KeaConfig config,
        IReadOnlyList<KeaLease> leases,
        DateTimeOffset now)
    {
        var byAddress = new Dictionary<string, (string Key, string? Name, string? Network)>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in clients)
        {
            foreach (var address in client.Ipv4.Concat(client.Ipv6))
                byAddress[address] = (client.Mac, client.Name, client.Network);
        }

        var reservationNames = config.AllReservations
            .Where(r => r.HwAddress is not null && r.Hostname is not null)
            .DistinctBy(r => r.HwAddress)
            .ToDictionary(r => r.HwAddress!, r => r.Hostname);
        foreach (var lease in leases.Where(l => l.IsActive(now) && l.HwAddress is not null))
        {
            var subnet = config.SubnetById(lease.SubnetId);
            byAddress.TryAdd(lease.IpAddress, (
                lease.HwAddress!,
                reservationNames.GetValueOrDefault(lease.HwAddress!) ?? lease.Hostname,
                subnet?.Name ?? subnet?.Subnet));
        }

        return deltas
            .GroupBy(d => byAddress.TryGetValue(d.Address, out var client) ? client : (UnknownKey, (string?)null, (string?)null))
            .Select(g => new ClientTraffic(
                g.Key.Item1,
                g.Key.Item2,
                g.Key.Item3,
                DownBytes: g.Where(d => d.Direction == TrafficDirection.Down).Sum(d => d.Bytes),
                UpBytes: g.Where(d => d.Direction == TrafficDirection.Up).Sum(d => d.Bytes)))
            // One device can show up under several names if its addresses resolved differently; merge by key.
            .GroupBy(c => c.Key)
            .Select(g => new ClientTraffic(
                g.Key,
                g.Select(c => c.Name).FirstOrDefault(n => n is not null),
                g.Select(c => c.Network).FirstOrDefault(n => n is not null),
                g.Sum(c => c.DownBytes),
                g.Sum(c => c.UpBytes)))
            .ToList();
    }
}
