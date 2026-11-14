using LocalFM.Core;
using Microsoft.Data.Sqlite;

namespace LocalFM.Library;

/// <summary>Something that ranks in a "top" list: a recording, an artist or an album.</summary>
public sealed record TopItem(Ulid Id, string Title, string? Subtitle, string? CoverPath, int Plays, long ListenedMs,
    Ulid? ReleaseId, DateTimeOffset? LastPlayed = null);

public sealed record RecentPlay(Ulid RecordingId, string Title, string? Artist, string? CoverPath, Ulid? ReleaseId,
    DateTimeOffset StartedAt, long ListenedMs, bool Counted);

/// <summary>One bar of the listening timeline (a day, or a month for long periods).</summary>
public sealed record StatsBucket(DateTime Start, string Label, long ListenedMs, int Plays);

public sealed record StatsSummary(
    long ListenedMs, int Plays, int Skips, int Tracks, int Artists, int Albums, int NewTracks,
    long? PreviousListenedMs, int CurrentStreakDays, int LongestStreakDays,
    int LibraryTracks, int LibraryTracksPlayed);

public sealed record StatsReport(
    StatsSummary Summary,
    IReadOnlyList<StatsBucket> Timeline,
    bool TimelineByMonth,
    IReadOnlyList<long> ByHour,      // 24 entries, listened ms per local hour
    IReadOnlyList<long> ByWeekday,   // 7 entries, Monday first
    IReadOnlyList<TopItem> TopTracks,
    IReadOnlyList<TopItem> TopArtists,
    IReadOnlyList<TopItem> TopAlbums,
    IReadOnlyList<TopItem> Forgotten,
    IReadOnlyList<RecentPlay> Recent);

/// <summary>
/// Statistics derived from the play log. A play "counts" when it finished or lasted at least 30 seconds
/// (the same rule as <see cref="History.PlayCount"/>); shorter, unfinished ones are skips.
/// Read-only and safe on any thread.
/// </summary>
public static class ListeningStats
{
    public const long CountedMs = 30_000;
    private const string Counted = "(e.completed = 1 OR e.ms_listened >= 30000)";

    // Each event with the track (and so the album) it was heard from. A play whose file is gone falls back
    // to any track of the recording.
    private const string Events = """
        ev AS (
            SELECT e.id, e.recording_id AS rec, e.started_at AS at, e.ms_listened AS ms,
                   (e.completed = 1 OR e.ms_listened >= 30000) AS counted,
                   COALESCE((SELECT f.track_id FROM audio_file f WHERE f.id = e.audio_file_id),
                            (SELECT t.id FROM track t WHERE t.recording_id = e.recording_id LIMIT 1)) AS track
            FROM play_event e
            WHERE e.recording_id IS NOT NULL AND e.started_at >= $1 AND e.started_at < $2)
        """;

    private const string Cover = """
        (SELECT i.rel_path FROM entity_image ei JOIN image i ON i.id = ei.image_id
         WHERE ei.entity_type = 'release' AND ei.entity_id = {0} AND ei.role = 'front'
         ORDER BY CASE ei.source WHEN 'user' THEN 0 WHEN 'folder' THEN 1 WHEN 'embedded' THEN 2 ELSE 3 END LIMIT 1)
        """;

    private static string CoverOf(string releaseExpr) => string.Format(Cover, releaseExpr);

