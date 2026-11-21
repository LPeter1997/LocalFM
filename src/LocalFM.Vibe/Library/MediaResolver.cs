using System.Collections.Concurrent;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;

namespace LocalFM.Vibe.Library;

/// <summary>A library file a remote peer may read.</summary>
public sealed record MediaFile(string Path, string? Codec, long? DurationMs);

/// <summary>
/// Turns the ids remote peers see into files: a track id to its best present file, an image id
/// (<c>r</c> + release id: the album cover, <c>a</c> + artist id: the artist photo) to the picture. Only the
/// library's own audio and pictures resolve, never an arbitrary path. Lookups are cached, since a player reads a
/// track in many ranges; a cached path that has gone away is looked up again. Safe to call from any thread.
/// </summary>
public sealed class MediaResolver(Database db, ImageStore images, LibraryQueries queries)
{
    private const int MaxCached = 4096;
    private readonly ConcurrentDictionary<string, MediaFile?> _tracks = new();
    private readonly ConcurrentDictionary<string, string?> _images = new();

    public static string CoverId(Ulid releaseId) => "r" + releaseId;
    public static string PhotoId(Ulid artistId) => "a" + artistId;

    public MediaFile? Track(string trackId)
    {
        if (_tracks.TryGetValue(trackId, out var hit) && hit is not null && File.Exists(hit.Path)) return hit;
        MediaFile? found = null;
        if (Ulid.TryParse(trackId, out var id) && queries.ByTrack(id) is { } t) found = new MediaFile(t.Path, t.Codec, t.DurationMs);
        Put(_tracks, trackId, found);
        return found;
    }

    public string? Image(string imageId)
    {
        if (_images.TryGetValue(imageId, out var hit) && (hit is null || File.Exists(hit))) return hit;
        string? found = null;
        if (imageId.Length > 1 && Ulid.TryParse(imageId.AsSpan(1), out var id))
            found = imageId[0] switch
            {
                'r' => queries.Album(id)?.CoverPath,
                'a' => ArtistProfiles.Load(db, images, id)?.PhotoPath,
                _ => null,
            };
        Put(_images, imageId, found);
        return found;
    }

    /// <summary>Remembers a picture the library API just handed out, so its request needs no query.</summary>
    internal string? Remember(string imageId, string? path)
    {
        if (path is null) return null;
        Put(_images, imageId, path);
        return imageId;
    }

    internal void Remember(TrackInfo t) => Put(_tracks, t.TrackId.ToString(), new MediaFile(t.Path, t.Codec, t.DurationMs));

    private static void Put<T>(ConcurrentDictionary<string, T> cache, string key, T value)
    {
        if (cache.Count >= MaxCached) cache.Clear();
        cache[key] = value;
    }
}
