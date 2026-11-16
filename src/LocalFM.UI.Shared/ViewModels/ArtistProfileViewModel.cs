using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Metadata;

namespace LocalFM.UI.ViewModels;

public sealed class MemberRow(ArtistMember m, ArtistSummary? local)
{
    public string Name => m.Name;
    public string Detail => string.Join(" · ", new[] { m.Years, m.Roles }.Where(s => !string.IsNullOrEmpty(s)));
    public bool HasDetail => Detail.Length > 0;
    public bool Former => m.Ended;
    public ArtistSummary? Local { get; } = local;
    public bool InLibrary => Local is not null;
}

public sealed class CandidateRow(ArtistCandidate candidate)
{
    public ArtistCandidate Candidate => candidate;
    public string Name => candidate.Name;
    public string Detail => Describe(candidate);

    private static string Describe(ArtistCandidate c) => string.Join(" · ", new[]
    {
        c.Disambiguation, c.Type, c.Country,
        c.Begin is { Length: >= 4 } b ? (c.End is { Length: >= 4 } e ? $"{b[..4]}–{e[..4]}" : $"{b[..4]}–") : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// The profile header of an artist page: photo, facts, Wikipedia summary, members and links.
/// Fetched in the background (never blocking the page, never touching playback); artists with a known
/// MusicBrainz id are refreshed automatically, others on request.
/// </summary>
public sealed partial class ArtistProfileViewModel(MainViewModel main, ArtistSummary artist) : ObservableObject
{
    private bool _autoTried;
    private AppHost Host => main.Host;

    [ObservableProperty] private string? _photoPath;
    [ObservableProperty] private string? _facts;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _bio;
    [ObservableProperty] private string? _bioUrl;
    [ObservableProperty] private string? _bioSource;
    [ObservableProperty] private bool _bioExpanded;
    [ObservableProperty] private bool _hasProfile;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _lookupLabel = "Look up artist info";
    [ObservableProperty] private bool _showFormer;
    [ObservableProperty] private string? _formerLabel;

    private List<MemberRow> _allMembers = [];

    public ObservableCollection<string> Genres { get; } = new();
    public ObservableCollection<ArtistLink> Links { get; } = new();
    public ObservableCollection<MemberRow> Members { get; } = new();
    public ObservableCollection<MemberRow> MemberOf { get; } = new();
    public ObservableCollection<CandidateRow> Candidates { get; } = new();

    public bool HasMembers => Members.Count > 0;
    public bool HasMemberOf => MemberOf.Count > 0;
    public bool HasCandidates => Candidates.Count > 0;
    public bool HasGenres => Genres.Count > 0;
    public bool HasLinks => Links.Count > 0;
    public bool HasBio => Bio is not null;

    public async Task RefreshAsync()
    {
        var (view, local) = await Task.Run(() => (ArtistProfiles.Load(Host.Db, Host.Images, artist.ArtistId), Host.Queries.Artists()));
        if (view is null) return;
        Show(view, local);
        if (!_autoTried && Host.Metadata.ShouldAutoFetchProfile(view))
        {
            _autoTried = true;
            await Fetch(null, quiet: true);
        }
    }

    private void Show(ArtistProfileView v, List<ArtistSummary> local)
    {
        HasProfile = v.FetchedAt is not null;
        PhotoPath = v.PhotoPath;
        Facts = DescribeFacts(v);
        Description = v.Description is { } d && !string.Equals(d, v.Disambiguation, StringComparison.OrdinalIgnoreCase) ? Capitalize(d) : null;
        Bio = v.Bio;
        BioUrl = v.BioUrl;
        BioSource = v.BioSource is { } s ? $"Read more on {s}" : null;
        LookupLabel = HasProfile ? "Refresh artist info" : "Look up artist info";

        var byMbid = ArtistProfiles.LocalArtistsByMbid(Host.Db);
        var byId = local.ToDictionary(a => a.ArtistId);
        ArtistSummary? Local(ArtistMember m) => m.Mbid is { } id && byMbid.TryGetValue(id, out var aid) ? byId.GetValueOrDefault(aid) : null;
        Replace(Genres, v.Genres);
        Replace(Links, v.Links);
        _allMembers = v.Members.Select(m => new MemberRow(m, Local(m))).ToList();
        ShowMembers();
        Replace(MemberOf, v.MemberOf.Select(m => new MemberRow(m, Local(m))));
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(HasMemberOf));
        OnPropertyChanged(nameof(HasGenres));
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasBio));
    }

    private static string? DescribeFacts(ArtistProfileView v)
    {
        string? type = v.Type switch
        {
            "group" => "Band", "person" => "Artist", "orchestra" => "Orchestra", "choir" => "Choir", null => null,
            var t => Capitalize(t),
        };
        string? where = v.Area ?? v.Country;
        string? years = (Y(v.Begin), Y(v.End), v.Type) switch
        {
            (null, _, _) => null,
            ({ } b, null, "person") => $"born {b}",
            ({ } b, { } e, "person") => $"{b}–{e}",
            ({ } b, null, _) => $"since {b}",
            ({ } b, { } e, _) => $"{b}–{e}",
        };
        var parts = new[] { type, where, years }.Where(p => p is not null).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
        static string? Y(string? d) => d is { Length: >= 4 } ? d[..4] : null;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [RelayCommand] private Task LookUp() => Fetch(null, quiet: false);

    [RelayCommand] private Task Choose(CandidateRow row) => Fetch(row.Candidate.Mbid, quiet: false);

    [RelayCommand] private void ToggleBio() => BioExpanded = !BioExpanded;

    [RelayCommand]
    private void ToggleFormer()
    {
        ShowFormer = !ShowFormer;
        ShowMembers();
    }

    /// <summary>Current members always; former ones on request when there are many.</summary>
    private void ShowMembers()
    {
        int former = _allMembers.Count(m => m.Former);
        bool collapse = former > 4 && _allMembers.Count > 8;
        Replace(Members, collapse && !ShowFormer ? _allMembers.Where(m => !m.Former) : _allMembers);
        FormerLabel = !collapse ? null : ShowFormer ? "Hide former members" : $"Show {former} former members";
        OnPropertyChanged(nameof(HasMembers));
    }

    [RelayCommand]
    private void OpenMember(MemberRow row)
    {
        if (row.Local is { } a) main.ShowArtist(a);
    }

    [RelayCommand] private void OpenLink(string? url) { if (url is not null) main.OpenUrl?.Invoke(url); }

    private async Task Fetch(string? mbid, bool quiet)
    {
        if (Busy) return;
        Busy = true;
        if (!quiet) Status = "Looking up…";
        try
        {
            var result = await Task.Run(() => Host.Metadata.FetchArtistProfileAsync(artist.ArtistId, mbid));
            Replace(Candidates, result.Candidates.Select(c => new CandidateRow(c)));
            OnPropertyChanged(nameof(HasCandidates));
            Status = result.Updated ? null : quiet ? null : result.Message;
            if (result.Updated)
            {
                var (view, local) = await Task.Run(() => (ArtistProfiles.Load(Host.Db, Host.Images, artist.ArtistId), Host.Queries.Artists()));
                if (view is not null) Show(view, local);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Artist", $"Profile lookup failed: {ex.Message}");
            if (!quiet) Status = "The lookup failed. Check the connection and try again.";
        }
        finally { Busy = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