    public static StatsReport Build(Database db, ImageStore images, DateTimeOffset? from, DateTimeOffset to,
        TimeZoneInfo? tz = null, int top = 10)
    {
        tz ??= TimeZoneInfo.Local;
        using var c = db.Open();
        long a = from?.ToUnixTimeMilliseconds() ?? 0, b = to.ToUnixTimeMilliseconds();
        string? Full(string? rel) => rel is null ? null : images.FullPath(rel);

        // ---- per-event rows for time bucketing (cheap: three numbers each)
        var rows = c.Query("SELECT e.started_at, e.ms_listened, " + Counted + " FROM play_event e WHERE e.started_at >= $1 AND e.started_at < $2",
            r => (At: r.GetInt64(0), Ms: r.GetInt64(1), Counted: r.GetInt64(2) != 0), a, b);
        long listened = rows.Sum(x => x.Ms);
        int plays = rows.Count(x => x.Counted), skips = rows.Count - plays;

        long? previous = null;
        if (from is { } f)
        {
            long span = b - a;
            previous = c.Scalar<long>("SELECT COALESCE(SUM(ms_listened), 0) FROM play_event WHERE started_at >= $1 AND started_at < $2", a - span, a);
        }

        var distinct = c.QuerySingle($"""
            WITH {Events}
            SELECT COUNT(DISTINCT ev.rec),
                   COUNT(DISTINCT acn.artist_id),
                   COUNT(DISTINCT m.release_id)
            FROM ev
            LEFT JOIN recording rec ON rec.id = ev.rec
            LEFT JOIN artist_credit_name acn ON acn.credit_id = rec.artist_credit_id
            LEFT JOIN track t ON t.id = ev.track
            LEFT JOIN medium m ON m.id = t.medium_id
            WHERE ev.counted
            """, r => (Tracks: r.GetInt32(0), Artists: r.GetInt32(1), Albums: r.GetInt32(2)), a, b);

        int newTracks = (int)c.Scalar<long>("""
            SELECT COUNT(*) FROM (
                SELECT e.recording_id FROM play_event e
                WHERE e.recording_id IS NOT NULL AND (e.completed = 1 OR e.ms_listened >= 30000)
                GROUP BY e.recording_id HAVING MIN(e.started_at) >= $1 AND MIN(e.started_at) < $2)
            """, a, b);

        // ---- streaks over all time (days with at least one counted play)
        var days = c.Query("SELECT e.started_at FROM play_event e WHERE " + Counted, r => r.GetInt64(0))
            .Select(ms => LocalDay(ms, tz)).Distinct().OrderBy(d => d).ToList();
        var today = TimeZoneInfo.ConvertTime(to.AddMilliseconds(-1), tz).Date;
        var (current, longest) = Streaks(days, today);

        var (libTracks, libPlayed) = c.QuerySingle("""
            SELECT COUNT(DISTINCT t.recording_id),
                   COUNT(DISTINCT CASE WHEN EXISTS (SELECT 1 FROM play_event e WHERE e.recording_id = t.recording_id
                                                    AND (e.completed = 1 OR e.ms_listened >= 30000)) THEN t.recording_id END)
            FROM track t WHERE EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t.id AND f.missing = 0)
            """, r => (r.GetInt32(0), r.GetInt32(1)));

        // ---- time shapes
        var localRows = rows.Select(x => (Local: TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(x.At), tz).DateTime, x.Ms, x.Counted)).ToList();
        var byHour = new long[24];
        var byWeekday = new long[7];
        foreach (var x in localRows)
        {
            byHour[x.Local.Hour] += x.Ms;
            byWeekday[((int)x.Local.DayOfWeek + 6) % 7] += x.Ms;
        }

        DateTime firstDay = from is { } ff
            ? TimeZoneInfo.ConvertTime(ff, tz).Date
            : localRows.Count > 0 ? localRows.Min(x => x.Local).Date : today;
        bool byMonth = (today - firstDay).TotalDays > 92;
        var timeline = Timeline(localRows.Select(x => (x.Local, x.Ms, x.Counted)), firstDay, today, byMonth);

        // ---- top lists
        var topTracks = c.Query($"""
            WITH {Events}
            SELECT ev.rec, COALESCE(t.title, rec.title), ac.display_name, {CoverOf("m.release_id")}, SUM(ev.counted), SUM(ev.ms), m.release_id
            FROM ev
            JOIN recording rec ON rec.id = ev.rec
            LEFT JOIN track t ON t.id = ev.track
            LEFT JOIN medium m ON m.id = t.medium_id
            LEFT JOIN artist_credit ac ON ac.id = COALESCE(t.artist_credit_id, rec.artist_credit_id)
            GROUP BY ev.rec HAVING SUM(ev.counted) > 0
            ORDER BY SUM(ev.counted) DESC, SUM(ev.ms) DESC LIMIT $3
            """, r => new TopItem(r.Id(0), r.GetString(1), r.Str(2), Full(r.Str(3)), r.GetInt32(4), r.GetInt64(5), r.IdN(6)), a, b, top);

        var topArtists = c.Query($"""
            WITH {Events}
            SELECT ar.id, ar.name, NULL,
                   (SELECT {CoverOf("m2.release_id")} FROM ev e2
                    JOIN recording r2 ON r2.id = e2.rec
                    JOIN artist_credit_name n2 ON n2.credit_id = r2.artist_credit_id AND n2.artist_id = ar.id
                    JOIN track t2 ON t2.id = e2.track JOIN medium m2 ON m2.id = t2.medium_id
                    GROUP BY m2.release_id ORDER BY COUNT(*) DESC LIMIT 1),
                   SUM(ev.counted), SUM(ev.ms), NULL
            FROM ev
            JOIN recording rec ON rec.id = ev.rec
            JOIN artist_credit_name acn ON acn.credit_id = rec.artist_credit_id
            JOIN artist ar ON ar.id = acn.artist_id
            GROUP BY ar.id HAVING SUM(ev.counted) > 0
            ORDER BY SUM(ev.counted) DESC, SUM(ev.ms) DESC LIMIT $3
            """, r => new TopItem(r.Id(0), r.GetString(1), r.Str(2), Full(r.Str(3)), r.GetInt32(4), r.GetInt64(5), r.IdN(6)), a, b, top);

