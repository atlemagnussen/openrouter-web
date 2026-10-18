namespace OpenRouter.Web.Kea;

/// <summary>
/// Kea's statistics (<c>statistic-get-all</c>). Each statistic keeps its most recent samples, newest first;
/// counters gain a sample each time they change. Statistics restart from zero when Kea restarts.
/// </summary>
public sealed record KeaStatistics(IReadOnlyDictionary<string, IReadOnlyList<KeaStatSample>> Values)
{
    public long? Latest(string name) =>
        Values.TryGetValue(name, out var samples) && samples.Count > 0 ? samples[0].Value : null;

    /// <summary>When the statistic last changed.</summary>
    public DateTimeOffset? LastChanged(string name) =>
        Values.TryGetValue(name, out var samples) && samples.Count > 0 ? samples[0].Time : null;
}

public sealed record KeaStatSample(long Value, DateTimeOffset Time);
