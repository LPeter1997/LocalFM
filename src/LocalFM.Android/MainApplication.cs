using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using LocalFM.UI;

namespace LocalFM.Android;

/// <summary>The process: Avalonia and the app's services live here, so they outlive any activity.</summary>
[Application]
public sealed class MainApplication : AvaloniaAndroidApplication<LocalFM.UI.App>
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
        LocalFM.UI.App.Platform = AndroidPlatform.Instance;
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
