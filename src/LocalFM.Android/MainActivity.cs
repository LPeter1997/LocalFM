using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace LocalFM.Android;

[Activity(Label = "LocalFM", MainLauncher = true, Icon = "@drawable/icon", Exported = true,
    Theme = "@style/Theme.AppCompat.NoActionBar", LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
                           | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidPlatform.Instance.ActivityCreated(this, Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        AndroidPlatform.Instance.ActivityCreated(this, intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidPlatform.Instance.Resumed();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidPlatform.Instance.PermissionsChanged();
    }
}
