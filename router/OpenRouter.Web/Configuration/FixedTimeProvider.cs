namespace OpenRouter.Web.Configuration;

/// <summary>A clock stopped at one moment, so the sample leases look the same whenever the app runs.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
