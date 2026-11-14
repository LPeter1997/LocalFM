using LocalFM.Core;
using LocalFM.Library.Enrich;
using LocalFM.Library.Import;

namespace LocalFM.Library.Edit;

/// <summary>
/// One field to change. Besides the plain columns (see <see cref="MetadataApplier.SetField"/>) two virtual fields exist:
/// <c>artist</c> on a track or release (a credit, typed as text: "A feat. B") and <c>disc</c> on a track.
/// </summary>
public sealed record FieldChange(string EntityType, Ulid Id, string Field, string? Value);

/// <summary>Where a field's current value came from, and whether online lookups may overwrite it.</summary>
public sealed record FieldState(string Source, bool Locked);

public sealed record TrackEditData(Ulid TrackId, int Disc, int Position, string Title, string? Artist, IReadOnlyDictionary<string, FieldState> States);

public sealed record AlbumEditData(
    Ulid ReleaseId, string Title, string? Artist, string? Date, string? Label, string? Edition, string? Country,
    IReadOnlyDictionary<string, FieldState> States, IReadOnlyList<TrackEditData> Tracks);

/// <summary>
/// Hand edits and cleanups of the knowledge base. Edits by a person (source "user") lock the field, so a later
/// MusicBrainz apply never undoes them. All writes run inside a writer transaction.
/// </summary>
public static class MetadataEditor
{
    public const string UserSource = "user";
    public const string CleanupSource = "cleanup";

    /// <summary>Applies changes. With <paramref name="lockFields"/>, each changed field is locked.</summary>
    public static int Apply(Tx tx, IEnumerable<FieldChange> changes, string source = UserSource, bool lockFields = true)
    {
        long now = Sql.Now();
        int written = 0;
        foreach (var ch in changes)
        {
            string? value = Normalize.Text(ch.Value);
            bool ok = (ch.EntityType, ch.Field) switch
            {
                ("track" or "release", "artist") => SetArtist(tx, ch.EntityType, ch.Id, value, source, now, lockFields),
                ("track", "disc") => MoveToDisc(tx, ch.Id, value, source, now, lockFields),
                ("track", "title") => SetTrackTitle(tx, ch.Id, value, source, now, lockFields),
                ("release", "title") => SetReleaseTitle(tx, ch.Id, value, source, now, lockFields),
                ("track", "position") => int.TryParse(value, out int p) && p > 0
                    && MetadataApplier.SetField(tx, "track", ch.Id, "position", p.ToString(), source, now, lockFields)
                    && MetadataApplier.SetField(tx, "track", ch.Id, "number", p.ToString(), source, now, lockFields),
                _ => MetadataApplier.SetField(tx, ch.EntityType, ch.Id, ch.Field, value, source, now, lockFields),
            };
            if (ok) written++;
        }
        PruneOrphans(tx);
        return written;
    }

    /// <summary>Locks or unlocks a field without changing its value. Virtual fields map to their columns.</summary>
    public static void SetLocked(Tx tx, string entityType, Ulid id, string field, bool locked)
    {
        field = Column(field);
        long now = Sql.Now();
        tx.Exec("""
            INSERT INTO field_provenance(entity_type, entity_id, field, source, updated_at, locked) VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT(entity_type, entity_id, field) DO UPDATE SET locked = excluded.locked, updated_at = excluded.updated_at
            """, entityType, id, field, locked ? UserSource : "tags", now, locked);
    }

    /// <summary>Provenance of every recorded field of an entity, keyed by the editor's field names.</summary>
    public static Dictionary<string, FieldState> States(Database db, string entityType, Ulid id)
    {
        using var c = db.Open();
        var rows = c.Query("SELECT field, source, locked FROM field_provenance WHERE entity_type = $1 AND entity_id = $2",
            r => (Field: r.GetString(0), State: new FieldState(r.GetString(1), r.GetInt64(2) != 0)), entityType, id);
        var map = new Dictionary<string, FieldState>();
        foreach (var (field, state) in rows)
        {
            map[field] = state;
            if (field == "artist_credit_id") map["artist"] = state;
            if (field == "medium_id") map["disc"] = state;
        }
        return map;
    }

