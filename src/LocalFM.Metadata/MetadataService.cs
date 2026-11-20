using System.Globalization;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Library.Import;
using LocalFM.Native;

namespace LocalFM.Metadata;

/// <summary>What we know locally about an album, used to search and to rank candidates.</summary>
public sealed record AlbumQuery(Ulid ReleaseId, string Title, string? Artist, int TrackCount, string? Year, IReadOnlyList<long?> TrackLengthsMs);

public sealed record LyricsView(LyricsData Data, IReadOnlyList<LyricLine> Lines)
{
    public bool Synced => Data.Format != LyricsFormat.Plain;
}

/// <summary>
/// Online enrichment: finds candidates, applies a chosen one (respecting locked fields), fetches
/// covers and lyrics. Everything is cached and rate-limited; nothing here runs on the playback path.
/// </summary>
public sealed record ArtistLookupResult(bool Updated, IReadOnlyList<ArtistCandidate> Candidates, string? Message);

public sealed class MetadataService
{
    private const string OnlineKey = "metadata.online";
    private const string LyricsKey = "lyrics.autofetch";
    private const string WordEstimateKey = "lyrics.wordEstimate";

    private readonly Database _db;
    private readonly History _kv;
    private readonly MetadataApplier _applier;
    private readonly ImageStore _images;

    public MetadataService(Database db, ImageStore images, History kv, HttpClient? client = null)
    {
        _db = db;
        _kv = kv;
        _images = images;
        _applier = new MetadataApplier(images);
        Http = new CachedHttp(db, client ?? CachedHttp.CreateClient());
        Http.SetRateLimit(MusicBrainz.Provider, TimeSpan.FromSeconds(1.1));
        Http.SetRateLimit(CoverArtArchive.Provider, TimeSpan.FromSeconds(0.5));
        Http.SetRateLimit(Itunes.Provider, TimeSpan.FromSeconds(3));
        Http.SetRateLimit(Lrclib.Provider, TimeSpan.FromSeconds(0.5));
        Http.SetRateLimit(Wikimedia.Provider, TimeSpan.FromSeconds(0.25));
        MusicBrainz = new MusicBrainz(Http);
        Artists = new MusicBrainzArtists(Http);
        Wiki = new Wikimedia(Http);
        CoverArt = new CoverArtArchive(Http);
        Itunes = new Itunes(Http);
        Lrclib = new Lrclib(Http);
        Albums = new AlbumFinder(MusicBrainz, db);
        Organizer = new AlbumOrganizer(this, db);
        Scout = new AlbumScout(this, db, kv);
    }

    /// <summary>"Which album is this song on?"</summary>
    public AlbumFinder Albums { get; }
    /// <summary>Puts songs on the albums they belong to.</summary>
    public AlbumOrganizer Organizer { get; }
    /// <summary>Tidy up's background album check.</summary>
    public AlbumScout Scout { get; }

    public CachedHttp Http { get; }
    public MusicBrainz MusicBrainz { get; }
    public CoverArtArchive CoverArt { get; }
    public Itunes Itunes { get; }
    public Lrclib Lrclib { get; }
    public MusicBrainzArtists Artists { get; }
    public Wikimedia Wiki { get; }

    /// <summary>Master switch: offline-first, so everything online can be turned off.</summary>
    public bool OnlineEnabled
    {
        get => _kv.Get(OnlineKey) != "0";
        set => _ = _kv.SetAsync(OnlineKey, value ? "1" : "0");
    }

    public bool AutoFetchLyrics
    {
        get => _kv.Get(LyricsKey) != "0";
        set => _ = _kv.SetAsync(LyricsKey, value ? "1" : "0");
    }

    /// <summary>Sweep through lines word by word even when the lyrics are only timed per line (an estimate).</summary>
    public bool EstimateWordTiming
    {
        get => _kv.Get(WordEstimateKey) != "0";
        set => _ = _kv.SetAsync(WordEstimateKey, value ? "1" : "0");
    }

    // ------------------------------------------------------------------ albums

