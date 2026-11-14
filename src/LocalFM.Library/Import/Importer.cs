using LocalFM.Core;

namespace LocalFM.Library.Import;

/// <summary>Everything the scanner learned about one file (gathered off the writer thread).</summary>
public sealed record ScannedFile(
    Ulid SourceId,
    string Path,
    long Size,
    long MTime,
    string AudioHash,
    string TagsHash,
    string Codec,
    string Container,
    long? DurationMs,
    long BitRate,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    FileTags Tags,
    byte[]? Picture,
    string? PictureMime,
    string? FolderImagePath);

public enum ImportOutcome { Unchanged, Added, Updated, Moved, Retagged }

/// <summary>
/// Turns scanned files into knowledge-base entities. Runs inside a writer transaction.
///
/// Matching order for each entity: MusicBrainz id (when tagged) → normalized keys. Tags only fill
/// entities they create; existing entities (possibly cleaned up by hand) are never overwritten here.
/// </summary>
public sealed class Importer(ImageStore images)
{
    private const string TagSource = "tags";

    /// <summary>Upserts one file. Returns what happened.</summary>
    public ImportOutcome Import(Tx tx, ScannedFile f)
    {
        long now = Sql.Now();

        // 1. Same path: content or tags changed.
        var byPath = tx.Single("SELECT id, audio_hash, tags_hash, track_id FROM audio_file WHERE path = $1",
            r => new { Id = r.Id(0), Hash = r.GetString(1), Tags = r.Str(2), Track = r.IdN(3) }, f.Path);
        if (byPath is not null)
        {
            UpdateTechnical(tx, byPath.Id, f, now);
            if (byPath.Tags == f.TagsHash && byPath.Track is not null) return ImportOutcome.Unchanged;
            // Once a track was matched online or edited by hand, the knowledge base is authoritative:
            // retagging the file elsewhere must not move it off its curated track.
            if (byPath.Track is { } curated &&
                tx.Long("SELECT COUNT(*) FROM field_provenance WHERE entity_type = 'track' AND entity_id = $1", curated) > 0)
            {
                tx.Exec("UPDATE audio_file SET tags_hash = $2 WHERE id = $1", byPath.Id, f.TagsHash);
                return ImportOutcome.Retagged;
            }
            var recording = byPath.Track is { } t
                ? tx.Single("SELECT recording_id FROM track WHERE id = $1", r => r.IdN(0), t) : null;
            var track = ResolveTrack(tx, f, recording, now);
            tx.Exec("UPDATE audio_file SET track_id = $2, tags_hash = $3 WHERE id = $1", byPath.Id, track, f.TagsHash);
            return ImportOutcome.Retagged;
        }

        // 2. Same content elsewhere, and the old file is gone: it was moved/renamed. Keep its identity.
        var sameContent = tx.Query("SELECT id, path FROM audio_file WHERE audio_hash = $1",
            r => (Id: r.Id(0), Path: r.GetString(1)), f.AudioHash);
        foreach (var (id, oldPath) in sameContent)
        {
            if (File.Exists(oldPath)) continue; // a duplicate copy, not a move
            tx.Exec("UPDATE audio_file SET path = $2, source_id = $3, missing = 0, updated_at = $4 WHERE id = $1",
                id, f.Path, f.SourceId, now);
            UpdateTechnical(tx, id, f, now);
            return ImportOutcome.Moved;
        }

        // 3. New file. Identical audio elsewhere is the same recording (a copy, or the same master on
        //    a compilation); which release/track it belongs to still comes from its own tags.
        Ulid? sameRecording = sameContent.Count > 0
            ? tx.Single("SELECT t.recording_id FROM audio_file f JOIN track t ON t.id = f.track_id WHERE f.id = $1",
                r => r.IdN(0), sameContent[0].Id)
            : null;
        var trackId = ResolveTrack(tx, f, sameRecording, now);

        var fileId = Ulid.NewUlid();
        tx.Exec("""
            INSERT INTO audio_file(id, source_id, path, size, mtime, audio_hash, tags_hash, codec, container,
                duration_ms, bit_rate, sample_rate, channels, bits_per_sample, track_id,
                rg_track_gain, rg_track_peak, rg_album_gain, rg_album_peak, missing, added_at, updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,0,$20,$20)
            """,
            fileId, f.SourceId, f.Path, f.Size, f.MTime, f.AudioHash, f.TagsHash, f.Codec, f.Container,
            f.DurationMs, f.BitRate, f.SampleRate, f.Channels, f.BitsPerSample, trackId,
            f.Tags.TrackGainDb, f.Tags.TrackPeak, f.Tags.AlbumGainDb, f.Tags.AlbumPeak, now);
        return ImportOutcome.Added;
    }

