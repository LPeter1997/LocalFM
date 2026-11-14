using LocalFM.App;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Import;
using LocalFM.Native;
using LocalFM.Playback;

namespace LocalFM.UI;

/// <summary>Composition root: builds and owns every service for the lifetime of the app.</summary>
public sealed class AppHost : IDisposable
{
    public AppHost()
    {
        Paths = AppPaths.Default();
        Db = new Database(Paths.Database);
        Images = new ImageStore(Paths.ImagesDir);
        Queries = new LibraryQueries(Db, Images);
        Playlists = new Playlists(Db);
        History = new History(Db);
        Scanner = new Scanner(Db, Images);
        Watcher = new FolderWatcher(Scanner);

        try
        {
            // LOCALFM_OUTPUT=null plays silently in real time (headless runs, UI checks).
            var mode = Environment.GetEnvironmentVariable("LOCALFM_OUTPUT") == "null" ? OutputMode.Null : OutputMode.Device;
            Engine = new PlaybackEngine(mode);
        }
        catch (Exception ex)
        {
            // No sound device must not keep the library from working; play into the void and say so.
            Log.Error("Host", "No audio device; using silent output", ex);
            StartupWarning = "No audio output device was found. Playback is silent until one is available and LocalFM is restarted.";
            Engine = new PlaybackEngine(OutputMode.Null);
        }
        Player = new PlayerService(Engine, History, Queries);
    }

    public AppPaths Paths { get; }
    public Database Db { get; }
    public ImageStore Images { get; }
    public LibraryQueries Queries { get; }
    public Playlists Playlists { get; }
    public History History { get; }
    public Scanner Scanner { get; }
    public FolderWatcher Watcher { get; }
    public PlaybackEngine Engine { get; }
    public PlayerService Player { get; }
    public string? StartupWarning { get; }

    /// <summary>Restores the queue and starts background scanning/watching of all sources.</summary>
    public void Start(IReadOnlyList<string>? addFolders = null)
    {
        Player.RestoreState();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var f in addFolders ?? [])
                    if (Directory.Exists(f)) await Sources.AddFolderAsync(Db, f).ConfigureAwait(false);
                foreach (var s in Sources.List(Db).Where(s => s.Enabled)) Watcher.Watch(s);
                await Scanner.ScanAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Error("Host", "Startup scan failed", ex); }
        });
    }

    public void Dispose()
    {
        Watcher.Dispose();
        Player.Dispose();
        Engine.Dispose();
        Db.Dispose();
    }
}
