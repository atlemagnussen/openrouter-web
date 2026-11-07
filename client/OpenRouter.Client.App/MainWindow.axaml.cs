using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenRouter.Client.Probes;
using OpenRouter.Client.Reporting;
using OpenRouter.Client.Storage;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using PlotColor = ScottPlot.Color;
using FontWeight = Avalonia.Media.FontWeight;

namespace OpenRouter.Client.App;

/// <summary>One row of the networks table.</summary>
public sealed record NetworkRow(string Name, string Bssid, string Maker, string Channel, string Typical, string Strongest, string Scans,
    string FirstHeard, string Note, FontWeight Weight);

/// <summary>One row of the events list.</summary>
public sealed record EventRow(DateTimeOffset Time, string When, string Kind, string Detail);

/// <summary>
/// The app's only window. It reads the collector's database every couple of seconds (never writes to it):
/// the status bar, charts over the chosen range, the networks heard in scans, and the events.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan StatusWindow = TimeSpan.FromMinutes(2);

    private readonly string _databasePath;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private HistoryReader? _reader;
    private Vendors _vendors = Vendors.Empty;
    private ChartStyle _style = ChartStyle.Light;
    private bool _busy;
    private bool _quitting;

    private TimeSpan _range = TimeSpan.FromHours(1);
    private DateTimeOffset? _end; // null: the range ends now and follows the clock
    private DateTimeOffset _chartsLoaded = DateTimeOffset.MinValue;
    private DateTimeOffset _networksLoaded = DateTimeOffset.MinValue;

    private Timeline? _timeline;
    private readonly List<VerticalLine> _cursors = [];
    private readonly List<(AvaPlot Plot, double X, double Y, string Text)> _dots = [];

    public MainWindow() : this(RunOptions.DefaultDatabasePath)
    {
    }

    public MainWindow(string databasePath)
    {
        _databasePath = databasePath;
        InitializeComponent();
        Icon = TrayIcons.For(StatusLevel.Good);

        foreach (var button in RangeButtons.Children.OfType<ToggleButton>())
            button.Click += (_, _) => PickRange(button);
        StartCollector.Click += (_, _) => StartTheCollector();
        EventList.SelectionChanged += (_, _) =>
        {
            if (EventList.SelectedItem is EventRow row)
                ShowMoment(row.Time);
        };
        foreach (var plot in new[] { SignalPlot, RatePlot, LatencyPlot, LossPlot })
        {
            plot.PointerMoved += (_, e) => HoverTimeline(plot, e);
            plot.PointerExited += (_, _) => HideCursor();
        }
        foreach (var plot in new[] { Band24Plot, Band5Plot })
            plot.PointerMoved += (_, e) => HoverNetworks(plot, e);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Q, KeyModifiers.Control), Command = new QuitCommand(this) });

        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        _timer.Tick += async (_, _) => await RefreshAsync();
        // Start-up choices: --networks opens that tab, --range 24h picks a range button by its label without spaces.
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--networks"))
            Tabs.SelectedIndex = 1;
        if (Array.IndexOf(args, "--range") is var r and >= 0 && r + 1 < args.Length &&
            RangeButtons.Children.OfType<ToggleButton>().FirstOrDefault(b => ((string)b.Content!).Replace(" ", "").Replace("min", "m") == args[r + 1]) is { } start)
            PickRange(start);
        Opened += async (_, _) =>
        {
            _vendors = await Task.Run(Vendors.Load);
            await RefreshAsync();
            _timer.Start();
        };
    }

    /// <summary>The tray icon follows this.</summary>
    public event Action<AppState>? StateChanged;

    public event Action? QuitRequested;

    /// <summary>When true, closing the window hides it and the app keeps running in the tray.</summary>
    public bool KeepRunningWhenClosed { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (KeepRunningWhenClosed && !_quitting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (!_quitting)
        {
            _quitting = true;
            QuitRequested?.Invoke();
        }
        base.OnClosing(e);
    }

    private void Quit()
    {
        _quitting = true;
        QuitRequested?.Invoke();
    }

    // ---- loading ----------------------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _reader ??= await Task.Run(() => HistoryReader.OpenAsync(_databasePath, CancellationToken.None));
            var reader = _reader;

            var status = await Task.Run(() => reader.GetStatusAsync(now, StatusWindow, CancellationToken.None));
            ShowStatus(status, now);

            var (from, to) = Range(now);
            if (now - _chartsLoaded >= ChartRefresh)
            {
                var bucket = HistoryReader.BucketFor(_range);
                var timeline = await Task.Run(() => reader.GetTimelineAsync(from, to, bucket, CancellationToken.None));
                var events = await Task.Run(() => reader.GetEventsAsync(from, to, CancellationToken.None));
                _timeline = timeline;
                DrawTimeline(timeline, events, from, to);
                ShowEvents(events);
                _chartsLoaded = now;
            }
            if (now - _networksLoaded >= TimeSpan.FromSeconds(30))
            {
                var scans = await Task.Run(() => reader.GetScansAsync(from, to, CancellationToken.None));
                var firstHeard = await Task.Run(() => reader.GetFirstHeardAsync(CancellationToken.None));
                ShowNetworks(scans, firstHeard, status.Wifi, now);
                _networksLoaded = now;
            }
        }
        catch (Exception ex)
        {
            CollectorBanner.IsVisible = true;
            CollectorText.Text = $"Can't read {_databasePath}: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Short ranges follow the clock closely; long ones barely change from one minute to the next.</summary>
    private TimeSpan ChartRefresh => _end is not null ? TimeSpan.MaxValue
        : _range <= TimeSpan.FromHours(1) ? TimeSpan.FromSeconds(2)
        : _range <= TimeSpan.FromDays(1) ? TimeSpan.FromSeconds(30)
        : TimeSpan.FromMinutes(2);

    private (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset now)
    {
        var to = _end ?? now;
        return (to - _range, to);
    }

    private void Reload()
    {
        _chartsLoaded = DateTimeOffset.MinValue;
        _networksLoaded = DateTimeOffset.MinValue;
        _ = RefreshAsync();
    }

    private void PickRange(ToggleButton picked)
    {
        foreach (var button in RangeButtons.Children.OfType<ToggleButton>())
            button.IsChecked = button == picked;
        _range = TimeSpan.Parse((string)picked.Tag!, System.Globalization.CultureInfo.InvariantCulture);
        _end = null;
        Reload();
    }

    /// <summary>From the events list: the 15 minutes around a moment, on the Connection tab.</summary>
    private void ShowMoment(DateTimeOffset time)
    {
        foreach (var button in RangeButtons.Children.OfType<ToggleButton>())
            button.IsChecked = false;
        _range = TimeSpan.FromMinutes(15);
        _end = time + _range / 2;
        Tabs.SelectedIndex = 0;
        Reload();
    }

    // ---- status bar -------------------------------------------------------------------------------

    private void ShowStatus(LiveStatus status, DateTimeOffset now)
    {
        var state = AppState.From(status, now);
        StateChanged?.Invoke(state);

        if (status.Wifi is { Connected: true } wifi)
        {
            WifiText.Text = $"{wifi.SignalDbm} dBm";
            WifiDetail.Text = $"{wifi.Ssid} · {wifi.Band} ch {wifi.Channel} · {wifi.TxBitrateMbps:0} Mbit/s · {wifi.Bssid}";
        }
        else
        {
            WifiText.Text = status.Wifi is null ? "—" : "disconnected";
            WifiDetail.Text = "";
        }
        RouterText.Text = Format.Ms(status.RouterMs);
        RouterDetail.Text = status.RouterLossPercent is { } routerLoss ? $"{Format.Percent(routerLoss)} lost" : "";
        InternetText.Text = Format.Ms(status.InternetMs);
        InternetDetail.Text = status.InternetLossPercent is { } internetLoss ? $"{Format.Percent(internetLoss)} lost" : "";

        var stale = state.Level == StatusLevel.Unknown;
        CollectorBanner.IsVisible = stale;
        if (stale)
            CollectorText.Text = status.LastSample is { } last
                ? $"The collector isn't recording: the last measurement is {Format.Span(now - last)} old."
                : "Nothing recorded yet. The collector (netcheck daemon) does the measuring; this window only shows it.";

        var (from, to) = Range(now);
        RangeText.Text = _end is null
            ? $"Live · {Local(from):ddd HH:mm} – now"
            : $"{Local(from):ddd d MMM HH:mm} – {Local(to):HH:mm} · pick a range to go back to live";
    }

    private void StartTheCollector()
    {
        try
        {
            var start = new ProcessStartInfo("systemctl") { RedirectStandardError = true };
            foreach (var argument in new[] { "--user", "start", "netcheck" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            CollectorText.Text = process.ExitCode == 0
                ? "Starting the collector…"
                : $"Couldn't start it: {error.Trim()} Install it with client/install.sh, or run `netcheck daemon` in a terminal.";
        }
        catch (Exception ex)
        {
            CollectorText.Text = $"Couldn't start it: {ex.Message}";
        }
    }

    // ---- connection charts ------------------------------------------------------------------------

    private void DrawTimeline(Timeline timeline, IReadOnlyList<NetEvent> events, DateTimeOffset from, DateTimeOffset to)
    {
        var xs = timeline.Times.Select(t => X(t + timeline.Bucket / 2)).ToArray();
        var (xMin, xMax) = (X(from), X(to));
        _cursors.Clear();

        _style.Reset(SignalPlot);
        AddLine(SignalPlot.Plot, xs, timeline.SignalDbm, _style.Series1);
        var switches = events.Where(e => e.Kind is "roam" or "wifi-disconnected").ToList();
        if (switches.Count <= 200)
        {
            foreach (var e in switches)
            {
                var line = SignalPlot.Plot.Add.VerticalLine(X(e.Time), 1, _style.Muted);
                line.LinePattern = LinePattern.Dashed;
            }
        }
        Finish(SignalPlot, xMin, xMax, -95, -25);

        _style.Reset(RatePlot);
        AddLine(RatePlot.Plot, xs, timeline.TxMbps, _style.Series1);
        Finish(RatePlot, xMin, xMax, 0, Math.Max(50, Max(timeline.TxMbps) * 1.1));

        // A single slow reply mustn't flatten the chart: scale to the 98th percentile and let the readout tell the rest.
        _style.Reset(LatencyPlot);
        AddLine(LatencyPlot.Plot, xs, timeline.RouterMs, _style.Series1);
        AddLine(LatencyPlot.Plot, xs, timeline.InternetMs, _style.Series2);
        Finish(LatencyPlot, xMin, xMax, 0, Math.Max(20, Percentile(timeline.RouterMs.Concat(timeline.InternetMs), 0.98) * 1.3));

        _style.Reset(LossPlot);
        AddLine(LossPlot.Plot, xs, timeline.RouterLossPercent, _style.Series1);
        AddLine(LossPlot.Plot, xs, timeline.InternetLossPercent, _style.Series2);
        Finish(LossPlot, xMin, xMax, 0, Math.Max(10, Max(timeline.RouterLossPercent.Concat(timeline.InternetLossPercent)) * 1.1));
    }

    /// <summary>A 2 px line, broken where nothing was measured (asleep, collector off), so a gap never looks like data.</summary>
    private static void AddLine(Plot plot, double[] xs, double?[] ys, PlotColor color)
    {
        var segmentX = new List<double>();
        var segmentY = new List<double>();
        void Flush()
        {
            if (segmentX.Count == 1)
            {
                var dot = plot.Add.ScatterPoints(segmentX.ToArray(), segmentY.ToArray(), color);
                dot.MarkerSize = 4;
            }
            else if (segmentX.Count > 1)
            {
                var line = plot.Add.ScatterLine(segmentX.ToArray(), segmentY.ToArray(), color);
                line.LineWidth = 2;
            }
            segmentX.Clear();
            segmentY.Clear();
        }
        for (var i = 0; i < xs.Length; i++)
        {
            if (ys[i] is { } y)
            {
                segmentX.Add(xs[i]);
                segmentY.Add(y);
            }
            else
                Flush();
        }
        Flush();
    }

    private void Finish(AvaPlot control, double xMin, double xMax, double yMin, double yMax)
    {
        var plot = control.Plot;
        var axis = plot.Axes.DateTimeTicksBottom();
        if (axis.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
            ticks.LabelFormatter = _range > TimeSpan.FromDays(1) ? t => t.ToString("ddd HH:mm") : t => t.ToString("HH:mm");
        plot.Axes.Bottom.TickLabelStyle.ForeColor = _style.Muted;
        plot.Axes.Bottom.MajorTickStyle.Color = _style.Grid;
        plot.Axes.Left.MajorTickStyle.Color = _style.Grid;
        plot.Axes.SetLimits(xMin, xMax, yMin, yMax);
        var cursor = plot.Add.VerticalLine(xMin, 1, _style.Text);
        cursor.IsVisible = false;
        _cursors.Add(cursor);
        control.Refresh();
    }

    private void HoverTimeline(AvaPlot control, PointerEventArgs e)
    {
        if (_timeline is not { Times.Count: > 0 } timeline)
            return;
        var position = e.GetPosition(control);
        var scale = control.DisplayScale;
        var x = control.Plot.GetCoordinates(new Pixel((float)(position.X * scale), (float)(position.Y * scale))).X;
        var time = new DateTimeOffset(DateTime.FromOADate(x));
        var i = (int)Math.Floor((time - timeline.From) / timeline.Bucket);
        if (i < 0 || i >= timeline.Times.Count)
        {
            HideCursor();
            return;
        }

        foreach (var cursor in _cursors)
        {
            cursor.X = x;
            cursor.IsVisible = true;
        }
        foreach (var plot in new[] { SignalPlot, RatePlot, LatencyPlot, LossPlot })
            plot.Refresh();

        string Loss(double? percent) => percent is { } p ? $", {Format.Percent(p)} lost" : "";
        var bucketText = timeline.Bucket > TimeSpan.FromSeconds(2) ? $" (average over {Format.Span(timeline.Bucket)})" : "";
        HoverText.Text = timeline.SignalDbm[i] is null && timeline.RouterMs[i] is null && timeline.RouterLossPercent[i] is null
            ? $"{Local(timeline.Times[i]):ddd HH:mm:ss}: nothing measured (asleep, or the collector was off)"
            : $"{Local(timeline.Times[i]):ddd HH:mm:ss}{bucketText} · signal {Dbm(timeline.SignalDbm[i])} · link {Mbit(timeline.TxMbps[i])} · " +
              $"router {Format.Ms(timeline.RouterMs[i])}{Loss(timeline.RouterLossPercent[i])} · internet {Format.Ms(timeline.InternetMs[i])}{Loss(timeline.InternetLossPercent[i])}";
    }

    private void HideCursor()
    {
        if (_cursors.All(c => !c.IsVisible))
            return;
        foreach (var cursor in _cursors)
            cursor.IsVisible = false;
        foreach (var plot in new[] { SignalPlot, RatePlot, LatencyPlot, LossPlot })
            plot.Refresh();
    }

    // ---- networks ---------------------------------------------------------------------------------

    private void ShowNetworks(IReadOnlyList<ScanSample> scans, IReadOnlyDictionary<string, DateTimeOffset> firstHeard, WifiSample? wifi, DateTimeOffset now)
    {
        var heard = Neighbourhood.Summarise(scans);
        var makers = _vendors.LookupAll(heard.Select(n => n.Bssid));
        var connected = heard.FirstOrDefault(n => n.Bssid == wifi?.Bssid) ?? heard.FirstOrDefault(n => n.Associated);
        var ownSsid = wifi?.Ssid ?? connected?.Ssid;
        var ownBssids = heard.Where(n => n.Ssid is not null && n.Ssid == ownSsid).Select(n => n.Bssid).ToList();
        // Yours: the same name, or a hidden network on the same device (mesh backhaul), whose address differs only at the ends.
        bool Yours(HeardNetwork n) => (n.Ssid is not null && n.Ssid == ownSsid) ||
            (n.Ssid is null && ownBssids.Any(b => b.Length == 17 && n.Bssid.Length == 17 && b[3..14] == n.Bssid[3..14]));
        var home = wifi is { Connected: true, FrequencyMhz: { } freq } ? (Band: WifiBand.Name(freq), Channel: WifiBand.Channel(freq)) : default;

        var scanCount = scans.Select(s => s.Time).Distinct().Count();
        var sharing = wifi is { Connected: true, FrequencyMhz: { } f } ? Neighbourhood.SharingChannel(heard, f, ownSsid) : [];
        ScanText.Text = scanCount == 0
            ? "No scans in this range yet. The collector scans every 2 minutes while connected to Wi-Fi."
            : $"{heard.Count} networks in {scanCount} scans, last at {Local(scans[^1].Time):HH:mm:ss}. Signal is the median over the scans." +
              (sharing.Count > 0
                  ? $" {sharing.Count} other network{(sharing.Count == 1 ? "" : "s")} at −70 dBm or stronger share your channel ({home.Channel}): " +
                    string.Join(", ", sharing.Select(n => n.Ssid ?? "hidden")) + "."
                  : "");

        _dots.Clear();
        DrawChannels(Band24Plot, "2.4 GHz", 0, 14, heard, Yours, makers, connected);
        DrawChannels(Band5Plot, "5 GHz", 30, 170, heard, Yours, makers, connected);

        NetworkList.ItemsSource = heard
            .OrderBy(n => n.FrequencyMhz).ThenByDescending(n => n.MedianDbm ?? -200)
            .Select(n =>
            {
                var notes = new List<string>();
                if (n.Bssid == connected?.Bssid)
                    notes.Add("connected");
                else if (Yours(n))
                    notes.Add(n.Ssid is null ? "your network (hidden backhaul)" : "your network");
                else if (n.Band == home.Band && n.Channel == home.Channel)
                    notes.Add("shares your channel");
                if (firstHeard.TryGetValue(n.Bssid, out var first) && now - first < TimeSpan.FromDays(1) && first > firstHeard.Values.Min() + TimeSpan.FromHours(1))
                    notes.Add("new today");
                if (n.Ssid?.EndsWith(".b") == true || n.Ssid?.StartsWith("DIRECT-") == true)
                    notes.Add(n.Ssid.StartsWith("DIRECT-") ? "Wi-Fi Direct (printer, TV, …)" : "setup network (Chromecast / Google device)");
                return new NetworkRow(
                    n.Ssid ?? "(hidden)", n.Bssid, makers[n.Bssid] ?? "", n.Channel.ToString(), Dbm(n.MedianDbm), Dbm(n.StrongestDbm),
                    n.TimesHeard.ToString(), firstHeard.TryGetValue(n.Bssid, out var f2) ? When(f2, now) : "",
                    string.Join(" · ", notes), n.Bssid == connected?.Bssid ? FontWeight.SemiBold : FontWeight.Normal);
            })
            .ToList();
    }

    private void DrawChannels(AvaPlot control, string band, double xMin, double xMax, IReadOnlyList<HeardNetwork> heard,
        Func<HeardNetwork, bool> yours, IReadOnlyDictionary<string, string?> makers, HeardNetwork? connected)
    {
        _style.Reset(control);
        var plot = control.Plot;
        var networks = heard.Where(n => n.Band == band && n.MedianDbm is not null).ToList();
        foreach (var n in networks.OrderBy(yours)) // yours on top
        {
            var color = yours(n) ? _style.Series1 : _style.Series2;
            var isConnected = n.Bssid == connected?.Bssid;
            var dot = plot.Add.Marker(n.Channel, n.MedianDbm!.Value, MarkerShape.FilledCircle, isConnected ? 13 : 9, color);
            if (isConnected)
            {
                // A ring in the surface colour, then one in the series colour: the network you're on.
                plot.Add.Marker(n.Channel, n.MedianDbm.Value, MarkerShape.OpenCircle, 20, color).MarkerStyle.LineWidth = 2;
            }
            _dots.Add((control, n.Channel, n.MedianDbm.Value,
                $"{n.Ssid ?? "(hidden)"} · {n.Bssid} · {makers[n.Bssid] ?? "unknown maker"} · {band} ch {n.Channel} · " +
                $"{n.MedianDbm} dBm typical, {n.StrongestDbm} dBm strongest · heard in {n.TimesHeard} scans" + (isConnected ? " · connected" : "")));
        }
        // Names only on the loud ones, one per channel. A name that would run into the previous one moves up a line.
        var placed = new List<(double Channel, double Dbm, float OffsetY)>();
        var span = xMax - xMin;
        foreach (var group in networks.Where(n => n.MedianDbm >= -72).GroupBy(n => n.Channel).OrderBy(g => g.Key))
        {
            var loudest = group.OrderByDescending(n => n.MedianDbm).First();
            var text = $"{loudest.Ssid ?? "(hidden)"}{(group.Count() > 1 ? $" +{group.Count() - 1}" : "")}";
            var offsetY = -4f;
            while (placed.Any(p => Math.Abs(p.Channel - loudest.Channel) < span * 0.3 && Math.Abs(p.Dbm - loudest.MedianDbm!.Value) < 8 && p.OffsetY == offsetY))
                offsetY -= 15;
            placed.Add((loudest.Channel, loudest.MedianDbm!.Value, offsetY));

            var label = plot.Add.Text(text, loudest.Channel, loudest.MedianDbm!.Value);
            label.LabelFontColor = _style.Text;
            label.LabelFontSize = 11;
            // Near the right edge the name goes to the left of the dot, so it isn't cut off.
            var rightSide = loudest.Channel > xMin + span * 0.7;
            label.LabelAlignment = rightSide ? Alignment.LowerRight : Alignment.LowerLeft;
            label.OffsetX = rightSide ? -8 : 8;
            label.OffsetY = offsetY;
        }
        plot.Axes.SetLimits(xMin, xMax, -100, -25);
        plot.Axes.Bottom.Label.Text = $"{band} channel";
        plot.Axes.Bottom.Label.ForeColor = _style.Muted;
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Bottom.Label.Bold = false;
        control.Refresh();
    }

    private void HoverNetworks(AvaPlot control, PointerEventArgs e)
    {
        var position = e.GetPosition(control);
        var scale = control.DisplayScale;
        var pixel = new Pixel((float)(position.X * scale), (float)(position.Y * scale));
        var nearest = _dots.Where(d => d.Plot == control)
            .Select(d => (d.Text, Distance: Distance(control.Plot.GetPixel(new Coordinates(d.X, d.Y)), pixel)))
            .OrderBy(d => d.Distance)
            .FirstOrDefault();
        NetworkHoverText.Text = nearest.Text is not null && nearest.Distance < 24 * scale ? nearest.Text : "Point at a dot to see which network it is.";
    }

    private static double Distance(Pixel a, Pixel b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // ---- events -----------------------------------------------------------------------------------

    private void ShowEvents(IReadOnlyList<NetEvent> events)
    {
        var selected = (EventList.SelectedItem as EventRow)?.Time;
        var rows = events.Reverse().Take(500)
            .Select(e => new EventRow(e.Time, $"{Local(e.Time):ddd d MMM HH:mm:ss}", e.Kind, e.Detail))
            .ToList();
        if (_end is not null && selected is not null)
            return; // looking at an event: keep the list still
        EventList.ItemsSource = rows;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private void ApplyTheme()
    {
        _style = ActualThemeVariant == ThemeVariant.Dark ? ChartStyle.Dark : ChartStyle.Light;
        IBrush Brush(PlotColor c) => new SolidColorBrush(Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
        LatencyRouterSwatch.Background = LossRouterSwatch.Background = YoursSwatch.Background = Brush(_style.Series1);
        LatencyInternetSwatch.Background = LossInternetSwatch.Background = OthersSwatch.Background = Brush(_style.Series2);
        Reload();
    }

    private static double X(DateTimeOffset time) => time.LocalDateTime.ToOADate();

    private static DateTime Local(DateTimeOffset time) => time.LocalDateTime;

    private static string When(DateTimeOffset time, DateTimeOffset now) =>
        Local(time).Date == Local(now).Date ? $"today {Local(time):HH:mm}" : $"{Local(time):d MMM HH:mm}";

    private static string Dbm(double? dbm) => dbm is { } d ? $"{d:0} dBm" : "—";

    private static string Mbit(double? mbps) => mbps is { } m ? $"{m:0.#} Mbit/s" : "—";

    private static double Max(IEnumerable<double?> values) => values.Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty(0).Max();

    private static double Percentile(IEnumerable<double?> values, double p)
    {
        var sorted = values.Where(v => v is not null).Select(v => v!.Value).Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
    }

    private sealed class QuitCommand(MainWindow window) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => window.Quit();
    }
}
