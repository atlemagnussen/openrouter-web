namespace OpenRouter.Client.Reporting;

/// <summary>One network seen across scans.</summary>
public sealed record HeardNetwork(
    string Bssid, string? Ssid, double FrequencyMhz, int? MedianDbm, int? StrongestDbm, int TimesHeard,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Associated)
{
    public string Band => WifiBand.Name(FrequencyMhz)!;

    /// <summary>The main channel.</summary>
    public int Channel => WifiBand.Channel(FrequencyMhz)!.Value;

    public int? WidthMhz { get; init; }
    public double? CenterMhz { get; init; }

    /// <summary>All the channels it covers: "36–48 (80 MHz)".</summary>
    public string Channels => WifiBand.Channels(FrequencyMhz, WidthMhz, CenterMhz);

    public (double Low, double High) Occupied => WifiBand.Occupied(FrequencyMhz, WidthMhz, CenterMhz);
}

/// <summary>Whose a network is: the one you're connected to (all its access points), another of yours, or someone else's.</summary>
public enum Ownership { Connected, Mine, Other }

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
                // The width as last heard: a network can narrow or widen, and the latest is what it does now.
                var sized = g.Where(s => s.WidthMhz is not null).MaxBy(s => s.Time);
                return new HeardNetwork(g.Key, g.Select(s => s.Ssid).LastOrDefault(s => s is not null), last.FrequencyMhz,
                    signals.Count > 0 ? signals[signals.Count / 2] : null, signals.Count > 0 ? signals[^1] : null,
                    g.Select(s => s.Time).Distinct().Count(), g.Min(s => s.Time), last.Time, g.Any(s => s.Associated))
                {
                    WidthMhz = sized?.FrequencyMhz == last.FrequencyMhz ? sized.WidthMhz : null,
                    CenterMhz = sized?.FrequencyMhz == last.FrequencyMhz ? sized.CenterMhz : null,
                };
            })
            .OrderByDescending(n => n.MedianDbm ?? -200)
            .ToList();

    /// <summary>
    /// Whose each network is. A hidden network takes the owner of a named one on the same device: mesh systems give
    /// their hidden links addresses that differ from the named network's only in the first and last byte.
    /// </summary>
    public static IReadOnlyDictionary<string, Ownership> Classify(IReadOnlyList<HeardNetwork> networks, string? connectedSsid,
        Settings.ClientSettings settings)
    {
        Ownership Named(HeardNetwork n) =>
            n.Ssid is not null && n.Ssid == connectedSsid ? Ownership.Connected
            : settings.IsMine(n.Ssid, n.Bssid) ? Ownership.Mine
            : Ownership.Other;

        var result = networks.ToDictionary(n => n.Bssid, Named);
        foreach (var hidden in networks.Where(n => n.Ssid is null && result[n.Bssid] == Ownership.Other))
        {
            var sibling = networks.FirstOrDefault(n => n.Ssid is not null && result[n.Bssid] != Ownership.Other && SameDevice(n.Bssid, hidden.Bssid));
            if (sibling is not null)
                result[hidden.Bssid] = result[sibling.Bssid];
        }
        return result;
    }

    /// <summary>Addresses that differ only in the first and last byte: one device's networks.</summary>
    public static bool SameDevice(string a, string b) => a.Length == 17 && b.Length == 17 && a != b && a[3..14] == b[3..14];

    /// <summary>
    /// Other networks that send on some of the same spectrum as the one you're on at <paramref name="frequencyMhz"/>, and
    /// are loud enough to matter. Wide networks count with all their channels: an 80 MHz network on 36 also covers 40,
    /// 44 and 48. Your own network's other access points (same name) are left out; hidden networks are kept, since
    /// mesh systems use them for their own backhaul.
    /// </summary>
    public static IReadOnlyList<HeardNetwork> SharingChannel(IReadOnlyList<HeardNetwork> networks, double frequencyMhz, string? ownSsid, int loudDbm = -70)
    {
        var home = HomeSpectrum(networks, frequencyMhz);
        return networks
            .Where(n => WifiBand.Overlap(n.Occupied, home))
            .Where(n => n.MedianDbm >= loudDbm && !n.Associated && (n.Ssid is null || n.Ssid != ownSsid))
            .ToList();
    }

    /// <summary>The spectrum of the network you're connected to on that frequency (20 MHz around it when its width isn't known).</summary>
    public static (double Low, double High) HomeSpectrum(IReadOnlyList<HeardNetwork> networks, double frequencyMhz) =>
        networks.FirstOrDefault(n => n.Associated && n.FrequencyMhz == frequencyMhz)?.Occupied ?? WifiBand.Occupied(frequencyMhz, null, null);

    /// <summary>"36–48 (80 MHz)" for the network you're connected to, or just the channel.</summary>
    public static string HomeChannels(IReadOnlyList<HeardNetwork> networks, double frequencyMhz) =>
        networks.FirstOrDefault(n => n.Associated && n.FrequencyMhz == frequencyMhz)?.Channels ?? WifiBand.Channel(frequencyMhz).ToString()!;
}
