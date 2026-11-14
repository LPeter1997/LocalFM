using LocalFM.Core;
using System.Globalization;
using System.Text.Json;
using LocalFM.Library.Enrich;

namespace LocalFM.Metadata;

public sealed record ArtistCandidate(string Mbid, string Name, string? Disambiguation, string? Type, string? Country,
    string? Begin, string? End, int Score);

/// <summary>MusicBrainz artist lookups (search and full profile with relationships).</summary>
public sealed class MusicBrainzArtists(CachedHttp http)
{
    private const string Base = "https://musicbrainz.org/ws/2/";

    // Links worth showing, with friendly names (MusicBrainz relationship type → label).
    private static readonly Dictionary<string, string> LinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["official homepage"] = "Website",
        ["bandcamp"] = "Bandcamp",
        ["discogs"] = "Discogs",
        ["allmusic"] = "AllMusic",
        ["last.fm"] = "Last.fm",
        ["soundcloud"] = "SoundCloud",
        ["youtube"] = "YouTube",
        ["wikipedia"] = "Wikipedia",
        ["setlistfm"] = "setlist.fm",
        ["songkick"] = "Songkick",
    };

    public async Task<List<ArtistCandidate>> SearchAsync(string name, CancellationToken ct = default)
    {
        string q = $"artist:\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        string url = $"{Base}artist/?query={Uri.EscapeDataString(q)}&fmt=json&limit=10";
        var res = await http.GetAsync(MusicBrainz.Provider, url, TimeSpan.FromDays(7), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return [];
        using var doc = JsonDocument.Parse(res.Body);
        return doc.RootElement.Arr("artists").Select(a => new ArtistCandidate(
            a.Str("id")!, a.Str("name") ?? "", a.Str("disambiguation"), a.Str("type"), a.Str("country"),
            a.Obj("life-span").Str("begin"), a.Obj("life-span").Str("end"), (int)(a.Long("score") ?? 0))).ToList();
    }

    public async Task<ArtistProfileData?> GetAsync(string mbid, CancellationToken ct = default)
    {
        string url = $"{Base}artist/{Uri.EscapeDataString(mbid)}?inc=url-rels+artist-rels+genres&fmt=json";
        var res = await http.GetAsync(MusicBrainz.Provider, url, TimeSpan.FromDays(30), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return null;
        using var doc = JsonDocument.Parse(res.Body);
        return Parse(doc.RootElement);
    }

    internal static ArtistProfileData Parse(JsonElement a)
    {
        var life = a.Obj("life-span");
        var rels = a.Arr("relations").ToList();
        string? wikidata = null;
        var links = new List<ArtistLink>();
        foreach (var r in rels.Where(r => r.Str("target-type") == "url"))
        {
            string? type = r.Str("type"), url = r.Obj("url").Str("resource");
            if (type is null || url is null || r.Bool("ended")) continue;
            if (type == "wikidata") { wikidata = url.TrimEnd('/').Split('/')[^1]; continue; }
            if (LinkTypes.TryGetValue(type, out var label) && links.All(l => l.Label != label)) links.Add(new ArtistLink(label, url));
        }

        ArtistMember Member(JsonElement r) => new(
            r.Obj("artist").Str("name") ?? "?", r.Obj("artist").Str("id"), r.Str("begin"), r.Str("end"), r.Bool("ended"),
            r.Arr("attributes").Select(x => x.GetString()).Where(x => x is { Length: > 0 }).ToList() is { Count: > 0 } roles
                ? string.Join(", ", roles) : null);
        var bandRels = rels.Where(r => r.Str("target-type") == "artist" && r.Str("type") == "member of band").ToList();
        // "backward": the other artist is a member of this one; "forward": this artist is a member of the other.
        var members = Merge(bandRels.Where(r => r.Str("direction") == "backward").Select(Member));
        var memberOf = Merge(bandRels.Where(r => r.Str("direction") == "forward").Select(Member));

        var genres = a.Arr("genres").OrderByDescending(g => g.Long("count") ?? 0).Select(g => g.Str("name"))
            .Where(g => g is not null).Select(g => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(g!)).ToList();

        return new ArtistProfileData(
            a.Str("id")!, a.Str("name") ?? "", a.Str("sort-name"), a.Str("type"), a.Str("country"),
            a.Obj("area").Str("name") ?? a.Obj("begin-area").Str("name"),
            life.Str("begin"), life.Str("end"), life.Bool("ended"), a.Str("disambiguation"),
            genres, links, members, memberOf, wikidata);
    }

    /// <summary>One line per person: several stints become one span, roles are combined; current members first.</summary>
    private static List<ArtistMember> Merge(IEnumerable<ArtistMember> list) => list
        .GroupBy(m => m.Mbid ?? m.Name)
        .Select(g =>
        {
            var all = g.ToList();
            bool current = all.Any(m => !m.Ended && m.End is null);
            var roles = all.SelectMany(m => (m.Roles ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();
            return new ArtistMember(all[0].Name, all[0].Mbid,
                all.Select(m => m.Begin).Where(b => b is not null).Order(StringComparer.Ordinal).FirstOrDefault(),
                current ? null : all.Select(m => m.End).Where(e => e is not null).Order(StringComparer.Ordinal).LastOrDefault(),
                !current, roles.Count > 0 ? string.Join(", ", roles) : null);
        })
        .OrderBy(m => m.Ended).ThenBy(m => m.Begin ?? "9999", StringComparer.Ordinal).ToList();
}

/// <summary>Wikidata (photo, sitelinks, description), Wikipedia (summary) and Commons (image files).</summary>
public sealed class Wikimedia(CachedHttp http)
{
    public const string Provider = "wikimedia";

    public sealed record Entity(string? Image, string? Description, IReadOnlyDictionary<string, string> Sitelinks);

    public async Task<Entity?> EntityAsync(string qid, string lang, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(qid, "^Q[0-9]+$")) return null;
        string url = $"https://www.wikidata.org/wiki/Special:EntityData/{qid}.json";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(30), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return null;
        using var doc = JsonDocument.Parse(res.Body);
        var e = doc.RootElement.Obj("entities").EnumerateObject().Select(p => p.Value).FirstOrDefault();
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? image = e.Obj("claims").Arr("P18").Select(c => c.Obj("mainsnak").Obj("datavalue").Str("value")).FirstOrDefault(v => v is not null);
        var desc = e.Obj("descriptions");
        string? description = desc.Obj(lang).Str("value") ?? desc.Obj("en").Str("value");
        var sitelinks = new Dictionary<string, string>();
        if (e.Obj("sitelinks") is { ValueKind: JsonValueKind.Object } sl)
            foreach (var p in sl.EnumerateObject())
                if (p.Value.Str("title") is { } t) sitelinks[p.Name] = t;
        return new Entity(image, description, sitelinks);
    }

    /// <summary>Article summary (plain text extract), or null for missing and disambiguation pages.</summary>
    public async Task<(string Extract, string Url)?> SummaryAsync(string lang, string title, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z-]{2,12}$")) return null;
        string url = $"https://{lang}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(30), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return null;
        using var doc = JsonDocument.Parse(res.Body);
        var r = doc.RootElement;
        if (r.Str("type") == "disambiguation" || r.Str("extract") is not { } extract) return null;
        return (extract, r.Obj("content_urls").Obj("desktop").Str("page") ?? $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(title)}");
    }

    /// <summary>A Commons file scaled to a sensible size (the image itself is stored in the image store, not the HTTP cache).</summary>
    public async Task<ImageBytes?> FileAsync(string fileName, CancellationToken ct = default)
    {
        string url = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(fileName.Replace(' ', '_'))}?width=800";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(30), cache: false, ct: ct, accept: "image/*").ConfigureAwait(false);
        if (!res.Ok || res.Body.Length < 100)
        {
            Log.Info("Artist", $"No photo from Commons for '{fileName}' (HTTP {res.Status})");
            return null;
        }
        string mime = res.ContentType?.Split(';')[0].Trim() ?? "image/jpeg";
        return mime.StartsWith("image/", StringComparison.Ordinal) && mime != "image/svg+xml" ? new ImageBytes(res.Body, mime, Provider) : null;
    }
}
