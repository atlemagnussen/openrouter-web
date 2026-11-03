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
    public string? Band => FrequencyMhz switch
    {
        null => null,
        < 3000 => "2.4 GHz",
        < 5925 => "5 GHz",
        _ => "6 GHz",
    };

    /// <summary>802.11 channel number from the frequency.</summary>
    public int? Channel => FrequencyMhz switch
    {
        null => null,
        2484 => 14,
        < 3000 => (int)((FrequencyMhz.Value - 2407) / 5),
        < 5925 => (int)((FrequencyMhz.Value - 5000) / 5),
        _ => (int)((FrequencyMhz.Value - 5950) / 5),
    };
}

/// <summary>Something that changed: roamed to another access point, lost the gateway, got a new IP, ...</summary>
public sealed record NetEvent(DateTimeOffset Time, string Kind, string Detail);
