using LocalFM.Library.Edit;
using LocalFM.Library.Enrich;

namespace LocalFM.Library.Tests;

public class EditorTests
{
    [Theory]
    [InlineData("Scorpions - Topic", "Scorpions")]
    [InlineData("ScorpionsVEVO", "Scorpions")]
    [InlineData("Daft+Punk", "Daft Punk")]
    [InlineData("Radiohead", null)]
    [InlineData("VEVO", null)]
    public void Cleans_artist_names(string input, string? expected) =>
        Assert.Equal(expected, Cleanup.CleanArtist(input)?.Text);

    [Theory]
    [InlineData("Blackout (Remastered 2001)", "Scorpions", "Blackout")]
    [InlineData("Scorpions - Blackout (Official Video)", "Scorpions - Topic", "Blackout")]
    [InlineData("Scorpions-+Blackout+(Remastered+2001)", "Scorpions", "Blackout")]
    [InlineData("The+Zoo", "Scorpions", "The Zoo")]
    [InlineData("The Zoo [ky78cAmVfx4]", "Scorpions", "The Zoo")]
    [InlineData("Karma Police - 2009 Remaster", "Radiohead", "Karma Police")]
    [InlineData("Lyrics [Live]", "X", null)]
    [InlineData("Live - Wire", "AC/DC", null)]                        // a dash that is not "artist - title"
    [InlineData("Under Pressure (Lyrics)", "Queen", "Under Pressure")]
    [InlineData("Paranoid Android", "Radiohead", null)]
    public void Cleans_titles(string input, string artist, string? expected) =>
        Assert.Equal(expected, Cleanup.CleanTitle(input, artist)?.Text);

    [Fact]
    public void Splits_credits_only_on_known_artists_for_ambiguous_joins()
    {
        var known = new HashSet<string> { "Queen", "David Bowie" };
        bool Known(string n) => known.Contains(n);
        Assert.Equal(["Simon & Garfunkel"], MetadataEditor.SplitCredit("Simon & Garfunkel", Known));
        Assert.Equal(["Queen", "David Bowie"], MetadataEditor.SplitCredit("Queen & David Bowie", Known));
        Assert.Equal(["Calvin Harris", "Rihanna"], MetadataEditor.SplitCredit("Calvin Harris feat. Rihanna", Known));
    }

    [SkippableFact]
    public async Task Hand_edits_lock_fields_and_merge_artists()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.flac", new() { ["title"] = "Blackout (Remastered 2001)", ["artist"] = "Scorpions - Topic", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("b/1.flac", new() { ["title"] = "The Zoo", ["artist"] = "Scorpions", ["album"] = "Animal Magnetism", ["track"] = "1" }, seconds: 0.3);
        await lib.ScanAsync();
        Assert.Equal(2, lib.Queries.Artists().Count);

