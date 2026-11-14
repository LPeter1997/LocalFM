using LocalFM.Core;
using Microsoft.Data.Sqlite;

namespace LocalFM.Library;

public sealed record AlbumSummary(
    Ulid ReleaseId, Ulid ReleaseGroupId, string Title, string? Artist, string? Date, string? Edition,
    string? CoverPath, int TrackCount, int OwnedCount)
{
    /// <summary>The tile badge: "12", or "5/12" when only some of the album's tracks have a file.</summary>
    public string OwnedText => OwnedCount >= TrackCount ? $"{OwnedCount}" : $"{OwnedCount}/{TrackCount}";

    public string OwnedTip => OwnedCount >= TrackCount
        ? $"All {TrackCount} tracks are in your library"
        : $"{OwnedCount} of {TrackCount} tracks are in your library";
}

public sealed record ArtistSummary(Ulid ArtistId, string Name, string? SortName, int AlbumCount, int TrackCount, string? PhotoPath = null);

/// <summary>A playable track: everything the queue and now-playing view need, in one row.</summary>
public sealed record TrackInfo(
    Ulid TrackId,
    Ulid RecordingId,
    Ulid FileId,
    string Path,
    string Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    Ulid ReleaseId,
    string? Date,
    int Disc,
    int Position,
    long? DurationMs,
    string? CoverPath,
    string? Codec,
    double? TrackGainDb,
    double? TrackPeak,
    double? AlbumGainDb,
    double? AlbumPeak);

/// <summary>Read-only views over the knowledge base. Safe to call from any thread.</summary>
public sealed class LibraryQueries(Database db, ImageStore images)
{
    private const string CoverSql = """
        (SELECT i.rel_path FROM entity_image ei JOIN image i ON i.id = ei.image_id
         WHERE ei.entity_type = 'release' AND ei.entity_id = rel.id AND ei.role = 'front'
         ORDER BY CASE ei.source WHEN 'user' THEN 0 WHEN 'folder' THEN 1 WHEN 'embedded' THEN 2 WHEN 'coverartarchive' THEN 3 ELSE 4 END LIMIT 1)
        """;

    // The best available file of a track: present on disk, highest quality first.
    private const string TrackSelect = $"""
        SELECT t.id, t.recording_id, f.id, f.path, t.title, tac.display_name, rel.title, rac.display_name, rel.id,
               rel.date, m.position, t.position, COALESCE(f.duration_ms, t.length_ms), {CoverSql}, f.codec,
               f.rg_track_gain, f.rg_track_peak, f.rg_album_gain, f.rg_album_peak
        FROM track t
        JOIN medium m ON m.id = t.medium_id
        JOIN release rel ON rel.id = m.release_id
        JOIN audio_file f ON f.id = (SELECT f2.id FROM audio_file f2 WHERE f2.track_id = t.id AND f2.missing = 0
                                     ORDER BY COALESCE(f2.bits_per_sample, 0) DESC, COALESCE(f2.bit_rate, 0) DESC LIMIT 1)
        LEFT JOIN artist_credit tac ON tac.id = t.artist_credit_id
        LEFT JOIN artist_credit rac ON rac.id = rel.artist_credit_id
        """;

    private TrackInfo MapTrack(SqliteDataReader r) => new(
        r.Id(0), r.Id(1), r.Id(2), r.GetString(3), r.GetString(4), r.Str(5), r.Str(6), r.Str(7), r.Id(8),
        r.Str(9), r.GetInt32(10), r.GetInt32(11), r.Long(12), CoverFullPath(r.Str(13)), r.Str(14),
        r.Dbl(15), r.Dbl(16), r.Dbl(17), r.Dbl(18));

    private string? CoverFullPath(string? rel) => rel is null ? null : images.FullPath(rel);

    public AlbumSummary? Album(Ulid releaseId) => Albums(releaseId: releaseId).FirstOrDefault();

    /// <summary>Other local albums that are the same album: another edition in its release group, or the same
    /// title by the same album artist (songs that were imported or matched one at a time).</summary>
    public List<AlbumSummary> OtherCopies(Ulid releaseId) => Albums(copiesOf: releaseId);

    public List<AlbumSummary> Albums(string? search = null, Ulid? artistId = null, Ulid? releaseId = null, Ulid? copiesOf = null)
    {
        using var c = db.Open();
        string where = "WHERE EXISTS (SELECT 1 FROM medium m JOIN track t ON t.medium_id = m.id JOIN audio_file f ON f.track_id = t.id WHERE m.release_id = rel.id AND f.missing = 0)";
        var args = new List<object?>();
        if (releaseId is { } rid)
        {
            args.Add(rid);
            where += $" AND rel.id = ${args.Count}";
        }
        if (copiesOf is { } cid)
        {
            args.Add(cid);
            int n = args.Count;
            where += $"""
                 AND rel.id <> ${n} AND EXISTS (SELECT 1 FROM release o WHERE o.id = ${n} AND (o.release_group_id = rel.release_group_id
                     OR (o.title_key = rel.title_key AND COALESCE(o.artist_credit_id, '') = COALESCE(rel.artist_credit_id, ''))))
                """;
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            args.Add("%" + search.Trim() + "%");
            where += $" AND (rel.title LIKE ${args.Count} OR ac.display_name LIKE ${args.Count})";
        }
        if (artistId is { } aid)
        {
            args.Add(aid);
            where += $"""
                 AND (EXISTS (SELECT 1 FROM artist_credit_name n WHERE n.credit_id = rel.artist_credit_id AND n.artist_id = ${args.Count})
                  OR EXISTS (SELECT 1 FROM medium m JOIN track t ON t.medium_id = m.id JOIN artist_credit_name n ON n.credit_id = t.artist_credit_id
                             WHERE m.release_id = rel.id AND n.artist_id = ${args.Count}))
                """;
        }
        return c.Query($"""
            SELECT rel.id, rel.release_group_id, rel.title, ac.display_name, rel.date, rel.edition, {CoverSql},
                   (SELECT COUNT(*) FROM medium m JOIN track t ON t.medium_id = m.id WHERE m.release_id = rel.id),
                   (SELECT COUNT(*) FROM medium m JOIN track t ON t.medium_id = m.id WHERE m.release_id = rel.id
                      AND EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0))
            FROM release rel
            LEFT JOIN artist_credit ac ON ac.id = rel.artist_credit_id
            {where}
            ORDER BY COALESCE((SELECT a.sort_name FROM artist_credit_name n JOIN artist a ON a.id = n.artist_id
                               WHERE n.credit_id = rel.artist_credit_id AND n.position = 0), ac.display_name) COLLATE NOCASE,
                     rel.date, rel.title COLLATE NOCASE
            """,
            r => new AlbumSummary(r.Id(0), r.Id(1), r.GetString(2), r.Str(3), r.Str(4), r.Str(5), CoverFullPath(r.Str(6)), r.GetInt32(7), r.GetInt32(8)),
            [.. args]);
    }

