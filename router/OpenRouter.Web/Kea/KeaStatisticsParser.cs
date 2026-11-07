using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenRouter.Web.Kea;

public static class KeaStatisticsParser
{
    /// <summary>
    /// Parses the response to <c>statistic-get-all</c>: <c>{"name": [[value, "2026-10-18 12:00:00.123456"], ...]}</c>.
    /// Kea writes timestamps in the server's local time, given as <paramref name="zone"/>.
    /// Non-integer statistics are skipped.
    /// </summary>
    public static KeaStatistics Parse(string json, TimeZoneInfo zone)
    {
        var node = JsonNode.Parse(json);
        // The Control Agent wraps responses in an array; the daemon's own socket doesn't.
        if (node is JsonArray array)
            node = array.FirstOrDefault();

        var result = (int?)node?["result"] ?? throw new FormatException("Kea response has no \"result\".");
        if (result != 0)
            throw new InvalidOperationException($"Kea returned result {result}: {(string?)node!["text"]}");

        var values = new Dictionary<string, IReadOnlyList<KeaStatSample>>();
        foreach (var (name, samples) in node!["arguments"] as JsonObject ?? [])
        {
            if (samples is not JsonArray list)
                continue;
            var parsed = list.OfType<JsonArray>()
                .Where(s => s.Count >= 2)
                .Select(s => ToLong(s[0]) is { } value && ToTime((string?)s[1], zone) is { } time
                    ? new KeaStatSample(value, time)
                    : null)
                .OfType<KeaStatSample>()
                .ToList();
            if (parsed.Count > 0)
                values[name] = parsed;
        }
        return new KeaStatistics(values);
    }

    private static long? ToLong(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static DateTimeOffset? ToTime(string? text, TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
