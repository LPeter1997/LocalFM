using LocalFM.Native;
using LocalFM.Playback;
using LocalFM.Vibe.Client;

namespace LocalFM.Vibe.Tests;

public class RemoteMediaTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "localfm-tests", "vibe-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static float[] DecodeAll(Decoder d)
    {
        var all = new List<float>();
        var buf = new float[4096 * 2];
        int n;
        while ((n = d.Read(buf)) > 0) all.AddRange(buf.AsSpan(0, n * 2));
        return all.ToArray();
    }

    [SkippableTheory]
    [InlineData("flac", "")]
    [InlineData("mp3", "-c:a libmp3lame -b:a 192k")]
    [InlineData("m4a", "-c:a aac -b:a 160k")] // moov atom at the end: needs a read far ahead of the audio
    public void Remote_decode_matches_local_decode(string ext, string codec)
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var file = Fixtures.Sweep("sweep-" + ext, ext, 6, codec);
        var host = new FakeHost { Delay = TimeSpan.FromMilliseconds(2) };
        host.Add("t", file);
        using var cache = new RemoteMediaCache(_dir, host);
        var remote = cache.Add("t", host.Length("t"));

        using var local = Decoder.Open(file, Rate);
        using var streamed = Decoder.Open(remote.OpenRead(), Rate);
        Assert.Equal(local.DurationFrames, streamed.DurationFrames);
        Assert.Equal(DecodeAll(local), DecodeAll(streamed));
    }

    [SkippableFact]
    public void Seeking_ahead_of_the_download_fetches_that_part_first()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        // Long enough that the chunks a seek reads (the demuxer probes its way there) are a small part of the file,
        // whatever the ffmpeg that made the fixture put in it.
        var file = Fixtures.Sweep("sweep-seek-long", "flac", 120);
        var host = new FakeHost { Delay = TimeSpan.FromMilliseconds(20) };
        host.Add("t", file);
        using var cache = new RemoteMediaCache(_dir, host);
        var remote = cache.Add("t", host.Length("t")); // not prioritized: only what the decoder asks for is fetched

        using var local = Decoder.Open(file, Rate);
        using var streamed = Decoder.Open(remote.OpenRead(), Rate);
        long target = 100L * Rate;
        Assert.True(local.Seek(target));
        Assert.True(streamed.Seek(target));
        var a = new float[2048 * 2];
        var b = new float[2048 * 2];
        Assert.Equal(local.Read(a), streamed.Read(b));
        Assert.Equal(a, b);
        Assert.InRange(remote.BytesAvailable, 1, remote.Length / 2); // it did not wait for the whole file
    }

    [SkippableFact]
    public void Remote_tracks_play_gaplessly_in_the_engine()
    {
        Skip.IfNot(Fixtures.HaveFfmpeg);
        var files = Fixtures.SplitSine("remote-gapless", "flac", Rate, 1.3, 0.7, 1.1);
        var host = new FakeHost { Delay = TimeSpan.FromMilliseconds(1) };
        using var cache = new RemoteMediaCache(_dir, host);
        var handles = files.Select((f, i) =>
        {
            var rf = cache.Add(host.Add($"t{i}", f), new FileInfo(f).Length);
            return new TrackHandle($"vibe://t{i}", i, Open: rate => Decoder.Open(rf.OpenRead(), rate));
        }).ToArray();
        cache.SetPriority(["t0", "t1", "t2"]);

        using var engine = new PlaybackEngine(OutputMode.Offline, Rate);
        var output = new List<float>();
        var block = new float[512 * 2];
        engine.Play(handles[0]);
        bool finished = false;
        int started = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var idleSince = DateTime.UtcNow;
        while (!finished && DateTime.UtcNow < deadline)
        {
            while (engine.Events.TryRead(out var e))
            {
                if (e is TrackStarted ts)
                {
                    started++;
                    int i = (int)ts.Track.Token!;
                    engine.SetNext(i + 1 < handles.Length ? handles[i + 1] : null);
                }
                if (e is PlaybackFinished) finished = true;
            }
            if (engine.BufferedFrames >= 512 || DateTime.UtcNow - idleSince > TimeSpan.FromMilliseconds(200))
            {
                engine.RenderOffline(block);
                output.AddRange(block);
                idleSince = DateTime.UtcNow;
            }
            else Thread.Sleep(1);
        }
        Assert.True(finished, "playback did not finish");
        Assert.Equal(3, started);

        var s = output.ToArray();
        int expected = (int)(3.1 * Rate);
        double max = 0;
        for (int i = 101; i < expected - 100; i++) max = Math.Max(max, Math.Abs(s[i * 2] - s[(i - 1) * 2]));
        Assert.True(max < 0.012, $"discontinuity {max:F4}");
    }

    [Fact]
    public async Task Playing_file_is_fetched_before_the_next_one()
    {
        var a = Path.Combine(_dir + "-src", "a.bin");
        var b = Path.Combine(_dir + "-src", "b.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllBytes(a, new byte[RemoteMediaCache.ChunkSize * 10]);
        File.WriteAllBytes(b, new byte[RemoteMediaCache.ChunkSize * 10]);
        try
        {
            var host = new FakeHost { Delay = TimeSpan.FromMilliseconds(2) };
            using var cache = new RemoteMediaCache(_dir, host);
            var fb = cache.Add(host.Add("b", b), host.Length("b"));
            var fa = cache.Add(host.Add("a", a), host.Length("a"));
            cache.SetPriority(["a", "b"]);
            await fb.WaitAsync(0, fb.Length, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fa.IsComplete);
            List<string> order;
            lock (host.Requests) order = host.Requests.Select(r => r.Id).ToList();
            // b was asked for (urgently, by the wait) but otherwise a went first
            int lastA = order.LastIndexOf("a");
            Assert.True(order.Take(lastA).Count(id => id == "b") <= 4, string.Join(",", order));
        }
        finally { Directory.Delete(_dir + "-src", true); }
    }

    [Fact]
    public void Dispose_unblocks_readers_and_deletes_the_files()
    {
        var src = Path.Combine(_dir + "-src", "x.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllBytes(src, new byte[RemoteMediaCache.ChunkSize * 4]);
        try
        {
            var host = new FakeHost { Fail = true };
            var cache = new RemoteMediaCache(_dir, host);
            var f = cache.Add(host.Add("x", src), host.Length("x"));
            var reader = Task.Run(() => f.OpenRead().ReadByte());
            Thread.Sleep(100);
            Assert.False(reader.IsCompleted); // waiting for bytes that cannot arrive yet
            cache.Dispose();
            Assert.ThrowsAny<IOException>(() => reader.GetAwaiter().GetResult());
            Assert.False(Directory.Exists(_dir));
        }
        finally { Directory.Delete(_dir + "-src", true); }
    }
}
