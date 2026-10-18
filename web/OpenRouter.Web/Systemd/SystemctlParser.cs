using System.Globalization;

namespace OpenRouter.Web.Systemd;

public static class SystemctlParser
{
    /// <summary>
    /// Parses <c>systemctl show --timestamp=unix --property=...</c> for several units:
    /// <c>Key=Value</c> lines, one blank-line-separated block per unit.
    /// </summary>
    public static IReadOnlyList<UnitStatus> Parse(string text) =>
        text.ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseUnit)
            .ToList();

    private static UnitStatus ParseUnit(string block)
    {
        var values = block.Split('\n')
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0])
            .ToDictionary(g => g.Key, g => g.Last()[1]);

        string Get(string key) => values.GetValueOrDefault(key, "");

        return new UnitStatus(
            Id: values.GetValueOrDefault("Id") ?? throw new FormatException("systemctl output without \"Id\"."),
            LoadState: Get("LoadState"),
            ActiveState: Get("ActiveState"),
            SubState: Get("SubState"),
            Result: Get("Result"),
            Restarts: int.TryParse(Get("NRestarts"), out var restarts) ? restarts : 0,
            ActiveSince: UnixTime(Get("ActiveEnterTimestamp")));
    }

    // --timestamp=unix prints "@1792324800"; empty when the unit never started.
    private static DateTimeOffset? UnixTime(string text) =>
        text.StartsWith('@') && long.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
