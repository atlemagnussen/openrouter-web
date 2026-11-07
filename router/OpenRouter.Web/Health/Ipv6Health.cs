using System.Globalization;
using System.Net;
using OpenRouter.Web.Network;
using OpenRouter.Web.Pages;
using OpenRouter.Web.Radvd;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>
/// radvd's router advertisements, checked against the router's addresses and routes. Phones are much
/// stricter about RAs than laptops, so this is a likely place for "only phones have problems".
/// </summary>
public static class Ipv6Health
{
    /// <summary>Android 14+ ignores RA information with a shorter lifetime (the kernel's accept_ra_min_lft).</summary>
    public const int MinimumLifetime = 180;

    // radvd's and RFC 4861's defaults.
    private const int DefaultMaxInterval = 600;
    private const int DefaultValidLifetime = 86400;
    private const int DefaultPreferredLifetime = 14400;

    public static IEnumerable<HealthCheck> Check(
        RadvdConfig? radvd, IReadOnlyList<NetInterface>? interfaces, IReadOnlyList<DefaultRoute>? routes6)
    {
        if (radvd is null)
            yield break;

        var advertisesInternet = false;
        foreach (var config in radvd.Interfaces)
        {
            var nic = interfaces?.FirstOrDefault(i => i.Name == config.Name);
            var addresses = nic?.Addresses.Where(a => a.IsIpv6 && a.Scope == "global").ToList() ?? [];
            var maxInterval = Seconds(config.Option("MaxRtrAdvInterval")) ?? DefaultMaxInterval;
            var routerLifetime = Seconds(config.Option("AdvDefaultLifetime")) ?? Math.Min(3 * maxInterval, 9000);

            yield return Advertisements(config, nic, interfaces is not null, maxInterval);
            yield return Lifetimes(config, maxInterval, routerLifetime);
            if (interfaces is not null)
                yield return Prefixes(config, addresses);
            yield return Dns(config, interfaces);

            advertisesInternet |= routerLifetime > 0 && config.Prefixes.Any(p => IsPublic(p, addresses));
        }

        if (routes6 is not null && advertisesInternet)
        {
            yield return routes6.Count == 0
                ? new("IPv6 internet for clients", Problem,
                    "Clients get a public IPv6 address and are told to use this router for IPv6, but the router has no IPv6 default route.",
                    "Apps try IPv6 first and wait for it to time out before falling back to IPv4, which feels like lag or connections that hang. " +
                    "Fix the WAN's IPv6 (prefix delegation), or stop advertising the router for IPv6 (AdvDefaultLifetime 0).")
                : new("IPv6 internet for clients", Ok, "Clients are offered IPv6 and the router has an IPv6 default route.");
        }
    }

    private static HealthCheck Advertisements(RadvdInterface config, NetInterface? nic, bool knowInterfaces, int maxInterval)
    {
        var title = $"Router advertisements on {config.Name}";
        if (knowInterfaces && nic is null)
            return new(title, Problem, $"radvd is configured for {config.Name}, which doesn't exist.");
        if (nic is { OperState: "DOWN" or "LOWERLAYERDOWN" })
            return new(title, Problem, $"{config.Name} is down.");
        if (!IsOn(config.Option("AdvSendAdvert")))
            return new(title, Warning, "AdvSendAdvert is off, so radvd doesn't advertise on this network.",
                "Without router advertisements clients don't get IPv6 addresses. Set AdvSendAdvert on, or remove the interface if that's intended.");

        var minInterval = Seconds(config.Option("MinRtrAdvInterval")) ?? Math.Max(3, maxInterval / 3);
        var summary = $"Sent every {minInterval}–{maxInterval} s.";
        return maxInterval < 30
            ? new(title, Warning, summary,
                $"Unusually frequent: radvd's default is every 200–{DefaultMaxInterval} s. Each advertisement is a Wi-Fi multicast that wakes every phone, " +
                "costing battery and airtime, and short intervals also shorten the default lifetimes below what Android accepts. " +
                "Raise MaxRtrAdvInterval (e.g. 600) unless you need fast failover.")
            : new(title, Ok, summary);
    }

    private static HealthCheck Lifetimes(RadvdInterface config, int maxInterval, int routerLifetime)
    {
        var title = $"Advertised lifetimes on {config.Name}";
        var defaulted = config.Option("AdvDefaultLifetime") is null ? " (default: 3 × MaxRtrAdvInterval)" : "";
        var entries = new List<(string What, int Seconds)> { ($"Router lifetime{defaulted}", routerLifetime) };

        foreach (var prefix in config.Prefixes)
        {
            var name = prefix.Arguments.FirstOrDefault() ?? "prefix";
            entries.Add(($"{name} valid lifetime", Seconds(prefix.Option("AdvValidLifetime")) ?? DefaultValidLifetime));
            entries.Add(($"{name} preferred lifetime", Seconds(prefix.Option("AdvPreferredLifetime")) ?? DefaultPreferredLifetime));
        }
        foreach (var section in config.Rdnss.Concat(config.Dnssl))
        {
            var label = $"{section.Kind} {string.Join(" ", section.Arguments)}";
            var lifetime = Seconds(section.Option(section.Kind.Equals("RDNSS", StringComparison.OrdinalIgnoreCase) ? "AdvRDNSSLifetime" : "AdvDNSSLLifetime"));
            // radvd's default is 2–3 × MaxRtrAdvInterval depending on version; assume the longer one.
            entries.Add(lifetime is { } value ? (label + " lifetime", value) : ($"{label} lifetime (default: about 3 × MaxRtrAdvInterval)", 3 * maxInterval));
        }

        // 0 has a meaning of its own (e.g. "not a default router"), so only flag short non-zero lifetimes.
        var tooShort = entries.Where(e => e.Seconds is > 0 and < MinimumLifetime).ToList();
        if (tooShort.Count == 0)
            return new(title, Ok, $"All lifetimes are at least {MinimumLifetime} s.");

        return new(title, Warning,
            $"{tooShort.Count} lifetime{(tooShort.Count == 1 ? " is" : "s are")} shorter than {MinimumLifetime} s.",
            $"Android 14 and later ignore advertised information that lives less than {MinimumLifetime} s (to save battery), so phones can " +
            "miss the IPv6 route, addresses or DNS servers that laptops accept. Raise MaxRtrAdvInterval, or set the lifetimes " +
            "explicitly (e.g. AdvDefaultLifetime 1800; AdvRDNSSLifetime 1800).",
            tooShort.Select(e => $"{e.What}: {Format.Duration(e.Seconds)}").ToList());
    }

