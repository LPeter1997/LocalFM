using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Library.Tests;

namespace LocalFM.Metadata.Tests;

public class ArtistTests
{
    private const string ScorpionsMbid = "c3cceeed-3332-4cf0-8c4c-bbde425147b6";

    private const string MbArtist = """
        {"id":"c3cceeed-3332-4cf0-8c4c-bbde425147b6","name":"Scorpions","sort-name":"Scorpions","type":"Group","country":"DE",
         "disambiguation":"German hard rock band","area":{"name":"Germany"},
         "life-span":{"begin":"1965","end":null,"ended":false},
         "genres":[{"name":"hard rock","count":12},{"name":"heavy metal","count":9}],
         "relations":[
          {"target-type":"url","type":"wikidata","ended":false,"url":{"resource":"https://www.wikidata.org/wiki/Q152493"}},
          {"target-type":"url","type":"official homepage","ended":false,"url":{"resource":"https://www.the-scorpions.com/"}},
          {"target-type":"url","type":"discogs","ended":false,"url":{"resource":"https://www.discogs.com/artist/39183"}},
          {"target-type":"url","type":"myspace","ended":true,"url":{"resource":"https://myspace.com/scorpions"}},
          {"target-type":"artist","type":"member of band","direction":"backward","begin":"1965","end":null,"ended":false,
           "attributes":["guitar","founder"],"artist":{"id":"r1","name":"Rudolf Schenker"}},
          {"target-type":"artist","type":"member of band","direction":"backward","begin":"1970","end":null,"ended":false,
           "attributes":["lead vocals"],"artist":{"id":"k1","name":"Klaus Meine"}},
          {"target-type":"artist","type":"member of band","direction":"backward","begin":"1972","end":"1974","ended":true,
           "attributes":["guitar"],"artist":{"id":"u1","name":"Uli Jon Roth"}},
          {"target-type":"artist","type":"member of band","direction":"backward","begin":"1973","end":"1978","ended":true,
           "attributes":["guitar"],"artist":{"id":"u1","name":"Uli Jon Roth"}}
         ]}
        """;

    private const string Wikidata = """
        {"entities":{"Q152493":{"descriptions":{"en":{"value":"German rock band"}},
          "claims":{"P18":[{"mainsnak":{"datavalue":{"value":"Scorpions band.jpg"}}}]},
          "sitelinks":{"enwiki":{"title":"Scorpions (band)"},"dewiki":{"title":"Scorpions"}}}}}
        """;

    private const string Summary = """
        {"type":"standard","title":"Scorpions (band)","extract":"Scorpions are a German rock band formed in 1965 in Hanover.",
         "content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Scorpions_(band)"}}}
        """;

    private static async Task<(TempLibrary Lib, Ulid ArtistId)> Library()
    {
        var lib = new TempLibrary();
        lib.MakeTrack("a/1.flac", new() { ["title"] = "Blackout", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.3);
        await lib.ScanAsync();
        return (lib, lib.Queries.Artists().Single().ArtistId);
    }

    [SkippableFact]
    public async Task Fetches_profile_with_members_bio_and_photo()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (lib, artistId) = await Library();
        using var _ = lib;
        var png = File.ReadAllBytes(lib.MakePng());
        var fake = new FakeHandler()
            .OnJson("ws/2/artist/?query", $$"""{"artists":[{"id":"{{ScorpionsMbid}}","name":"Scorpions","score":100,"type":"Group"},{"id":"x","name":"Scorpions Tribute","score":60}]}""")
            .OnJson($"ws/2/artist/{ScorpionsMbid}", MbArtist)
            .OnJson("wikidata.org/wiki/Special:EntityData/Q152493.json", Wikidata)
            .OnJson("en.wikipedia.org/api/rest_v1/page/summary/Scorpions_%28band%29", Summary)
            .OnBytes("commons.wikimedia.org/wiki/Special:FilePath/Scorpions_band.jpg", png, "image/png");
        var svc = new MetadataService(lib.Db, lib.Images, new History(lib.Db), new HttpClient(fake));
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

        var result = await svc.FetchArtistProfileAsync(artistId);
        Assert.True(result.Updated, result.Message);

        var p = ArtistProfiles.Load(lib.Db, lib.Images, artistId)!;
        Assert.Equal("group", p.Type);
        Assert.Equal("DE", p.Country);
        Assert.Equal("1965", p.Begin);
        Assert.Equal("German rock band", p.Description);
        Assert.StartsWith("Scorpions are a German rock band", p.Bio);
        Assert.Equal("https://en.wikipedia.org/wiki/Scorpions_(band)", p.BioUrl);
        Assert.True(File.Exists(p.PhotoPath));
        Assert.Equal(["Hard Rock", "Heavy Metal"], p.Genres);
        Assert.Equal(["Website", "Discogs"], p.Links.Select(l => l.Label));     // ended links are dropped
        Assert.Equal(["Rudolf Schenker", "Klaus Meine", "Uli Jon Roth"], p.Members.Select(m => m.Name));
        var uli = p.Members[2];
        Assert.Equal("1972–1978", uli.Years);                                    // two stints, one line
        Assert.Equal("1965–", p.Members[0].Years);
        Assert.Equal(ScorpionsMbid, p.Mbid);
        Assert.False(svc.ShouldAutoFetchProfile(p));                             // just fetched

        // A hand-set type survives a refresh.
        await lib.Db.Writer.Run((c, t) => LocalFM.Library.Edit.MetadataEditor.Apply(new Tx(c, t),
            [new LocalFM.Library.Edit.FieldChange("artist", artistId, "type", "duo")]));
        Assert.True((await svc.FetchArtistProfileAsync(artistId)).Updated);
        Assert.Equal("duo", ArtistProfiles.Load(lib.Db, lib.Images, artistId)!.Type);
    }

    [SkippableFact]
    public async Task Ambiguous_names_return_candidates_and_store_nothing()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (lib, artistId) = await Library();
        using var _ = lib;
        var fake = new FakeHandler().OnJson("ws/2/artist/?query", """
            {"artists":[{"id":"a","name":"Scorpions","score":100,"disambiguation":"German rock band"},
                        {"id":"b","name":"Scorpions","score":100,"disambiguation":"Polish punk band"}]}
            """);
        var svc = new MetadataService(lib.Db, lib.Images, new History(lib.Db), new HttpClient(fake));
        var result = await svc.FetchArtistProfileAsync(artistId);
        Assert.False(result.Updated);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(ArtistProfiles.Load(lib.Db, lib.Images, artistId)!.FetchedAt);
    }
}
