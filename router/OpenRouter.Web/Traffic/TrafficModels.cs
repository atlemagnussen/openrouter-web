namespace OpenRouter.Web.Traffic;

/// <summary>Seen from the client: <see cref="Up"/> is what it sends, <see cref="Down"/> what it receives.</summary>
public enum TrafficDirection
{
    Down,
    Up,
}

/// <summary>A byte counter for one LAN address from the nftables accounting sets.</summary>
public sealed record AddressCounter(string Address, TrafficDirection Direction, long Bytes);

/// <summary>Bytes one client moved during a sample interval.</summary>
/// <param name="Key">The MAC address, or <see cref="TrafficAttribution.UnknownKey"/> for addresses no device could be found for.</param>
public sealed record ClientTraffic(string Key, string? Name, string? Network, long DownBytes, long UpBytes);

/// <summary>Bytes through one router interface during a sample interval (from the interface's point of view).</summary>
public sealed record LinkTraffic(string Interface, long RxBytes, long TxBytes);
