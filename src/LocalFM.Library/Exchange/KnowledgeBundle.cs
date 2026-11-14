using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LocalFM.Core;
using LocalFM.Library.Enrich;
using Microsoft.Data.Sqlite;

namespace LocalFM.Library.Exchange;

public sealed record ExportOptions(bool IncludeHistory = true, bool IncludePlaylists = true);

public sealed record ExportReport(int Albums, int Tracks, int Images, int Plays);

public sealed record ImportReport(
    int AlbumsMatched, int AlbumsAdded, int ArtistsMatched, int ArtistsAdded, int FieldsUpdated,
    int Images, int Lyrics, int Playlists, int Plays, int FilesWaiting)
{
    public string Describe()
    {
        var parts = new List<string>();
        if (AlbumsMatched > 0) parts.Add($"{AlbumsMatched} album{S(AlbumsMatched)} matched");
        if (AlbumsAdded > 0) parts.Add($"{AlbumsAdded} new album{S(AlbumsAdded)}");
        if (ArtistsAdded > 0) parts.Add($"{ArtistsAdded} new artist{S(ArtistsAdded)}");
        if (FieldsUpdated > 0) parts.Add($"{FieldsUpdated} field{S(FieldsUpdated)} updated");
        if (Lyrics > 0) parts.Add($"{Lyrics} lyrics");
        if (Playlists > 0) parts.Add($"{Playlists} playlist{S(Playlists)}");
        if (Plays > 0) parts.Add($"{Plays} plays");
        if (FilesWaiting > 0) parts.Add($"{FilesWaiting} file{S(FilesWaiting)} not found here yet (they attach when scanned)");
        return parts.Count == 0 ? "Nothing new in that file." : string.Join(", ", parts) + ".";
        static string S(int n) => n == 1 ? "" : "s";
    }
}

/// <summary>
/// A portable copy of the knowledge base: a zip with a JSON-lines dump of the entities (no local paths:
/// files are identified by their audio hash) plus the images.
/// Importing merges: entities are matched by MusicBrainz id, then by id (bundles from this same library),
/// then by audio content; curated fields fill in where the local value is not locked; anything unknown is added,
/// and its files attach as soon as a scan finds audio with the same hash.
/// </summary>
public static class KnowledgeBundle
{
    public const string Format = "localfm-kb";
    public const int Version = 1;

    // Dependency order; also the whitelist of tables an import may touch.
    private static readonly string[] Tables =
    [
        "artist", "artist_credit", "artist_credit_name", "work", "recording", "recording_work", "release_group", "release",
        "medium", "track", "audio_file", "genre", "entity_genre", "image", "entity_image", "external_id", "attribute",
        "field_provenance", "lyrics", "playlist", "playlist_entry", "play_event",
    ];

    // Machine-specific columns that never leave the machine.
    private static readonly string[] AudioFileColumns =
    [
        "id", "audio_hash", "codec", "container", "duration_ms", "bit_rate", "sample_rate", "channels", "bits_per_sample",
        "track_id", "rg_track_gain", "rg_track_peak", "rg_album_gain", "rg_album_peak", "added_at", "updated_at",
    ];

    private const string PlaceholderPrefix = "localfm-kb:";

    // ================================================================== export

