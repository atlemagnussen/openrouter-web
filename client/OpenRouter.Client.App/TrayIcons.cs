using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OpenRouter.Client.App;

/// <summary>The tray icon, drawn in code: Wi-Fi arcs in a colour for the state (grey: not recording).</summary>
public static class TrayIcons
{
    private static readonly Dictionary<StatusLevel, WindowIcon> Cache = [];

    public static WindowIcon For(StatusLevel level)
    {
        if (Cache.TryGetValue(level, out var icon))
            return icon;
        var color = level switch
        {
            StatusLevel.Good => Color.Parse("#1baf7a"),
            StatusLevel.Warning => Color.Parse("#eda100"),
            StatusLevel.Bad => Color.Parse("#e04848"),
            _ => Color.Parse("#9a9893"),
        };
        return Cache[level] = Draw(color);
    }

    private static WindowIcon Draw(Color color)
    {
        const int size = 64;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = bitmap.CreateDrawingContext())
        {
            var pen = new Pen(new SolidColorBrush(color), 7, lineCap: PenLineCap.Round);
            foreach (var radius in new[] { 40.0, 26.0 })
            {
                // An arc of 90° opening upwards, centred on the dot.
                var start = new Point(32 - radius * Math.Sin(Math.PI / 4), 54 - radius * Math.Cos(Math.PI / 4));
                var end = new Point(32 + radius * Math.Sin(Math.PI / 4), start.Y);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(start, false);
                    g.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
            context.DrawEllipse(new SolidColorBrush(color), null, new Point(32, 52), 7, 7);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
