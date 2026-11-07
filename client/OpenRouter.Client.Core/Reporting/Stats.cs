namespace OpenRouter.Client.Reporting;

/// <summary>Latency statistics over a set of attempts, where null is a failed attempt (lost ping, no answer).</summary>
public sealed record Stats(int Count, int Lost, double? Average, double? Median, double? P95, double? Max, double? Jitter)
{
    public double LossPercent => Count == 0 ? 0 : 100.0 * Lost / Count;

    public static Stats Of(IEnumerable<double?> values)
    {
        var all = values.ToList();
        var ok = all.OfType<double>().ToList();
        if (ok.Count == 0)
            return new Stats(all.Count, all.Count, null, null, null, null, null);
        var sorted = ok.Order().ToList();
        // Jitter: the average change between consecutive replies (RFC 3550 style, without smoothing).
        double? jitter = ok.Count > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        return new Stats(all.Count, all.Count - ok.Count, ok.Average(), Percentile(sorted, 0.5), Percentile(sorted, 0.95), sorted[^1], jitter);
    }

    /// <summary>Nearest-rank percentile of already sorted values.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>A stretch of consecutive failed attempts.</summary>
public sealed record Outage(DateTimeOffset Start, DateTimeOffset End, int Attempts)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Runs of at least <paramref name="minimum"/> consecutive failures; shorter ones are ordinary loss.</summary>
    public static IReadOnlyList<Outage> Find(IEnumerable<(DateTimeOffset Time, bool Failed)> attempts, int minimum = 3)
    {
        var outages = new List<Outage>();
        DateTimeOffset? start = null;
        DateTimeOffset last = default;
        var count = 0;
        foreach (var (time, failed) in attempts)
        {
            if (failed)
            {
                start ??= time;
                last = time;
                count++;
                continue;
            }
            if (start is { } s && count >= minimum)
                outages.Add(new Outage(s, time, count));
            start = null;
            count = 0;
        }
        if (start is { } open && count >= minimum)
            outages.Add(new Outage(open, last, count));
        return outages;
    }
}
