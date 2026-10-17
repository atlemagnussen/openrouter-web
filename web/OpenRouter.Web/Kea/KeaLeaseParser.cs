using System.Text.Json.Nodes;

namespace OpenRouter.Web.Kea;

public static class KeaLeaseParser
{
    /// <summary>
    /// Parses Kea's memfile (kea-leases4.csv). The file is append-only between cleanups,
    /// so an address can appear several times and the last row wins.
    /// </summary>
    public static IReadOnlyList<KeaLease> ParseCsv(string csv)
    {
        using var reader = new StringReader(csv);
        var header = reader.ReadLine()?.Split(',') ?? throw new FormatException("Lease file is empty.");

        int Column(string name) => Array.IndexOf(header, name) is var i and >= 0
            ? i
            : throw new FormatException($"Lease file has no \"{name}\" column.");

        var address = Column("address");
        var hwaddr = Column("hwaddr");
        var clientId = Column("client_id");
        var validLifetime = Column("valid_lifetime");
        var expire = Column("expire");
        var subnetId = Column("subnet_id");
        var hostname = Column("hostname");
        var state = Column("state");

        var byAddress = new Dictionary<string, KeaLease>();
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(',');
            // Skip blank lines, repeated headers and a last line Kea is still writing.
            if (fields.Length < header.Length || fields[address] == "address")
                continue;

            var lease = new KeaLease(
                IpAddress: fields[address],
                HwAddress: Empty(fields[hwaddr])?.ToLowerInvariant(),
                ClientId: Empty(fields[clientId]),
                Hostname: NormalizeHostname(Unescape(fields[hostname])),
                SubnetId: int.Parse(fields[subnetId]),
                ValidLifetime: int.Parse(fields[validLifetime]),
                Expires: DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[expire])),
                State: int.Parse(fields[state]));
            byAddress[lease.IpAddress] = lease;
        }
        return byAddress.Values.ToList();
    }

    /// <summary>Parses the response to <c>lease4-get-all</c> from Kea's control socket.</summary>
    public static IReadOnlyList<KeaLease> ParseCommandResponse(string json)
    {
        var node = JsonNode.Parse(json);
        // The Control Agent wraps responses in an array; the daemon's own socket doesn't.
        if (node is JsonArray array)
            node = array.FirstOrDefault();

        var result = (int?)node?["result"] ?? throw new FormatException("Kea response has no \"result\".");
        if (result == 3) // empty: no leases
            return [];
        if (result != 0)
            throw new InvalidOperationException($"Kea returned result {result}: {(string?)node!["text"]}");

        var leases = node!["arguments"]?["leases"] as JsonArray ?? [];
        return leases.OfType<JsonNode>().Select(l =>
        {
            var validLifetime = (int?)l["valid-lft"] ?? 0;
            return new KeaLease(
                IpAddress: (string?)l["ip-address"] ?? throw new FormatException("Lease without \"ip-address\"."),
                HwAddress: Empty((string?)l["hw-address"])?.ToLowerInvariant(),
                ClientId: Empty((string?)l["client-id"]),
                Hostname: NormalizeHostname((string?)l["hostname"]),
                SubnetId: (int?)l["subnet-id"] ?? 0,
                ValidLifetime: validLifetime,
                Expires: DateTimeOffset.FromUnixTimeSeconds(((long?)l["cltt"] ?? 0) + validLifetime),
                State: (int?)l["state"] ?? 0);
        }).ToList();
    }

    /// <summary>Clients send whatever they like: drop the trailing dot of an FQDN and treat blank as missing.</summary>
    public static string? NormalizeHostname(string? hostname) => Empty(hostname?.Trim().TrimEnd('.'));

    // The memfile escapes commas inside fields.
    private static string Unescape(string field) => field.Replace("&#x2c", ",");

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
