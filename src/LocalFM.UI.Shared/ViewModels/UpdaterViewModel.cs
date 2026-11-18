using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.UI.Updates;

namespace LocalFM.UI.ViewModels;

/// <summary>What the updater needs from the app (MainViewModel; a fake in tests).</summary>
public interface IUpdaterHost
{
    string? Get(string key);
    void Set(string key, string value);
    void Notify(string message);
    /// <summary>Music is playing: restarting interrupts it for a moment.</summary>
    bool IsPlaying { get; }
    /// <summary>Hosting or listening to a vibe: restarting would end it (for everyone, when hosting).</summary>
    bool InVibe { get; }
    /// <summary>Pauses, saves the queue and position, and (when music was playing) asks the next start to resume.
    /// Completes once that is on disk: the update ends the process right after.</summary>
    Task PrepareRestartAsync();
    Task OpenUrlAsync(string url);
}

/// <summary>
/// The update banner and the Updates section (ported from CheeseGit): "When I quit" (download now, install once
/// LocalFM quits), "Restart now" (install and resume where the music was), Skip, and What's new once the new
/// version runs. Distro builds only get a notice with a link. Nothing here stops the music unasked.
/// </summary>
public sealed partial class UpdaterViewModel(IUpdaterHost host, IUpdateService service, string currentVersion) : ObservableObject
{
    public const string CheckKey = "update.check";
    public const string SkippedKey = "update.skipped";
    public const string NotesKey = "update.notes";
    public const string NotesVersionKey = "update.notesVersion";
    /// <summary>Set by "Restart now": the next start resumes playback (AppHost.Start).</summary>
    public const string ResumeKey = "update.resume";

    public UpdateMode Mode => service.Mode;
    public bool CanCheck => service.Mode != UpdateMode.None;
    public bool IsInApp => service.Mode == UpdateMode.InApp;
    public bool IsNoticeOnly => service.Mode == UpdateMode.NoticeOnly;
    public string CurrentVersion => currentVersion;

    public string ModeText => service.HowToUpdate ?? service.Mode switch
    {
        UpdateMode.InApp => "Updates install themselves when you quit, or right away if you choose.",
        UpdateMode.NoticeOnly => "Your package manager installs updates; you are told when there is one.",
        _ => "This copy does not update itself (a development build, or run from a folder).",
    };

    /// <summary>Check for updates when LocalFM starts.</summary>
    public bool CheckOnStart
    {
        get => host.Get(CheckKey) != "0";
        set { host.Set(CheckKey, value ? "1" : "0"); OnPropertyChanged(); }
    }