    public List<ArtistSummary> Artists(string? search = null)
    {
        using var c = db.Open();
        string filter = string.IsNullOrWhiteSpace(search) ? "" : "AND a.name LIKE $1";
        return c.Query($"""
            SELECT a.id, a.name, a.sort_name,
                   (SELECT COUNT(DISTINCT rel.id) FROM artist_credit_name n JOIN release rel ON rel.artist_credit_id = n.credit_id WHERE n.artist_id = a.id),
                   (SELECT COUNT(*) FROM artist_credit_name n JOIN track t ON t.artist_credit_id = n.credit_id WHERE n.artist_id = a.id),
                   (SELECT i.rel_path FROM entity_image ei JOIN image i ON i.id = ei.image_id
                    WHERE ei.entity_type = 'artist' AND ei.entity_id = a.id AND ei.role = 'photo'
                    ORDER BY CASE ei.source WHEN 'user' THEN 0 ELSE 1 END LIMIT 1)
            FROM artist a
            WHERE EXISTS (SELECT 1 FROM artist_credit_name n
                          JOIN track t ON t.artist_credit_id = n.credit_id OR t.medium_id IN
                               (SELECT m.id FROM medium m JOIN release rel ON rel.id = m.release_id WHERE rel.artist_credit_id = n.credit_id)
                          JOIN audio_file f ON f.track_id = t.id AND f.missing = 0
                          WHERE n.artist_id = a.id)
            {filter}
            ORDER BY COALESCE(a.sort_name, a.name) COLLATE NOCASE
            """,
            r => new ArtistSummary(r.Id(0), r.GetString(1), r.Str(2), r.GetInt32(3), r.GetInt32(4), CoverFullPath(r.Str(5))),
            string.IsNullOrWhiteSpace(search) ? [] : ["%" + search.Trim() + "%"]);
    }

    public List<TrackInfo> AlbumTracks(Ulid releaseId)
    {
        using var c = db.Open();
        return c.Query($"{TrackSelect} WHERE rel.id = $1 ORDER BY m.position, t.position", MapTrack, releaseId);
    }

    public List<TrackInfo> Tracks(string? search = null, int limit = 5000)
    {
        using var c = db.Open();
        string where = string.IsNullOrWhiteSpace(search) ? "" : "WHERE t.title LIKE $2 OR tac.display_name LIKE $2 OR rel.title LIKE $2";
        return c.Query($"""
            {TrackSelect} {where}
            ORDER BY COALESCE(rac.display_name, tac.display_name) COLLATE NOCASE, rel.date, rel.title COLLATE NOCASE, m.position, t.position
            LIMIT $1
            """, MapTrack, string.IsNullOrWhiteSpace(search) ? [limit] : [limit, "%" + search.Trim() + "%"]);
    }

    /// <summary>Resolves recordings to playable tracks (best file), preserving input order. Missing ones are skipped.</summary>
    public List<TrackInfo> ByRecordings(IReadOnlyList<Ulid> recordingIds)
    {
        if (recordingIds.Count == 0) return [];
        using var c = db.Open();
        var found = new Dictionary<Ulid, TrackInfo>();
        foreach (var chunk in recordingIds.Distinct().Chunk(500))
        {
            string inList = string.Join(",", chunk.Select((_, i) => "$" + (i + 1)));
            foreach (var t in c.Query($"{TrackSelect} WHERE t.recording_id IN ({inList})", MapTrack, [.. chunk.Cast<object?>()]))
                found.TryAdd(t.RecordingId, t);
        }
        return recordingIds.Where(found.ContainsKey).Select(id => found[id]).ToList();
    }

    public TrackInfo? ByTrack(Ulid trackId)
    {
        using var c = db.Open();
        return c.QuerySingle($"{TrackSelect} WHERE t.id = $1", MapTrack, trackId);
    }

    /// <summary>All artist names, for autocomplete.</summary>
    public List<string> ArtistNames()
    {
        using var c = db.Open();
        return c.Query("SELECT DISTINCT name FROM artist ORDER BY name COLLATE NOCASE", r => r.GetString(0));
    }

    public (int Tracks, int Albums, int Artists) Counts()
    {
        using var c = db.Open();
        return (
            (int)c.Scalar<long>("SELECT COUNT(DISTINCT track_id) FROM audio_file WHERE missing = 0"),
            (int)c.Scalar<long>("SELECT COUNT(*) FROM release"),
            (int)c.Scalar<long>("SELECT COUNT(*) FROM artist"));
    }
}
