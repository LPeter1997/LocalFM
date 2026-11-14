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
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm) vm.PickFolder = PickFolderAsync;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Program.SnapshotPath is { } path) _ = SnapshotAndExit(path);
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
                if (page == "album" && vm.Albums.Albums.FirstOrDefault() is { } a) vm.ShowAlbum(a);
                else if (vm.Nav.FirstOrDefault(n => n.Name.Equals(page, StringComparison.OrdinalIgnoreCase)) is { } nav) vm.Navigate(nav);
            }
            if (Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_PLAY") == "1" && Vm is { } v2)
            {
                await Task.Delay(500);
                if (v2.Albums.Albums.FirstOrDefault() is { } first) v2.Albums.PlayCommand.Execute(first);
            }
            await Task.Delay(2500); // let pages and covers load
            var root = (Visual?)this.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.VisualLayerManager>().FirstOrDefault() ?? (Visual)Content!;
            var visual = ElementComposition.GetElementVisual(root)!;
            using Bitmap bmp = await visual.Compositor.CreateCompositionVisualSnapshot(visual, RenderScaling);
            bmp.Save(path, PngBitmapEncoderOptions.Default);
            Console.WriteLine($"snapshot {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"snapshot failed: {ex}");
        }
        Dispatcher.UIThread.Post(Close);
    }
}
