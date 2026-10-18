using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Firewall;

/// <summary>Reads the nftables ruleset file. Not parsed: the Health page only looks for a few telltale rules.</summary>
public sealed class FirewallService(IOptions<RouterOptions> options)
{
    public Task<string> GetConfigTextAsync(CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(options.Value.NftablesConfigPath, cancellationToken);
}