    /// <summary>Everything the album editor shows: release fields, tracks, and each field's provenance.</summary>
    public static AlbumEditData? LoadAlbum(Database db, Ulid releaseId)
    {
        using var c = db.Open();
        var head = c.QuerySingle("""
            SELECT r.title, ac.display_name, r.date, r.label, r.edition, r.country
            FROM release r LEFT JOIN artist_credit ac ON ac.id = r.artist_credit_id WHERE r.id = $1
            """, r => (Title: r.GetString(0), Artist: r.Str(1), Date: r.Str(2), Label: r.Str(3), Edition: r.Str(4), Country: r.Str(5)), releaseId);
        if (head.Title is null) return null;
        var tracks = c.Query("""
            SELECT t.id, m.position, t.position, t.title, ac.display_name
            FROM track t JOIN medium m ON m.id = t.medium_id LEFT JOIN artist_credit ac ON ac.id = t.artist_credit_id
            WHERE m.release_id = $1 ORDER BY m.position, t.position
            """, r => (Id: r.Id(0), Disc: r.GetInt32(1), Pos: r.GetInt32(2), Title: r.GetString(3), Artist: r.Str(4)), releaseId);
        return new AlbumEditData(releaseId, head.Title, head.Artist, head.Date, head.Label, head.Edition, head.Country,
            States(db, "release", releaseId),
            tracks.Select(t => new TrackEditData(t.Id, t.Disc, t.Pos, t.Title, t.Artist, States(db, "track", t.Id))).ToList());
    }

    private static string Column(string field) => field switch { "artist" => "artist_credit_id", "disc" => "medium_id", _ => field };

    private static bool IsLocked(Tx tx, string entityType, Ulid id, string field) =>
        tx.Long("SELECT locked FROM field_provenance WHERE entity_type = $1 AND entity_id = $2 AND field = $3", entityType, id, field) != 0;

    private static bool SetTrackTitle(Tx tx, Ulid trackId, string? title, string source, long now, bool lockIt)
    {
        if (title is null) return false; // a track always has a title
        if (!MetadataApplier.SetField(tx, "track", trackId, "title", title, source, now, lockIt)) return false;
        // The recording is the song itself; follow along when this is its only appearance.
        var rec = tx.Single("SELECT recording_id FROM track WHERE id = $1", r => r.Id(0), trackId);
        if (tx.Long("SELECT COUNT(*) FROM track WHERE recording_id = $1", rec) == 1)
            MetadataApplier.SetField(tx, "recording", rec, "title", title, source, now, lockIt);
        return true;
    }

    private static bool SetReleaseTitle(Tx tx, Ulid releaseId, string? title, string source, long now, bool lockIt)
    {
        if (title is null) return false;
        if (!MetadataApplier.SetField(tx, "release", releaseId, "title", title, source, now, lockIt)) return false;
        var group = tx.Single("SELECT release_group_id FROM release WHERE id = $1", r => r.Id(0), releaseId);
        if (tx.Long("SELECT COUNT(*) FROM release WHERE release_group_id = $1", group) == 1)
            MetadataApplier.SetField(tx, "release_group", group, "title", title, source, now, lockIt);
        return true;
    }

    private static bool SetArtist(Tx tx, string entityType, Ulid id, string? text, string source, long now, bool lockIt)
    {
        if (!lockIt && IsLocked(tx, entityType, id, "artist_credit_id")) return false;
        string? creditId = text is null ? null : ResolveCreditText(tx, text, now).ToString();
        if (!MetadataApplier.SetField(tx, entityType, id, "artist_credit_id", creditId, source, now, lockIt)) return false;
        if (entityType == "track")
        {
            var rec = tx.Single("SELECT recording_id FROM track WHERE id = $1", r => r.Id(0), id);
            if (tx.Long("SELECT COUNT(*) FROM track WHERE recording_id = $1", rec) == 1)
                MetadataApplier.SetField(tx, "recording", rec, "artist_credit_id", creditId, source, now, lockIt);
        }
        else
        {
            var group = tx.Single("SELECT release_group_id FROM release WHERE id = $1", r => r.Id(0), id);
            if (tx.Long("SELECT COUNT(*) FROM release WHERE release_group_id = $1", group) == 1)
                MetadataApplier.SetField(tx, "release_group", group, "artist_credit_id", creditId, source, now, lockIt);
        }
        return true;
    }

