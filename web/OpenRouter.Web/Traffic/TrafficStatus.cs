namespace OpenRouter.Web.Traffic;

/// <summary>What the sampler last did, for the Traffic page. Written by the sampler, read by requests.</summary>
public sealed class TrafficStatus
{
    private volatile Snapshot _current = new(null, null, null, null);

    public sealed record Snapshot(DateTimeOffset? LastSample, int? LastClientCount, string? LastError, DateTimeOffset? LastErrorAt);

    public Snapshot Current => _current;

    public void Sampled(DateTimeOffset at, int? clients, string? error) =>
        _current = error is null
            ? new(at, clients, _current.LastError, _current.LastErrorAt)
            : new(at, clients, error, at);
}
