namespace OpenRouter.Client.Probes;

/// <summary>
/// Who made a device, from the first half of its MAC address (the OUI), using the IEEE list most Linux systems
/// ship in <c>/usr/share/hwdata/oui.txt</c> (package hwdata). Mesh systems give each extra network its own
/// "locally administered" address made from the real one, so those are looked up with that bit cleared.
/// </summary>
public sealed class Vendors(IReadOnlyDictionary<string, string> byPrefix)
{
    public static readonly string[] DefaultPaths = ["/usr/share/hwdata/oui.txt", "/usr/share/ieee-data/oui.txt", "/usr/share/misc/oui.txt"];

    public static Vendors Empty { get; } = new(new Dictionary<string, string>());

    public static Vendors Load()
    {
        foreach (var path in DefaultPaths)
        {
            if (File.Exists(path))
                return Parse(File.ReadLines(path));
        }
        return Empty;
    }

    /// <summary>Reads lines like "B0-B9-8A   (hex)		NETGEAR".</summary>
    public static Vendors Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var hex = line.IndexOf("(hex)", StringComparison.Ordinal);
            if (hex < 8)
                continue;
            var prefix = line[..hex].Trim().Replace('-', ':');
            var name = line[(hex + 5)..].Trim();
            if (prefix.Length == 8 && name.Length > 0)
                map.TryAdd(prefix, name);
        }
        return new Vendors(map);
    }

    /// <summary>The maker, "(random address)" for a locally administered one that matches nothing, or null if unknown.</summary>
    public string? Lookup(string mac)
    {
        if (mac.Length < 8)
            return null;
        if (byPrefix.TryGetValue(mac[..8], out var name))
            return name;
        if (!IsLocal(mac))
            return null;
        var first = Convert.ToByte(mac[..2], 16) & ~0x02;
        return byPrefix.TryGetValue($"{first:x2}{mac[2..8]}", out var derived) ? derived : "(random address)";
    }

    /// <summary>
    /// Makers for a set of BSSIDs heard together. A locally administered one that matches nothing gets the maker of a
    /// sibling: a network whose address differs only in the first and last byte (mesh systems make up extra BSSIDs that way).
    /// </summary>
    public IReadOnlyDictionary<string, string?> LookupAll(IEnumerable<string> bssids)
    {
        var all = bssids.Distinct().ToList();
        var result = all.ToDictionary(b => b, Lookup);
        foreach (var bssid in all.Where(b => result[b] == "(random address)"))
        {
            var sibling = all.FirstOrDefault(other => other != bssid && other.Length == 17 && bssid.Length == 17 &&
                other[3..14] == bssid[3..14] && result[other] is { } maker && maker != "(random address)");
            if (sibling is not null)
                result[bssid] = result[sibling];
        }
        return result;
    }

    /// <summary>The "locally administered" bit: set on randomised addresses and on extra networks a device makes up.</summary>
    public static bool IsLocal(string mac) => mac.Length >= 2 && (Convert.ToByte(mac[..2], 16) & 0x02) != 0;
}
