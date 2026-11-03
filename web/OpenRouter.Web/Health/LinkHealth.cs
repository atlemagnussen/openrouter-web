using OpenRouter.Web.Kernel;
using OpenRouter.Web.Network;
using OpenRouter.Web.Pages;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>Physical links: up, speed, duplex, errors, queue drops and flapping. Counters are since boot.</summary>
public static class LinkHealth
{
    public sealed record Port(string Name, string Role);

    /// <summary>Link drops in this window are a warning; older ones are only mentioned.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    /// <param name="history">When links went down, from <see cref="LinkMonitor"/>; null if not recorded.</param>
    public static IEnumerable<HealthCheck> Check(
        IReadOnlyList<Port> ports, IReadOnlyList<LinkStats>? links, IReadOnlyList<NicInfo>? nics,
        IReadOnlyDictionary<string, LinkHistory>? history = null, DateTimeOffset? now = null)
    {
        foreach (var port in ports)
        {
            var title = $"{port.Role} link ({port.Name})";
            var link = links?.FirstOrDefault(l => l.Name == port.Name);
            if (links is not null && link is null)
            {
                yield return new(title, Problem, $"Interface {port.Name} doesn't exist.",
                    "The router's config uses an interface name that isn't there. Check the .link files that pin interface names.");
                continue;
            }
            var nic = nics?.FirstOrDefault(n => n.Name == port.Name);
            yield return CheckPort(title, link, nic, history?.GetValueOrDefault(port.Name), now);
        }
    }

    private static HealthCheck CheckPort(string title, LinkStats? link, NicInfo? nic, LinkHistory? history, DateTimeOffset? now)
    {
        var issues = new List<(HealthStatus Status, string Text)>();
        var facts = new List<string>();

        if (link is not null)
        {
            facts.Add(link.OperState);
            if (link.OperState is "DOWN" or "LOWERLAYERDOWN")
                issues.Add((Problem, "The link is down: cable unplugged, or the device at the other end is off."));
        }

        if (nic?.SpeedMbps is { } speed)
        {
            facts.Add($"{(speed >= 1000 ? $"{speed / 1000.0:0.#} Gbit/s" : $"{speed} Mbit/s")} {nic.Duplex} duplex");
            if (speed < 1000)
                issues.Add((Warning, $"Negotiated only {speed} Mbit/s. Usually a damaged cable (one pair broken) or a 100 Mbit port at the other end."));
        }
        if (nic?.Duplex == "half")
            issues.Add((Problem, "Half duplex: collisions and very slow transfers. Usually one side has a forced speed setting."));

        if (link is not null)
        {
            facts.Add($"{Format.Bytes(link.Rx.Bytes)} in, {Format.Bytes(link.Tx.Bytes)} out");
            var packets = link.Rx.Packets + link.Tx.Packets;
            var errors = link.Rx.Errors + link.Tx.Errors;
            if (errors > 0)
            {
                var text = $"{errors:N0} transmission errors since boot ({Percent(errors, packets)} of packets).";
                issues.Add(errors >= packets / 10_000
                    ? (Warning, text + " Errors on Ethernet mean a bad cable, port or network card.")
                    : (Info, text));
            }
            if (link.Tx.Dropped > 0 && link.Tx.Dropped >= link.Tx.Packets / 1000)
                issues.Add((Warning, $"{link.Tx.Dropped:N0} outgoing packets dropped ({Percent(link.Tx.Dropped, link.Tx.Packets)}): " +
                    "the send queue overflowed, so the link was full at times."));
            if (link.Rx.Dropped > 0 && link.Rx.Dropped >= link.Rx.Packets / 100)
                issues.Add((Info, $"{link.Rx.Dropped:N0} incoming packets dropped ({Percent(link.Rx.Dropped, link.Rx.Packets)}). " +
                    "Often harmless (protocols the router doesn't use, such as mesh or LLDP frames), but a full receive ring also counts here."));
        }

        if (Drops(nic, history, now) is { } drops)
        {
            if (drops.Status is { } status)
                issues.Add((status, drops.Text));
            else
                facts.Add(drops.Text);
        }

        var summary = string.Join(" · ", facts);
        return issues.Count == 0
            ? new(title, Ok, summary)
            : new(title, issues.Max(i => i.Status), summary, Details: issues.Select(i => i.Text).ToList());
    }

    private const string DropAdvice =
        "Check the cable, and whether the device at the other end (modem, Orbi, Nest) reboots or goes into power saving.";

    /// <summary>
    /// What to say about link drops: a warning if one happened in the last 24 h, a plain fact if the last one
    /// is older, a note if they happened before monitoring started and it's been watching less than 24 h.
    /// </summary>
    private static (HealthStatus? Status, string Text)? Drops(NicInfo? nic, LinkHistory? history, DateTimeOffset? now)
    {
        // The first link-up after boot accounts for one or two changes; each drop after that adds two (down, up).
        static long SinceBoot(long changes) => changes >= 3 ? (changes - 1) / 2 : 0;
        static string Times(long n) => n == 1 ? "once" : $"{n} times";

        if (history is null || now is null)
        {
            // Not monitored: all we have is the count since boot, with no idea when.
            return nic?.CarrierChanges is { } changes && SinceBoot(changes) > 0
                ? (Info, $"The link has gone down and up {Times(SinceBoot(changes))} since boot (when isn't known). {DropAdvice}")
                : null;
        }

        if (history.RecentChanges > 0)
        {
            var drops = (history.RecentChanges + 1) / 2;
            return (Warning, $"The link went down {Times(drops)} in the last {RecentWindow.TotalHours:0} h, " +
                $"last {Format.Duration(now.Value - history.LastChange!.Value)} ago. {DropAdvice}");
        }
        if (history.LastChange is { } last)
            return (null, $"last link drop {Format.Duration(now.Value - last)} ago");

        var before = SinceBoot(history.CarrierChangesAtStart);
        if (before == 0)
            return null;
        var watching = now.Value - history.MonitoredSince;
        return watching < RecentWindow
            ? (Info, $"The link went down {Times(before)} after boot, before monitoring started {Format.Duration(watching)} ago, " +
                "so when isn't known. This turns OK after 24 h without a new drop.")
            : (null, $"no link drops in the last {Format.Duration(watching)}");
    }

    private static string Percent(long part, long whole) =>
        whole == 0 ? "—"
        : 100.0 * part / whole is var percent && percent < 0.001 ? "under 0.001%"
        : $"{percent:0.###}%";
}
