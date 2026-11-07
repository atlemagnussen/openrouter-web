using System.Text.Json;

namespace OpenRouter.Client.Settings;

/// <summary>
/// The user's own choices, shared by the app and the command line: which Wi-Fi networks are theirs.
/// Kept in <c>~/.config/netcheck/settings.json</c>, apart from the measurements.
/// </summary>
public sealed record ClientSettings
{
    /// <summary>Network names (SSIDs) that are yours, besides the one you're connected to.</summary>
    public IReadOnlyList<string> MyNetworks { get; init; } = [];

    /// <summary>Single access points that are yours, for hidden networks that can't be named.</summary>
    public IReadOnlyList<string> MyBssids { get; init; } = [];

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "netcheck", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The settings, or the defaults when the file doesn't exist yet or can't be read.</summary>
    public static ClientSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Writes to a temporary file first, so a crash never leaves half a file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    public bool IsMine(string? ssid, string bssid) =>
        (ssid is not null && MyNetworks.Contains(ssid)) || MyBssids.Contains(bssid, StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks a network (by name, or by BSSID when hidden) as yours or not.</summary>
    public ClientSettings With(string? ssid, string bssid, bool mine)
    {
        if (ssid is not null)
            return this with { MyNetworks = Toggle(MyNetworks, ssid, mine, StringComparer.Ordinal) };
        return this with { MyBssids = Toggle(MyBssids, bssid.ToLowerInvariant(), mine, StringComparer.OrdinalIgnoreCase) };
    }

    private static IReadOnlyList<string> Toggle(IReadOnlyList<string> list, string value, bool add, StringComparer comparer)
    {
        var rest = list.Where(v => !comparer.Equals(v, value)).ToList();
        if (add)
            rest.Add(value);
        return rest.Order(comparer).ToList();
    }
}
