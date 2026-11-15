namespace LocalFM.Core;

/// <summary>Who the app is to the desktop.</summary>
public static class AppInfo
{
    /// <summary>The Linux app id: desktop file, metainfo, Flatpak, the window's WM_CLASS and MPRIS' DesktopEntry
    /// (which must name the desktop file, without ".desktop"). Also LocalFMAppId in Directory.Build.props.</summary>
    public const string AppId = "io.github.LPeter1997.LocalFM";

    /// <summary>The release version (Directory.Build.props, or package.sh's -p:Version).</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion is { } v
            ? v.Split('+')[0] // without the commit hash the SDK appends
            : "0.0.0";
}