        var track = lib.Queries.Tracks("Blackout").Single();
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t),
        [
            new FieldChange("track", track.TrackId, "title", "Blackout"),
            new FieldChange("track", track.TrackId, "artist", "Scorpions"),
            new FieldChange("release", track.ReleaseId, "artist", "Scorpions"),
            new FieldChange("track", track.TrackId, "disc", "2"),
        ]));

        var edited = lib.Queries.ByTrack(track.TrackId)!;
        Assert.Equal("Blackout", edited.Title);
        Assert.Equal("Scorpions", edited.Artist);
        Assert.Equal(2, edited.Disc);
        Assert.Single(lib.Queries.Artists());                         // "Scorpions - Topic" is gone
        var states = MetadataEditor.States(lib.Db, "track", track.TrackId);
        Assert.True(states["title"].Locked);
        Assert.Equal("user", states["artist"].Source);

        // A locked field survives an automatic (unlocked) write.
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t),
            [new FieldChange("track", track.TrackId, "title", "Something else")], MetadataEditor.CleanupSource, lockFields: false));
        Assert.Equal("Blackout", lib.Queries.ByTrack(track.TrackId)!.Title);

        // Unlocking lets it through again.
        await lib.Db.Writer.Run((c, t) => MetadataEditor.SetLocked(new Tx(c, t), "track", track.TrackId, "title", false));
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t),
            [new FieldChange("track", track.TrackId, "title", "Blackout!")], MetadataEditor.CleanupSource, lockFields: false));
        Assert.Equal("Blackout!", lib.Queries.ByTrack(track.TrackId)!.Title);
    }

    [SkippableFact]
    public async Task Cleanup_suggestions_group_artists_and_skip_locked_fields()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.flac", new() { ["title"] = "Blackout (Official Video)", ["artist"] = "Scorpions - Topic", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("a/2.flac", new() { ["title"] = "Can't Live Without You", ["artist"] = "Scorpions - Topic", ["album"] = "Blackout", ["track"] = "2" }, seconds: 0.3);
        await lib.ScanAsync();

        var found = Cleanup.Find(lib.Db);
        var artist = Assert.Single(found, s => s.Kind == "Artist");
        Assert.Equal("Scorpions", artist.Suggested);
        Assert.Equal(3, artist.Changes.Count); // two tracks and the album
        var title = Assert.Single(found, s => s.Kind == "Track");
        Assert.Equal("Blackout", title.Suggested);

        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t), found.SelectMany(s => s.Changes),
            MetadataEditor.CleanupSource, lockFields: false));
        Assert.Empty(Cleanup.Find(lib.Db));
        Assert.All(lib.Queries.Tracks(), t => Assert.Equal("Scorpions", t.Artist));
        Assert.Equal("Scorpions", lib.Queries.Albums().Single().Artist);
    }

    [SkippableFact]
    public async Task Retagging_a_file_elsewhere_keeps_hand_edits()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        var path = lib.MakeTrack("a/1.flac", new() { ["title"] = "Blakout", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.3);
        await lib.ScanAsync();
        var track = lib.Queries.Tracks().Single();
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t), [new FieldChange("track", track.TrackId, "title", "Blackout")]));

        // Another program rewrites the tags (same audio, same path).
        lib.MakeTrack("a/1.flac", new() { ["title"] = "Something Else", ["artist"] = "Other", ["album"] = "Other", ["track"] = "1" }, seconds: 0.3);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await lib.ScanAsync();

        var after = lib.Queries.Tracks().Single();
        Assert.Equal(track.TrackId, after.TrackId);
        Assert.Equal("Blackout", after.Title);
    }

    [SkippableFact]
    public async Task Renaming_an_artist_keeps_its_fetched_profile()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.flac", new() { ["title"] = "Blackout", ["artist"] = "Scorpions - Topic", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.3);
        await lib.ScanAsync();
        var topic = lib.Queries.Artists().Single();
        var profile = new ArtistProfileData("mbid-1", "Scorpions", "Scorpions", "Group", "DE", "Germany", "1965", null, false, null,
            ["Hard Rock"], [new ArtistLink("Website", "https://example.org/")], [], [], "Q1", Bio: "A German band.");
        await lib.Db.Writer.Run((c, t) => ArtistProfiles.Apply(new Tx(c, t), lib.Images, topic.ArtistId, profile));

        // Tidy up renames every credit, which leaves the old artist without credits.
        var fixes = Cleanup.Find(lib.Db).SelectMany(s => s.Changes).ToList();
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t), fixes, MetadataEditor.CleanupSource, lockFields: false));

        var artist = Assert.Single(lib.Queries.Artists());
        Assert.Equal("Scorpions", artist.Name);
        Assert.NotEqual(topic.ArtistId, artist.ArtistId);
        var p = ArtistProfiles.Load(lib.Db, lib.Images, artist.ArtistId)!;
        Assert.Equal("mbid-1", p.Mbid);
        Assert.Equal("A German band.", p.Bio);
        Assert.Equal("group", p.Type);
        Assert.Equal(["Hard Rock"], p.Genres);
        Assert.Null(ArtistProfiles.Load(lib.Db, lib.Images, topic.ArtistId));
    }
}
