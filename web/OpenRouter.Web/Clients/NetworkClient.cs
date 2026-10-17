namespace OpenRouter.Web.Clients;

/// <summary>A device seen on a LAN interface, merged from the neighbour table and Kea by MAC address.</summary>
public sealed record NetworkClient(
    string Mac,
    string? Name,
    bool HasReservation,
    IReadOnlyList<string> Ipv4,
    IReadOnlyList<string> Ipv6,
    string Device,
    string? Network,
    NeighbourStatus Status);

/// <summary>Best kernel state across a device's addresses.</summary>
public enum NeighbourStatus
{
    /// <summary>Confirmed recently (REACHABLE, PERMANENT).</summary>
    Reachable,

    /// <summary>Being re-checked right now (DELAY, PROBE).</summary>
    Checking,

    /// <summary>Not confirmed recently, but not known to be gone (STALE).</summary>
    Stale,
}
