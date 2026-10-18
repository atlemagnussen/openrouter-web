using OpenRouter.Web.Pages;
using OpenRouter.Web.Systemd;
using static OpenRouter.Web.Health.HealthStatus;

namespace OpenRouter.Web.Health;

/// <summary>Are the router's daemons running, and have they been crashing.</summary>
public static class ServiceHealth
{
    public static IEnumerable<HealthCheck> Check(IReadOnlyList<UnitStatus>? units, DateTimeOffset now)
    {
        foreach (var unit in units ?? [])
        {
            var name = unit.Id.EndsWith(".service") ? unit.Id[..^".service".Length] : unit.Id;
            var logs = $"See journalctl -u {name} -b.";

            if (!unit.IsInstalled)
            {
                yield return new(name, Info, "Not installed.");
                continue;
            }

            var running = unit.ActiveSince is { } started ? $" for {Format.Duration(now - started)}" : "";
            yield return unit.ActiveState switch
            {
                "failed" => new(name, Problem, $"Failed ({unit.Result}).", logs),
                "inactive" => new(name, Problem, "Not running.", logs),
                "activating" or "deactivating" or "reloading" => new(name, Warning, $"{Capitalize(unit.ActiveState)} ({unit.SubState}).", logs),
                _ when unit.Restarts > 0 => new(name, Warning,
                    $"Running{running}, after systemd restarted it {unit.Restarts} time{(unit.Restarts == 1 ? "" : "s")}.",
                    $"The service crashed or exited and was restarted automatically; clients lose that service for a moment each time. {logs}"),
                _ when now - unit.ActiveSince < TimeSpan.FromHours(1) => new(name, Info,
                    $"Started {Format.Duration(now - unit.ActiveSince.Value)} ago.", "A recent restart; fine if you did it."),
                _ => new(name, Ok, unit.SubState == "exited" ? $"Loaded{running}." : $"Running{running}."),
            };
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
