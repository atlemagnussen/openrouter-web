using System.Net;
using System.Threading.Channels;
using OpenRouter.Client.Probes;
using OpenRouter.Client.Reporting;
using OpenRouter.Client.Storage;

namespace OpenRouter.Client;

/// <summary>
/// Runs all probes side by side for the run's duration, writes every sample to the store, notes events
/// (roaming, outages, address changes) and prints a status line now and then.
/// </summary>
public sealed class NetMonitor(RunOptions options, ResultStore store, long runId, NetworkState network, TimeProvider time, TextWriter output)
{
    /// <summary>Consecutive lost pings before it counts as an outage (and an event).</summary>
    private const int OutageAfter = 3;

    private readonly Channel<object> _samples = Channel.CreateUnbounded<object>();
    private readonly Lock _lock = new();
    private readonly List<PingSample> _recentPings = [];
    private readonly List<DnsSample> _recentDns = [];
    private readonly List<HttpSample> _recentHttp = [];
    private WifiSample? _wifi;
    private bool _wifiFailed;
    private NetworkState _network = network;
    private bool _networkDown;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var targets = PingTargets(_network);
        var dnsServers = DnsServers(_network);
        output.WriteLine($"Pinging {string.Join(", ", targets.Select(t => $"{t.Label} {t.Address}"))} every {Format.Span(options.PingInterval)}.");
        output.WriteLine($"DNS via {string.Join(", ", dnsServers)} every {Format.Span(options.DnsInterval)}; HTTPS to {string.Join(", ", options.HttpUrls)} every {Format.Span(options.HttpInterval)}.");
        var wireless = WifiProbe.IsWireless(_network.Interface);
        output.WriteLine(wireless
            ? $"Watching Wi-Fi on {_network.Interface} every {Format.Span(options.WifiInterval)}."
            : $"{_network.Interface} is not a Wi-Fi interface (or this isn't Linux); no signal readings.");
        output.WriteLine();

