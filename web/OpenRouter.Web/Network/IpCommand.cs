using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Network;

/// <summary>Reads network state with iproute2. Needs no privileges.</summary>
public sealed class IpCommand(IOptions<RouterOptions> options)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<Neighbour>> GetNeighboursAsync(CancellationToken cancellationToken) =>
        IpJsonParser.ParseNeighbours(await RunAsync(["-j", "neigh", "show"], "ip-neigh.json", cancellationToken));

    public async Task<IReadOnlyList<NetInterface>> GetInterfacesAsync(CancellationToken cancellationToken) =>
        IpJsonParser.ParseInterfaces(await RunAsync(["-j", "addr", "show"], "ip-addr.json", cancellationToken));

    private async Task<string> RunAsync(string[] arguments, string sampleFile, CancellationToken cancellationToken)
    {
        if (options.Value.IpSampleDir is { } sampleDir)
            return await File.ReadAllTextAsync(Path.Combine(sampleDir, sampleFile), cancellationToken);

        var startInfo = new ProcessStartInfo("ip")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start ip.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ip {string.Join(' ', arguments)} failed: {(await error).Trim()}");
        return await output;
    }
}
