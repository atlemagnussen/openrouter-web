using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Kernel;

/// <summary>Reads /proc and /sys. Everything used here is world-readable.</summary>
public sealed class KernelReader(IOptions<RouterOptions> options)
{
    public async Task<KernelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var proc = options.Value.ProcRoot;
        async Task<T?> Read<T>(string path, Func<string, T> parse) where T : struct =>
            await ReadOptionalAsync(Path.Combine(proc, path), cancellationToken) is { } text ? parse(text) : null;

        var softnet = await ReadOptionalAsync(Path.Combine(proc, "net/softnet_stat"), cancellationToken);
        var meminfo = await ReadOptionalAsync(Path.Combine(proc, "meminfo"), cancellationToken);
        return new KernelSnapshot(
            ConntrackCount: await Read("sys/net/netfilter/nf_conntrack_count", KernelParser.ParseNumber),
            ConntrackMax: await Read("sys/net/netfilter/nf_conntrack_max", KernelParser.ParseNumber),
            Softnet: softnet is null ? null : KernelParser.ParseSoftnetStat(softnet),
            NeighbourLimitIpv4: (int?)await Read("sys/net/ipv4/neigh/default/gc_thresh3", KernelParser.ParseNumber),
            NeighbourLimitIpv6: (int?)await Read("sys/net/ipv6/neigh/default/gc_thresh3", KernelParser.ParseNumber),
            Load1: await Read("loadavg", KernelParser.ParseLoadavg),
            Memory: meminfo is null ? null : KernelParser.ParseMeminfo(meminfo),
            Uptime: await Read("uptime", KernelParser.ParseUptime),
            CpuCount: Environment.ProcessorCount);
    }

    public async Task<IReadOnlyList<NicInfo>> GetNicsAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var nics = new List<NicInfo>();
        foreach (var name in names)
        {
            // Interface names come from the router's own config and ip output, but never build paths from odd ones.
            if (name.Length == 0 || name.Contains('/') || name.StartsWith('.'))
                continue;
            var dir = Path.Combine(options.Value.SysRoot, "class/net", name);
            if (!Directory.Exists(dir))
                continue;

            // speed and duplex fail with EINVAL while the link is down.
            var speed = await ReadOptionalAsync(Path.Combine(dir, "speed"), cancellationToken, ignoreIoErrors: true);
            var duplex = await ReadOptionalAsync(Path.Combine(dir, "duplex"), cancellationToken, ignoreIoErrors: true);
            var changes = await ReadOptionalAsync(Path.Combine(dir, "carrier_changes"), cancellationToken, ignoreIoErrors: true);
            nics.Add(new NicInfo(
                name,
                SpeedMbps: int.TryParse(speed?.Trim(), out var mbps) && mbps > 0 ? mbps : null,
                Duplex: duplex?.Trim() is { Length: > 0 } d and not "unknown" ? d : null,
                CarrierChanges: long.TryParse(changes?.Trim(), out var count) ? count : null));
        }
        return nics;
    }

    /// <summary>The file's text, or null if it doesn't exist (e.g. conntrack isn't loaded).</summary>
    private static async Task<string?> ReadOptionalAsync(string path, CancellationToken cancellationToken, bool ignoreIoErrors = false)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException ||
            (ignoreIoErrors && ex is IOException))
        {
            return null;
        }
    }
}
