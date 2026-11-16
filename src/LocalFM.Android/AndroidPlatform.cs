using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.UI;
using LocalFM.UI.Updates;
using LocalFM.UI.ViewModels;
using LocalFM.UI.Views;
using LocalFM.Vibe;
using AndroidLog = Android.Util.Log;
using AndroidUri = Android.Net.Uri;

namespace LocalFM.Android;

/// <summary>The phone: one view, the music folders on shared storage, a release notice instead of updates.</summary>
public sealed class AndroidPlatform : IAppPlatform
{
    public static AndroidPlatform Instance { get; } = new();

    private const string LogTag = "LocalFM";
    private const int PermissionRequest = 1;

    private AppHost? _host;
    private MainViewModel? _vm;
    private MainView? _view;
    private WeakReference<Activity>? _activity;
    private bool _resumedOnce, _askedOnce, _blocked;

    private AndroidPlatform()
    {
        Log.Sink = (level, area, msg, ex) =>
        {
            string text = $"{area}: {msg}{(ex is null ? "" : " — " + ex)}";
            switch (level)
            {
                case LogLevel.Error: AndroidLog.Error(LogTag, text); break;
                case LogLevel.Warning: AndroidLog.Warn(LogTag, text); break;
                case LogLevel.Debug: AndroidLog.Debug(LogTag, text); break;
                default: AndroidLog.Info(LogTag, text); break;
            }
        };
    }

    public PlatformCapabilities Capabilities => PlatformCapabilities.Phone;

    public AppHost? Host => _host;

