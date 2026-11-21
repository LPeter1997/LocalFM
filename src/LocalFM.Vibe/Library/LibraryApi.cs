using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Metadata;
using LocalFM.Vibe.Protocol;

namespace LocalFM.Vibe.Library;

/// <summary>
/// Answers <see cref="LibraryRequest"/>s from the knowledge base, whatever the transport (vibe TLS wire, web
/// socket). Read-only, runs on the thread pool, never touches playback. Replies carry ids, never paths.
/// </summary>
public sealed class LibraryApi(
    Database db, ImageStore images, LibraryQueries queries, Playlists playlists, MediaResolver media,
    Func<TrackInfo, CancellationToken, Task<LyricsView?>> lyrics)
{
    public const int DefaultTrackLimit = 5000;
    public const int SearchLimit = 50;

    public MediaResolver Media => media;

    /// <summary>Never throws (except on cancellation): failures come back as <see cref="LibraryReply.Error"/>.</summary>
    public async Task<LibraryReply> HandleAsync(LibraryRequest q, CancellationToken ct = default)
    {
        try
        {
            if (q.Op == LibraryOp.Lyrics) return await LyricsAsync(q, ct).ConfigureAwait(false);
            return await Task.Run(() => Answer(q), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Log.Warn("Library", $"Remote '{q.Op}' failed: {e.Message}");
            return new LibraryReply(q.Request) { Error = "Something went wrong on the host." };
        }
    }

    private LibraryReply Answer(LibraryRequest q)
    {
        var r = new LibraryReply(q.Request);
        string? search = string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim();
        switch (q.Op)
        {
            case LibraryOp.Albums:
            {
                Ulid? artist = q.Id is null ? null : Id(q.Id);
                return r with { Albums = queries.Albums(search, artist).Select(Map).ToList() };
            }
            case LibraryOp.Album:
            {
                var id = Id(q.Id);
                if (queries.Album(id) is not { } a) return NotFound(r);
                return r with { Album = Map(a), Tracks = queries.AlbumTracks(id).Select(Map).ToList() };
            }
            case LibraryOp.Artists:
                return r with { Artists = queries.Artists(search).Select(Map).ToList() };
            case LibraryOp.Artist:
            {
                var id = Id(q.Id);
                if (ArtistProfiles.Load(db, images, id) is not { } p) return NotFound(r);
                return r with { Artist = Map(p), Albums = queries.Albums(artistId: id).Select(Map).ToList() };
            }
            case LibraryOp.Tracks:
            {
                int limit = Math.Clamp(q.Limit ?? DefaultTrackLimit, 1, DefaultTrackLimit);
                return r with { Tracks = queries.Tracks(search, limit).Select(Map).ToList() };
            }
            case LibraryOp.Playlists:
                return r with { Playlists = playlists.List().Select(Map).ToList() };
            case LibraryOp.Playlist:
            {
                var id = Id(q.Id);
                if (playlists.List().FirstOrDefault(p => p.Id == id) is not { } p) return NotFound(r);
                return r with { Playlist = Map(p), Tracks = queries.ByRecordings(playlists.Entries(id)).Select(Map).ToList() };
            }
            case LibraryOp.Counts:
            {
                var (tracks, albums, artists) = queries.Counts();
                return r with { Counts = new RemoteCounts(tracks, albums, artists) };
            }
            case LibraryOp.Search:
                if (search is null) return r with { Albums = [], Artists = [], Tracks = [] };
                return r with
                {
                    Albums = queries.Albums(search).Take(SearchLimit).Select(Map).ToList(),
                    Artists = queries.Artists(search).Take(SearchLimit).Select(Map).ToList(),
                    Tracks = queries.Tracks(search, SearchLimit).Select(Map).ToList(),
                };
            default:
                return r with { Error = $"Unknown request '{q.Op}'." };
        }
    }

    private async Task<LibraryReply> LyricsAsync(LibraryRequest q, CancellationToken ct)
    {
        var r = new LibraryReply(q.Request);
        var id = Id(q.Id);
        var track = await Task.Run(() => queries.ByTrack(id), ct).ConfigureAwait(false);
        if (track is null) return NotFound(r);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        LyricsView? found;
        try { found = await lyrics(track, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { found = null; }
        if (found is null) return r;
        var lines = found.Lines.Select(l => new RemoteLyricLine(l.Time is { } t ? (long)t.TotalMilliseconds : null, l.Text)).ToList();
        return r with { Lyrics = new RemoteLyrics(found.Data.Provider, MetadataService.FormatName(found.Data.Format), lines) };
    }

    private static LibraryReply NotFound(LibraryReply r) => r with { Error = "Not found." };

    private static Ulid Id(string? s) =>
        Ulid.TryParse(s ?? "", out var id) ? id : throw new ArgumentException($"Bad id '{s}'.");

    private RemoteAlbum Map(AlbumSummary a) => new(
        a.ReleaseId.ToString(), a.Title, a.Artist, a.Date, a.Edition, media.Remember(MediaResolver.CoverId(a.ReleaseId), a.CoverPath),
        a.TrackCount, a.OwnedCount);

    private RemoteTrack Map(TrackInfo t)
    {
        media.Remember(t);
        return new RemoteTrack(t.TrackId.ToString(), t.Title, t.Artist, t.Album, t.ReleaseId.ToString(), t.Disc, t.Position,
            t.DurationMs, media.Remember(MediaResolver.CoverId(t.ReleaseId), t.CoverPath), t.Codec, t.TrackGainDb, t.TrackPeak);
    }

    private RemoteArtist Map(ArtistSummary a) => new(
        a.ArtistId.ToString(), a.Name, a.AlbumCount, a.TrackCount, media.Remember(MediaResolver.PhotoId(a.ArtistId), a.PhotoPath));

    private RemoteArtistProfile Map(ArtistProfileView p) => new(
        p.ArtistId.ToString(), p.Name, p.Type, p.Country, p.Begin, p.End, p.Description, p.Bio, p.BioSource,
        media.Remember(MediaResolver.PhotoId(p.ArtistId), p.PhotoPath),
        [.. p.Genres], [.. p.Members.Select(m => m.Name)], [.. p.MemberOf.Select(m => m.Name)]);

    private static RemotePlaylist Map(PlaylistSummary p) => new(p.Id.ToString(), p.Name, p.Description, p.EntryCount);
}
