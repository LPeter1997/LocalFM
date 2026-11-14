using System.Text.RegularExpressions;
using LocalFM.Core;

namespace LocalFM.Library.Edit;

/// <summary>A proposed fix: one before/after shown to the person, possibly touching many rows (an artist on 40 tracks).</summary>
public sealed record CleanupSuggestion(
    string Kind, string Current, string Suggested, string Reason, string Context, IReadOnlyList<FieldChange> Changes);

/// <summary>
/// Spots the usual damage in tags: video-site artist names ("X - Topic", "XVEVO"), "(Official Video)" and similar,
/// the artist repeated in the title, "+" for spaces, video ids, remaster notes. Pure text rules plus a scan
/// that skips locked fields. Nothing is changed until the suggestions are applied.
/// </summary>
public static partial class Cleanup
{
    public sealed record Result(string Text, IReadOnlyList<string> Reasons);

    public static Result? CleanArtist(string name)
    {
        var reasons = new List<string>();
        string s = Common(name, reasons);
        string t = TopicSuffix().Replace(s, "");
        t = VevoSuffix().Replace(t, "");
        if (t != s && t.Length > 0) { reasons.Add("video channel name"); s = t; }
        return s == name.Trim() || s.Length == 0 ? null : new Result(s, reasons);
    }

    public static Result? CleanTitle(string title, string? artist, bool remasterNotes = true)
    {
        var reasons = new List<string>();
        string s = Common(title, reasons);

        string t = VideoTag().Replace(s, "");
        if (t != s) { reasons.Add("video tag"); s = t; }

        t = VideoId().Replace(s, "");
        if (t != s) { reasons.Add("video id"); s = t; }

        if (artist is not null && ArtistPrefix().Match(s) is { Success: true } m)
        {
            string prefix = m.Groups[1].Value;
            string cleanArtist = CleanArtist(artist)?.Text ?? artist;
            if (m.Groups[2].Value.Length > 0 &&
                (Normalize.Key(prefix) == Normalize.Key(artist) || Normalize.Key(prefix) == Normalize.Key(cleanArtist)))
            {
                s = m.Groups[2].Value;
                reasons.Add("artist repeated in title");
            }
        }

        if (remasterNotes)
        {
            t = RemasterBracket().Replace(s, "");
            t = RemasterDash().Replace(t, "");
            if (t != s && t.Trim().Length > 0) { reasons.Add("remaster note"); s = t; }
        }

        s = Spaces().Replace(s, " ").Trim().TrimEnd('-', '–', '—').Trim();
        return s == title.Trim() || s.Length == 0 ? null : new Result(s, reasons);
    }

    private static string Common(string s, List<string> reasons)
    {
        string t = s.Trim();
        // "The+Zoo": URL-encoded spaces from a download, only when there are no real spaces.
        if (!t.Contains(' ') && PlusBetweenWords().IsMatch(t))
        {
            t = t.Replace('+', ' ');
            reasons.Add("'+' instead of spaces");
        }
        string u = Spaces().Replace(t, " ").Trim();
        if (u != t && reasons.Count == 0) reasons.Add("spacing");
        return u;
    }

