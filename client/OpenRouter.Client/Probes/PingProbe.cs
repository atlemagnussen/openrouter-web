using System.Net;
using System.Net.NetworkInformation;

namespace OpenRouter.Client.Probes;

/// <summary>ICMP echo. Linux allows it without root when net.ipv4.ping_group_range includes the user (Fedora and Debian default).</summary>
public static class PingProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Round trip in milliseconds, or null when nothing came back in time.</summary>
    public static async Task<double?> PingAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, Timeout, cancellationToken: cancellationToken);
            // RoundtripTime is whole milliseconds; under 1 ms reads as 0, which is still a reply.
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            // No route, network down: the same as no reply for our purposes.
            return null;
        }
    }
}
