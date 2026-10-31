using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Processes;

namespace OpenRouter.Web.Traffic;

/// <summary>
/// Reads the accounting counters. Listing nftables needs root, so this runs one exact command through sudo,
/// allowed by a sudoers line (see README): <c>sudo -n /usr/sbin/nft -j list table inet acct</c>.
/// </summary>
public sealed class NftService(IOptions<RouterOptions> options)
{
    public async Task<IReadOnlyList<AddressCounter>> GetCountersAsync(CancellationToken cancellationToken)
    {
        if (options.Value.SampleDir is { } sampleDir)
            return NftAcctParser.Parse(await File.ReadAllTextAsync(Path.Combine(sampleDir, "nft-acct.json"), cancellationToken));

        // -n: fail instead of asking for a password when the sudoers line is missing.
        return NftAcctParser.Parse(await CommandRunner.RunAsync("sudo",
            ["-n", options.Value.NftPath, "-j", "list", "table", "inet", "acct"], cancellationToken));
    }
}