    private static HealthCheck Prefixes(RadvdInterface config, IReadOnlyList<NetAddress> addresses)
    {
        var title = $"Advertised prefixes on {config.Name}";
        var problems = new List<string>();
        var warnings = new List<string>();
        foreach (var section in config.Prefixes)
        {
            var text = section.Arguments.FirstOrDefault() ?? "";
            if (!IpPrefix.TryParse(text, out var prefix))
            {
                problems.Add($"{text} isn't a valid prefix.");
                continue;
            }
            if (IsAutomatic(prefix.Value))
                continue; // radvd advertises whatever /64s the interface has.

            var covering = addresses.Where(a => a.Covers(prefix.Value)).ToList();
            if (covering.Count == 0)
                problems.Add($"{text} is advertised, but {config.Name} has no address in it. Clients get addresses that replies can't be routed back to " +
                    "(often an old ISP prefix left in radvd.conf).");
            else if (covering.All(a => a.PreferredLifetime == TimeSpan.Zero))
                warnings.Add($"{text} is deprecated on the router (preferred lifetime 0): the ISP has probably moved you to a new prefix.");
        }

        var summary = string.Join(", ", config.Prefixes.Select(p => p.Arguments.FirstOrDefault()));
        if (problems.Count > 0)
            return new(title, Problem, summary, Details: [.. problems, .. warnings]);
        if (warnings.Count > 0)
            return new(title, Warning, summary, Details: warnings);
        return new(title, Ok, summary.Length > 0 ? summary : "No prefixes advertised.");
    }

    private static HealthCheck Dns(RadvdInterface config, IReadOnlyList<NetInterface>? interfaces)
    {
        var title = $"IPv6 DNS servers on {config.Name}";
        var servers = config.Rdnss.SelectMany(r => r.Arguments).ToList();
        if (servers.Count == 0)
            return new(title, Info, "No DNS servers advertised (RDNSS).",
                "Dual-stack clients still use the IPv4 DNS servers from DHCP, so this is fine unless you want IPv6-only clients.");

        // Compare parsed addresses, not text: radvd.conf may say fda9:9699:0faa:… where ip shows fda9:9699:faa:….
        var routerAddresses = interfaces?.SelectMany(i => i.Addresses)
            .Select(a => IPAddress.TryParse(a.Address, out var ip) ? ip : null).OfType<IPAddress>().ToHashSet();
        var foreign = routerAddresses is null ? [] : servers
            .Where(s => !IPAddress.TryParse(s, out var ip) || !routerAddresses.Contains(ip)).ToList();
        return foreign.Count == 0
            ? new(title, Ok, string.Join(", ", servers))
            : new(title, Info, string.Join(", ", servers),
                "Some advertised DNS servers aren't addresses of this router. That's fine if they're meant to be elsewhere; " +
                "otherwise IPv6 lookups go to an address nobody answers on.",
                foreign.Select(f => $"{f} is not on this router.").ToList());
    }

    /// <summary>A prefix that gives clients internet IPv6: anything but ULA. <c>::/64</c> counts if the interface has a public /64.</summary>
    private static bool IsPublic(RadvdSection prefix, IReadOnlyList<NetAddress> addresses)
    {
        if (!IpPrefix.TryParse(prefix.Arguments.FirstOrDefault() ?? "", out var parsed))
            return false;
        if (IsAutomatic(parsed.Value))
            return addresses.Any(a => a.PrefixLength == parsed.Value.Length &&
                IpPrefix.TryParse($"{a.Address}/{a.PrefixLength}", out var own) && !own.Value.IsUniqueLocal);
        return !parsed.Value.IsUniqueLocal;
    }

    private static bool IsAutomatic(IpPrefix prefix) => prefix.Network.Equals(IPAddress.IPv6Any);

    private static bool IsOn(string? value) => value?.ToLowerInvariant() is "on" or "yes" or "1" or "true";

    /// <summary>A lifetime or interval in seconds; "infinity" counts as long enough.</summary>
    private static int? Seconds(string? value) =>
        value?.ToLowerInvariant() == "infinity" ? int.MaxValue
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds
        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fractional) ? (int)fractional
        : null;
}
