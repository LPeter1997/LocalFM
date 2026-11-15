using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Native;
using LocalFM.Playback;
using LocalFM.Vibe;
using LocalFM.Vibe.Net;

// Headless vibe harness (no library, no UI), for trying vibes between machines:
//   LocalFM.VibeCli host [--null] [--port N] [--no-map] [--lan] file1 file2 ...   prints an invite, plays the files (--lan: visible on the network)
//   LocalFM.VibeCli join [--null] <invite>                     follows that vibe
// --debug shows sync decisions.
if (args.Length < 2 || args[0] is not ("host" or "join"))
{
    Console.Error.WriteLine("usage: LocalFM.VibeCli host [--null] [--port N] <files...> | join [--null] <invite>");
    return 1;
}
if (args.Contains("--debug")) Log.MinimumLevel = LogLevel.Debug;
var mode = args.Contains("--null") ? OutputMode.Null : OutputMode.Device;
int port = args.SkipWhile(a => a != "--port").Skip(1).Select(int.Parse).FirstOrDefault(TlsTransport.DefaultPort);
var rest = args.Skip(1).Where((a, i) => !a.StartsWith("--") && (i == 0 || args[i] != "--port")).ToList();

string home = Path.Combine(Path.GetTempPath(), "localfm-vibecli-" + Environment.ProcessId);
var paths = new AppPaths(home);
using var db = new Database(paths.Database);
var queries = new LibraryQueries(db, new ImageStore(paths.ImagesDir));
using var engine = new PlaybackEngine(mode);
using var player = new PlayerService(engine, new History(db), queries);
await using var vibe = new VibeService(player, paths);
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };

if (args[0] == "host")
{
    var tracks = rest.Select((f, i) => new TrackInfo(Ulid.NewUlid(), Ulid.NewUlid(), Ulid.NewUlid(), Path.GetFullPath(f),
        Path.GetFileNameWithoutExtension(f), "VibeCli", "Files", null, Ulid.Empty, null, 1, i + 1, null, null, null,
        null, null, null, null)).ToList();
    var host = await vibe.StartHostingAsync("VibeCli", port, mapPort: !args.Contains("--no-map"));
    Console.WriteLine($"Invite: {host.Invite.Encode()}");
    Console.WriteLine($"Listening on port {host.Port}; reachable at: {string.Join(", ", host.Invite.Endpoints)}");
    vibe.Changed += () => { if (vibe.Reachability != Reachability.Checking) Console.WriteLine($"\nReachability: {vibe.Reachability} {vibe.Mapping?.EndPoint}"); };
    host.ListenersChanged += () => Console.WriteLine($"\nListeners: {string.Join(", ", host.Listeners.Select(l => l.Name))}");
    if (args.Contains("--lan")) vibe.SetVisibleOnNetwork(true);
    player.PlayTracks(tracks, 0);
}
else
{
    if (!Invite.TryDecode(rest[0], out var invite)) { Console.Error.WriteLine("Not a vibe invite."); return 1; }
    vibe.Changed += () =>
    {
        if (vibe.Role == VibeRole.None) { Console.WriteLine($"\nEnded: {vibe.EndedReason}"); stop.TrySetResult(); }
        else if (vibe.Reconnecting) Console.WriteLine("\nReconnecting…");
    };
    await vibe.JoinAsync(invite, "VibeCli");
    Console.WriteLine($"Listening to {vibe.HostName}");
}

while (!stop.Task.IsCompleted)
{
    await Task.WhenAny(stop.Task, Task.Delay(500));
    var err = vibe.Follower?.LastError is { } e ? $" off {e.TotalMilliseconds,5:F0} ms" : "";
    Console.Write($"\r{player.Current?.Track.Title,-30} {player.Position:mm\\:ss\\.f} {player.State,-8}{err}   ");
}
Console.WriteLine();
try { Directory.Delete(home, true); } catch { }
return 0;
