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

    /// <summary>Library editing (match, edit, merge, move) is offered on this platform.</summary>
    public bool CanEdit => Main.Capabilities.Editing;

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
    [ObservableProperty] private MoveViewModel? _move;

    /// <summary>Other local albums that are this same album (see <see cref="LibraryQueries.OtherCopies"/>).</summary>
    public ObservableCollection<AlbumSummary> Copies { get; } = new();

    public override Task RefreshAsync() => Load(
        () => (Album: Host.Queries.Album(Album.ReleaseId), Tracks: Host.Queries.AlbumTracks(Album.ReleaseId), Copies: Host.Queries.OtherCopies(Album.ReleaseId)),
        r =>
        {
            if (r.Album is not null) Album = r.Album;
            Replace(Tracks, r.Tracks);
            Replace(Copies, r.Copies);
            MultiDisc = r.Tracks.Select(t => t.Disc).Distinct().Count() > 1;
            long ms = r.Tracks.Sum(t => t.DurationMs ?? 0);
            // Only the parts we know, so a missing year leaves no stray separators.
            Summary = string.Join("  ·  ", new[] { Year, Converters.Count(r.Tracks.Count, "track") + " · " + Converters.Time(TimeSpan.FromMilliseconds(ms)) }
                .Where(s => !string.IsNullOrEmpty(s)));
        });

    private void EnsureMatch()
    {
        if (Match is not null) return;
        // "Guns N' Roses - Civil War" by an uploader's channel: search for the song by the artist we already know.
        var known = Host.Queries.ArtistNames().GroupBy(Normalize.Key).ToDictionary(g => g.Key, g => g.First());
        var (title, artist) = Library.Edit.Cleanup.SplitArtistTitle(Album.Title, Album.Artist, n => known.GetValueOrDefault(Normalize.Key(n)))
            is { } split ? (split.Title, split.Artist) : (Album.Title, Album.Artist);
        Match = new MatchViewModel(Host, Album.ReleaseId, title, artist, AfterMatch, AfterSort);
        // An album without a real name is best found through its songs.
        string key = Normalize.Key(Album.Title);
        Match.Start(bySongs: key == Normalize.Key("[Unknown Album]") || key == Normalize.Key("Singles"));
    }

    /// <summary>Songs were sorted into their albums: stay here if this album still exists, else go where they went.</summary>
    private async Task AfterSort(Metadata.SortReport report)
    {
        if (report.Moved > 0) Main.Notify($"Moved {Converters.Count(report.Moved, "song")} to {(report.Albums.Count == 1 ? "its album" : $"{report.Albums.Count} albums")}");
        if (Host.Queries.Album(Album.ReleaseId) is not null) { await RefreshAsync(); return; }
        if (report.Albums.Select(Host.Queries.Album).FirstOrDefault(a => a is not null) is { } next) Main.ReplaceWithAlbum(next);
        else Main.BackCommand.Execute(null);
    }

    private async Task AfterMatch(Library.Enrich.ApplyResult result)
    {
        if (result.MergedInto is { } into && Host.Queries.Album(into) is { } joined) Main.ReplaceWithAlbum(joined);
        else await RefreshAsync();
    }

    /// <summary>Opens the panel for moving songs to another album.</summary>
    public void OpenMove(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0) return;
        MatchOpen = false;
        Move = new MoveViewModel(Host, tracks, AfterMove, () => Move = null);
    }

    private async Task AfterMove(string where, IReadOnlyList<Ulid> albums)
    {
        int n = Move?.Tracks.Count ?? 0;
        string what = Move?.Tracks is [var one] ? $"“{one.Title}”" : Converters.Count(n, "song");
        Move = null;
        foreach (var a in albums) await RefineFromMatch(a);
        _ = Host.Player.RefreshMetadataAsync();
        Main.Notify($"Moved {what} {where}");
        if (Host.Queries.Album(Album.ReleaseId) is not null) await RefreshAsync();
        else if (albums.Select(Host.Queries.Album).FirstOrDefault(a => a is not null) is { } next) Main.ReplaceWithAlbum(next);
    }

    /// <summary>Songs moved onto a matched album take its numbering and credits (from the cached lookup).
    /// Best effort: offline, they simply keep what they had.</summary>
    private async Task RefineFromMatch(Ulid releaseId)
    {
        try { await Task.Run(() => Host.Metadata.ReapplyMatchAsync(releaseId)); }
        catch (Exception ex) { Log.Warn("Album", $"Re-applying the match after a move failed: {ex.Message}"); }
    }

    /// <summary>Brings every track of another copy of this album onto this one; the other copy disappears.</summary>
    [RelayCommand]
    private async Task MergeCopy(AlbumSummary other)
    {
        try
        {
            int n = await Host.Db.Writer.Run((c, t) => Library.Edit.MetadataEditor.MergeRelease(new Tx(c, t), other.ReleaseId, Album.ReleaseId));
            await RefineFromMatch(Album.ReleaseId);
            _ = Host.Player.RefreshMetadataAsync();
            Main.Notify(n == 1 ? "Merged 1 track into this album" : $"Merged {n} tracks into this album");
        }
        catch (Exception ex)
        {
            Log.Error("Album", "Merge failed", ex);
            Main.Notify($"Merging failed: {ex.Message}");
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void ToggleMatch()
    {
        MatchOpen = !MatchOpen;
        if (MatchOpen) { Move = null; EnsureMatch(); }
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
        Summary = Converters.Count(list.Count, "track");
    });

    [RelayCommand] private void PlayFrom(TrackInfo track) => Host.Player.PlayTracks(Tracks.ToList(), Tracks.IndexOf(track));

    /// <summary>Plays what the list shows: the whole library, or what the search found.</summary>
    [RelayCommand] private void PlayAll() => Host.Player.PlayTracks(Tracks.ToList());

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Tracks.Count == 0) return;
        Host.Player.SetShuffle(true);
        Host.Player.PlayTracks(Tracks.OrderBy(_ => Random.Shared.Next()).ToList());
    }
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
        Summary = $"{Converters.Count(items.Count, "track")} · {Converters.Time(TimeSpan.FromMilliseconds(ms))} remaining";
        return Task.CompletedTask;
    }

    [RelayCommand] private void Play(QueueRow row) => Host.Player.PlayItem(row.Item.Id);
    [RelayCommand] private void Remove(QueueRow row) => Host.Player.RemoveItem(row.Item.Id);
    /// <summary>Drag and drop in the list.</summary>
    public void Move(int from, int to)
    {
        if (from == to) return;
        Host.Player.MoveItem(from, to);
        _ = RefreshAsync(); // right away, so the dropped row doesn't flash back to its old place
    }

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
        Main.Notify($"Saved playlist “{name}” ({Converters.Count(recordings.Count, "track")})");
    }
}