    /// <summary>The offered update; the banner shows while this is set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner), nameof(BannerText), nameof(CanRestart))]
    private AvailableUpdate? _available;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BannerText), nameof(CanAct))] private bool _isDownloading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BannerText))] private int _progress;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))] private bool _isChecking;
    /// <summary>Downloaded and handed to Velopack, which installs it once LocalFM quits.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private bool _readyOnExit;
    /// <summary>The result of the last manual check (Updates section).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private string? _lastCheck;

    public bool ShowBanner => Available is not null;
    public bool CanAct => !IsDownloading;
    /// <summary>Restarting would end a vibe, so only "When I quit" is offered then.</summary>
    public bool CanRestart => IsInApp && !host.InVibe;

    public string BannerText => Available is not { } update ? ""
        : IsDownloading ? $"Downloading LocalFM {update.Version}… {Progress}%"
        : $"LocalFM {update.Version} is available";

    public string? StatusText => ReadyOnExit ? "The update installs when you quit LocalFM." : LastCheck;

    /// <summary>The vibe started or ended: "Restart now" comes and goes.</summary>
    public void VibeChanged() => OnPropertyChanged(nameof(CanRestart));

    /// <summary>On start: silent when offline, and a skipped version stays skipped.</summary>
    public async Task CheckOnStartAsync()
    {
        if (service.Mode == UpdateMode.None || !CheckOnStart) return;
        try
        {
            if (await service.CheckAsync() is { } update && update.Version != host.Get(SkippedKey)) Offer(update);
        }
        catch (Exception)
        {
            // No network, a proxy or GitHub's rate limit: not worth a message on every start.
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNow()
    {
        IsChecking = true;
        try
        {
            if (await service.CheckAsync() is { } update)
            {
                Offer(update);
                LastCheck = $"LocalFM {update.Version} is available.";
            }
            else LastCheck = $"LocalFM {currentVersion} is up to date.";
        }
        catch (Exception e)
        {
            LastCheck = $"Could not check for updates: {e.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanCheckNow() => !IsChecking && service.Mode != UpdateMode.None;

    private void Offer(AvailableUpdate update)
    {
        if (ReadyOnExit || IsDownloading) return;
        Available = update;
    }

    /// <summary>The gentle default: download now, install once LocalFM quits (closing to the tray is not quitting).</summary>
    [RelayCommand]
    private async Task UpdateOnQuit()
    {
        if (!await DownloadAsync() || Available is not { } update) return;
        try
        {
            service.ApplyOnExit(update);
            ReadyOnExit = true;
            Available = null;
            host.Notify($"LocalFM {update.Version} installs when you quit LocalFM.");
        }
        catch (Exception e)
        {
            host.Notify($"Could not install the update: {e.Message}");
        }
    }

    /// <summary>Install now: the music stops for a moment and picks up where it was.</summary>
    [RelayCommand]
    private async Task RestartNow()
    {
        if (!CanRestart) return;
        if (!await DownloadAsync() || Available is not { } update) return;
        if (host.InVibe) // joined one while downloading
        {
            OnPropertyChanged(nameof(CanRestart));
            return;
        }
        try
        {
            await host.PrepareRestartAsync();
            service.ApplyAndRestart(update);
        }
        catch (Exception e)
        {
            host.Set(ResumeKey, "");
            host.Notify($"Could not install the update: {e.Message}");
        }
    }

    private async Task<bool> DownloadAsync()
    {
        if (Available is not { } update || IsDownloading || service.Mode != UpdateMode.InApp) return false;
        IsDownloading = true;
        Progress = 0;
        try
        {
            // Velopack reports progress from its own threads.
            await service.DownloadAsync(update, p => Dispatcher.UIThread.Post(() => Progress = Math.Clamp(p, 0, 100)));
            Progress = 100;
            // The new version shows these once it runs (What's new).
            host.Set(NotesKey, update.NotesMarkdown ?? "");
            host.Set(NotesVersionKey, update.Version);
            return true;
        }
        catch (Exception e)
        {
            host.Notify($"Could not download the update: {e.Message}");
            return false;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        if (Available is not { } update) return;
        host.Set(SkippedKey, update.Version);
        Available = null;
    }

    /// <summary>Remind me later: the next start offers it again.</summary>
    [RelayCommand]
    private void Dismiss() => Available = null;

    [RelayCommand]
    private Task OpenReleasePage() =>
        host.OpenUrlAsync(Available?.ReleaseUrl ?? ReleaseSource.RepositoryUrl + "/releases/latest");

    // ------------------------------------------------------------------ What's new

    /// <summary>The installed version's notes, shown once after an update.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WhatsNewTitle))] private string? _whatsNew;
    [ObservableProperty] private bool _whatsNewOpen;
    public string WhatsNewTitle => $"What's new in {currentVersion}";

    /// <summary>The first start of a new version shows its notes once; notes of an update that never got installed are dropped.</summary>
    public void ShowWhatsNewIfUpdated()
    {
        if (host.Get(NotesVersionKey) is not { Length: > 0 } version) return;
        int order = ReleaseSource.CompareVersions(version, currentVersion);
        if (order > 0) return; // still waiting to be installed when LocalFM quits
        string? notes = host.Get(NotesKey);
        host.Set(NotesKey, "");
        host.Set(NotesVersionKey, "");
        if (order == 0 && !string.IsNullOrWhiteSpace(notes))
        {
            WhatsNew = notes;
            WhatsNewOpen = true;
        }
    }

    [RelayCommand] private void CloseWhatsNew() => WhatsNewOpen = false;

    // ------------------------------------------------------------------ AppImage menu entry

    internal string? AppImagePath { get; set; } = DesktopEntry.AppImagePath;
    internal string DataHome { get; set; } = DesktopEntry.DataHome;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanAddToMenu), nameof(MenuText))]
    private DesktopEntryStatus _menuStatus = DesktopEntryStatus.NotApplicable;

    public bool CanAddToMenu => MenuStatus is DesktopEntryStatus.Missing or DesktopEntryStatus.Stale;
    public string MenuText => MenuStatus == DesktopEntryStatus.Stale
        ? "The applications menu starts LocalFM from another place. Point it here?"
        : "This AppImage is not in your applications menu yet.";

    /// <summary>An AppImage without a menu entry says so once; the Updates section keeps the button.</summary>
    public void CheckMenuEntry()
    {
        MenuStatus = DesktopEntry.Check(AppImagePath, DataHome);
        if (CanAddToMenu && host.Get("appimage.menuHint") != "1")
        {
            host.Set("appimage.menuHint", "1");
            host.Notify("Add LocalFM to your applications menu from Sources → Updates.");
        }
    }

    [RelayCommand]
    private void AddToMenu()
    {
        if (AppImagePath is not { } appImage) return;
        try
        {
            using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LocalFM/Assets/icon.png"));
            using var bytes = new MemoryStream();
            icon.CopyTo(bytes);
            DesktopEntry.Register(appImage, DataHome, bytes.ToArray());
            MenuStatus = DesktopEntry.Check(appImage, DataHome);
            host.Notify("LocalFM is in your applications menu.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            host.Notify($"Could not add LocalFM to the menu: {e.Message}");
        }
    }
}
