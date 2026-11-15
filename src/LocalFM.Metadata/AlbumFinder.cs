using System.Text.Json;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Edit;

namespace LocalFM.Metadata;

/// <summary>One release a recording appears on, as a recording search lists it.</summary>
public sealed record RecordingRelease(
    string Id, string Title, string Artist, string? Date, string? Status, string? GroupId,
    string? PrimaryType, IReadOnlyList<string> SecondaryTypes, int TrackCount, int? TrackNumber);

public sealed record RecordingHit(string Id, string Title, string Artist, long? LengthMs, int Score, IReadOnlyList<RecordingRelease> Releases);

/// <summary>A local song to look up.</summary>
public sealed record SongQuery(Ulid TrackId, string Title, string? Artist, long? LengthMs);

/// <summary>
/// "Which album is this song on?" Looks each song up on MusicBrainz (title + artist, cached, rate-limited), keeps
/// the official recordings that really are that song (title and length), and groups the releases they appear on by album
/// (release group). Then <see cref="Assign"/> gives every song one album, keeping songs of one album together and
/// preferring the original studio album over compilations and live records.
/// </summary>
public sealed class AlbumFinder(MusicBrainz mb, Database db)
{
    /// <summary>Kinds of release that are rarely where a song "belongs" (still used when nothing else has it).</summary>
    private static readonly HashSet<string> SecondaryKinds = new(StringComparer.OrdinalIgnoreCase)
        { "Compilation", "Live", "Soundtrack", "DJ-mix", "Mixtape/Street", "Remix", "Demo", "Interview", "Spokenword", "Audio drama" };

    /// <summary>Releases that are not where a song lives: unofficial ones and stand-ins.</summary>
    private static readonly HashSet<string> SkippedStatuses = new(StringComparer.OrdinalIgnoreCase) { "Bootleg", "Pseudo-Release", "Withdrawn", "Cancelled" };

    /// <summary>The local tracks as queries: their title, credited artist and the file's real length.</summary>
    public List<SongQuery> Describe(IEnumerable<Ulid> trackIds)
    {
        using var c = db.Open();
        var list = new List<SongQuery>();
        foreach (var id in trackIds.Distinct())
        {
            var row = c.QuerySingle("""
                SELECT t.title, COALESCE(tac.display_name, rac.display_name),
                       COALESCE((SELECT MAX(f.duration_ms) FROM audio_file f WHERE f.track_id = t.id), t.length_ms)
                FROM track t JOIN medium m ON m.id = t.medium_id JOIN release r ON r.id = m.release_id
                LEFT JOIN artist_credit tac ON tac.id = t.artist_credit_id LEFT JOIN artist_credit rac ON rac.id = r.artist_credit_id
                WHERE t.id = $1
                """, r => (Title: r.GetString(0), Artist: r.Str(1), Length: r.Long(2)), id);
            if (row.Title is not null) list.Add(new SongQuery(id, row.Title, row.Artist, row.Length));
        }
        return list;
    }

    public List<SongQuery> DescribeAlbum(Ulid releaseId)
    {
        using var c = db.Open();
        var ids = c.Query("SELECT t.id FROM track t JOIN medium m ON m.id = t.medium_id WHERE m.release_id = $1 ORDER BY m.position, t.position",
            r => r.Id(0), releaseId);
        return Describe(ids);
    }

    /// <summary>Every album the songs appear on, best first. <paramref name="progress"/> counts songs looked up.</summary>
    public async Task<List<AlbumGuess>> FindAsync(IReadOnlyList<SongQuery> songs, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var perSong = new List<(SongQuery Song, List<RecordingRelease> Releases)>();
        int done = 0;
        foreach (var song in songs)
        {
            ct.ThrowIfCancellationRequested();
            perSong.Add((song, await ReleasesOfAsync(song, ct).ConfigureAwait(false)));
            progress?.Report(++done);
        }
        return Group(perSong);
    }

