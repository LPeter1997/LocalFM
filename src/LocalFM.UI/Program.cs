using Avalonia;

namespace LocalFM.UI;

internal static class Program
{
    /// <summary>When set, the window renders itself to this PNG after loading and exits (UI checks without screen capture).</summary>
    public static string? SnapshotPath { get; } = Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT") is { Length: > 0 } s ? s : null;

    /// <summary>In snapshot mode, which page to show (albums, artists, tracks, playlists, queue, sources, album).</summary>
    public static string? SnapshotPage { get; } = Environment.GetEnvironmentVariable("LOCALFM_SNAPSHOT_PAGE");

    /// <summary>Folders given with --add-folder; added as sources at startup.</summary>
    public static IReadOnlyList<string> AddFolders { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        var folders = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--add-folder") folders.Add(Path.GetFullPath(args[++i]));
        AddFolders = folders;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "localfm", OverlayPopups = SnapshotPath is not null })
            .LogToTrace();
}
