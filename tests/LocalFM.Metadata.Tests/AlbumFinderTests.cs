using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Edit;
using LocalFM.Library.Tests;

namespace LocalFM.Metadata.Tests;

/// <summary>The Dire Straits case: three songs from two albums, matched to the wrong one, then sorted out.</summary>
public class AlbumFinderTests
{
    private const string Ds = "Dire Straits";

    /// <summary>Real lengths: the test files are a fraction of a second, so they are given these in the database.</summary>
    private static readonly Dictionary<string, long> Lengths = new()
    {
        ["Down to the Waterline"] = 235_000, ["Sultans of Swing"] = 348_000, ["So Far Away"] = 312_000,
        ["Money for Nothing"] = 506_000, ["Walk of Life"] = 252_000,
    };

    private static string Q(string s) => "\"" + s + "\"";

    private static string Credit(string name = Ds) =>
        "[{\"name\":" + Q(name) + ",\"artist\":{\"id\":\"ds-artist\",\"name\":" + Q(name) + ",\"sort-name\":" + Q(name) + "}}]";

    private static string RecordingSearch(string recId, string title, params (string Id, string Title, string Group, string Date, string? Secondary)[] releases)
    {
        var rels = releases.Select(r =>
            "{\"id\":" + Q(r.Id) + ",\"title\":" + Q(r.Title) + ",\"status\":\"Official\",\"date\":" + Q(r.Date) + ",\"track-count\":9," +
            "\"release-group\":{\"id\":" + Q(r.Group) + ",\"primary-type\":\"Album\"" +
            (r.Secondary is null ? "" : ",\"secondary-types\":[" + Q(r.Secondary) + "]") + "}," +
            "\"media\":[{\"position\":1,\"track\":[{\"number\":\"1\",\"title\":" + Q(title) + "}]}]}");
        return "{\"recordings\":[{\"id\":" + Q(recId) + ",\"length\":" + Lengths[title] + ",\"score\":100,\"title\":" + Q(title) + ",\"artist-credit\":" + Credit() +
               ",\"releases\":[" + string.Join(",", rels) + "]}]}";
    }

    private static string Release(string id, string title, string group, string date, params (string RecId, string Title)[] tracks)
    {
        var ts = tracks.Select((t, i) =>
            "{\"id\":" + Q("track-" + t.RecId) + ",\"length\":" + Lengths[t.Title] + ",\"position\":" + (i + 1) + ",\"number\":" + Q((i + 1).ToString()) + ",\"title\":" + Q(t.Title) +
            ",\"recording\":{\"id\":" + Q(t.RecId) + ",\"title\":" + Q(t.Title) + ",\"artist-credit\":" + Credit() + "}}");
        return "{\"id\":" + Q(id) + ",\"title\":" + Q(title) + ",\"status\":\"Official\",\"date\":" + Q(date) + ",\"artist-credit\":" + Credit() +
               ",\"release-group\":{\"id\":" + Q(group) + ",\"title\":" + Q(title) + ",\"primary-type\":\"Album\",\"first-release-date\":" + Q(date) +
               ",\"artist-credit\":" + Credit() + "}," +
               "\"media\":[{\"position\":1,\"format\":\"CD\",\"tracks\":[" + string.Join(",", ts) + "]}]}";
    }

    private static (MetadataService Svc, FakeHandler Http) Service(TempLibrary lib)
    {
        const string hits = "Sultans of Swing - The Very Best of Dire Straits";
        var fake = new FakeHandler()
            .OnJson("Sultans", RecordingSearch("rec-sultans", "Sultans of Swing",
                ("rel-ds", Ds, "rg-ds", "1978-10-07", null), ("rel-hits", hits, "rg-hits", "1998-10-19", "Compilation")))
            .OnJson("Money", RecordingSearch("rec-mfn", "Money for Nothing",
                ("rel-bia", "Brothers in Arms", "rg-bia", "1985-05-13", null), ("rel-hits", hits, "rg-hits", "1998-10-19", "Compilation"),
                ("rel-live", "On the Night", "rg-live", "1993-05-10", "Live")))
            .OnJson("Walk", RecordingSearch("rec-wol", "Walk of Life",
                ("rel-bia", "Brothers in Arms", "rg-bia", "1985-05-13", null), ("rel-hits", hits, "rg-hits", "1998-10-19", "Compilation")))
            .OnJson("release/rel-ds?", Release("rel-ds", Ds, "rg-ds", "1978-10-07",
                ("rec-water", "Down to the Waterline"), ("rec-sultans", "Sultans of Swing")))
            .OnJson("release/rel-bia?", Release("rel-bia", "Brothers in Arms", "rg-bia", "1985-05-13",
                ("rec-so-far", "So Far Away"), ("rec-mfn", "Money for Nothing"), ("rec-wol", "Walk of Life")));
        return (new MetadataService(lib.Db, lib.Images, new History(lib.Db), new HttpClient(fake)), fake);
    }

