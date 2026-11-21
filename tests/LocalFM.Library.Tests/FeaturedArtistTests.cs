using LocalFM.Library.Edit;

namespace LocalFM.Library.Tests;

public sealed class FeaturedArtistTests
{
    [Theory]
    [InlineData("Slash, Chris Cornell", "Promise (feat. Chris Cornell)", "Slash")]
    [InlineData("Slash feat. Iggy Pop", "We're All Gonna Die (feat. Iggy Pop)", "Slash")]
    [InlineData("A, B & C", "Song (feat. B & C)", "A")]
    [InlineData("A, B", "Song ft. B", "A")]
    [InlineData("Simon & Garfunkel, X", "Song [feat. X]", "Simon & Garfunkel")]
    [InlineData("Simon & Garfunkel", "Song", null)]
    [InlineData("Slash", "Promise (feat. Chris Cornell)", null)]
    [InlineData("Jay-Z, Kanye West", "Otis", null)]
    [InlineData("Chris Cornell, Slash", "Promise (feat. Chris Cornell)", null)]
    public void Without_featured(string artist, string title, string? expected) =>
        Assert.Equal(expected, Cleanup.WithoutFeatured(artist, title));

    private static Dictionary<string, string> Tags(string title, string artist, int n) =>
        new() { ["title"] = title, ["artist"] = artist, ["album"] = "Slash", ["date"] = "2010", ["track"] = n.ToString() };

    private static List<(string Album, string? Artist)> Albums(TempLibrary lib)
    {
        using var c = lib.Db.Open();
        return c.Query("""
            SELECT r.title, ac.display_name FROM release r LEFT JOIN artist_credit ac ON ac.id = r.artist_credit_id
            WHERE EXISTS (SELECT 1 FROM medium m JOIN track t ON t.medium_id = m.id WHERE m.release_id = r.id)
            """, r => (r.GetString(0), r.Str(1)));
    }

    [SkippableFact]
    public async Task Guests_do_not_split_an_album()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a.m4a", Tags("Promise (feat. Chris Cornell)", "Slash, Chris Cornell", 1));
        lib.MakeTrack("b.m4a", Tags("We're All Gonna Die (feat. Iggy Pop)", "Slash, Iggy Pop", 2));
        lib.MakeTrack("c.m4a", Tags("Starlight (feat. Myles Kennedy)", "Slash, Myles Kennedy", 3));
        await lib.ScanAsync();
        Assert.Equal([("Slash", "Slash")], Albums(lib));
    }

    [SkippableFact]
    public async Task Repair_joins_albums_split_by_guests()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        // Titles without "feat." import split, as older versions did; then the titles say who is featured.
        lib.MakeTrack("a.m4a", Tags("Promise", "Slash, Chris Cornell", 1));
        lib.MakeTrack("b.m4a", Tags("We're All Gonna Die", "Slash, Iggy Pop", 2));
        lib.MakeTrack("c.m4a", Tags("Starlight", "Slash, Myles Kennedy", 3));
        await lib.ScanAsync();
        Assert.Equal(3, Albums(lib).Count);
        await lib.Db.Writer.Run((c, t) => new Tx(c, t).Exec("""
            UPDATE track SET title = title || ' (feat. ' || substr((SELECT display_name FROM artist_credit WHERE id = track.artist_credit_id), 8) || ')'
            """));

        int n = await lib.Db.Writer.Run((c, t) => FeaturedArtistRepair.Run(new Tx(c, t)));
        Assert.Equal(3, n);
        Assert.Equal([("Slash", "Slash")], Albums(lib));
        using (var c = lib.Db.Open())
        {
            Assert.Equal(3L, c.Scalar<long>("SELECT COUNT(*) FROM track t JOIN artist_credit ac ON ac.id = t.artist_credit_id WHERE ac.display_name = 'Slash'"));
            Assert.Equal(0L, c.Scalar<long>("SELECT COUNT(*) FROM artist WHERE name LIKE 'Slash, %'"));
        }
        // Nothing left to do the second time.
        Assert.Equal(0, await lib.Db.Writer.Run((c, t) => FeaturedArtistRepair.Run(new Tx(c, t))));
    }
}
