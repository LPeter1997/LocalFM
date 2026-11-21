using System.Net;
using LocalFM.Library;
using LocalFM.Core;
using LocalFM.Library.Enrich;
using LocalFM.Library.Import;
using LocalFM.Library.Tests;

namespace LocalFM.Metadata.Tests;

public class MetadataTests
{
    private static readonly string[] OkcTitles =
    [
        "Airbag", "Paranoid Android", "Subterranean Homesick Alien", "Exit Music (for a Film)", "Let Down", "Karma Police",
        "Fitter Happier", "Electioneering", "Climbing Up the Walls", "No Surprises", "Lucky", "The Tourist",
    ];

    private static (MetadataService Svc, FakeHandler Http) Service(TempLibrary lib)
    {
        var fake = new FakeHandler()
            .On("musicbrainz.org/ws/2/release/?query", "mb-search-okc.json")
            .On("musicbrainz.org/ws/2/release/6a09041b", "mb-release-okc.json");
        var svc = new MetadataService(lib.Db, lib.Images, new History(lib.Db), new HttpClient(fake));
        return (svc, fake);
    }

    /// <summary>A sloppily tagged local rip: lowercase artist, a few wrong titles, no dates.</summary>
    private static async Task<AlbumSummary> SloppyOkComputer(TempLibrary lib)
    {
        for (int i = 0; i < 12; i++)
        {
            string title = i switch { 0 => "airbag", 3 => "Exit Music", 6 => "Track 07", _ => OkcTitles[i] };
            lib.MakeTrack($"okc/{i + 1:00}.flac", new()
            {
                ["title"] = title, ["artist"] = "radiohead", ["album"] = "ok computer", ["track"] = (i + 1).ToString(),
            }, seconds: 0.3);
        }
        await lib.ScanAsync();
        var album = lib.Queries.Albums().Single();
        await SetFileLengths(lib, album.ReleaseId, OkcLengths);
        return album;
    }

    private static readonly long[] OkcLengths =
        [284400, 383493, 267706, 264800, 299266, 261626, 117333, 230640, 285200, 228533, 259626, 324533];

    /// <summary>Fixture files are a fraction of a second long; give them the lengths real rips would have.</summary>
    private static Task SetFileLengths(TempLibrary lib, Ulid releaseId, long[] lengths) => lib.Db.Writer.Run((c, t) =>
    {
        var tx = new Tx(c, t);
        var tracks = tx.Query("SELECT t.id FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1 ORDER BY m.position, t.position",
            r => r.Id(0), releaseId);
        for (int i = 0; i < tracks.Count; i++)
            tx.Exec("UPDATE audio_file SET duration_ms = $2 WHERE track_id = $1", tracks[i], lengths[i]);
    });

    /// <summary>One song from an album, ripped on its own (the "The Zoo" from Comeblack case).</summary>
    private static async Task<AlbumSummary> LoneSong(TempLibrary lib)
    {
        lib.MakeTrack("single/karma.mp3", new() { ["title"] = "Karma Police", ["artist"] = "Radiohead - Topic", ["album"] = "Karma Police" }, seconds: 0.3);
        await lib.ScanAsync();
        var album = lib.Queries.Albums().Single();
        await SetFileLengths(lib, album.ReleaseId, [261626 + 1500]);
        return album;
    }

    /// <summary>A one-track promo whose only track is a half-hour interview: same track count, wrong everything else.</summary>
    private static ReleaseData InterviewPromo(ReleaseData real)
    {
        var t = real.Media[0].Tracks[0];
        var interview = t with
        {
            Id = "track-qa", Title = "Radiohead 31x \"Q & A\"", LengthMs = 1_900_000,
            Recording = t.Recording with { Id = "rec-qa", Title = "Radiohead 31x \"Q & A\"", LengthMs = 1_900_000 },
        };
        return real with { Id = "promo-qa", Media = [new MediumData(1, "CD", null, [interview])] };
    }

