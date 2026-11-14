using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;

namespace LocalFM.UI.ViewModels;

/// <summary>A page in the content area. Pages load their data off the UI thread.</summary>
public abstract partial class PageViewModel(MainViewModel main) : ObservableObject
{
    protected MainViewModel Main { get; } = main;
    protected AppHost Host => Main.Host;

    public abstract string Title { get; }

    /// <summary>Whether the toolbar search box applies to this page.</summary>
    public virtual bool Searchable => false;

    [ObservableProperty] private bool _isLoading;

    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <summary>Runs a query on the thread pool and applies the result on the UI thread.</summary>
    protected async Task Load<T>(Func<T> query, Action<T> apply)
    {
        IsLoading = true;
        try
        {
            var result = await Task.Run(query);
            apply(result);
        }
        catch (Exception ex)
        {
            Log.Error("UI", $"Loading {Title} failed", ex);
        }
        finally { IsLoading = false; }
    }

    protected static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}

public sealed partial class AlbumsViewModel(MainViewModel main, ArtistSummary? artist = null) : PageViewModel(main)
{
    public override string Title => artist?.Name ?? "Albums";
    public override bool Searchable => artist is null;
    public ArtistSummary? Artist => artist;
    public bool IsArtistPage => artist is not null;
    public ArtistProfileViewModel? Profile { get; } = artist is null ? null : new ArtistProfileViewModel(main, artist);
    public ObservableCollection<AlbumSummary> Albums { get; } = new();

    [ObservableProperty] private string _summary = "";

    public override Task RefreshAsync()
    {
        var albums = Load(
            () => Host.Queries.Albums(artist is null ? Main.SearchText : null, artist?.ArtistId),
            list =>
            {
                Replace(Albums, list);
                Summary = list.Count == 1 ? "1 album" : $"{list.Count} albums";
            });
        return Profile is null ? albums : Task.WhenAll(albums, Profile.RefreshAsync());
    }

    [RelayCommand] private void Open(AlbumSummary album) => Main.ShowAlbum(album);

    [RelayCommand]
    private async Task Play(AlbumSummary album)
    {
        var tracks = await Task.Run(() => Host.Queries.AlbumTracks(album.ReleaseId));
        Host.Player.PlayTracks(tracks);
    }

    [RelayCommand]
    private async Task PlayAll()
    {
        var all = new List<TrackInfo>();
        foreach (var a in Albums.ToList()) all.AddRange(await Task.Run(() => Host.Queries.AlbumTracks(a.ReleaseId)));
        Host.Player.PlayTracks(all);
    }
}

public sealed partial class AlbumDetailViewModel : PageViewModel
{
    public AlbumDetailViewModel(MainViewModel main, AlbumSummary album, bool openMatch = false) : base(main)
    {
        _album = album;
        _matchOpen = openMatch;
        if (openMatch) EnsureMatch();
    }

