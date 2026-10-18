using OpenRouter.Web.Kea;
using OpenRouter.Web.Network;
using OpenRouter.Web.Pages;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>DHCPv4: pool usage, address conflicts, devices without leases and Kea's own counters.</summary>
public static class DhcpHealth
{
    private const double PoolWarning = 0.8;
    private const double PoolProblem = 0.95;

    public static IEnumerable<HealthCheck> Check(
        KeaConfig? config,
        IReadOnlyList<KeaLease>? leases,
        KeaStatistics? statistics,
        IReadOnlyList<Neighbour>? neighbours,
        DateTimeOffset now)
    {
        if (config is not null && leases is not null)
        {
            foreach (var subnet in config.Subnets)
            {
                if (PoolUsage(subnet, leases, now) is { } check)
                    yield return check;
            }

            yield return Conflicts(config, leases, neighbours, now);
            if (neighbours is not null)
                yield return Unleased(config, leases, neighbours, now);
        }

        if (statistics is not null)
        {
            foreach (var check in Counters(statistics, now))
                yield return check;
        }
    }

    private static HealthCheck? PoolUsage(KeaSubnet subnet, IReadOnlyList<KeaLease> leases, DateTimeOffset now)
    {
        var pools = subnet.Pools
            .Select(p => KeaPoolRange.TryParse(p, out var range) ? range : null)
            .OfType<KeaPoolRange>()
            .ToList();
        var size = pools.Sum(p => p.Size);
        if (size == 0)
            return null;

        // Declined addresses can't be handed out either until their probation ends.
        var used = leases.Count(l =>
            l.SubnetId == subnet.Id &&
            (l.IsActive(now) || (l.State == 1 && l.Expires > now)) &&
            pools.Any(p => p.Contains(l.IpAddress)));
        var share = (double)used / size;
        var title = $"Address pool: {subnet.Name ?? subnet.Subnet}";
        var summary = $"{used} of {size} addresses in use ({share:P0}).";
        if (share < PoolWarning)
            return new(title, Ok, summary);
        return new(title, share >= PoolProblem ? Problem : Warning, summary,
            "When the pool runs out, new devices get no address and can't connect. Phones with private (random) Wi-Fi addresses " +
            "can take a new lease each time their address changes. Widen the pool or shorten the lease time.");
    }

    /// <summary>One address, two devices: either a client declined an address as taken, or the router sees it at another MAC than Kea leased it to.</summary>
    private static HealthCheck Conflicts(
        KeaConfig config, IReadOnlyList<KeaLease> leases, IReadOnlyList<Neighbour>? neighbours, DateTimeOffset now)
    {
        const string title = "Address conflicts";
        var details = new List<string>();
        var status = Ok;

        foreach (var declined in leases.Where(l => l.State == 1 && l.Expires > now).OrderBy(l => IpAddressOrder.Key(l.IpAddress)))
        {
            details.Add($"{declined.IpAddress} was declined by a client because another device already uses it " +
                $"(blocked for another {Format.Duration(declined.Expires - now)}).");
            status = Warning;
        }

        if (neighbours is not null)
        {
            var leaseMacs = leases.Where(l => l.IsActive(now) && l.HwAddress is not null)
                .DistinctBy(l => l.IpAddress)
                .ToDictionary(l => l.IpAddress, l => l.HwAddress!);
            var reservedMacs = config.AllReservations.Where(r => r.IpAddress is not null && r.HwAddress is not null)
                .DistinctBy(r => r.IpAddress)
                .ToDictionary(r => r.IpAddress!, r => r.HwAddress!);
            var lanDevices = config.InterfaceNames.ToHashSet();

            foreach (var neighbour in FreshIpv4(neighbours, lanDevices))
            {
                if (leaseMacs.TryGetValue(neighbour.Address, out var leased) && leased != neighbour.Mac)
                    details.Add($"{neighbour.Address} is leased to {leased} but answered by {neighbour.Mac}.");
                else if (!leaseMacs.ContainsKey(neighbour.Address) &&
                    reservedMacs.TryGetValue(neighbour.Address, out var reserved) && reserved != neighbour.Mac)
                    details.Add($"{neighbour.Address} is reserved for {reserved} but used by {neighbour.Mac}.");
                else
                    continue;
                status = Problem;
            }
        }

        return status == Ok
            ? new(title, Ok, "No device uses an address that belongs to another.")
            : new(title, status, $"{details.Count} address{(details.Count == 1 ? "" : "es")} in conflict.",
                "Two devices with the same IP address take turns losing their connection. Look for a device with a manually set IP " +
                "inside the DHCP range, and give it a reservation instead.",
                details);
    }

