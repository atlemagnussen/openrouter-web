using OpenRouter.Client.Reporting;
using OpenRouter.Client.Storage;

namespace OpenRouter.Client.App;

public enum StatusLevel { Unknown, Good, Warning, Bad }

/// <summary>What the tray icon and the status bar show: how the last two minutes went.</summary>
public sealed record AppState(StatusLevel Level, string Summary)
{
    /// <summary>The collector writes every couple of seconds; older than this means it isn't running (or the machine slept).</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);

    public static AppState From(LiveStatus status, DateTimeOffset now)
    {
        if (status.LastSample is not { } last)
            return new(StatusLevel.Unknown, "netcheck: nothing recorded yet");
        if (now - last > Stale)
            return new(StatusLevel.Unknown, $"netcheck: not recording (last measurement {Format.Span(now - last)} ago)");
        if (status.Wifi is { Connected: false })
            return new(StatusLevel.Bad, "netcheck: Wi-Fi disconnected");

        var level = (status.RouterLossPercent, status.InternetLossPercent) switch
        {
            ( >= 10, _) => StatusLevel.Bad,
            ( >= 1, _) or (_, >= 3) => StatusLevel.Warning,
            _ => StatusLevel.Good,
        };
        var wifi = status.Wifi is { Connected: true } w ? $"{w.SignalDbm} dBm {w.Band} ch {w.Channel}, " : "";
        return new(level, $"netcheck: {wifi}router {Format.Ms(status.RouterMs)} ({Format.Percent(status.RouterLossPercent ?? 0)} lost), " +
            $"internet {Format.Ms(status.InternetMs)} ({Format.Percent(status.InternetLossPercent ?? 0)} lost)");
    }
}