    /// <summary>Scans the library for fixable names. Locked fields are left alone.</summary>
    public static List<CleanupSuggestion> Find(Database db)
    {
        using var c = db.Open();
        var list = new List<CleanupSuggestion>();

        // Artists, grouped by credit: one suggestion per distinct name, applied to every track and album using it.
        var credits = c.Query("SELECT id, display_name FROM artist_credit", r => (Id: r.Id(0), Name: r.GetString(1)));
        foreach (var (creditId, name) in credits)
        {
            if (CleanArtist(name) is not { } fixedName) continue;
            var users = c.Query("""
                SELECT 'track', t.id FROM track t WHERE t.artist_credit_id = $1
                  AND NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type='track' AND p.entity_id=t.id AND p.field='artist_credit_id' AND p.locked=1)
                UNION ALL
                SELECT 'release', r.id FROM release r WHERE r.artist_credit_id = $1
                  AND NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type='release' AND p.entity_id=r.id AND p.field='artist_credit_id' AND p.locked=1)
                """, r => new FieldChange(r.GetString(0), r.Id(1), "artist", fixedName.Text), creditId);
            if (users.Count == 0) continue;
            int tracks = users.Count(u => u.EntityType == "track"), albumCount = users.Count - tracks;
            string ctx = string.Join(", ", new[] { Plural(tracks, "track"), Plural(albumCount, "album") }.Where(x => x.Length > 0));
            list.Add(new CleanupSuggestion("Artist", name, fixedName.Text, string.Join(", ", fixedName.Reasons), ctx, users));
        }

        var albums = c.Query("""
            SELECT r.id, r.title, ac.display_name FROM release r LEFT JOIN artist_credit ac ON ac.id = r.artist_credit_id
            WHERE NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type='release' AND p.entity_id=r.id AND p.field='title' AND p.locked=1)
            """, r => (Id: r.Id(0), Title: r.GetString(1), Artist: r.Str(2)));
        foreach (var a in albums)
            if (CleanTitle(a.Title, a.Artist) is { } fixedTitle)
                list.Add(new CleanupSuggestion("Album", a.Title, fixedTitle.Text, string.Join(", ", fixedTitle.Reasons), a.Artist ?? "",
                    [new FieldChange("release", a.Id, "title", fixedTitle.Text)]));

        var tracksRows = c.Query("""
            SELECT t.id, t.title, COALESCE(tac.display_name, rac.display_name), rel.title
            FROM track t JOIN medium m ON m.id = t.medium_id JOIN release rel ON rel.id = m.release_id
            LEFT JOIN artist_credit tac ON tac.id = t.artist_credit_id LEFT JOIN artist_credit rac ON rac.id = rel.artist_credit_id
            WHERE NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type='track' AND p.entity_id=t.id AND p.field='title' AND p.locked=1)
            ORDER BY rel.title, m.position, t.position
            """, r => (Id: r.Id(0), Title: r.GetString(1), Artist: r.Str(2), Album: r.GetString(3)));
        foreach (var t in tracksRows)
            if (CleanTitle(t.Title, t.Artist) is { } fixedTitle)
                list.Add(new CleanupSuggestion("Track", t.Title, fixedTitle.Text, string.Join(", ", fixedTitle.Reasons),
                    t.Artist is null ? t.Album : $"{t.Artist} · {t.Album}", [new FieldChange("track", t.Id, "title", fixedTitle.Text)]));
        return list;
    }

    private static string Plural(int n, string word) => n switch { 0 => "", 1 => $"1 {word}", _ => $"{n} {word}s" };

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicSuffix();

    [GeneratedRegex(@"(?<=\p{L})VEVO$")]
    private static partial Regex VevoSuffix();

    [GeneratedRegex(@"\s*[\(\[]\s*(?:official\s+)?(?:music\s+|lyrics?\s+|audio\s+|hd\s+|hq\s+|4k\s+)?(?:video|audio|visuali[sz]er|lyrics?|clip|videoclip)(?:\s+(?:hd|hq|4k))?\s*[\)\]]|\s*[\(\[]\s*(?:hd|hq|4k|explicit)\s*[\)\]]",
        RegexOptions.IgnoreCase)]
    private static partial Regex VideoTag();

    // YouTube ids as yt-dlp appends them: 11 characters, with a digit, '_' or '-' or mixed case to tell them from words.
    [GeneratedRegex(@"\s*\[(?=[A-Za-z0-9_-]{11}\])(?=[^\]]*(?:[0-9_-]|[a-z][A-Z]))[A-Za-z0-9_-]{11}\]")]
    private static partial Regex VideoId();

    [GeneratedRegex(@"^(.+?)\s*[-–—]\s+(.+)$")]
    private static partial Regex ArtistPrefix();

    [GeneratedRegex(@"\s*[\(\[]\s*(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?remaster(?:ed)?(?:\s+version)?(?:\s+\d{4})?\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex RemasterBracket();

    [GeneratedRegex(@"\s+[-–—]\s+(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?remaster(?:ed)?(?:\s+version)?(?:\s+\d{4})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RemasterDash();

    [GeneratedRegex(@"[\p{L}\p{N}\)\]-]\+[\p{L}\p{N}\(\[]")]
    private static partial Regex PlusBetweenWords();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
