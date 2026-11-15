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

    [Theory]
    [InlineData("Guns N' Roses - Civil War", "anbj0991", "Guns N’ Roses", "Civil War")]
    [InlineData("Guns N' Roses - Civil War (Official Video) [PAYmGCYhVAo]", "someone", "Guns N’ Roses", "Civil War")]
    [InlineData("Guns N' Roses - Civil War", "Guns N' Roses", null, null)]        // the artist-repeated rule handles this
    [InlineData("Live - Wire", "AC/DC", null, null)]                            // "Live" is not an artist we know
    [InlineData("Civil War", "anbj0991", null, null)]
    public void Splits_artist_out_of_uploaded_titles(string title, string artist, string? expectArtist, string? expectTitle)
    {
        string? Known(string n) => Normalize.Key(n) == "guns n roses" ? "Guns N’ Roses" : null;
        var split = Cleanup.SplitArtistTitle(title, artist, Known);
        Assert.Equal(expectArtist, split?.Artist);
        Assert.Equal(expectTitle, split?.Title);
    }

    [SkippableFact]
    public async Task Tidy_up_moves_the_artist_out_of_an_uploaded_title()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.mp3", new() { ["title"] = "Welcome to the Jungle", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("yt/cw.mp3", new() { ["title"] = "Guns N' Roses - Civil War", ["artist"] = "anbj0991", ["album"] = "Guns N' Roses - Civil War" }, seconds: 0.3);
        await lib.ScanAsync();

        var found = Cleanup.Find(lib.Db).Where(x => x.Reason.Contains("uploader")).ToList();
        Assert.Equal(["Album", "Track"], found.Select(x => x.Kind).Order());
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t), found.SelectMany(x => x.Changes), MetadataEditor.CleanupSource, lockFields: false));

        var cw = lib.Queries.Tracks("Civil War").Single();
        Assert.Equal("Civil War", cw.Title);
        Assert.Equal("Guns N' Roses", cw.Artist);
        Assert.Equal("Civil War", cw.Album);
        Assert.Equal("Guns N' Roses", cw.AlbumArtist);
        Assert.DoesNotContain(lib.Queries.Artists(), a => a.Name == "anbj0991");
    }

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
    public async Task Album_artist_edit_carries_tracks_credited_like_the_album()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        const string band = "Slash featuring Myles Kennedy and the Conspirators";
        lib.MakeTrack("a/1.flac", new() { ["title"] = "No More Heroes", ["artist"] = band, ["album"] = "Apocalyptic Love", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("a/2.flac", new() { ["title"] = "Guest Song", ["artist"] = "Slash feat. Someone Else", ["albumartist"] = band, ["album"] = "Apocalyptic Love", ["track"] = "2" }, seconds: 0.3);
        await lib.ScanAsync();

        var heroes = lib.Queries.Tracks("No More Heroes").Single();
        await lib.Db.Writer.Run((c, t) => MetadataEditor.Apply(new Tx(c, t),
            [new FieldChange("release", heroes.ReleaseId, "artist", "Slash feat. Myles Kennedy & The Conspirators")]));

        Assert.Equal("Slash feat. Myles Kennedy & The Conspirators", lib.Queries.ByTrack(heroes.TrackId)!.Artist);
        Assert.Equal("Slash feat. Someone Else", lib.Queries.Tracks("Guest Song").Single().Artist); // its own credit stays
        var artists = lib.Queries.Artists();
        Assert.DoesNotContain(artists, a => a.Name == band);                                          // old band pruned
        var slash = artists.Single(a => a.Name == "Slash");
        Assert.Equal(1, slash.AlbumCount);
        Assert.Equal(1, slash.TrackCount);                                                            // the guest track is a different credit
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

    [SkippableFact]
    public async Task Moving_a_track_to_another_copy_of_its_album_merges_them()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/7.mp3", new() { ["title"] = "My Michelle", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "7", ["date"] = "1987" }, seconds: 0.3);
        lib.MakeTrack("b/12.mp3", new() { ["title"] = "Rocket Queen", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "12", ["date"] = "1998" }, seconds: 0.3);
        lib.MakeTrack("b/7.mp3", new() { ["title"] = "Paradise City", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "7", ["date"] = "1998" }, seconds: 0.3);
        await lib.ScanAsync();
        var michelle = lib.Queries.Tracks("My Michelle").Single();
        var target = lib.Queries.Tracks("Rocket Queen").Single().ReleaseId;
        Assert.Equal(target, lib.Queries.OtherCopies(michelle.ReleaseId).Single().ReleaseId);

        int moved = await lib.Db.Writer.Run((c, t) => MetadataEditor.MoveTracks(new Tx(c, t), [michelle.TrackId], target));

        Assert.Equal(1, moved);
        var album = lib.Queries.Albums().Single();                    // the emptied copy is gone
        Assert.Equal(target, album.ReleaseId);
        var moved1 = lib.Queries.ByTrack(michelle.TrackId)!;
        Assert.Equal(target, moved1.ReleaseId);
        Assert.Equal(13, moved1.Position);                             // slot 7 was taken by another song: appended
        Assert.True(MetadataEditor.States(lib.Db, "track", michelle.TrackId)["disc"].Locked);
        Assert.Empty(lib.Queries.OtherCopies(target));
    }

    [SkippableFact]
    public async Task A_moved_track_keeps_its_old_albums_artist_only_when_the_new_album_is_someone_elses()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.mp3", new() { ["title"] = "Sweet Child O' Mine", ["album_artist"] = "Guns N' Roses", ["album"] = "Single", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("b/7.mp3", new() { ["title"] = "My Michelle", ["artist"] = "Guns N’ Roses", ["album_artist"] = "Guns N’ Roses", ["album"] = "Appetite for Destruction", ["track"] = "7" }, seconds: 0.3);
        lib.MakeTrack("c/1.mp3", new() { ["title"] = "Patience", ["album_artist"] = "Guns N' Roses", ["album"] = "Lies", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("d/1.mp3", new() { ["title"] = "Some Song", ["artist"] = "Various", ["album_artist"] = "Various Artists", ["album"] = "Mix", ["track"] = "1" }, seconds: 0.3);
        await lib.ScanAsync();
        var sweet = lib.Queries.Tracks("Sweet Child").Single();
        var patience = lib.Queries.Tracks("Patience").Single();
        var appetite = lib.Queries.Tracks("My Michelle").Single().ReleaseId;
        var mix = lib.Queries.Tracks("Some Song").Single().ReleaseId;
        using (var c = lib.Db.Open())
        {
            c.Exec("UPDATE track SET artist_credit_id = NULL WHERE id IN ($1, $2)", sweet.TrackId, patience.TrackId);
        }

        await lib.Db.Writer.Run((c, t) => MetadataEditor.MoveTracks(new Tx(c, t), [sweet.TrackId], appetite));
        await lib.Db.Writer.Run((c, t) => MetadataEditor.MoveTracks(new Tx(c, t), [patience.TrackId], mix));

        Assert.Null(MetadataEditor.LoadAlbum(lib.Db, appetite)!.Tracks.Single(t => t.TrackId == sweet.TrackId).Artist); // same act: still the album's
        Assert.Equal("guns n roses", Normalize.Key(MetadataEditor.LoadAlbum(lib.Db, mix)!.Tracks.Single(t => t.TrackId == patience.TrackId).Artist));
    }

    [SkippableFact]
    public async Task Merging_copies_keeps_one_track_per_song()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.mp3", new() { ["title"] = "Welcome to the Jungle", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "1", ["date"] = "1987" }, seconds: 0.3);
        lib.MakeTrack("b/1.flac", new() { ["title"] = "Welcome to the Jungle", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "1", ["date"] = "1998" }, seconds: 0.4);
        lib.MakeTrack("b/2.flac", new() { ["title"] = "It's So Easy", ["artist"] = "Guns N' Roses", ["album"] = "Appetite for Destruction", ["track"] = "2", ["date"] = "1998" }, seconds: 0.3);
        await lib.ScanAsync();
        var albums = lib.Queries.Albums();
        Assert.Equal(2, albums.Count);
        var small = albums.Single(a => a.TrackCount == 1);
        var big = albums.Single(a => a.TrackCount == 2);

        await lib.Db.Writer.Run((c, t) => MetadataEditor.MergeRelease(new Tx(c, t), small.ReleaseId, big.ReleaseId));

        var merged = lib.Queries.Albums().Single();
        Assert.Equal(2, merged.TrackCount);                            // both Jungle files now belong to one track
        using var c = lib.Db.Open();
        Assert.Equal(3L, c.Scalar<long>("SELECT COUNT(*) FROM audio_file WHERE track_id IS NOT NULL"));
        Assert.Equal(2L, c.Scalar<long>("SELECT COUNT(*) FROM recording"));
    }
}