    private static void UpdateTechnical(Tx tx, Ulid id, ScannedFile f, long now) =>
        tx.Exec("""
            UPDATE audio_file SET size = $2, mtime = $3, audio_hash = $4, codec = $5, container = $6,
                duration_ms = $7, bit_rate = $8, sample_rate = $9, channels = $10, bits_per_sample = $11,
                rg_track_gain = $12, rg_track_peak = $13, rg_album_gain = $14, rg_album_peak = $15,
                missing = 0, updated_at = $16
            WHERE id = $1
            """,
            id, f.Size, f.MTime, f.AudioHash, f.Codec, f.Container, f.DurationMs, f.BitRate, f.SampleRate,
            f.Channels, f.BitsPerSample, f.Tags.TrackGainDb, f.Tags.TrackPeak, f.Tags.AlbumGainDb, f.Tags.AlbumPeak, now);

    // ------------------------------------------------------------------ entity resolution

    private Ulid ResolveTrack(Tx tx, ScannedFile f, Ulid? keepRecording, long now)
    {
        var t = f.Tags;
        string title = Normalize.Text(t.Title) ?? System.IO.Path.GetFileNameWithoutExtension(f.Path);
        string? artistText = Normalize.Text(t.Artist) ?? Normalize.Text(t.AlbumArtist);
        Ulid? trackCredit = artistText is null ? null : ResolveCredit(tx, artistText, t.Artists, t.MbArtistIds, now);

        string? albumArtistText = Normalize.Text(t.AlbumArtist) ?? (t.Compilation ? "Various Artists" : artistText);
        Ulid? albumCredit = albumArtistText is null ? null
            : ResolveCredit(tx, albumArtistText, [], t.MbAlbumArtistIds.Count > 0 ? t.MbAlbumArtistIds : (t.AlbumArtist is null ? t.MbArtistIds : []), now);

        var releaseId = ResolveRelease(tx, f, albumCredit, now);
        int discNo = t.DiscNumber is > 0 ? t.DiscNumber.Value : 1;
        var mediumId = ResolveMedium(tx, releaseId, discNo, t, now);

        // Track: MusicBrainz track id, else same position + title on this medium.
        Ulid? trackId = ByExternal(tx, "track", "musicbrainz", t.MbTrackId);
        if (trackId is null && t.TrackNumber is { } no)
            trackId = tx.Query("SELECT id, title FROM track WHERE medium_id = $1 AND position = $2",
                    r => (Id: r.Id(0), Title: r.GetString(1)), mediumId, no)
                .Where(x => Normalize.Key(x.Title) == Normalize.Key(title))
                .Select(x => (Ulid?)x.Id).FirstOrDefault();
        if (trackId is { } existing)
        {
            ImportFileLevelExtras(tx, f, existing, releaseId, now);
            return existing;
        }

        var recordingId = ByExternal(tx, "recording", "musicbrainz", t.MbRecordingId)
            ?? keepRecording
            ?? CreateRecording(tx, title, trackCredit, f.DurationMs, t, now);

        int position = t.TrackNumber ?? (int)tx.Long("SELECT COALESCE(MAX(position), 0) + 1 FROM track WHERE medium_id = $1", mediumId);
        var id = Ulid.NewUlid();
        tx.Exec("""
            INSERT INTO track(id, medium_id, recording_id, position, number, title, artist_credit_id, length_ms, created_at, updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$9)
            """, id, mediumId, recordingId, position, t.TrackNumber?.ToString(), title, trackCredit, f.DurationMs, now);
        AddExternal(tx, "track", id, "musicbrainz", t.MbTrackId);
        ImportFileLevelExtras(tx, f, id, releaseId, now);
        return id;
    }

