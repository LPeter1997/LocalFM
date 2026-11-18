namespace LocalFM.UI.Updates;

/// <summary>How the desktop app updates: Velopack in the app, or a notice for distro packages.</summary>
public static class DesktopUpdates
{
    /// <summary>
    /// The project file sets <c>NO_UPDATER</c> for distro builds (<c>-p:LocalFMUpdater=false</c>), so the
    /// Velopack updater cannot run where the system owns the files.
    /// </summary>
    public static IUpdateService Create(string currentVersion)
    {
#if NO_UPDATER
        return new ReleaseNoticeService(currentVersion);
#else
        var velopack = new VelopackUpdateService(ReleaseSource.Override);
        return velopack.Mode == UpdateMode.InApp ? velopack : new NoUpdateService();
#endif
    }
}

#if !NO_UPDATER
/// <summary>Velopack against the GitHub releases: delta packages, applied by Velopack's updater process.</summary>
public sealed class VelopackUpdateService(string? source = null) : IUpdateService
{
    // A path or URL string becomes Velopack's file or web source.
    private readonly Velopack.UpdateManager _manager = source is null
        ? new(new Velopack.Sources.GithubSource(ReleaseSource.RepositoryUrl, null, false))
        : new(source);

    public UpdateMode Mode => _manager.IsInstalled ? UpdateMode.InApp : UpdateMode.None;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        if (!_manager.IsInstalled) return null;
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        if (info is null) return null;
        var target = info.TargetFullRelease;
        var page = source is null ? $"{ReleaseSource.RepositoryUrl}/releases/tag/v{target.Version}" : null;
        return new AvailableUpdate(target.Version.ToString(), target.NotesMarkdown, page, info);
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct = default) =>
        _manager.DownloadUpdatesAsync((Velopack.UpdateInfo)update.Handle!, progress, ct);

    public void ApplyAndRestart(AvailableUpdate update) => _manager.ApplyUpdatesAndRestart(((Velopack.UpdateInfo)update.Handle!).TargetFullRelease);

    public void ApplyOnExit(AvailableUpdate update) =>
        _manager.WaitExitThenApplyUpdates(((Velopack.UpdateInfo)update.Handle!).TargetFullRelease, silent: true, restart: false);
}
#endif
