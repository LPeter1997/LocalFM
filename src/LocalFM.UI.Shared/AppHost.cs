using LocalFM.App;
using LocalFM.Core;
using LocalFM.Ingest;
using LocalFM.Library;
using LocalFM.Library.Import;
using LocalFM.Library.Edit;
using LocalFM.Metadata;
using LocalFM.Native;
using LocalFM.Playback;
using LocalFM.Vibe;
using LocalFM.Vibe.Library;
using LocalFM.Vibe.Net;
using LocalFM.Vibe.Web;

namespace LocalFM.UI;

/// <summary>Composition root: builds and owns every service for the lifetime of the app.</summary>
public sealed class AppHost : IDisposable
{
    public AppHost(PlatformCapabilities? capabilities = null, AppPaths? paths = null)
    {
        Capabilities = capabilities ?? PlatformCapabilities.Desktop;
        Paths = paths ?? AppPaths.Default();
        LogFile = StartLog(Paths);
        Db = new Database(Paths.Database);
        Images = new ImageStore(Paths.ImagesDir);
        Queries = new LibraryQueries(Db, Images);
        Playlists = new Playlists(Db);
        History = new History(Db);
        Scanner = new Scanner(Db, Images);
        Watcher = new FolderWatcher(Scanner);
        Metadata = new MetadataService(Db, Images, History);
        Import = new ImportService(Db, Paths, Scanner, Watcher, History);

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
        Sound = SoundSettings.Load(History);
        Player.ApplySound(Sound);
        Log.Info("Session", $"Audio: {Engine.OutputDescription}");
        Log.Info("Session", $"Sound: EQ {(Sound.EqEnabled ? $"on ({Sound.Preset}, preamp {Sound.EffectivePreampDb:+0.#;-0.#;0} dB)" : "off")}, " +
                            $"crossfade {Sound.CrossfadeSeconds:0.#} s, gapless albums {(Sound.GaplessAlbums ? "on" : "off")}, ReplayGain {Sound.ReplayGain}");
        Vibe = new VibeService(Player, Paths,
            async (track, ct) => (await Metadata.GetLyricsAsync(track, allowFetch: true, ct).ConfigureAwait(false))?.Data);
        Share = new LibraryShareService(
            new LibraryApi(Db, Images, Queries, Playlists, new MediaResolver(Db, Images, Queries),
                (track, ct) => Metadata.GetLyricsAsync(track, allowFetch: true, ct)),
            History);
    }

    public VibeService Vibe { get; }

    /// <summary>The library share for TVs and browsers (desktop only).</summary>
    public LibraryShareService Share { get; }

    /// <summary>This run's log file, or null when it could not be created (then logs go to stderr only).</summary>
    public LogFile? LogFile { get; }

    private static LogFile? StartLog(AppPaths paths)
    {
        if (Environment.GetEnvironmentVariable("LOCALFM_LOG") is { } level && Enum.TryParse<LogLevel>(level, ignoreCase: true, out var min))
            Log.MinimumLevel = min;
        LogFile? file = null;
        try
        {
            file = LogFile.Open(paths.LogsDir);
            Log.Sink = file.Write;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Session", $"No log file in {paths.LogsDir}", ex);
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Session", "Crashed", e.ExceptionObject as Exception);
            file?.Flush(TimeSpan.FromSeconds(2)); // the process is going down: get the reason on disk
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Warn("Session", "Unobserved task error", e.Exception);
        Log.Info("Session", $"LocalFM {AppInfo.Version} started (pid {Environment.ProcessId}), " +
                            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                            $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, .NET {Environment.Version}, data {paths.Root}");
        return file;
    }

    public PlatformCapabilities Capabilities { get; }

    public SoundSettings Sound { get; }

    public AppPaths Paths { get; }
    public Database Db { get; }
    public ImageStore Images { get; }
    public LibraryQueries Queries { get; }
    public Playlists Playlists { get; }
    public History History { get; }
    public Scanner Scanner { get; }
    public FolderWatcher Watcher { get; }
    public MetadataService Metadata { get; }
    public ImportService Import { get; }
    public PlaybackEngine Engine { get; }
    public PlayerService Player { get; }
    public string? StartupWarning { get; }

    private readonly CancellationTokenSource _shutdown = new();
    /// <summary>Cancelled when the app closes: long background work (online checks) stops with it.</summary>
    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Restores the queue and starts background scanning/watching of all sources.</summary>
    public void Start(IReadOnlyList<string>? addFolders = null, VibeStart? vibe = null)
    {
        Player.RestoreState();
        // "Restart now" for an update: carry on where the music was.
        if (History.Get(ViewModels.UpdaterViewModel.ResumeKey) == "1")
        {
            _ = History.SetAsync(ViewModels.UpdaterViewModel.ResumeKey, "");
            Log.Info("Host", "Updated: playing on where the music was");
            Player.Play("update restart");
        }
        if (vibe is not null) _ = StartVibeAsync(vibe);
        if (Capabilities.ShareLibrary) Share.Restore();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var f in addFolders ?? [])
                    if (Directory.Exists(f)) await Sources.AddFolderAsync(Db, f).ConfigureAwait(false);
                if (Capabilities.WatchFolders)
                    foreach (var s in Sources.List(Db).Where(s => s.Enabled)) Watcher.Watch(s);
                await Scanner.ScanAllAsync().ConfigureAwait(false);
                await Db.Writer.Run((c, t) => FeaturedArtistRepair.Run(new Tx(c, t))).ConfigureAwait(false);
                if (Capabilities.Import) Import.Start(); // after the startup scan, so resumed imports do not compete with it
            }
            catch (Exception ex) { Log.Error("Host", "Startup scan failed", ex); }
        });
    }

    /// <summary>Where folders are not watched (Android): look for changes when the app comes back.</summary>
    public void Rescan()
    {
        if (Interlocked.Exchange(ref _rescanning, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Scanner.ScanAllAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("Host", "Rescan failed", ex); }
            finally { Volatile.Write(ref _rescanning, 0); }
        });
    }

    private int _rescanning;

    /// <summary>--vibe-host / --vibe-join: handy for trying two instances. LOCALFM_VIBE_MAP=0 leaves the router alone.</summary>
    private async Task StartVibeAsync(VibeStart start)
    {
        try
        {
            if (start.JoinCode is { } code)
            {
                if (!Invite.TryDecode(code, out var invite)) { Log.Warn("Host", "--vibe-join: not a vibe invite"); return; }
                await Vibe.JoinAsync(invite).ConfigureAwait(false);
            }
            else
            {
                bool map = Environment.GetEnvironmentVariable("LOCALFM_VIBE_MAP") != "0";
                var host = await Vibe.StartHostingAsync(mapPort: map).ConfigureAwait(false);
                string text = host.Invite.Encode();
                Log.Info("Host", $"Vibe invite: {text}");
                Console.WriteLine($"Vibe invite: {text}");
            }
        }
        catch (Exception ex) { Log.Error("Host", "Could not start the vibe", ex); }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { Vibe.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { Share.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        Import.Dispose();
        Watcher.Dispose();
        Player.Dispose();
        Engine.Dispose();
        Db.Dispose();
        Log.Info("Session", "Closed");
        if (LogFile is { } log)
        {
            Log.Sink = (level, area, msg, ex) => Console.Error.WriteLine(LogFile.Format(DateTime.Now, level, area, msg, ex));
            log.Dispose();
        }
    }
}

/// <summary>A vibe to start with the app: host one (no code) or join one.</summary>
public sealed record VibeStart(string? JoinCode);
