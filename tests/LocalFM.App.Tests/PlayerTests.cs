using LocalFM.App;
using LocalFM.Library;
using LocalFM.Library.Tests;
using LocalFM.Native;
using LocalFM.Playback;

namespace LocalFM.App.Tests;

public class PlayerTests
{
    private static Dictionary<string, string> Tags(string title, int n) =>
        new() { ["title"] = title, ["artist"] = "X", ["album"] = "Y", ["track"] = n.ToString() };

    /// <summary>
    /// Plays an album through the real-time null device while the library is being scanned and
    /// hammered with writes: playback must not underrun, and must advance and record history.
    /// </summary>
    [SkippableFact]
    public async Task Plays_album_gaplessly_while_library_is_busy()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        for (int i = 1; i <= 3; i++) lib.MakeTrack($"album/{i:00}.flac", Tags($"T{i}", i), seconds: 1.5);
        await lib.ScanAsync();
        var tracks = lib.Queries.AlbumTracks(lib.Queries.Albums().Single().ReleaseId);

        // more music to scan while playing
        for (int i = 0; i < 30; i++) lib.MakeTrack($"more/{i:00}.mp3", Tags($"M{i}", i + 1), seconds: 2);

        using var engine = new PlaybackEngine(OutputMode.Null);
        var history = new History(lib.Db);
        using var player = new PlayerService(engine, history, lib.Queries);
        var started = new List<string>();
        player.Changed += () => { lock (started) { var t = player.Current?.Track.Title; if (t is not null && (started.Count == 0 || started[^1] != t)) started.Add(t); } };

        player.PlayTracks(tracks, 0);
        var scan = lib.ScanAsync();
        var spam = Task.Run(async () =>
        {
            for (int i = 0; i < 2000; i++) await history.SetAsync("spam" + (i % 10), new string('x', 1000));
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !(engine.State == PlaybackState.Stopped && started.Count == 3))
            await Task.Delay(50);
        await scan;
        await spam;

        Assert.Equal(["T1", "T2", "T3"], started);
        Assert.Equal(PlaybackState.Stopped, engine.State);
        Assert.True(engine.Underruns <= 1, $"underruns: {engine.Underruns}"); // 1 = the expected end of playback
        await Task.Delay(300); // history writes are async
        Assert.All(tracks, t => Assert.Equal(1, history.PlayCount(t.RecordingId)));
        Assert.Equal(33, lib.Queries.Counts().Tracks);
    }

    [SkippableFact]
    public async Task Restores_queue_and_position()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        for (int i = 1; i <= 2; i++) lib.MakeTrack($"{i}.flac", Tags($"T{i}", i), seconds: 3);
        await lib.ScanAsync();
        var tracks = lib.Queries.AlbumTracks(lib.Queries.Albums().Single().ReleaseId);
        var history = new History(lib.Db);

        using (var engine = new PlaybackEngine(OutputMode.Null))
        using (var player = new PlayerService(engine, history, lib.Queries))
        {
            player.PlayTracks(tracks, 1);
            await Task.Delay(1200);
            player.Volume = 0.5f;
        }
        await Task.Delay(200);

        using var engine2 = new PlaybackEngine(OutputMode.Null);
        using var player2 = new PlayerService(engine2, history, lib.Queries);
        player2.RestoreState();
        Assert.Equal("T2", player2.Current!.Track.Title);
        Assert.InRange(player2.Position.TotalSeconds, 0.8, 2.0);
        Assert.Equal(0.5f, player2.Volume);
    }

    private sealed class LocalFollow : IFollowSource
    {
        public TrackHandle Handle(QueueItem item, float gain) => new(item.Track.Path, item.Id, gain);
    }

    /// <summary>
    /// Changed handlers read the player under locks of their own (MPRIS does). If the player raised Changed while
    /// holding its gate, such a handler and another thread would deadlock (leaving a vibe froze the app this way).
    /// </summary>
    [SkippableFact]
    public async Task Changed_is_never_raised_while_the_player_is_locked()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        for (int i = 1; i <= 3; i++) lib.MakeTrack($"{i}.flac", Tags($"T{i}", i), seconds: 3);
        await lib.ScanAsync();
        var tracks = lib.Queries.AlbumTracks(lib.Queries.Albums().Single().ReleaseId);
        using var engine = new PlaybackEngine(OutputMode.Null);
        using var player = new PlayerService(engine, new History(lib.Db), lib.Queries);
        int locked = 0;
        player.Changed += () =>
        {
            // Another thread must be able to read the player while the handler runs.
            if (!Task.Run(() => player.Current).Wait(TimeSpan.FromMilliseconds(500))) Interlocked.Increment(ref locked);
        };

        player.PlayTracks(tracks, 0);
        player.Enqueue(tracks);
        player.PlayNext([tracks[2]]);
        player.MoveItem(1, 2);
        player.SetShuffle(true);
        player.ClearUpcoming();
        player.EnterFollowMode(new LocalFollow());
        player.FollowSetQueue(tracks.Select(QueueItem.For).ToList());
        player.LeaveFollowMode();
        await Task.Delay(1000); // deferred events

        Assert.Equal(0, locked);
    }
}
