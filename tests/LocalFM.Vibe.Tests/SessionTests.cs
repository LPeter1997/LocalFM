using System.Net;
using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Enrich;
using LocalFM.Library.Tests;
using LocalFM.Native;
using LocalFM.Playback;
using LocalFM.Vibe.Client;
using LocalFM.Vibe.Host;
using LocalFM.Vibe.Net;

namespace LocalFM.Vibe.Tests;

/// <summary>A host and a listener in one process, over real TLS on loopback, with real-time silent outputs.</summary>
public sealed class SessionTests : IDisposable
{
    private readonly TempLibrary _hostLib = new(), _listenerLib = new();
    private readonly List<IDisposable> _dispose = new();

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_dispose)) d.Dispose();
        _hostLib.Dispose();
        _listenerLib.Dispose();
    }

    private static Dictionary<string, string> Tags(string title, int n) =>
        new() { ["title"] = title, ["artist"] = "X", ["album"] = "Y", ["track"] = n.ToString() };

    private async Task<IReadOnlyList<TrackInfo>> Album(TempLibrary lib, string prefix, int count, double seconds)
    {
        for (int i = 1; i <= count; i++) lib.MakeTrack($"{prefix}/{i:00}.flac", Tags($"{prefix}{i}", i), seconds);
        await lib.ScanAsync();
        return lib.Queries.AlbumTracks(lib.Queries.Albums().Single().ReleaseId);
    }

    private (PlayerService Player, History History) Player(TempLibrary lib)
    {
        var engine = new PlaybackEngine(OutputMode.Null);
        var history = new History(lib.Db);
        var player = new PlayerService(engine, history, lib.Queries);
        _dispose.Add(engine);
        _dispose.Add(player);
        return (player, history);
    }

    private static Invite Loopback(VibeHost host) => host.Invite with { Addresses = [IPAddress.Loopback] };

    private static async Task Until(Func<bool> cond, double seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>Host minus listener, in what the speakers play (sampled together).</summary>
    private static TimeSpan Offset(PlayerService host, PlayerService listener) =>
        (host.Position - host.OutputLatency) - (listener.Position - listener.OutputLatency);

    [SkippableFact]
    public async Task Listener_follows_the_host_and_gets_its_own_queue_back()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var hostTracks = await Album(_hostLib, "h", 3, 4);
        var ownTracks = await Album(_listenerLib, "own", 2, 2);
        var (host, _) = Player(_hostLib);
        var (listener, listenerHistory) = Player(_listenerLib);
        listener.PlayTracks(ownTracks, 1);
        await Until(() => listener.State == PlaybackState.Playing, 5, "own track plays");
        await Task.Delay(300);
        listener.Pause();
        await Until(() => listener.State == PlaybackState.Paused, 5, "own track paused");
        await Task.Delay(300); // the device's last take plays out
        var ownPosition = listener.Position;

        host.PlayTracks(hostTracks, 0);
        await using var vibe = new VibeHost(host, "Host", port: 0);
        var cacheDir = Path.Combine(_listenerLib.Root, "vibe-cache");
        await using var client = await VibeClient.ConnectAsync(Loopback(vibe), "Friend");
        var follower = new Follower(client, listener, cacheDir);
        try
        {
            await Until(() => vibe.Listeners.Count == 1, 5, "host sees the listener");
            Assert.Equal("Friend", vibe.Listeners.Single().Name);

            // Same item, same moment.
            await Until(() => listener.AudibleItem == host.Current!.Id && listener.State == PlaybackState.Playing, 10, "listener plays the host's track");
            Assert.Equal(host.Snapshot(out _).Select(i => i.Id), listener.Snapshot(out _).Select(i => i.Id));
            Assert.Equal("h1", listener.Current!.Track.Title);
            await Task.Delay(500);
            Assert.InRange(Offset(host, listener).TotalMilliseconds, -80, 80);

            // Listeners cannot steer.
            listener.Next();
            listener.Seek(TimeSpan.Zero);
            await Task.Delay(300);
            Assert.Equal(host.Current!.Id, listener.AudibleItem);

            // The host seeks: the listener jumps along.
            host.Seek(TimeSpan.FromSeconds(3));
            await Until(() => listener.Position > TimeSpan.FromSeconds(2.9) && listener.Position < TimeSpan.FromSeconds(4), 5, "listener follows the seek");
            await Task.Delay(300);
            Assert.InRange(Offset(host, listener).TotalMilliseconds, -80, 80);

            // The host moves on to the next track (gaplessly on both sides).
            await Until(() => host.Current!.Track.Title == "h2", 5, "host reaches track 2");
            await Until(() => listener.Current?.Track.Title == "h2", 5, "listener reaches track 2");
            await Task.Delay(300);
            Assert.InRange(Offset(host, listener).TotalMilliseconds, -80, 80);

            // The host pauses and resumes.
            host.Pause();
            await Until(() => listener.State == PlaybackState.Paused, 5, "listener pauses");
            host.Play();
            await Until(() => listener.State == PlaybackState.Playing, 5, "listener resumes");
        }
        finally { await follower.DisposeAsync(); }

        // Back to our own queue, where we left it; the vibe left no trace in our history or disk.
        Assert.False(listener.IsFollowing);
        Assert.Equal(ownTracks.Select(t => t.TrackId), listener.Snapshot(out int idx).Select(i => i.Track.TrackId));
        Assert.Equal(1, idx);
        Assert.Equal(PlaybackState.Stopped, listener.State);
        Assert.InRange((listener.Position - ownPosition).Duration().TotalMilliseconds, 0, 50);
        await Task.Delay(200);
        using (var conn = _listenerLib.Db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM play_event";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
        Assert.False(Directory.Exists(cacheDir));
    }

    [SkippableFact]
    public async Task Listener_gets_covers_lyrics_and_who_is_there()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        _hostLib.MakeTrack("c/1.flac", Tags("Covered", 1), 3, coverPng: _hostLib.MakePng());
        await _hostLib.ScanAsync();
        var tracks = _hostLib.Queries.AlbumTracks(_hostLib.Queries.Albums().Single().ReleaseId);
        Assert.NotNull(tracks[0].CoverPath);
        var (host, _) = Player(_hostLib);
        var (listener, _) = Player(_listenerLib);
        host.PlayTracks(tracks, 0);
        LyricsData? Lyrics(TrackInfo t) => t.Title == "Covered" ? new LyricsData("lrclib", LyricsFormat.Lrc, "[00:01.00]Hello\n[00:02.00]World") : null;
        await using var vibe = new VibeHost(host, "Host", port: 0, lyrics: (t, _) => Task.FromResult(Lyrics(t)));
        await using var client = await VibeClient.ConnectAsync(Loopback(vibe), "Friend");
        var follower = new Follower(client, listener, Path.Combine(_listenerLib.Root, "vibe-cache"));
        try
        {
            await Until(() => listener.Current?.Track.CoverPath is { } p && File.Exists(p), 10, "the cover arrives");
            Assert.Equal(File.ReadAllBytes(tracks[0].CoverPath!), File.ReadAllBytes(listener.Current!.Track.CoverPath!));

            var lyrics = await follower.LyricsAsync(listener.Current!.Track, CancellationToken.None);
            Assert.NotNull(lyrics);
            Assert.True(lyrics.Synced);
            Assert.Equal(["Hello", "World"], lyrics.Lines.Select(l => l.Text));

            await Until(() => follower.People.Count == 2, 5, "presence");
            Assert.Equal(["Host", "Friend"], follower.People);
        }
        finally { await follower.DisposeAsync(); }
    }

    [SkippableFact]
    public async Task Wrong_secret_is_rejected_and_ending_the_vibe_tells_listeners()
    {
        Skip.IfNot(TempLibrary.HaveFfmpeg);
        var (host, _) = Player(_hostLib);
        var vibe = new VibeHost(host, "Host", port: 0);
        try
        {
            var bad = Loopback(vibe) with { Secret = new byte[16] };
            var ex = await Assert.ThrowsAsync<VibeRejectedException>(() => VibeClient.ConnectAsync(bad, "Eve"));
            Assert.Contains("not valid", ex.Message);

            var wrongHost = Loopback(vibe) with { Fingerprint = new byte[32] };
            await Assert.ThrowsAsync<IOException>(() => VibeClient.ConnectAsync(wrongHost, "Eve"));

            await using var client = await VibeClient.ConnectAsync(Loopback(vibe), "Friend");
            var ended = new TaskCompletionSource<string>();
            client.Ended += r => ended.TrySetResult(r);
            await Until(() => vibe.Listeners.Count == 1, 5, "joined");
            await vibe.DisposeAsync();
            Assert.Equal("The host ended the vibe.", await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { await vibe.DisposeAsync(); }
    }
}