    public AlbumQuery DescribeAlbum(Ulid releaseId)
    {
        using var c = _db.Open();
        var head = c.QuerySingle("""
            SELECT rel.title, ac.display_name, rel.date FROM release rel LEFT JOIN artist_credit ac ON ac.id = rel.artist_credit_id WHERE rel.id = $1
            """, r => (Title: r.GetString(0), Artist: r.Str(1), Date: r.Str(2)), releaseId);
        var lengths = c.Query("""
            SELECT COALESCE((SELECT MAX(f.duration_ms) FROM audio_file f WHERE f.track_id = t.id), t.length_ms)
            FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1 ORDER BY m.position, t.position
            """, r => r.Long(0), releaseId);
        return new AlbumQuery(releaseId, head.Title, head.Artist, lengths.Count,
            head.Date is { Length: >= 4 } d ? d[..4] : null, lengths);
    }

    /// <summary>MusicBrainz releases matching the album, best first. Score blends MusicBrainz's text score
    /// with how well the track count and year fit what we have.</summary>
    public async Task<List<ReleaseCandidate>> SearchAlbumAsync(AlbumQuery q, string? titleOverride = null, string? artistOverride = null, CancellationToken ct = default)
    {
        if (!OnlineEnabled) return [];
        string title = string.IsNullOrWhiteSpace(titleOverride) ? q.Title : titleOverride;
        string? artist = artistOverride ?? q.Artist;
        var hits = await MusicBrainz.SearchReleasesAsync(title, string.IsNullOrWhiteSpace(artist) ? null : artist, ct).ConfigureAwait(false);
        var inLibrary = LocalReleaseIds(q.ReleaseId);
        return hits
            .Select(h => h with { InLibrary = inLibrary.Contains(h.Id) })
            .Select(h => h with { Score = Rank(h, q) })
            .OrderByDescending(h => h.Score)
            .ToList();
    }

    /// <summary>MusicBrainz ids of the other local albums.</summary>
    private HashSet<string> LocalReleaseIds(Ulid except)
    {
        using var c = _db.Open();
        return c.Query("SELECT external_id FROM external_id WHERE provider = 'musicbrainz' AND entity_type = 'release' AND entity_id <> $1",
            r => r.GetString(0), except).ToHashSet();
    }

    private static double Rank(ReleaseCandidate c, AlbumQuery q)
    {
        double score = c.Score * 0.6;
        // Part of an album (typically lone songs matched one by one): the edition already in the library is where it
        // belongs, so applying it joins that album instead of starting a second copy of it.
        if (q.TrackCount < c.TrackCount)
            if (c.InLibrary) score += 40;
        // A lone song says nothing about the release's size (it is usually one track of a full album).
        if (q.TrackCount == 1)
            score += 15;
        else if (q.TrackCount > 1)
            score += c.TrackCount == q.TrackCount ? 25 : Math.Max(0, 25 - 5 * Math.Abs(c.TrackCount - q.TrackCount));
        // CD-Rs are mostly promos and bootlegs: rarely the edition someone ripped.
        if (c.Format?.Contains("CD-R", StringComparison.OrdinalIgnoreCase) == true) score -= 10;
        if (q.Year is not null && c.Date is { Length: >= 4 } d)
            score += d[..4] == q.Year ? 10 : 0;
        if (string.Equals(c.Status, "Official", StringComparison.OrdinalIgnoreCase)) score += 5;
        return Math.Round(Math.Min(100, score));
    }

    /// <summary>Fetches full release data (+ cover) and writes it onto the local release.</summary>
    public async Task<ApplyResult?> ApplyReleaseAsync(Ulid releaseId, string mbid, bool fetchCover = true, CancellationToken ct = default)
    {
        var data = await MusicBrainz.GetReleaseAsync(mbid, ct).ConfigureAwait(false);
        if (data is null) return null;
        ImageBytes? cover = null;
        if (fetchCover)
        {
            cover = await CoverArt.FrontAsync(data.Id, data.Group.Id, ct).ConfigureAwait(false)
                ?? await Itunes.FrontAsync(data.Title, ReleaseData.CreditText(data.Credit), ct).ConfigureAwait(false);
        }
        var fileTitles = FileTagTitles(releaseId);
        var result = await _db.Writer.Run((c, t) =>
            _applier.ApplyRelease(new Tx(c, t), releaseId, data, cover?.Data, cover?.Mime, cover?.Source, fileTitles)).ConfigureAwait(false);
        Log.Info("Metadata", $"Applied {data.Provider} release {data.Id} to {releaseId}: {result}");
        return result;
    }

