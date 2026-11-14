using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // The slider follows playback until the user grabs it; the seek happens on release.
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => { if (Vm is { } vm) vm.NowPlaying.IsSeeking = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => CommitSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, (_, _) => CommitSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        MiniButton.Click += (_, _) => MiniPlayerRequested?.Invoke();
        LyricsList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is LyricLineViewModel line) Vm?.Lyrics.SeekTo(line);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Keeps the sung line about a third from the top of the lyrics panel.</summary>
    private void ScrollLyricTo(int index)
    {
        if (index < 0 || LyricsList.ContainerFromIndex(index) is not Control c) return;
        var p = c.TranslatePoint(new Point(0, 0), LyricsList);
        if (p is null) return;
        double target = Math.Max(0, p.Value.Y - LyricsScroll.Viewport.Height / 3);
        LyricsScroll.Offset = new Vector(0, target);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>The person asked to switch to the miniplayer.</summary>
    public event Action? MiniPlayerRequested;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            vm.PickFolder = PickFolderAsync;
            vm.Lyrics.CurrentLineChanged += ScrollLyricTo;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Program.SnapshotPath is { } path)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_HEIGHT"), out int height)) Height = height;
            _ = SnapshotAndExit(path);
        }
    }

    private void CommitSeek()
    {
        if (Vm is not { } vm || !vm.NowPlaying.IsSeeking) return;
        vm.NowPlaying.IsSeeking = false;
        vm.NowPlaying.CommitSeek(SeekSlider.Value);
    }

    private async Task<string?> PickFolderAsync()
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add music folder", AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        bool typing = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl && e.Key == Key.F) { Search.Focus(); Search.SelectAll(); e.Handled = true; return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.Left) { vm.BackCommand.Execute(null); e.Handled = true; return; }
        if (typing)
        {
            if (e.Key == Key.Escape) { vm.SearchText = ""; Focus(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Space: vm.NowPlaying.PlayPauseCommand.Execute(null); break;
            case Key.MediaPlayPause: vm.NowPlaying.PlayPauseCommand.Execute(null); break;
            case Key.MediaNextTrack: vm.NowPlaying.NextCommand.Execute(null); break;
            case Key.MediaPreviousTrack: vm.NowPlaying.PreviousCommand.Execute(null); break;
            case Key.Right when ctrl: vm.NowPlaying.NextCommand.Execute(null); break;
            case Key.Left when ctrl: vm.NowPlaying.PreviousCommand.Execute(null); break;
            case Key.Right: vm.NowPlaying.SeekBy(5); break;
            case Key.Left: vm.NowPlaying.SeekBy(-5); break;
            case Key.L: vm.ToggleLyricsCommand.Execute(null); break;
            case Key.M when ctrl: MiniPlayerRequested?.Invoke(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ snapshot mode

    private async Task SnapshotAndExit(string path)
    {
        try
        {
            if (Vm is { } vm && Program.SnapshotPage is { } page)
            {
                await Task.Delay(800);
                if (page is "album" or "match" && vm.Albums.Albums.FirstOrDefault() is { } a) vm.ShowAlbum(a, openMatch: page == "match");
                else if (page == "edit" && vm.Albums.Albums.FirstOrDefault() is { } e) vm.EditAlbum(e.ReleaseId);
                else if (page == "mini") MiniPlayerRequested?.Invoke();
                else if (vm.Nav.FirstOrDefault(n => n.Name.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is { } nav) vm.Navigate(nav);
            }
            if (Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_PLAY") == "1" && Vm is { } v2)
            {
                await Task.Delay(500);
                if (v2.Albums.Albums.FirstOrDefault() is { } first) v2.Albums.PlayCommand.Execute(first);
            }
            if (Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_LYRICS") == "1" && Vm is { } v3) v3.Lyrics.IsOpen = true;
            int wait = int.TryParse(Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_WAIT"), out int w) ? w : 2500;
            await Task.Delay(wait); // let pages, covers and lookups finish
            if (double.TryParse(Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_SCROLL"), out double scroll)
                && this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(sv => sv.Extent.Height > sv.Viewport.Height + 1) is { } scroller)
            {
                scroller.Offset = new Vector(0, scroll);
                await Task.Delay(400);
            }
            // The miniplayer replaces this window when it is open.
            Window shown = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
                .Windows.FirstOrDefault(w => w is MiniPlayerWindow && w.IsVisible) ?? this;
            var root = (Visual?)shown.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.VisualLayerManager>().FirstOrDefault() ?? (Visual)shown.Content!;
            var visual = ElementComposition.GetElementVisual(root)!;
            using Bitmap bmp = await visual.Compositor.CreateCompositionVisualSnapshot(visual, shown.RenderScaling);
            bmp.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"snapshot {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"snapshot failed: {ex}");
        }
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var w in (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows.ToList() ?? [])
                if (w != this) w.Close();
            Close();
        });
    }
}
