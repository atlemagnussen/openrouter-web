using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenRouter.Client.Probes;

/// <summary>Parses <c>iw dev IF scan dump</c>: the kernel's list of networks heard in the last scan.</summary>
public static partial class ScanParser
{
    /// <param name="text">The command's output.</param>
    /// <param name="at">When the dump was read; entries are stamped with it.</param>
    /// <param name="maxAge">Entries the kernel last heard longer ago than this are left out (it keeps them for a while).</param>
    public static IReadOnlyList<ScanSample> Parse(string text, DateTimeOffset at, TimeSpan maxAge)
    {
        var results = new List<ScanSample>();
        foreach (Match block in BssPattern().Matches(text))
        {
            var body = block.Groups["body"].Value;
            if (Field(body, "freq") is not { } freqText || !double.TryParse(LeadingNumber().Match(freqText).Value, CultureInfo.InvariantCulture, out var freq))
                continue;
            if (Field(body, "last seen") is { } seen && seen.EndsWith("ms ago") &&
                long.TryParse(LeadingNumber().Match(seen).Value, CultureInfo.InvariantCulture, out var ago) && ago > maxAge.TotalMilliseconds)
                continue;
            int? signal = Field(body, "signal") is { } s && double.TryParse(LeadingNumber().Match(s).Value, CultureInfo.InvariantCulture, out var dbm)
                ? (int)Math.Round(dbm)
                : null;
            var (width, center) = Width(body, freq);
            results.Add(new ScanSample(at, block.Groups["bssid"].Value.ToLowerInvariant(), Ssid(Field(body, "SSID")), freq, signal,
                block.Groups["rest"].Value.Contains("associated"))
            {
                WidthMhz = width,
                CenterMhz = center,
            });
        }
        return results;
    }

    /// <summary>
    /// The channel width from the "VHT operation" block (80/160 MHz, 5 GHz), else the "HT operation" block
    /// (40 MHz when there's a secondary channel and the width is "any"), else 20 MHz.
    /// </summary>
    private static (int? Width, double? Center) Width(string body, double frequencyMhz)
    {
        var vht = Section(body, "VHT operation");
        if (vht is not null && Field(vht, "* channel width", nested: true) is { } vhtWidth &&
            int.TryParse(Field(vht, "* center freq segment 1", nested: true), out var segment1) && segment1 > 0)
        {
            int.TryParse(Field(vht, "* center freq segment 2", nested: true), out var segment2);
            static double Mhz(int channel) => 5000 + 5 * channel;
            switch (vhtWidth[0])
            {
                case '1' when segment2 > 0 && Math.Abs(segment2 - segment1) == 8:
                    return (160, Mhz(segment2)); // 160 MHz, signalled the newer way
                case '1':
                    return (80, Mhz(segment1));  // 80 MHz (or 80+80, counted as its first 80)
                case '2':
                    return (160, Mhz(segment1));
                case '3':
                    return (80, Mhz(segment1));
            }
        }
        var ht = Section(body, "HT operation");
        if (ht is null)
            return (null, null);
        var offset = Field(ht, "* secondary channel offset", nested: true);
        var any = Field(ht, "* STA channel width", nested: true) == "any";
        return offset switch
        {
            "above" when any => (40, frequencyMhz + 10),
            "below" when any => (40, frequencyMhz - 10),
            _ => (20, frequencyMhz),
        };
    }

    /// <summary>The indented lines under a top-level "Name:" line of a block.</summary>
    private static string? Section(string body, string name)
    {
        var lines = body.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == $"\t{name}:");
        if (start < 0)
            return null;
        return string.Join('\n', lines.Skip(start + 1).TakeWhile(l => l.StartsWith("\t\t")));
    }

    /// <summary>A hidden network sends an empty name, or one made of zero bytes (which iw prints as \x00).</summary>
    private static string? Ssid(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Replace("\\x00", "").Length == 0)
            return null;
        // iw escapes other bytes as \xNN; turn them back into UTF-8 text.
        if (!raw.Contains("\\x"))
            return raw;
        var bytes = new List<byte>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 3 < raw.Length && raw[i + 1] == 'x' &&
                byte.TryParse(raw.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 3;
            }
            else
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(raw[i].ToString()));
        }
        return System.Text.Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>The first "name: value" line at the top level of a block (one tab deep), or at any depth when nested.</summary>
    private static string? Field(string body, string name, bool nested = false)
    {
        foreach (var line in body.Split('\n'))
        {
            if (!nested && (!line.StartsWith('\t') || line.StartsWith("\t\t")))
                continue;
            var trimmed = line.Trim();
            if (trimmed.StartsWith(name + ":", StringComparison.Ordinal))
                return trimmed[(name.Length + 1)..].Trim();
        }
        return null;
    }

    [GeneratedRegex(@"^BSS (?<bssid>[0-9a-fA-F:]{17})(?<rest>[^\n]*)\n(?<body>(?:\t[^\n]*\n?)*)", RegexOptions.Multiline)]
    private static partial Regex BssPattern();

    [GeneratedRegex(@"^-?\d+(\.\d+)?")]
    private static partial Regex LeadingNumber();
}
