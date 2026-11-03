using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;

namespace OpenRouter.Web.Dns;

/// <summary>
/// Reads bind's config (<c>named.conf.options</c>, world-readable on Debian) and its statistics channel,
/// a local HTTP endpoint enabled with <c>statistics-channels</c> (see README). Neither needs privileges.
/// </summary>
public sealed class BindService(IOptions<RouterOptions> options)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<BindOptions> GetOptionsAsync(CancellationToken cancellationToken) =>
        BindConfigParser.ParseOptions(await File.ReadAllTextAsync(options.Value.BindOptionsPath, cancellationToken));

    public async Task<BindStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        if (options.Value.SampleDir is { } sampleDir)
            return BindStatsParser.Parse(await File.ReadAllTextAsync(Path.Combine(sampleDir, "bind-stats.json"), cancellationToken));
        try
        {
            return BindStatsParser.Parse(await Http.GetStringAsync(options.Value.BindStatsUrl, cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"bind's statistics channel {options.Value.BindStatsUrl} didn't answer: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"bind's statistics channel {options.Value.BindStatsUrl} didn't answer in time.", ex);
        }
    }
}
