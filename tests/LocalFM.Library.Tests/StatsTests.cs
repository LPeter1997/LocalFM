namespace LocalFM.Library.Tests;

public class StatsTests
{
    [Fact]
    public void Streaks_count_consecutive_days()
    {
        var d = new DateTime(2026, 11, 14);
        var days = new[] { d.AddDays(-10), d.AddDays(-9), d.AddDays(-8), d.AddDays(-5), d.AddDays(-1), d };
        Assert.Equal((2, 3), ListeningStats.Streaks(days, d));
        Assert.Equal((2, 3), ListeningStats.Streaks(days[..^1].Append(d.AddDays(-2)).OrderBy(x => x).ToList(), d)); // yesterday still counts
        Assert.Equal((0, 3), ListeningStats.Streaks(days[..4], d));
    }

    [SkippableFact]
    public async Task Builds_totals_top_lists_and_time_shapes()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.flac", new() { ["title"] = "Blackout", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("a/2.flac", new() { ["title"] = "Arizona", ["artist"] = "Scorpions", ["album"] = "Blackout", ["track"] = "2" }, seconds: 0.3);
        lib.MakeTrack("b/1.flac", new() { ["title"] = "Karma Police", ["artist"] = "Radiohead", ["album"] = "OK Computer", ["track"] = "1" }, seconds: 0.3);
        lib.MakeTrack("c/1.flac", new() { ["title"] = "Unplayed", ["artist"] = "Nobody", ["album"] = "Nothing", ["track"] = "1" }, seconds: 0.3);
        await lib.ScanAsync();
        var tracks = lib.Queries.Tracks().ToDictionary(t => t.Title);
        var history = new History(lib.Db);
        var utc = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

        async Task Play(string title, DateTimeOffset at, int seconds, bool completed = true) =>
            await history.RecordPlayAsync(tracks[title].RecordingId, tracks[title].FileId, at, TimeSpan.FromSeconds(seconds), completed);

        // This week: Blackout 3x (evenings), Arizona once, Karma Police once plus a skip.
        await Play("Blackout", now.AddDays(-1).AddHours(-1), 240);
        await Play("Blackout", now.AddDays(-2).AddHours(-1), 240);
        await Play("Blackout", now.AddHours(-1), 240);
        await Play("Arizona", now.AddHours(-2), 200);
        await Play("Karma Police", now.AddHours(-3), 260);
        await Play("Karma Police", now.AddHours(-4), 10, completed: false);   // a skip
        // Long ago: Karma Police was a favourite.
        for (int i = 0; i < 6; i++) await Play("Karma Police", now.AddDays(-200 - i), 260);

        var week = ListeningStats.Build(lib.Db, lib.Images, now.AddDays(-7), now, utc);
        var s = week.Summary;
        Assert.Equal(5, s.Plays);
        Assert.Equal(1, s.Skips);
        Assert.Equal((240 * 3 + 200 + 260 + 10) * 1000L, s.ListenedMs);
        Assert.Equal(3, s.Tracks);
        Assert.Equal(2, s.Artists);
        Assert.Equal(2, s.Albums);
        Assert.Equal(2, s.NewTracks);                   // Karma Police was heard before
        Assert.Equal(0, s.PreviousListenedMs);
        Assert.Equal(3, s.CurrentStreakDays);
        Assert.Equal(4, s.LibraryTracks);
        Assert.Equal(3, s.LibraryTracksPlayed);

        Assert.Equal("Blackout", week.TopTracks[0].Title);
        Assert.Equal(3, week.TopTracks[0].Plays);
        Assert.Equal("Scorpions", week.TopArtists[0].Title);
        Assert.Equal(4, week.TopArtists[0].Plays);
        Assert.Equal("Blackout", week.TopAlbums[0].Title);
        Assert.Equal("Scorpions", week.TopAlbums[0].Subtitle);

        Assert.False(week.TimelineByMonth);
        Assert.Equal(8, week.Timeline.Count);          // the 7th day back through today
        Assert.Equal(3, week.Timeline[^1].Plays);      // today: Blackout, Arizona, Karma Police (the skip does not count)
        Assert.Equal(240 * 3 * 1000L, week.ByHour[19]);
        Assert.Equal(6, week.Recent.Count);
        Assert.False(week.Recent.Single(r => r.ListenedMs == 10_000).Counted);

        // Karma Police: loved six times long ago, but played this week, so it is not "forgotten" now...
        Assert.Empty(week.Forgotten);
        // ...it was two months after its streak, though.
        var then = ListeningStats.Build(lib.Db, lib.Images, now.AddDays(-120), now.AddDays(-100), utc);
        Assert.Equal("Karma Police", Assert.Single(then.Forgotten).Title);
        Assert.Equal(0, then.Summary.Plays);

        var all = ListeningStats.Build(lib.Db, lib.Images, null, now, utc);
        Assert.True(all.TimelineByMonth);
        Assert.Equal(11, all.Summary.Plays);
        Assert.Equal("Karma Police", all.TopTracks[0].Title);
        Assert.Equal(6, all.Summary.LongestStreakDays);
    }
}
