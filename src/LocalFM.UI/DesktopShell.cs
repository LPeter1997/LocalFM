using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LocalFM.Core;
using LocalFM.Desktop;
using LocalFM.UI.ViewModels;
using LocalFM.UI.Views;

namespace LocalFM.UI;

/// <summary>
/// Everything that ties the app to the desktop: the tray icon, the miniplayer, keeping playback alive when the
/// window is closed, and media keys / "now playing" widgets (MPRIS on Linux). None of it is needed for playback,
/// so every part is optional and failures only get logged.
/// </summary>
public sealed class DesktopShell : IDisposable
{
    private const string CloseToTrayKey = "ui.closeToTray";

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private MiniPlayerWindow? _mini;
    private TrayIcon? _tray;
    private NativeMenuItem? _nowPlayingItem, _playPauseItem, _closeToTrayItem;
    private MprisSession? _mpris;
    private SleepGuard? _sleep;
    private bool _quitting, _disposed;

    public DesktopShell(IClassicDesktopStyleApplicationLifetime desktop, AppHost host, MainViewModel vm, MainWindow main)
    {
        _desktop = desktop;
        _host = host;
        _vm = vm;
        _main = main;
        CloseToTray = host.History.Get(CloseToTrayKey) == "1";

        // The app ends when the person quits, not when the last window hides.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var icon = MakeIcon();
        if (icon is not null) main.Icon = icon;
        main.Closing += OnMainClosing;
        main.Closed += (_, _) => { if (!_quitting) Quit(); };
        main.MiniPlayerRequested += ShowMini;

        if (Program.SnapshotPath is null)
        {
            CreateTray(icon);
            if (OperatingSystem.IsLinux()) _ = StartMprisAsync();
            // While music plays the computer does not fall asleep on its own.
            _sleep = new SleepGuard(host.Player);
        }
    }

    /// <summary>Closing the window keeps the music going in the tray (when enabled).</summary>
    public bool CloseToTray { get; private set; }

    // ------------------------------------------------------------------ windows

    public void ShowMain()
    {
        if (_mini is { } m) { _mini = null; m.Close(); }
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void ShowMini()
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerWindow { DataContext = _vm.NowPlaying, Icon = _main.Icon };
            _mini.Expand += ShowMain;
            _mini.AttachVisualizer(_host.Player, _vm);
            _mini.AttachLyrics(_vm.Lyrics);
        }
        _mini.Show();
        _mini.Activate();
        _main.Hide();
    }

    private void ToggleMain()
    {
        if (_main.IsVisible && _main.WindowState != WindowState.Minimized && _main.IsActive) _main.Hide();
        else ShowMain();
    }

    private void OnMainClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || !CloseToTray || _tray is null || Program.SnapshotPath is not null) return;
        e.Cancel = true;
        _main.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Log.Info("Shell", "Window closed to tray; playback continues");
        }
    }

    private bool _trayHintShown;

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _mini?.Close();
        _desktop.Shutdown();
    }

    // ------------------------------------------------------------------ tray

    private void CreateTray(WindowIcon? icon)
    {
        try
        {
            _nowPlayingItem = new NativeMenuItem("Nothing playing") { IsEnabled = false };
            _playPauseItem = new NativeMenuItem("Play");
            _playPauseItem.Click += (_, _) => _vm.NowPlaying.PlayPauseCommand.Execute("tray");
            var next = new NativeMenuItem("Next");
            next.Click += (_, _) => _vm.NowPlaying.NextCommand.Execute("tray");
            var prev = new NativeMenuItem("Previous");
            prev.Click += (_, _) => _vm.NowPlaying.PreviousCommand.Execute("tray");
            var show = new NativeMenuItem("Show LocalFM");
            show.Click += (_, _) => ShowMain();
            var mini = new NativeMenuItem("Miniplayer");
            mini.Click += (_, _) => ShowMini();
            _closeToTrayItem = new NativeMenuItem("Keep playing when the window is closed")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = CloseToTray,
            };
            _closeToTrayItem.Click += (_, _) => SetCloseToTray(!CloseToTray);
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => Quit();

            var menu = new NativeMenu();
            foreach (var item in new NativeMenuItemBase[]
                     {
                         _nowPlayingItem, new NativeMenuItemSeparator(), _playPauseItem, next, prev, new NativeMenuItemSeparator(),
                         show, mini, _closeToTrayItem, new NativeMenuItemSeparator(), quit,
                     })
                menu.Items.Add(item);

            _tray = new TrayIcon { Icon = icon, ToolTipText = "LocalFM", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ToggleMain();
            TrayIcon.SetIcons(Application.Current!, [_tray]);
            _vm.NowPlaying.PropertyChanged += OnNowPlayingChanged;
            UpdateTray();
        }
        catch (Exception ex)
        {
            _tray = null;
            Log.Warn("Shell", $"No tray icon: {ex.Message}");
        }
    }

    private void OnNowPlayingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.Title) or nameof(NowPlayingViewModel.Artist)
            or nameof(NowPlayingViewModel.IsPlaying) or nameof(NowPlayingViewModel.HasTrack))
            UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null) return;
        var np = _vm.NowPlaying;
        string line = np.HasTrack ? (np.Artist is { Length: > 0 } a ? $"{np.Title} — {a}" : np.Title) : "Nothing playing";
        _nowPlayingItem!.Header = line.Length > 60 ? line[..59] + "…" : line;
        _playPauseItem!.Header = np.IsPlaying ? "Pause" : "Play";
        _tray.ToolTipText = np.HasTrack ? $"LocalFM: {line}" : "LocalFM";
    }

    private void SetCloseToTray(bool on)
    {
        CloseToTray = on;
        if (_closeToTrayItem is { } i) i.IsChecked = on;
        _ = _host.History.SetAsync(CloseToTrayKey, on ? "1" : "0");
    }

    /// <summary>The app icon (Assets/icon.png, rendered from icon.svg).</summary>
    private static WindowIcon? MakeIcon()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LocalFM/Assets/icon.png"));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("Shell", $"No icon: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ MPRIS

    private async Task StartMprisAsync()
    {
        try
        {
            _mpris = await MprisSession.StartAsync(_host.Player,
                raise: () => Dispatcher.UIThread.Post(ShowMain),
                quit: () => Dispatcher.UIThread.Post(Quit)).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed)) { _mpris?.Dispose(); _mpris = null; }
        }
        catch (Exception ex) { Log.Warn("Shell", $"Media keys unavailable: {ex.Message}"); }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        _sleep?.Dispose();
        _sleep = null;
        _mpris?.Dispose();
        _mpris = null;
        if (_tray is { } t)
        {
            _vm.NowPlaying.PropertyChanged -= OnNowPlayingChanged;
            t.IsVisible = false;
            t.Dispose();
            _tray = null;
        }
    }
}
