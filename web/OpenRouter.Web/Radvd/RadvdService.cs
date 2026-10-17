using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Radvd;

public sealed class RadvdService(IOptions<RouterOptions> options)
{
    public async Task<RadvdConfig> GetConfigAsync(CancellationToken cancellationToken) =>
        RadvdParser.Parse(await File.ReadAllTextAsync(options.Value.RadvdConfigPath, cancellationToken));
}