        var topAlbums = c.Query($"""
            WITH {Events}
            SELECT rel.id, rel.title, ac.display_name, {CoverOf("rel.id")}, SUM(ev.counted), SUM(ev.ms), rel.id
            FROM ev
            JOIN track t ON t.id = ev.track
            JOIN medium m ON m.id = t.medium_id
            JOIN release rel ON rel.id = m.release_id
            LEFT JOIN artist_credit ac ON ac.id = rel.artist_credit_id
            GROUP BY rel.id HAVING SUM(ev.counted) > 0
            ORDER BY SUM(ev.counted) DESC, SUM(ev.ms) DESC LIMIT $3
            """, r => new TopItem(r.Id(0), r.GetString(1), r.Str(2), Full(r.Str(3)), r.GetInt32(4), r.GetInt64(5), r.IdN(6)), a, b, top);

        // Loved once, not heard for two months (relative to the end of the period).
        var forgotten = c.Query($"""
            SELECT e.recording_id, COALESCE(t.title, rec.title), ac.display_name, {CoverOf("m.release_id")},
                   SUM({Counted}), SUM(e.ms_listened), m.release_id, MAX(e.started_at)
            FROM play_event e
            JOIN recording rec ON rec.id = e.recording_id
            LEFT JOIN track t ON t.id = (SELECT t1.id FROM track t1 WHERE t1.recording_id = rec.id
                                         AND EXISTS (SELECT 1 FROM audio_file f WHERE f.track_id = t1.id AND f.missing = 0) LIMIT 1)
            LEFT JOIN medium m ON m.id = t.medium_id
            LEFT JOIN artist_credit ac ON ac.id = COALESCE(t.artist_credit_id, rec.artist_credit_id)
            WHERE e.started_at < $1 AND t.id IS NOT NULL
            GROUP BY e.recording_id
            HAVING SUM({Counted}) >= 5 AND MAX(e.started_at) < $2
            ORDER BY SUM({Counted}) DESC LIMIT $3
            """, r => new TopItem(r.Id(0), r.GetString(1), r.Str(2), Full(r.Str(3)), r.GetInt32(4), r.GetInt64(5), r.IdN(6),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7))),
            b, b - (long)TimeSpan.FromDays(60).TotalMilliseconds, top);

        var recent = c.Query($"""
            WITH {Events}
            SELECT ev.rec, COALESCE(t.title, rec.title), ac.display_name, {CoverOf("m.release_id")}, m.release_id, ev.at, ev.ms, ev.counted
            FROM ev
            JOIN recording rec ON rec.id = ev.rec
            LEFT JOIN track t ON t.id = ev.track
            LEFT JOIN medium m ON m.id = t.medium_id
            LEFT JOIN artist_credit ac ON ac.id = COALESCE(t.artist_credit_id, rec.artist_credit_id)
            ORDER BY ev.at DESC LIMIT 30
            """, r => new RecentPlay(r.Id(0), r.GetString(1), r.Str(2), Full(r.Str(3)), r.IdN(4),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)), r.GetInt64(6), r.GetInt64(7) != 0), a, b);

        return new StatsReport(
            new StatsSummary(listened, plays, skips, distinct.Tracks, distinct.Artists, distinct.Albums, newTracks,
                previous, current, longest, libTracks, libPlayed),
            timeline, byMonth, byHour, byWeekday, topTracks, topArtists, topAlbums, forgotten, recent);
    }

    private static DateTime LocalDay(long ms, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), tz).Date;

    /// <summary>Current streak (ending today, or yesterday if nothing yet today) and the longest run of days.</summary>
    public static (int Current, int Longest) Streaks(IReadOnlyList<DateTime> sortedDays, DateTime today)
    {
        int longest = 0, run = 0;
        DateTime? prev = null;
        foreach (var d in sortedDays)
        {
            if (d > today) break;
            run = prev is { } p && (d - p).TotalDays == 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
            prev = d;
        }
        int current = prev is { } last && (today - last).TotalDays <= 1 ? run : 0;
        return (current, longest);
    }

    private static List<StatsBucket> Timeline(IEnumerable<(DateTime Local, long Ms, bool Counted)> rows, DateTime first, DateTime last, bool byMonth)
    {
        DateTime Key(DateTime d) => byMonth ? new DateTime(d.Year, d.Month, 1) : d.Date;
        var sums = rows.GroupBy(x => Key(x.Local)).ToDictionary(g => g.Key, g => (Ms: g.Sum(x => x.Ms), Plays: g.Count(x => x.Counted)));
        var list = new List<StatsBucket>();
        for (var d = Key(first); d <= last; d = byMonth ? d.AddMonths(1) : d.AddDays(1))
        {
            var s = sums.GetValueOrDefault(d);
            list.Add(new StatsBucket(d, byMonth ? d.ToString("MMM yyyy") : d.ToString("ddd d MMM"), s.Ms, s.Plays));
            if (list.Count > 400) break; // a sanity bound; monthly buckets keep long spans small
        }
        return list;
    }
}
