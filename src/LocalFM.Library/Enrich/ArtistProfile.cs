using LocalFM.Core;
using LocalFM.Library.Import;

namespace LocalFM.Library.Enrich;

/// <summary>A band member, or a band this artist belongs to.</summary>
public sealed record ArtistMember(string Name, string? Mbid, string? Begin, string? End, bool Ended, string? Roles)
{
    public string Years => (Begin?.Length >= 4 ? Begin[..4] : null, End?.Length >= 4 ? End[..4] : null, Ended) switch
    {
        (null, null, _) => "",
        ({ } b, null, false) => $"{b}–",
        ({ } b, null, true) => b,
        ({ } b, { } e, _) => b == e ? b : $"{b}–{e}",
        (null, { } e, _) => $"–{e}",
    };
}

public sealed record ArtistLink(string Label, string Url);

/// <summary>Everything an online lookup knows about an artist (MusicBrainz, plus Wikidata/Wikipedia when linked).</summary>
public sealed record ArtistProfileData(
    string Mbid,
    string Name,
    string? SortName,
    string? Type,
    string? Country,
    string? Area,
    string? Begin,
    string? End,
    bool Ended,
    string? Disambiguation,
    IReadOnlyList<string> Genres,
    IReadOnlyList<ArtistLink> Links,
    IReadOnlyList<ArtistMember> Members,
    IReadOnlyList<ArtistMember> MemberOf,
    string? WikidataId = null,
    string? Description = null,
    string? Bio = null,
    string? BioUrl = null,
    string? BioSource = null);

/// <summary>What the artist page shows.</summary>
public sealed record ArtistProfileView(
    Ulid ArtistId,
    string Name,
    string? Type,
    string? Country,
    string? Area,
    string? Begin,
    string? End,
    string? Disambiguation,
    string? Description,
    string? Bio,
    string? BioUrl,
    string? BioSource,
    string? PhotoPath,
    IReadOnlyList<string> Genres,
    IReadOnlyList<ArtistLink> Links,
    IReadOnlyList<ArtistMember> Members,
    IReadOnlyList<ArtistMember> MemberOf,
    string? Mbid,
    DateTimeOffset? FetchedAt);

/// <summary>Stores and reads artist profiles. Columns go through the lock-aware field setter; the rest are attributes.</summary>
public static class ArtistProfiles
{
    public const string Source = "musicbrainz";
    public const string WikiSource = "wikipedia";
    public const string PhotoSource = "wikimedia";

    public static void Apply(Tx tx, ImageStore images, Ulid artistId, ArtistProfileData p, byte[]? photo = null, string? photoMime = null)
    {
        long now = Sql.Now();
        // Identity: link the MusicBrainz artist unless another local artist already owns that id.
        var owner = Importer.ByExternal(tx, "artist", Source, p.Mbid);
        if (owner is null) tx.Exec("INSERT OR IGNORE INTO external_id VALUES ('artist',$1,$2,$3)", artistId, Source, p.Mbid);
        else if (owner != artistId) Log.Warn("Artist", $"MusicBrainz artist {p.Mbid} already belongs to another artist; profile stored without linking");
        if (p.WikidataId is { } q)
        {
            tx.Exec("DELETE FROM external_id WHERE entity_type='artist' AND entity_id=$1 AND provider='wikidata'", artistId);
            tx.Exec("INSERT OR IGNORE INTO external_id VALUES ('artist',$1,'wikidata',$2)", artistId, q);
        }

        // Columns (locked fields stay as the person set them). The display name is not changed here:
        // credits already carry the provider's spelling when an album was matched.
        MetadataApplier.SetField(tx, "artist", artistId, "sort_name", p.SortName, Source, now);
        MetadataApplier.SetField(tx, "artist", artistId, "type", p.Type?.ToLowerInvariant(), Source, now);
        MetadataApplier.SetField(tx, "artist", artistId, "country", p.Country, Source, now);
        MetadataApplier.SetField(tx, "artist", artistId, "disambiguation", p.Disambiguation, Source, now);
        MetadataApplier.SetField(tx, "artist", artistId, "begin_date", p.Begin, Source, now);
        MetadataApplier.SetField(tx, "artist", artistId, "end_date", p.End ?? (p.Ended ? "" : null), Source, now);

        SetAttr(tx, artistId, "area", p.Area, Source, now);
        SetAttr(tx, artistId, "members", Lines(p.Members), Source, now);
        SetAttr(tx, artistId, "member_of", Lines(p.MemberOf), Source, now);
        SetAttr(tx, artistId, "links", p.Links.Count == 0 ? null : string.Join('\n', p.Links.Select(l => $"{Clean(l.Label)}\t{Clean(l.Url)}")), Source, now);
        SetAttr(tx, artistId, "profile_fetched", now.ToString(), Source, now);
        SetAttr(tx, artistId, "description", p.Description, WikiSource, now);
        SetAttr(tx, artistId, "bio", p.Bio, WikiSource, now);
        SetAttr(tx, artistId, "bio_url", p.BioUrl, WikiSource, now);
        SetAttr(tx, artistId, "bio_source", p.BioSource, WikiSource, now);

        if (p.Genres.Count > 0)
        {
            tx.Exec("DELETE FROM entity_genre WHERE entity_type = 'artist' AND entity_id = $1 AND source = $2", artistId, Source);
            foreach (var g in p.Genres.Take(8)) Importer.LinkGenre(tx, "artist", artistId, g, Source);
        }

        if (photo is { Length: > 0 } && photoMime is not null)
        {
            var imageId = images.Store(tx, photo, photoMime);
            tx.Exec("DELETE FROM entity_image WHERE entity_type = 'artist' AND entity_id = $1 AND role = 'photo' AND source = $2", artistId, PhotoSource);
            tx.Exec("INSERT OR IGNORE INTO entity_image(entity_type, entity_id, image_id, role, source) VALUES ('artist',$1,$2,'photo',$3)",
                artistId, imageId, PhotoSource);
        }
    }