    private Ulid CreateRecording(Tx tx, string title, Ulid? credit, long? lengthMs, FileTags t, long now)
    {
        var id = Ulid.NewUlid();
        tx.Exec("INSERT INTO recording(id, title, artist_credit_id, length_ms, created_at, updated_at) VALUES ($1,$2,$3,$4,$5,$5)",
            id, title, credit, lengthMs, now);
        AddExternal(tx, "recording", id, "musicbrainz", t.MbRecordingId);
        AddExternal(tx, "recording", id, "isrc", t.Isrc);
        foreach (var g in t.Genres) LinkGenre(tx, "recording", id, g);
        if (t.Bpm is { } bpm) SetAttribute(tx, "recording", id, "bpm", bpm, now);
        if (t.MusicalKey is { } key) SetAttribute(tx, "recording", id, "key", key, now);
        if (Normalize.Text(t.Composer) is { } composer) SetAttribute(tx, "recording", id, "composer", composer, now);

        if (t.MbWorkId is { } mbWork || t.Composer is not null)
        {
            var workId = ByExternal(tx, "work", "musicbrainz", t.MbWorkId);
            if (workId is null)
            {
                workId = Ulid.NewUlid();
                tx.Exec("INSERT INTO work(id, title, created_at, updated_at) VALUES ($1,$2,$3,$3)", workId, title, now);
                AddExternal(tx, "work", workId.Value, "musicbrainz", t.MbWorkId);
            }
            tx.Exec("INSERT OR IGNORE INTO recording_work(recording_id, work_id) VALUES ($1,$2)", id, workId);
        }

        if (Normalize.Text(t.Lyrics) is { } lyrics)
        {
            string format = LooksSynced(lyrics) ? "lrc" : "plain";
            tx.Exec("INSERT INTO lyrics(id, recording_id, format, content, source, updated_at) VALUES ($1,$2,$3,$4,$5,$6)",
                Ulid.NewUlid(), id, format, t.Lyrics!.Trim(), TagSource, now);
        }
        return id;
    }

    private static bool LooksSynced(string lyrics) =>
        System.Text.RegularExpressions.Regex.IsMatch(lyrics, @"^\s*\[\d{1,2}:\d{2}([.:]\d{1,3})?\]", System.Text.RegularExpressions.RegexOptions.Multiline);

    private Ulid ResolveRelease(Tx tx, ScannedFile f, Ulid? albumCredit, long now)
    {
        var t = f.Tags;
        if (ByExternal(tx, "release", "musicbrainz", t.MbReleaseId) is { } mb) return mb;

        string title = Normalize.Text(t.Album) ?? "[Unknown Album]";
        string titleKey = Normalize.Key(t.Album);
        string? year = Year(t.Date);

        // Editions share a title; tell them apart by release year when both sides know it.
        var candidates = tx.Query("""
            SELECT id, date FROM release
            WHERE title_key = $1 AND COALESCE(artist_credit_id, '') = COALESCE($2, '')
            """, r => (Id: r.Id(0), Date: r.Str(1)), titleKey, albumCredit);
        foreach (var c in candidates)
        {
            string? cy = Year(c.Date);
            if (year is null || cy is null || year == cy) return c.Id;
        }

        var groupId = ByExternal(tx, "release_group", "musicbrainz", t.MbReleaseGroupId)
            ?? tx.Single("""
                SELECT id FROM release_group WHERE title_key = $1 AND COALESCE(artist_credit_id, '') = COALESCE($2, '')
                """, r => r.IdN(0), titleKey, albumCredit);
        if (groupId is null)
        {
            groupId = Ulid.NewUlid();
            var (primary, secondary) = SplitReleaseType(t.ReleaseType, t.Compilation);
            tx.Exec("""
                INSERT INTO release_group(id, title, artist_credit_id, primary_type, secondary_types, first_release_date, title_key, created_at, updated_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$8)
                """, groupId, title, albumCredit, primary, secondary, t.OriginalDate ?? t.Date, titleKey, now);
            AddExternal(tx, "release_group", groupId.Value, "musicbrainz", t.MbReleaseGroupId);
        }
        foreach (var g in t.Genres) LinkGenre(tx, "release_group", groupId.Value, g);

        var id = Ulid.NewUlid();
        tx.Exec("""
            INSERT INTO release(id, release_group_id, title, artist_credit_id, date, country, label, catalog_number, barcode, status, title_key, created_at, updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$12)
            """, id, groupId, title, albumCredit, t.Date, t.Country, Normalize.Text(t.Label), t.CatalogNumber, t.Barcode,
            t.ReleaseStatus?.ToLowerInvariant(), titleKey, now);
        AddExternal(tx, "release", id, "musicbrainz", t.MbReleaseId);
        return id;
    }