public sealed partial class SourcesViewModel(MainViewModel main) : PageViewModel(main)
{
    public UpdaterViewModel Updater => Main.Updater;

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

/// <summary>
/// Picks another album for one or more songs and moves them there: an album the songs are on according to
/// MusicBrainz (made if it is not in the library yet), an album already in the library, or a new one.
/// </summary>
public sealed partial class MoveViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Func<string, IReadOnlyList<Ulid>, Task> _moved;
    private readonly Action _close;
    private int _query;

    /// <param name="moved">Called with a description ("to Brothers in Arms") and the albums the songs ended up on.</param>
    public MoveViewModel(AppHost host, IReadOnlyList<TrackInfo> tracks, Func<string, IReadOnlyList<Ulid>, Task> moved, Action close)
    {
        _host = host;
        _moved = moved;
        _close = close;
        Tracks = tracks;
        // Start from the album's own title (finds its other copies), unless it has no real one.
        string album = tracks[0].Album ?? "";
        _search = Normalize.Key(album) is var key && (key == Normalize.Key("[Unknown Album]") || key == Normalize.Key("Singles")) ? "" : album;
        _newAlbumName = "";
        _ = Find();
        _ = FindOnline();
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }
    public string Heading => Tracks.Count == 1 ? $"Move “{Tracks[0].Title}” to another album" : $"Move {Tracks.Count} songs to another album";
    public string OnlineHeading => Tracks.Count == 1 ? "Albums with this song" : "Albums with these songs";
    public ObservableCollection<AlbumSummary> Results { get; } = new();
    public ObservableCollection<AlbumGuessRow> Online { get; } = new();