    public override string Title => Album.Title;
    public string? Year => Album.Date is { Length: >= 4 } d ? d[..4] : null;
    public ObservableCollection<TrackInfo> Tracks { get; } = new();
    public TrackActions Actions => Main.Actions;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Year))] private AlbumSummary _album;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _multiDisc;
    [ObservableProperty] private bool _matchOpen;
    [ObservableProperty] private MatchViewModel? _match;
    [ObservableProperty] private string? _coverStatus;

    public override Task RefreshAsync() => Load(
        () => (Album: Host.Queries.Album(Album.ReleaseId), Tracks: Host.Queries.AlbumTracks(Album.ReleaseId)),
        r =>
        {
            if (r.Album is not null) Album = r.Album;
            Replace(Tracks, r.Tracks);
            MultiDisc = r.Tracks.Select(t => t.Disc).Distinct().Count() > 1;
            long ms = r.Tracks.Sum(t => t.DurationMs ?? 0);
            Summary = $"{r.Tracks.Count} tracks · {Converters.Time(TimeSpan.FromMilliseconds(ms))}";
        });

    private void EnsureMatch()
    {
        if (Match is not null) return;
        Match = new MatchViewModel(Host, Album.ReleaseId, Album.Title, Album.Artist, RefreshAsync);
        Match.SearchCommand.Execute(null);
    }

    [RelayCommand]
    private void ToggleMatch()
    {
        MatchOpen = !MatchOpen;
        if (MatchOpen) EnsureMatch();
    }

    [RelayCommand]
    private async Task FetchCover()
    {
        CoverStatus = "Looking for a cover…";
        bool ok = await Task.Run(() => Host.Metadata.FetchCoverAsync(Album.ReleaseId));
        CoverStatus = ok ? null : "No cover found online.";
        await RefreshAsync();
    }

    [RelayCommand] private void Edit() => Main.EditAlbum(Album.ReleaseId);

    [RelayCommand] private void PlayAll() => Host.Player.PlayTracks(Tracks.ToList());

    [RelayCommand]
    private void ShuffleAll()
    {
        Host.Player.SetShuffle(true);
        Host.Player.PlayTracks(Tracks.OrderBy(_ => Random.Shared.Next()).ToList());
    }

    [RelayCommand] private void PlayFrom(TrackInfo track) => Host.Player.PlayTracks(Tracks.ToList(), Tracks.IndexOf(track));
    [RelayCommand] private void PlayAlbumNext() => Host.Player.PlayNext(Tracks.ToList());
    [RelayCommand] private void EnqueueAlbum() => Host.Player.Enqueue(Tracks.ToList());
}

public sealed partial class ArtistsViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Title => "Artists";
    public override bool Searchable => true;
    public ObservableCollection<ArtistSummary> Artists { get; } = new();

    public override Task RefreshAsync() => Load(() => Host.Queries.Artists(Main.SearchText), list => Replace(Artists, list));

    [RelayCommand] private void Open(ArtistSummary artist) => Main.ShowArtist(artist);
}

public sealed partial class TracksViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Title => "Tracks";
    public override bool Searchable => true;
    public ObservableCollection<TrackInfo> Tracks { get; } = new();
    public TrackActions Actions => Main.Actions;

    [ObservableProperty] private string _summary = "";

    public override Task RefreshAsync() => Load(() => Host.Queries.Tracks(Main.SearchText), list =>
    {
        Replace(Tracks, list);
        Summary = $"{list.Count} tracks";
    });

    [RelayCommand] private void PlayFrom(TrackInfo track) => Host.Player.PlayTracks(Tracks.ToList(), Tracks.IndexOf(track));
}

public sealed partial class PlaylistsViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Title => "Playlists";
    public ObservableCollection<PlaylistSummary> Playlists => Main.PlaylistList;
    public ObservableCollection<TrackInfo> Tracks { get; } = new();
    public TrackActions Actions => Main.Actions;

    [ObservableProperty] private PlaylistSummary? _selected;
    [ObservableProperty] private string _newName = "";

    public override async Task RefreshAsync()
    {
        await Main.RefreshPlaylistsAsync();
        Selected = Selected is null ? Playlists.FirstOrDefault() : Playlists.FirstOrDefault(p => p.Id == Selected.Id);
    }

    partial void OnSelectedChanged(PlaylistSummary? value)
    {
        if (value is null) { Tracks.Clear(); return; }
        _ = Load(() => Host.Queries.ByRecordings(Host.Playlists.Entries(value.Id)), list => Replace(Tracks, list));
    }

    [RelayCommand]
    private async Task Create()
    {
        if (string.IsNullOrWhiteSpace(NewName)) return;
        var id = await Host.Playlists.CreateAsync(NewName);
        NewName = "";
        await Main.RefreshPlaylistsAsync();
        Selected = Playlists.FirstOrDefault(p => p.Id == id);
    }

    [RelayCommand]
    private async Task Delete(PlaylistSummary playlist)
    {
        await Host.Playlists.DeleteAsync(playlist.Id);
        await RefreshAsync();
    }

    [RelayCommand] private void PlayAll() { if (Tracks.Count > 0) Host.Player.PlayTracks(Tracks.ToList()); }
    [RelayCommand] private void PlayFrom(TrackInfo track) => Host.Player.PlayTracks(Tracks.ToList(), Tracks.IndexOf(track));

    [RelayCommand]
    private async Task RemoveTrack(TrackInfo track)
    {
        if (Selected is not { } p) return;
        var remaining = Tracks.Where(t => t != track).Select(t => t.RecordingId).ToList();
        await Host.Playlists.SetEntriesAsync(p.Id, remaining);
        Tracks.Remove(track);
        await Main.RefreshPlaylistsAsync();
    }
}

