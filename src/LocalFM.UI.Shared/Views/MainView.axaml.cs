using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LocalFM.UI.ViewModels;

namespace LocalFM.UI.Views;

/// <summary>
/// The whole app inside one control: title row, navigation, pages, side panels and the player bar. The desktop's
/// window wraps it with its own chrome; Android shows it as the single view.
/// </summary>
public partial class MainView : UserControl
{
    private TopLevel? _topLevel;

    public MainView()
    {
        InitializeComponent();
        // The sliders follow playback until the user grabs one; the seek happens on release.
        foreach (var slider in new[] { SeekSlider, CompactSeekSlider })
        {
            slider.AddHandler(PointerPressedEvent, (_, _) => { if (Vm is { } vm) vm.NowPlaying.IsSeeking = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
            slider.AddHandler(PointerReleasedEvent, (_, _) => CommitSeek(slider), RoutingStrategies.Tunnel, handledEventsToo: true);
            slider.AddHandler(PointerCaptureLostEvent, (_, _) => CommitSeek(slider), RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        MiniButton.Click += (_, _) => MiniPlayerRequested?.Invoke();
        // Scroll over the volume icon or slider: 5% a notch (touchpads scroll in finer steps).
        VolumeArea.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (Vm is not { } vm) return;
            vm.NowPlaying.NudgeVolume(e.Delta.Y * 5);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // The mouse's back button goes back, like in a browser.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && Vm is { CanGoBack: true } vm)
            {
                vm.BackCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        BarTitle.Tapped += (_, _) => Vm?.ShowNowPlayingAlbumCommand.Execute(null);
        BarArtist.Tapped += (_, _) => Vm?.ShowNowPlayingArtistCommand.Execute(null);
        LyricsList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is LyricLineViewModel line) Vm?.Lyrics.SeekTo(line);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, e) => ApplySize(e.NewSize);
    }

    /// <summary>Below this width, or this height (a phone on its side), the phone layout is used: bottom bar, mini
    /// player, full-screen panels.</summary>
    public const double CompactWidth = 640, CompactHeight = 480;

    private void ApplySize(Size size)
    {
        double width = size.Width;
        bool compact = width < CompactWidth || size.Height < CompactHeight;
        Classes.Set("compact", compact);
        // Narrow windows: a slimmer lyrics panel and volume slider (styles), so the page and player bar still fit.
        Classes.Set("narrow", !compact && width < 1100);
        if (Vm is { } vm) vm.IsCompact = compact;
    }

    /// <summary>The row the desktop window drags by.</summary>
    public Control TitleRow => TitleBar;

    /// <summary>Where the desktop window puts its caption buttons.</summary>
    public ContentControl CaptionButtonSlot => CaptionSlot;

    /// <summary>The person asked to switch to the miniplayer.</summary>
    public event Action? MiniPlayerRequested;

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static readonly FilePickerFileType BundleType = new("LocalFM knowledge base") { Patterns = ["*.localfm-kb"] };

    private static readonly FilePickerFileType ImportType = new("Music and zips")
    {
        Patterns = ["*.zip", .. LocalFM.Library.Import.Scanner.AudioExtensions.Select(e => "*" + e)],
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Shortcuts work wherever focus is, also when nothing has it (keys then go to the top level).
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        if (_topLevel is not null) _topLevel.BackRequested += OnBackRequested;
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (Vm?.HandleSystemBack() == true) e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnKeyDownTunnel);
        if (_topLevel is not null) _topLevel.BackRequested -= OnBackRequested;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private static Task<string?> LocalPath(IStorageItem item) =>
        App.Platform?.LocalPathAsync(item) ?? Task.FromResult(item.TryGetLocalPath());

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        Classes.Set("touch", vm.Capabilities.Touch);
        if (Bounds.Width > 0) ApplySize(Bounds.Size);
        vm.PickFolder = async () =>
        {
            if (Storage is not { } storage) return null;
            var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add music folder", AllowMultiple = false });
            return result.Count > 0 ? await LocalPath(result[0]) : null;
        };
        vm.PickSaveFile = async name =>
        {
            if (Storage is not { } storage) return null;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export knowledge base", SuggestedFileName = name, DefaultExtension = "localfm-kb",
                FileTypeChoices = [BundleType],
            });
            return file?.TryGetLocalPath();
        };
        vm.PickOpenFile = async () =>
        {
            if (Storage is not { } storage) return null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import knowledge base", AllowMultiple = false, FileTypeFilter = [BundleType, FilePickerFileTypes.All],
            });
            return files.Count > 0 ? await LocalPath(files[0]) : null;
        };
        vm.PickImportFiles = async () =>
        {
            if (Storage is not { } storage) return [];
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import music", AllowMultiple = true, FileTypeFilter = [ImportType, FilePickerFileTypes.All],
            });
            return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        };
        vm.PickProgram = async () =>
        {
            if (Storage is not { } storage) return null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Locate yt-dlp", AllowMultiple = false });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
        vm.OpenUrl = url =>
        {
            // Only web links: the URLs come from online sources.
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        };
        vm.CopyText = async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        vm.Lyrics.CurrentLineChanged += ScrollLyricTo;
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

    private void CommitSeek(Slider slider)
    {
        if (Vm is not { } vm || !vm.NowPlaying.IsSeeking) return;
        vm.NowPlaying.IsSeeking = false;
        vm.NowPlaying.CommitSeek(slider.Value);
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
            if (e.Key == Key.Escape) { vm.SearchText = ""; TopLevel.GetTopLevel(this)?.Focus(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Space: vm.NowPlaying.PlayPauseCommand.Execute(null); break;
            case Key.MediaPlayPause: vm.NowPlaying.PlayPauseCommand.Execute(null); break;
            case Key.MediaNextTrack: vm.NowPlaying.NextCommand.Execute(null); break;
            case Key.MediaPreviousTrack: vm.NowPlaying.PreviousCommand.Execute(null); break;
            case Key.Up when ctrl: vm.NowPlaying.NudgeVolume(5); break;
            case Key.Down when ctrl: vm.NowPlaying.NudgeVolume(-5); break;
            case Key.VolumeMute: vm.NowPlaying.ToggleMuteCommand.Execute(null); break;
            case Key.Right when ctrl: vm.NowPlaying.NextCommand.Execute(null); break;
            case Key.Left when ctrl: vm.NowPlaying.PreviousCommand.Execute(null); break;
            case Key.Right: vm.NowPlaying.SeekBy(5); break;
            case Key.Left: vm.NowPlaying.SeekBy(-5); break;
            case Key.L: vm.ToggleLyricsCommand.Execute(null); break;
            case Key.V: vm.ToggleNowPlayingCommand.Execute(null); break;
            case Key.Escape when vm.Vibe.IsOpen: vm.Vibe.IsOpen = false; break;
            case Key.Escape when vm.Lyrics.IsOpen: vm.Lyrics.IsOpen = false; break;
            case Key.Escape when vm.NowPlayingOpen: vm.NowPlayingOpen = false; break;
            case Key.M when ctrl && vm.Capabilities.Miniplayer: MiniPlayerRequested?.Invoke(); break;
            default: return;
        }
        e.Handled = true;
    }
}
