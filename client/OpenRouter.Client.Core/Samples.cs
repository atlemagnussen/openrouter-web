namespace OpenRouter.Client;

/// <summary>One ping. <see cref="Milliseconds"/> is null when no reply came back in time.</summary>
public sealed record PingSample(DateTimeOffset Time, string Target, string Label, double? Milliseconds);

/// <summary>One DNS lookup. <see cref="Milliseconds"/> is null when no answer came back.</summary>
public sealed record DnsSample(DateTimeOffset Time, string Server, string Name, double? Milliseconds, int? ResponseCode, string? Error);

/// <summary>
/// One fresh HTTPS request, timed step by step: name lookup, TCP connect, TLS handshake, first byte of the
/// response. Steps after a failure are null.
/// </summary>
public sealed record HttpSample(
    DateTimeOffset Time, string Url, double? DnsMs, double? ConnectMs, double? TlsMs, double? FirstByteMs, int? Status, string? Error)
{
    public double? TotalMs => FirstByteMs is null ? null : (DnsMs ?? 0) + (ConnectMs ?? 0) + (TlsMs ?? 0) + FirstByteMs;
}

/// <summary>Wi-Fi link state from <c>iw</c>. Counters (<see cref="TxFailed"/>, <see cref="BeaconLoss"/>) are cumulative per association.</summary>
public sealed record WifiSample(
    DateTimeOffset Time,
    string Interface,
    bool Connected,
    string? Ssid,
    string? Bssid,
    double? FrequencyMhz,
    int? SignalDbm,
    int? SignalAverageDbm,
    double? TxBitrateMbps,
    double? RxBitrateMbps,
    long? TxPackets,
    long? TxFailed,
    long? TxRetries,
    long? BeaconLoss)
{
    public string? Band => WifiBand.Name(FrequencyMhz);

    /// <summary>802.11 channel number from the frequency.</summary>
    public int? Channel => WifiBand.Channel(FrequencyMhz);
}

/// <summary>One network heard in a Wi-Fi scan: an access point, a mesh node's hidden backhaul, a Chromecast's setup network, ...</summary>
public sealed record ScanSample(DateTimeOffset Time, string Bssid, string? Ssid, double FrequencyMhz, int? SignalDbm, bool Associated)
{
    public string Band => WifiBand.Name(FrequencyMhz)!;
    public int Channel => WifiBand.Channel(FrequencyMhz)!.Value;

    /// <summary>How wide the network sends (20, 40, 80, 160 MHz); null when the scan didn't say (counted as 20).</summary>
    public int? WidthMhz { get; init; }

    /// <summary>The middle of all the channels it uses; differs from <see cref="FrequencyMhz"/> (its main channel) when wider than 20 MHz.</summary>
    public double? CenterMhz { get; init; }
}

/// <summary>Band and channel from a Wi-Fi frequency.</summary>
public static class WifiBand
{
    /// <summary>
    /// The 20 MHz channels a network covers, as text: "6", "36–48 (80 MHz)". On 2.4 GHz a 40 MHz network uses
    /// its main channel and one 4 away ("1+5").
    /// </summary>
    public static string Channels(double frequencyMhz, int? widthMhz, double? centerMhz)
    {
        var main = Channel(frequencyMhz)!.Value;
        if (widthMhz is not { } width || width <= 20 || centerMhz is not { } center)
            return main.ToString();
        if (frequencyMhz < 3000)
            return $"{Math.Min(main, Channel(2 * center - frequencyMhz)!.Value)}+{Math.Max(main, Channel(2 * center - frequencyMhz)!.Value)} ({width} MHz)";
        var low = Channel(center - width / 2.0 + 10)!.Value;
        var high = Channel(center + width / 2.0 - 10)!.Value;
        return $"{low}–{high} ({width} MHz)";
    }

    /// <summary>The radio spectrum a network occupies, in MHz.</summary>
    public static (double Low, double High) Occupied(double frequencyMhz, int? widthMhz, double? centerMhz)
    {
        var width = widthMhz ?? 20;
        var center = width > 20 && centerMhz is { } c ? c : frequencyMhz;
        return (center - width / 2.0, center + width / 2.0);
    }

    /// <summary>True when two networks send on some of the same spectrum (and so take turns, or collide).</summary>
    public static bool Overlap((double Low, double High) a, (double Low, double High) b) => a.Low < b.High && b.Low < a.High;

    public static string? Name(double? frequencyMhz) => frequencyMhz switch
    {
        null => null,
        < 3000 => "2.4 GHz",
        < 5925 => "5 GHz",
        _ => "6 GHz",
    };

    public static int? Channel(double? frequencyMhz) => frequencyMhz switch
    {
        null => null,
        2484 => 14,
        < 3000 => (int)((frequencyMhz.Value - 2407) / 5),
        < 5925 => (int)((frequencyMhz.Value - 5000) / 5),
        _ => (int)((frequencyMhz.Value - 5950) / 5),
    };
}

/// <summary>Something that changed: roamed to another access point, lost the gateway, got a new IP, ...</summary>
public sealed record NetEvent(DateTimeOffset Time, string Kind, string Detail);
