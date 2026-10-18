using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Pages;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>The router box itself: kernel tables that drop traffic when full, CPU, memory and uptime.</summary>
public static class SystemHealth
{
    public static IEnumerable<HealthCheck> Check(KernelSnapshot? kernel, IReadOnlyList<Neighbour>? neighbours)
    {
        if (kernel is null)
            yield break;

        if (kernel is { ConntrackCount: { } count, ConntrackMax: { } max and > 0 })
        {
            var share = (double)count / max;
            var summary = $"{count:N0} of {max:N0} tracked connections ({share:P0}).";
            yield return share < 0.75
                ? new("Connection tracking table", Ok, summary)
                : new("Connection tracking table", share >= 0.9 ? Problem : Warning, summary,
                    "Every connection through the router's NAT needs an entry. When the table is full, new connections are dropped " +
                    "(\"nf_conntrack: table full\" in the kernel log). Raise net.netfilter.nf_conntrack_max, or look for a device opening huge numbers of connections.");
        }

        if (kernel.Softnet is { } softnet)
        {
            yield return softnet.Dropped == 0
                ? new("Kernel packet queue", Ok, "No packets dropped since boot because the CPU fell behind.")
                : new("Kernel packet queue", Warning,
                    $"{softnet.Dropped:N0} packets dropped since boot because a CPU's input queue was full.",
                    "The CPU couldn't keep up with incoming traffic at times. Check for CPU-heavy processes, and that network interrupts " +
                    "are spread over the CPUs (irqbalance).");
        }

        if (neighbours is not null)
        {
            var ipv4 = neighbours.Count(n => !n.IsIpv6);
            var ipv6 = neighbours.Count(n => n.IsIpv6);
            var full = new List<string>();
            if (kernel.NeighbourLimitIpv4 is { } limit4 && ipv4 >= limit4 * 0.8)
                full.Add($"IPv4: {ipv4} of at most {limit4} entries.");
            if (kernel.NeighbourLimitIpv6 is { } limit6 && ipv6 >= limit6 * 0.8)
                full.Add($"IPv6: {ipv6} of at most {limit6} entries.");
            var summary = $"{ipv4} IPv4 and {ipv6} IPv6 entries.";
            yield return full.Count == 0
                ? new("Neighbour table", Ok, summary)
                : new("Neighbour table", Warning, summary,
                    "When the table is full, the kernel logs \"neighbour table overflow\" and can't reach new devices. " +
                    "IPv6 privacy addresses fill it faster. Raise net.ipv4/ipv6.neigh.default.gc_thresh3 (and gc_thresh2).",
                    full);
        }

        if (kernel.Load1 is { } load)
        {
            var perCpu = load / kernel.CpuCount;
            var summary = $"Load {load:0.00} on {kernel.CpuCount} CPU{(kernel.CpuCount == 1 ? "" : "s")}.";
            yield return perCpu <= 1
                ? new("CPU load", Ok, summary)
                : new("CPU load", perCpu > 2 ? Problem : Warning, summary,
                    "More work than the CPUs can do right now; routing and DNS slow down. Look for the busy process with top.");
        }

        if (kernel.Memory is { TotalKb: > 0 } memory)
        {
            var share = (double)memory.AvailableKb / memory.TotalKb;
            var summary = $"{Format.Bytes(memory.AvailableKb * 1024)} of {Format.Bytes(memory.TotalKb * 1024)} available ({share:P0}).";
            yield return share >= 0.1
                ? new("Memory", Ok, summary)
                : new("Memory", share < 0.05 ? Problem : Warning, summary,
                    "Low memory makes the kernel drop caches and eventually kill processes (possibly Kea or named).");
        }

        if (kernel.Uptime is { } uptime)
        {
            yield return uptime < TimeSpan.FromHours(1)
                ? new("Uptime", Info, $"Rebooted {Format.Duration(uptime)} ago.", "Counters on this page start from zero at boot.")
                : new("Uptime", Ok, $"Up {Format.Duration(uptime)}. Counters on this page are since then.");
        }
    }
}