    /// <summary>
    /// Re-applies the release an album is already matched to, e.g. after songs were moved onto it: they pick up
    /// their real number, credit and ids. Does nothing for an unmatched album. Locked fields stay as they are.
    /// </summary>
    public async Task<ApplyResult?> ReapplyMatchAsync(Ulid releaseId, CancellationToken ct = default)
    {
        string? mbid;
        using (var c = _db.Open())
            mbid = c.QuerySingle("SELECT external_id FROM external_id WHERE entity_type = 'release' AND entity_id = $1 AND provider = 'musicbrainz'",
                r => r.Str(0), releaseId);
        return mbid is null ? null : await ApplyReleaseAsync(releaseId, mbid, fetchCover: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches an album to another edition of it when that edition fits more of its songs than the one it is
    /// matched to now (bonus tracks of a special edition). Returns the apply result, or null when it stays as it is.
    /// </summary>
    public async Task<ApplyResult?> SwitchEditionIfBetterAsync(Ulid releaseId, string mbid, int matchedNow, CancellationToken ct = default)
    {
        var data = await MusicBrainz.GetReleaseAsync(mbid, ct).ConfigureAwait(false);
        if (data is null) return null;
        var fileTitles = FileTagTitles(releaseId);
        var result = await _db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            return MetadataApplier.CountMatches(tx, releaseId, data, fileTitles) > matchedNow
                ? _applier.ApplyRelease(tx, releaseId, data, null, null, null, fileTitles)
                : null;
        }).ConfigureAwait(false);
        if (result is not null) Log.Info("Metadata", $"Switched {releaseId} to edition {data.Id}: {result}");
        return result;
    }

    /// <summary>The title each track's file carries in its own tags. The stored title may have drifted from it
    /// (an earlier wrong match); the tag is a second chance to recognise the song.</summary>
    private Dictionary<Ulid, string> FileTagTitles(Ulid releaseId)
    {
        List<(Ulid Track, string Path)> files;
        using (var c = _db.Open())
            files = c.Query("""
                SELECT t.id, f.path FROM track t JOIN medium m ON m.id = t.medium_id JOIN audio_file f ON f.track_id = t.id
                WHERE m.release_id = $1 AND f.missing = 0
                """, r => (r.Id(0), r.GetString(1)), releaseId);
        var titles = new Dictionary<Ulid, string>();
        foreach (var (track, path) in files)
        {
            if (titles.ContainsKey(track) || !MediaProbe.TryOpen(path, out var probe) || probe is null) continue;
            using (probe)
                if (FileTags.Parse(probe.Tags).Title is { Length: > 0 } title) titles[track] = title;
        }
        return titles;
    }

