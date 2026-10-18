using System.Text.RegularExpressions;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>
/// A quick look through nftables.conf for rules whose absence is a classic cause of stalls.
/// It searches the text rather than parsing nft, so it can be fooled by unusual rulesets.
/// </summary>
public static partial class FirewallHealth
{
    public static IEnumerable<HealthCheck> Check(string? ruleset, int? wanMtu)
    {
        if (ruleset is null)
            yield break;

        var rules = CommentPattern().Replace(ruleset, "");
        var dropsByDefault = PolicyDropPattern().IsMatch(rules);

        if (dropsByDefault && !(rules.Contains("icmpv6") || rules.Contains("ipv6-icmp") || rules.Contains("nd-neighbor")))
            yield return new("ICMPv6", Problem, "The ruleset drops traffic by default and never mentions ICMPv6.",
                "IPv6 can't work without ICMPv6: neighbour discovery, router solicitations and \"packet too big\" all use it. " +
                "Blocking it gives stalls that come and go. Accept at least nd-neighbor-solicit/advert, nd-router-solicit, " +
                "packet-too-big, time-exceeded, parameter-problem and echo-request.");
        else
            yield return new("ICMPv6", Ok, dropsByDefault ? "Rules for ICMPv6 are present." : "No default-drop policy.");

        if (dropsByDefault && !rules.Contains("related"))
            yield return new("Related traffic", Warning, "No rule accepts \"related\" packets.",
                "ICMP errors such as \"fragmentation needed\" belong to existing connections as \"related\" traffic. " +
                "Without ct state established,related accept, large transfers can hang.");

        if (wanMtu is { } mtu)
        {
            if (mtu >= 1500)
                yield return new("WAN MTU", Ok, $"WAN MTU is {mtu}; no MSS clamping needed.");
            else if (rules.Contains("maxseg"))
                yield return new("WAN MTU", Ok, $"WAN MTU is {mtu} and TCP MSS is clamped.");
            else
                yield return new("WAN MTU", Warning, $"WAN MTU is {mtu}, but TCP MSS isn't clamped.",
                    "Clients assume 1500 bytes; without clamping some sites and apps hang while loading. Add " +
                    "\"tcp flags syn tcp option maxseg size set rt mtu\" to the forward chain.");
        }
    }

    [GeneratedRegex(@"#[^\n]*")]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"\bpolicy\s+drop\b")]
    private static partial Regex PolicyDropPattern();
}
