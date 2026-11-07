using System.Net;

namespace OpenRouter.Web.Network;

/// <summary>An entry in the kernel neighbour table (ARP for IPv4, NDP for IPv6).</summary>
public sealed record Neighbour(string Address, string Device, string? Mac, IReadOnlyList<string> States)
{
    public bool IsIpv6 => Address.Contains(':');

    /// <summary>False for entries the kernel couldn't resolve (no MAC) or has given up on.</summary>
    public bool IsUsable => Mac is not null && !States.Any(s => s is "FAILED" or "INCOMPLETE" or "NOARP");
}

public sealed record NetInterface(string Name, string? Mac, string OperState, IReadOnlyList<NetAddress> Addresses);

public sealed record NetAddress(
    string Address,
    int PrefixLength,
    bool IsIpv6,
    string Scope,
    TimeSpan? ValidLifetime,
    TimeSpan? PreferredLifetime)
{
    /// <summary>True when this address's own prefix contains <paramref name="prefix"/>, so the router can route it.</summary>
    public bool Covers(IpPrefix prefix) =>
        PrefixLength <= prefix.Length &&
        IPAddress.TryParse(Address, out var ip) &&
        new IpPrefix(ip, PrefixLength).Contains(prefix.Network);

    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>An interface's counters from <c>ip -s link</c>, cumulative since boot.</summary>
public sealed record LinkStats(string Name, string OperState, int Mtu, LinkCounters Rx, LinkCounters Tx);

/// <summary><see cref="Errors"/> is the kernel's total (CRC, frame, overrun, carrier, ...).</summary>
public sealed record LinkCounters(long Bytes, long Packets, long Errors, long Dropped);

/// <summary>A default route. <see cref="Gateway"/> is null for point-to-point links.</summary>
public sealed record DefaultRoute(string? Gateway, string Device);
