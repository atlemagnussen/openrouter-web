using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace OpenRouter.Client.Probes;

/// <summary>
/// A fresh HTTPS request each time (no connection reuse), timed per step the way a browser opening a new site
/// experiences it: name lookup, TCP connect, TLS handshake, waiting for the first byte.
/// </summary>
public static class HttpProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<HttpSample> GetAsync(Uri url, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        double? dns = null, connect = null, tls = null;
        var step = "name lookup";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(url.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(url.Host, timeout.Token);
            dns = Lap(stopwatch);
            if (addresses.Length == 0)
                return new HttpSample(at, url.ToString(), dns, null, null, null, null, "name has no addresses");

            step = "connect";
            using var tcp = new TcpClient(addresses[0].AddressFamily);
            await tcp.ConnectAsync(addresses[0], url.Port, timeout.Token);
            connect = Lap(stopwatch);

            Stream stream = tcp.GetStream();
            if (url.Scheme == Uri.UriSchemeHttps)
            {
                step = "TLS";
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = url.Host }, timeout.Token);
                tls = Lap(stopwatch);
                stream = ssl;
            }

            step = "response";
            var request = $"GET {url.PathAndQuery} HTTP/1.1\r\nHost: {url.Host}\r\nUser-Agent: netcheck\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            var firstByte = Lap(stopwatch);
            return new HttpSample(at, url.ToString(), dns, connect, tls, firstByte, StatusCode(buffer.AsSpan(0, read)), null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            var message = ex is OperationCanceledException ? $"timed out after {Timeout.TotalSeconds:0} s" : ex.Message;
            return new HttpSample(at, url.ToString(), dns, connect, tls, null, null, $"{step}: {message}");
        }
    }

    private static double Lap(Stopwatch stopwatch)
    {
        var ms = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();
        return ms;
    }

    /// <summary>"HTTP/1.1 204 No Content" → 204.</summary>
    public static int? StatusCode(ReadOnlySpan<byte> start)
    {
        var line = Encoding.ASCII.GetString(start);
        var parts = line.Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) && int.TryParse(parts[1], out var code) ? code : null;
    }
}
