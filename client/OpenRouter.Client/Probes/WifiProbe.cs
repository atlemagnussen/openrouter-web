using System.Diagnostics;

namespace OpenRouter.Client.Probes;

/// <summary>Reads the Wi-Fi link with <c>iw</c> (Linux, no root needed). Unavailable on other systems or wired links.</summary>
public sealed class WifiProbe(string interfaceName)
{
    /// <summary>True if <paramref name="interfaceName"/> is a wireless interface.</summary>
    public static bool IsWireless(string interfaceName) =>
        OperatingSystem.IsLinux() && Directory.Exists($"/sys/class/net/{interfaceName}/wireless");

    public async Task<WifiSample> ReadAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        var link = IwParser.ParseLink(await RunAsync(["dev", interfaceName, "link"], cancellationToken));
        var station = link.Connected
            ? IwParser.ParseStation(await RunAsync(["dev", interfaceName, "station", "dump"], cancellationToken))
            : new IwParser.Station(null, null, null, null, null);
        return new WifiSample(at, interfaceName, link.Connected, link.Ssid, link.Bssid, link.FrequencyMhz, link.SignalDbm,
            station.SignalAverageDbm, link.TxBitrateMbps, link.RxBitrateMbps, station.TxPackets, station.TxFailed, station.TxRetries,
            station.BeaconLoss);
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("iw") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start iw.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output;
    }
}
