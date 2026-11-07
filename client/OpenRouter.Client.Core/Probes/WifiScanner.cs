using System.Diagnostics;

namespace OpenRouter.Client.Probes;

/// <summary>
/// Lists the Wi-Fi networks around, without root: asks NetworkManager to scan (allowed for the logged-in user),
/// waits for it, then reads the results with signal in dBm from <c>iw dev IF scan dump</c>.
/// A scan makes the radio leave its channel for a moment, so pings around it may be a little slower.
/// </summary>
public sealed class WifiScanner(string interfaceName)
{
    /// <summary>How long a scan of all channels takes, roughly.</summary>
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(6);

    public async Task<IReadOnlyList<ScanSample>> ScanAsync(TimeProvider time, CancellationToken cancellationToken)
    {
        // Fails when a scan ran moments ago or NetworkManager isn't there; the dump then has what the kernel heard last.
        if (await TryRunAsync("nmcli", ["device", "wifi", "rescan", "ifname", interfaceName], cancellationToken))
            await Task.Delay(ScanTime, time, cancellationToken);
        var dump = await RunAsync("iw", ["dev", interfaceName, "scan", "dump"], cancellationToken);
        return ScanParser.Parse(dump, time.GetUtcNow(), maxAge: TimeSpan.FromSeconds(30));
    }

    private static async Task<bool> TryRunAsync(string file, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Start(file, arguments);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // not installed
        }
    }

    private static async Task<string> RunAsync(string file, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = Start(file, arguments);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output;
    }

    private static Process Start(string file, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {file}.");
    }
}
