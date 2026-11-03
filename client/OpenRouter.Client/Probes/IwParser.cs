using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenRouter.Client.Probes;

/// <summary>Parses <c>iw dev IF link</c> and <c>iw dev IF station dump</c>. Fields the driver doesn't report stay null.</summary>
public static partial class IwParser
{
    public sealed record Link(bool Connected, string? Bssid, string? Ssid, double? FrequencyMhz, int? SignalDbm, double? TxBitrateMbps, double? RxBitrateMbps);

    public sealed record Station(int? SignalAverageDbm, long? TxPackets, long? TxFailed, long? TxRetries, long? BeaconLoss);

    public static Link ParseLink(string text)
    {
        var connected = ConnectedPattern().Match(text);
        if (!connected.Success)
            return new Link(false, null, null, null, null, null, null);
        return new Link(
            true,
            connected.Groups[1].Value.ToLowerInvariant(),
            Field(text, "SSID"),
            Number(Field(text, "freq")),
            Int(Field(text, "signal")),
            Number(Field(text, "tx bitrate")),
            Number(Field(text, "rx bitrate")));
    }

    public static Station ParseStation(string text) => new(
        Int(Field(text, "signal avg")),
        Long(Field(text, "tx packets")),
        Long(Field(text, "tx failed")),
        Long(Field(text, "tx retries")),
        Long(Field(text, "beacon loss")));

    /// <summary>The value after "name:" on its line, e.g. "signal avg:	-57 [-61, -54] dBm" → "-57 [-61, -54] dBm".</summary>
    private static string? Field(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(name + ":", StringComparison.Ordinal))
                return trimmed[(name.Length + 1)..].Trim();
        }
        return null;
    }

    private static double? Number(string? value) =>
        value is not null && LeadingNumber().Match(value) is { Success: true } m
            ? double.Parse(m.Value, CultureInfo.InvariantCulture)
            : null;

    private static int? Int(string? value) => Number(value) is { } n ? (int)Math.Round(n) : null;

    private static long? Long(string? value) => Number(value) is { } n ? (long)n : null;

    [GeneratedRegex(@"^Connected to ([0-9a-fA-F:]{17})", RegexOptions.Multiline)]
    private static partial Regex ConnectedPattern();

    [GeneratedRegex(@"^-?\d+(\.\d+)?")]
    private static partial Regex LeadingNumber();
}