    /// <summary>Devices active in a DHCP pool without a lease: a static IP inside the pool, or a device whose renewal failed.</summary>
    private static HealthCheck Unleased(
        KeaConfig config, IReadOnlyList<KeaLease> leases, IReadOnlyList<Neighbour> neighbours, DateTimeOffset now)
    {
        const string title = "Devices without a lease";
        var leased = leases.Where(l => l.IsActive(now)).Select(l => l.IpAddress).ToHashSet();
        var reserved = config.AllReservations.Select(r => r.IpAddress).OfType<string>().ToHashSet();
        var pools = config.Subnets.SelectMany(s => s.Pools)
            .Select(p => KeaPoolRange.TryParse(p, out var range) ? range : null)
            .OfType<KeaPoolRange>()
            .ToList();

        var details = FreshIpv4(neighbours, config.InterfaceNames.ToHashSet())
            .Where(n => pools.Any(p => p.Contains(n.Address)) && !leased.Contains(n.Address) && !reserved.Contains(n.Address))
            .Select(n => $"{n.Address} ({n.Mac}) on {n.Device}")
            .ToList();

        return details.Count == 0
            ? new(title, Ok, "Every active device in the DHCP ranges has a lease.")
            : new(title, Warning, $"{details.Count} device{(details.Count == 1 ? "" : "s")} use{(details.Count == 1 ? "s" : "")} an address in the DHCP range without a current lease.",
                "Either the device has a manually set IP inside the pool, which Kea may hand to someone else, or its lease ran out " +
                "because renewals didn't reach Kea. Give static devices a reservation or an address outside the pool.",
                details);
    }

    private static IEnumerable<HealthCheck> Counters(KeaStatistics statistics, DateTimeOffset now)
    {
        string When(string name) =>
            statistics.LastChanged(name) is { } time ? $", last {Format.Relative(time, now)}" : "";

        var failures = statistics.Latest("v4-allocation-fail") ?? 0;
        if (failures == 0)
        {
            yield return new("Address allocation", Ok, "Kea hasn't failed to hand out an address since it started.");
        }
        else
        {
            var recent = statistics.LastChanged("v4-allocation-fail") is { } last && now - last < TimeSpan.FromDays(1);
            yield return new("Address allocation", recent ? Problem : Warning,
                $"Kea couldn't hand out an address {failures:N0} time{(failures == 1 ? "" : "s")} since it started{When("v4-allocation-fail")}.",
                "The device that asked got no address. Usually a full pool, or a request on a network that has no matching subnet.");
        }

        var naks = statistics.Latest("pkt4-nak-sent") ?? 0;
        yield return naks == 0
            ? new("Refused requests (NAK)", Ok, "Kea hasn't refused any address requests since it started.")
            : new("Refused requests (NAK)", Info,
                $"Kea refused {naks:N0} request{(naks == 1 ? "" : "s")} for an address since it started{When("pkt4-nak-sent")}.",
                "Normal when a device comes back asking for an address from another network, e.g. after moving between the main and guest Wi-Fi. " +
                "The device then starts over and gets a new address, which takes a few seconds. Many of these means devices keep hopping networks.");

        var dropped = (statistics.Latest("pkt4-receive-drop") ?? 0) + (statistics.Latest("pkt4-parse-failed") ?? 0);
        if (dropped > 0)
            yield return new("Ignored DHCP packets", Info,
                $"Kea ignored {dropped:N0} DHCP packet{(dropped == 1 ? "" : "s")} since it started{When("pkt4-receive-drop")}.",
                "Kea drops malformed packets and requests meant for another DHCP server. Occasional drops are normal; " +
                "a steady stream means a misbehaving device or a second DHCP server on the network.");
    }

    // STALE entries can be hours old, so only trust entries the kernel has confirmed recently.
    private static IEnumerable<Neighbour> FreshIpv4(IEnumerable<Neighbour> neighbours, IReadOnlySet<string> lanDevices) =>
        neighbours.Where(n => !n.IsIpv6 && n.IsUsable && lanDevices.Contains(n.Device) &&
            n.States.Any(s => s is "REACHABLE" or "DELAY" or "PROBE"));
}
