namespace OpenRouter.Web.Health;

/// <summary>Ordered from best to worst, so the worst of several is their <c>Max()</c>.</summary>
public enum HealthStatus
{
    Ok,
    Info,
    Warning,
    Problem,
}

/// <param name="Summary">One line: what was found.</param>
/// <param name="Advice">What it means and what to try. Shown for anything that isn't OK.</param>
/// <param name="Details">Supporting facts, such as the affected devices.</param>
public sealed record HealthCheck(
    string Title,
    HealthStatus Status,
    string Summary,
    string? Advice = null,
    IReadOnlyList<string>? Details = null);

public sealed record HealthArea(string Name, string Description, IReadOnlyList<HealthCheck> Checks)
{
    public HealthStatus Status => Checks.Count > 0 ? Checks.Max(c => c.Status) : HealthStatus.Ok;
}