    // "feat." always separates artists; "&", "," and " x " only when every part is an artist we already know,
    // so "Simon & Garfunkel" stays one act unless both are in the library on their own.
    private static readonly string[] AlwaysJoins = [" feat. ", " featuring ", " ft. ", " feat ", " ft "];
    private static readonly string[] MaybeJoins = [" & ", ", ", " x ", " and ", " vs. ", " with "];

    /// <summary>Turns typed credit text into a credit, reusing known artists by name.</summary>
    public static Ulid ResolveCreditText(Tx tx, string text, long now)
    {
        var names = SplitCredit(text, name =>
            tx.Long("SELECT COUNT(*) FROM artist WHERE name_key = $1", Normalize.Key(name)) > 0);
        return Importer.ResolveCredit(tx, text, names.Count > 1 ? names : [], [], now);
    }

    public static List<string> SplitCredit(string text, Func<string, bool> isKnownArtist)
    {
        if (isKnownArtist(text)) return [text];
        var parts = new List<string> { text };
        foreach (var join in AlwaysJoins) parts = parts.SelectMany(p => SplitOn(p, join)).ToList();
        var result = new List<string>();
        foreach (var part in parts)
        {
            var pieces = new List<string> { part };
            foreach (var join in MaybeJoins) pieces = pieces.SelectMany(p => SplitOn(p, join)).ToList();
            if (pieces.Count > 1 && pieces.All(isKnownArtist)) result.AddRange(pieces);
            else result.Add(part);
        }
        return result.Where(n => n.Length > 0).ToList();
    }