    public static ExportReport Export(Database db, ImageStore images, Stream output, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        using var c = db.Open();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var entry = zip.CreateEntry("entities.jsonl", CompressionLevel.Optimal);
        int images0 = 0;
        using (var s = entry.Open())
        using (var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = false }))
        {
            foreach (var table in Tables)
            {
                if (!options.IncludeHistory && table == "play_event") continue;
                if (!options.IncludePlaylists && table is "playlist" or "playlist_entry") continue;
                string cols = table == "audio_file" ? string.Join(", ", AudioFileColumns) : "*";
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT {cols} FROM {table}";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    w.WriteStartObject();
                    w.WriteString("t", table);
                    w.WriteStartObject("r");
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        string name = r.GetName(i);
                        if (r.IsDBNull(i)) { w.WriteNull(name); continue; }
                        switch (r.GetValue(i))
                        {
                            case long l: w.WriteNumber(name, l); break;
                            case double d: w.WriteNumber(name, d); break;
                            case string str: w.WriteString(name, str); break;
                            case byte[] b: w.WriteBase64String(name, b); break;
                            case var v: w.WriteString(name, Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); break;
                        }
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.Flush();
                    w.Reset();                 // one JSON document per line
                    s.WriteByte((byte)'\n');
                }
            }
        }

        foreach (var (rel, _) in c.Query("SELECT rel_path, id FROM image", r => (r.GetString(0), r.GetString(1))))
        {
            string full = images.FullPath(rel);
            if (!File.Exists(full)) continue;
            zip.CreateEntryFromFile(full, "images/" + rel.Replace('\\', '/'), CompressionLevel.NoCompression);
            images0++;
        }

        var counts = (Albums: (int)c.Scalar<long>("SELECT COUNT(*) FROM release"), Tracks: (int)c.Scalar<long>("SELECT COUNT(*) FROM track"),
            Plays: options.IncludeHistory ? (int)c.Scalar<long>("SELECT COUNT(*) FROM play_event") : 0);
        var manifest = zip.CreateEntry("manifest.json");
        using (var s = manifest.Open())
        using (var w = new Utf8JsonWriter(s, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", Version);
            w.WriteNumber("exported_at", Sql.Now());
            w.WriteNumber("albums", counts.Albums);
            w.WriteNumber("tracks", counts.Tracks);
            w.WriteNumber("images", images0);
            w.WriteNumber("plays", counts.Plays);
            w.WriteEndObject();
        }
        return new ExportReport(counts.Albums, counts.Tracks, images0, counts.Plays);
    }

    // ================================================================== import

    private sealed class Row(string table, Dictionary<string, object?> values)
    {
        public string Table { get; } = table;
        public Dictionary<string, object?> V { get; } = values;
        public string? this[string col] => V.GetValueOrDefault(col) switch { null => null, string s => s, var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) };
        public string Id => this["id"] ?? "";
    }

    /// <summary>Reads and checks a bundle (cheap; no database work).</summary>
    public static void Validate(ZipArchive zip)
    {
        var manifest = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Not a LocalFM knowledge base file (no manifest).");
        using var s = manifest.Open();
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var f) || f.GetString() != Format)
            throw new InvalidDataException("Not a LocalFM knowledge base file.");
        if (root.TryGetProperty("version", out var v) && v.GetInt32() > Version)
            throw new InvalidDataException("This file was made by a newer LocalFM. Update LocalFM to import it.");
        if (zip.GetEntry("entities.jsonl") is null) throw new InvalidDataException("The file is incomplete (no entities).");
    }

    public static ImportReport Import(Tx tx, ImageStore images, ZipArchive zip)
    {
        Validate(zip);
        var rows = ReadRows(zip);
        var columns = Tables.ToDictionary(t => t, t => tx.Query($"PRAGMA table_info({t})", r => r.GetString(1)).ToHashSet());
        var by = rows.GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.ToList());
        List<Row> T(string table) => by.GetValueOrDefault(table) ?? [];

        var map = new Dictionary<string, string>();          // bundle id -> local id (ULIDs are unique across tables)
        var added = new HashSet<string>();                   // bundle ids inserted as new local rows
        long now = Sql.Now();
        string Map(string? id) => id is null ? null! : map.GetValueOrDefault(id, id);
        bool Exists(string table, string id) => tx.Long($"SELECT COUNT(*) FROM {table} WHERE id = $1", id) > 0;

        var externals = T("external_id").ToLookup(r => r["entity_id"] ?? "");
        string? LocalByExternal(string type, string bundleId)
        {
            foreach (var e in externals[bundleId])
                if (e["provider"] is { } p && e["external_id"] is { } x && LocalFM.Library.Import.Importer.ByExternal(tx, type, p, x) is { } local)
                    return local.ToString();
            return null;
        }

        // ---- files by content: the anchor between a bundle and this library
        var bundleFiles = T("audio_file");
        var localFileByHash = new Dictionary<string, (string File, string? Track)>();
        foreach (var f in bundleFiles)
        {
            if (f["audio_hash"] is not { } hash || localFileByHash.ContainsKey(hash)) continue;
            var local = tx.Single("SELECT id, track_id FROM audio_file WHERE audio_hash = $1 ORDER BY missing LIMIT 1",
                r => (File: r.GetString(0), Track: r.IsDBNull(1) ? null : r.GetString(1)), hash);
            if (local.File is not null) localFileByHash[hash] = local;
        }
        var bundleTrackLocal = new Dictionary<string, string>(); // bundle track -> local track (via file content)
        foreach (var f in bundleFiles)
            if (f["track_id"] is { } bt && f["audio_hash"] is { } h && localFileByHash.TryGetValue(h, out var lf) && lf.Track is { } lt)
                bundleTrackLocal.TryAdd(bt, lt);

        // ---- artists
        int artistsMatched = 0, artistsAdded = 0;
        foreach (var a in T("artist"))
        {
            string? local = LocalByExternal("artist", a.Id) ?? (Exists("artist", a.Id) ? a.Id : null);
            if (local is null && a["name_key"] is { } key)
            {
                var same = tx.Query("SELECT id FROM artist WHERE name_key = $1", r => r.GetString(0), key);
                if (same.Count == 1) local = same[0];
            }
            if (local is not null) { map[a.Id] = local; artistsMatched++; }
            else { Insert(tx, columns, a, Map); added.Add(a.Id); artistsAdded++; }
        }

        // ---- artist credits: reuse a local credit naming the same artists in the same order
        var creditNames = T("artist_credit_name").GroupBy(n => n["credit_id"] ?? "").ToDictionary(g => g.Key, g => g.OrderBy(n => n.V.GetValueOrDefault("position") as long? ?? 0).ToList());
        foreach (var cr in T("artist_credit"))
        {
            if (Exists("artist_credit", cr.Id)) { map[cr.Id] = cr.Id; continue; }
            var names = creditNames.GetValueOrDefault(cr.Id) ?? [];
            // The dedup key names artists by id, so it is rebuilt with this library's ids.
            string key = string.Join(";", names.Select(n =>
                $"{Map(n["artist_id"])}|{Normalize.Key(n["credited_name"])}|{Normalize.Key(n["join_phrase"])}"));
            string? local = tx.Single("SELECT id FROM artist_credit WHERE credit_key = $1", r => r.GetString(0), key);
            if (local is not null) { map[cr.Id] = local; continue; }
            Insert(tx, columns, new Row("artist_credit", new Dictionary<string, object?>(cr.V) { ["credit_key"] = key }), Map);
            foreach (var n in names) Insert(tx, columns, n, Map);
            added.Add(cr.Id);
        }

        // ---- releases (and their groups): MusicBrainz id, same id, or most of their tracks' audio
        var bundleMediumRelease = T("medium").ToDictionary(m => m.Id, m => m["release_id"] ?? "");
        var bundleTracksByRelease = T("track").GroupBy(t => bundleMediumRelease.GetValueOrDefault(t["medium_id"] ?? "") ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());
        int albumsMatched = 0, albumsAdded = 0;
        var releaseLocal = new Dictionary<string, string>();
        foreach (var rel in T("release"))
        {
            string? local = LocalByExternal("release", rel.Id) ?? (Exists("release", rel.Id) ? rel.Id : null);
            if (local is null)
            {
                var votes = (bundleTracksByRelease.GetValueOrDefault(rel.Id) ?? [])
                    .Select(t => bundleTrackLocal.GetValueOrDefault(t.Id))
                    .Where(t => t is not null)
                    .Select(t => tx.Single("SELECT m.release_id FROM track t JOIN medium m ON m.id = t.medium_id WHERE t.id = $1",
                        r => r.GetString(0), t!))
                    .Where(x => x is not null).GroupBy(x => x!).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (votes is not null) local = votes.Key;
            }
            if (local is not null) releaseLocal[rel.Id] = local;
        }
        foreach (var g in T("release_group"))
        {
            string? local = LocalByExternal("release_group", g.Id) ?? (Exists("release_group", g.Id) ? g.Id : null);
            local ??= T("release").Where(r => r["release_group_id"] == g.Id && releaseLocal.ContainsKey(r.Id))
                .Select(r => tx.Single("SELECT release_group_id FROM release WHERE id = $1", x => x.GetString(0), releaseLocal[r.Id]))
                .FirstOrDefault(x => x is not null);
            if (local is not null) map[g.Id] = local;
            else { Insert(tx, columns, g, Map); added.Add(g.Id); }
        }
        foreach (var rel in T("release"))
        {
            if (releaseLocal.TryGetValue(rel.Id, out var local)) { map[rel.Id] = local; albumsMatched++; }
            else { Insert(tx, columns, rel, Map); added.Add(rel.Id); albumsAdded++; }
        }

        // ---- recordings, media, tracks
        foreach (var rec in T("recording"))
        {
            string? local = LocalByExternal("recording", rec.Id) ?? (Exists("recording", rec.Id) ? rec.Id : null);
            local ??= T("track").Where(t => t["recording_id"] == rec.Id && bundleTrackLocal.ContainsKey(t.Id))
                .Select(t => tx.Single("SELECT recording_id FROM track WHERE id = $1", r => r.GetString(0), bundleTrackLocal[t.Id]))
                .FirstOrDefault(x => x is not null);
            if (local is not null) map[rec.Id] = local;
            else { Insert(tx, columns, rec, Map); added.Add(rec.Id); }
        }
        foreach (var w in T("work"))
            if (!Exists("work", w.Id)) { Insert(tx, columns, w, Map); added.Add(w.Id); }
        foreach (var rw in T("recording_work")) Insert(tx, columns, rw, Map, orIgnore: true);
        foreach (var m in T("medium"))
        {
            string? local = Exists("medium", m.Id) ? m.Id
                : tx.Single("SELECT id FROM medium WHERE release_id = $1 AND position = $2", r => r.GetString(0), Map(m["release_id"]), m.V.GetValueOrDefault("position"));
            if (local is not null) map[m.Id] = local;
            else { Insert(tx, columns, m, Map); added.Add(m.Id); }
        }
        foreach (var t in T("track"))
        {
            string? local = bundleTrackLocal.GetValueOrDefault(t.Id) ?? (Exists("track", t.Id) ? t.Id : null)
                ?? (added.Contains(t["medium_id"] ?? "") ? null
                    : tx.Single("SELECT id FROM track WHERE medium_id = $1 AND position = $2", r => r.GetString(0), Map(t["medium_id"]), t.V.GetValueOrDefault("position")));
            if (local is not null) map[t.Id] = local;
            else { Insert(tx, columns, t, Map); added.Add(t.Id); }
        }

        // ---- files: known content maps to the local file; unknown waits for a scan under a placeholder path
        int waiting = 0;
        foreach (var f in bundleFiles)
        {
            if (f["audio_hash"] is { } h && localFileByHash.TryGetValue(h, out var lf)) { map[f.Id] = lf.File; continue; }
            if (Exists("audio_file", f.Id)) { map[f.Id] = f.Id; continue; }
            var v = new Dictionary<string, object?>(f.V)
            {
                ["path"] = PlaceholderPrefix + f.Id, ["size"] = 0L, ["mtime"] = 0L, ["missing"] = 1L, ["source_id"] = null,
            };
            Insert(tx, columns, new Row("audio_file", v), Map);
            added.Add(f.Id);
            waiting++;
        }

        // ---- facts about entities that already existed: curated fields fill in unless locked here
        int fields = 0;
        foreach (var p in T("field_provenance"))
        {
            string type = p["entity_type"] ?? "", id = p["entity_id"] ?? "", field = p["field"] ?? "", source = p["source"] ?? "";
            if (added.Contains(id)) { Insert(tx, columns, p, Map, orIgnore: true); continue; }
            if (source == "tags" || !map.TryGetValue(id, out var local) || !Tables.Contains(type)) continue;
            var bundleRow = by.GetValueOrDefault(type)?.FirstOrDefault(r => r.Id == id);
            if (bundleRow is null || !bundleRow.V.TryGetValue(field, out var value)) continue;
            string? text = value switch { null => null, string s => s, var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) };
            if (field.EndsWith("_id", StringComparison.Ordinal) && text is not null) text = Map(text);
            if (!columns[type].Contains(field)) continue;   // only real columns of this schema
            var current = tx.Single($"SELECT {field} FROM {type} WHERE id = $1",
                r => r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), local);
            bool lockIt = p["locked"] == "1";
            if (current == text && (!lockIt || tx.Long("SELECT locked FROM field_provenance WHERE entity_type=$1 AND entity_id=$2 AND field=$3", type, local, field) == 1))
                continue;                                   // already the same here
            try
            {
                if (MetadataApplier.SetField(tx, type, Ulid.Parse(local), field, text, source, now, lockIt)) fields++;
            }
            catch (ArgumentException) { /* not a field this version lets imports write */ }
        }

        // ---- attached facts (local data wins on conflicts)
        foreach (var gr in T("genre"))
        {
            string? local = tx.Single("SELECT id FROM genre WHERE name_key = $1", r => r.GetString(0), gr["name_key"]);
            if (local is not null) map[gr.Id] = local;
            else Insert(tx, columns, gr, Map);
        }
        foreach (var eg in T("entity_genre")) InsertAttached(tx, columns, eg, Map);

        int imagesStored = 0;
        foreach (var img in T("image"))
        {
            var zipEntry = img["rel_path"] is { } rel ? zip.GetEntry("images/" + rel.Replace('\\', '/')) : null;
            if (zipEntry is null || img["mime"] is not { } mime) continue;
            using var ms = new MemoryStream();
            using (var s = zipEntry.Open()) s.CopyTo(ms);
            map[img.Id] = images.Store(tx, ms.GetBuffer().AsSpan(0, (int)ms.Length), mime).ToString();
            imagesStored++;
        }
        foreach (var ei in T("entity_image"))
        {
            if (ei["image_id"] is not { } imageId || !map.ContainsKey(imageId)) continue; // image file missing from the bundle
            // One front cover per release: keep the local one if there is one.
            if (ei["role"] == "front" && ei["entity_type"] == "release" && !added.Contains(ei["entity_id"] ?? "")
                && tx.Long("SELECT COUNT(*) FROM entity_image WHERE entity_type='release' AND entity_id=$1 AND role='front'", Map(ei["entity_id"])) > 0)
                continue;
            InsertAttached(tx, columns, ei, Map);
        }
        foreach (var e in T("external_id"))
        {
            // An entity keeps the identity it has here; a second id from the same provider would be ambiguous.
            if (!added.Contains(e["entity_id"] ?? "") && tx.Long("SELECT COUNT(*) FROM external_id WHERE entity_type=$1 AND entity_id=$2 AND provider=$3",
                    e["entity_type"], Map(e["entity_id"]), e["provider"]) > 0)
                continue;
            InsertAttached(tx, columns, e, Map);
        }
        foreach (var a in T("attribute")) InsertAttached(tx, columns, a, Map);

        int lyrics = 0;
        foreach (var l in T("lyrics"))
        {
            if (Exists("lyrics", l.Id)) continue;
            string rec = Map(l["recording_id"]);
            if (tx.Long("SELECT COUNT(*) FROM lyrics WHERE recording_id = $1 AND content = $2", rec, l["content"]) > 0) continue;
            Insert(tx, columns, l, Map);
            lyrics++;
        }

        int playlists = 0;
        var newPlaylists = new HashSet<string>();
        foreach (var p in T("playlist"))
        {
            if (Exists("playlist", p.Id)) continue;
            Insert(tx, columns, p, Map);
            newPlaylists.Add(p.Id);
            playlists++;
        }
        foreach (var e in T("playlist_entry"))
            if (newPlaylists.Contains(e["playlist_id"] ?? "")) Insert(tx, columns, e, Map, orIgnore: true);

        int plays = 0;
        foreach (var e in T("play_event"))
        {
            if (Exists("play_event", e.Id)) continue;
            Insert(tx, columns, e, Map);
            plays++;
        }

        Log.Info("KB", $"Imported bundle: {albumsMatched} albums matched, {albumsAdded} added, {fields} fields, {waiting} files waiting");
        return new ImportReport(albumsMatched, albumsAdded, artistsMatched, artistsAdded, fields, imagesStored, lyrics, playlists, plays, waiting);
    }

    private static List<Row> ReadRows(ZipArchive zip)
    {
        var list = new List<Row>();
        using var s = zip.GetEntry("entities.jsonl")!.Open();
        using var reader = new StreamReader(s, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string table = root.GetProperty("t").GetString() ?? "";
            if (!Tables.Contains(table)) continue;
            var values = new Dictionary<string, object?>();
            foreach (var p in root.GetProperty("r").EnumerateObject())
                values[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => p.Value.TryGetInt64(out long l) ? l : p.Value.GetDouble(),
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => p.Value.GetRawText(),
                };
            list.Add(new Row(table, values));
        }
        return list;
    }

    // Columns holding ids of other entities, remapped on insert.
    private static readonly HashSet<string> RefColumns =
    [
        "artist_id", "credit_id", "artist_credit_id", "recording_id", "work_id", "release_group_id", "release_id", "medium_id",
        "track_id", "audio_file_id", "genre_id", "image_id", "playlist_id", "entity_id",
    ];

    private static void Insert(Tx tx, Dictionary<string, HashSet<string>> columns, Row row, Func<string?, string> map, bool orIgnore = false)
    {
        var known = columns[row.Table];
        var cols = row.V.Keys.Where(known.Contains).ToList();   // only columns this schema has (never SQL from the file)
        if (cols.Count == 0) return;
        var args = cols.Select(c => RefColumns.Contains(c) && row.V[c] is string s ? map(s) : row.V[c]).ToArray();
        string sql = $"INSERT {(orIgnore ? "OR IGNORE " : "")}INTO {row.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", cols.Select((_, i) => "$" + (i + 1)))})";
        tx.Exec(sql, args);
    }

    private static void InsertAttached(Tx tx, Dictionary<string, HashSet<string>> columns, Row row, Func<string?, string> map) =>
        Insert(tx, columns, row, map, orIgnore: true);
}
