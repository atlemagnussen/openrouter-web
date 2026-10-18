using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Processes;

namespace OpenRouter.Web.Network;

/// <summary>Reads network state with iproute2. Needs no privileges.</summary>
public sealed class IpCommand(IOptions<RouterOptions> options)
{
    public async Task<IReadOnlyList<Neighbour>> GetNeighboursAsync(CancellationToken cancellationToken) =>
        IpJsonParser.ParseNeighbours(await RunAsync(["-j", "neigh", "show"], "ip-neigh.json", cancellationToken));

    public async Task<IReadOnlyList<NetInterface>> GetInterfacesAsync(CancellationToken cancellationToken) =>
        IpJsonParser.ParseInterfaces(await RunAsync(["-j", "addr", "show"], "ip-addr.json", cancellationToken));

    /// <summary>Packet and error counters per interface, cumulative since boot.</summary>
    public async Task<IReadOnlyList<LinkStats>> GetLinkStatsAsync(CancellationToken cancellationToken) =>
        IpJsonParser.ParseLinkStats(await RunAsync(["-j", "-s", "link", "show"], "ip-link.json", cancellationToken));

    public async Task<IReadOnlyList<DefaultRoute>> GetDefaultRoutesAsync(bool ipv6, CancellationToken cancellationToken) =>
        IpJsonParser.ParseDefaultRoutes(ipv6
            ? await RunAsync(["-6", "-j", "route", "show", "default"], "ip-route6.json", cancellationToken)
            : await RunAsync(["-4", "-j", "route", "show", "default"], "ip-route.json", cancellationToken));

    private async Task<string> RunAsync(string[] arguments, string sampleFile, CancellationToken cancellationToken) =>
        options.Value.SampleDir is { } sampleDir
            ? await File.ReadAllTextAsync(Path.Combine(sampleDir, sampleFile), cancellationToken)
            : await CommandRunner.RunAsync("ip", arguments, cancellationToken);
}
