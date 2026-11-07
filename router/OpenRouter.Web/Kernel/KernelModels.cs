namespace OpenRouter.Web.Kernel;

/// <summary>Kernel counters and limits from /proc. Values are null where the kernel doesn't provide them.</summary>
public sealed record KernelSnapshot(
    long? ConntrackCount,
    long? ConntrackMax,
    SoftnetTotals? Softnet,
    int? NeighbourLimitIpv4,
    int? NeighbourLimitIpv6,
    double? Load1,
    MemoryInfo? Memory,
    TimeSpan? Uptime,
    int CpuCount);

/// <summary>Totals over all CPUs from /proc/net/softnet_stat, since boot.</summary>
/// <param name="Dropped">Packets dropped because a CPU's input backlog was full.</param>
/// <param name="TimeSqueezed">Times packet processing ran out of its time budget with work left.</param>
public sealed record SoftnetTotals(long Processed, long Dropped, long TimeSqueezed);

public sealed record MemoryInfo(long TotalKb, long AvailableKb);

/// <summary>Link details from /sys/class/net. Speed and duplex are null when the link is down or virtual.</summary>
public sealed record NicInfo(string Name, int? SpeedMbps, string? Duplex, long? CarrierChanges);
