using System.Text.Json;
using LocalFM.Library;
using LocalFM.Library.Tests;
using LocalFM.Vibe.Library;
using LocalFM.Vibe.Protocol;

namespace LocalFM.Vibe.Tests;

public sealed class LibraryApiTests
{
    private static async Task<(TempLibrary Lib, LibraryApi Api)> MakeAsync()
    {
        var lib = new TempLibrary();
        string png = lib.MakePng();
        lib.MakeTrack("blackout/1.mp3", new() { ["title"] = "Blackout", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "1" }, 0.4, png);
        lib.MakeTrack("blackout/2.mp3", new() { ["title"] = "Can't Live Without You", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "2" }, 0.5, png);
        lib.MakeTrack("okc/1.flac", new() { ["title"] = "Airbag", ["artist"] = "Radiohead", ["album"] = "OK Computer", ["track"] = "1" }, 0.6);
        await lib.ScanAsync();
        var media = new MediaResolver(lib.Db, lib.Images, lib.Queries);
        var api = new LibraryApi(lib.Db, lib.Images, lib.Queries, new Playlists(lib.Db), media, (_, _) => Task.FromResult<LocalFM.Metadata.LyricsView?>(null));
        return (lib, api);
    }

    private static string Json(LibraryReply r) => JsonSerializer.Serialize<Message>(r, WireJson.Default.Message);

    [SkippableFact]
    public async Task Browses_albums_tracks_and_artists_without_paths()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (lib, api) = await MakeAsync();
        using var _ = lib;

        var albums = await api.HandleAsync(new LibraryRequest(1, LibraryOp.Albums));
        Assert.Null(albums.Error);
        Assert.Equal(1u, albums.Request);
        Assert.Equal(["Blackout", "OK Computer"], albums.Albums!.Select(a => a.Title).Order());

        var blackout = albums.Albums!.Single(a => a.Title == "Blackout");
        var album = await api.HandleAsync(new LibraryRequest(2, LibraryOp.Album, blackout.Id));
        Assert.Equal(["Blackout", "Can't Live Without You"], album.Tracks!.Select(t => t.Title));
        Assert.Equal("mp3", album.Tracks![0].Codec);
        Assert.NotNull(blackout.Cover);

        var artists = await api.HandleAsync(new LibraryRequest(3, LibraryOp.Artists));
        Assert.Contains(artists.Artists!, a => a.Name == "Radiohead");
        var radiohead = artists.Artists!.Single(a => a.Name == "Radiohead");
        var artist = await api.HandleAsync(new LibraryRequest(4, LibraryOp.Artist, radiohead.Id));
        Assert.Equal("Radiohead", artist.Artist!.Name);
        Assert.Equal(["OK Computer"], artist.Albums!.Select(a => a.Title));

        var search = await api.HandleAsync(new LibraryRequest(5, LibraryOp.Search, Search: "air"));
        Assert.Equal(["Airbag"], search.Tracks!.Select(t => t.Title));

        var counts = await api.HandleAsync(new LibraryRequest(6, LibraryOp.Counts));
        Assert.Equal(3, counts.Counts!.Tracks);

        foreach (var r in new[] { albums, album, artists, artist, search })
            Assert.DoesNotContain(lib.Root.Replace("\\", "\\\\"), Json(r));
    }

    [SkippableFact]
    public async Task Resolves_only_library_media()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (lib, api) = await MakeAsync();
        using var _ = lib;

        var tracks = (await api.HandleAsync(new LibraryRequest(1, LibraryOp.Tracks))).Tracks!;
        var airbag = tracks.Single(t => t.Title == "Airbag");
        // A fresh resolver (no warm cache) finds the file and the cover from the ids alone.
        var media = new MediaResolver(lib.Db, lib.Images, lib.Queries);
        Assert.EndsWith("1.flac", media.Track(airbag.Id)!.Path);
        var cover = tracks.First(t => t.Title == "Blackout").Cover!;
        Assert.True(File.Exists(media.Image(cover)));

        Assert.Null(media.Track("../../etc/passwd"));
        Assert.Null(media.Track(LocalFM.Core.Ulid.NewUlid().ToString()));
        Assert.Null(media.Image("x" + airbag.Id));
        Assert.Null(media.Image(lib.Root));
    }

    [SkippableFact]
    public async Task Bad_requests_come_back_as_errors()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (lib, api) = await MakeAsync();
        using var _ = lib;

        Assert.NotNull((await api.HandleAsync(new LibraryRequest(1, "nope"))).Error);
        Assert.NotNull((await api.HandleAsync(new LibraryRequest(2, LibraryOp.Album, "not-an-id"))).Error);
        Assert.Equal("Not found.", (await api.HandleAsync(new LibraryRequest(3, LibraryOp.Album, LocalFM.Core.Ulid.NewUlid().ToString()))).Error);
        var tracks = (await api.HandleAsync(new LibraryRequest(4, LibraryOp.Tracks))).Tracks!;
        var lyrics = await api.HandleAsync(new LibraryRequest(5, LibraryOp.Lyrics, tracks[0].Id));
        Assert.Null(lyrics.Error);
        Assert.Null(lyrics.Lyrics);
    }

    [Fact]
    public void Library_messages_round_trip()
    {
        Message m = new LibraryRequest(7, LibraryOp.Albums, Search: "x");
        string json = JsonSerializer.Serialize(m, WireJson.Default.Message);
        Assert.Contains("\"t\":\"lib\"", json);
        Assert.Equal(m, JsonSerializer.Deserialize(json, WireJson.Default.Message));
        // What a browser sends: camelCase, extra fields ignored.
        var q = JsonSerializer.Deserialize("""{"t":"lib","request":3,"op":"album","id":"A","extra":1}""", WireJson.Default.Message);
        Assert.Equal(new LibraryRequest(3, "album", "A"), q);
    }
}