    /// <summary>Downloads a front cover for a release that has none (by MusicBrainz id when known, else iTunes).</summary>
    public async Task<bool> FetchCoverAsync(Ulid releaseId, CancellationToken ct = default)
    {
        if (!OnlineEnabled) return false;
        string? mb, mbGroup;
        AlbumQuery q = DescribeAlbum(releaseId);
        using (var c = _db.Open())
        {
            mb = c.Scalar<string>("SELECT external_id FROM external_id WHERE entity_type='release' AND entity_id=$1 AND provider='musicbrainz'", releaseId);
            mbGroup = c.Scalar<string>("""
                SELECT e.external_id FROM external_id e JOIN release r ON r.release_group_id = e.entity_id
                WHERE e.entity_type='release_group' AND r.id=$1 AND e.provider='musicbrainz'
                """, releaseId);
        }
        var img = (mb ?? mbGroup) is not null ? await CoverArt.FrontAsync(mb, mbGroup, ct).ConfigureAwait(false) : null;
        img ??= await Itunes.FrontAsync(q.Title, q.Artist, ct).ConfigureAwait(false);
        if (img is null) return false;
        await _db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            var id = _images.Store(tx, img.Data, img.Mime);
            tx.Exec("INSERT OR IGNORE INTO entity_image(entity_type, entity_id, image_id, role, source) VALUES ('release',$1,$2,'front',$3)", releaseId, id, img.Source);
        }).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ artists

    /// <summary>Profiles older than this are refreshed when the artist page is opened.</summary>
    public static readonly TimeSpan ProfileMaxAge = TimeSpan.FromDays(90);

    /// <summary>Whether opening the artist page should quietly fetch (or refresh) its profile.</summary>
    public bool ShouldAutoFetchProfile(ArtistProfileView v) =>
        OnlineEnabled && v.Mbid is not null && (v.FetchedAt is not { } f || DateTimeOffset.UtcNow - f > ProfileMaxAge);

    /// <summary>
    /// Fetches an artist's profile: MusicBrainz (by its known id, the given one, or an unambiguous name search),
    /// then the Wikidata photo and the Wikipedia summary in the person's language (English as fallback).
    /// When the name is ambiguous, nothing is stored and the candidates are returned to choose from.
    /// </summary>
    public async Task<ArtistLookupResult> FetchArtistProfileAsync(Ulid artistId, string? mbid = null, CancellationToken ct = default)
    {
        if (!OnlineEnabled) return new(false, [], "Online lookups are turned off (Sources page).");
        string? name;
        using (var c = _db.Open())
        {
            name = c.Scalar<string>("SELECT name FROM artist WHERE id = $1", artistId);
            mbid ??= c.Scalar<string>("SELECT external_id FROM external_id WHERE entity_type='artist' AND entity_id=$1 AND provider=$2",
                artistId, MusicBrainz.Provider);
        }
        if (name is null) return new(false, [], "This artist no longer exists.");

        if (mbid is null)
        {
            // YouTube-style names ("Scorpions - Topic", "ScorpionsVEVO") search better tidied up.
            string query = LocalFM.Library.Edit.Cleanup.CleanArtist(name)?.Text ?? name;
            var found = await Artists.SearchAsync(query, ct).ConfigureAwait(false);
            string key = Normalize.Key(query);
            var exact = found.Where(f => f.Score >= 90 && Normalize.Key(f.Name) == key).ToList();
            if (exact.Count == 1) mbid = exact[0].Mbid;
            else return new(false, found.Take(8).ToList(), found.Count == 0
                ? $"MusicBrainz knows no artist called “{name}”."
                : $"More than one artist could be “{name}”. Pick the right one.");
        }

        var profile = await Artists.GetAsync(mbid, ct).ConfigureAwait(false);
        if (profile is null) return new(false, [], "MusicBrainz did not answer. Try again later.");

        ImageBytes? photo = null;
        if (profile.WikidataId is { } qid)
        {
            try
            {
                string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } l and not "iv" ? l : "en";
                if (await Wiki.EntityAsync(qid, lang, ct).ConfigureAwait(false) is { } entity)
                {
                    profile = profile with { Description = entity.Description };
                    string? title = entity.Sitelinks.GetValueOrDefault(lang + "wiki");
                    string titleLang = lang;
                    if (title is null && lang != "en") { title = entity.Sitelinks.GetValueOrDefault("enwiki"); titleLang = "en"; }
                    if (title is not null && await Wiki.SummaryAsync(titleLang, title, ct).ConfigureAwait(false) is { } summary)
                        profile = profile with { Bio = summary.Extract, BioUrl = summary.Url, BioSource = "Wikipedia" };
                    if (entity.Image is { } file) photo = await Wiki.FileAsync(file, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The MusicBrainz part is still worth keeping.
                Log.Warn("Artist", $"Wikimedia lookup for {qid} failed: {ex.Message}");
            }
        }

        await _db.Writer.Run((c, t) => ArtistProfiles.Apply(new Tx(c, t), _images, artistId, profile, photo?.Data, photo?.Mime)).ConfigureAwait(false);
        Log.Info("Artist", $"Profile for '{name}' from MusicBrainz {mbid}{(profile.Bio is null ? "" : " + Wikipedia")}{(photo is null ? "" : " + photo")}");
        return new(true, [], null);
    }

    // ------------------------------------------------------------------ lyrics

    /// <summary>Best stored lyrics for the recording: synced before plain; null when none.</summary>
    public LyricsView? StoredLyrics(Ulid recordingId)
    {
        using var c = _db.Open();
        var row = c.QuerySingle("""
            SELECT format, content, source, language FROM lyrics WHERE recording_id = $1
            ORDER BY CASE format WHEN 'elrc' THEN 0 WHEN 'lrc' THEN 1 ELSE 2 END,
                     CASE source WHEN 'user' THEN 0 ELSE 1 END
            LIMIT 1
            """, r => new LyricsData(r.GetString(2), FormatOf(r.GetString(0)), r.GetString(1), r.Str(3)), recordingId);
        return row is null ? null : new LyricsView(row, Lrc.Parse(row.Content, row.Format));
    }

    /// <summary>Stored lyrics, or (when allowed) fetched from LRCLIB and stored. <paramref name="force"/> looks up
    /// again even when synced lyrics are stored. Never throws.</summary>
    public async Task<LyricsView?> GetLyricsAsync(TrackInfo track, bool allowFetch, CancellationToken ct = default, bool force = false)
    {
        var stored = StoredLyrics(track.RecordingId);
        if ((stored is { Synced: true } && !force) || !allowFetch || !OnlineEnabled || track.Artist is null) return stored;
        try
        {
            var all = await LyricsCandidatesAsync(track, ct).ConfigureAwait(false);
            if (all.Count == 0) return stored;
            await StoreLrclib(track.RecordingId, all[0]).ConfigureAwait(false);
            return StoredLyrics(track.RecordingId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Lyrics", $"Lyrics lookup failed for '{track.Title}': {ex.Message}");
            return stored;
        }
    }

    /// <summary>Replaces the stored LRCLIB lyrics with the next synced version (for when the timing is off).
    /// Returns the new lyrics, which version it is (1-based) and how many there are; null when there is no other.</summary>
    public async Task<(LyricsView View, int Version, int Versions)?> NextLyricsVersionAsync(TrackInfo track, CancellationToken ct = default)
    {
        if (!OnlineEnabled || track.Artist is null) return null;
        var synced = (await LyricsCandidatesAsync(track, ct).ConfigureAwait(false)).Where(r => r.Synced is not null).ToList();
        if (synced.Count < 2) return null;
        // Versions with identical text and timing are one version.
        synced = synced.DistinctBy(r => r.Synced!.Content.Trim()).ToList();
        if (synced.Count < 2) return null;
        string? current = StoredLyrics(track.RecordingId)?.Data.Content.Trim();
        int at = synced.FindIndex(r => r.Synced!.Content.Trim() == current);
        int next = (at + 1) % synced.Count;
        await StoreLrclib(track.RecordingId, synced[next]).ConfigureAwait(false);
        return StoredLyrics(track.RecordingId) is { } view ? (view, next + 1, synced.Count) : null;
    }

    private async Task<List<Lrclib.Result>> LyricsCandidatesAsync(TrackInfo track, CancellationToken ct)
    {
        var all = await Lrclib.CandidatesAsync(track.Artist!, track.Title, track.Album,
            track.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, ct).ConfigureAwait(false);
        return all.Where(r => r.Synced is not null || r.Plain is not null).ToList();
    }

    private Task StoreLrclib(Ulid recordingId, Lrclib.Result res) => _db.Writer.Run((c, t) =>
    {
        var tx = new Tx(c, t);
        tx.Exec("DELETE FROM lyrics WHERE recording_id = $1 AND source = $2", recordingId, Lrclib.Provider);
        foreach (var l in new[] { res.Synced, res.Plain })
        {
            if (l is null) continue;
            tx.Exec("INSERT INTO lyrics(id, recording_id, format, content, source, updated_at) VALUES ($1,$2,$3,$4,$5,$6)",
                Ulid.NewUlid(), recordingId, FormatName(l.Format), l.Content, l.Provider, Sql.Now());
        }
    });

    public static string FormatName(LyricsFormat f) => f switch { LyricsFormat.Lrc => "lrc", LyricsFormat.EnhancedLrc => "elrc", _ => "plain" };
    public static LyricsFormat FormatOf(string s) => s switch { "lrc" => LyricsFormat.Lrc, "elrc" => LyricsFormat.EnhancedLrc, _ => LyricsFormat.Plain };
}
