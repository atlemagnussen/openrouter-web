using System.Globalization;

namespace OpenRouter.Web.Kernel;

/// <summary>Parses /proc files.</summary>
public static class KernelParser
{
    /// <summary>One line per CPU of hex columns: processed, dropped, time_squeeze, ...</summary>
    public static SoftnetTotals ParseSoftnetStat(string text)
    {
        long processed = 0, dropped = 0, squeezed = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 3)
                throw new FormatException($"softnet_stat line has {columns.Length} columns: \"{line}\"");
            processed += Hex(columns[0]);
            dropped += Hex(columns[1]);
            squeezed += Hex(columns[2]);
        }
        return new SoftnetTotals(processed, dropped, squeezed);
    }

    /// <summary>Reads MemTotal and MemAvailable (in kB) from /proc/meminfo.</summary>
    public static MemoryInfo ParseMeminfo(string text)
    {
        long? total = null, available = null;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
                continue;
            var value = parts[1].Trim().Split(' ')[0];
            if (parts[0] == "MemTotal")
                total = long.Parse(value, CultureInfo.InvariantCulture);
            else if (parts[0] == "MemAvailable")
                available = long.Parse(value, CultureInfo.InvariantCulture);
        }
        return new MemoryInfo(
            total ?? throw new FormatException("meminfo has no MemTotal."),
            available ?? throw new FormatException("meminfo has no MemAvailable."));
    }

    /// <summary>The 1-minute load average, the first field of /proc/loadavg.</summary>
    public static double ParseLoadavg(string text) =>
        double.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);

    /// <summary>Seconds since boot, the first field of /proc/uptime.</summary>
    public static TimeSpan ParseUptime(string text) =>
        TimeSpan.FromSeconds(double.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture));

    /// <summary>A file holding one integer, such as nf_conntrack_count.</summary>
    public static long ParseNumber(string text) => long.Parse(text.Trim(), CultureInfo.InvariantCulture);

    private static long Hex(string text) => long.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
