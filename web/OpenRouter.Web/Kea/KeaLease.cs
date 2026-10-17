namespace OpenRouter.Web.Kea;

/// <summary>A DHCPv4 lease. <see cref="State"/> is Kea's lease state: 0 = default, 1 = declined, 2 = expired-reclaimed.</summary>
public sealed record KeaLease(
    string IpAddress,
    string? HwAddress,
    string? ClientId,
    string? Hostname,
    int SubnetId,
    int ValidLifetime,
    DateTimeOffset Expires,
    int State)
{
    public bool IsActive(DateTimeOffset now) => State == 0 && ValidLifetime > 0 && Expires > now;
}
