using System.IO.Compression;
using LocalFM.Core;
using LocalFM.Library.Edit;
using LocalFM.Library.Enrich;
using LocalFM.Library.Exchange;

namespace LocalFM.Library.Tests;

public class ExchangeTests
{
    /// <summary>A curated library: hand edits with a lock, an artist profile with a photo, lyrics, a playlist, plays.</summary>
    private static async Task<TempLibrary> Curated()
    {
        var lib = new TempLibrary();
        lib.MakeTrack("blackout/1.flac", new() { ["title"] = "Blackout (Remastered 2001)", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.4);
        lib.MakeTrack("blackout/2.flac", new() { ["title"] = "Cant Live Without You", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "2" }, seconds: 0.5);
        lib.MakeTrack("okc/1.flac", new() { ["title"] = "Airbag", ["artist"] = "Radiohead", ["album"] = "OK Computer", ["track"] = "1" }, seconds: 0.6);
        await lib.ScanAsync();

        var t1 = lib.Queries.Tracks().Single(t => t.Title.StartsWith("Blackout ("));
        var t2 = lib.Queries.Tracks().Single(t => t.Title.StartsWith("Cant"));
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t),
        [
            new FieldChange("track", t1.TrackId, "title", "Blackout"),
            new FieldChange("track", t2.TrackId, "title", "Can't Live Without You"),
        ]));
        var scorpions = lib.Queries.Artists("Scorpions").Single();
        var png = File.ReadAllBytes(lib.MakePng());
        await lib.Db.Writer.Run((c, t) => ArtistProfiles.Apply(new Tx(c, t), lib.Images, scorpions.ArtistId,
            new ArtistProfileData("c3cceeed-3332-4cf0-8c4c-bbde425147b6", "Scorpions", null, "Group", "DE", null, "1965", null, false, null,
                ["Hard Rock"], [], [], [], Bio: "German rock band."), png, "image/png"));
        await lib.Db.Writer.Run((c, t) => new Tx(c, t).Exec(
            "INSERT INTO lyrics(id, recording_id, format, content, source, updated_at) VALUES ($1,$2,'plain','When I wake up in the morning','lrclib',0)",
            Ulid.NewUlid(), t1.RecordingId));
        await new Playlists(lib.Db).CreateAsync("Loud", [t1.RecordingId, lib.Queries.Tracks("Airbag").Single().RecordingId]);
        var history = new History(lib.Db);
        for (int i = 0; i < 3; i++) await history.RecordPlayAsync(t1.RecordingId, t1.FileId, DateTimeOffset.UtcNow.AddDays(-i), TimeSpan.FromMinutes(4), true);
        return lib;
    }

    private static byte[] Export(TempLibrary lib)
    {
        using var ms = new MemoryStream();
        var report = KnowledgeBundle.Export(lib.Db, lib.Images, ms);
        Assert.Equal(2, report.Albums);
        Assert.Equal(1, report.Images);
        return ms.ToArray();
    }

    private static Task<ImportReport> Import(TempLibrary into, byte[] bundle) => into.Db.Writer.Run((c, t) =>
    {
        using var zip = new ZipArchive(new MemoryStream(bundle), ZipArchiveMode.Read);
        return KnowledgeBundle.Import(new Tx(c, t), into.Images, zip);
    });

    private static void CopyMusic(TempLibrary from, TempLibrary to)
    {
        foreach (var f in Directory.EnumerateFiles(from.Music, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to.Music, "copied", Path.GetRelativePath(from.Music, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    private static void AssertCurated(TempLibrary lib)
    {
        Assert.Equal(["Blackout", "OK Computer"], lib.Queries.Albums().Select(a => a.Title).Order());
        var blackout = lib.Queries.AlbumTracks(lib.Queries.Albums().Single(a => a.Title == "Blackout").ReleaseId);
        Assert.Equal(["Blackout", "Can't Live Without You"], blackout.Select(t => t.Title));
        Assert.True(MetadataEditor.States(lib.Db, "track", blackout[0].TrackId)["title"].Locked);

        var artist = lib.Queries.Artists("Scorpions").Single();
        var profile = ArtistProfiles.Load(lib.Db, lib.Images, artist.ArtistId)!;
        Assert.Equal("German rock band.", profile.Bio);
        Assert.True(File.Exists(profile.PhotoPath));
        Assert.Equal("c3cceeed-3332-4cf0-8c4c-bbde425147b6", profile.Mbid);

        Assert.Equal(3, new History(lib.Db).PlayCount(blackout[0].RecordingId));
        var playlist = new Playlists(lib.Db).List().Single(p => p.Name == "Loud");
        var entries = new Playlists(lib.Db).Entries(playlist.Id);
        Assert.Equal(["Blackout", "Airbag"], entries.Select(r => lib.Queries.ByRecordings([r]).Single().Title));
        using var c = lib.Db.Open();
        Assert.Equal(1L, c.Scalar<long>("SELECT COUNT(*) FROM lyrics"));
    }

    [SkippableFact]
    public async Task Restores_into_an_empty_library_and_attaches_files_on_scan()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var a = await Curated();
        var bundle = Export(a);

        using var b = new TempLibrary();
        var report = await Import(b, bundle);
        Assert.Equal(2, report.AlbumsAdded);
        Assert.Equal(3, report.FilesWaiting);
        Assert.Empty(b.Queries.Albums());           // nothing playable until the files are here

        CopyMusic(a, b);
        await b.ScanAsync();
        AssertCurated(b);
        Assert.Equal(3, b.Queries.Tracks().Count);  // the files joined the imported tracks; no duplicates
    }

    [SkippableFact]
    public async Task Merges_into_a_library_that_already_has_the_files()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var a = await Curated();
        var bundle = Export(a);

        using var b = new TempLibrary();
        CopyMusic(a, b);
        await b.ScanAsync();                         // B knows the same files under its own ids
        // B's own hand edit wins over the bundle's.
        var airbag = b.Queries.Tracks("Airbag").Single();
        await b.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t), [new FieldChange("track", airbag.TrackId, "title", "Airbag!")]));

        var report = await Import(b, bundle);
        Assert.Equal(2, report.AlbumsMatched);
        Assert.Equal(0, report.AlbumsAdded);
        Assert.Equal(0, report.FilesWaiting);

        var albums = b.Queries.Albums();
        Assert.Equal(2, albums.Count);
        Assert.Equal("Airbag!", b.Queries.ByTrack(airbag.TrackId)!.Title);
        // Everything else from the curated library came across.
        var blackout = b.Queries.AlbumTracks(albums.Single(x => x.Title == "Blackout").ReleaseId);
        Assert.Equal(["Blackout", "Can't Live Without You"], blackout.Select(t => t.Title));
        Assert.Equal(3, new History(b.Db).PlayCount(blackout[0].RecordingId));

        // Importing the same file again changes nothing.
        var again = await Import(b, bundle);
        Assert.Equal(0, again.Plays);
        Assert.Equal(0, again.Lyrics);
        Assert.Equal(0, again.Playlists);
        Assert.Equal(2, b.Queries.Albums().Count);
    }

    [Fact]
    public void Rejects_files_that_are_not_bundles()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write("""{"format":"something-else"}""");
        ms.Position = 0;
        using var read = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => KnowledgeBundle.Validate(read));
    }
}
