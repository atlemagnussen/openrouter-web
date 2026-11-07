using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenRouter.Web.Dns;

/// <summary>Parses bind's JSON statistics (<c>http://127.0.0.1:8053/json/v1</c>).</summary>
public static class BindStatsParser
{
    public static BindStats Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("bind statistics are not a JSON object.");
        if (root["nsstats"] is null && root["views"] is null)
            throw new FormatException("Not bind statistics: no \"nsstats\" or \"views\".");

        // "_bind" is bind's internal CHAOS view (version.bind and friends), not real resolving.
        var views = (root["views"] as JsonObject ?? [])
            .Where(v => v.Key != "_bind")
            .Select(v => v.Value?["resolver"])
            .OfType<JsonObject>()
            .ToList();

        return new BindStats(
            Version: (string?)root["version"],
            BootTime: DateTimeOffset.TryParse((string?)root["boot-time"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var boot)
                ? boot
                : null,
            Server: Numbers(root["nsstats"]),
            Rcodes: Numbers(root["rcodes"]),
            Resolver: Sum(views.Select(v => Numbers(v["stats"]))),
            Cache: Sum(views.Select(v => Numbers(v["cachestats"]))));
    }

    private static Dictionary<string, long> Numbers(JsonNode? node)
    {
        var numbers = new Dictionary<string, long>();
        foreach (var (key, value) in node as JsonObject ?? [])
        {
            if (value is JsonValue v && v.TryGetValue<long>(out var number))
                numbers[key] = number;
        }
        return numbers;
    }

    private static Dictionary<string, long> Sum(IEnumerable<Dictionary<string, long>> parts)
    {
        var sum = new Dictionary<string, long>();
        foreach (var part in parts)
        {
            foreach (var (key, value) in part)
                sum[key] = sum.GetValueOrDefault(key) + value;
        }
        return sum;
    }
}
