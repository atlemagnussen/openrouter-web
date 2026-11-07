using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRouter.Web.Kea;

public static class KeaConfigParser
{
    // Kea also accepts '#' comments and <?include?>; neither is supported here.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static KeaConfig Parse(string json)
    {
        var dhcp4 = JsonNode.Parse(json, documentOptions: DocumentOptions)?["Dhcp4"] as JsonObject
            ?? throw new FormatException("Not a Kea DHCPv4 config: no \"Dhcp4\" object.");

        return new KeaConfig(
            Interfaces: Strings(dhcp4["interfaces-config"]?["interfaces"]),
            ValidLifetime: (int?)dhcp4["valid-lifetime"],
            MaxValidLifetime: (int?)dhcp4["max-valid-lifetime"],
            Subnets: Items(dhcp4["subnet4"]).Select(ParseSubnet).ToList(),
            GlobalReservations: Items(dhcp4["reservations"]).Select(ParseReservation).ToList());
    }

    private static KeaSubnet ParseSubnet(JsonNode node) => new(
        Id: (int?)node["id"] ?? 0,
        Subnet: (string?)node["subnet"] ?? throw new FormatException("Subnet without \"subnet\"."),
        Interface: (string?)node["interface"],
        Name: (string?)node["user-context"]?["name"],
        Pools: Items(node["pools"]).Select(p => (string?)p["pool"]).OfType<string>().ToList(),
        Options: Items(node["option-data"])
            .Select(o => ((string?)o["name"], (string?)o["data"]))
            .Where(o => o.Item1 is not null)
            .Select(o => new KeaOption(o.Item1!, o.Item2 ?? ""))
            .ToList(),
        ValidLifetime: (int?)node["valid-lifetime"],
        MaxValidLifetime: (int?)node["max-valid-lifetime"],
        Reservations: Items(node["reservations"]).Select(ParseReservation).ToList());

    private static KeaReservation ParseReservation(JsonNode node) => new(
        Hostname: (string?)node["hostname"],
        HwAddress: ((string?)node["hw-address"])?.ToLowerInvariant(),
        IpAddress: (string?)node["ip-address"]);

    private static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonNode>() : [];

    private static List<string> Strings(JsonNode? node) =>
        Items(node).Select(n => (string?)n).OfType<string>().ToList();
}
