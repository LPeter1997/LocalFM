using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Edit;

namespace LocalFM.Metadata;

/// <summary>
/// Tidy up's online album check. Looks up the songs MusicBrainz has not confirmed yet (no track id: lone songs,
/// unmatched albums, songs left over after a match), artist by artist, and reports where they belong:
/// songs of one album spread over several (gather them), songs on an album they are not on (move them), and
/// lone songs whose album was found. Answers are cached, so a second run costs no requests.
/// </summary>
public sealed class AlbumScout(MetadataService svc, Database db, History kv)
{
    public sealed record Progress(int Done, int Total);

    private sealed record Candidate(Ulid Track, Ulid Release, string ArtistKey);

    private sealed record LocalAlbum(Ulid Id, string Title, string? Mbid, string? GroupMbid);

    /// <summary>Runs the check, reporting suggestions as each artist is done. Dismissed suggestions are skipped.</summary>
    public async Task RunAsync(IProgress<AlbumSuggestion> found, IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        if (!svc.OnlineEnabled) return;
        var candidates = Candidates();
        int total = candidates.Count, done = 0;
        progress?.Report(new Progress(0, total));
        foreach (var artist in candidates.GroupBy(c => c.ArtistKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var songs = svc.Albums.Describe(artist.Select(c => c.Track));
            int before = done;
            var guesses = await svc.Albums.FindAsync(songs, new Progress<int>(n => progress?.Report(new Progress(before + n, total))), ct)
                .ConfigureAwait(false);
            done += songs.Count;
            progress?.Report(new Progress(done, total));
            foreach (var s in Suggest(AlbumFinder.Assign(guesses), guesses))
                if (!AlbumChecks.IsDismissed(kv, s)) found.Report(s);
        }
    }

    /// <summary>
    /// Matches matched albums again where some songs are not confirmed: songs a scan added to an album after it
    /// was matched (same album tags) were never compared with the release. Usually answered from the cache.
    /// Returns how many songs are now confirmed.
    /// </summary>
    public async Task<int> SettleAsync(CancellationToken ct = default)
    {
        if (!svc.OnlineEnabled) return 0;
        List<Ulid> albums;
        using (var c = db.Open())
            albums = c.Query("""
                SELECT DISTINCT m.release_id FROM track t JOIN medium m ON m.id = t.medium_id
                JOIN external_id e ON e.entity_type = 'release' AND e.entity_id = m.release_id AND e.provider = 'musicbrainz'
                WHERE NOT EXISTS (SELECT 1 FROM external_id x WHERE x.entity_type = 'track' AND x.entity_id = t.id AND x.provider = 'musicbrainz')
                  AND EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0)
                """, r => r.Id(0));
        int confirmed = 0;
        foreach (var album in albums)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await svc.ReapplyMatchAsync(album, ct).ConfigureAwait(false) is { Applied: true } r)
                    confirmed += r.TracksMatched;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("Tidy", $"Matching album {album} again failed: {ex.Message}");
            }
        }
        return confirmed;
    }

    /// <summary>Songs not confirmed by MusicBrainz, whose album was not chosen by hand.</summary>
    private List<Candidate> Candidates()
    {
        using var c = db.Open();
        return c.Query("""
            SELECT t.id, m.release_id, COALESCE(tac.display_name, rac.display_name, '')
            FROM track t JOIN medium m ON m.id = t.medium_id JOIN release r ON r.id = m.release_id
            LEFT JOIN artist_credit tac ON tac.id = t.artist_credit_id LEFT JOIN artist_credit rac ON rac.id = r.artist_credit_id
            WHERE NOT EXISTS (SELECT 1 FROM external_id x WHERE x.entity_type = 'track' AND x.entity_id = t.id AND x.provider = 'musicbrainz')
              AND NOT EXISTS (SELECT 1 FROM field_provenance p WHERE p.entity_type = 'track' AND p.entity_id = t.id AND p.field = 'medium_id' AND p.locked = 1)
              AND EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0)
            """, r => new Candidate(r.Id(0), r.Id(1), Normalize.Key(Cleanup.CleanArtist(r.GetString(2))?.Text ?? r.GetString(2))));
    }

    /// <summary>Turns placements into suggestions about where the songs are now.</summary>
    /// <param name="all">Every album the songs are on: a song is never moved off an album it is on.</param>
    internal List<AlbumSuggestion> Suggest(IReadOnlyList<AlbumGuess> placements, IReadOnlyList<AlbumGuess>? all = null)
    {
        var groupsOf = (all ?? placements).SelectMany(g => g.Songs.Select(s => (Song: s, g.GroupMbid, g.LocalReleaseId)))
            .ToLookup(x => x.Song);
        // The song's album is (an edition of) one MusicBrainz lists it on: it is where it belongs.
        bool AlreadyHome(Ulid song, LocalAlbum album) => groupsOf[song].Any(x =>
            x.LocalReleaseId == album.Id || (album.GroupMbid is not null && x.GroupMbid == album.GroupMbid));
        var list = new List<AlbumSuggestion>();
        using var c = db.Open();
        LocalAlbum? Album(Ulid trackId) => c.QuerySingle("""
            SELECT r.id, r.title,
                   (SELECT external_id FROM external_id e WHERE e.entity_type = 'release' AND e.entity_id = r.id AND e.provider = 'musicbrainz'),
                   (SELECT external_id FROM external_id e WHERE e.entity_type = 'release_group' AND e.entity_id = r.release_group_id AND e.provider = 'musicbrainz')
            FROM track t JOIN medium m ON m.id = t.medium_id JOIN release r ON r.id = m.release_id WHERE t.id = $1
            """, r => new LocalAlbum(r.Id(0), r.GetString(1), r.Str(2), r.Str(3)), trackId);
        string Title(Ulid trackId) => c.Scalar<string>("SELECT title FROM track WHERE id = $1", trackId) ?? "";

        foreach (var p in placements)
        {
            var where = p.Songs.Select(s => (Song: s, Album: Album(s))).Where(x => x.Album is not null && !AlreadyHome(x.Song, x.Album)).ToList();
            if (where.Count == 0) continue;
            // Already where they belong.
            if (where.All(x => x.Album!.Id == p.LocalReleaseId || (x.Album.GroupMbid is not null && x.Album.GroupMbid == p.GroupMbid))) continue;
            var songs = where.Where(x => x.Album!.Id != p.LocalReleaseId).Select(x => x.Song).ToList();
            var from = where.Where(x => songs.Contains(x.Song)).Select(x => x.Album!).DistinctBy(a => a.Id).ToList();
            string album = $"“{p.Title}”{(p.Year is { } y ? $" ({y})" : "")}";
            string names = AlbumChecks.Join(songs.Select(Title).ToList());

            if (p.Songs.Count >= 2 && (from.Count >= 2 || p.LocalReleaseId is not null))
            {
                list.Add(new AlbumSuggestion(AlbumIssue.BelongTogether,
                    $"{AlbumChecks.Count(p.Songs.Count, "song")} are from {album} by {p.Artist}",
                    $"{names}, now on {AlbumChecks.Join(from.Select(a => a.Title).ToList(), 2)}", songs) { Target = p });
            }
            else if (from.Count == 1 && from[0].Mbid is not null)
            {
                list.Add(new AlbumSuggestion(AlbumIssue.BelongsElsewhere,
                    $"{(songs.Count == 1 ? names : AlbumChecks.Count(songs.Count, "song"))} {(songs.Count == 1 ? "is" : "are")} on {album}, not “{from[0].Title}”",
                    songs.Count == 1 ? $"by {p.Artist}" : names, songs) { Target = p });
            }
            else if (!p.Secondary)
            {
                list.Add(new AlbumSuggestion(AlbumIssue.AlbumFound,
                    $"{(songs.Count == 1 ? names : AlbumChecks.Count(songs.Count, "song"))} {(songs.Count == 1 ? "is" : "are")} from {album} by {p.Artist}",
                    songs.Count == 1 ? $"now on “{from.FirstOrDefault()?.Title}”" : names, songs) { Target = p });
            }
        }
        return list;
    }
}
