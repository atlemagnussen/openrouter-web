namespace OpenRouter.Web.Pages;

public static class Format
{
    /// <summary>A span in the largest sensible unit: "45 s", "10 min", "2 h 30 min", "3 d".</summary>
    public static string Duration(TimeSpan span)
    {
        span = span.Duration();
        if (span < TimeSpan.FromMinutes(1))
            return $"{(int)span.TotalSeconds} s";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes} min";
        if (span < TimeSpan.FromDays(1))
            return span.Minutes == 0 ? $"{span.Hours} h" : $"{span.Hours} h {span.Minutes} min";
        return span.Hours == 0 ? $"{span.Days} d" : $"{span.Days} d {span.Hours} h";
    }

    public static string Duration(int? seconds) =>
        seconds is { } s ? Duration(TimeSpan.FromSeconds(s)) : "—";

    /// <summary>Lifetime from <c>ip addr</c>; null means forever.</summary>
    public static string Lifetime(TimeSpan? lifetime) =>
        lifetime is { } span ? Duration(span) : "forever";

    /// <summary>"in 7 min" or "12 min ago".</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now) =>
        when >= now ? $"in {Duration(when - now)}" : $"{Duration(now - when)} ago";
}
