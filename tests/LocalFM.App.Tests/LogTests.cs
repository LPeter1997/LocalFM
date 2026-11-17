using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Tests;
using LocalFM.Native;
using LocalFM.Playback;

namespace LocalFM.App.Tests;

public class LogTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "localfm-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Session_file_gets_every_line_with_exceptions_indented()
    {
        string dir = TempDir();
        try
        {
            string path;
            using (var log = LogFile.Open(dir, echo: false))
            {
                path = log.FilePath;
                log.Write(LogLevel.Info, "Player", "Pause by app, x.m4a at 1:20.0");
                log.Write(LogLevel.Error, "Engine", "Engine loop error", new InvalidOperationException("boom\nsecond line"));
                log.Flush(TimeSpan.FromSeconds(5));
                var text = File.ReadAllText(path); // readable while the app still has it open
                Assert.Contains("INFO  Player: Pause by app, x.m4a at 1:20.0", text);
                Assert.Contains("ERROR Engine: Engine loop error", text);
                Assert.Contains("\n    System.InvalidOperationException: boom\n    second line", text);
            }
            Assert.StartsWith("localfm-", Path.GetFileName(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Keeps_only_the_newest_sessions()
    {
        string dir = TempDir();
        try
        {
            for (int i = 0; i < LogFile.KeepSessions + 5; i++)
                File.WriteAllText(Path.Combine(dir, $"localfm-20260101-0000{i:00}-1.log"), "old");
            File.WriteAllText(Path.Combine(dir, "unrelated.txt"), "keep me");
            using (LogFile.Open(dir, echo: false)) { }

            var logs = Directory.GetFiles(dir, "localfm-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(LogFile.KeepSessions, logs.Count);
            Assert.DoesNotContain("localfm-20260101-000000-1.log", logs); // the oldest went first
            Assert.True(File.Exists(Path.Combine(dir, "unrelated.txt")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Writing_after_dispose_is_ignored()
    {
        string dir = TempDir();
        try
        {
            var log = LogFile.Open(dir, echo: false);
            log.Dispose();
            log.Write(LogLevel.Info, "X", "late"); // e.g. a background thread logging during shutdown
            log.Flush(TimeSpan.FromMilliseconds(100));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A skip or pause in the log must say who asked for it.</summary>
    [SkippableFact]
    public async Task Player_logs_who_paused_and_seeked()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        using var lib = new TempLibrary();
        lib.MakeTrack("a.flac", new() { ["title"] = "A", ["artist"] = "X", ["album"] = "Y" }, seconds: 3);
        await lib.ScanAsync();

        var lines = new List<string>();
        var previous = Log.Sink;
        Log.Sink = (level, area, msg, ex) => { lock (lines) lines.Add($"{area}: {msg}"); previous(level, area, msg, ex); };
        try
        {
            using var engine = new PlaybackEngine(OutputMode.Null);
            using var player = new PlayerService(engine, new History(lib.Db), lib.Queries);
            player.PlayTracks(lib.Queries.Tracks(), source: "test-play");
            for (int i = 0; i < 100 && engine.State != PlaybackState.Playing; i++) await Task.Delay(20);
            player.Seek(TimeSpan.FromSeconds(1), "test-seek");
            player.Pause("MPRIS :1.99");
            await Task.Delay(200);

            List<string> mine;
            lock (lines) mine = lines.Where(l => l.StartsWith("Player:", StringComparison.Ordinal)).ToList();
            Assert.Contains(mine, l => l.Contains("Play 1 track(s) from #1 by test-play"));
            Assert.Contains(mine, l => l.Contains("Started ") && l.Contains("a.flac"));
            Assert.Contains(mine, l => l.Contains("Seek to 0:01.0 by test-seek, a.flac at"));
            Assert.Contains(mine, l => l.Contains("Pause by MPRIS :1.99, a.flac at"));
        }
        finally { Log.Sink = previous; }
    }
}
