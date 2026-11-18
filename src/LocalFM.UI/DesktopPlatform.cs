using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using LocalFM.Core;
using LocalFM.UI.Updates;
using LocalFM.UI.ViewModels;
using LocalFM.UI.Views;

namespace LocalFM.UI;

/// <summary>Linux, Windows and macOS: our own window, tray, miniplayer, MPRIS and Velopack.</summary>
public sealed class DesktopPlatform : IAppPlatform
{
    private AppHost? _host;
    private DesktopShell? _shell;

    public PlatformCapabilities Capabilities => PlatformCapabilities.Desktop;

    public IUpdateService CreateUpdateService() => DesktopUpdates.Create(AppInfo.Version);

    public Task<string?> LocalPathAsync(IStorageItem item) => Task.FromResult(item.TryGetLocalPath());

    public void Launch(App app, IApplicationLifetime? lifetime)
    {
        if (lifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _host = new AppHost(Capabilities);
        var vm = new MainViewModel(_host);
        var main = new MainWindow { DataContext = vm };
        desktop.MainWindow = main;
        _shell = new DesktopShell(desktop, _host, vm, main);
        desktop.ShutdownRequested += (_, _) => Shutdown();
        desktop.Exit += (_, _) => Shutdown();
        _host.Start(Program.AddFolders, Program.Vibe);
        vm.Start();
    }

    private void Shutdown()
    {
        Interlocked.Exchange(ref _shell, null)?.Dispose();
        Interlocked.Exchange(ref _host, null)?.Dispose();
    }
}
