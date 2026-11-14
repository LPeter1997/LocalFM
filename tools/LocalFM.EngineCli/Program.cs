using LocalFM.Native;
using LocalFM.Playback;

// Headless harness: plays the given files back to back through the engine.
//   LocalFM.EngineCli [--null] file1 file2 ...
var files = args.Where(a => !a.StartsWith("--")).ToList();
var mode = args.Contains("--null") ? OutputMode.Null : OutputMode.Device;
if (files.Count == 0) { Console.Error.WriteLine("usage: LocalFM.EngineCli [--null] <files...>"); return 1; }

using var engine = new PlaybackEngine(mode);
Console.WriteLine($"output: {engine.SampleRate} Hz ({mode})");
var handles = files.Select((f, i) => new TrackHandle(Path.GetFullPath(f), i)).ToList();
var done = new TaskCompletionSource();

_ = Task.Run(async () =>
{
    await foreach (var e in engine.Events.ReadAllAsync())
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {e}");
        if (e is TrackStarted ts)
        {
            int i = (int)ts.Track.Token!;
            engine.SetNext(i + 1 < handles.Count ? handles[i + 1] : null);
        }
        else if (e is PlaybackFinished) done.TrySetResult();
    }
});

engine.Play(handles[0]);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
while (!done.Task.IsCompleted)
{
    await Task.WhenAny(done.Task, Task.Delay(1000));
    Console.Write($"\r{engine.Position:mm\\:ss} / {engine.Duration:mm\\:ss}  underruns={engine.Underruns}   ");
}
Console.WriteLine();
return 0;
