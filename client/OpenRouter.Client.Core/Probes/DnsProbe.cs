using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpenRouter.Web.Probes;

namespace OpenRouter.Client.Probes;

/// <summary>One A query over UDP, straight to a server (not through the laptop's own resolver or cache).</summary>
public static class DnsProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<(double? Milliseconds, int? ResponseCode, string? Error)> QueryAsync(
        IPAddress server, string name, CancellationToken cancellationToken)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53);
            await udp.SendAsync(DnsMessage.BuildQuery(id, name, DnsMessage.TypeA), timeout.Token);
            while (true)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                try
                {
                    var (code, _) = DnsMessage.ParseResponse(response.Buffer, id);
                    return (stopwatch.Elapsed.TotalMilliseconds, code, code == 0 ? null : DnsMessage.ResponseCodeName(code));
                }
                catch (FormatException)
                {
                    // Not the answer to this query; keep waiting.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null, $"no answer within {Timeout.TotalSeconds:0} s");
        }
        catch (SocketException ex)
        {
            return (null, null, ex.Message);
        }
    }
}
