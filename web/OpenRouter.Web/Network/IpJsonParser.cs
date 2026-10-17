using System.Text.Json.Nodes;

namespace OpenRouter.Web.Network;

/// <summary>Parses the JSON output of iproute2 (<c>ip -j neigh</c>, <c>ip -j addr</c>).</summary>
public static class IpJsonParser
{
    // iproute2 reports "forever" as the maximum 32-bit value.
    private const long Forever = uint.MaxValue;

    public static IReadOnlyList<Neighbour> ParseNeighbours(string json) =>
        Items(JsonNode.Parse(json)).Select(n => new Neighbour(
            Address: (string?)n["dst"] ?? throw new FormatException("Neighbour without \"dst\"."),
            Device: (string?)n["dev"] ?? "",
            Mac: ((string?)n["lladdr"])?.ToLowerInvariant(),
            States: Items(n["state"]).Select(s => (string?)s).OfType<string>().ToList()))
        .ToList();

    public static IReadOnlyList<NetInterface> ParseInterfaces(string json) =>
        Items(JsonNode.Parse(json)).Select(n => new NetInterface(
            Name: (string?)n["ifname"] ?? throw new FormatException("Interface without \"ifname\"."),
            Mac: ((string?)n["address"])?.ToLowerInvariant(),
            OperState: (string?)n["operstate"] ?? "UNKNOWN",
            Addresses: Items(n["addr_info"]).Select(a => new NetAddress(
                Address: (string?)a["local"] ?? "",
                PrefixLength: (int?)a["prefixlen"] ?? 0,
                IsIpv6: (string?)a["family"] == "inet6",
                Scope: (string?)a["scope"] ?? "",
                ValidLifetime: Lifetime(a["valid_life_time"]),
                PreferredLifetime: Lifetime(a["preferred_life_time"])))
            .ToList()))
        .ToList();

    private static TimeSpan? Lifetime(JsonNode? node) =>
        (long?)node is { } seconds and < Forever ? TimeSpan.FromSeconds(seconds) : null;

    private static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonNode>() : [];
}
