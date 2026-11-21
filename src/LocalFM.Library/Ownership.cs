using System.Text.RegularExpressions;
using LocalFM.Library.Edit;

namespace LocalFM.Library;

public enum OwnedKind { None, OtherVersion, Likely, Owned }

/// <summary>What the library already has of a song: how sure we are, and where it is ("Love at First Sting").</summary>
public sealed record OwnedMatch(OwnedKind Kind, string? Album = null, string? Title = null);

/// <summary>
/// "Do I already have this?" for import previews, by artist and song title, with no file or network access.
/// A song with the same title and artist is likely the one you own when the album or the length agrees; a live
/// take, a re-recording on another album ("Comeblack") or a length several seconds off is another version.
/// Load once per preview: it reads every present track (a single query), then answers from memory.
/// </summary>
public sealed partial class Ownership
{
    /// <summary>Lengths within this are taken as the same recording (encoders and silence trimming differ a little).</summary>
    private static readonly long SameLengthMs = 4000;

    private sealed record Owned(string Title, string VersionKey, string? Album, string AlbumKey, long? LengthMs, HashSet<string> ArtistKeys);

    private readonly Dictionary<string, List<Owned>> _bySong = new(StringComparer.Ordinal);

    private Ownership() { }

    public static Ownership Load(Database db)
    {
        var o = new Ownership();
        using var c = db.Open();
        var rows = c.Query("""
            SELECT t.title, rel.title, COALESCE(t.length_ms, r.length_ms,
                       (SELECT MAX(f.duration_ms) FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0)),
                   ac.display_name,
                   (SELECT group_concat(a.name, char(31)) FROM artist_credit_name n JOIN artist a ON a.id = n.artist_id
                    WHERE n.credit_id = ac.id),
                   rac.display_name
            FROM track t
            JOIN recording r ON r.id = t.recording_id
            JOIN medium m ON m.id = t.medium_id
            JOIN release rel ON rel.id = m.release_id
            LEFT JOIN artist_credit ac ON ac.id = COALESCE(t.artist_credit_id, r.artist_credit_id)
            LEFT JOIN artist_credit rac ON rac.id = rel.artist_credit_id
            WHERE EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0)
            """, r => (Title: r.GetString(0), Album: r.Str(1), Length: r.Long(2), Credit: r.Str(3), Names: r.Str(4), AlbumArtist: r.Str(5)));
        foreach (var row in rows)
        {
            var artists = new HashSet<string>(StringComparer.Ordinal);
            AddArtistKeys(artists, row.Credit);
            foreach (var n in (row.Names ?? "").Split('\u001f', StringSplitOptions.RemoveEmptyEntries)) AddArtistKeys(artists, n);
            AddArtistKeys(artists, row.AlbumArtist);
            if (artists.Count == 0) continue;
            string song = SongKey(row.Title);
            if (song.Length == 0) continue;
            if (!o._bySong.TryGetValue(song, out var list)) o._bySong[song] = list = [];
            list.Add(new Owned(row.Title, VersionKey(row.Title), row.Album, Normalize.Key(row.Album), row.Length, artists));
        }
        return o;
    }

    /// <summary>Matches one candidate. <paramref name="album"/> and <paramref name="lengthMs"/> are optional.</summary>
    public OwnedMatch Find(string? artist, string title, string? album, long? lengthMs)
    {
        if (string.IsNullOrWhiteSpace(artist) || !_bySong.TryGetValue(SongKey(title), out var list)) return new(OwnedKind.None);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        AddArtistKeys(wanted, artist);
        var mine = list.Where(o => o.ArtistKeys.Overlaps(wanted)).ToList();
        if (mine.Count == 0) return new(OwnedKind.None);

        string version = VersionKey(title);
        string albumKey = Normalize.Key(album);
        bool CloseLength(Owned o) => lengthMs is null || o.LengthMs is null || Math.Abs(o.LengthMs.Value - lengthMs.Value) <= SameLengthMs;

        // Same title (qualifiers included) and the album agrees, or one side has no album and the length agrees.
        var same = mine.FirstOrDefault(o => o.VersionKey == version && albumKey.Length > 0 && o.AlbumKey == albumKey)
            ?? mine.FirstOrDefault(o => o.VersionKey == version && (albumKey.Length == 0 || o.AlbumKey.Length == 0) && CloseLength(o));
        if (same is not null) return new(OwnedKind.Likely, same.Album, same.Title);

        // Otherwise prefer naming the closest other version: same title first, then the shortest one (the plain studio take).
        var other = mine.OrderBy(o => o.VersionKey == version ? 0 : 1).ThenBy(o => o.Title.Length).First();
        return new(OwnedKind.OtherVersion, other.Album, other.Title);
    }

    /// <summary>The song itself: "Rock You Like a Hurricane (Live at Wacken) [feat. X]" → "rock you like a hurricane".</summary>
    internal static string SongKey(string title)
    {
        string s = Cleanup.CleanTitle(title, null)?.Text ?? title;
        for (string prev = ""; prev != s;)
        {
            prev = s;
            s = TrailingQualifier().Replace(s, "").Trim();
        }
        if (s.Length == 0) s = title;
        string key = Normalize.Key(s);
        return key.StartsWith("the ", StringComparison.Ordinal) && key.Length > 4 ? key[4..] : key;
    }

    /// <summary>The song plus what sets a version apart ("live", "acoustic"), ignoring remaster notes and featured artists.</summary>
    internal static string VersionKey(string title)
    {
        string s = Cleanup.CleanTitle(title, null)?.Text ?? title;
        s = Featuring().Replace(s, "");
        string key = Normalize.Key(s);
        return key.StartsWith("the ", StringComparison.Ordinal) && key.Length > 4 ? key[4..] : key;
    }

    private static void AddArtistKeys(HashSet<string> keys, string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        string clean = Cleanup.CleanArtist(artist)?.Text ?? artist;
        Add(clean);
        foreach (var part in Cleanup.SplitArtists(clean)) Add(part);

        void Add(string name)
        {
            string k = Normalize.Key(name);
            if (k.StartsWith("the ", StringComparison.Ordinal) && k.Length > 4) k = k[4..];
            if (k.Length > 0 && k != "various artists") keys.Add(k);
        }
    }

    /// <summary>A trailing "(...)", "[...]" or " - ..." part: versions, features, edits.</summary>
    [GeneratedRegex(@"\s*(?:[\(\[][^\(\)\[\]]*[\)\]]|\s[-–—]\s.*)$")]
    private static partial Regex TrailingQualifier();

    [GeneratedRegex(@"\s*[\(\[]\s*(?:feat\.?|featuring|ft\.?|with)\s[^\)\]]*[\)\]]|\s+(?:feat\.?|featuring|ft\.)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();
}
