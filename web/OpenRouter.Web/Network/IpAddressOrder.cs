using System.Buffers.Binary;
using System.Net;

namespace OpenRouter.Web.Network;

public static class IpAddressOrder
{
    /// <summary>Sort key in numeric address order: IPv4 before IPv6, unparseable last.</summary>
    public static (int Family, UInt128 Value) Key(string? address)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return (2, UInt128.MaxValue);

        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4
            ? (0, BinaryPrimitives.ReadUInt32BigEndian(bytes))
            : (1, BinaryPrimitives.ReadUInt128BigEndian(bytes));
    }
}
