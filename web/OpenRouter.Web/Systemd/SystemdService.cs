using Microsoft.Extensions.Options;
using OpenRouter.Web.Configuration;
using OpenRouter.Web.Processes;

namespace OpenRouter.Web.Systemd;

/// <summary>Reads unit state with <c>systemctl show</c>, which needs no privileges.</summary>
public sealed class SystemdService(IOptions<RouterOptions> options)
{
    public async Task<IReadOnlyList<UnitStatus>> GetUnitsAsync(IReadOnlyList<string> units, CancellationToken cancellationToken)
    {
        if (units.Count == 0)
            return [];

        string output;
        if (options.Value.SampleDir is { } sampleDir)
        {
            output = await File.ReadAllTextAsync(Path.Combine(sampleDir, "systemctl-show.txt"), cancellationToken);
        }
        else
        {
            // "--" so a unit name can never be taken as an option.
            output = await CommandRunner.RunAsync("systemctl",
                ["show", "--timestamp=unix", "--property=Id,LoadState,ActiveState,SubState,Result,NRestarts,ActiveEnterTimestamp", "--", .. units],
                cancellationToken);
        }

        // systemctl adds ".service"; match the configured names either way.
        var wanted = units.Select(u => u.Contains('.') ? u : u + ".service").ToHashSet();
        return SystemctlParser.Parse(output).Where(u => wanted.Contains(u.Id)).ToList();
    }
}
