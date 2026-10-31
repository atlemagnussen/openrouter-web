using System.Text.Json.Nodes;

namespace OpenRouter.Web.Traffic;

/// <summary>
/// Parses <c>nft -j list table inet acct</c>: the accounting sets <c>up4</c>, <c>down4</c>, <c>up6</c> and
/// <c>down6</c>, whose elements carry per-address counters added by <c>update @up4 { ip saddr counter }</c>.
/// </summary>
public static class NftAcctParser
{
    public static IReadOnlyList<AddressCounter> Parse(string json)
    {
        var items = JsonNode.Parse(json)?["nftables"] as JsonArray
            ?? throw new FormatException("Not nft JSON output: no \"nftables\" array.");

        var counters = new List<AddressCounter>();
        foreach (var set in items.Select(i => i?["set"]).OfType<JsonObject>())
        {
            var name = (string?)set["name"] ?? "";
            TrafficDirection? direction =
                name.StartsWith("up", StringComparison.Ordinal) ? TrafficDirection.Up
                : name.StartsWith("down", StringComparison.Ordinal) ? TrafficDirection.Down
                : null;
            if (direction is null || set["elem"] is not JsonArray elements)
                continue;

            foreach (var element in elements)
            {
                // Elements with a counter are {"elem": {"val": ..., "counter": {...}}}; plain ones are just the value.
                var entry = element is JsonObject outer && outer["elem"] is JsonObject wrapped ? wrapped : element as JsonObject;
                if (entry is null ||
                    (string?)entry["val"] is not { } address ||
                    (long?)entry["counter"]?["bytes"] is not { } bytes)
                    continue;
                counters.Add(new AddressCounter(address, direction.Value, bytes));
            }
        }
        return counters;
    }
}