    /// <summary>
    /// The official releases a song is on: recordings with the same title and about the same length (a video edit
    /// may be a minute shorter than the album version, so the tolerance is the one applying a release uses).
    /// Tries, in order: the cleaned title and artist; "Artist - Song" from the title (uploads credited to a
    /// channel); artist and title swapped (tags filled the wrong way round). Each first near the song's length,
    /// then at any length.
    /// </summary>
    public async Task<List<RecordingRelease>> ReleasesOfAsync(SongQuery song, CancellationToken ct = default)
    {
        string title = Cleanup.CleanTitle(song.Title, song.Artist, remasterNotes: true)?.Text ?? song.Title;
        string? artist = song.Artist is null ? null : Cleanup.CleanArtist(song.Artist)?.Text ?? song.Artist;
        var tries = new List<(string Title, string? Artist)> { (title, artist) };
        if (Cleanup.SplitArtistTitle(song.Title, song.Artist, null) is { } split) tries.Add((split.Title, split.Artist));
        if (artist is { Length: > 0 }) tries.Add((artist, title));

        var found = new List<RecordingRelease>();
        var (usedTitle, usedArtist) = tries[0];
        foreach (var (t, a) in tries.Distinct())
        {
            if (song.LengthMs is { } len)
                found = Matching(await mb.SearchRecordingsAsync(t, a, (len, Slack(len)), ct).ConfigureAwait(false), t, len);
            if (found.Count == 0)
                found = Matching(await mb.SearchRecordingsAsync(t, a, null, ct).ConfigureAwait(false), t, song.LengthMs);
            if (found.Count > 0) { (usedTitle, usedArtist) = (t, a); break; }
        }

        // Only on compilations (or nowhere): the song may have come out as a single that its recording does not list.
        if (!found.Any(r => !IsSecondary(r)) && usedArtist is { Length: > 0 })
        {
            string key = Cleanup.SongKey(usedTitle);
            found.AddRange((await mb.SearchSinglesAsync(usedTitle, usedArtist, ct).ConfigureAwait(false))
                .Where(r => !IsSecondary(r) && SameArtist(r.Artist, usedArtist))
                .Where(r => r.Title.Split(" / ").Any(side => Cleanup.SongKey(side) == key)));
        }
        return found;
    }

    /// <summary>How far a recording's length may be from the song's: the loose rule of applying a release.</summary>
    private static long Slack(long ms) => Math.Max(30_000, ms / 4);

    private static List<RecordingRelease> Matching(List<RecordingHit> hits, string title, long? lengthMs)
    {
        string key = Cleanup.SongKey(title);
        return hits
            .Where(h => Cleanup.SongKey(h.Title) == key)
            .Where(h => lengthMs is not { } l || h.LengthMs is not { } hl || Math.Abs(hl - l) <= Slack(Math.Max(hl, l)))
            .SelectMany(h => h.Releases.Select(r => SameArtist(r.Artist, h.Artist) ? r : r with { SecondaryTypes = [.. r.SecondaryTypes, "Compilation"] }))
            .Where(r => r.Status is null || !SkippedStatuses.Contains(r.Status))
            .ToList();
    }

    /// <summary>
    /// Whether a release is the song's artist's own. A various-artists record ("Old Favorite Pops" by Herman's
    /// Hermits / The Animals / …) is a compilation in all but name. "Slash" and "Slash feat. Myles Kennedy" are
    /// the same artist here.
    /// </summary>
    private static bool SameArtist(string release, string song)
    {
        string a = Normalize.Key(release), b = Normalize.Key(song);
        return a.Length == 0 || b.Length == 0 || a == b
               || a.StartsWith(b + " ", StringComparison.Ordinal) || b.StartsWith(a + " ", StringComparison.Ordinal);
    }

