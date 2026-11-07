using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenRouter.Client.App;

public sealed class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var database = Option(desktop.Args ?? [], "--db") ?? RunOptions.DefaultDatabasePath;
            _window = new MainWindow(database);
            _window.StateChanged += state =>
            {
                if (_tray is null)
                    return;
                _tray.Icon = TrayIcons.For(state.Level);
                _tray.ToolTipText = state.Summary;
            };
            _tray = CreateTray(desktop);
            // Closing the window keeps the app (and its tray icon) running; quit from the tray menu or with Ctrl+Q.
            _window.KeepRunningWhenClosed = _tray is not null;
            _window.QuitRequested += () => desktop.Shutdown();
            if (!(desktop.Args ?? []).Contains("--hidden"))
                _window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon? CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var open = new NativeMenuItem("Open netcheck");
            open.Click += (_, _) => ShowWindow();
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => desktop.Shutdown();
            var tray = new TrayIcon
            {
                Icon = TrayIcons.For(StatusLevel.Unknown),
                ToolTipText = "netcheck",
                Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } },
                IsVisible = true,
            };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, [tray]);
            return tray;
        }
        catch (Exception)
        {
            return null; // no tray on this desktop: the window is the app
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
