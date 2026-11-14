using LocalFM.Core;
using LocalFM.Library.Import;

namespace LocalFM.Library.Enrich;

public sealed record ApplyResult(int TracksMatched, int TracksUnmatched, bool CoverStored, bool MergedIntoGroup);

/// <summary>
/// Writes external release data onto a local release, inside a writer transaction.
/// Every field goes through <see cref="SetField"/>: a field a person locked is never overwritten, and
/// every value written records where it came from (field_provenance).
/// </summary>
public sealed class MetadataApplier(ImageStore images)
{
    public ApplyResult ApplyRelease(Tx tx, Ulid releaseId, ReleaseData d, byte[]? cover, string? coverMime, string? coverSource)
    {
        string src = d.Provider;
        long now = Sql.Now();

        // ---- release
        SetField(tx, "release", releaseId, "title", d.Title, src, now);
        SetField(tx, "release", releaseId, "date", d.Date, src, now);
        SetField(tx, "release", releaseId, "country", d.Country, src, now);
        SetField(tx, "release", releaseId, "label", d.Label, src, now);
        SetField(tx, "release", releaseId, "catalog_number", d.CatalogNumber, src, now);
        SetField(tx, "release", releaseId, "barcode", d.Barcode, src, now);
        SetField(tx, "release", releaseId, "status", d.Status?.ToLowerInvariant(), src, now);
        SetField(tx, "release", releaseId, "edition", d.Disambiguation, src, now);
        if (d.Credit.Count > 0)
            SetField(tx, "release", releaseId, "artist_credit_id", ResolveCredit(tx, d.Credit, src, now).ToString(), src, now);
        Importer.AddExternal(tx, "release", releaseId, src, d.Id);

        // ---- release group: another local edition may already be linked to it; then join that group
        var groupId = tx.Single("SELECT release_group_id FROM release WHERE id = $1", r => r.IdN(0), releaseId)!.Value;
        bool merged = false;
        if (Importer.ByExternal(tx, "release_group", src, d.Group.Id) is { } existingGroup && existingGroup != groupId)
        {
            tx.Exec("UPDATE release SET release_group_id = $2, updated_at = $3 WHERE id = $1", releaseId, existingGroup, now);
            if (tx.Long("SELECT COUNT(*) FROM release WHERE release_group_id = $1", groupId) == 0)
                DeleteEntity(tx, "release_group", groupId);
            groupId = existingGroup;
            merged = true;
        }
        SetField(tx, "release_group", groupId, "title", d.Group.Title, src, now);
        SetField(tx, "release_group", groupId, "primary_type", d.Group.PrimaryType?.ToLowerInvariant(), src, now);
        SetField(tx, "release_group", groupId, "secondary_types",
            d.Group.SecondaryTypes.Count > 0 ? string.Join(",", d.Group.SecondaryTypes.Select(s => s.ToLowerInvariant())) : null, src, now);
        SetField(tx, "release_group", groupId, "first_release_date", d.Group.FirstReleaseDate, src, now);
        var groupCredit = d.Group.Credit.Count > 0 ? d.Group.Credit : d.Credit;
        if (groupCredit.Count > 0)
            SetField(tx, "release_group", groupId, "artist_credit_id", ResolveCredit(tx, groupCredit, src, now).ToString(), src, now);
        Importer.AddExternal(tx, "release_group", groupId, src, d.Group.Id);
        ReplaceGenres(tx, "release_group", groupId, d.Group.Genres.Count > 0 ? d.Group.Genres : d.Genres, src);

        // ---- media and tracks
        var local = tx.Query("""
            SELECT t.id, m.id, m.position, t.position, t.title, t.recording_id
            FROM track t JOIN medium m ON m.id = t.medium_id
            WHERE m.release_id = $1 ORDER BY m.position, t.position
            """, r => new LocalTrack(r.Id(0), r.Id(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.Id(5)), releaseId);
        var unmatched = new List<LocalTrack>(local);
        int matched = 0;

        // When the local release has exactly as many tracks in the same order, position matching is reliable;
        // otherwise fall back to titles.
        var remoteFlat = d.Media.SelectMany(m => m.Tracks.Select(t => (Medium: m, Track: t))).ToList();
        bool sameShape = local.Count == remoteFlat.Count;

        for (int i = 0; i < remoteFlat.Count; i++)
        {
            var (m, t) = remoteFlat[i];
            var hit = unmatched.FirstOrDefault(l => l.Disc == m.Position && l.Position == t.Position && SimilarTitle(l.Title, t.Title))
                ?? unmatched.FirstOrDefault(l => SimilarTitle(l.Title, t.Title))
                ?? unmatched.FirstOrDefault(l => l.Disc == m.Position && l.Position == t.Position)
                ?? (sameShape ? local[i] : null);
            if (hit is null || !unmatched.Remove(hit)) continue;
            matched++;

            var mediumId = EnsureMedium(tx, releaseId, m, src, now);
            if (mediumId != hit.MediumId)
                tx.Exec("UPDATE track SET medium_id = $2 WHERE id = $1", hit.Id, mediumId);
            SetField(tx, "track", hit.Id, "position", t.Position.ToString(), src, now);
            SetField(tx, "track", hit.Id, "title", t.Title, src, now);
            SetField(tx, "track", hit.Id, "number", t.Number, src, now);
            SetField(tx, "track", hit.Id, "length_ms", t.LengthMs?.ToString(), src, now);
            if (t.Credit.Count > 0)
                SetField(tx, "track", hit.Id, "artist_credit_id", ResolveCredit(tx, t.Credit, src, now).ToString(), src, now);
            Importer.AddExternal(tx, "track", hit.Id, src, t.Id);

            ApplyRecording(tx, hit, t.Recording, src, now);
        }

        // Tracks may have moved between discs; drop media left empty.
        tx.Exec("DELETE FROM medium WHERE release_id = $1 AND NOT EXISTS (SELECT 1 FROM track t WHERE t.medium_id = medium.id)", releaseId);

        bool coverStored = false;
        if (cover is { Length: > 0 })
        {
            var img = images.Store(tx, cover, coverMime ?? "image/jpeg");
            tx.Exec("INSERT OR IGNORE INTO entity_image(entity_type, entity_id, image_id, role, source) VALUES ('release',$1,$2,'front',$3)",
                releaseId, img, coverSource ?? src);
            coverStored = true;
        }

        return new ApplyResult(matched, local.Count - matched, coverStored, merged);
    }

    private sealed record LocalTrack(Ulid Id, Ulid MediumId, int Disc, int Position, string Title, Ulid RecordingId);

    private void ApplyRecording(Tx tx, LocalTrack track, RecordingData rec, string src, long now)
    {
        var recordingId = track.RecordingId;
        // The same recording may already exist locally (e.g. from a compilation): merge into it.
        if (Importer.ByExternal(tx, "recording", src, rec.Id) is { } known && known != recordingId)
        {
            MergeRecording(tx, recordingId, known);
            recordingId = known;
        }
        SetField(tx, "recording", recordingId, "title", rec.Title, src, now);
        SetField(tx, "recording", recordingId, "length_ms", rec.LengthMs?.ToString(), src, now);
        if (rec.Credit.Count > 0)
            SetField(tx, "recording", recordingId, "artist_credit_id", ResolveCredit(tx, rec.Credit, src, now).ToString(), src, now);
        Importer.AddExternal(tx, "recording", recordingId, src, rec.Id);
        foreach (var isrc in rec.Isrcs) Importer.AddExternal(tx, "recording", recordingId, "isrc", isrc);
        ReplaceGenres(tx, "recording", recordingId, rec.Genres, src);
        if (rec.FirstReleaseDate is { } frd)
            tx.Exec("INSERT OR REPLACE INTO attribute(entity_type, entity_id, key, value, source, updated_at) VALUES ('recording',$1,'first_release_date',$2,$3,$4)",
                recordingId, frd, src, now);
    }

    /// <summary>Moves everything that points at <paramref name="from"/> to <paramref name="to"/> and deletes it.</summary>
    public static void MergeRecording(Tx tx, Ulid from, Ulid to)
    {
        if (from == to) return;
        tx.Exec("UPDATE track SET recording_id = $2 WHERE recording_id = $1", from, to);
        tx.Exec("UPDATE play_event SET recording_id = $2 WHERE recording_id = $1", from, to);
        tx.Exec("UPDATE playlist_entry SET recording_id = $2 WHERE recording_id = $1", from, to);
        tx.Exec("UPDATE lyrics SET recording_id = $2 WHERE recording_id = $1", from, to);
        tx.Exec("INSERT OR IGNORE INTO recording_work(recording_id, work_id) SELECT $2, work_id FROM recording_work WHERE recording_id = $1", from, to);
        tx.Exec("""
            INSERT OR IGNORE INTO external_id(entity_type, entity_id, provider, external_id)
            SELECT entity_type, $2, provider, external_id FROM external_id WHERE entity_type = 'recording' AND entity_id = $1
            """, from, to);
        tx.Exec("""
            INSERT OR IGNORE INTO attribute(entity_type, entity_id, key, value, source, updated_at)
            SELECT entity_type, $2, key, value, source, updated_at FROM attribute WHERE entity_type = 'recording' AND entity_id = $1
            """, from, to);
        DeleteEntity(tx, "recording", from);
    }

    private static void DeleteEntity(Tx tx, string type, Ulid id)
    {
        foreach (var t in new[] { "external_id", "attribute", "field_provenance", "entity_genre", "entity_image" })
            tx.Exec($"DELETE FROM {t} WHERE entity_type = $1 AND entity_id = $2", type, id);
        tx.Exec($"DELETE FROM {type} WHERE id = $1", id);
    }

    private static Ulid EnsureMedium(Tx tx, Ulid releaseId, MediumData m, string src, long now)
    {
        var id = tx.Single("SELECT id FROM medium WHERE release_id = $1 AND position = $2", r => r.IdN(0), releaseId, m.Position);
        if (id is null)
        {
            id = Ulid.NewUlid();
            tx.Exec("INSERT INTO medium(id, release_id, position, format, title, track_count) VALUES ($1,$2,$3,$4,$5,$6)",
                id, releaseId, m.Position, m.Format, Normalize.Text(m.Title), m.Tracks.Count);
        }
        else
        {
            SetField(tx, "medium", id.Value, "format", m.Format, src, now);
            SetField(tx, "medium", id.Value, "title", Normalize.Text(m.Title), src, now);
            SetField(tx, "medium", id.Value, "track_count", m.Tracks.Count.ToString(), src, now);
        }
        return id.Value;
    }

    private static Ulid ResolveCredit(Tx tx, IReadOnlyList<CreditPart> credit, string src, long now)
    {
        string display = ReleaseData.CreditText(credit);
        var names = credit.Select(c => c.CreditedName).ToList();
        var ids = credit.Select(c => c.Artist.Id).ToList();
        var creditId = Importer.ResolveCredit(tx, display, names.Count > 1 ? names : [],
            ids.All(i => i is not null) ? ids.Select(i => i!).ToList() : [], now);
        // The credit may have matched an existing one spelled differently ("radiohead"): take the provider's spelling.
        tx.Exec("UPDATE artist_credit SET display_name = $2 WHERE id = $1", creditId, display);
        if (credit.Count > 1)
            for (int i = 0; i < credit.Count; i++)
                tx.Exec("UPDATE artist_credit_name SET credited_name = $3, join_phrase = $4 WHERE credit_id = $1 AND position = $2",
                    creditId, i, credit[i].CreditedName, credit[i].JoinPhrase);
        else
            tx.Exec("UPDATE artist_credit_name SET credited_name = $2 WHERE credit_id = $1", creditId, credit[0].CreditedName);

        // Canonical artist data from the provider.
        foreach (var part in credit)
        {
            if (Importer.ByExternal(tx, "artist", src, part.Artist.Id) is not { } artistId) continue;
            SetField(tx, "artist", artistId, "name", part.Artist.Name, src, now);
            SetField(tx, "artist", artistId, "sort_name", part.Artist.SortName, src, now);
            SetField(tx, "artist", artistId, "type", part.Artist.Type?.ToLowerInvariant(), src, now);
            SetField(tx, "artist", artistId, "country", part.Artist.Country, src, now);
            SetField(tx, "artist", artistId, "disambiguation", part.Artist.Disambiguation, src, now);
        }
        return creditId;
    }

    private static void ReplaceGenres(Tx tx, string entityType, Ulid id, IReadOnlyList<string> genres, string src)
    {
        if (genres.Count == 0) return;
        tx.Exec("DELETE FROM entity_genre WHERE entity_type = $1 AND entity_id = $2 AND source = $3", entityType, id, src);
        foreach (var g in genres.Take(8)) Importer.LinkGenre(tx, entityType, id, g, src);
    }

    private static bool SimilarTitle(string a, string b)
    {
        string ka = Normalize.Key(a), kb = Normalize.Key(b);
        return ka.Length > 0 && (ka == kb || ka.StartsWith(kb + " ", StringComparison.Ordinal) || kb.StartsWith(ka + " ", StringComparison.Ordinal));
    }

    // Columns that may be written, per entity type (also the guard against building SQL from anything else).
    private static readonly Dictionary<string, HashSet<string>> Writable = new()
    {
        ["artist"] = ["name", "sort_name", "type", "country", "disambiguation", "begin_date", "end_date"],
        ["release"] = ["title", "date", "country", "label", "catalog_number", "barcode", "status", "edition", "artist_credit_id"],
        ["release_group"] = ["title", "primary_type", "secondary_types", "first_release_date", "artist_credit_id"],
        ["medium"] = ["format", "title", "track_count"],
        ["track"] = ["title", "number", "position", "length_ms", "artist_credit_id"],
        ["recording"] = ["title", "length_ms", "artist_credit_id", "disambiguation"],
    };

    /// <summary>
    /// Sets one field unless it is locked. Returns true when written. Derived match keys
    /// (name_key, title_key) follow their source field.
    /// </summary>
    public static bool SetField(Tx tx, string entityType, Ulid id, string field, string? value, string source, long now, bool lockIt = false)
    {
        if (!Writable.TryGetValue(entityType, out var cols) || !cols.Contains(field))
            throw new ArgumentException($"{entityType}.{field} is not writable");
        if (value is null && !lockIt) return false;
        bool locked = tx.Long("SELECT locked FROM field_provenance WHERE entity_type = $1 AND entity_id = $2 AND field = $3",
            entityType, id, field) != 0;
        if (locked && !lockIt) return false;

        object? dbValue = field is "position" or "length_ms" or "track_count"
            ? (value is null ? null : long.TryParse(value, out long n) ? n : null)
            : value;
        bool hasUpdated = entityType != "medium";
        tx.Exec($"UPDATE {entityType} SET {field} = $2{(hasUpdated ? ", updated_at = $3" : "")} WHERE id = $1", id, dbValue, now);
        if (field == "title" && entityType is "release" or "release_group")
            tx.Exec($"UPDATE {entityType} SET title_key = $2 WHERE id = $1", id, Normalize.Key(value));
        if (field == "name" && entityType == "artist")
            tx.Exec("UPDATE artist SET name_key = $2 WHERE id = $1", id, Normalize.Key(value));

        tx.Exec("""
            INSERT INTO field_provenance(entity_type, entity_id, field, source, updated_at, locked) VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT(entity_type, entity_id, field) DO UPDATE SET source = excluded.source, updated_at = excluded.updated_at,
                locked = MAX(field_provenance.locked, excluded.locked)
            """, entityType, id, field, source, now, lockIt);
        return true;
    }
}