    /// <summary>Groups releases into albums (release groups) and picks the edition to use for each.</summary>
    public List<AlbumGuess> Group(IReadOnlyList<(SongQuery Song, List<RecordingRelease> Releases)> perSong)
    {
        var albums = new Dictionary<string, (List<RecordingRelease> Releases, HashSet<Ulid> Songs)>();
        foreach (var (song, releases) in perSong)
            foreach (var r in releases)
            {
                string key = r.GroupId ?? r.Id;
                if (!albums.TryGetValue(key, out var a)) albums[key] = a = ([], []);
                a.Releases.Add(r);
                a.Songs.Add(song.TrackId);
            }

        var (localReleases, localGroups) = LocalIds();
        var order = perSong.Select((p, i) => (p.Song.TrackId, i)).ToDictionary(x => x.TrackId, x => x.i);
        var guesses = new List<AlbumGuess>();
        foreach (var (groupKey, a) in albums)
        {
            var edition = a.Releases
                .OrderByDescending(r => localReleases.ContainsKey(r.Id))
                .ThenByDescending(r => string.Equals(r.Status, "Official", StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.Date is { Length: >= 4 } ? r.Date : "9999")
                .ThenByDescending(r => r.TrackCount)
                .First();
            Ulid? local = localReleases.TryGetValue(edition.Id, out var lr) ? lr
                : edition.GroupId is not null && localGroups.TryGetValue(edition.GroupId, out var lg) ? lg : null;
            string? year = a.Releases.Select(r => r.Date).Where(d => d is { Length: >= 4 }).Select(d => d![..4]).Order().FirstOrDefault();
            guesses.Add(new AlbumGuess(edition.Id, edition.GroupId, edition.Title, edition.Artist, year, edition.TrackCount,
                edition.PrimaryType, IsSecondary(edition), a.Songs.OrderBy(s => order[s]).ToList(), local)
                { Editions = a.Releases.Select(r => r.Id).Distinct().Count() });
        }
        return guesses
            .OrderByDescending(g => g.Songs.Count)
            .ThenBy(Tier)
            .ThenByDescending(g => g.LocalReleaseId is not null)
            .ThenByDescending(g => g.Editions)
            .ThenBy(g => g.Year ?? "9999")
            .ToList();
    }

    /// <summary>
    /// The guesses in the order to show them: the albums the songs are placed on (see <see cref="Assign"/>) first,
    /// then the other albums they are on.
    /// </summary>
    public static List<AlbumGuess> Rank(IReadOnlyList<AlbumGuess> guesses)
    {
        var placed = Assign(guesses).Select(p => p.ReleaseMbid).ToList();
        return placed.Select(id => guesses.First(g => g.ReleaseMbid == id))
            .Concat(guesses.Where(g => !placed.Contains(g.ReleaseMbid))).ToList();
    }

    /// <summary>
    /// One album per song. Studio albums are considered before singles, and both before compilations and live
    /// records; within a tier the album that collects the most remaining songs wins, so songs of one album stay
    /// together. Songs nothing was found for are left out.
    /// </summary>
    public static List<AlbumGuess> Assign(IReadOnlyList<AlbumGuess> guesses)
    {
        var left = guesses.SelectMany(g => g.Songs).ToHashSet();
        var result = new List<AlbumGuess>();
        foreach (var tier in guesses.GroupBy(Tier).OrderBy(t => t.Key))
        {
            var pool = tier.ToList();
            while (left.Count > 0)
            {
                var best = pool
                    .Select(g => (Guess: g, Songs: g.Songs.Where(left.Contains).ToList()))
                    .Where(x => x.Songs.Count > 0)
                    .OrderByDescending(x => x.Songs.Count)
                    .ThenByDescending(x => x.Guess.LocalReleaseId is not null)
                    .ThenByDescending(x => x.Guess.Editions) // the song's real home is on many editions
                    .ThenBy(x => x.Guess.Year ?? "9999")
                    .FirstOrDefault();
                if (best.Guess is null) break;
                result.Add(best.Guess with { Songs = best.Songs });
                foreach (var s in best.Songs) left.Remove(s);
                pool.Remove(best.Guess);
            }
        }
        return result;
    }

    /// <summary>0 studio album/EP, 1 single, 2 other, 3 compilation/live and the like.</summary>
    private static int Tier(AlbumGuess g) =>
        g.Secondary ? 3 : g.PrimaryType?.ToLowerInvariant() switch { "album" or "ep" => 0, "single" => 1, _ => 2 };

    private static bool IsSecondary(RecordingRelease r) => r.SecondaryTypes.Any(SecondaryKinds.Contains);

    private (Dictionary<string, Ulid> Releases, Dictionary<string, Ulid> Groups) LocalIds()
    {
        using var c = db.Open();
        var releases = c.Query("SELECT external_id, entity_id FROM external_id WHERE provider = 'musicbrainz' AND entity_type = 'release'",
            r => (Mbid: r.GetString(0), Id: r.Id(1))).GroupBy(x => x.Mbid).ToDictionary(g => g.Key, g => g.First().Id);
        var groups = c.Query("""
            SELECT e.external_id, (SELECT r.id FROM release r WHERE r.release_group_id = e.entity_id ORDER BY r.created_at LIMIT 1)
            FROM external_id e WHERE e.provider = 'musicbrainz' AND e.entity_type = 'release_group'
            """, r => (Mbid: r.GetString(0), Id: r.IdN(1)))
            .Where(x => x.Id is not null).GroupBy(x => x.Mbid).ToDictionary(g => g.Key, g => g.First().Id!.Value);
        return (releases, groups);
    }
}
