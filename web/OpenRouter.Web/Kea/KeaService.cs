using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Kea;

public sealed class KeaService(
    IOptions<RouterOptions> options,
    KeaControlClient control,
    ILogger<KeaService> logger)
{
    public async Task<KeaConfig> GetConfigAsync(CancellationToken cancellationToken) =>
        KeaConfigParser.Parse(await File.ReadAllTextAsync(options.Value.KeaConfigPath, cancellationToken));

    /// <summary>Kea's counters (packets, allocation failures, ...). Needs the control socket.</summary>
    public async Task<KeaStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        if (options.Value.SampleDir is { } sampleDir)
            return KeaStatisticsParser.Parse(
                await File.ReadAllTextAsync(Path.Combine(sampleDir, "kea-statistics.json"), cancellationToken), TimeZoneInfo.Local);
        if (!control.IsAvailable)
            throw new IOException($"Kea's control socket {options.Value.KeaControlSocket} doesn't exist.");
        try
        {
            return KeaStatisticsParser.Parse(await control.SendAsync("statistic-get-all", cancellationToken), TimeZoneInfo.Local);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Kea's control socket didn't answer in time.");
        }
    }

    /// <summary>Leases from Kea's API when its socket is reachable, otherwise from the lease file.</summary>
    public async Task<IReadOnlyList<KeaLease>> GetLeasesAsync(CancellationToken cancellationToken)
    {
        if (control.IsAvailable)
        {
            try
            {
                return KeaLeaseParser.ParseCommandResponse(await control.SendAsync("lease4-get-all", cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is SocketException or IOException or OperationCanceledException or JsonException
                    or FormatException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Kea control socket failed; reading the lease file instead");
            }
        }
        return KeaLeaseParser.ParseCsvFiles(await ReadLeaseFilesAsync(cancellationToken));
    }

    /// <summary>
    /// The memfile and the files Kea's lease file cleanup (LFC) leaves next to it, oldest first, the way Kea
    /// reads them at startup: <c>.completed</c> (or <c>.1</c> while a cleanup is running), <c>.2</c>, then the
    /// file itself. After a cleanup, leases that haven't changed since are only in the older files.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadLeaseFilesAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.KeaLeasesPath;
        var completed = path + ".completed";
        string[] candidates = [File.Exists(completed) ? completed : path + ".1", path + ".2", path];

        var texts = new List<string>();
        foreach (var file in candidates)
        {
            // The main file must exist; the others only do around a cleanup.
            if (file != path && !File.Exists(file))
                continue;
            texts.Add(await File.ReadAllTextAsync(file, cancellationToken));
        }
        return texts;
    }
}