    [ObservableProperty] private string _search;
    [ObservableProperty] private AlbumSummary? _selected;
    [ObservableProperty] private AlbumGuessRow? _selectedOnline;
    [ObservableProperty] private string _newAlbumName;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _onlineStatus;

    partial void OnSearchChanged(string value) => _ = Find();

    // One choice at a time across the two lists.
    partial void OnSelectedChanged(AlbumSummary? value) { if (value is not null) SelectedOnline = null; }
    partial void OnSelectedOnlineChanged(AlbumGuessRow? value) { if (value is not null) Selected = null; }

    private Ulid CurrentAlbum => Tracks[0].ReleaseId;

    private async Task Find()
    {
        int query = ++_query;
        string text = Search;
        var list = await Task.Run(() => _host.Queries.Albums(text).Where(a => a.ReleaseId != CurrentAlbum).Take(60).ToList());
        if (query != _query) return; // a newer search is on its way
        bool hadSelection = Selected is not null;
        Results.Clear();
        foreach (var a in list) Results.Add(a);
        if (hadSelection || SelectedOnline is null) Selected = Results.FirstOrDefault();
        Status = list.Count == 0 ? "No other album in your library matches. Try part of the title or the artist." : null;
    }

    private async Task FindOnline()
    {
        if (!_host.Metadata.OnlineEnabled) { OnlineStatus = "Online lookups are off (Sources page)."; return; }
        OnlineStatus = "Looking up the albums on MusicBrainz…";
        try
        {
            var ids = Tracks.Select(t => t.TrackId).ToList();
            var guesses = await Task.Run(async () =>
            {
                var songs = _host.Metadata.Albums.Describe(ids);
                return await _host.Metadata.Albums.FindAsync(songs);
            });
            foreach (var g in LocalFM.Metadata.AlbumFinder.Rank(guesses).Where(g => g.LocalReleaseId != CurrentAlbum).Take(12)) Online.Add(new AlbumGuessRow(g, Tracks.Count));
            OnlineStatus = Online.Count == 0 ? "MusicBrainz knows no other album with this song." : null;
            // The original album is the likely answer; prefer it over the first local search hit.
            if (Online.Count > 0 && Online[0].Guess is { Secondary: false }) SelectedOnline = Online[0];
        }
        catch (Exception ex)
        {
            OnlineStatus = "The lookup failed.";
            Log.Warn("Album", $"Song lookup for a move failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (Busy) return;
        var ids = Tracks.Select(t => t.TrackId).ToList();
        Busy = true;
        try
        {
            if (SelectedOnline is { } online)
            {
                Status = "Moving and fetching the album…";
                var report = await Task.Run(() => _host.Metadata.Organizer.SortAsync([online.Guess with { Songs = ids }]));
                await _moved($"to {online.Title}", report.Albums);
            }
            else if (Selected is { } target)
            {
                await _host.Db.Writer.Run((c, t) => Library.Edit.MetadataEditor.MoveTracks(new Tx(c, t), ids, target.ReleaseId));
                await _moved($"to {target.Title}", [target.ReleaseId]);
            }
        }
        catch (Exception ex)
        {
            Status = $"Moving failed: {ex.Message}";
            Log.Error("Album", "Move failed", ex);
        }
        finally { Busy = false; }
    }

    /// <summary>Moves the songs to a new album with the typed name (by the songs' artist).</summary>
    [RelayCommand]
    private async Task CreateNew()
    {
        string name = NewAlbumName.Trim();
        if (name.Length == 0 || Busy) return;
        var ids = Tracks.Select(t => t.TrackId).ToList();
        string? artist = Tracks[0].Artist;
        Busy = true;
        try
        {
            var album = await _host.Db.Writer.Run((c, t) =>
            {
                var tx = new Tx(c, t);
                var id = Library.Edit.MetadataEditor.NewAlbum(tx, name, artist);
                Library.Edit.MetadataEditor.MoveTracks(tx, ids, id);
                return id;
            });
            await _moved($"to the new album {name}", [album]);
        }
        catch (Exception ex)
        {
            Status = $"Moving failed: {ex.Message}";
            Log.Error("Album", "Move to a new album failed", ex);
        }
        finally { Busy = false; }
    }

    [RelayCommand] private void Cancel() => _close();
}