        var writer = WriteLoopAsync(CancellationToken.None);
        var loops = new List<Task>
        {
            Every(options.StatusInterval, PrintStatus, cancellationToken, runFirst: false),
            Every(TimeSpan.FromSeconds(5), CheckNetworkAsync, cancellationToken),
            Every(options.DnsInterval, ct => DnsRoundAsync(dnsServers, ct), cancellationToken),
            Every(options.HttpInterval, HttpRoundAsync, cancellationToken),
        };
        loops.AddRange(targets.Select(t => PingLoopAsync(t.Label, t.Address, cancellationToken)));
        if (wireless)
            loops.Add(Every(options.WifiInterval, ct => WifiAsync(new WifiProbe(_network.Interface), ct), cancellationToken));

        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        _samples.Writer.Complete();
        await writer;
    }

    private List<(string Label, IPAddress Address)> PingTargets(NetworkState state)
    {
        var targets = new List<(string, IPAddress)>();
        if (state.Gateway4 is { } gateway4)
            targets.Add(("gateway", gateway4));
        if (state.Gateway6 is { } gateway6)
            targets.Add(("gateway-v6", gateway6));
        foreach (var target in options.PingTargets)
        {
            if (!IPAddress.TryParse(target, out var address))
            {
                output.WriteLine($"Skipping ping target {target}: not an IP address.");
                continue;
            }
            // Without a global IPv6 address an IPv6 target would just look like 100% loss.
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !state.HasGlobalIpv6)
            {
                output.WriteLine($"Skipping {target}: {state.Interface} has no global IPv6 address.");
                continue;
            }
            targets.Add((address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "internet-v6" : "internet", address));
        }
        return targets;
    }

    private List<string> DnsServers(NetworkState state)
    {
        var servers = new List<string>();
        if (!options.NoGatewayDns && state.Gateway4 is { } gateway)
            servers.Add(gateway.ToString());
        servers.AddRange(options.DnsServers.Where(s => IPAddress.TryParse(s, out _) && !servers.Contains(s)));
        return servers;
    }

    private async Task PingLoopAsync(string label, IPAddress address, CancellationToken cancellationToken)
    {
        var lost = 0;
        DateTimeOffset? outageStart = null;
        using var timer = new PeriodicTimer(options.PingInterval, time);
        do
        {
            var at = time.GetUtcNow();
            // Don't wait for the reply before the next ping: a slow reply mustn't slow the sampling down.
            _ = Task.Run(async () =>
            {
                double? ms;
                try
                {
                    ms = await PingProbe.PingAsync(address, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return; // the run is ending
                }
                var sample = new PingSample(at, address.ToString(), label, ms);
                Publish(sample);
                lock (_lock)
                {
                    _recentPings.Add(sample);
                    if (ms is null)
                    {
                        lost++;
                        if (lost == OutageAfter)
                        {
                            outageStart = at - options.PingInterval * (OutageAfter - 1);
                            Event("outage-start", $"{label} {address} stopped answering");
                        }
                    }
                    else
                    {
                        if (outageStart is { } start)
                            Event("outage-end", $"{label} {address} answered again after {Format.Span(at - start)} ({lost} lost)");
                        lost = 0;
                        outageStart = null;
                    }
                }
            }, CancellationToken.None);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task DnsRoundAsync(IReadOnlyList<string> servers, CancellationToken cancellationToken)
    {
        var name = options.DnsNames[(int)(time.GetUtcNow().ToUnixTimeSeconds() / (long)options.DnsInterval.TotalSeconds % options.DnsNames.Count)];
        await Task.WhenAll(servers.Select(async server =>
        {
            var at = time.GetUtcNow();
            var (ms, code, error) = await DnsProbe.QueryAsync(IPAddress.Parse(server), name, cancellationToken);
            var sample = new DnsSample(at, server, name, ms, code, error);
            Publish(sample);
            lock (_lock)
                _recentDns.Add(sample);
        }));
    }

    private async Task HttpRoundAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(options.HttpUrls.Select(async url =>
        {
            var sample = await HttpProbe.GetAsync(new Uri(url), time.GetUtcNow(), cancellationToken);
            Publish(sample);
            lock (_lock)
                _recentHttp.Add(sample);
        }));
    }

    private async Task WifiAsync(WifiProbe probe, CancellationToken cancellationToken)
    {
        if (_wifiFailed)
            return;
        WifiSample sample;
        try
        {
            sample = await probe.ReadAsync(time.GetUtcNow(), cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.WriteLine($"Can't read Wi-Fi ({ex.Message}); is the iw package installed? Carrying on without it.");
            _wifiFailed = true;
            return;
        }
        Publish(sample);

        var previous = _wifi;
        _wifi = sample;
        if (previous is null)
            return;
        if (previous.Connected && !sample.Connected)
            Event("wifi-disconnected", $"lost {previous.Ssid} ({previous.Bssid})");
        else if (!previous.Connected && sample.Connected)
            Event("wifi-connected", $"{sample.Ssid} via {sample.Bssid}, {sample.Band} channel {sample.Channel}, {sample.SignalDbm} dBm");
        else if (sample.Connected && previous.Bssid != sample.Bssid)
            Event("roam", $"{previous.Bssid} ({previous.Band}, {previous.SignalDbm} dBm) → {sample.Bssid} ({sample.Band} channel {sample.Channel}, {sample.SignalDbm} dBm)");
        else if (sample.Connected && previous.Band != sample.Band)
            Event("band", $"{previous.Band} → {sample.Band} on {sample.Bssid}");
    }

    private Task CheckNetworkAsync(CancellationToken cancellationToken)
    {
        var current = NetworkState.Read(options.Interface ?? _network.Interface);
        var previous = _network;
        if (current is null)
        {
            if (!_networkDown)
                Event("network-down", $"{previous.Interface} has no default gateway");
            _networkDown = true;
            return Task.CompletedTask;
        }
        if (_networkDown)
            Event("network-up", $"{current.Interface} has a default gateway again ({current.Gateway4})");
        _networkDown = false;
        if (!current.Ipv4.SequenceEqual(previous.Ipv4))
            Event("address", $"IPv4 {string.Join(", ", previous.Ipv4)} → {string.Join(", ", current.Ipv4)}");
        if (!Equals(current.Gateway4, previous.Gateway4))
            Event("gateway", $"{previous.Gateway4} → {current.Gateway4}");
        if (!current.Ipv6Global.Order().SequenceEqual(previous.Ipv6Global.Order()))
            Event("address-v6", $"{previous.Ipv6Global.Count} → {current.Ipv6Global.Count} global IPv6 addresses");
        _network = current;
        return Task.CompletedTask;
    }

    private void PrintStatus(CancellationToken cancellationToken)
    {
        List<PingSample> pings;
        List<DnsSample> dns;
        List<HttpSample> http;
        lock (_lock)
        {
            pings = [.. _recentPings];
            dns = [.. _recentDns];
            http = [.. _recentHttp];
            _recentPings.Clear();
            _recentDns.Clear();
            _recentHttp.Clear();
        }

        var parts = new List<string> { time.GetLocalNow().ToString("HH:mm:ss") };
        foreach (var group in pings.GroupBy(p => (p.Label, p.Target)).OrderBy(g => RunReport.LabelOrder(g.Key.Label)).ThenBy(g => g.Key.Target))
        {
            var stats = Stats.Of(group.Select(p => p.Milliseconds));
            parts.Add($"{(group.Key.Label.StartsWith("internet") ? group.Key.Target : group.Key.Label)} {Format.Ms(stats.Average)}{(stats.Lost > 0 ? $" LOST {stats.Lost}/{stats.Count}" : "")}");
        }
        if (dns.Count > 0)
        {
            var stats = Stats.Of(dns.Select(d => d.ResponseCode == 0 ? d.Milliseconds : null));
            parts.Add($"dns {Format.Ms(stats.Average)}{(stats.Lost > 0 ? $" FAIL {stats.Lost}" : "")}");
        }
        if (http.Count > 0)
        {
            var stats = Stats.Of(http.Select(h => h.TotalMs));
            parts.Add($"https {Format.Ms(stats.Average)}{(stats.Lost > 0 ? $" FAIL {stats.Lost}" : "")}");
        }
        if (_wifi is { } wifi)
            parts.Add(wifi.Connected ? $"wifi {wifi.SignalDbm} dBm {wifi.Band} ch{wifi.Channel} {wifi.Bssid}" : "wifi DISCONNECTED");
        output.WriteLine(string.Join(" | ", parts));
    }

    private void Event(string kind, string detail)
    {
        var e = new NetEvent(time.GetUtcNow(), kind, detail);
        Publish(e);
        output.WriteLine($"{time.GetLocalNow():HH:mm:ss} ** {kind}: {detail}");
    }

    private void Publish(object sample) => _samples.Writer.TryWrite(sample);

    /// <summary>Writes samples in batches every couple of seconds, so a long run doesn't hold everything in memory.</summary>
    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var batch = new List<object>();
        while (await _samples.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_samples.Reader.TryRead(out var sample))
                batch.Add(sample);
            await store.WriteAsync(runId, batch, cancellationToken);
            batch.Clear();
            await Task.Delay(TimeSpan.FromSeconds(2), time, CancellationToken.None);
        }
    }

    private async Task Every(TimeSpan interval, Func<CancellationToken, Task> action, CancellationToken cancellationToken, bool runFirst = true)
    {
        using var timer = new PeriodicTimer(interval, time);
        if (runFirst)
            await action(cancellationToken);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await action(cancellationToken);
    }

    private Task Every(TimeSpan interval, Action<CancellationToken> action, CancellationToken cancellationToken, bool runFirst = true) =>
        Every(interval, ct =>
        {
            action(ct);
            return Task.CompletedTask;
        }, cancellationToken, runFirst);
}