/// <summary>Queue row: the item plus its place relative to what is playing.</summary>
public sealed record QueueRow(global::LocalFM.App.QueueItem Item, int Index, bool IsCurrent, bool IsPlayed)
{
    public TrackInfo Track => Item.Track;
    public double Opacity => IsPlayed ? 0.5 : 1;
}

public sealed partial class QueueViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Title => "Queue";
    public ObservableCollection<QueueRow> Rows { get; } = new();

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _playlistName = "";

    public override Task RefreshAsync()
    {
        var items = Host.Player.Snapshot(out int current);
        Replace(Rows, items.Select((it, i) => new QueueRow(it, i, i == current, i < current)));
        long ms = items.Skip(Math.Max(0, current)).Sum(i => i.Track.DurationMs ?? 0);
        Summary = $"{items.Count} tracks · {Converters.Time(TimeSpan.FromMilliseconds(ms))} remaining";
        return Task.CompletedTask;
    }

    [RelayCommand] private void Play(QueueRow row) => Host.Player.PlayItem(row.Item.Id);
    [RelayCommand] private void Remove(QueueRow row) => Host.Player.RemoveItem(row.Item.Id);
    [RelayCommand] private void MoveUp(QueueRow row) => Host.Player.MoveItem(row.Index, row.Index - 1);
    [RelayCommand] private void MoveDown(QueueRow row) => Host.Player.MoveItem(row.Index, row.Index + 1);
    [RelayCommand] private void Reshuffle() => Host.Player.Reshuffle();
    [RelayCommand] private void ClearUpcoming() => Host.Player.ClearUpcoming();

    [RelayCommand]
    private async Task SaveQueue()
    {
        var recs = Host.Player.Snapshot(out _).Select(i => i.Track.RecordingId).ToList();
        await SaveAs(recs, string.IsNullOrWhiteSpace(PlaylistName) ? $"Queue {DateTime.Now:yyyy-MM-dd HH:mm}" : PlaylistName);
    }

    [RelayCommand]
    private async Task SaveSession()
    {
        var recs = Host.Player.SessionHistory.Select(t => t.RecordingId).ToList();
        await SaveAs(recs, string.IsNullOrWhiteSpace(PlaylistName) ? $"Session {DateTime.Now:yyyy-MM-dd HH:mm}" : PlaylistName);
    }

    private async Task SaveAs(List<Ulid> recordings, string name)
    {
        if (recordings.Count == 0) return;
        await Host.Playlists.CreateAsync(name, recordings);
        PlaylistName = "";
        await Main.RefreshPlaylistsAsync();
        Main.Notify($"Saved playlist “{name}” ({recordings.Count} tracks)");
    }
}

public sealed partial class SourcesViewModel(MainViewModel main) : PageViewModel(main)
{
    public override string Title => "Sources";
    public ObservableCollection<Source> Sources { get; } = new();

    public string DataFolder => Host.Paths.Root;

    public bool OnlineEnabled
    {
        get => Host.Metadata.OnlineEnabled;
        set { Host.Metadata.OnlineEnabled = value; OnPropertyChanged(); }
    }