    [SkippableFact]
    public async Task Applied_cover_replaces_the_last_one_and_beats_a_video_thumbnail()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        // One file carries embedded art, as video downloads do (a thumbnail).
        lib.MakeTrack("okc/01.flac", new() { ["title"] = "Airbag", ["artist"] = "Radiohead", ["album"] = "OK Computer", ["track"] = "1" },
            seconds: 0.3, coverPng: lib.MakePng());
        await lib.ScanAsync();
        var album = lib.Queries.Albums().Single();
        var (svc, _) = Service(lib);
        var okc = (await svc.MusicBrainz.GetReleaseAsync("6a09041b-0f79-3278-88d9-0552dd28d37f"))!;
        await SetFileLengths(lib, album.ReleaseId, [OkcLengths[0]]);

        byte[] Png(string color)
        {
            string path = Path.Combine(lib.Root, color + ".png");
            TempLibrary.Ffmpeg($"-f lavfi -i \"color=c={color}:s=32x32:d=1\" -frames:v 1 \"{path}\"");
            return File.ReadAllBytes(path);
        }
        byte[] first = Png("blue"), second = Png("green");
        var applier = new MetadataApplier(lib.Images);
        await lib.Db.Writer.Run((c, t) => applier.ApplyRelease(new Tx(c, t), album.ReleaseId, okc, first, "image/png", "coverartarchive"));
        Assert.Equal(first, File.ReadAllBytes(lib.Queries.Album(album.ReleaseId)!.CoverPath!));
        await lib.Db.Writer.Run((c, t) => applier.ApplyRelease(new Tx(c, t), album.ReleaseId, okc, second, "image/png", "coverartarchive"));
        Assert.Equal(second, File.ReadAllBytes(lib.Queries.Album(album.ReleaseId)!.CoverPath!));

