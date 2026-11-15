using System.IO.Hashing;
using System.Text;
using LocalFM.Core;

namespace LocalFM.Library.Edit;

/// <summary>An album some songs are on, according to an online lookup of those songs.</summary>
/// <param name="Songs">The local tracks this guess covers (a subset of what was asked about).</param>
/// <param name="LocalReleaseId">The local album that already is this album (same release, or another edition of it).</param>
public sealed record AlbumGuess(
    string ReleaseMbid,
    string? GroupMbid,
    string Title,
    string Artist,
    string? Year,
    int TrackCount,
    string? PrimaryType,
    bool Secondary,
    IReadOnlyList<Ulid> Songs,
    Ulid? LocalReleaseId)
{
    /// <summary>How many releases of this album have the songs: the album a song really is from has many.</summary>
    public int Editions { get; init; } = 1;
}

public enum AlbumIssue { NotOnAlbum, SameAlbumTwice, BelongTogether, BelongsElsewhere, AlbumFound }

/// <summary>A proposed album fix in Tidy up: which songs, what is wrong, and where they should go.</summary>
public sealed record AlbumSuggestion(AlbumIssue Kind, string Summary, string Detail, IReadOnlyList<Ulid> Tracks)
{
    /// <summary>Same album twice: the copy that is folded into <see cref="MergeInto"/>.</summary>
    public Ulid? MergeFrom { get; init; }
    public Ulid? MergeInto { get; init; }
    /// <summary>Where the songs go (online suggestions).</summary>
    public AlbumGuess? Target { get; init; }

    public string KindLabel => Kind switch
    {
        AlbumIssue.NotOnAlbum => "Not on album",
        AlbumIssue.SameAlbumTwice => "Twice",
        AlbumIssue.BelongTogether => "Together",
        AlbumIssue.BelongsElsewhere => "Elsewhere",
        _ => "Album found",
    };

    /// <summary>Stable identity, so a dismissed suggestion stays dismissed until the songs or the target change.</summary>
    public string Key
    {
        get
        {
            string raw = $"{Kind}|{string.Join(",", Tracks.Select(t => t.ToString()).Order(StringComparer.Ordinal))}|{Target?.ReleaseMbid}|{MergeFrom}|{MergeInto}";
            return Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes(raw)));
        }
    }
}

/// <summary>
/// Album problems visible without going online: songs left on a matched album they are not part of, and the
/// same album twice. Songs a person put somewhere by hand (locked) are left alone.
/// </summary>
public static class AlbumChecks
{
    private const string DismissPrefix = "tidy.dismissed.";

    public static List<AlbumSuggestion> FindOffline(Database db)
    {
        using var c = db.Open();
        var list = new List<AlbumSuggestion>();

        // Songs on a MusicBrainz-matched album that the release did not account for.
        var strays = c.Query("""
            SELECT r.id, r.title, t.id, t.title FROM release r
            JOIN external_id e ON e.entity_type = 'release' AND e.entity_id = r.id AND e.provider = 'musicbrainz'
            JOIN medium m ON m.release_id = r.id JOIN track t ON t.medium_id = m.id
            WHERE NOT EXISTS (SELECT 1 FROM external_id x WHERE x.entity_type = 'track' AND x.entity_id = t.id AND x.provider = 'musicbrainz')
              AND NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type = 'track' AND p.entity_id = t.id AND p.field = 'medium_id' AND p.locked = 1)
              AND EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0)
            ORDER BY r.title, m.position, t.position
            """, r => (Release: r.Id(0), Album: r.GetString(1), Track: r.Id(2), Title: r.GetString(3)));
        foreach (var g in strays.GroupBy(s => (s.Release, s.Album)))
        {
            var titles = g.Select(s => s.Title).ToList();
            list.Add(new AlbumSuggestion(AlbumIssue.NotOnAlbum,
                $"{Count(titles.Count, "song")} {(titles.Count == 1 ? "isn't" : "aren't")} on “{g.Key.Album}”",
                Join(titles), g.Select(s => s.Track).ToList()));
        }

        // The same album twice: same title and artist (or the same MusicBrainz release), unless both are matched
        // to different editions on purpose.
        var albums = c.Query("""
            SELECT r.id, r.title, r.title_key, COALESCE(r.artist_credit_id, ''), ac.display_name,
                   (SELECT external_id FROM external_id e WHERE e.entity_type = 'release' AND e.entity_id = r.id AND e.provider = 'musicbrainz'),
                   (SELECT COUNT(*) FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = r.id),
                   r.created_at
            FROM release r LEFT JOIN artist_credit ac ON ac.id = r.artist_credit_id
            """, r => (Id: r.Id(0), Title: r.GetString(1), Key: r.GetString(2), Credit: r.GetString(3), Artist: r.Str(4),
                Mbid: r.Str(5), Tracks: (int)r.GetInt64(6), Created: r.GetInt64(7)));
        string unknownKey = Normalize.Key("[Unknown Album]"), singlesKey = Normalize.Key("Singles");
        var seen = new HashSet<Ulid>();
        foreach (var g in albums.Where(a => a.Tracks > 0 && a.Key.Length > 0 && a.Key != unknownKey && a.Key != singlesKey)
                     .GroupBy(a => (a.Key, a.Credit)).Concat(albums.Where(a => a.Mbid is not null).GroupBy(a => (Key: a.Mbid!, Credit: "mb"))))
        {
            var copies = g.Where(a => !seen.Contains(a.Id)).ToList();
            if (copies.Count < 2) continue;
            var into = copies.OrderByDescending(a => a.Tracks).ThenByDescending(a => a.Mbid is not null).ThenBy(a => a.Created).First();
            foreach (var from in copies.Where(a => a.Id != into.Id))
            {
                if (from.Mbid is not null && into.Mbid is not null && from.Mbid != into.Mbid) continue;
                seen.Add(from.Id);
                var tracks = c.Query("SELECT t.id FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1", r => r.Id(0), from.Id);
                list.Add(new AlbumSuggestion(AlbumIssue.SameAlbumTwice,
                    $"“{into.Title}”{(into.Artist is { } a ? $" by {a}" : "")} is in your library twice",
                    $"{Count(from.Tracks, "song")} would join the copy with {Count(into.Tracks, "song")}", tracks)
                    { MergeFrom = from.Id, MergeInto = into.Id });
            }
        }
        return list;
    }

    public static bool IsDismissed(History kv, AlbumSuggestion s) => kv.Get(DismissPrefix + s.Key) is not null;

    public static Task DismissAsync(History kv, AlbumSuggestion s) => kv.SetAsync(DismissPrefix + s.Key, "1");

    public static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>"A, B and 3 more".</summary>
    public static string Join(IReadOnlyList<string> titles, int show = 3) =>
        titles.Count <= show ? string.Join(", ", titles.Select(t => $"“{t}”"))
            : string.Join(", ", titles.Take(show).Select(t => $"“{t}”")) + $" and {titles.Count - show} more";
}
