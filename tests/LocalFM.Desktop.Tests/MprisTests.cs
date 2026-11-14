using System.Diagnostics;
using LocalFM.App;
using LocalFM.Library.Tests;
using LocalFM.Native;
using LocalFM.Playback;

namespace LocalFM.Desktop.Tests;

/// <summary>Runs the MPRIS service on a private bus and drives it with gdbus, as a desktop would.</summary>
public sealed class MprisTests : IDisposable
{
    private readonly Process? _bus;
    private readonly string? _address;

    public MprisTests()
    {
        if (!OperatingSystem.IsLinux() || !Exists("dbus-daemon") || !Exists("gdbus")) return;
        _bus = Process.Start(new ProcessStartInfo("dbus-daemon", "--session --nofork --print-address=1")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        _address = _bus?.StandardOutput.ReadLine();
    }

    public void Dispose()
    {
        try { _bus?.Kill(); } catch (InvalidOperationException) { }
        _bus?.Dispose();
    }

    private static bool Exists(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, tool)));

    private string Call(string method, params string[] args)
    {
        var psi = new ProcessStartInfo("gdbus") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "call", "--address", _address!, "--dest", MprisSession.BusName, "--object-path", MprisSession.ObjectPathName, "--method", method })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    private string Prop(string iface, string name) => Call("org.freedesktop.DBus.Properties.Get", iface, name);

    private static void Until(Func<bool> cond)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!cond()) { Assert.True(DateTime.UtcNow < deadline, "timed out"); Thread.Sleep(20); }
    }

    [SkippableFact]
    public async Task Desktop_can_read_and_control_playback()
    {
        Skip.If(_address is null || !TempLibrary.HaveFfmpeg, "needs dbus-daemon, gdbus and ffmpeg");
        using var lib = new TempLibrary();
        lib.MakeTrack("a/1.flac", new() { ["title"] = "First", ["artist"] = "Band", ["album"] = "Record", ["track"] = "1" }, seconds: 5);
        lib.MakeTrack("a/2.flac", new() { ["title"] = "Second", ["artist"] = "Band", ["album"] = "Record", ["track"] = "2" }, seconds: 5);
        await lib.ScanAsync();
        using var engine = new PlaybackEngine(OutputMode.Null);
        using var player = new PlayerService(engine, new LocalFM.Library.History(lib.Db), lib.Queries);
        bool raised = false;
        using var session = await MprisSession.StartAsync(player, () => raised = true, () => { }, _address);
        Assert.NotNull(session);

        Assert.Contains("Stopped", Prop("org.mpris.MediaPlayer2.Player", "PlaybackStatus"));
        player.PlayTracks(lib.Queries.AlbumTracks(lib.Queries.Albums().Single().ReleaseId));
        Until(() => player.State == PlaybackState.Playing);

        string meta = Prop("org.mpris.MediaPlayer2.Player", "Metadata");
        Assert.Contains("'xesam:title': <'First'>", meta);
        Assert.Contains("'xesam:artist': <['Band']>", meta);

        var trackId = System.Text.RegularExpressions.Regex.Match(meta, @"'mpris:trackid': <objectpath '([^']+)'>").Groups[1].Value;
        Assert.StartsWith("/org/localfm/track/", trackId);
        TimeSpan? seeked = null;
        player.Seeked += p => seeked = p;
        Call("org.mpris.MediaPlayer2.Player.SetPosition", trackId, "3000000");
        Until(() => seeked is not null);
        Assert.Equal(3, seeked!.Value.TotalSeconds, 1);
        Call("org.mpris.MediaPlayer2.Player.SetPosition", "/org/localfm/track/other", "1000000");   // not the current track: ignored
        Assert.True(player.Position.TotalSeconds >= 2.9);

        Call("org.mpris.MediaPlayer2.Player.Pause");
        Until(() => player.State == PlaybackState.Paused);
        Assert.Contains("Paused", Prop("org.mpris.MediaPlayer2.Player", "PlaybackStatus"));

        Call("org.mpris.MediaPlayer2.Player.Next");
        Until(() => player.Current?.Track.Title == "Second");

        Call("org.freedesktop.DBus.Properties.Set", "org.mpris.MediaPlayer2.Player", "Volume", "<0.25>");
        Assert.Equal(0.25f, player.Volume, 3);
        Call("org.freedesktop.DBus.Properties.Set", "org.mpris.MediaPlayer2.Player", "LoopStatus", "<'Track'>");
        Assert.Equal(RepeatMode.One, player.Repeat);

        Call("org.mpris.MediaPlayer2.Raise");
        Assert.True(raised);
        Assert.Contains("UnknownMethod", Call("org.mpris.MediaPlayer2.Player.Nope"));
    }
}