    public bool AutoFetchLyrics
    {
        get => Host.Metadata.AutoFetchLyrics;
        set { Host.Metadata.AutoFetchLyrics = value; OnPropertyChanged(); }
    }

    public bool EstimateWordTiming
    {
        get => Host.Metadata.EstimateWordTiming;
        set { Host.Metadata.EstimateWordTiming = value; OnPropertyChanged(); Main.Lyrics.Retime(); }
    }

    public override Task RefreshAsync() => Load(() => Library.Sources.List(Host.Db), list => Replace(Sources, list));

    [RelayCommand]
    private async Task AddFolder()
    {
        if (Main.PickFolder is null || await Main.PickFolder() is not { } path) return;
        var source = await Library.Sources.AddFolderAsync(Host.Db, path);
        await RefreshAsync();
        Host.Watcher.Watch(source);
        _ = Task.Run(() => Host.Scanner.ScanAsync(source));
    }

    [RelayCommand]
    private async Task Remove(Source source)
    {
        Host.Watcher.Unwatch(source.Id);
        await Library.Sources.RemoveAsync(Host.Db, source.Id);
        await RefreshAsync();
    }

    [RelayCommand] private void RescanAll() => _ = Task.Run(() => Host.Scanner.ScanAllAsync());

    // ------------------------------------------------------------------ knowledge base file

    [ObservableProperty] private bool _includeHistory = true;
    [ObservableProperty] private string? _bundleStatus;
    [ObservableProperty] private bool _bundleBusy;

    /// <summary>Writes everything LocalFM knows (not the music itself) to one file.</summary>
    [RelayCommand]
    private async Task ExportBundle()
    {
        if (Main.PickSaveFile is null || await Main.PickSaveFile($"LocalFM {DateTime.Now:yyyy-MM-dd}.localfm-kb") is not { } path) return;
        BundleBusy = true;
        BundleStatus = "Exporting…";
        try
        {
            bool history = IncludeHistory;
            var report = await Task.Run(() =>
            {
                string tmp = path + ".part";
                Library.Exchange.ExportReport r;
                using (var fs = File.Create(tmp))
                    r = Library.Exchange.KnowledgeBundle.Export(Host.Db, Host.Images, fs, new Library.Exchange.ExportOptions(IncludeHistory: history));
                File.Move(tmp, path, overwrite: true);
                return r;
            });
            BundleStatus = $"Saved {report.Albums:N0} albums, {report.Tracks:N0} tracks and {report.Images:N0} images" +
                           (history ? $" with {report.Plays:N0} plays" : "") + $" to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Log.Error("KB", "Export failed", ex);
            BundleStatus = $"Export failed: {ex.Message}";
        }
        finally { BundleBusy = false; }
    }

    /// <summary>Merges a knowledge base file into this library; its files attach when scans find matching audio.</summary>
    [RelayCommand]
    private async Task ImportBundle()
    {
        if (Main.PickOpenFile is null || await Main.PickOpenFile() is not { } path) return;
        BundleBusy = true;
        BundleStatus = "Importing…";
        try
        {
            var report = await Task.Run(async () =>
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                Library.Exchange.KnowledgeBundle.Validate(zip);
                return await Host.Db.Writer.Run((c, t) => Library.Exchange.KnowledgeBundle.Import(new Tx(c, t), Host.Images, zip));
            });
            BundleStatus = report.Describe();
            _ = Host.Player.RefreshMetadataAsync();
            _ = Main.RefreshPlaylistsAsync();
            if (report.FilesWaiting > 0) _ = Task.Run(() => Host.Scanner.ScanAllAsync());
        }
        catch (InvalidDataException ex) { BundleStatus = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("KB", "Import failed", ex);
            BundleStatus = $"Import failed: {ex.Message}";
        }
        finally { BundleBusy = false; }
    }
}
