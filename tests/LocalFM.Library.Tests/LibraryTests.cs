using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Import;

namespace LocalFM.Library.Tests;

public class LibraryTests
{
    private static Dictionary<string, string> Tags(string title, string artist, string album, int track, string? date = null, int disc = 1, string? albumArtist = null)
    {
        var d = new Dictionary<string, string> { ["title"] = title, ["artist"] = artist, ["album"] = album, ["track"] = track.ToString(), ["disc"] = disc.ToString() };
        if (date is not null) d["date"] = date;
        if (albumArtist is not null) d["album_artist"] = albumArtist;
        return d;
    }

    [Fact]
    public void Migrates_empty_database()
    {
        using var lib = new TempLibrary();
        using var c = lib.Db.Open();
        Assert.Equal(1L, c.Scalar<long>("PRAGMA user_version"));
        foreach (var table in new[] { "artist", "artist_credit", "recording", "release_group", "release", "medium", "track", "audio_file", "playlist", "play_event", "field_provenance", "external_id" })
            Assert.Equal(1L, c.Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$1", table));
    }

    [SkippableFact]
    public async Task Scans_album_structure()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("A/01.flac", Tags("One", "The Band", "First", 1, "2001"));
        lib.MakeTrack("A/02.flac", Tags("Two", "the band", "First", 2, "2001"));
        lib.MakeTrack("A/CD2/01.flac", Tags("Three", "The Band", "First", 1, "2001", disc: 2));
        lib.MakeTrack("B/01.mp3", Tags("Solo", "Someone", "Other", 1, "1999"));
        await lib.ScanAsync();

        var albums = lib.Queries.Albums();
        Assert.Equal(2, albums.Count);
        var first = albums.Single(a => a.Title == "First");
        Assert.Equal(3, first.TrackCount);
        Assert.Equal("The Band", first.Artist, ignoreCase: true); // first-seen spelling wins; scan order is parallel

        var tracks = lib.Queries.AlbumTracks(first.ReleaseId);
        Assert.Equal(["One", "Two", "Three"], tracks.Select(t => t.Title));
        Assert.Equal([1, 1, 2], tracks.Select(t => t.Disc));

        // "The Band" and "the band" are one artist
        var artists = lib.Queries.Artists();
        Assert.Equal(2, artists.Count);
        Assert.Equal("Band, The", artists.Single(a => a.Name.Equals("The Band", StringComparison.OrdinalIgnoreCase)).SortName, ignoreCase: true);
    }

