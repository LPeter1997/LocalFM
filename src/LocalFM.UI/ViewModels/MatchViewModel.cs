using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library.Edit;
using LocalFM.Library.Enrich;
using LocalFM.Metadata;

namespace LocalFM.UI.ViewModels;

/// <summary>An album some songs are on (from a song lookup), as a row.</summary>
public sealed class AlbumGuessRow(AlbumGuess guess, int asked)
{
    public AlbumGuess Guess { get; } = guess;
    public string Title => Guess.Title;
    public string Artist => Guess.Artist;
    public string Facts => string.Join(" · ", new[]
    {
        Guess.Year, Guess.TrackCount > 0 ? Converters.Count(Guess.TrackCount, "track") : null,
        Guess.Secondary ? "compilation or live" : Guess.PrimaryType?.ToLowerInvariant() is "single" or "ep" ? Guess.PrimaryType : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
    public string Coverage => asked <= 1 ? "" : $"{Guess.Songs.Count} of {asked} songs";
    public bool InLibrary => Guess.LocalReleaseId is not null;
}

/// <summary>
/// Finds an album on MusicBrainz and applies it: by title (the edition list), or by songs (each song looked up,
/// grouped by the albums they are on). When the songs are on different albums they can be sorted in one go.
/// </summary>
public sealed partial class MatchViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Ulid _releaseId;
    private readonly Func<ApplyResult, Task> _afterApply;
    private readonly Func<SortReport, Task> _afterSort;
    private IReadOnlyList<Ulid>? _songScope;
    private List<AlbumGuess> _placements = [];

    public MatchViewModel(AppHost host, Ulid releaseId, string title, string? artist, Func<ApplyResult, Task> afterApply,
        Func<SortReport, Task> afterSort)
    {
        _host = host;
        _releaseId = releaseId;
        _afterApply = afterApply;
        _afterSort = afterSort;
        _title = title;
        _artist = artist;
    }

    public ObservableCollection<ReleaseCandidate> Candidates { get; } = new();
    public ObservableCollection<AlbumGuessRow> Guesses { get; } = new();
    public IReadOnlyList<string> Modes { get; } = ["By album title", "By songs"];

    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private ReleaseCandidate? _selected;
    [ObservableProperty] private AlbumGuessRow? _selectedGuess;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _fetchCover = true;
    /// <summary>0: by album title, 1: by songs.</summary>
    [ObservableProperty] private int _mode;
    [ObservableProperty] private string? _sortSummary;
    [ObservableProperty] private int _strayCount;

    public bool BySongs => Mode == 1;
    public bool ByTitle => Mode == 0;
    public bool CanSort => _placements.Count >= 2;
    public bool HasStrays => StrayCount > 0;
    private IReadOnlyList<Ulid> _strays = [];

    partial void OnModeChanged(int value)
    {
        OnPropertyChanged(nameof(BySongs));
        OnPropertyChanged(nameof(ByTitle));
        if (value == 1 && Guesses.Count == 0 && !Busy) _ = FindBySongs();
    }

    partial void OnStrayCountChanged(int value) => OnPropertyChanged(nameof(HasStrays));

    private void OnRetrying(string provider, TimeSpan delay) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Busy) Status = $"{provider} is busy, retrying in {delay.TotalSeconds:0.#} s…"; });

    /// <summary>Starts in the mode that fits: albums without a real name are found by their songs.</summary>
    public void Start(bool bySongs)
    {
        if (bySongs) Mode = 1; // runs the song lookup
        else SearchCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Search()
    {
        if (!_host.Metadata.OnlineEnabled) { Status = "Online lookups are turned off (Sources page)."; return; }
        Busy = true;
        Status = "Searching MusicBrainz…";
        _host.Metadata.Http.Retrying += OnRetrying;
        bool none = false;
        try
        {
            var q = await Task.Run(() => _host.Metadata.DescribeAlbum(_releaseId));
            var hits = await Task.Run(() => _host.Metadata.SearchAlbumAsync(q, Title, Artist));
            // Nothing for "Artist - Song" credited to an uploader: search again as the song by that artist.
            if (hits.Count == 0 && Cleanup.SplitArtistTitle(Title, Artist, null) is { } split)
            {
                var retry = await Task.Run(() => _host.Metadata.SearchAlbumAsync(q, split.Title, split.Artist));
                if (retry.Count > 0) { (Title, Artist, hits) = (split.Title, split.Artist, retry); }
            }
            Candidates.Clear();
            foreach (var h in hits) Candidates.Add(h);
            Selected = Candidates.FirstOrDefault();
            Status = hits.Count == 0 ? "Nothing found by that title." : $"{hits.Count} releases. The best fit for your {q.TrackCount} tracks is first.";
            none = hits.Count == 0;
        }
        catch (Exception ex)
        {
            Status = $"Search failed: {ex.Message}";
            Log.Warn("Match", "Search failed", ex);
        }
        finally { Busy = false; _host.Metadata.Http.Retrying -= OnRetrying; }
        if (none) Mode = 1; // the songs may still know where they belong
    }

    /// <summary>Looks each song up and lists the albums they are on.</summary>
    [RelayCommand]
    private async Task FindBySongs()
    {
        if (!_host.Metadata.OnlineEnabled) { Status = "Online lookups are turned off (Sources page)."; return; }
        Busy = true;
        _host.Metadata.Http.Retrying += OnRetrying;
        try
        {
            var songs = await Task.Run(() => _songScope is { } scope ? _host.Metadata.Albums.Describe(scope) : _host.Metadata.Albums.DescribeAlbum(_releaseId));
            Status = $"Looking up {Converters.Count(songs.Count, "song")}…";
            var progress = new Progress<int>(n => { if (Busy) Status = $"Looking up songs… {n} / {songs.Count}"; });
            var guesses = await Task.Run(() => _host.Metadata.Albums.FindAsync(songs, progress));
            _placements = AlbumFinder.Assign(guesses);
            Guesses.Clear();
            // The albums that place songs first (in placement order), then the other albums they are on.
            var placedIds = _placements.Select(p => p.ReleaseMbid).ToHashSet();
            foreach (var g in _placements.Select(p => guesses.First(x => x.ReleaseMbid == p.ReleaseMbid))
                         .Concat(guesses.Where(g => !placedIds.Contains(g.ReleaseMbid))).Take(40))
                Guesses.Add(new AlbumGuessRow(g, songs.Count));
            SelectedGuess = Guesses.FirstOrDefault();
            int placed = _placements.Sum(p => p.Songs.Count);
            SortSummary = _placements.Count >= 2
                ? "Your songs are on " + string.Join(", ", _placements.Select(p => $"“{p.Title}” ({p.Songs.Count})")) + "."
                : null;
            OnPropertyChanged(nameof(CanSort));
            Status = guesses.Count == 0 ? "MusicBrainz knows none of these songs. Check the titles and artist, or search by album title."
                : placed < songs.Count ? $"{Converters.Count(songs.Count - placed, "song")} could not be found."
                : null;
        }
        catch (Exception ex)
        {
            Status = $"Lookup failed: {ex.Message}";
            Log.Warn("Match", "Song lookup failed", ex);
        }
        finally { Busy = false; _host.Metadata.Http.Retrying -= OnRetrying; }
    }

    /// <summary>After an apply: look up only the songs that were not on the release.</summary>
    [RelayCommand]
    private Task FindTheirAlbums()
    {
        _songScope = _strays;
        StrayCount = 0;
        Guesses.Clear();
        if (Mode == 1) return FindBySongs();
        Mode = 1;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (Selected is not { } c) return;
        Busy = true;
        Status = FetchCover ? "Downloading release data and cover…" : "Downloading release data…";
        _host.Metadata.Http.Retrying += OnRetrying;
        try
        {
            var result = await Task.Run(() => _host.Metadata.ApplyReleaseAsync(_releaseId, c.Id, FetchCover));
            if (result is null) { Status = "MusicBrainz did not return that release. Try again later."; return; }
            if (!result.Applied)
            {
                Status = "None of your tracks fit this release (titles and lengths differ), so nothing was changed. Try another edition.";
                return;
            }
            _strays = result.UnmatchedTrackIds;
            StrayCount = _strays.Count;
            Status = result.MergedInto is not null ? "This edition was already in your library, so the songs joined that album." :
                     $"Applied: {result.TracksMatched} tracks matched" +
                     (result.TracksUnmatched > 0 ? $". {Converters.Count(result.TracksUnmatched, "song")} {(result.TracksUnmatched == 1 ? "isn't" : "aren't")} on this release" : "") +
                     (result.CoverStored ? ", cover saved" : "") + ".";
            await _afterApply(result);
            _ = _host.Player.RefreshMetadataAsync();
        }
        catch (Exception ex)
        {
            Status = $"Applying failed: {ex.Message}";
            Log.Warn("Match", "Apply failed", ex);
        }
        finally { Busy = false; _host.Metadata.Http.Retrying -= OnRetrying; }
    }

    /// <summary>Puts the songs the chosen album has on that album.</summary>
    [RelayCommand]
    private Task ApplyGuess() => SelectedGuess is { } g ? Sort([g.Guess]) : Task.CompletedTask;

    /// <summary>Puts every song on its own album.</summary>
    [RelayCommand]
    private Task SortAll() => Sort(_placements);

    private async Task Sort(IReadOnlyList<AlbumGuess> placements)
    {
        Busy = true;
        Status = "Sorting songs into their albums…";
        _host.Metadata.Http.Retrying += OnRetrying;
        try
        {
            bool cover = FetchCover;
            var report = await Task.Run(() => _host.Metadata.Organizer.SortAsync(placements, cover));
            Status = report.Problems.Count > 0 ? string.Join(" ", report.Problems) : null;
            Guesses.Clear();
            _placements = [];
            SortSummary = null;
            OnPropertyChanged(nameof(CanSort));
            await _afterSort(report);
            _ = _host.Player.RefreshMetadataAsync();
        }
        catch (Exception ex)
        {
            Status = $"Sorting failed: {ex.Message}";
            Log.Warn("Match", "Sort failed", ex);
        }
        finally { Busy = false; _host.Metadata.Http.Retrying -= OnRetrying; }
    }
}