    public static ArtistProfileView? Load(Database db, ImageStore images, Ulid artistId)
    {
        using var c = db.Open();
        var row = c.QuerySingle("SELECT name, type, country, begin_date, end_date, disambiguation FROM artist WHERE id = $1",
            r => (Name: r.GetString(0), Type: r.Str(1), Country: r.Str(2), Begin: r.Str(3), End: r.Str(4), Dis: r.Str(5)), artistId);
        if (row.Name is null) return null;
        var attrs = c.Query("SELECT key, value FROM attribute WHERE entity_type = 'artist' AND entity_id = $1",
            r => (Key: r.GetString(0), Value: r.GetString(1)), artistId)
            .GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First().Value);
        string? A(string k) => attrs.GetValueOrDefault(k) is { Length: > 0 } v ? v : null;
        var photo = c.Scalar<string>("""
            SELECT i.rel_path FROM entity_image ei JOIN image i ON i.id = ei.image_id
            WHERE ei.entity_type = 'artist' AND ei.entity_id = $1 AND ei.role = 'photo'
            ORDER BY CASE ei.source WHEN 'user' THEN 0 ELSE 1 END LIMIT 1
            """, artistId);
        var genres = c.Query("""
            SELECT g.name FROM entity_genre eg JOIN genre g ON g.id = eg.genre_id
            WHERE eg.entity_type = 'artist' AND eg.entity_id = $1 ORDER BY g.name
            """, r => r.GetString(0), artistId);
        var mbid = c.Scalar<string>("SELECT external_id FROM external_id WHERE entity_type='artist' AND entity_id=$1 AND provider=$2", artistId, Source);
        DateTimeOffset? fetched = long.TryParse(A("profile_fetched"), out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        return new ArtistProfileView(artistId, row.Name, row.Type, row.Country, A("area"), row.Begin, row.End is { Length: > 0 } e ? e : null,
            row.Dis, A("description"), A("bio"), A("bio_url"), A("bio_source"), photo is null ? null : images.FullPath(photo),
            genres, ParseLinks(A("links")), ParseMembers(A("members")), ParseMembers(A("member_of")), mbid, fetched);
    }

    /// <summary>Local artist ids that have a MusicBrainz id, keyed by it (to link members to their pages).</summary>
    public static Dictionary<string, Ulid> LocalArtistsByMbid(Database db)
    {
        using var c = db.Open();
        return c.Query("SELECT external_id, entity_id FROM external_id WHERE entity_type='artist' AND provider=$1",
            r => (Mbid: r.GetString(0), Id: r.Id(1)), Source).GroupBy(x => x.Mbid).ToDictionary(g => g.Key, g => g.First().Id);
    }

    private static void SetAttr(Tx tx, Ulid id, string key, string? value, string source, long now)
    {
        if (value is null) tx.Exec("DELETE FROM attribute WHERE entity_type='artist' AND entity_id=$1 AND key=$2 AND source=$3", id, key, source);
        else tx.Exec("INSERT OR REPLACE INTO attribute(entity_type, entity_id, key, value, source, updated_at) VALUES ('artist',$1,$2,$3,$4,$5)",
            id, key, value, source, now);
    }

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Trim();

    private static string? Lines(IReadOnlyList<ArtistMember> list) => list.Count == 0 ? null : string.Join('\n', list.Select(m =>
        $"{Clean(m.Name)}\t{Clean(m.Mbid)}\t{Clean(m.Begin)}\t{Clean(m.End)}\t{(m.Ended ? 1 : 0)}\t{Clean(m.Roles)}"));

    private static List<ArtistMember> ParseMembers(string? text) => (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Split('\t')).Where(p => p.Length >= 6)
        .Select(p => new ArtistMember(p[0], N(p[1]), N(p[2]), N(p[3]), p[4] == "1", N(p[5]))).ToList();

    private static List<ArtistLink> ParseLinks(string? text) => (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Split('\t')).Where(p => p.Length == 2).Select(p => new ArtistLink(p[0], p[1])).ToList();

    private static string? N(string s) => s.Length == 0 ? null : s;
}
