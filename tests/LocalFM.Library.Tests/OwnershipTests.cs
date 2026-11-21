namespace LocalFM.Library.Tests;

public sealed class OwnershipTests
{
    private static Dictionary<string, string> Tags(string title, string artist, string album) =>
        new() { ["title"] = title, ["artist"] = artist, ["album"] = album, ["track"] = "1" };

    [SkippableFact]
    public async Task Tells_owned_songs_from_other_versions()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("Scorpions/01.flac", Tags("Rock You Like a Hurricane", "Scorpions", "Love at First Sting"), seconds: 3);
        await lib.ScanAsync();
        var own = Ownership.Load(lib.Db);

        // A plain video of it, a remaster and a featured credit are the song you have.
        Assert.Equal(OwnedKind.Likely, own.Find("Scorpions", "Rock You Like A Hurricane (Official Music Video)", null, 3000).Kind);
        Assert.Equal(OwnedKind.Likely, own.Find("Scorpions", "Rock You Like a Hurricane (2015 Remaster)", "Love at First Sting", 9000).Kind);
        Assert.Equal(OwnedKind.Likely, own.Find("Scorpions feat. Someone", "Rock You Like a Hurricane", null, null).Kind);
        Assert.Equal(OwnedKind.Likely, own.Find("The Scorpions", "Rock you like a hurricane", null, 4000).Kind);

        // A re-recording on another album, a live take or a clearly different length is another version.
        var comeblack = own.Find("Scorpions", "Rock You Like a Hurricane", "Comeblack", 3000);
        Assert.Equal(OwnedKind.OtherVersion, comeblack.Kind);
        Assert.Equal("Love at First Sting", comeblack.Album);
        Assert.Equal(OwnedKind.OtherVersion, own.Find("Scorpions", "Rock You Like a Hurricane (Live at Wacken)", null, 3000).Kind);
        Assert.Equal(OwnedKind.OtherVersion, own.Find("Scorpions", "Rock You Like a Hurricane - Acoustic", null, null).Kind);
        Assert.Equal(OwnedKind.OtherVersion, own.Find("Scorpions", "Rock You Like a Hurricane", null, 60_000).Kind);

        // Another artist's song of the same name, or another song, is not yours.
        Assert.Equal(OwnedKind.None, own.Find("Someone Else", "Rock You Like a Hurricane", null, 3000).Kind);
        Assert.Equal(OwnedKind.None, own.Find("Scorpions", "Wind of Change", null, 3000).Kind);
        Assert.Equal(OwnedKind.None, own.Find(null, "Rock You Like a Hurricane", null, 3000).Kind);
    }

    [SkippableFact]
    public async Task Missing_files_are_not_owned()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        string path = lib.MakeTrack("a.flac", Tags("Song", "Artist", "Album"));
        await lib.ScanAsync();
        File.Delete(path);
        await lib.ScanAsync();
        Assert.Equal(OwnedKind.None, Ownership.Load(lib.Db).Find("Artist", "Song", null, null).Kind);
    }
}
