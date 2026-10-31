namespace OpenRouter.Web.Traffic;

/// <summary>What the sampler last did, for the Traffic page. Written by the sampler, read by requests.</summary>
public sealed class TrafficStatus
{
    private volatile Snapshot _current = new(null, null, null, null, null);

    /// <param name="LastAddressCount">Addresses with a counter in the nftables accounting table; null if it couldn't be read.</param>
    public sealed record Snapshot(
        DateTimeOffset? LastSample, int? LastClientCount, int? LastAddressCount, string? LastError, DateTimeOffset? LastErrorAt);

    public Snapshot Current => _current;

    public void Sampled(DateTimeOffset at, int? clients, int? addresses, string? error) =>
        _current = error is null
            ? new(at, clients, addresses, _current.LastError, _current.LastErrorAt)
            : new(at, clients, addresses, error, at);
}
