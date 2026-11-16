using Avalonia;
using Avalonia.Markup.Xaml;

namespace LocalFM.UI;

public partial class App : Application
{
    /// <summary>Set by the app head (desktop or Android) before Avalonia starts.</summary>
    public static IAppPlatform Platform { get; set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Platform.Launch(this, ApplicationLifetime);
        base.OnFrameworkInitializationCompleted();
    }
}
