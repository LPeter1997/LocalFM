using System.Text;
using System.Text.Json;
using LocalFM.Library.Enrich;

namespace LocalFM.Metadata;

/// <summary>MusicBrainz web service v2 (JSON). 1 request/second; responses are cached.</summary>
public sealed class MusicBrainz(CachedHttp http)
{
    public const string Provider = "musicbrainz";
    private const string Base = "https://musicbrainz.org/ws/2/";

    public async Task<List<ReleaseCandidate>> SearchReleasesAsync(string album, string? artist, CancellationToken ct = default)
    {
        var q = new StringBuilder($"release:\"{Escape(album)}\"");
        if (!string.IsNullOrWhiteSpace(artist)) q.Append($" AND artist:\"{Escape(artist)}\"");
        string url = $"{Base}release/?query={Uri.EscapeDataString(q.ToString())}&fmt=json&limit=25";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(7), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return [];
        using var doc = JsonDocument.Parse(res.Body);
        return doc.RootElement.Arr("releases").Select(ParseCandidate).ToList();
    }

    public async Task<ReleaseData?> GetReleaseAsync(string mbid, CancellationToken ct = default)
    {
        string url = $"{Base}release/{Uri.EscapeDataString(mbid)}?inc=recordings+artist-credits+labels+release-groups+genres+isrcs&fmt=json";
        var res = await http.GetAsync(Provider, url, TimeSpan.FromDays(30), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return null;
        using var doc = JsonDocument.Parse(res.Body);
        return ParseRelease(doc.RootElement);
    }

    private static ReleaseCandidate ParseCandidate(JsonElement r)
    {
        var label = r.Arr("label-info").FirstOrDefault();
        var rg = r.Obj("release-group");
        return new ReleaseCandidate(
            Provider,
            r.Str("id")!,
            r.Str("title") ?? "",
            ReleaseData.CreditText(Credit(r)),
            r.Str("date"),
            r.Str("country"),
            label.Obj("label").Str("name"),
            label.Str("catalog-number"),
            r.Str("barcode"),
            FormatText(r.Arr("media").Select(m => m.Str("format") ?? "Unknown")),
            (int)(r.Long("track-count") ?? r.Arr("media").Sum(m => m.Long("track-count") ?? 0)),
            r.Str("status"),
            r.Str("disambiguation"),
            rg.Str("id"),
            rg.Str("primary-type"),
            r.Long("score") ?? 0);
    }

    private static ReleaseData ParseRelease(JsonElement r)
    {
        var label = r.Arr("label-info").FirstOrDefault();
        var rg = r.Obj("release-group");
        var media = r.Arr("media").Select(m => new MediumData(
            (int)(m.Long("position") ?? 1),
            m.Str("format"),
            m.Str("title"),
            m.Arr("tracks").Select(t =>
            {
                var rec = t.Obj("recording");
                var trackCredit = Credit(t);
                return new TrackData(
                    (int)(t.Long("position") ?? 0),
                    t.Str("number"),
                    t.Str("title") ?? rec.Str("title") ?? "",
                    t.Long("length") ?? rec.Long("length"),
                    t.Str("id"),
                    trackCredit,
                    new RecordingData(
                        rec.Str("id"),
                        rec.Str("title") ?? t.Str("title") ?? "",
                        rec.Long("length"),
                        rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("artist-credit", out _) ? Credit(rec) : trackCredit,
                        rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty("isrcs", out var isrcs)
                            ? isrcs.EnumerateArray().Select(i => i.GetString()!).Where(i => i is not null).ToList() : [],
                        Genres(rec),
                        rec.Str("first-release-date")));
            }).ToList())).ToList();

        return new ReleaseData(
            Provider,
            r.Str("id")!,
            r.Str("title") ?? "",
            Credit(r),
            r.Str("date"),
            r.Str("country"),
            label.Obj("label").Str("name"),
            label.Str("catalog-number"),
            r.Str("barcode"),
            r.Str("status"),
            r.Str("disambiguation"),
            new ReleaseGroupData(
                rg.Str("id"),
                rg.Str("title") ?? r.Str("title") ?? "",
                rg.Str("primary-type"),
                rg.ValueKind == JsonValueKind.Object && rg.TryGetProperty("secondary-types", out var st)
                    ? st.EnumerateArray().Select(s => s.GetString()!).Where(s => s is not null).ToList() : [],
                rg.Str("first-release-date"),
                Credit(rg),
                Genres(rg)),
            media,
            Genres(r));
    }

    private static List<CreditPart> Credit(JsonElement e) =>
        e.Arr("artist-credit").Select(c =>
        {
            var a = c.Obj("artist");
            string name = a.Str("name") ?? c.Str("name") ?? "";
            return new CreditPart(
                new ArtistRef(name, a.Str("id"), a.Str("sort-name"), a.Str("type"), a.Str("country"), a.Str("disambiguation")),
                c.Str("name") ?? name,
                c.ValueKind == JsonValueKind.Object && c.TryGetProperty("joinphrase", out var j) ? j.GetString() ?? "" : "");
        }).ToList();

    /// <summary>Community genres, most-voted first.</summary>
    private static List<string> Genres(JsonElement e) =>
        e.Arr("genres").OrderByDescending(g => g.Long("count") ?? 0).Select(g => g.Str("name")).OfType<string>().ToList();

    /// <summary>"CD", "2×CD", "CD + DVD-Video".</summary>
    public static string? FormatText(IEnumerable<string> formats)
    {
        var groups = formats.GroupBy(f => f).Select(g => g.Count() > 1 ? $"{g.Count()}×{g.Key}" : g.Key).ToList();
        return groups.Count == 0 ? null : string.Join(" + ", groups);
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
