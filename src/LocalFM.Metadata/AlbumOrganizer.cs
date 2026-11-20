using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Edit;

namespace LocalFM.Metadata;

/// <summary>What a sort did: the albums the songs now live on, and anything that went wrong.</summary>
public sealed record SortReport(int Moved, IReadOnlyList<Ulid> Albums, IReadOnlyList<string> Problems)
{
    public static readonly SortReport Empty = new(0, [], []);
}

/// <summary>
/// Puts songs on the albums they belong to. A song goes to the local album that already is that album (same
/// release, or another edition of it); otherwise a new album is made and the release applied to it. Moves are
/// unlocked ("musicbrainz" source), so a later, better match may still move them. Nothing touches playback.
/// </summary>
public sealed class AlbumOrganizer(MetadataService svc, Database db)
{
    private const string Source = "musicbrainz";

    public async Task<SortReport> SortAsync(IReadOnlyList<AlbumGuess> placements, bool fetchCover = true, CancellationToken ct = default)
    {
        int moved = 0;
        var albums = new List<Ulid>();
        var problems = new List<string>();
        foreach (var g in placements.Where(p => p.Songs.Count > 0))
        {
            ct.ThrowIfCancellationRequested();
            var songs = g.Songs;
            if (LocalAlbumFor(g) is { } local)
            {
                moved += await db.Writer.Run((c, t) => MetadataEditor.MoveTracks(new Tx(c, t), songs, local, Source, lockIt: false)).ConfigureAwait(false);
                var r = await svc.ReapplyMatchAsync(local, ct).ConfigureAwait(false);
                // The songs are on another edition of this album (bonus tracks): use that edition if it fits more songs.
                if (r is { Applied: true } && r.UnmatchedTrackIds.Any(songs.Contains))
                {
                    var better = await svc.SwitchEditionIfBetterAsync(local, g.ReleaseMbid, r.TracksMatched, ct).ConfigureAwait(false);
                    if ((better?.UnmatchedTrackIds ?? r.UnmatchedTrackIds).Any(songs.Contains))
                        problems.Add($"MusicBrainz lists {(songs.Count == 1 ? "this song" : "these songs")} on “{g.Title}”, but not on the edition in your library.");
                }
                albums.Add(local);
                continue;
            }

            // The songs are all that an unmatched album has: that album simply is this one, so match it in place
            // (keeps its cover, edits and history). Otherwise start a new album for them.
            Ulid target = SoleUnmatchedAlbum(songs) ?? await db.Writer.Run((c, t) =>
            {
                var tx = new Tx(c, t);
                var id = MetadataEditor.NewAlbum(tx, g.Title, g.Artist);
                moved += MetadataEditor.MoveTracks(tx, songs, id, Source, lockIt: false);
                return id;
            }).ConfigureAwait(false);

            var result = await svc.ApplyReleaseAsync(target, g.ReleaseMbid, fetchCover, ct).ConfigureAwait(false);
            if (result is null) problems.Add($"MusicBrainz did not return “{g.Title}”; the songs are on a new album with that name.");
            else if (!result.Applied) problems.Add($"The songs did not line up with “{g.Title}”; they are on a new album with that name.");
            albums.Add(result?.MergedInto ?? target);
        }
        return new SortReport(moved, albums.Distinct().ToList(), problems);
    }

    /// <summary>Carries out a Tidy up suggestion.</summary>
    public async Task<SortReport> ApplyAsync(AlbumSuggestion s, CancellationToken ct = default)
    {
        switch (s.Kind)
        {
            case AlbumIssue.SameAlbumTwice when s.MergeFrom is { } from && s.MergeInto is { } into:
                int n = await db.Writer.Run((c, t) => MetadataEditor.MergeRelease(new Tx(c, t), from, into, MetadataEditor.CleanupSource, lockIt: false))
                    .ConfigureAwait(false);
                await svc.ReapplyMatchAsync(into, ct).ConfigureAwait(false);
                return new SortReport(n, [into], []);
            case AlbumIssue.NotOnAlbum:
                var songs = svc.Albums.Describe(s.Tracks);
                var placements = AlbumFinder.Assign(await svc.Albums.FindAsync(songs, ct: ct).ConfigureAwait(false));
                if (placements.Count == 0) return new SortReport(0, [], ["MusicBrainz knows no album for these songs."]);
                var report = await SortAsync(placements, ct: ct).ConfigureAwait(false);
                int missing = songs.Count - placements.Sum(p => p.Songs.Count);
                return missing > 0 ? report with { Problems = [.. report.Problems, $"No album found for {missing} of the songs; they stay where they are."] } : report;
            default:
                return s.Target is { } target ? await SortAsync([target with { Songs = s.Tracks }], ct: ct).ConfigureAwait(false) : SortReport.Empty;
        }
    }

    /// <summary>The local album that is this album: the same release, else another edition of the same album.</summary>
    private Ulid? LocalAlbumFor(AlbumGuess g)
    {
        using var c = db.Open();
        return c.QuerySingle("SELECT entity_id FROM external_id WHERE provider = 'musicbrainz' AND entity_type = 'release' AND external_id = $1",
                   r => r.IdN(0), g.ReleaseMbid)
               ?? (g.GroupMbid is null ? null : c.QuerySingle("""
                   SELECT r.id FROM external_id e JOIN release r ON r.release_group_id = e.entity_id
                   WHERE e.provider = 'musicbrainz' AND e.entity_type = 'release_group' AND e.external_id = $1
                   ORDER BY r.created_at LIMIT 1
                   """, r => r.IdN(0), g.GroupMbid));
    }

    /// <summary>An unmatched album holding exactly these songs and nothing else.</summary>
    private Ulid? SoleUnmatchedAlbum(IReadOnlyList<Ulid> songs)
    {
        using var c = db.Open();
        var releases = songs.Select(s => c.QuerySingle("SELECT m.release_id FROM track t JOIN medium m ON m.id = t.medium_id WHERE t.id = $1",
            r => r.IdN(0), s)).Distinct().ToList();
        if (releases is not [{ } release]) return null;
        long total = c.Scalar<long>("SELECT COUNT(*) FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1", release);
        long matched = c.Scalar<long>("SELECT COUNT(*) FROM external_id WHERE entity_type = 'release' AND entity_id = $1 AND provider = 'musicbrainz'", release);
        return total == songs.Count && matched == 0 ? release : null;
    }
}
