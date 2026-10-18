using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace OpenRouter.Web.Probes;

/// <summary>
/// Live tests from the router itself: ping and DNS lookups. Ping uses unprivileged ICMP sockets,
/// which Debian allows for all users (<c>net.ipv4.ping_group_range</c>).
/// </summary>
public sealed class ProbeService(ILogger<ProbeService> logger)
{
    private const int PingCount = 5;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);

    public async Task<ProbeResults> RunAsync(
        string? gateway, string? internet4, string? internet6, IReadOnlyList<string> dnsServers, string dnsName,
        CancellationToken cancellationToken)
    {
        async Task<PingResult?> Ping(string? target) =>
            target is null ? null : await PingAsync(target, cancellationToken);

        var gatewayTask = Ping(gateway);
        var internet4Task = Ping(internet4);
        var internet6Task = Ping(internet6);
        var dnsTask = Task.WhenAll(dnsServers.Select(s => QueryAsync(s, dnsName, cancellationToken)));
        return new ProbeResults(await gatewayTask, await internet4Task, await internet6Task, await dnsTask);
    }

    public async Task<PingResult> PingAsync(string target, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target, out var address))
            return new PingResult(target, 0, [], "not an IP address");

        using var ping = new Ping();
        var rtts = new List<TimeSpan>();
        try
        {
            for (var i = 0; i < PingCount; i++)
            {
                if (i > 0)
                    await Task.Delay(PingInterval, cancellationToken);
                var reply = await ping.SendPingAsync(address, PingTimeout, cancellationToken: cancellationToken);
                if (reply.Status == IPStatus.Success)
                    rtts.Add(TimeSpan.FromMilliseconds(reply.RoundtripTime));
            }
            return new PingResult(target, PingCount, rtts, null);
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not ping {Target}", target);
            return new PingResult(target, 0, [], (ex.InnerException ?? ex).Message);
        }
    }

    public async Task<DnsResult> QueryAsync(string server, string name, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(server, out var address))
            return new DnsResult(server, name, null, null, 0, "not an IP address");

        var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DnsTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var udp = new UdpClient(address.AddressFamily);
            udp.Connect(address, 53);
            await udp.SendAsync(DnsMessage.BuildQuery(id, name, DnsMessage.TypeA), timeout.Token);
            // Skip stray datagrams that don't answer this query.
            while (true)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                try
                {
                    var (code, answers) = DnsMessage.ParseResponse(response.Buffer, id);
                    return new DnsResult(server, name, stopwatch.Elapsed, code, answers, null);
                }
                catch (FormatException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DnsResult(server, name, null, null, 0, $"no answer within {DnsTimeout.TotalSeconds:0} s");
        }
        catch (SocketException ex)
        {
            return new DnsResult(server, name, null, null, 0, ex.Message);
        }
    }
}
