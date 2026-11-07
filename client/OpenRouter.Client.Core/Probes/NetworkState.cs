using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenRouter.Client.Probes;

/// <summary>The laptop's current connection: which interface carries the default route, its addresses and gateways.</summary>
public sealed record NetworkState(
    string Interface,
    string? Mac,
    IReadOnlyList<string> Ipv4,
    IReadOnlyList<string> Ipv6Global,
    IPAddress? Gateway4,
    IPAddress? Gateway6)
{
    public bool HasGlobalIpv6 => Ipv6Global.Count > 0;

    /// <summary>
    /// The interface to watch: <paramref name="preferred"/> if given, otherwise the first up interface with an
    /// IPv4 default gateway. Null when there's no network at all.
    /// </summary>
    public static NetworkState? Read(string? preferred)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        var nic = preferred is not null
            ? candidates.FirstOrDefault(n => n.Name == preferred)
            : candidates.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => IsUsable(g.Address, AddressFamily.InterNetwork)));
        if (nic is null)
            return null;

        var properties = nic.GetIPProperties();
        var addresses = properties.UnicastAddresses.Select(a => a.Address).ToList();
        var mac = nic.GetPhysicalAddress().GetAddressBytes();
        return new NetworkState(
            nic.Name,
            mac.Length == 6 ? string.Join(':', mac.Select(b => b.ToString("x2"))) : null,
            addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
            addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal)
                .Select(a => a.ToString()).ToList(),
            properties.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => IsUsable(a, AddressFamily.InterNetwork)),
            properties.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => IsUsable(a, AddressFamily.InterNetworkV6)));
    }

    private static bool IsUsable(IPAddress address, AddressFamily family) =>
        address.AddressFamily == family && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
}
