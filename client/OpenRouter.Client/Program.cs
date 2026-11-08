using System.Net;
using System.Runtime.InteropServices;
using OpenRouter.Client;
using OpenRouter.Client.Probes;
using OpenRouter.Client.Reporting;
using OpenRouter.Client.Settings;
using OpenRouter.Client.Storage;

// netcheck: measures this machine's connection over time (ping, DNS, HTTPS, Wi-Fi) into SQLite. See README.md.
try
{
    return await (args.FirstOrDefault() switch
    {
        "run" => RunAsync(args.Skip(1).ToList()),
        "report" => ReportAsync(args.Skip(1).ToList()),
        "runs" => RunsAsync(args.Skip(1).ToList()),
        "daemon" => DaemonAsync(args.Skip(1).ToList()),
        "scan" => ScanAsync(args.Skip(1).ToList()),
        "mine" => Task.FromResult(Mine(args.Skip(1).ToList())),
        null or "help" or "--help" or "-h" => Task.FromResult(Help()),
        var other => Task.FromResult(Fail($"Unknown command \"{other}\". Try: netcheck help")),
    });
}
catch (FormatException ex)
{
    return Fail(ex.Message);
}

static async Task<int> RunAsync(IReadOnlyList<string> args)
{
    var options = RunOptions.Parse(args);
    var network = NetworkState.Read(options.Interface);
    if (network is null)
        return Fail(options.Interface is null ? "No network interface with a default gateway." : $"Interface {options.Interface} isn't up.");

    using var stop = StopOnSignals();
    if (options.Duration is { } duration)
        stop.CancelAfter(duration);

    await using var store = await ResultStore.OpenAsync(options.DatabasePath, CancellationToken.None);
    var time = TimeProvider.System;
    var runId = await store.StartRunAsync(time.GetUtcNow(), Dns.GetHostName(), network.Interface, network.Mac,
        string.Join(", ", network.Ipv4), network.Gateway4?.ToString(), options.Note, CancellationToken.None);

    Console.WriteLine($"netcheck run {runId}: {network.Interface} ({network.Mac}), IPv4 {string.Join(", ", network.Ipv4)}, gateway {network.Gateway4}" +
        (network.HasGlobalIpv6 ? $", {network.Ipv6Global.Count} global IPv6" : ", no global IPv6"));
    Console.WriteLine($"Recording to {store.DatabasePath} {(options.Duration is { } d ? $"for {Format.Span(d)}" : "until Ctrl+C")}. Ctrl+C stops early.");

    await new NetMonitor(options, store, runId, network, time, Console.Out).RunAsync(stop.Token);
    await store.EndRunAsync(runId, time.GetUtcNow(), CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine(RunReport.Build((await store.GetRunAsync(runId, CancellationToken.None))!, TimeZoneInfo.Local, Vendors.Load(), ClientSettings.Load(ClientSettings.DefaultPath)));
    return 0;
}

// The collector: measures until stopped (systemd user service), one run per network, and deletes old data.
static async Task<int> DaemonAsync(IReadOnlyList<string> args)
{
    var options = RunOptions.Parse(args) with { Continuous = true, Duration = null, StatusInterval = TimeSpan.FromMinutes(5) };
    using var stop = StopOnSignals();
    await using var store = await ResultStore.OpenAsync(options.DatabasePath, CancellationToken.None);
    var time = TimeProvider.System;
    Console.WriteLine($"netcheck collector: recording to {store.DatabasePath}, keeping {Format.Span(options.Keep)}.");

    var pruned = DateTimeOffset.MinValue;
    var waiting = false;
    while (!stop.IsCancellationRequested)
    {
        if (time.GetUtcNow() - pruned > TimeSpan.FromHours(6))
        {
            await store.DeleteBeforeAsync(time.GetUtcNow() - options.Keep, CancellationToken.None);
            pruned = time.GetUtcNow();
        }

        var network = NetworkState.Read(options.Interface);
        if (network is null)
        {
            if (!waiting)
                Console.WriteLine("No network with a default gateway; waiting.");
            waiting = true;
            await Pause(TimeSpan.FromSeconds(10), stop.Token);
            continue;
        }
        waiting = false;

        var runId = await store.StartRunAsync(time.GetUtcNow(), Dns.GetHostName(), network.Interface, network.Mac,
            string.Join(", ", network.Ipv4), network.Gateway4?.ToString(), options.Note, CancellationToken.None);
        Console.WriteLine($"Run {runId}: {network.Interface} ({network.Mac}), IPv4 {string.Join(", ", network.Ipv4)}, gateway {network.Gateway4}");
        var monitor = new NetMonitor(options, store, runId, network, time, Console.Out);
        await monitor.RunAsync(stop.Token);
        await store.EndRunAsync(runId, monitor.LastAwake, CancellationToken.None);
        Console.WriteLine($"Run {runId} ended: {monitor.StopReason ?? "stopped"}.");
        if (monitor.StopReason is not null)
            await Pause(TimeSpan.FromSeconds(5), stop.Token);
    }
    return 0;
}

// One scan: the networks this machine hears now, by band and channel.
static async Task<int> ScanAsync(IReadOnlyList<string> args)
{
    var iface = Option(args, "--interface") ?? Option(args, "-i") ?? NetworkState.Read(null)?.Interface;
    if (iface is null || !WifiProbe.IsWireless(iface))
        return Fail(iface is null ? "No network interface found; pass -i wlan0." : $"{iface} is not a Wi-Fi interface.");
    Console.WriteLine($"Scanning on {iface}…");
    var vendors = Vendors.Load();
    var networks = await new WifiScanner(iface).ScanAsync(TimeProvider.System, CancellationToken.None);
    var makers = vendors.LookupAll(networks.Select(n => n.Bssid));
    var heard = Neighbourhood.Summarise(networks);
    var owners = Neighbourhood.Classify(heard, networks.FirstOrDefault(n => n.Associated)?.Ssid, ClientSettings.Load(ClientSettings.DefaultPath));
    Console.WriteLine($"{"band",-8} {"channels",-16} {"signal",8}  {"network",-26} {"bssid",-17}  maker");
    foreach (var n in networks.OrderBy(n => n.FrequencyMhz).ThenByDescending(n => n.SignalDbm))
        Console.WriteLine($"{n.Band,-8} {WifiBand.Channels(n.FrequencyMhz, n.WidthMhz, n.CenterMhz),-16} {(n.SignalDbm is { } dbm ? $"{dbm} dBm" : "—"),8}  {(n.Ssid ?? "(hidden)"),-26} {n.Bssid}  " +
            $"{makers[n.Bssid]}{(n.Associated ? "  ← connected" : owners[n.Bssid] == Ownership.Mine ? "  (yours)" : "")}");
    return 0;
}

// Which networks are yours: netcheck mine [add|remove NAME]. A BSSID (aa:bb:…) marks a single, hidden network.
static int Mine(IReadOnlyList<string> args)
{
    var path = ClientSettings.DefaultPath;
    var settings = ClientSettings.Load(path);
    if (args.Count >= 2 && args[0] is "add" or "remove")
    {
        var name = string.Join(' ', args.Skip(1));
        var isBssid = System.Text.RegularExpressions.Regex.IsMatch(name, "^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$");
        settings = settings.With(isBssid ? null : name, isBssid ? name : "", args[0] == "add");
        settings.Save(path);
    }
    else if (args.Count > 0)
        return Fail("Usage: netcheck mine [add NAME | remove NAME]");

    Console.WriteLine(settings.MyNetworks.Count + settings.MyBssids.Count == 0
        ? "No networks marked as yours yet (the one you're connected to always counts). Add one with: netcheck mine add NAME"
        : $"Your networks, besides the one you're connected to: {string.Join(", ", settings.MyNetworks.Concat(settings.MyBssids))}");
    Console.WriteLine($"({path})");
    return 0;
}

// Ctrl+C and systemd's SIGTERM both finish cleanly: flush samples and end the run.
static CancellationTokenSource StopOnSignals()
{
    var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    // Kept in a field: the registration ends when it's garbage collected.
    Signals.Term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });
    return stop;
}