    /// <summary>Three Dire Straits songs on an album without a name, then the "Dire Straits" release applied to it.</summary>
    private static async Task<(Ulid Album, Library.Enrich.ApplyResult Result)> WronglyMatched(TempLibrary lib, MetadataService svc)
    {
        foreach (var t in new[] { "Sultans of Swing", "Money for Nothing", "Walk of Life" })
            lib.MakeTrack($"ds/{t}.mp3", new() { ["title"] = t, ["artist"] = Ds }, seconds: 0.3);
        await Scan(lib);
        var album = lib.Queries.Albums().Single();
        var result = await svc.ApplyReleaseAsync(album.ReleaseId, "rel-ds", fetchCover: false);
        return (album.ReleaseId, result!);
    }

    private static async Task Scan(TempLibrary lib)
    {
        await lib.ScanAsync();
        await lib.Db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            foreach (var (title, ms) in Lengths)
                tx.Exec("UPDATE audio_file SET duration_ms = $2 WHERE track_id IN (SELECT id FROM track WHERE title = $1)", title, ms);
        });
    }

    private static List<(string Album, string[] Songs)> Albums(TempLibrary lib) =>
        lib.Queries.Albums().OrderBy(a => a.Title)
            .Select(a => (a.Title, lib.Queries.AlbumTracks(a.ReleaseId).Select(t => t.Title).Order().ToArray())).ToList();

    [SkippableFact]
    public async Task Apply_reports_the_songs_that_are_not_on_the_release()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, _) = Service(lib);
        var (_, result) = await WronglyMatched(lib, svc);
        Assert.Equal(1, result.TracksMatched);
        Assert.Equal(2, result.UnmatchedTrackIds.Count);
        var stray = Assert.Single(AlbumChecks.FindOffline(lib.Db));
        Assert.Equal(AlbumIssue.NotOnAlbum, stray.Kind);
        Assert.Equal("2 songs aren't on “Dire Straits”", stray.Summary);
    }

    [SkippableFact]
    public async Task Songs_find_their_studio_album_not_the_compilation()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, _) = Service(lib);
        foreach (var t in new[] { "Sultans of Swing", "Money for Nothing", "Walk of Life" })
            lib.MakeTrack($"ds/{t}.mp3", new() { ["title"] = t, ["artist"] = Ds }, seconds: 0.3);
        await Scan(lib);
        var songs = svc.Albums.DescribeAlbum(lib.Queries.Albums().Single().ReleaseId);

        var guesses = await svc.Albums.FindAsync(songs);
        // The compilation has all three, so it ranks first as a guess…
        Assert.Equal("rel-hits", guesses[0].ReleaseMbid);
        // …but each song is placed on its original album.
        var placed = AlbumFinder.Assign(guesses);
        Assert.Equal(["Brothers in Arms", Ds], placed.Select(p => p.Title).Order().ToArray());
        Assert.Equal(2, placed.Single(p => p.Title == "Brothers in Arms").Songs.Count);
    }

    [SkippableFact]
    public async Task Find_their_albums_moves_the_strays_to_a_new_album()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, _) = Service(lib);
        await WronglyMatched(lib, svc);
        var report = await svc.Organizer.ApplyAsync(AlbumChecks.FindOffline(lib.Db).Single());

        Assert.Empty(report.Problems);
        Assert.Equal(2, report.Moved);
        var albums = Albums(lib);
        Assert.Equal(2, albums.Count);
        Assert.Equal("Brothers in Arms", albums[0].Album);
        Assert.Equal(["Money for Nothing", "Walk of Life"], albums[0].Songs);
        Assert.Equal(Ds, albums[1].Album);
        Assert.Equal(["Sultans of Swing"], albums[1].Songs);
        Assert.Empty(AlbumChecks.FindOffline(lib.Db)); // everything is where it belongs now
    }

    [SkippableFact]
    public async Task Scout_says_where_the_strays_belong_and_skips_confirmed_songs()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, http) = Service(lib);
        await WronglyMatched(lib, svc);

        var found = new List<AlbumSuggestion>();
        await svc.Scout.RunAsync(new SyncProgress<AlbumSuggestion>(found.Add));
        var s = Assert.Single(found);
        Assert.Equal(AlbumIssue.BelongsElsewhere, s.Kind);
        Assert.Equal("2 songs are on “Brothers in Arms” (1985), not “Dire Straits”", s.Summary);
        // Sultans of Swing is confirmed by the match, so it was not looked up.
        Assert.DoesNotContain(http.Calls, u => u.Contains("Sultans", StringComparison.Ordinal));

        await AlbumChecks.DismissAsync(new History(lib.Db), s);
        found.Clear();
        await svc.Scout.RunAsync(new SyncProgress<AlbumSuggestion>(found.Add));
        Assert.Empty(found);
    }

    [SkippableFact]
    public async Task Gathering_lone_songs_joins_the_album_already_in_the_library()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, _) = Service(lib);
        await WronglyMatched(lib, svc);
        // The two strays now live on Brothers in Arms; a third copy of "Walk of Life" arrives as a lone single.
        await svc.Organizer.ApplyAsync(AlbumChecks.FindOffline(lib.Db).Single());
        lib.MakeTrack("singles/wol.mp3", new() { ["title"] = "Walk of Life", ["artist"] = "Dire Straits - Topic", ["album"] = "Walk of Life" }, seconds: 0.4);
        await Scan(lib);
        Assert.Equal(3, lib.Queries.Albums().Count);

        var found = new List<AlbumSuggestion>();
        await svc.Scout.RunAsync(new SyncProgress<AlbumSuggestion>(found.Add));
        var s = Assert.Single(found);
        Assert.Equal(AlbumIssue.AlbumFound, s.Kind);
        await svc.Organizer.ApplyAsync(s);
        // It joined the local Brothers in Arms (same recording: merged into its track), no new album.
        Assert.Equal(2, lib.Queries.Albums().Count);
    }

    [SkippableFact]
    public async Task Same_album_twice_is_offered_as_a_merge()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.mp3", new() { ["title"] = "One", ["artist"] = "Band", ["album"] = "Record", ["date"] = "1990" }, seconds: 0.3);
        lib.MakeTrack("b/2.mp3", new() { ["title"] = "Two", ["artist"] = "Band", ["album"] = "Record", ["date"] = "2001" }, seconds: 0.3);
        await Scan(lib);
        Assert.Equal(2, lib.Queries.Albums().Count); // years differ, so the importer kept them apart
        var s = Assert.Single(AlbumChecks.FindOffline(lib.Db));
        Assert.Equal(AlbumIssue.SameAlbumTwice, s.Kind);
        var (svc, _) = Service(lib);
        await svc.Organizer.ApplyAsync(s);
        Assert.Single(lib.Queries.Albums());
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests want the callback inline.</summary>
    [SkippableFact]
    public async Task A_song_a_scan_adds_to_a_matched_album_is_confirmed_not_moved()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, _) = Service(lib);
        lib.MakeTrack("ds/Sultans of Swing.mp3", new() { ["title"] = "Sultans of Swing", ["artist"] = Ds, ["album"] = Ds }, seconds: 0.3);
        await Scan(lib);
        var album = lib.Queries.Albums().Single().ReleaseId;
        await svc.ApplyReleaseAsync(album, "rel-ds", fetchCover: false);

        // Later another song of the album arrives: the scan puts it on the album, but nothing compared it with the release.
        lib.MakeTrack("ds/Down to the Waterline.mp3", new() { ["title"] = "Down to the Waterline", ["artist"] = Ds, ["album"] = Ds }, seconds: 0.3);
        await Scan(lib);
        Assert.Equal(AlbumIssue.NotOnAlbum, Assert.Single(AlbumChecks.FindOffline(lib.Db)).Kind);

        Assert.Equal(2, await svc.Scout.SettleAsync());
        Assert.Empty(AlbumChecks.FindOffline(lib.Db));
        var found = new List<AlbumSuggestion>();
        await svc.Scout.RunAsync(new SyncProgress<AlbumSuggestion>(found.Add));
        Assert.Empty(found);
        var (title, songs) = Assert.Single(Albums(lib));
        Assert.Equal(Ds, title);
        Assert.Equal(["Down to the Waterline", "Sultans of Swing"], songs);
    }

    [SkippableFact]
    public async Task A_song_with_title_and_artist_swapped_is_still_found()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg, "ffmpeg CLI needed");
        using var lib = new TempLibrary();
        var (svc, _) = Service(lib);
        lib.MakeTrack("swapped.mp3", new() { ["title"] = Ds, ["artist"] = "Sultans of Swing" }, seconds: 0.3);
        await lib.ScanAsync();
        await lib.Db.Writer.Run((c, t) => new Tx(c, t).Exec("UPDATE audio_file SET duration_ms = 348000"));

        var placed = AlbumFinder.Assign(await svc.Albums.FindAsync(svc.Albums.DescribeAlbum(lib.Queries.Albums().Single().ReleaseId)));
        Assert.Equal(Ds, Assert.Single(placed).Title);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
