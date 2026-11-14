using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LocalFM.UI.ViewModels;
using LocalFM.UI.Views;

namespace LocalFM.UI;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = new AppHost();
            var vm = new MainViewModel(_host);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => Shutdown();
            desktop.Exit += (_, _) => Shutdown();
            _host.Start(Program.AddFolders);
            vm.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        var h = Interlocked.Exchange(ref _host, null);
        h?.Dispose();
    }
}
