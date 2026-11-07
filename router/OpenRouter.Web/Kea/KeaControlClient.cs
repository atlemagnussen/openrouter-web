using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Kea;

/// <summary>
/// Talks to kea-dhcp4 over its unix control socket. The app's user needs to be in the
/// <c>_kea</c> group to reach <c>/run/kea</c>.
/// </summary>
public sealed class KeaControlClient(IOptions<RouterOptions> options)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public bool IsAvailable => File.Exists(options.Value.KeaControlSocket);

    /// <summary>Sends one command and returns the raw JSON response. Kea closes the connection after answering.</summary>
    public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.Value.KeaControlSocket), timeout.Token);

        var request = new JsonObject { ["command"] = command }.ToJsonString();
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), timeout.Token);

        await using var stream = new NetworkStream(socket);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(timeout.Token);
    }
}
