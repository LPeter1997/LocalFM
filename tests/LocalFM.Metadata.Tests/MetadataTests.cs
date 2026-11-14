using System.Net;
using LocalFM.Library;
using LocalFM.Library.Enrich;
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
        return lib.Queries.Albums().Single();
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
        var data = await svc.MusicBrainz.GetReleaseAsync("6a09041b-0f79-3278-88d9-0552dd28d37f");
        var edition = data! with { Id = "11111111-2222-3333-4444-555555555555", Title = "OK Computer OKNOTOK 1997 2017", Date = "2017-06-23" };
        var applier = new MetadataApplier(lib.Images);
        var r = await lib.Db.Writer.Run((c, t) => applier.ApplyRelease(new Tx(c, t), other.ReleaseId, edition, null, null, null));
        Assert.True(r.MergedIntoGroup);
        Assert.Single(lib.Queries.Albums().Select(a => a.ReleaseGroupId).Distinct());
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
        Assert.Equal(TimeSpan.FromSeconds(1.5), e[0].Words[1].Time);
    }

    [Fact]
    public void Formats_media()
    {
        Assert.Equal("2×CD + DVD-Video", MusicBrainz.FormatText(["CD", "CD", "DVD-Video"]));
    }
}
