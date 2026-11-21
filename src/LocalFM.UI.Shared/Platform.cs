using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using LocalFM.UI.Updates;

namespace LocalFM.UI;

/// <summary>What this platform's app offers. The phone leaves out library editing and the importer for now.</summary>
public sealed record PlatformCapabilities
{
    /// <summary>The Import page (yt-dlp, archives).</summary>
    public bool Import { get; init; } = true;

    /// <summary>Tidy up, the album editor, MusicBrainz matching and moving songs between albums.</summary>
    public bool Editing { get; init; } = true;

    /// <summary>Knowledge base export (import works everywhere).</summary>
    public bool ExportKnowledge { get; init; } = true;

    /// <summary>Watch source folders for changes; otherwise the library is rescanned when the app comes back.</summary>
    public bool WatchFolders { get; init; } = true;

    /// <summary>The separate always-on-top miniplayer window.</summary>
    public bool Miniplayer { get; init; } = true;

    /// <summary>"Share my library on this network": TVs and browsers at home browse and play the library.</summary>
    public bool ShareLibrary { get; init; } = true;

    /// <summary>Keyboard and mouse first: hover-revealed actions, shortcuts in tooltips.</summary>
    public bool Touch { get; init; }

    public static PlatformCapabilities Desktop { get; } = new();

    public static PlatformCapabilities Phone { get; } = new()
    {
        Import = false, Editing = false, ExportKnowledge = false, WatchFolders = false, Miniplayer = false, Touch = true, ShareLibrary = false,
    };
}

/// <summary>
/// What each app head (desktop, Android) provides. The head sets <see cref="App.Platform"/> before Avalonia starts;
/// <see cref="App"/> hands it the lifetime to build its windows or views.
/// </summary>
public interface IAppPlatform
{
    PlatformCapabilities Capabilities { get; }

    /// <summary>Builds the services and the UI for this lifetime.</summary>
    void Launch(App app, IApplicationLifetime? lifetime);

    IUpdateService CreateUpdateService();

    /// <summary>A path the app can read for a picked file or folder (null when there is none). The desktop's
    /// pickers return local files; Android's return content URIs, which the head turns into paths or copies.</summary>
    Task<string?> LocalPathAsync(IStorageItem item);

    /// <summary>Whether the app may read the music folders. Android asks the person; desktops always may.</summary>
    bool HasMusicAccess => true;

    /// <summary>Asks for music access (Android: the permission prompt, or the app's settings once Android no
    /// longer shows the prompt).</summary>
    void RequestMusicAccess() { }

    /// <summary>The system share sheet (Android); desktops copy instead.</summary>
    bool CanShare => false;

    void Share(string text) { }
}
