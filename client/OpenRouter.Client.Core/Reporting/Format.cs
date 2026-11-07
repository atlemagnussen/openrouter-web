namespace OpenRouter.Client.Reporting;

public static class Format
{
    public static string Ms(double? ms) => ms switch
    {
        null => "—",
        < 10 => $"{ms:0.0} ms",
        _ => $"{ms:0} ms",
    };

    public static string Span(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{span.TotalSeconds:0.#} s",
        { TotalMinutes: < 60 } => span.Seconds == 0 ? $"{span.Minutes} min" : $"{span.Minutes} min {span.Seconds} s",
        { TotalHours: < 24 } => span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min",
        _ => span.Hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {span.Hours} h",
    };

    public static string Percent(double percent) => percent switch
    {
        0 => "0%",
        < 0.1 => "<0.1%",
        < 10 => $"{percent:0.0}%",
        _ => $"{percent:0}%",
    };
}