    private static Ulid ResolveMedium(Tx tx, Ulid releaseId, int position, FileTags t, long now)
    {
        var existing = tx.Single("SELECT id FROM medium WHERE release_id = $1 AND position = $2", r => r.IdN(0), releaseId, position);
        if (existing is { } id) return id;
        id = Ulid.NewUlid();
        tx.Exec("INSERT INTO medium(id, release_id, position, format, title, track_count) VALUES ($1,$2,$3,$4,$5,$6)",
            id, releaseId, position, t.Media, Normalize.Text(t.DiscSubtitle), t.TrackTotal);
        return id;
    }

    /// <summary>Artist credit for a display string. Multiple artists only when tags say so explicitly
    /// (ARTISTS tag or several MusicBrainz ids); "A &amp; B" is otherwise kept as one name, because
    /// guessing splits ("Simon &amp; Garfunkel") makes a library dirtier, not cleaner.</summary>
    public static Ulid ResolveCredit(Tx tx, string display, IReadOnlyList<string> names, IReadOnlyList<string> mbids, long now)
    {
        var parts = new List<(string Name, string Join, string? Mbid)>();
        if (names.Count > 1)
        {
            int cursor = 0;
            for (int i = 0; i < names.Count; i++)
            {
                string join = "";
                if (i + 1 < names.Count)
                {
                    int here = display.IndexOf(names[i], cursor, StringComparison.OrdinalIgnoreCase);
                    int nextAt = here < 0 ? -1 : display.IndexOf(names[i + 1], here + names[i].Length, StringComparison.OrdinalIgnoreCase);
                    join = here >= 0 && nextAt >= 0 ? display[(here + names[i].Length)..nextAt] : " & ";
                    if (nextAt >= 0) cursor = nextAt;
                }
                parts.Add((names[i], join, i < mbids.Count && mbids.Count == names.Count ? mbids[i] : null));
            }
        }
        else
        {
            parts.Add((display, "", mbids.Count == 1 ? mbids[0] : null));
        }

        var resolved = parts.Select(p => (ArtistId: ResolveArtist(tx, p.Name, p.Mbid, now), p.Name, p.Join)).ToList();
        string key = string.Join(";", resolved.Select(p => $"{p.ArtistId}|{Normalize.Key(p.Name)}|{Normalize.Key(p.Join)}"));
        var existing = tx.Single("SELECT id FROM artist_credit WHERE credit_key = $1", r => r.IdN(0), key);
        if (existing is { } id) return id;

        id = Ulid.NewUlid();
        string displayName = string.Concat(resolved.Select(p => p.Name + p.Join));
        tx.Exec("INSERT INTO artist_credit(id, display_name, credit_key) VALUES ($1,$2,$3)", id, displayName, key);
        for (int i = 0; i < resolved.Count; i++)
            tx.Exec("INSERT INTO artist_credit_name(credit_id, position, artist_id, credited_name, join_phrase) VALUES ($1,$2,$3,$4,$5)",
                id, i, resolved[i].ArtistId, resolved[i].Name, resolved[i].Join);
        return id;
    }

    private static Ulid ResolveArtist(Tx tx, string name, string? mbid, long now)
    {
        if (ByExternal(tx, "artist", "musicbrainz", mbid) is { } byMb) return byMb;
        string key = Normalize.Key(name);
        // Prefer an artist without a conflicting MusicBrainz id.
        var candidates = tx.Query("""
            SELECT a.id, (SELECT COUNT(*) FROM external_id e WHERE e.entity_type = 'artist' AND e.entity_id = a.id AND e.provider = 'musicbrainz')
            FROM artist a WHERE a.name_key = $1
            """, r => (Id: r.Id(0), HasMb: r.GetInt64(1) > 0), key);
        foreach (var c in candidates)
        {
            if (mbid is not null && c.HasMb) continue; // different artist with the same name
            AddExternal(tx, "artist", c.Id, "musicbrainz", mbid);
            return c.Id;
        }
        var id = Ulid.NewUlid();
        tx.Exec("INSERT INTO artist(id, name, sort_name, name_key, created_at, updated_at) VALUES ($1,$2,$3,$4,$5,$5)",
            id, name, SortName(name), key, now);
        AddExternal(tx, "artist", id, "musicbrainz", mbid);
        return id;
    }