static async Task Pause(TimeSpan delay, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(delay, cancellationToken);
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task<int> ReportAsync(IReadOnlyList<string> args)
{
    var db = Option(args, "--db") ?? RunOptions.DefaultDatabasePath;
    await using var store = await ResultStore.OpenAsync(db, CancellationToken.None);
    var runs = await store.GetRunsAsync(CancellationToken.None);
    if (runs.Count == 0)
        return Fail($"No runs in {db} yet. Start one with: netcheck run");

    var which = args.FirstOrDefault(a => !a.StartsWith('-') && a != db);
    var runId = which is null or "last" ? runs[^1].Id : long.TryParse(which, out var id) ? id : throw new FormatException($"\"{which}\" is not a run number.");
    var data = await store.GetRunAsync(runId, CancellationToken.None);
    if (data is null)
        return Fail($"No run {runId}. See: netcheck runs");
    Console.WriteLine(RunReport.Build(data, TimeZoneInfo.Local, Vendors.Load(), ClientSettings.Load(ClientSettings.DefaultPath)));
    return 0;
}

static async Task<int> RunsAsync(IReadOnlyList<string> args)
{
    var db = Option(args, "--db") ?? RunOptions.DefaultDatabasePath;
    await using var store = await ResultStore.OpenAsync(db, CancellationToken.None);
    foreach (var run in await store.GetRunsAsync(CancellationToken.None))
    {
        var length = (run.Ended ?? run.Started) - run.Started;
        Console.WriteLine($"{run.Id,4}  {run.Started.ToLocalTime():yyyy-MM-dd HH:mm}  {Format.Span(length),-12} {run.Host} {run.Interface}" +
            (run.Ended is null ? "  (unfinished)" : "") + (run.Note is null ? "" : $"  {run.Note}"));
    }
    return 0;
}

static string? Option(IReadOnlyList<string> args, string name)
{
    var i = args.ToList().IndexOf(name);
    return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
}

static int Help()
{
    Console.WriteLine("""
        netcheck: measure this machine's network connection over time.

          netcheck run [options]       measure, then print a report (Ctrl+C stops early)
          netcheck report [N|last]     report on run N (default: the last one)
          netcheck runs                list recorded runs
          netcheck scan                list the Wi-Fi networks around now, with channel, signal and maker
          netcheck mine [add|remove N] networks that are yours (shown as such in reports and the app)
          netcheck daemon [options]    measure continuously, one run per network (the systemd user service)

        Run options:
          -d, --duration 4h            how long (s, m, h, d; 0 = until Ctrl+C). Default 2h
          --ping 1.1.1.1,8.8.8.8       ping targets besides the gateway (replaces the defaults)
          --dns 1.1.1.1                DNS servers besides the gateway (replaces the defaults)
          --no-gateway-dns             don't query the gateway as DNS server
          --url https://...            HTTPS URLs to time (replaces the defaults)
          -i, --interface wlan0        interface to watch (default: the one with the default route)
          --ping-interval 1s  --dns-interval 10s  --http-interval 30s
          --scan-interval 2m           scan for networks around (0 = never). Default 2m
          --keep 30d                   daemon: delete measurements older than this. Default 30d
          --note "living room"         stored with the run
          --db path.db                 database (default ~/.local/share/openrouter-client/netcheck.db)
        """);
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

internal static class Signals
{
    public static PosixSignalRegistration? Term;
}
