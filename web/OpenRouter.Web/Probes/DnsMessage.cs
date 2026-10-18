using System.Buffers.Binary;
using System.Text;

namespace OpenRouter.Web.Probes;

/// <summary>Just enough of the DNS wire format (RFC 1035) to ask one question and read the answer's status.</summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    public static byte[] BuildQuery(ushort id, string name, ushort type)
    {
        var message = new List<byte>(32 + name.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // one question
        message.AddRange(header.ToArray());

        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
                throw new ArgumentException($"\"{name}\" is not a valid DNS name.", nameof(name));
            message.Add((byte)bytes.Length);
            message.AddRange(bytes);
        }
        message.Add(0);

        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question, type);
        BinaryPrimitives.WriteUInt16BigEndian(question[2..], 1); // class IN
        message.AddRange(question.ToArray());
        return message.ToArray();
    }

    /// <summary>Reads the response code (0 = NOERROR, 2 = SERVFAIL, 3 = NXDOMAIN, ...) and the number of answers.</summary>
    public static (int ResponseCode, int Answers) ParseResponse(ReadOnlySpan<byte> response, ushort expectedId)
    {
        if (response.Length < 12)
            throw new FormatException("DNS response is shorter than a header.");
        if (BinaryPrimitives.ReadUInt16BigEndian(response) != expectedId)
            throw new FormatException("DNS response doesn't match the query.");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0)
            throw new FormatException("DNS message is not a response.");
        return (flags & 0x000F, BinaryPrimitives.ReadUInt16BigEndian(response[6..]));
    }

    public static string ResponseCodeName(int code) => code switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE {code}",
    };
}