    private static IEnumerable<string> SplitOn(string s, string join) =>
        s.Split(join, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
         .DefaultIfEmpty(s.Trim());

    private static bool MoveToDisc(Tx tx, Ulid trackId, string? value, string source, long now, bool lockIt)
    {
        if (!int.TryParse(value, out int disc) || disc < 1) return false;
        if (!lockIt && IsLocked(tx, "track", trackId, "medium_id")) return false;
        var (mediumId, releaseId, current) = tx.Single("""
            SELECT m.id, m.release_id, m.position FROM track t JOIN medium m ON m.id = t.medium_id WHERE t.id = $1
            """, r => (r.Id(0), r.Id(1), r.GetInt32(2)), trackId);
        if (current == disc) return false;
        var target = tx.Single("SELECT id FROM medium WHERE release_id = $1 AND position = $2", r => r.IdN(0), releaseId, disc);
        if (target is null)
        {
            target = Ulid.NewUlid();
            tx.Exec("INSERT INTO medium(id, release_id, position) VALUES ($1,$2,$3)", target, releaseId, disc);
        }
        tx.Exec("UPDATE track SET medium_id = $2, updated_at = $3 WHERE id = $1", trackId, target, now);
        tx.Exec("DELETE FROM medium WHERE id = $1 AND NOT EXISTS (SELECT 1 FROM track WHERE medium_id = $1)", mediumId);
        tx.Exec("""
            INSERT INTO field_provenance(entity_type, entity_id, field, source, updated_at, locked) VALUES ('track',$1,'medium_id',$2,$3,$4)
            ON CONFLICT(entity_type, entity_id, field) DO UPDATE SET source = excluded.source, updated_at = excluded.updated_at,
                locked = MAX(field_provenance.locked, excluded.locked)
            """, trackId, source, now, lockIt);
        return true;
    }

    /// <summary>
    /// Moves tracks onto another album, keeping their disc and number (or appending when that slot is taken by
    /// another song). A song the target already has becomes one track with both files. Albums left empty are
    /// removed; their cover moves along when the target has none. Returns how many tracks moved.
    /// </summary>
    public static int MoveTracks(Tx tx, IEnumerable<Ulid> trackIds, Ulid targetReleaseId, string source = UserSource, bool lockIt = true)
    {
        long now = Sql.Now();
        int moved = 0;
        var emptied = new HashSet<Ulid>();
        foreach (var trackId in trackIds.Distinct().ToList())
        {
            var row = tx.Single("""
                SELECT m.id, m.release_id, m.position, t.position, t.title, t.recording_id
                FROM track t JOIN medium m ON m.id = t.medium_id WHERE t.id = $1
                """, r => (Medium: r.Id(0), Release: r.Id(1), Disc: r.GetInt32(2), Pos: r.GetInt32(3), Title: r.GetString(4), Recording: r.Id(5)), trackId);
            if (row.Title is null || row.Release == targetReleaseId) continue;
            emptied.Add(row.Release);

            var onTarget = tx.Query("""
                SELECT t.id, t.title, t.recording_id FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1
                """, r => (Id: r.Id(0), Title: r.GetString(1), Recording: r.Id(2)), targetReleaseId);
            var same = onTarget.FirstOrDefault(t => t.Recording == row.Recording);
            if (same.Title is null) same = onTarget.FirstOrDefault(t => Normalize.Key(t.Title) == Normalize.Key(row.Title));
            if (same.Title is not null)
            {
                tx.Exec("UPDATE audio_file SET track_id = $2 WHERE track_id = $1", trackId, same.Id);
                MetadataApplier.MergeRecording(tx, row.Recording, same.Recording);
                MetadataApplier.DeleteEntity(tx, "track", trackId);
            }
            else
            {
                var medium = tx.Single("SELECT id FROM medium WHERE release_id = $1 AND position = $2", r => r.IdN(0), targetReleaseId, row.Disc);
                if (medium is null)
                {
                    medium = Ulid.NewUlid();
                    tx.Exec("INSERT INTO medium(id, release_id, position) VALUES ($1,$2,$3)", medium, targetReleaseId, row.Disc);
                }
                int pos = tx.Long("SELECT COUNT(*) FROM track WHERE medium_id = $1 AND position = $2", medium, row.Pos) == 0
                    ? row.Pos : (int)tx.Long("SELECT COALESCE(MAX(position), 0) + 1 FROM track WHERE medium_id = $1", medium);
                tx.Exec("UPDATE track SET medium_id = $2, position = $3, number = CASE WHEN position = $3 THEN number ELSE $4 END, updated_at = $5 WHERE id = $1",
                    trackId, medium, pos, pos.ToString(), now);
                tx.Exec("""
                    INSERT INTO field_provenance(entity_type, entity_id, field, source, updated_at, locked) VALUES ('track',$1,'medium_id',$2,$3,$4)
                    ON CONFLICT(entity_type, entity_id, field) DO UPDATE SET source = excluded.source, updated_at = excluded.updated_at,
                        locked = MAX(field_provenance.locked, excluded.locked)
                    """, trackId, source, now, lockIt);
            }
            moved++;
        }

        foreach (var rel in emptied)
        {
            tx.Exec("DELETE FROM medium WHERE release_id = $1 AND NOT EXISTS (SELECT 1 FROM track t WHERE t.medium_id = medium.id)", rel);
            if (tx.Long("SELECT COUNT(*) FROM medium WHERE release_id = $1", rel) > 0) continue;
            if (tx.Long("SELECT COUNT(*) FROM entity_image WHERE entity_type = 'release' AND entity_id = $1 AND role = 'front'", targetReleaseId) == 0)
                tx.Exec("UPDATE OR IGNORE entity_image SET entity_id = $2 WHERE entity_type = 'release' AND entity_id = $1", rel, targetReleaseId);
            var group = tx.Single("SELECT release_group_id FROM release WHERE id = $1", r => r.Id(0), rel);
            MetadataApplier.DeleteEntity(tx, "release", rel);
            if (tx.Long("SELECT COUNT(*) FROM release WHERE release_group_id = $1", group) == 0)
                MetadataApplier.DeleteEntity(tx, "release_group", group);
        }
        PruneOrphans(tx);
        return moved;
    }

    /// <summary>Moves every track of one album onto another (merging two editions or two halves of one album).</summary>
    public static int MergeRelease(Tx tx, Ulid fromReleaseId, Ulid intoReleaseId, string source = UserSource, bool lockIt = true) =>
        fromReleaseId == intoReleaseId ? 0 : MoveTracks(tx,
            tx.Query("SELECT t.id FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1 ORDER BY m.position, t.position",
                r => r.Id(0), fromReleaseId),
            intoReleaseId, source, lockIt);

    /// <summary>Removes credits nothing uses any more, and artists left without credits that we know nothing else about.</summary>
    public static void PruneOrphans(Tx tx)
    {
        tx.Exec("""
            DELETE FROM artist_credit WHERE
                NOT EXISTS (SELECT 1 FROM track WHERE artist_credit_id = artist_credit.id) AND
                NOT EXISTS (SELECT 1 FROM recording WHERE artist_credit_id = artist_credit.id) AND
                NOT EXISTS (SELECT 1 FROM release WHERE artist_credit_id = artist_credit.id) AND
                NOT EXISTS (SELECT 1 FROM release_group WHERE artist_credit_id = artist_credit.id)
            """);
        tx.Exec("DELETE FROM artist_credit_name WHERE NOT EXISTS (SELECT 1 FROM artist_credit WHERE id = artist_credit_name.credit_id)");
        CarryOverRenamedArtists(tx);
        var orphans = tx.Query("""
            SELECT a.id FROM artist a
            WHERE NOT EXISTS (SELECT 1 FROM artist_credit_name n WHERE n.artist_id = a.id)
              AND NOT EXISTS (SELECT 1 FROM external_id e WHERE e.entity_type = 'artist' AND e.entity_id = a.id)
            """, r => r.Id(0));
        foreach (var id in orphans)
        {
            foreach (var t in new[] { "attribute", "field_provenance", "entity_genre", "entity_image" })
                tx.Exec($"DELETE FROM {t} WHERE entity_type = 'artist' AND entity_id = $1", id);
            tx.Exec("DELETE FROM artist WHERE id = $1", id);
        }
    }

    /// <summary>
    /// An artist that lost all its credits by being renamed ("Scorpions - Topic" → "Scorpions") but carries online
    /// ids hands its profile (ids, photo, bio, genres, facts) to the artist now credited under the tidied name.
    /// </summary>
    private static void CarryOverRenamedArtists(Tx tx)
    {
        var orphans = tx.Query("""
            SELECT a.id, a.name FROM artist a
            WHERE NOT EXISTS (SELECT 1 FROM artist_credit_name n WHERE n.artist_id = a.id)
              AND EXISTS (SELECT 1 FROM external_id e WHERE e.entity_type = 'artist' AND e.entity_id = a.id)
            """, r => (Id: r.Id(0), Name: r.GetString(1)));
        foreach (var (from, name) in orphans)
        {
            string key = Normalize.Key(Cleanup.CleanArtist(name)?.Text ?? name);
            var successors = tx.Query("""
                SELECT a.id FROM artist a
                WHERE a.name_key = $1 AND a.id <> $2 AND EXISTS (SELECT 1 FROM artist_credit_name n WHERE n.artist_id = a.id)
                  AND NOT EXISTS (SELECT 1 FROM external_id e WHERE e.entity_type = 'artist' AND e.entity_id = a.id AND e.provider = 'musicbrainz')
                """, r => r.Id(0), key, from);
            if (successors.Count == 1) MergeArtist(tx, from, successors[0]);
        }
    }

    /// <summary>Moves everything known about <paramref name="from"/> onto <paramref name="to"/> where <paramref name="to"/> has nothing yet.</summary>
    public static void MergeArtist(Tx tx, Ulid from, Ulid to)
    {
        tx.Exec("UPDATE OR IGNORE external_id SET entity_id = $2 WHERE entity_type = 'artist' AND entity_id = $1", from, to);
        tx.Exec("UPDATE OR IGNORE attribute SET entity_id = $2 WHERE entity_type = 'artist' AND entity_id = $1", from, to);
        tx.Exec("UPDATE OR IGNORE entity_genre SET entity_id = $2 WHERE entity_type = 'artist' AND entity_id = $1", from, to);
        tx.Exec("UPDATE OR IGNORE entity_image SET entity_id = $2 WHERE entity_type = 'artist' AND entity_id = $1", from, to);
        tx.Exec("UPDATE OR IGNORE field_provenance SET entity_id = $2 WHERE entity_type = 'artist' AND entity_id = $1 AND field <> 'name'", from, to);
        foreach (var col in new[] { "sort_name", "type", "disambiguation", "country", "begin_date", "end_date" })
            tx.Exec($"UPDATE artist SET {col} = (SELECT {col} FROM artist WHERE id = $1) WHERE id = $2 AND {col} IS NULL", from, to);
        tx.Exec("DELETE FROM artist_credit_name WHERE artist_id = $1", from);
        foreach (var t in new[] { "external_id", "attribute", "field_provenance", "entity_genre", "entity_image" })
            tx.Exec($"DELETE FROM {t} WHERE entity_type = 'artist' AND entity_id = $1", from);
        tx.Exec("DELETE FROM artist WHERE id = $1", from);
    }
}