    private void ImportFileLevelExtras(Tx tx, ScannedFile f, Ulid trackId, Ulid releaseId, long now)
    {
        bool hasFront = tx.Long("SELECT COUNT(*) FROM entity_image WHERE entity_type = 'release' AND entity_id = $1 AND role = 'front'", releaseId) > 0;
        if (hasFront) return;
        try
        {
            if (f.Picture is { Length: > 0 } pic)
            {
                var img = images.Store(tx, pic, f.PictureMime ?? "image/jpeg");
                tx.Exec("INSERT OR IGNORE INTO entity_image(entity_type, entity_id, image_id, role, source) VALUES ('release',$1,$2,'front','embedded')", releaseId, img);
            }
            else if (f.FolderImagePath is { } folderImage && File.Exists(folderImage))
            {
                var data = File.ReadAllBytes(folderImage);
                string mime = folderImage.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                var img = images.Store(tx, data, mime);
                tx.Exec("INSERT OR IGNORE INTO entity_image(entity_type, entity_id, image_id, role, source) VALUES ('release',$1,$2,'front','folder')", releaseId, img);
            }
        }
        catch (IOException ex)
        {
            Log.Warn("Import", $"Could not store cover for '{f.Path}': {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ helpers

    public static Ulid? ByExternal(Tx tx, string entityType, string provider, string? externalId) =>
        string.IsNullOrWhiteSpace(externalId) ? null
            : tx.Single("SELECT entity_id FROM external_id WHERE provider = $1 AND external_id = $2 AND entity_type = $3",
                r => r.IdN(0), provider, externalId.Trim().ToLowerInvariant(), entityType);

    public static void AddExternal(Tx tx, string entityType, Ulid id, string provider, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return;
        tx.Exec("INSERT OR IGNORE INTO external_id(entity_type, entity_id, provider, external_id) VALUES ($1,$2,$3,$4)",
            entityType, id, provider, externalId.Trim().ToLowerInvariant());
    }

    public static void LinkGenre(Tx tx, string entityType, Ulid id, string name, string source = TagSource)
    {
        string? clean = Normalize.Text(name);
        if (clean is null) return;
        string key = Normalize.Key(clean);
        var genreId = tx.Single("SELECT id FROM genre WHERE name_key = $1", r => r.IdN(0), key);
        if (genreId is null)
        {
            genreId = Ulid.NewUlid();
            tx.Exec("INSERT INTO genre(id, name, name_key) VALUES ($1,$2,$3)", genreId, clean, key);
        }
        tx.Exec("INSERT OR IGNORE INTO entity_genre(entity_type, entity_id, genre_id, source) VALUES ($1,$2,$3,$4)",
            entityType, id, genreId, source);
    }

    private static void SetAttribute(Tx tx, string entityType, Ulid id, string key, string value, long now) =>
        tx.Exec("INSERT OR REPLACE INTO attribute(entity_type, entity_id, key, value, source, updated_at) VALUES ($1,$2,$3,$4,$5,$6)",
            entityType, id, key, value, TagSource, now);

    private static string? Year(string? date) =>
        date is { Length: >= 4 } && char.IsDigit(date[0]) ? date[..4] : null;

    private static (string? Primary, string? Secondary) SplitReleaseType(string? type, bool compilation)
    {
        var parts = (type ?? "").ToLowerInvariant().Split([';', '/', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        string[] primaries = ["album", "single", "ep", "broadcast", "other"];
        string? primary = parts.FirstOrDefault(p => primaries.Contains(p));
        var secondary = parts.Where(p => !primaries.Contains(p)).ToList();
        if (compilation && !secondary.Contains("compilation")) secondary.Add("compilation");
        return (primary ?? "album", secondary.Count > 0 ? string.Join(",", secondary) : null);
    }

    /// <summary>"The Beatles" → "Beatles, The". Persons are not reordered (needs data we don't have yet).</summary>
    private static string SortName(string name)
    {
        foreach (var article in new[] { "The ", "A ", "An " })
            if (name.StartsWith(article, StringComparison.OrdinalIgnoreCase) && name.Length > article.Length)
                return $"{name[article.Length..]}, {name[..(article.Length - 1)]}";
        return name;
    }
}