        using var db = lib.Db.Open();
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM entity_image WHERE entity_id = $1 AND role = 'front' AND source = 'coverartarchive'", album.ReleaseId));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM entity_image WHERE entity_id = $1 AND role = 'front' AND source = 'embedded'", album.ReleaseId));
    }

    [SkippableFact]
    public async Task Release_where_no_track_fits_is_rejected_untouched()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        var album = await LoneSong(lib);
        var (svc, _) = Service(lib);
        var promo = InterviewPromo((await svc.MusicBrainz.GetReleaseAsync("6a09041b-0f79-3278-88d9-0552dd28d37f"))!);

        var r = await lib.Db.Writer.Run((c, t) => new MetadataApplier(lib.Images).ApplyRelease(new Tx(c, t), album.ReleaseId, promo, null, null, null));

        Assert.False(r.Applied);
        Assert.Equal("Karma Police", lib.Queries.AlbumTracks(album.ReleaseId).Single().Title);
        Assert.Equal("Karma Police", lib.Queries.Album(album.ReleaseId)!.Title);
    }

    [SkippableFact]
    public async Task Lone_song_finds_its_place_on_the_full_album_by_file_title()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        var album = await LoneSong(lib);
        var (svc, _) = Service(lib);
        var applier = new MetadataApplier(lib.Images);
        // An earlier bad apply left a wrong title and a wrong MusicBrainz id behind.
        await lib.Db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            var trackId = lib.Queries.AlbumTracks(album.ReleaseId).Single().TrackId;
            MetadataApplier.SetField(tx, "track", trackId, "title", "Radiohead 31x \"Q & A\"", "musicbrainz", Sql.Now());
            Importer.AddExternal(tx, "release", album.ReleaseId, "musicbrainz", "promo-qa");
        });

        var r = await svc.ApplyReleaseAsync(album.ReleaseId, "6a09041b-0f79-3278-88d9-0552dd28d37f", fetchCover: false);

        Assert.True(r!.Applied);
        Assert.Equal(1, r.TracksMatched);
        var track = lib.Queries.AlbumTracks(album.ReleaseId).Single();
        Assert.Equal("Karma Police", track.Title);
        Assert.Equal(6, track.Position);
        Assert.Equal("OK Computer", track.Album);
        using var c = lib.Db.Open();
        Assert.Equal(1L, c.Scalar<long>("SELECT COUNT(*) FROM external_id WHERE entity_type='release' AND entity_id=$1 AND provider='musicbrainz'", album.ReleaseId));
    }

    [SkippableFact]
    public async Task Search_ranks_candidates_by_fit()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        var album = await SloppyOkComputer(lib);
        var (svc, _) = Service(lib);
        var q = svc.DescribeAlbum(album.ReleaseId);
        Assert.Equal(12, q.TrackCount);
        var hits = await svc.SearchAlbumAsync(q);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("OK Computer", h.Title));
        Assert.Equal("6a09041b-0f79-3278-88d9-0552dd28d37f", hits[0].Id);
        Assert.Equal("CD", hits[0].Format);
    }

    [SkippableFact]
    public async Task Apply_cleans_up_album_and_keeps_locked_fields()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        var album = await SloppyOkComputer(lib);
        var (svc, http) = Service(lib);
        var recordingsBefore = lib.Queries.AlbumTracks(album.ReleaseId).Select(t => t.RecordingId).ToList();

        // A person fixed track 12's title by hand: that must survive.
        var t12 = lib.Queries.AlbumTracks(album.ReleaseId)[11];
        await lib.Db.Writer.Run((c, t) => MetadataApplier.SetField(new Tx(c, t), "track", t12.TrackId, "title", "The Tourist (my edit)", "user", Sql.Now(), lockIt: true));

        var result = await svc.ApplyReleaseAsync(album.ReleaseId, "6a09041b-0f79-3278-88d9-0552dd28d37f", fetchCover: false);
        Assert.NotNull(result);
        Assert.Equal(12, result!.TracksMatched);

        var a = lib.Queries.Albums().Single();
        Assert.Equal("OK Computer", a.Title);
        Assert.Equal("Radiohead", a.Artist);
        Assert.Equal("1997-07-01", a.Date);
        var tracks = lib.Queries.AlbumTracks(album.ReleaseId);
        Assert.Equal([.. OkcTitles[..11], "The Tourist (my edit)"], tracks.Select(t => t.Title));
        Assert.Equal(recordingsBefore, tracks.Select(t => t.RecordingId)); // identity (and history) kept

        using var c = lib.Db.Open();
        Assert.Equal("Capitol Records", c.Scalar<string>("SELECT label FROM release"));
        Assert.Equal("album", c.Scalar<string>("SELECT primary_type FROM release_group"));
        Assert.Equal("1997-05-21", c.Scalar<string>("SELECT first_release_date FROM release_group"));
        Assert.Equal(12L, c.Scalar<long>("SELECT COUNT(*) FROM external_id WHERE entity_type='recording' AND provider='musicbrainz'"));
        Assert.Equal("a74b1b7f-71a5-4011-9441-d0b5e4122711", c.Scalar<string>("SELECT external_id FROM external_id WHERE entity_type='artist' AND provider='musicbrainz'"));
        Assert.Equal("group", c.Scalar<string>("SELECT type FROM artist"));
        Assert.True(c.Scalar<long>("SELECT COUNT(*) FROM entity_genre g JOIN genre n ON n.id = g.genre_id WHERE g.entity_type='release_group' AND n.name='alternative rock'") == 1);

        // Applying again is answered from the cache.
        int calls = http.Calls.Count;
        await svc.ApplyReleaseAsync(album.ReleaseId, "6a09041b-0f79-3278-88d9-0552dd28d37f", fetchCover: false);
        await Task.Delay(100);
        Assert.Equal(calls, http.Calls.Count);
    }

    [SkippableFact]
    public async Task Two_local_editions_join_one_release_group()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        var album = await SloppyOkComputer(lib);
        // a second local copy, different year → separate release and group initially
        lib.MakeTrack("okc2/01.flac", new() { ["title"] = "Airbag", ["artist"] = "Radiohead", ["album"] = "OK Computer OKNOTOK", ["track"] = "1", ["date"] = "2017" }, seconds: 0.3);
        await lib.ScanAsync();
        var (svc, _) = Service(lib);
        await svc.ApplyReleaseAsync(album.ReleaseId, "6a09041b-0f79-3278-88d9-0552dd28d37f", fetchCover: false);

        // Pretend the other one is linked to the same release group (as a different MB release).
        var other = lib.Queries.Albums().Single(a => a.ReleaseId != album.ReleaseId);
        await SetFileLengths(lib, other.ReleaseId, [OkcLengths[0]]);
        var data = await svc.MusicBrainz.GetReleaseAsync("6a09041b-0f79-3278-88d9-0552dd28d37f");
        var edition = data! with { Id = "11111111-2222-3333-4444-555555555555", Title = "OK Computer OKNOTOK 1997 2017", Date = "2017-06-23" };
        var applier = new MetadataApplier(lib.Images);
        var r = await lib.Db.Writer.Run((c, t) => applier.ApplyRelease(new Tx(c, t), other.ReleaseId, edition, null, null, null));
        Assert.True(r.MergedIntoGroup);
        Assert.Single(lib.Queries.Albums().Select(a => a.ReleaseGroupId).Distinct());
    }

    [SkippableFact]
    public async Task Fan_upload_with_the_artist_in_its_title_fits_the_album()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("yt/kp.mp3", new() { ["title"] = "Radiohead - Karma Police", ["artist"] = "some uploader", ["album"] = "Radiohead - Karma Police" }, seconds: 0.3);
        await lib.ScanAsync();
        var album = lib.Queries.Albums().Single();
        await SetFileLengths(lib, album.ReleaseId, [261626 - 9000]);   // a video's intro/outro: not a tight length match
        var (svc, _) = Service(lib);

        var r = await svc.ApplyReleaseAsync(album.ReleaseId, "6a09041b-0f79-3278-88d9-0552dd28d37f", fetchCover: false);

        Assert.True(r!.Applied);
        var track = lib.Queries.AlbumTracks(album.ReleaseId).Single();
        Assert.Equal("Karma Police", track.Title);
        Assert.Equal(6, track.Position);
        Assert.Equal("Radiohead", track.Artist);
    }

    [SkippableFact]
    public async Task Lone_songs_matched_one_by_one_end_up_on_one_album()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        // Two songs of one album, ripped separately with different artist spellings: two local albums.
        lib.MakeTrack("yt/airbag.mp3", new() { ["title"] = "Airbag", ["artist"] = "Radiohead", ["album"] = "OK Computer" }, seconds: 0.3);
        lib.MakeTrack("yt/lucky.mp3", new() { ["title"] = "Lucky", ["artist"] = "Radiohead - Topic", ["album"] = "OK Computer" }, seconds: 0.3);
        await lib.ScanAsync();
        var albums = lib.Queries.Albums();
        Assert.Equal(2, albums.Count);
        var airbag = albums.Single(a => lib.Queries.AlbumTracks(a.ReleaseId).Single().Title == "Airbag");
        var lucky = albums.Single(a => a.ReleaseId != airbag.ReleaseId);
        await SetFileLengths(lib, airbag.ReleaseId, [OkcLengths[0]]);
        await SetFileLengths(lib, lucky.ReleaseId, [OkcLengths[10]]);
        var (svc, _) = Service(lib);
        const string Mbid = "6a09041b-0f79-3278-88d9-0552dd28d37f";

        var first = await svc.ApplyReleaseAsync(airbag.ReleaseId, Mbid, fetchCover: false);
        Assert.Null(first!.MergedInto);

        // Searching for the second song puts the edition already in the library first, marked as such.
        var hits = await svc.SearchAlbumAsync(svc.DescribeAlbum(lucky.ReleaseId));
        Assert.Contains(hits, h => h.Id == Mbid && h.InLibrary);

        var second = await svc.ApplyReleaseAsync(lucky.ReleaseId, Mbid, fetchCover: false);
        Assert.Equal(airbag.ReleaseId, second!.MergedInto);
        var only = lib.Queries.Albums().Single();
        Assert.Equal(["Airbag", "Lucky"], lib.Queries.AlbumTracks(only.ReleaseId).Select(t => t.Title));
        Assert.Equal([1, 11], lib.Queries.AlbumTracks(only.ReleaseId).Select(t => t.Position));
    }

    [Fact]
    public async Task Http_cache_retries_busy_and_caches_not_found()
    {
        using var lib = new TempLibrary();
        var fake = new FakeHandler()
            .OnStatus("busy", HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK)
            .OnStatus("missing", HttpStatusCode.NotFound);
        var http = new CachedHttp(lib.Db, new HttpClient(fake));
        http.SetRateLimit("t", TimeSpan.Zero);

        var busy = await http.GetAsync("t", "https://x/busy", TimeSpan.FromDays(1));
        Assert.Equal(200, busy.Status);
        Assert.Equal(2, fake.Calls.Count);

        var a = await http.GetAsync("t", "https://x/missing", TimeSpan.FromDays(1));
        await Task.Delay(100); // cache write is async
        var b = await http.GetAsync("t", "https://x/missing", TimeSpan.FromDays(1));
        Assert.Equal(404, a.Status);
        Assert.Equal(404, b.Status);
        Assert.True(b.FromCache);
        Assert.Equal(3, fake.Calls.Count);
    }

    [Fact]
    public void Parses_lrc_and_enhanced_lrc()
    {
        var lines = Lrc.Parse("[ar: X]\n[00:12.50]First line\n[00:05.00][00:30.00]Chorus\n[01:02.3]Last", LyricsFormat.Lrc);
        Assert.Equal(["Chorus", "First line", "Chorus", "Last"], lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(5), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(62.3), lines[3].Time);

        var e = Lrc.Parse("[00:01.00]<00:01.00>Hello <00:01.50>world", LyricsFormat.EnhancedLrc);
        Assert.Equal("Hello world", e[0].Text);
        Assert.Equal(2, e[0].Words.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), e[0].Words[1].Start);
    }

    [Fact]
    public void Word_tags_keep_their_place_in_the_tidied_line()
    {
        var lines = Lrc.Parse("[00:10.00] <00:10.00>Hold  <00:10.40>me <00:11.00>now<00:11.80>\n[00:20.00]<00:20.00>Next", LyricsFormat.EnhancedLrc);
        var l = lines[0];
        Assert.Equal("Hold me now", l.Text);
        Assert.Equal(["Hold", "me", "now"], l.Words.Select(w => l.Text.Substring(w.CharStart, w.CharLength)));
        Assert.Equal(TimeSpan.FromSeconds(10.4), l.Words[0].End);
        Assert.Equal(TimeSpan.FromSeconds(11.8), l.Words[2].End); // closing tag ends the last word

        // A chorus stamped twice is sung twice: its words move with the second stamp.
        var twice = Lrc.Parse("[00:05.00][00:30.00]<00:05.00>La <00:05.50>la", LyricsFormat.EnhancedLrc);
        Assert.Equal(TimeSpan.FromSeconds(30.5), twice[1].Words[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(5.5), twice[0].Words[1].Start);
    }

    [Fact]
    public void Karaoke_follows_real_word_timing()
    {
        var lines = Lrc.Parse("[00:10.00]<00:10.00>Hold <00:10.40>me <00:11.00>now<00:11.80>\n[00:20.00]<00:20.00>Next", LyricsFormat.EnhancedLrc);
        var t = Karaoke.Timings(lines, estimate: false);
        Assert.Equal(0, Karaoke.SungChars(t[0], TimeSpan.FromSeconds(9)));
        Assert.Equal(2, Karaoke.SungChars(t[0], TimeSpan.FromSeconds(10.2)), 3);  // halfway through "Hold"
        Assert.Equal(5, Karaoke.SungChars(t[0], TimeSpan.FromSeconds(10.4)), 3);  // "Hold " done, "me" starts
        Assert.Equal(11, Karaoke.SungChars(t[0], TimeSpan.FromSeconds(15)), 3);   // whole line
        // The last line's last word has no end tag: it still ends, before any following line would.
        Assert.True(t[1][0].End > t[1][0].Start);
    }

    [Fact]
    public void Karaoke_estimates_line_synced_lyrics_only_when_asked()
    {
        var lines = Lrc.Parse("[00:10.00]One two three\n[00:11.00]Short gap\n[00:40.00]Last", LyricsFormat.Lrc);
        Assert.All(Karaoke.Timings(lines, estimate: false), w => Assert.Empty(w));

        var t = Karaoke.Timings(lines, estimate: true);
        Assert.Equal(["One", "two", "three"], t[0].Select(w => lines[0].Text.Substring(w.CharStart, w.CharLength)));
        Assert.True(t[0][^1].End <= TimeSpan.FromSeconds(11)); // never runs into the next line
        Assert.Equal(lines[0].Text.Length, Karaoke.SungChars(t[0], TimeSpan.FromSeconds(11)), 3);
        // A long instrumental gap does not stretch the line: it is sung at a normal pace.
        Assert.True(t[1][^1].End - t[1][0].Start < TimeSpan.FromSeconds(2));
        double mid = Karaoke.SungChars(t[1], TimeSpan.FromSeconds(11) + (t[1][^1].End - TimeSpan.FromSeconds(11)) / 2);
        Assert.InRange(mid, 3, 6);
    }

    [Fact]
    public void Formats_media()
    {
        Assert.Equal("2×CD + DVD-Video", MusicBrainz.FormatText(["CD", "CD", "DVD-Video"]));
    }
    private static string LrclibEntry(long id, string album, double duration, int firstVerseSec) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["trackName"] = "The Zoo", ["artistName"] = "Scorpions", ["albumName"] = album, ["duration"] = duration,
            ["plainLyrics"] = "The job is done, I go out\nAnother boring day",
            ["syncedLyrics"] = $"[{firstVerseSec / 60:00}:{firstVerseSec % 60:00}.10] The job is done, I go out\n[{(firstVerseSec + 5) / 60:00}:{(firstVerseSec + 5) % 60:00}.20] Another boring day",
        }, LrclibTestJson.Default.DictionaryStringObject);

    [Fact]
    public async Task Lrclib_prefers_the_timing_most_entries_agree_on()
    {
        using var lib = new TempLibrary();
        // The exact match carries another recording's timing (vocals at 1:02); three other entries agree on 0:42.
        var fake = new FakeHandler()
            .OnJson("lrclib.net/api/get", LrclibEntry(1, "Comeblack", 338, 62))
            .OnJson("lrclib.net/api/search", "[" + string.Join(",",
                LrclibEntry(1, "Comeblack", 338, 62), LrclibEntry(2, "Acoustica", 338, 42), LrclibEntry(3, "Comeblack (JP)", 338, 42),
                LrclibEntry(4, "Comeblack/Acoustica", 337.5, 42), LrclibEntry(5, "Animal Magnetism", 331, 62)) + "]");
        var http = new CachedHttp(lib.Db, new HttpClient(fake));
        http.SetRateLimit(Lrclib.Provider, TimeSpan.Zero);

        var all = await new Lrclib(http).CandidatesAsync("Scorpions", "The Zoo", "Comeblack", TimeSpan.FromSeconds(338.35));

        Assert.Equal(4, all.Count); // the 331 s recording is out
        Assert.Equal(42, (int)Lrc.Parse(all[0].Synced!.Content, all[0].Synced!.Format)[0].Time!.Value.TotalSeconds);
        Assert.Equal(1, all[^1].Id);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(long))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class LrclibTestJson : System.Text.Json.Serialization.JsonSerializerContext;
