using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;

using LocalFM.UI.Controls;

namespace LocalFM.UI.ViewModels;

public sealed record NavItem(string Name, Geometry? Icon, PageViewModel Page);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Stack<PageViewModel> _back = new();
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _libraryDebounce;
    private readonly DispatcherTimer _queueDebounce;
    private DispatcherTimer? _noticeTimer;

    public MainViewModel(AppHost host)
    {
        Host = host;
        Actions = new TrackActions(this);
        NowPlaying = new NowPlayingViewModel(host.Player);
        Theme = new AccentTheme();
        _visualizerMode = Enum.TryParse<VisualizerMode>(host.History.Get(VisualizerKey), out var mode) ? mode : VisualizerMode.Spectrum;
        _coverColours = host.History.Get(CoverColoursKey) != "0";
        if (!_coverColours) Theme.SetEnabled(false);
        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.CoverPath)) _ = Theme.CoverChangedAsync(NowPlaying.CoverPath);
        };
        Lyrics = new LyricsViewModel(host);
        Albums = new AlbumsViewModel(this);
        Queue = new QueueViewModel(this);
        Sources = new SourcesViewModel(this);
        Nav =
        [
            new("Albums", Icon("IcoAlbum"), Albums),
            new("Artists", Icon("IcoArtist"), new ArtistsViewModel(this)),
            new("Tracks", Icon("IcoTracks"), new TracksViewModel(this)),
            new("Playlists", Icon("IcoPlaylist"), new PlaylistsViewModel(this)),
            new("Queue", Icon("IcoQueue"), Queue),
            new("Stats", Icon("IcoStats"), new StatsViewModel(this)),
            new("Tidy up", Icon("IcoMagic"), new TidyViewModel(this)),
            new("Sound", Icon("IcoSliders"), new SoundViewModel(this)),
            new("Sources", Icon("IcoFolder"), Sources),
        ];
        _selectedNav = Nav[0];
        _currentPage = Albums;

        _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _searchDebounce!.Stop();
            if (CurrentPage.Searchable) _ = CurrentPage.RefreshAsync();
        });
        // The library changes in bursts while scanning; refresh the visible page at most every 1.5 s.
        _libraryDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
        {
            _libraryDebounce!.Stop();
            if (CurrentPage is not QueueViewModel) _ = CurrentPage.RefreshAsync();
            UpdateCounts();
        });
        _queueDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            _queueDebounce!.Stop();
            if (CurrentPage is QueueViewModel) _ = Queue.RefreshAsync();
        });

        host.Db.Writer.Committed += () => Dispatcher.UIThread.Post(() => { if (!_libraryDebounce.IsEnabled) _libraryDebounce.Start(); });
        host.Player.Changed += () => Dispatcher.UIThread.Post(() => { if (!_queueDebounce.IsEnabled) _queueDebounce.Start(); });
        host.Player.Error += msg => Dispatcher.UIThread.Post(() => Notify(msg));
        host.Scanner.Progress += p => Dispatcher.UIThread.Post(() =>
            ScanStatus = p.Done ? null : $"Scanning… {p.Processed:N0} / {p.Found:N0}");
    }

    public AppHost Host { get; }
    public TrackActions Actions { get; }
    public NowPlayingViewModel NowPlaying { get; }
    public AccentTheme Theme { get; }

    private const string VisualizerKey = "ui.visualizer";
    private const string CoverColoursKey = "ui.coverTheme";

    public IReadOnlyList<VisualizerMode> VisualizerModes { get; } = [VisualizerMode.Spectrum, VisualizerMode.Mirror, VisualizerMode.Wave, VisualizerMode.Off];

    /// <summary>The large now-playing view with the visualizer.</summary>
    [ObservableProperty] private bool _nowPlayingOpen;
    [ObservableProperty] private VisualizerMode _visualizerMode;
    /// <summary>Tint the app with the playing album's colours.</summary>
    [ObservableProperty] private bool _coverColours;

    [RelayCommand] private void ToggleNowPlaying() => NowPlayingOpen = !NowPlayingOpen;

    partial void OnVisualizerModeChanged(VisualizerMode value) => _ = Host.History.SetAsync(VisualizerKey, value.ToString());

    partial void OnCoverColoursChanged(bool value)
    {
        Theme.SetEnabled(value);
        _ = Host.History.SetAsync(CoverColoursKey, value ? "1" : "0");
    }
    public LyricsViewModel Lyrics { get; }
    public AlbumsViewModel Albums { get; }
    public QueueViewModel Queue { get; }
    public SourcesViewModel Sources { get; }
    public IReadOnlyList<NavItem> Nav { get; }
    public ObservableCollection<PlaylistSummary> PlaylistList { get; } = new();

    /// <summary>Set by the window: shows a folder picker and returns the chosen local path.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the window: asks where to save a file (suggested name); returns a local path.</summary>
    public Func<string, Task<string?>>? PickSaveFile { get; set; }

    /// <summary>Set by the window: asks for a knowledge base file to open; returns a local path.</summary>
    public Func<Task<string?>>? PickOpenFile { get; set; }

    /// <summary>Set by the window: opens a web link in the person's browser.</summary>
    public Action<string>? OpenUrl { get; set; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private PageViewModel _currentPage;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private string? _scanStatus;
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _canGoBack;

    public void Start()
    {
        if (Host.StartupWarning is { } w) Notify(w, TimeSpan.FromSeconds(15));
        _ = RefreshPlaylistsAsync();
        _ = CurrentPage.RefreshAsync();
        UpdateCounts();
        if (Sources_Empty()) Navigate(Nav[^1]);
    }

    private bool Sources_Empty() => Library.Sources.List(Host.Db).Count == 0;

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        _back.Clear();
        CanGoBack = false;
        Show(value.Page);
    }

    partial void OnSearchTextChanged(string? value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    public void Navigate(NavItem item) => SelectedNav = item;

    private void Show(PageViewModel page)
    {
        NowPlayingOpen = false; // going somewhere means leaving the now-playing view
        CurrentPage = page;
        _ = page.RefreshAsync();
    }

    private void Push(PageViewModel page)
    {
        _back.Push(CurrentPage);
        CanGoBack = true;
        Show(page);
    }

    [RelayCommand]
    private void Back()
    {
        if (_back.Count == 0) return;
        Show(_back.Pop());
        CanGoBack = _back.Count > 0;
    }

    public void ShowAlbum(AlbumSummary album, bool openMatch = false) => Push(new AlbumDetailViewModel(this, album, openMatch));

    /// <summary>Shows an album in place of the current page (the current one no longer exists, e.g. after a merge).</summary>
    public void ReplaceWithAlbum(AlbumSummary album) => Show(new AlbumDetailViewModel(this, album));
    public void ShowArtist(ArtistSummary artist) => Push(new AlbumsViewModel(this, artist));
    public void EditAlbum(Ulid releaseId, Ulid? focusTrack = null) => Push(new AlbumEditorViewModel(this, releaseId, focusTrack));

    public void ShowAlbumOf(TrackInfo track, bool openMatch = false)
    {
        if (Host.Queries.Album(track.ReleaseId) is { } album) ShowAlbum(album, openMatch);
    }

    /// <summary>Opens the track's album with the "move to another album" panel for that track.</summary>
    public void MoveTrack(TrackInfo track)
    {
        if (CurrentPage is AlbumDetailViewModel open && open.Album.ReleaseId == track.ReleaseId) { open.OpenMove(track); return; }
        if (Host.Queries.Album(track.ReleaseId) is not { } album) return;
        var page = new AlbumDetailViewModel(this, album);
        Push(page);
        page.OpenMove(track);
    }

    [RelayCommand]
    private void ShowNowPlayingAlbum()
    {
        if (Host.Player.Current?.Track is { } t) ShowAlbumOf(t);
    }

    /// <summary>The artist name in the player bar: that artist's page (the album artist when the credit is a combination).</summary>
    [RelayCommand]
    private void ShowNowPlayingArtist()
    {
        if (Host.Player.Current?.Track is not { } t) return;
        var album = Host.Queries.Album(t.ReleaseId);
        foreach (var name in new[] { t.Artist, album?.Artist })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (Host.Queries.Artists(name).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } artist)
            {
                ShowArtist(artist);
                return;
            }
        }
    }

    /// <summary>The now-playing "fetch data" button: the playing album, with the MusicBrainz match open.</summary>
    [RelayCommand]
    private void MatchNowPlaying()
    {
        if (Host.Player.Current?.Track is { } t) ShowAlbumOf(t, openMatch: true);
    }

    [RelayCommand] private void ToggleLyrics() => Lyrics.IsOpen = !Lyrics.IsOpen;

    public async Task RefreshPlaylistsAsync()
    {
        var list = await Task.Run(() => Host.Playlists.List());
        PlaylistList.Clear();
        foreach (var p in list) PlaylistList.Add(p);
    }

    public void Notify(string message, TimeSpan? duration = null)
    {
        Notice = message;
        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer(duration ?? TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            _noticeTimer?.Stop();
            Notice = null;
        });
        _noticeTimer.Start();
    }

    private void UpdateCounts() => _ = Task.Run(() =>
    {
        var (tracks, albums, artists) = Host.Queries.Counts();
        Dispatcher.UIThread.Post(() => Counts = $"{tracks:N0} tracks · {albums:N0} albums · {artists:N0} artists");
    });

    private static Geometry? Icon(string key) =>
        Application.Current!.TryGetResource(key, null, out var g) ? g as Geometry : null;
}
