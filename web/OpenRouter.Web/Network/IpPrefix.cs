using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace OpenRouter.Web.Network;

/// <summary>An address prefix such as <c>2001:db8::/64</c> or <c>192.168.1.0/24</c>.</summary>
public readonly record struct IpPrefix(IPAddress Network, int Length)
{
    public static bool TryParse(string text, [NotNullWhen(true)] out IpPrefix? prefix)
    {
        prefix = null;
        var slash = text.IndexOf('/');
        if (slash < 0 ||
            !IPAddress.TryParse(text.AsSpan(0, slash), out var address) ||
            !int.TryParse(text.AsSpan(slash + 1), out var length) ||
            length < 0 || length > address.GetAddressBytes().Length * 8)
            return false;

        prefix = new IpPrefix(address, length);
        return true;
    }

    public bool Contains(string address) => IPAddress.TryParse(address, out var ip) && Contains(ip);

    public bool Contains(IPAddress address)
    {
        var network = Network.GetAddressBytes();
        var other = address.GetAddressBytes();
        if (network.Length != other.Length)
            return false;

        var fullBytes = Length / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (network[i] != other[i])
                return false;
        }

        var remainingBits = Length % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (network[fullBytes] & mask) == (other[fullBytes] & mask);
    }

    public override string ToString() => $"{Network}/{Length}";
}
