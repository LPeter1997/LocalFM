using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;

using LocalFM.UI.Controls;
using LocalFM.UI.Updates;

namespace LocalFM.UI.ViewModels;

public sealed partial class NavItem(string name, Geometry? icon, PageViewModel page) : ObservableObject
{
    public string Name { get; } = name;
    public Geometry? Icon { get; } = icon;
    public PageViewModel Page { get; } = page;

    /// <summary>The page shown is this one (the phone's bottom bar highlights it).</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>A count shown as a red dot next to the name (fixes waiting in Tidy up); 0 hides it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBadge), nameof(BadgeText))] private int _badge;
    public bool HasBadge => Badge > 0;
    public string BadgeText => Badge > 99 ? "99+" : Badge.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Stack<PageViewModel> _back = new();
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _libraryDebounce;
    /// <summary>Each source reports its scan done; Tidy up looks again once they have settled.</summary>
    private readonly DispatcherTimer _tidyDebounce;
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
        Vibe = new VibeViewModel(this);
        Share = new ShareViewModel(this);
        Updater = new UpdaterViewModel(new UpdaterHost(this), CreateUpdateService(), AppInfo.Version);
        // One side panel at a time: lyrics, the vibe, or What's new.
        Lyrics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LyricsViewModel.IsOpen)) return;
            if (Lyrics.IsOpen) { Vibe.IsOpen = false; Updater.WhatsNewOpen = false; }
            OnPropertyChanged(nameof(SheetOpen));
        };
        Vibe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VibeViewModel.IsOpen))
            {
                if (Vibe.IsOpen) Updater.WhatsNewOpen = false;
                OnPropertyChanged(nameof(SheetOpen));
            }
            if (e.PropertyName == nameof(VibeViewModel.InVibe)) Updater.VibeChanged();
        };
        Updater.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UpdaterViewModel.WhatsNewOpen)) return;
            if (Updater.WhatsNewOpen) { Lyrics.IsOpen = false; Vibe.IsOpen = false; }
            OnPropertyChanged(nameof(SheetOpen));
        };
        host.Player.IgnoredWhileFollowing += () => Dispatcher.UIThread.Post(() =>
            Notify($"You're listening to {Host.Vibe.HostName ?? "someone"}'s vibe. Leave it to play your own music."));
        Albums = new AlbumsViewModel(this);
        Queue = new QueueViewModel(this);
        Sources = new SourcesViewModel(this);
        Tidy = new TidyViewModel(this);
        var caps = host.Capabilities;
        Nav =
        [
            new("Albums", Icon("IcoAlbum"), Albums),
            new("Artists", Icon("IcoArtist"), new ArtistsViewModel(this)),
            new("Tracks", Icon("IcoTracks"), new TracksViewModel(this)),
            new("Playlists", Icon("IcoPlaylist"), new PlaylistsViewModel(this)),
            new("Queue", Icon("IcoQueue"), Queue),
            new("Stats", Icon("IcoStats"), new StatsViewModel(this)),
            .. caps.Editing ? [new NavItem("Tidy up", Icon("IcoMagic"), Tidy)] : Array.Empty<NavItem>(),
            new("Sound", Icon("IcoSliders"), new SoundViewModel(this)),
            .. caps.Import ? [new NavItem("Import", Icon("IcoImport"), new ImportViewModel(this))] : Array.Empty<NavItem>(),
            new("Sources", Icon("IcoFolder"), Sources),
        ];
        _selectedNav = Nav[0];
        Nav[0].IsSelected = true;
        _currentPage = Albums;
        // The phone's bottom bar: the first four pages, then "More" for the rest.
        PrimaryNav = Nav.Take(4).ToList();
        More = new MoreViewModel(this, Nav.Skip(4).ToList());

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
        {
            ScanStatus = p.Done ? null : $"Scanning… {p.Processed:N0} / {p.Found:N0}";
            // New or changed songs may need tidying: look again in the background and count what is found.
            if (p.Done && Capabilities.Editing) { _tidyDebounce!.Stop(); _tidyDebounce.Start(); }
        });
        _tidyDebounce = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _tidyDebounce!.Stop();
            _ = Tidy.CheckInBackgroundAsync(CurrentPage == Tidy);
        });
        _tidyDebounce.Stop();
        if (Nav.FirstOrDefault(n => n.Page == Tidy) is { } tidyNav)
            Tidy.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TidyViewModel.FixCount)) tidyNav.Badge = Tidy.FixCount; };
    }

    public AppHost Host { get; }
    public PlatformCapabilities Capabilities => Host.Capabilities;
    public TidyViewModel Tidy { get; }
    public TrackActions Actions { get; }
    public NowPlayingViewModel NowPlaying { get; }
    public AccentTheme Theme { get; }

    private const string VisualizerKey = "ui.visualizer";
    private const string CoverColoursKey = "ui.coverTheme";

    public IReadOnlyList<VisualizerMode> VisualizerModes { get; } = [VisualizerMode.Spectrum, VisualizerMode.Mirror, VisualizerMode.Wave, VisualizerMode.Off];

    /// <summary>The large now-playing view with the visualizer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetOpen), nameof(WideVisualizerActive), nameof(CompactVisualizerActive))]
    private bool _nowPlayingOpen;

    /// <summary>Set by the view: a phone-sized layout (bottom bar, mini player, full-screen sheets).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetOpen), nameof(WideVisualizerActive), nameof(CompactVisualizerActive))]
    private bool _isCompact;

    /// <summary>On a phone: something covers the whole screen (now playing, lyrics, the vibe, What's new), so the
    /// title row, mini player and bottom bar step aside.</summary>
    public bool SheetOpen => IsCompact && (NowPlayingOpen || Lyrics.IsOpen || Vibe.IsOpen || Updater.WhatsNewOpen);

    public bool WideVisualizerActive => NowPlayingOpen && !IsCompact;
    public bool CompactVisualizerActive => NowPlayingOpen && IsCompact;

    /// <summary>The bottom bar's pages; the others are under <see cref="More"/>.</summary>
    public IReadOnlyList<NavItem> PrimaryNav { get; }
    public MoreViewModel More { get; }

    /// <summary>"More" is lit while it or one of its pages is shown.</summary>
    public bool MoreSelected => CurrentPage == More || (SelectedNav is { } n && !PrimaryNav.Contains(n) && CurrentPage == n.Page);

    [RelayCommand]
    private void ShowMore()
    {
        _back.Clear();
        CanGoBack = false;
        if (SelectedNav is { } old) old.IsSelected = false;
        SelectedNav = null;
        Show(More);
    }

    [RelayCommand]
    private void NavigateTo(NavItem item)
    {
        if (SelectedNav == item && CurrentPage != item.Page) { _back.Clear(); CanGoBack = false; Show(item.Page); return; }
        SelectedNav = item;
    }

    partial void OnCurrentPageChanged(PageViewModel value) => OnPropertyChanged(nameof(MoreSelected));
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
    public VibeViewModel Vibe { get; }
    public ShareViewModel Share { get; }
    public UpdaterViewModel Updater { get; }
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

    /// <summary>Set by the window: asks for zips or audio files to import; returns local paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickImportFiles { get; set; }

    /// <summary>Set by the window: asks for a program (the yt-dlp executable); returns a local path.</summary>
    public Func<Task<string?>>? PickProgram { get; set; }

    /// <summary>Set by the window: opens a web link in the person's browser.</summary>
    public Action<string>? OpenUrl { get; set; }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyText { get; set; }

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

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        var value = newValue;
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

    /// <summary>The system's back (Android's gesture or button): closes what is on top, then goes back a page.
    /// False when there is nothing left to close, so the system can leave the app.</summary>
    public bool HandleSystemBack()
    {
        if (Updater.WhatsNewOpen) Updater.WhatsNewOpen = false;
        else if (Vibe.IsOpen) Vibe.IsOpen = false;
        else if (Lyrics.IsOpen) Lyrics.IsOpen = false;
        else if (NowPlayingOpen) NowPlayingOpen = false;
        else if (CanGoBack) Back();
        // Like other phone apps: back from another tab goes to the first one before leaving.
        else if (CurrentPage != Nav[0].Page) Navigate(Nav[0]);
        else return false;
        return true;
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

    /// <summary>Opens the songs' album with the "move to another album" panel for them (songs of one album).</summary>
    public void MoveTracks(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;
        var release = tracks[0].ReleaseId;
        tracks = tracks.Where(t => t.ReleaseId == release).ToList();
        if (CurrentPage is AlbumDetailViewModel open && open.Album.ReleaseId == release) { open.OpenMove(tracks); return; }
        if (Host.Queries.Album(release) is not { } album) return;
        var page = new AlbumDetailViewModel(this, album);
        Push(page);
        page.OpenMove(tracks);
    }

    public void MoveTrack(TrackInfo track) => MoveTracks([track]);

    [RelayCommand]
    private void ShowNowPlayingAlbum()
    {
        if (Host.Player.IsFollowing) return;
        if (Host.Player.Current?.Track is { } t) ShowAlbumOf(t);
    }

    /// <summary>The artist name in the player bar: that artist's page (the album artist when the credit is a combination).</summary>
    [RelayCommand]
    private void ShowNowPlayingArtist()
    {
        if (Host.Player.IsFollowing || Host.Player.Current?.Track is not { } t) return;
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
        if (Host.Player.IsFollowing) return;
        if (Host.Player.Current?.Track is { } t) ShowAlbumOf(t, openMatch: true);
    }

    [RelayCommand] private void ToggleLyrics() => Lyrics.IsOpen = !Lyrics.IsOpen;

    /// <summary>The phone's now-playing screen: the queue page.</summary>
    [RelayCommand]
    private void OpenQueue()
    {
        NowPlayingOpen = false;
        if (Nav.FirstOrDefault(n => n.Page == Queue) is { } queue) NavigateTo(queue);
    }
    [RelayCommand] private void ToggleVibe() => Vibe.IsOpen = !Vibe.IsOpen;

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

    /// <summary>The platform's updater (Velopack or a release notice); snapshot runs get a canned update and no network.</summary>
    private static IUpdateService CreateUpdateService() =>
        // quit/restart press the real buttons (against LOCALFM_UPDATE_SOURCE): the local update round trip.
        Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_UPDATE") is { Length: > 0 } shot and not ("quit" or "restart")
            ? new SnapshotUpdateService(shot == "notice" ? UpdateMode.NoticeOnly : UpdateMode.InApp)
            : App.Platform?.CreateUpdateService() ?? new NoUpdateService();

    /// <summary>The app as the updater sees it.</summary>
    private sealed class UpdaterHost(MainViewModel main) : IUpdaterHost
    {
        public string? Get(string key) => main.Host.History.Get(key);
        public void Set(string key, string value) => _ = main.Host.History.SetAsync(key, value);
        public void Notify(string message) => main.Notify(message);
        public bool IsPlaying => main.Host.Player.State == LocalFM.Playback.PlaybackState.Playing;
        public bool InVibe => main.Host.Vibe.Role != LocalFM.Vibe.VibeRole.None;

        public async Task PrepareRestartAsync()
        {
            var player = main.Host.Player;
            bool playing = IsPlaying;
            player.Pause("update restart");
            player.SaveState(force: true);
            // The writer runs in order: once this is stored, so is the state above.
            await main.Host.History.SetAsync(UpdaterViewModel.ResumeKey, playing ? "1" : "");
        }

        public Task OpenUrlAsync(string url)
        {
            main.OpenUrl?.Invoke(url);
            return Task.CompletedTask;
        }
    }
}
