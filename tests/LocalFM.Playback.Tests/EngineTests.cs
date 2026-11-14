using LocalFM.Native;

namespace LocalFM.Playback.Tests;

public class EngineTests
{
    private const int Rate = 48000;

    /// <summary>Drives an offline engine: plays the files as a queue and returns everything it rendered.</summary>
    private static (float[] Samples, List<EngineEvent> Events, long Underruns) RenderQueue(
        string[] files, Action<PlaybackEngine, TrackHandle[], int>? onFrame = null,
        Action<PlaybackEngine>? setup = null, Func<string, int, TrackHandle>? handle = null)
    {
        using var engine = new PlaybackEngine(OutputMode.Offline, Rate);
        setup?.Invoke(engine);
        var handles = files.Select((f, i) => handle?.Invoke(f, i) ?? new TrackHandle(f, i)).ToArray();
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

    [SkippableFact]
    public void Changing_next_during_a_buffered_crossfade_rewinds_and_fades_into_the_new_choice()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var ha = new TrackHandle(Fixtures.Sine("xf-undo-a", 440, Rate, 3.0), "a");
        var hb = new TrackHandle(Fixtures.Sine("xf-undo-b3", 660, Rate, 3.0), "b");
        var hc = new TrackHandle(Fixtures.Sine("xf-undo-c3", 880, Rate, 3.0), "c");
        using var engine = new PlaybackEngine(OutputMode.Offline, Rate, ringMs: 1000) { Crossfade = TimeSpan.FromSeconds(1) };
        engine.Play(ha);
        engine.SetNext(hb);
        var block = new float[512 * 2];
        var started = new List<string>();
        var output = new List<float>();
        bool changed = false, finished = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var lastData = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline && !finished)
        {
            while (engine.Events.TryRead(out var e))
            {
                if (e is TrackStarted ts)
                {
                    started.Add((string)ts.Track.Token!);
                    if ((string)ts.Track.Token! == "c") engine.SetNext(null);
                }
                if (e is PlaybackFinished) finished = true;
            }
            // At 1.3 s the ring (1 s) already holds the start of the A→B mix at 2.0 s.
            if (!changed && engine.Position > TimeSpan.FromSeconds(1.3))
            {
                engine.SetNext(hc);
                changed = true;
                Thread.Sleep(50);
            }
            if (engine.BufferedFrames >= 512 || (DateTime.UtcNow - lastData).TotalMilliseconds > 100)
            {
                engine.RenderOffline(block);
                output.AddRange(block);
                lastData = DateTime.UtcNow;
                Thread.Sleep(2); // ~5x real time: file opening must get the head start it has in real playback
            }
            else Thread.Sleep(1);
        }
        if (Environment.GetEnvironmentVariable("LOCALFM_DUMP") is { } dump)
            File.WriteAllBytes(dump, System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.ToArray().AsSpan()).ToArray());
        Assert.True(finished);
        Assert.Equal(["a", "c"], started);
        // A was rewound to where the A→B mix began and mixed into C over 1 s: 3 + 3 - 1 seconds in all.
        // (A cut short at the old mix point would be ~4.3 s; no crossfade, 6 s.)
        Assert.InRange(output.Count / 2, 5 * Rate - 4096, 5 * Rate + 4096);
    }

    private static double Rms(float[] s, int fromFrame, int toFrame)
    {
        double sum = 0;
        for (int i = fromFrame; i < toFrame; i++) sum += s[i * 2] * s[i * 2];
        return Math.Sqrt(sum / Math.Max(1, toFrame - fromFrame));
    }

    [SkippableFact]
    public void Crossfade_overlaps_the_end_of_one_track_with_the_next()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        string[] files = [Fixtures.Sine("xfade-a", 440, Rate, 3.0), Fixtures.Sine("xfade-b", 660, Rate, 3.0)];
        var (s, events, _) = RenderQueue(files, setup: e => e.Crossfade = TimeSpan.FromSeconds(1));
        int frames = s.Length / 2;

        Assert.Equal(2, events.OfType<TrackStarted>().Count());
        int expected = 5 * Rate; // 3 + 3 - 1 s of overlap
        Assert.InRange(frames, expected - 2048, expected + 2048);
        // No dip to silence anywhere in the mix (equal-power curves keep the level up).
        for (int f = Rate / 10; f < expected - Rate / 10; f += Rate / 10)
            Assert.True(Rms(s, f, f + Rate / 20) > 0.05, $"quiet at {f / (double)Rate:F2}s");
    }

    [SkippableFact]
    public void Tracks_marked_gapless_are_not_crossfaded()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var files = Fixtures.SplitSine("xfade-album", "flac", Rate, 2.0, 2.0);
        var (s, _, _) = RenderQueue(files, setup: e => e.Crossfade = TimeSpan.FromSeconds(1),
            handle: (f, i) => new TrackHandle(f, i, JoinGapless: i > 0));
        Assert.InRange(s.Length / 2, 4 * Rate, 4 * Rate + 1024);
        var (max, at) = MaxDelta(s, 100, 4 * Rate - 100);
        Assert.True(max < 0.012, $"discontinuity {max:F4} at frame {at}");
    }

    [SkippableFact]
    public void Equalizer_boosts_its_band_on_the_audio_thread()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var files = Fixtures.SplitSine("eq", "flac", Rate, 1.5);
        var (flat, _, _) = RenderQueue(files);
        var (boosted, _, _) = RenderQueue(files, setup: e =>
            e.SetEqualizer([new EqBand(EqBandType.Peak, 440, 1.41f, 12)], 0));
        double ratio = Rms(boosted, Rate / 2, Rate) / Rms(flat, Rate / 2, Rate);
        Assert.InRange(ratio, 3.6, 4.4); // +12 dB at the sine's frequency
        var (cut, _, _) = RenderQueue(files, setup: e =>
            e.SetEqualizer([new EqBand(EqBandType.HighShelf, 4000, 0.71f, 12)], -6));
        Assert.InRange(Rms(cut, Rate / 2, Rate) / Rms(flat, Rate / 2, Rate), 0.45, 0.56); // shelf far above 440 Hz: just the preamp
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
