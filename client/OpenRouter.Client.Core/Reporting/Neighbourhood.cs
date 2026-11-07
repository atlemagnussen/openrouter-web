namespace OpenRouter.Client.Reporting;

/// <summary>One network seen across scans.</summary>
public sealed record HeardNetwork(
    string Bssid, string? Ssid, double FrequencyMhz, int? MedianDbm, int? StrongestDbm, int TimesHeard,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Associated)
{
    public string Band => WifiBand.Name(FrequencyMhz)!;
    public int Channel => WifiBand.Channel(FrequencyMhz)!.Value;
}

/// <summary>Which networks are around, and which compete with yours for airtime.</summary>
public static class Neighbourhood
{
    /// <summary>One row per BSSID (a network on one radio), strongest first.</summary>
    public static IReadOnlyList<HeardNetwork> Summarise(IEnumerable<ScanSample> scans) =>
        scans.GroupBy(s => s.Bssid)
            .Select(g =>
            {
                var signals = g.Where(s => s.SignalDbm is not null).Select(s => s.SignalDbm!.Value).Order().ToList();
                var last = g.MaxBy(s => s.Time)!;
                return new HeardNetwork(g.Key, g.Select(s => s.Ssid).LastOrDefault(s => s is not null), last.FrequencyMhz,
                    signals.Count > 0 ? signals[signals.Count / 2] : null, signals.Count > 0 ? signals[^1] : null,
                    g.Select(s => s.Time).Distinct().Count(), g.Min(s => s.Time), last.Time, g.Any(s => s.Associated));
            })
            .OrderByDescending(n => n.MedianDbm ?? -200)
            .ToList();

    /// <summary>
    /// Other networks that share the channel of <paramref name="frequencyMhz"/> and are loud enough to matter: on
    /// 2.4 GHz channels less than 5 apart overlap. Your own network's other access points (same name) are left out,
    /// but hidden networks are kept, since mesh systems use them for their own backhaul.
    /// </summary>
    public static IReadOnlyList<HeardNetwork> SharingChannel(IEnumerable<HeardNetwork> networks, double frequencyMhz, string? ownSsid, int loudDbm = -70)
    {
        var channel = WifiBand.Channel(frequencyMhz)!.Value;
        var band = WifiBand.Name(frequencyMhz);
        return networks
            .Where(n => n.Band == band && (band == "2.4 GHz" ? Math.Abs(n.Channel - channel) < 5 : n.Channel == channel))
            .Where(n => n.MedianDbm >= loudDbm && !n.Associated && (n.Ssid is null || n.Ssid != ownSsid))
            .ToList();
    }
}
