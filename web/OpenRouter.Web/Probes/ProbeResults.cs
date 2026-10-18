namespace OpenRouter.Web.Probes;

/// <param name="Rtts">Round-trip times of the replies that came back.</param>
public sealed record PingResult(string Target, int Sent, IReadOnlyList<TimeSpan> Rtts, string? Error)
{
    public int Lost => Sent - Rtts.Count;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    public TimeSpan? Average => Rtts.Count > 0 ? TimeSpan.FromTicks((long)Rtts.Average(r => r.Ticks)) : null;
    public TimeSpan? Max => Rtts.Count > 0 ? Rtts.Max() : null;
    public TimeSpan? Min => Rtts.Count > 0 ? Rtts.Min() : null;
}

/// <param name="ResponseCode">Null when no answer came back (see <paramref name="Error"/>).</param>
public sealed record DnsResult(string Server, string Name, TimeSpan? Elapsed, int? ResponseCode, int Answers, string? Error);

public sealed record ProbeResults(
    PingResult? Gateway,
    PingResult? Internet4,
    PingResult? Internet6,
    IReadOnlyList<DnsResult> Dns);
