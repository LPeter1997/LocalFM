using System.Text.Json;
using LocalFM.Library;

namespace LocalFM.Metadata;

public sealed record ImageBytes(byte[] Data, string Mime, string Source);

/// <summary>Cover Art Archive (by MusicBrainz release / release group id).</summary>
public sealed class CoverArtArchive(CachedHttp http)
{
    public const string Provider = "coverartarchive";

    public async Task<ImageBytes?> FrontAsync(string? releaseMbid, string? releaseGroupMbid, CancellationToken ct = default)
    {
        if (releaseMbid is not null && await Get($"https://coverartarchive.org/release/{releaseMbid}/front-1200", ct).ConfigureAwait(false) is { } a) return a;
        if (releaseGroupMbid is not null && await Get($"https://coverartarchive.org/release-group/{releaseGroupMbid}/front-1200", ct).ConfigureAwait(false) is { } b) return b;
        return null;
    }

    private async Task<ImageBytes?> Get(string url, CancellationToken ct)
    {
        var res = await http.GetAsync(Provider, url, TimeSpan.Zero, cache: false, ct: ct).ConfigureAwait(false);
        return res.Ok && res.Body.Length > 0 ? new ImageBytes(res.Body, res.ContentType ?? "image/jpeg", Provider) : null;
    }
}

/// <summary>iTunes Search API: a fallback for album artwork (and release dates) when MusicBrainz has none.</summary>
public sealed class Itunes(CachedHttp http)
{
    public const string Provider = "itunes";

    public sealed record Album(long Id, string Title, string Artist, string? ReleaseDate, string? Genre, int TrackCount, string? ArtworkUrl);

    public async Task<List<Album>> SearchAlbumsAsync(string album, string? artist, CancellationToken ct = default)
    {
        string term = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? album : $"{artist} {album}");
        var res = await http.GetAsync(Provider, $"https://itunes.apple.com/search?term={term}&entity=album&limit=15", TimeSpan.FromDays(7), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return [];
        using var doc = JsonDocument.Parse(res.Body);
        return doc.RootElement.Arr("results").Select(r => new Album(
            r.Long("collectionId") ?? 0,
            r.Str("collectionName") ?? "",
            r.Str("artistName") ?? "",
            r.Str("releaseDate"),
            r.Str("primaryGenreName"),
            (int)(r.Long("trackCount") ?? 0),
            r.Str("artworkUrl100")?.Replace("100x100bb", "1200x1200bb"))).ToList();
    }

    /// <summary>Artwork of the best-matching album (same normalized title and artist), if any.</summary>
    public async Task<ImageBytes?> FrontAsync(string album, string? artist, CancellationToken ct = default)
    {
        var hits = await SearchAlbumsAsync(album, artist, ct).ConfigureAwait(false);
        string ka = Normalize.Key(album), kr = Normalize.Key(artist);
        var best = hits.FirstOrDefault(h => Normalize.Key(h.Title) == ka && (kr.Length == 0 || Normalize.Key(h.Artist) == kr))
            ?? hits.FirstOrDefault(h => Normalize.Key(h.Title).StartsWith(ka, StringComparison.Ordinal) && (kr.Length == 0 || Normalize.Key(h.Artist) == kr));
        if (best?.ArtworkUrl is null) return null;
        var res = await http.GetAsync(Provider, best.ArtworkUrl, TimeSpan.Zero, cache: false, ct: ct).ConfigureAwait(false);
        return res.Ok && res.Body.Length > 0 ? new ImageBytes(res.Body, res.ContentType ?? "image/jpeg", Provider) : null;
    }
}