    /// <summary>The default music folder on shared storage.</summary>
    private static string MusicFolder =>
        global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryMusic)?.AbsolutePath
        ?? "/storage/emulated/0/Music";

    private static Context Context => global::Android.App.Application.Context;

    public IUpdateService CreateUpdateService() => new ReleaseNoticeService(AppInfo.Version,
        "New versions are on GitHub as an APK; you are told when there is one, and installing it keeps your library.");

    public void Launch(LocalFM.UI.App app, IApplicationLifetime? lifetime)
    {
        if (lifetime is not ISingleViewApplicationLifetime single) return;
        if (_host is null)
        {
            VibeService.DefaultName = DeviceName();
            _host = new AppHost(Capabilities, new AppPaths(Path.Combine(Context.FilesDir!.AbsolutePath, "LocalFM")));
            _vm = new MainViewModel(_host);
            _view = new MainView { DataContext = _vm };
            if (HasMusicPermission()) AddDefaultSourceIfNone();
            _host.Start();
            _vm.Start();
            MediaPlaybackService.Watch(_host.Player, _host.Vibe);
            WatchNetwork(_host, _vm);
        }
        single.MainView = _view;
    }

    /// <summary>"Pixel 7", or the manufacturer and model when the model alone says little.</summary>
    private static string DeviceName()
    {
        string model = Build.Model ?? "";
        string maker = Build.Manufacturer ?? "";
        if (model.Length == 0) return "Phone";
        return maker.Length == 0 || model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Equals("google", StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{char.ToUpperInvariant(maker[0])}{maker[1..]} {model}";
    }

    // ------------------------------------------------------------------ vibes

    private NetworkLocks? _locks;

    private void WatchNetwork(AppHost host, MainViewModel vm)
    {
        _locks = new NetworkLocks(Context);
        void Update() => Dispatcher.UIThread.Post(() =>
            _locks.Update(discovering: vm.Vibe.IsOpen || host.Vibe.VisibleOnNetwork, inVibe: NetworkLocks.InVibe(host.Vibe)));
        host.Vibe.Changed += Update;
        vm.Vibe.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VibeViewModel.IsOpen)) Update(); };
    }

    public bool CanShare => true;

    public void Share(string text)
    {
        if (_activity is null || !_activity.TryGetTarget(out var activity)) return;
        var send = new Intent(Intent.ActionSend).SetType("text/plain")!.PutExtra(Intent.ExtraText, text)!;
        activity.StartActivity(Intent.CreateChooser(send, "Share the invite"));
    }

    // ------------------------------------------------------------------ activity

    public void ActivityCreated(Activity activity, Intent? intent)
    {
        _activity = new WeakReference<Activity>(activity);
        // The first start asks right away; after that the Sources page has the button.
        if (!HasMusicPermission() && !_askedOnce) RequestMusicPermission(activity);
        if (intent?.GetStringExtra("LOCALFM_SNAPSHOT") is { Length: > 0 }) RunSnapshot(intent);
    }

    /// <summary>Folders are not watched: changes are picked up when the app comes back.</summary>
    public void Resumed()
    {
        if (!_resumedOnce) { _resumedOnce = true; return; } // the start already scans
        // Back from the app's settings, perhaps with access granted there.
        if (_vm?.Sources is { MusicAccessMissing: true }) { PermissionsChanged(); return; }
        if (HasMusicPermission()) _host?.Rescan();
    }

    public void PermissionsChanged()
    {
        bool granted = HasMusicPermission();
        // Refused, and Android will not show the prompt again: the button opens the app's settings instead.
        _blocked = !granted && _activity?.TryGetTarget(out var activity) == true
                   && !activity.ShouldShowRequestPermissionRationale(MusicPermission);
        Dispatcher.UIThread.Post(() => _vm?.Sources.RefreshMusicAccess());
        if (!granted || _host is null) return;
        AddDefaultSourceIfNone();
        _host.Rescan();
        Dispatcher.UIThread.Post(() =>
        {
            _vm?.Sources.RefreshMusicAccess();
            // The first run lands on Sources while there is no folder; go to the music once there is one.
            if (_vm is { CurrentPage: SourcesViewModel } vm) vm.Navigate(vm.Nav[0]);
        });
    }

    public bool HasMusicAccess => HasMusicPermission();

    public void RequestMusicAccess()
    {
        if (_activity is null || !_activity.TryGetTarget(out var activity)) return;
        if (!_blocked) { RequestMusicPermission(activity); return; }
        var settings = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                                  AndroidUri.FromParts("package", activity.PackageName, null));
        activity.StartActivity(settings);
    }

    private static string MusicPermission =>
        Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu ? Manifest.Permission.ReadMediaAudio : Manifest.Permission.ReadExternalStorage;

    private static bool HasMusicPermission() => Context.CheckSelfPermission(MusicPermission) == Permission.Granted;

    private void RequestMusicPermission(Activity activity)
    {
        _askedOnce = true;
        activity.RequestPermissions([MusicPermission], PermissionRequest);
    }

    private void AddDefaultSourceIfNone()
    {
        if (_host is null || Sources.List(_host.Db).Count > 0) return;
        string music = MusicFolder;
        if (!Directory.Exists(music)) return;
        try { Sources.AddFolderAsync(_host.Db, music).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn("Android", $"Could not add {music}", ex); }
    }

    // ------------------------------------------------------------------ pickers

    /// <summary>Folders: a document tree on shared storage maps to its path. Files: copied into the cache, since
    /// a content URI has no path (a knowledge base file is read once).</summary>
    public async Task<string?> LocalPathAsync(IStorageItem item)
    {
        if (item.TryGetLocalPath() is { } local) return local;
        if (item is IStorageFolder) return TreeUriToPath(item.Path);
        if (item is IStorageFile file)
        {
            string dir = Path.Combine(Context.CacheDir!.AbsolutePath, "picked");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(file.Name) is { Length: > 0 } n ? n : "picked");
            await using var input = await file.OpenReadAsync();
            await using var output = File.Create(target);
            await input.CopyToAsync(output);
            return target;
        }
        return null;
    }

    /// <summary>content://com.android.externalstorage.documents/tree/primary%3AMusic%2FRock → /storage/emulated/0/Music/Rock;
    /// an SD card's "1234-ABCD:Music" → /storage/1234-ABCD/Music.</summary>
    internal static string? TreeUriToPath(Uri uri)
    {
        if (uri.Scheme != "content" || uri.Host != "com.android.externalstorage.documents") return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int tree = Array.IndexOf(segments, "tree");
        if (tree < 0 || tree + 1 >= segments.Length) return null;
        string docId = Uri.UnescapeDataString(segments[tree + 1]);
        int colon = docId.IndexOf(':');
        if (colon < 0) return null;
        string volume = docId[..colon], rel = docId[(colon + 1)..];
        string root = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0"
            : "/storage/" + volume;
        return rel.Length == 0 ? root : Path.Combine(root, rel);
    }

    // ------------------------------------------------------------------ snapshot mode

    /// <summary>The ui-review skill's Android shots: the same LOCALFM_SNAPSHOT_* settings as intent extras; the
    /// script (snapshot-android.sh) takes the screenshot once "snapshot ready" is logged.</summary>
    private void RunSnapshot(Intent intent)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var key in intent.Extras?.KeySet() ?? [])
            if (key.StartsWith("LOCALFM_SNAPSHOT", StringComparison.Ordinal)) settings[key] = intent.GetStringExtra(key);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_vm is null || _view is null) return;
                await SnapshotScript.RunAsync(_vm, _view, k => settings.GetValueOrDefault(k));
            }
            catch (Exception ex) { Log.Error("Snapshot", "Snapshot script failed", ex); }
            AndroidLog.Info(LogTag, "snapshot ready");
        });
    }
}
