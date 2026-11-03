using System.Net;
using OpenRouter.Client;
using OpenRouter.Client.Probes;
using OpenRouter.Client.Reporting;
using OpenRouter.Client.Storage;

// netcheck: measures this machine's connection over time (ping, DNS, HTTPS, Wi-Fi) into SQLite. See README.md.
try
{
    return await (args.FirstOrDefault() switch
    {
        "run" => RunAsync(args.Skip(1).ToList()),
        "report" => ReportAsync(args.Skip(1).ToList()),
        "runs" => RunsAsync(args.Skip(1).ToList()),
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

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // finish cleanly: flush samples and print the report
        stop.Cancel();
    };
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
    Console.WriteLine(RunReport.Build((await store.GetRunAsync(runId, CancellationToken.None))!, TimeZoneInfo.Local));
    return 0;
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
    Console.WriteLine(RunReport.Build(data, TimeZoneInfo.Local));
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

        Run options:
          -d, --duration 4h            how long (s, m, h, d; 0 = until Ctrl+C). Default 2h
          --ping 1.1.1.1,8.8.8.8       ping targets besides the gateway (replaces the defaults)
          --dns 1.1.1.1                DNS servers besides the gateway (replaces the defaults)
          --no-gateway-dns             don't query the gateway as DNS server
          --url https://...            HTTPS URLs to time (replaces the defaults)
          -i, --interface wlan0        interface to watch (default: the one with the default route)
          --ping-interval 1s  --dns-interval 10s  --http-interval 30s
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
