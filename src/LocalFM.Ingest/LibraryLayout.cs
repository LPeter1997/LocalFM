using System.Text;

namespace LocalFM.Ingest;

/// <summary>Where an imported file goes: &lt;folder&gt;/&lt;Album artist&gt;/&lt;Album or Singles&gt;/&lt;NN &gt;Title[ [id]].ext,
/// with names that are valid on Linux, Windows and macOS.</summary>
public static class LibraryLayout
{
    public const string SinglesFolder = "Singles";
    private const int MaxNameLength = 120;

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The album folder for a candidate (singles share one folder per artist).</summary>
    public static string AlbumDir(string root, ImportCandidate c) =>
        Path.Combine(root, Sanitize(c.AlbumArtist ?? c.Artist ?? "Unknown Artist"), Sanitize(c.Album ?? SinglesFolder));

    /// <summary>A free path for the file, never overwriting an existing one.</summary>
    public static string Destination(string root, ImportCandidate c, string extension)
    {
        var name = new StringBuilder();
        if (c.Album is not null && c.TrackNumber is { } n) name.Append(n.ToString("00")).Append(' ');
        name.Append(c.Title);
        string stem = Sanitize(name.ToString());
        // The video id keeps re-downloads recognisable, as yt-dlp names its files by default.
        if (c.ProviderId == YtDlpProvider.ProviderId && c.ExternalId is { } id) stem += $" [{Sanitize(id)}]";

        string dir = AlbumDir(root, c);
        string path = Path.Combine(dir, stem + extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){extension}");
        return path;
    }

    /// <summary>One path segment, safe everywhere.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(ch)) continue;
            sb.Append(ch switch
            {
                '/' or '\\' or '|' => '-',
                ':' => '-',
                '<' or '>' or '"' or '?' or '*' => '_',
                _ => ch,
            });
        }
        string s = sb.ToString().Trim().TrimEnd('.', ' ');
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        if (s.Length > MaxNameLength)
        {
            int cut = char.IsHighSurrogate(s[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
            s = s[..cut].TrimEnd('.', ' ');
        }
        if (s.Length == 0 || s.All(c => c == '.')) return "_";
        if (WindowsReserved.Contains(s.Split('.')[0])) s = "_" + s;
        return s;
    }
}
