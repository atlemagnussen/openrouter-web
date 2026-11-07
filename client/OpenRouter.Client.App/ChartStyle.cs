using ScottPlot;
using ScottPlot.Avalonia;

namespace OpenRouter.Client.App;

/// <summary>
/// Chart colours: the same validated palette as the router's web pages (site.css), with separate steps for dark mode.
/// Series 1 is always this machine's own (router, the network you're on), series 2 the other (internet, other
/// networks), series 3 your other networks.
/// </summary>
public sealed record ChartStyle(Color Surface, Color Text, Color Muted, Color Grid, Color Series1, Color Series2, Color Series3, Color Other)
{
    public static ChartStyle Light { get; } = new(
        Color.FromHex("#f6f7f9"), Color.FromHex("#1c2128"), Color.FromHex("#5d6673"), Color.FromHex("#e3e7ec"),
        Color.FromHex("#2a78d6"), Color.FromHex("#eb6834"), Color.FromHex("#1baf7a"), Color.FromHex("#9a9893"));

    public static ChartStyle Dark { get; } = new(
        Color.FromHex("#14171c"), Color.FromHex("#e3e7ec"), Color.FromHex("#98a2ae"), Color.FromHex("#262b33"),
        Color.FromHex("#3987e5"), Color.FromHex("#d95926"), Color.FromHex("#199e70"), Color.FromHex("#6f6e69"));

    /// <summary>Clears the plot and applies the surface, recessive axes and grid. Mouse pan/zoom is off: the range buttons pick the time.</summary>
    public void Reset(AvaPlot control)
    {
        var plot = control.Plot;
        plot.Clear();
        plot.FigureBackground.Color = Surface;
        plot.DataBackground.Color = Surface;
        plot.Axes.Color(Muted);
        plot.Axes.FrameColor(Grid);
        plot.Grid.MajorLineColor = Grid;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        control.UserInputProcessor.Disable();
    }
}
