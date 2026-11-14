namespace LocalFM.Core;

/// <summary>Per-user data locations. Everything LocalFM owns lives under one root so it can be backed up or moved.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ImagesDir);
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "library.db");
    public string ImagesDir => Path.Combine(Root, "images");
    public string ThumbnailsDir => Path.Combine(Root, "thumbs");

    public static AppPaths Default()
    {
        string? env = Environment.GetEnvironmentVariable("LOCALFM_HOME");
        if (!string.IsNullOrEmpty(env)) return new AppPaths(env);
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new AppPaths(Path.Combine(baseDir, "LocalFM"));
    }
}
