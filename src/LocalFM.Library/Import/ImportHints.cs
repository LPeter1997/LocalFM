using LocalFM.Core;

namespace LocalFM.Library.Import;

/// <summary>
/// What an integration knew about a file it delivered (a YouTube video's title, artist, album, id).
/// Files downloaded without post-processing carry few or no tags, so the scanner fills the gaps from this.
/// The file's own tags always win, and once imported the knowledge base is authoritative as usual.
/// </summary>
public sealed record ImportHint(
    string Path,
    string Provider,
    string? ExternalId,
    string? Url,
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    string? Date,
    int? TrackNumber)
{
    /// <summary>The external_id provider name this hint's id is stored under (e.g. "youtube").</summary>
    public string? IdProvider => Provider switch { "ytdlp" => "youtube", _ => null };
}

public static class ImportHints
{
    /// <summary>Records a hint. Written before the file is moved into a watched folder, so a scan never misses it.</summary>
    public static Task PutAsync(Database db, ImportHint h) => db.Writer.Run((c, t) =>
        new Tx(c, t).Exec("""
            INSERT OR REPLACE INTO import_hint(path, provider, external_id, url, title, artist, album_artist, album, date, track_no, created_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
            """, h.Path, h.Provider, h.ExternalId, h.Url, h.Title, h.Artist, h.AlbumArtist, h.Album, h.Date, h.TrackNumber, Sql.Now()));

    public static Task RemoveAsync(Database db, string path) =>
        db.Writer.Run((c, t) => new Tx(c, t).Exec("DELETE FROM import_hint WHERE path = $1", path));

    /// <summary>All hints for files under a folder, by path.</summary>
    public static Dictionary<string, ImportHint> Under(Database db, string folder)
    {
        string prefix = folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        using var c = db.Open();
        return c.Query("""
                SELECT path, provider, external_id, url, title, artist, album_artist, album, date, track_no
                FROM import_hint WHERE substr(path, 1, length($1)) = $1
                """, Read, prefix)
            .ToDictionary(h => h.Path);
    }

    private static ImportHint Read(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.Str(2), r.Str(3), r.Str(4), r.Str(5), r.Str(6), r.Str(7), r.Str(8), r.Int(9));

    /// <summary>Tags with the hint's values where the file had none.</summary>
    public static FileTags Apply(FileTags tags, ImportHint h) => tags with
    {
        Title = tags.Title ?? h.Title,
        Artist = tags.Artist ?? h.Artist,
        AlbumArtist = tags.AlbumArtist ?? h.AlbumArtist,
        Album = tags.Album ?? h.Album,
        Date = tags.Date ?? h.Date,
        TrackNumber = tags.TrackNumber ?? h.TrackNumber,
    };

    /// <summary>Whether something with this external id is already in the library: tagged by an earlier import,
    /// or an older rip whose filename carries the id in brackets ("Song [dQw4w9WgXcQ].mp3").</summary>
    public static bool IsKnown(Database db, string idProvider, string externalId)
    {
        using var c = db.Open();
        if (c.Scalar<long>("SELECT COUNT(*) FROM external_id WHERE provider = $1 AND external_id = $2 AND entity_type = 'recording'",
                idProvider, externalId.Trim().ToLowerInvariant()) > 0) return true;
        // instr is case-sensitive, which video ids need.
        return idProvider == "youtube"
               && c.Scalar<long>("SELECT COUNT(*) FROM audio_file WHERE missing = 0 AND instr(path, $1) > 0", $"[{externalId}]") > 0;
    }
}
