using LocalFM.Native;

namespace LocalFM.Playback.Tests;

public class EngineTests
{
    private const int Rate = 48000;

    /// <summary>Drives an offline engine: plays the files as a queue and returns everything it rendered.</summary>
    private static (float[] Samples, List<EngineEvent> Events, long Underruns) RenderQueue(
        string[] files, Action<PlaybackEngine, TrackHandle[], int>? onFrame = null)
    {
        using var engine = new PlaybackEngine(OutputMode.Offline, Rate);
        var handles = files.Select((f, i) => new TrackHandle(f, i)).ToArray();
        var events = new List<EngineEvent>();
        var output = new List<float>();
        var block = new float[512 * 2];

        engine.Play(handles[0]);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        bool finished = false;
        int rendered = 0;
        long lastBuffered = -1;
        var stalledSince = DateTime.UtcNow;
        while (!finished && DateTime.UtcNow < deadline)
        {
            while (engine.Events.TryRead(out var e))
            {
                events.Add(e);
                if (e is TrackStarted ts)
                {
                    int i = (int)ts.Track.Token!;
                    engine.SetNext(i + 1 < handles.Length ? handles[i + 1] : null);
                }
                if (e is PlaybackFinished) finished = true;
            }
            long buffered = engine.BufferedFrames;
            if (buffered != lastBuffered) { lastBuffered = buffered; stalledSince = DateTime.UtcNow; }
            // Render full blocks; once the engine has nothing more to add (end of queue), drain the tail.
            bool drain = DateTime.UtcNow - stalledSince > TimeSpan.FromMilliseconds(100);
            if (buffered >= 512 || drain)
            {
                engine.RenderOffline(block);
                output.AddRange(block);
                onFrame?.Invoke(engine, handles, rendered++);
                stalledSince = DateTime.UtcNow;
            }
            else Thread.Sleep(1);
        }
        Assert.True(finished, "playback did not finish");
        return (output.ToArray(), events, engine.Underruns);
    }

    /// <summary>Largest jump between consecutive samples of the left channel, skipping fade-in.</summary>
    private static (double MaxDelta, int At) MaxDelta(float[] s, int from, int to)
    {
        double max = 0; int at = -1;
        for (int i = from + 1; i < to; i++)
        {
            double d = Math.Abs(s[i * 2] - s[(i - 1) * 2]);
            if (d > max) { max = d; at = i; }
        }
        return (max, at);
    }

    [SkippableTheory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public void Consecutive_tracks_are_gapless(string ext)
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var files = Fixtures.SplitSine("gapless", ext, Rate, 1.3, 0.7, 1.1);
        var (s, events, underruns) = RenderQueue(files);
        int frames = s.Length / 2;

        Assert.Equal(3, events.OfType<TrackStarted>().Count());
        Assert.Equal(3, events.OfType<TrackEnded>().Count(e => e.Completed));
        Assert.True(underruns <= 1, $"underruns: {underruns}"); // the harness drains past the end once

        // Total length is the sum of the parts (no inserted silence), within a block of tail padding.
        int expected = (int)(3.1 * Rate);
        Assert.InRange(frames, expected, expected + 1024);

        // A continuous 440 Hz sine at amplitude 1/8 moves at most ~0.0072 per sample; a gap or a
        // duplicated/missing chunk shows up as a jump. MP3 is lossy, so allow some codec noise.
        double limit = ext == "mp3" ? 0.03 : 0.012;
        var (max, at) = MaxDelta(s, 100, expected - 100);
        Assert.True(max < limit, $"discontinuity {max:F4} at frame {at} ({at / (double)Rate:F4}s)");
    }

    [SkippableFact]
    public void Seek_moves_position_and_audio()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var file = Fixtures.SplitSine("seek", "flac", Rate, 5.0)[0];
        using var engine = new PlaybackEngine(OutputMode.Offline, Rate);
        engine.Play(new TrackHandle(file));
        SpinUntil(() => engine.BufferedFrames > 4096);
        var block = new float[1024];
        for (int i = 0; i < 20; i++) engine.RenderOffline(block);

        engine.Seek(TimeSpan.FromSeconds(3));
        SpinUntil(() => engine.Position >= TimeSpan.FromSeconds(3));
        // position is reported immediately; audio follows after the fade-out (~8 ms)
        for (int i = 0; i < 10; i++) { SpinUntil(() => engine.BufferedFrames >= 512); engine.RenderOffline(block); }
        var pos = engine.Position.TotalSeconds;
        Assert.InRange(pos, 3.0, 3.2);
    }

    [SkippableFact]
    public void Changing_next_after_splice_plays_the_new_choice()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var a = Fixtures.SplitSine("splice-a", "flac", Rate, 2.0)[0];
        var b = Fixtures.SplitSine("splice-b", "flac", Rate, 1.0)[0];
        var c = Fixtures.SplitSine("splice-c", "flac", Rate, 1.0)[0];
        using var engine = new PlaybackEngine(OutputMode.Offline, Rate, ringMs: 1000);
        var ha = new TrackHandle(a, "a");
        var hb = new TrackHandle(b, "b");
        var hc = new TrackHandle(c, "c");
        engine.Play(ha);
        engine.SetNext(hb);
        var block = new float[512 * 2];
        var started = new List<string>();
        bool changed = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            while (engine.Events.TryRead(out var e))
            {
                if (e is TrackStarted ts)
                {
                    started.Add((string)ts.Track.Token!);
                    if ((string)ts.Track.Token! == "c") engine.SetNext(null);
                }
            }
            if (started.Count == 2) break;
            // Once A has ~0.7 s left, B has been spliced into the 1 s ring: change our mind.
            if (!changed && engine.Position > TimeSpan.FromSeconds(1.3))
            {
                engine.SetNext(hc);
                changed = true;
                // Offline rendering runs far faster than real time; let the engine apply the command
                // before the playhead moves on (in real time there is ~0.7 s of slack here).
                Thread.Sleep(50);
            }
            if (engine.BufferedFrames >= 512) engine.RenderOffline(block); else Thread.Sleep(1);
        }
        Assert.Equal(["a", "c"], started);
    }

    private static void SpinUntil(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Thread.Sleep(1);
        }
    }
}
