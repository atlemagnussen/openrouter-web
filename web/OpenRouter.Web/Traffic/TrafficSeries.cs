namespace OpenRouter.Web.Traffic;

/// <summary>Chart data: rates in bits per second per bucket, null where nothing was sampled.</summary>
/// <param name="Timestamps">Start of each bucket, Unix seconds.</param>
public sealed record TrafficSeries(
    long From,
    int BucketSeconds,
    IReadOnlyList<long> Timestamps,
    string? WanInterface,
    IReadOnlyList<double?> WanDown,
    IReadOnlyList<double?> WanUp,
    IReadOnlyList<ClientSeries> Clients);

/// <param name="Key">MAC address, <see cref="TrafficAttribution.UnknownKey"/> or <see cref="TrafficSeriesBuilder.OtherKey"/>.</param>
public sealed record ClientSeries(string Key, string Name, string? Network, long TotalBytes, IReadOnlyList<double?> BitsPerSecond);

/// <summary>Turns per-bucket sums from the database into chart series.</summary>
public static class TrafficSeriesBuilder
{
    public const string OtherKey = "other";

    /// <param name="Minutes">How many sampled minutes the bucket holds for this interface.</param>
    public sealed record LinkRow(int Bucket, string Interface, string Role, long RxBytes, long TxBytes, int Minutes);

    public sealed record ClientRow(int Bucket, string Key, string? Name, string? Network, long Bytes);

    public static TrafficSeries Build(
        long from, long to, int bucketSeconds, IReadOnlyList<LinkRow> links, IReadOnlyList<ClientRow> clients, int top)
    {
        var count = (int)((to - from) / bucketSeconds);
        var timestamps = Enumerable.Range(0, count).Select(i => from + (long)i * bucketSeconds).ToList();

        // Rates are bytes over the time actually sampled, so a bucket the sampler only partly covered isn't diluted.
        var sampledSeconds = new int[count];
        foreach (var row in links.Where(r => r.Bucket >= 0 && r.Bucket < count))
            sampledSeconds[row.Bucket] = Math.Max(sampledSeconds[row.Bucket], row.Minutes * 60);
        foreach (var row in clients.Where(r => r.Bucket >= 0 && r.Bucket < count && sampledSeconds[r.Bucket] == 0))
            sampledSeconds[row.Bucket] = bucketSeconds;

        double? Rate(long bytes, int bucket) =>
            sampledSeconds[bucket] == 0 ? null : bytes * 8.0 / sampledSeconds[bucket];

        var wan = links.Where(l => l.Role == "WAN").Select(l => l.Interface).Order().FirstOrDefault();
        var wanDown = new long[count];
        var wanUp = new long[count];
        foreach (var row in links.Where(r => r.Interface == wan && r.Bucket >= 0 && r.Bucket < count))
        {
            wanDown[row.Bucket] += row.RxBytes;
            wanUp[row.Bucket] += row.TxBytes;
        }

        var byClient = clients
            .Where(r => r.Bucket >= 0 && r.Bucket < count)
            .GroupBy(r => r.Key)
            .Select(g => (Key: g.Key, Rows: g.ToList(), Total: g.Sum(r => r.Bytes)))
            .OrderByDescending(c => c.Total)
            .ThenBy(c => c.Key)
            .ToList();

        var series = new List<ClientSeries>();
        foreach (var client in byClient.Take(top))
        {
            var bytes = new long[count];
            foreach (var row in client.Rows)
                bytes[row.Bucket] += row.Bytes;
            var first = client.Rows[0];
            var name = client.Key == TrafficAttribution.UnknownKey
                ? "Unknown addresses"
                : client.Rows.Select(r => r.Name).FirstOrDefault(n => n is not null) ?? client.Key;
            series.Add(new ClientSeries(client.Key, name, first.Network, client.Total,
                bytes.Select((b, i) => Rate(b, i)).ToList()));
        }

        var rest = byClient.Skip(top).ToList();
        if (rest.Count > 0)
        {
            var bytes = new long[count];
            foreach (var row in rest.SelectMany(c => c.Rows))
                bytes[row.Bucket] += row.Bytes;
            series.Add(new ClientSeries(OtherKey, $"Other ({rest.Count} device{(rest.Count == 1 ? "" : "s")})", null,
                rest.Sum(c => c.Total), bytes.Select((b, i) => Rate(b, i)).ToList()));
        }

        return new TrafficSeries(
            from, bucketSeconds, timestamps, wan,
            wanDown.Select((b, i) => Rate(b, i)).ToList(),
            wanUp.Select((b, i) => Rate(b, i)).ToList(),
            series);
    }
}

/// <summary>The time ranges the Traffic page offers, each with a bucket size that gives a few hundred points.</summary>
public sealed record TrafficRange(string Key, string Label, TimeSpan Length, int BucketSeconds)
{
    public static IReadOnlyList<TrafficRange> All { get; } =
    [
        new("1h", "1 hour", TimeSpan.FromHours(1), 60),
        new("6h", "6 hours", TimeSpan.FromHours(6), 60),
        new("24h", "24 hours", TimeSpan.FromDays(1), 300),
        new("7d", "7 days", TimeSpan.FromDays(7), 1800),
        new("30d", "30 days", TimeSpan.FromDays(30), 7200),
    ];

    public static TrafficRange Find(string? key) => All.FirstOrDefault(r => r.Key == key) ?? All[2];

    /// <summary>[from, to) ending with the bucket that contains <paramref name="now"/>, aligned to whole buckets.</summary>
    public (long From, long To) Window(DateTimeOffset now)
    {
        var to = (now.ToUnixTimeSeconds() / BucketSeconds + 1) * BucketSeconds;
        return (to - (long)Length.TotalSeconds, to);
    }
}