    [SkippableFact]
    public async Task Rescan_skips_unchanged_files()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("01.flac", Tags("One", "X", "Y", 1));
        await lib.ScanAsync();
        ScanProgress? last = null;
        lib.Scanner.Progress += p => last = p;
        await lib.ScanAsync();
        Assert.NotNull(last);
        Assert.Equal(0, last!.Added);
        Assert.Equal(0, last.Updated);
    }

    [SkippableFact]
    public async Task Tag_edit_keeps_identity_and_audio_hash()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        string path = lib.MakeTrack("01.flac", Tags("Old Title", "X", "Y", 1));
        var before = Scanner.ProbeFile(Ulid.Empty, path)!;
        await lib.ScanAsync();
        var fileIdBefore = lib.Queries.Tracks().Single().FileId;

        // Rewrite tags without touching audio.
        string tmp = path + ".tmp.flac";
        TempLibrary.Ffmpeg($"-i \"{path}\" -c copy -map_metadata -1 -metadata title=\"New Title\" -metadata artist=X -metadata album=Y -metadata track=1 \"{tmp}\"");
        File.Move(tmp, path, overwrite: true);
        var after = Scanner.ProbeFile(Ulid.Empty, path)!;
        Assert.Equal(before.AudioHash, after.AudioHash);
        Assert.NotEqual(before.TagsHash, after.TagsHash);

        await lib.ScanAsync();
        var t = lib.Queries.Tracks().Single();
        Assert.Equal(fileIdBefore, t.FileId);
        Assert.Equal("New Title", t.Title);
    }

    [SkippableFact]
    public async Task Moved_file_keeps_identity_and_history()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        string path = lib.MakeTrack("old/01.flac", Tags("Song", "X", "Y", 1));
        await lib.ScanAsync();
        var t = lib.Queries.Tracks().Single();
        var history = new History(lib.Db);
        await history.RecordPlayAsync(t.RecordingId, t.FileId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60), true);

        string moved = Path.Combine(lib.Music, "new", "renamed.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(path, moved);
        await lib.ScanAsync();

        var after = lib.Queries.Tracks().Single();
        Assert.Equal(t.FileId, after.FileId);
        Assert.Equal(t.RecordingId, after.RecordingId);
        Assert.Equal(moved, after.Path);
        Assert.Equal(1, history.PlayCount(after.RecordingId));
    }

    [SkippableFact]
    public async Task Editions_share_release_group()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("orig/01.flac", Tags("Song", "X", "Album", 1, "1990"));
        lib.MakeTrack("remaster/01.flac", Tags("Song", "X", "Album", 1, "2015"));
        await lib.ScanAsync();
        var albums = lib.Queries.Albums();
        Assert.Equal(2, albums.Count);
        Assert.Single(albums.Select(a => a.ReleaseGroupId).Distinct());
    }

    [SkippableFact]
    public async Task Album_counts_owned_tracks()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/01.flac", Tags("One", "X", "Album", 1));
        lib.MakeTrack("a/02.flac", Tags("Two", "X", "Album", 2));
        await lib.ScanAsync();
        var full = lib.Queries.Albums().Single();
        Assert.Equal((2, 2, "2"), (full.TrackCount, full.OwnedCount, full.OwnedText));

        using (var c = lib.Db.Open()) c.Exec("UPDATE audio_file SET missing = 1 WHERE path LIKE '%02.flac'");
        var partial = lib.Queries.Albums().Single();
        Assert.Equal((2, 1, "1/2"), (partial.TrackCount, partial.OwnedCount, partial.OwnedText));
    }

    [SkippableFact]
    public async Task Embedded_cover_is_stored_once()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        string png = lib.MakePng();
        lib.MakeTrack("01.mp3", Tags("One", "X", "Covered", 1), coverPng: png);
        lib.MakeTrack("02.mp3", Tags("Two", "X", "Covered", 2), coverPng: png);
        await lib.ScanAsync();
        var album = lib.Queries.Albums().Single();
        Assert.NotNull(album.CoverPath);
        Assert.True(File.Exists(album.CoverPath));
        using var c = lib.Db.Open();
        Assert.Equal(1L, c.Scalar<long>("SELECT COUNT(*) FROM image"));
    }

    [SkippableFact]
    public async Task Same_audio_on_two_releases_is_one_recording_two_tracks()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        string original = lib.MakeTrack("album/01.flac", Tags("Hit", "X", "Album", 3));
        string compilation = Path.Combine(lib.Music, "best-of", "05.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(compilation)!);
        TempLibrary.Ffmpeg($"-i \"{original}\" -c copy -map_metadata -1 -metadata title=Hit -metadata artist=X -metadata album=\"Best Of\" -metadata track=5 \"{compilation}\"");
        await lib.ScanAsync();

        var tracks = lib.Queries.Tracks();
        Assert.Equal(2, tracks.Count);
        Assert.Equal(2, tracks.Select(t => t.TrackId).Distinct().Count());
        Assert.Single(tracks.Select(t => t.RecordingId).Distinct());
        Assert.Equal(2, lib.Queries.Albums().Count);
    }

    [Fact]
    public void Parses_tag_variants()
    {
        var t = FileTags.Parse([
            new("TITLE", "  Song "), new("MusicBrainz Album Id", "ABC"), new("track", "3/12"),
            new("REPLAYGAIN_TRACK_GAIN", "-6.5 dB"), new("ARTISTS", "A;B"), new("genre", "Rock;Pop"),
            new("lyrics-eng", "la la"),
        ]);
        Assert.Equal("Song", t.Title);
        Assert.Equal("ABC", t.MbReleaseId);
        Assert.Equal(3, t.TrackNumber);
        Assert.Equal(12, t.TrackTotal);
        Assert.Equal(-6.5, t.TrackGainDb);
        Assert.Equal(["A", "B"], t.Artists);
        Assert.Equal(["Rock", "Pop"], t.Genres);
        Assert.Equal("la la", t.Lyrics);
    }

    [Fact]
    public void Normalized_keys()
    {
        Assert.Equal(Normalize.Key("Beyoncé"), Normalize.Key("BEYONCE"));
        Assert.Equal(Normalize.Key("Simon & Garfunkel"), Normalize.Key("simon and  garfunkel"));
        Assert.Equal("ac dc", Normalize.Key("AC/DC"));
    }

    [Fact]
    public void Ulid_roundtrips_and_sorts()
    {
        var a = Ulid.NewUlid(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var b = Ulid.NewUlid(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        Assert.Equal(a, Ulid.Parse(a.ToString()));
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
        Assert.Equal(1000, a.Time.ToUnixTimeMilliseconds());
    }
}
