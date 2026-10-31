namespace OpenRouter.Web.Traffic;

/// <summary>Traffic rates over the last few seconds, in bits per second.</summary>
/// <param name="Time">End of the interval, Unix seconds.</param>
/// <param name="Clients">The busiest devices right now, busiest first.</param>
public sealed record LivePoint(long Time, double WanDown, double WanUp, IReadOnlyList<LiveClient> Clients)
{
    public const int TopClients = 8;

    /// <summary>Rates from byte counts moved during <paramref name="seconds"/>.</summary>
    public static LivePoint Build(
        DateTimeOffset at, double seconds, string? wanInterface, IReadOnlyList<LinkTraffic> links, IReadOnlyList<ClientTraffic> clients)
    {
        double Rate(long bytes) => seconds > 0 ? bytes * 8 / seconds : 0;
        var wan = links.FirstOrDefault(l => l.Interface == wanInterface);
        return new LivePoint(
            at.ToUnixTimeSeconds(),
            Rate(wan?.RxBytes ?? 0),
            Rate(wan?.TxBytes ?? 0),
            clients
                .OrderByDescending(c => c.DownBytes + c.UpBytes)
                .ThenBy(c => c.Key)
                .Take(TopClients)
                .Select(c => new LiveClient(c.Key, c.Key == TrafficAttribution.UnknownKey ? "Unknown addresses" : c.Name ?? c.Key,
                    c.Network, Rate(c.DownBytes), Rate(c.UpBytes)))
                .ToList());
    }
}

public sealed record LiveClient(string Key, string Name, string? Network, double Down, double Up);
