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
    public override string ToString() => $"{Address}/{PrefixLength}";
}
