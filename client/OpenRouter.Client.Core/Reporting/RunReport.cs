using System.Text;
using OpenRouter.Client.Storage;

namespace OpenRouter.Client.Reporting;

/// <summary>Turns a run's samples into a readable summary, with hints about where the problem is.</summary>
public static class RunReport
{
    public static string Build(RunData data, TimeZoneInfo zone, Probes.Vendors? vendors = null)
    {
        var run = data.Run;
        var report = new StringBuilder();
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm:ss");
        string Clock(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm:ss");

        var end = run.Ended ?? data.Pings.LastOrDefault()?.Time ?? run.Started;
        report.AppendLine($"Run {run.Id} on {run.Host}, {run.Interface}{(run.Mac is null ? "" : $" ({run.Mac})")}, IPv4 {run.Ipv4 ?? "—"}, gateway {run.Gateway ?? "—"}");
        report.AppendLine($"{Local(run.Started)} – {Local(end)} ({Format.Span(end - run.Started)}){(run.Ended is null ? ", unfinished" : "")}" +
            (run.Note is null ? "" : $" · {run.Note}"));
        report.AppendLine();

        // Pings
        var pingGroups = data.Pings.GroupBy(p => (p.Label, p.Target)).OrderBy(g => LabelOrder(g.Key.Label)).ThenBy(g => g.Key.Target).ToList();
        var pingOutages = new Dictionary<(string, string), IReadOnlyList<Outage>>();
        if (pingGroups.Count > 0)
        {
            var width = Math.Max(30, pingGroups.Max(g => g.Key.Label.Length + g.Key.Target.Length + 1));
            report.AppendLine($"{"Ping".PadRight(width + 2)}  sent    lost    loss     avg     p50     p95     max  jitter  outages");
            foreach (var group in pingGroups)
            {
                var stats = Stats.Of(group.Select(p => p.Milliseconds));
                var outages = Outage.Find(group.Select(p => (p.Time, p.Milliseconds is null)));
                pingOutages[group.Key] = outages;
                var longest = outages.MaxBy(o => o.Duration);
                report.AppendLine($"  {$"{group.Key.Label} {group.Key.Target}".PadRight(width)} {stats.Count,6} {stats.Lost,7} {Format.Percent(stats.LossPercent),7} " +
                    $"{Format.Ms(stats.Average),7} {Format.Ms(stats.Median),7} {Format.Ms(stats.P95),7} {Format.Ms(stats.Max),7} {Format.Ms(stats.Jitter),7}  " +
                    (outages.Count == 0 ? "none" : $"{outages.Count}, longest {Format.Span(longest!.Duration)} at {Clock(longest.Start)}"));
            }
            report.AppendLine();
        }

        // DNS
        var dnsGroups = data.Dns.GroupBy(d => d.Server).ToList();
        if (dnsGroups.Count > 0)
        {
            report.AppendLine("DNS                               sent  failed     avg     p95     max   >500ms  last failure");
            foreach (var group in dnsGroups)
            {
                var stats = Stats.Of(group.Select(d => d.ResponseCode == 0 ? d.Milliseconds : null));
                var slow = group.Count(d => d.Milliseconds > 500);
                var failure = group.LastOrDefault(d => d.ResponseCode != 0);
                report.AppendLine($"  {group.Key,-30} {stats.Count,6} {stats.Lost,7} {Format.Ms(stats.Average),7} {Format.Ms(stats.P95),7} {Format.Ms(stats.Max),7} {slow,8}  " +
                    (failure is null ? "" : $"{Clock(failure.Time)} {failure.Name}: {failure.Error}"));
            }
            report.AppendLine();
        }

        // HTTPS
        var httpGroups = data.Http.GroupBy(h => h.Url).ToList();
        if (httpGroups.Count > 0)
        {
            report.AppendLine("HTTPS (new connection each time)  sent  failed  lookup connect     TLS   first  total p95  last failure");
            foreach (var group in httpGroups)
            {
                var ok = group.Where(h => h.Error is null).ToList();
                var total = Stats.Of(group.Select(h => h.TotalMs));
                double? Avg(Func<HttpSample, double?> step) => ok.Select(step).OfType<double>().DefaultIfEmpty().Average() is var a && ok.Count > 0 ? a : null;
                var failure = group.LastOrDefault(h => h.Error is not null);
                report.AppendLine($"  {Shorten(group.Key, 30),-30} {total.Count,6} {total.Lost,7} {Format.Ms(Avg(h => h.DnsMs)),7} {Format.Ms(Avg(h => h.ConnectMs)),7} " +
                    $"{Format.Ms(Avg(h => h.TlsMs)),7} {Format.Ms(Avg(h => h.FirstByteMs)),7} {Format.Ms(total.P95),10}  " +
                    (failure is null ? "" : $"{Clock(failure.Time)} {failure.Error}"));
            }
            report.AppendLine();
        }

        // Wi-Fi
        var wifi = data.Wifi.Where(w => w.Connected && w.SignalDbm is not null).ToList();
        var roams = data.Events.Count(e => e.Kind == "roam");
        if (data.Wifi.Count > 0)
        {
            report.AppendLine("Wi-Fi");
            if (wifi.Count > 0)
            {
                var signals = wifi.Select(w => (double)w.SignalDbm!.Value).ToList();
                report.AppendLine($"  Signal       avg {signals.Average():0} dBm, weakest {signals.Min():0} dBm, strongest {signals.Max():0} dBm " +
                    $"({SignalWord(signals.Average())})");
                var bands = wifi.GroupBy(w => w.Band ?? "?").Select(g => $"{g.Key} {Format.Percent(100.0 * g.Count() / wifi.Count)}");
                report.AppendLine($"  Band         {string.Join(", ", bands)} of the time");
                foreach (var ap in wifi.GroupBy(w => w.Bssid).OrderByDescending(g => g.Count()))
                {
                    var first = ap.First();
                    report.AppendLine($"  Access point {ap.Key} {first.Band} channel {first.Channel} ({first.Ssid}): " +
                        $"{Format.Percent(100.0 * ap.Count() / wifi.Count)} of the time, avg {ap.Average(w => w.SignalDbm!.Value):0} dBm");
                }
                var bitrates = wifi.Where(w => w.TxBitrateMbps is not null).Select(w => w.TxBitrateMbps!.Value).ToList();
                if (bitrates.Count > 0)
                    report.AppendLine($"  Link rate    send avg {bitrates.Average():0} Mbit/s, lowest {bitrates.Min():0} Mbit/s");
                if (TxFailure(wifi) is { } failed)
                    report.AppendLine($"  Sending      {failed.Failed:N0} of {failed.Packets:N0} packets failed ({Format.Percent(failed.Percent)})");
            }
            var disconnects = data.Events.Count(e => e.Kind == "wifi-disconnected");
            report.AppendLine($"  Roams        {roams}, disconnects {disconnects}");
            report.AppendLine();
        }

        // Networks around: what shares the channel this machine used most.
        if (data.Scans.Count > 0 && HomeChannel(data) is { } home)
        {
            var heard = Neighbourhood.Summarise(data.Scans);
            var scans = data.Scans.Select(s => s.Time).Distinct().Count();
            var onChannel = heard.Where(n => n.Band == home.Band && n.Channel == home.Channel).ToList();
            var makers = vendors?.LookupAll(heard.Select(n => n.Bssid));
            report.AppendLine($"Networks on {home.Band} channel {home.Channel} ({scans} scans, {heard.Count} networks heard in all)");
            foreach (var n in onChannel.Take(12))
                report.AppendLine($"  {(n.Ssid ?? "(hidden)"),-24} {n.Bssid}  {(n.MedianDbm is { } dbm ? $"{dbm} dBm" : "—"),8}  " +
                    $"{makers?[n.Bssid] ?? ""}{(n.Associated ? "  (connected)" : "")}");
            report.AppendLine();
        }

        // Events
        if (data.Events.Count > 0)
        {
            report.AppendLine($"Events ({data.Events.Count})");
            const int shown = 40;
            foreach (var e in data.Events.Take(shown))
                report.AppendLine($"  {Clock(e.Time)}  {e.Kind,-18} {e.Detail}");
            if (data.Events.Count > shown)
                report.AppendLine($"  … and {data.Events.Count - shown} more (see the events table).");
            report.AppendLine();
        }

        var hints = Hints(data, pingOutages);
        report.AppendLine("What it suggests");
        foreach (var hint in hints.DefaultIfEmpty("Nothing stands out: no notable loss, slow DNS or weak Wi-Fi in this run."))
            report.AppendLine($"  - {hint}");
        return report.ToString();
    }

    /// <summary>The channel this machine was connected on most of the run.</summary>
    private static (double FrequencyMhz, string Band, int Channel, string? Ssid)? HomeChannel(RunData data) =>
        data.Wifi.Where(w => w.Connected && w.FrequencyMhz is not null).GroupBy(w => w.FrequencyMhz!.Value).MaxBy(g => g.Count()) is { } most
            ? (most.Key, WifiBand.Name(most.Key)!, WifiBand.Channel(most.Key)!.Value, most.First().Ssid)
            : null;

    /// <summary>Rules of thumb that point at where a problem sits: Wi-Fi, the router, DNS or the ISP.</summary>
    public static IReadOnlyList<string> Hints(RunData data, IReadOnlyDictionary<(string Label, string Target), IReadOnlyList<Outage>> outages)
    {
        var hints = new List<string>();
        Stats? Ping(string label) =>
            data.Pings.Where(p => p.Label == label).ToList() is { Count: > 0 } list ? Stats.Of(list.Select(p => p.Milliseconds)) : null;

        var gateway = Ping("gateway");
        var internet = Ping("internet");
        if (gateway is { LossPercent: >= 1 })
            hints.Add($"{Format.Percent(gateway.LossPercent)} of pings to the router itself were lost. The problem is between this laptop and the router " +
                "(Wi-Fi or access point), not the internet.");
        else if (internet is { LossPercent: >= 1 } && gateway is { LossPercent: < 0.5 })
            hints.Add($"The router answered fine but {Format.Percent(internet.LossPercent)} of internet pings were lost: look at the WAN side " +
                "(ISP, modem, the router's WAN link).");

        if (gateway is { P95: > 30 })
            hints.Add($"Round trips to the router are often slow (95th percentile {Format.Ms(gateway.P95)}, a good Wi-Fi link stays under 10 ms). " +
                "That's Wi-Fi congestion, interference or power saving.");
        if (internet is { Jitter: > 30 } && gateway is { Jitter: < 10 })
            hints.Add($"Internet latency varies a lot (jitter {Format.Ms(internet.Jitter)}) while the router is steady: typical of a busy line (bufferbloat).");

        var gatewayDns = data.Run.Gateway;
        var dnsByServer = data.Dns.GroupBy(d => d.Server).ToDictionary(g => g.Key, g => Stats.Of(g.Select(d => d.ResponseCode == 0 ? d.Milliseconds : null)));
        if (gatewayDns is not null && dnsByServer.TryGetValue(gatewayDns, out var routerDns))
        {
            var others = dnsByServer.Where(d => d.Key != gatewayDns).Select(d => d.Value).ToList();
            if (routerDns.LossPercent >= 1)
                hints.Add($"{Format.Percent(routerDns.LossPercent)} of lookups through the router's DNS failed or timed out" +
                    (others.All(o => o.LossPercent < 0.5) ? ", while public DNS answered: look at bind on the router (DNS page)." : "."));
            else if (routerDns.P95 > 200 && others.Any(o => o.P95 < routerDns.P95 / 2))
                hints.Add($"The router's DNS is slow at times (95th percentile {Format.Ms(routerDns.P95)}) compared with public DNS.");
        }

        var wifi = data.Wifi.Where(w => w.Connected && w.SignalDbm is not null).ToList();
        if (wifi.Count > 0)
        {
            var weak = wifi.Count(w => w.SignalDbm < -75);
            // A few weak readings in hours are a glitch or a brief dip, not a weak spot.
            if (weak > 0 && weak >= wifi.Count * 0.02)
                hints.Add($"The signal was below -75 dBm {Format.Percent(100.0 * weak / wifi.Count)} of the time (weakest {wifi.Min(w => w.SignalDbm)} dBm): " +
                    "too far from the access point, or a wall in the way.");
            var slowBand = wifi.Count(w => w.Band == "2.4 GHz");
            if (slowBand > wifi.Count / 2)
                hints.Add($"Connected on 2.4 GHz {Format.Percent(100.0 * slowBand / wifi.Count)} of the time. It's slower and more crowded than 5 GHz; " +
                    "if 5 GHz reaches this spot, consider a separate 5 GHz network name or band steering on the access point.");
            // The lowest rates with a good signal: the radio can't get frames through for reasons other than distance.
            var collapsed = wifi.Where(w => w.TxBitrateMbps <= 13 && w.SignalDbm >= -65).ToList();
            if (collapsed.Count > 0 && collapsed.Count >= wifi.Count * 0.01)
            {
                var gatewayLoss = data.Pings.Where(p => p.Label == "gateway" && p.Milliseconds is null).ToList();
                var lostThen = gatewayLoss.Count(p => collapsed.Any(w => Math.Abs((p.Time - w.Time).TotalSeconds) <= 5));
                hints.Add($"The link rate fell to {collapsed.Min(w => w.TxBitrateMbps):0.#} Mbit/s or close to it " +
                    $"{Format.Percent(100.0 * collapsed.Count / wifi.Count)} of the time while the signal was still good (−65 dBm or better)" +
                    (gatewayLoss.Count > 0 ? $", and {Format.Percent(100.0 * lostThen / gatewayLoss.Count)} of the lost router pings fell in those moments" : "") +
                    ": interference or a busy channel, not distance. Try another channel on the access point.");
            }
            if (TxFailure(wifi) is { Percent: > 2 } failed)
                hints.Add($"{Format.Percent(failed.Percent)} of sent packets failed at the Wi-Fi level: interference or a weak link.");
        }

        // Other loud networks on the same channel take turns with yours for airtime.
        if (data.Scans.Count > 0 && HomeChannel(data) is { } home)
        {
            var others = Neighbourhood.SharingChannel(Neighbourhood.Summarise(data.Scans), home.FrequencyMhz, home.Ssid);
            if (others.Count > 0)
                hints.Add($"{others.Count} other network{(others.Count == 1 ? "" : "s")} at −70 dBm or stronger share {home.Band} channel {home.Channel} " +
                    $"({string.Join(", ", others.Take(5).Select(n => $"{n.Ssid ?? "hidden"} {n.MedianDbm} dBm"))}). They take turns with yours " +
                    "for airtime; if one of them is yours, move it to another channel.");
        }

        // Outages that start just after a roam point at roaming. One that starts before the roam is the laptop
        // leaving a link that had already failed, which says nothing against roaming. Router outages count when
        // there are any: every target drops at once when the local link fails, so counting them all repeats one outage.
        var roamTimes = data.Events.Where(e => e.Kind is "roam" or "wifi-disconnected").Select(e => e.Time).ToList();
        var routerOutages = outages.Where(o => o.Key.Label.StartsWith("gateway")).SelectMany(o => o.Value).ToList();
        var counted = routerOutages.Count > 0 ? routerOutages : outages.SelectMany(o => o.Value).ToList();
        var afterRoam = counted.Count(o => roamTimes.Any(t => (o.Start - t).TotalSeconds is > -3 and < 15));
        var roamCount = data.Events.Count(e => e.Kind == "roam");
        if (afterRoam > 0)
            hints.Add($"{afterRoam} of {counted.Count} {(routerOutages.Count > 0 ? "router " : "")}outages started within 15 s after switching access point " +
                "or losing Wi-Fi: roaming between the mesh nodes is a likely cause.");
        else if (roamCount > 10)
            hints.Add($"Switched access point {roamCount} times: the laptop sits between two nodes and keeps changing.");
        return hints;
    }

    private static (long Failed, long Packets, double Percent)? TxFailure(IReadOnlyList<WifiSample> wifi)
    {
        // Counters restart on every association, so add up the increase per access point stretch.
        long failed = 0, packets = 0;
        foreach (var pair in wifi.Zip(wifi.Skip(1)))
        {
            if (pair.First.Bssid != pair.Second.Bssid)
                continue;
            if (pair.Second.TxFailed - pair.First.TxFailed is { } f and >= 0)
                failed += f;
            if (pair.Second.TxPackets - pair.First.TxPackets is { } p and >= 0)
                packets += p;
        }
        return packets > 0 ? (failed, packets, 100.0 * failed / packets) : null;
    }

    /// <summary>Gateway first, then the internet targets.</summary>
    public static int LabelOrder(string label) => label switch
    {
        "gateway" => 0,
        "gateway-v6" => 1,
        "internet" => 2,
        "internet-v6" => 3,
        _ => 4,
    };

    private static string SignalWord(double dbm) => dbm switch
    {
        >= -55 => "excellent",
        >= -65 => "good",
        >= -72 => "fair",
        >= -80 => "weak",
        _ => "very weak",
    };

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
