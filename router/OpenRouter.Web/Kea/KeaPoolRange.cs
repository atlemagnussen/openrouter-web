using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using OpenRouter.Web.Network;

namespace OpenRouter.Web.Kea;

/// <summary>An IPv4 address pool, written in Kea as <c>192.168.1.100 - 192.168.1.199</c> or <c>192.168.1.0/25</c>.</summary>
public readonly record struct KeaPoolRange(uint First, uint Last)
{
    public long Size => (long)Last - First + 1;

    public static bool TryParse(string text, [NotNullWhen(true)] out KeaPoolRange? range)
    {
        range = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            if (ToNumber(text[..dash].Trim()) is not { } first || ToNumber(text[(dash + 1)..].Trim()) is not { } last || last < first)
                return false;
            range = new KeaPoolRange(first, last);
            return true;
        }

        if (!IpPrefix.TryParse(text.Trim(), out var prefix) ||
            ToNumber(prefix.Value.Network.ToString()) is not { } network)
            return false;
        var hostBits = 32 - prefix.Value.Length;
        var mask = hostBits == 32 ? 0u : uint.MaxValue << hostBits;
        range = new KeaPoolRange(network & mask, (network & mask) | ~mask);
        return true;
    }

    public bool Contains(string address) => ToNumber(address) is { } number && number >= First && number <= Last;

    private static uint? ToNumber(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())
            : null;
}
