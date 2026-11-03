using System.Globalization;

namespace OpenRouter.Client;

/// <summary>Settings for <c>netcheck run</c>; defaults suit a few hours on a laptop.</summary>
public sealed record RunOptions
{
    /// <summary>How long to run; null runs until Ctrl+C.</summary>
    public TimeSpan? Duration { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DnsInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HttpInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WifiInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Extra ping targets; the gateway is always added.</summary>
    public IReadOnlyList<string> PingTargets { get; init; } = ["1.1.1.1", "8.8.8.8", "2606:4700:4700::1111"];

    /// <summary>DNS servers to query; the gateway is added first unless <see cref="NoGatewayDns"/>.</summary>
    public IReadOnlyList<string> DnsServers { get; init; } = ["1.1.1.1"];
    public bool NoGatewayDns { get; init; }

    /// <summary>Names looked up in turn, so some answers come from cache and some don't.</summary>
    public IReadOnlyList<string> DnsNames { get; init; } =
        ["www.google.com", "www.youtube.com", "www.netflix.com", "www.vg.no", "www.nrk.no", "github.com", "debian.org", "www.apple.com"];

    public IReadOnlyList<string> HttpUrls { get; init; } = ["https://www.google.com/generate_204", "https://1.1.1.1/cdn-cgi/trace"];

    /// <summary>Network interface to watch; by default the one with the default route.</summary>
    public string? Interface { get; init; }

    public string DatabasePath { get; init; } = DefaultDatabasePath;

    public string? Note { get; init; }

    public static string DefaultDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openrouter-client", "netcheck.db");

    /// <summary>"90s", "15m", "4h", "1h30m"; "0" or "forever" means no limit.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        if (text is "0" or "forever")
            return null;
        var total = TimeSpan.Zero;
        var number = "";
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (char.IsDigit(c) || c == '.')
            {
                number += c;
                continue;
            }
            if (number.Length == 0)
                throw new FormatException($"\"{text}\" is not a duration (try 30s, 15m, 4h).");
            var value = double.Parse(number, CultureInfo.InvariantCulture);
            total += c switch
            {
                's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                'h' => TimeSpan.FromHours(value),
                'd' => TimeSpan.FromDays(value),
                _ => throw new FormatException($"\"{text}\": unknown unit '{c}' (use s, m, h or d)."),
            };
            number = "";
        }
        if (number.Length > 0)
            total += TimeSpan.FromSeconds(double.Parse(number, CultureInfo.InvariantCulture));
        return total;
    }

    /// <summary>Applies command line options (after the command name) to the defaults.</summary>
    public static RunOptions Parse(IReadOnlyList<string> args)
    {
        var options = new RunOptions();
        var pings = new List<string>();
        var dns = new List<string>();
        var urls = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new FormatException($"{args[i]} needs a value.");
            options = args[i] switch
            {
                "--duration" or "-d" => options with { Duration = ParseDuration(Value()) },
                "--ping" => Add(pings, Value(), options),
                "--dns" => Add(dns, Value(), options),
                "--url" => Add(urls, Value(), options),
                "--no-gateway-dns" => options with { NoGatewayDns = true },
                "--interface" or "-i" => options with { Interface = Value() },
                "--db" => options with { DatabasePath = Value() },
                "--note" => options with { Note = Value() },
                "--ping-interval" => options with { PingInterval = ParseDuration(Value()) ?? throw new FormatException("--ping-interval can't be 0.") },
                "--dns-interval" => options with { DnsInterval = ParseDuration(Value()) ?? throw new FormatException("--dns-interval can't be 0.") },
                "--http-interval" => options with { HttpInterval = ParseDuration(Value()) ?? throw new FormatException("--http-interval can't be 0.") },
                _ => throw new FormatException($"Unknown option {args[i]}. Try netcheck help."),
            };
        }
        // Options given on the command line replace the defaults rather than adding to them.
        return options with
        {
            PingTargets = pings.Count > 0 ? pings : options.PingTargets,
            DnsServers = dns.Count > 0 ? dns : options.DnsServers,
            HttpUrls = urls.Count > 0 ? urls : options.HttpUrls,
        };
    }

    private static RunOptions Add(List<string> list, string value, RunOptions options)
    {
        list.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return options;
    }
}
