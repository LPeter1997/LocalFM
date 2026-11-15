using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalFM.Core;
using LocalFM.Library.Edit;

namespace LocalFM.Ingest;

/// <summary>
/// Downloads through yt-dlp when the person has it installed. LocalFM never ships it: it is found on PATH or at a
/// configured location and run as a separate low-priority process. The original audio stream is kept (no
/// re-encoding, so no ffmpeg needed); the metadata yt-dlp knows is handed to the library as an import hint.
/// </summary>
public sealed partial class YtDlpProvider(IProcessRunner runner, Func<string?> configuredPath) : IImportProvider
{
    public const string ProviderId = "ytdlp";
    public string Id => ProviderId;
    public string Name => "yt-dlp";

    /// <summary>Fields printed after a download, so the file comes with full metadata (flat playlists have little).</summary>
    private const string InfoTemplate =
        "after_move:%(.{id,title,track,artist,artists,creator,album,album_artist,release_year,release_date,track_number,uploader,channel,webpage_url,duration,license,thumbnails})j";

    private static bool InFlatpak => Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 };

    public string? Executable()
    {
        string? configured = configuredPath();
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        return ProcessRunner.FindOnPath("yt-dlp");
    }

    public async Task<ProviderStatus> DetectAsync(CancellationToken ct = default)
    {
        if (Executable() is not { } exe)
        {
            string? configured = configuredPath();
            return new ProviderStatus(false, Note: !string.IsNullOrWhiteSpace(configured) ? $"Not found at {configured}."
                // The sandbox cannot run the system's programs; yt-dlp's single-file build (yt-dlp_linux) runs from home.
                : InFlatpak ? "The Flatpak cannot run the system's yt-dlp. Locate yt-dlp_linux from its GitHub releases."
                : "Not installed. Install yt-dlp (github.com/yt-dlp/yt-dlp) and LocalFM will find it.");
        }
        try
        {
            var r = await runner.RunAsync(exe, ["--version"], ct: ct).ConfigureAwait(false);
            string version = r.StdOut.Trim();
            return r.ExitCode == 0 && version.Length > 0
                ? new ProviderStatus(true, version, exe)
                : new ProviderStatus(false, Location: exe, Note: LastError(r) ?? "It did not report a version.");
        }
        catch (FileNotFoundException ex) { return new ProviderStatus(false, Location: exe, Note: ex.Message); }
    }

    /// <summary>Runs yt-dlp's self-update. Returns its last line of output (it explains when it cannot update itself).</summary>
    public async Task<string> UpdateAsync(CancellationToken ct = default)
    {
        string exe = Executable() ?? throw new ImportException("yt-dlp is not installed.");
        var r = await runner.RunAsync(exe, ["-U"], ct: ct).ConfigureAwait(false);
        string text = (r.StdOut + "\n" + r.StdErr).Trim();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "Done.";
    }

    public bool CanHandle(string input) =>
        Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public async Task<ImportPreview> ResolveAsync(string input, CancellationToken ct = default)
    {
        string exe = Executable() ?? throw new ImportException("yt-dlp is not installed.");
        var r = await runner.RunAsync(exe, ["--ignore-config", "-J", "--flat-playlist", "--", input.Trim()], ct: ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || r.StdOut.TrimStart() is not ['{', ..])
            throw new ImportException(LastError(r) ?? "yt-dlp could not read that link.");
        return ParsePreview(r.StdOut, input.Trim()) with { Note = FirstWarning(r) };
    }

    public async Task<FetchedFile> FetchAsync(ImportCandidate candidate, string stagingDir, IProgress<double> progress, CancellationToken ct = default)
    {
        string exe = Executable() ?? throw new ImportException("yt-dlp is not installed.");
        Directory.CreateDirectory(stagingDir);
        string url = candidate.Url ?? candidate.Input;
        string? path = null;
        ImportCandidate? info = null;
        void OnLine(string line, bool isErr)
        {
            if (ParseProgress(line) is { } p) { progress.Report(p); return; }
            if (isErr) return;
            string t = line.Trim();
            if (t.StartsWith('{')) { try { info = ParseEntry(JsonDocument.Parse(t).RootElement, null); } catch (JsonException) { } }
            else if (t.Length > 0 && File.Exists(t)) path = t;
        }
        var r = await runner.RunAsync(exe,
        [
            "--ignore-config", "--no-playlist", "--no-mtime", "--newline", "--progress",
            "-f", "bestaudio[ext=m4a]/bestaudio",
            "--progress-template", "download:LFM %(progress.downloaded_bytes)s %(progress.total_bytes,progress.total_bytes_estimate)s",
            "--print", "after_move:filepath",
            "--print", InfoTemplate,
            "-o", Path.Combine(stagingDir, "%(id)s.%(ext)s"),
            "--", url,
        ], OnLine, ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || path is null)
            throw new ImportException(LastError(r) ?? "yt-dlp did not produce a file.");
        progress.Report(1);
        return new FetchedFile(path, info is null ? candidate : candidate.Refine(info));
    }

    // ------------------------------------------------------------------ parsing (internal for tests)

    internal static ImportPreview ParsePreview(string json, string input)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.Str("_type") is "playlist" or "multi_video")
        {
            string title = root.Str("title") ?? "Playlist";
            // YouTube Music albums are playlists named "Album - X", owned by the artist's channel.
            bool isAlbum = title.StartsWith("Album - ", StringComparison.Ordinal) || (root.Str("id") ?? "").StartsWith("OLAK5uy_", StringComparison.Ordinal);
            string? album = isAlbum ? (title.StartsWith("Album - ", StringComparison.Ordinal) ? title[8..] : title) : null;
            string? albumArtist = isAlbum ? CleanArtist(root.Str("artist") ?? root.Str("channel") ?? root.Str("uploader")) : null;
            var items = new List<ImportCandidate>();
            int index = 0;
            foreach (var e in root.Arr("entries"))
            {
                index++;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var c = ParseEntry(e, null);
                if (c.ExternalId is null) continue;
                if (isAlbum)
                    c = c with { Album = c.Album ?? album, AlbumArtist = c.AlbumArtist ?? albumArtist, Artist = c.Artist ?? albumArtist, TrackNumber = c.TrackNumber ?? index };
                items.Add(c);
            }
            return new ImportPreview(isAlbum ? $"{album} · {albumArtist}" : title, items);
        }
        var single = ParseEntry(root, input);
        return new ImportPreview(single.Title, [single]);
    }

    /// <summary>One video's JSON (full or flat) as a candidate. Prefers YouTube Music's own fields.</summary>
    internal static ImportCandidate ParseEntry(JsonElement e, string? input)
    {
        string? id = e.Str("id");
        string? url = e.Str("webpage_url") ?? (e.Str("url") is { } u && u.StartsWith("http", StringComparison.Ordinal) ? u
            : id is not null ? $"https://www.youtube.com/watch?v={id}" : null);
        string rawTitle = e.Str("title") ?? id ?? "Untitled";
        var artists = e.Arr("artists").Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).Where(a => a.Length > 0).ToList();
        string? artist = e.Str("artist") ?? (artists.Count > 0 ? string.Join(", ", artists) : null) ?? e.Str("creator");
        string? title = e.Str("track");
        if (title is null)
        {
            // A plain video: "Artist - Song (Official Video)".
            title = rawTitle;
            if (artist is null && DashSplit().Match(rawTitle) is { Success: true } m)
            {
                artist = m.Groups[1].Value.Trim();
                title = m.Groups[2].Value.Trim();
            }
            artist ??= CleanArtist(e.Str("channel") ?? e.Str("uploader"));
            title = Cleanup.CleanTitle(title, artist, remasterNotes: false)?.Text ?? title;
        }
        string? date = e.Str("release_date") is { Length: 8 } d && d.All(char.IsAsciiDigit) ? $"{d[..4]}-{d[4..6]}-{d[6..]}"
            : e.Long("release_year") is { } y ? y.ToString(CultureInfo.InvariantCulture) : null;
        double? seconds = e.Double("duration");

        return new ImportCandidate(YtDlpProvider.ProviderId, url ?? input ?? "", title)
        {
            ExternalId = id,
            Url = url,
            Artist = CleanArtist(artist),
            AlbumArtist = CleanArtist(e.Str("album_artist")),
            Album = e.Str("album"),
            TrackNumber = e.Long("track_number") is { } n and > 0 and < 1000 ? (int)n : null,
            Date = date,
            DurationMs = seconds is { } s ? (long)(s * 1000) : null,
            ThumbnailUrl = SquareThumbnail(e),
            License = ShortLicense(e.Str("license")),
        };
    }

    /// <summary>The largest square thumbnail (album art on YouTube Music). Video stills are not covers.</summary>
    private static string? SquareThumbnail(JsonElement e) =>
        e.Arr("thumbnails")
            .Where(t => t.Long("width") is { } w && w >= 200 && t.Long("height") == w && t.Str("url") is not null)
            .OrderByDescending(t => t.Long("width"))
            .Select(t => t.Str("url"))
            .FirstOrDefault();

    private static string? CleanArtist(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Cleanup.CleanArtist(name)?.Text ?? name.Trim();

    internal static string? ShortLicense(string? license)
    {
        if (string.IsNullOrWhiteSpace(license)) return null;
        if (license.Contains("Creative Commons", StringComparison.OrdinalIgnoreCase)) return "CC BY";
        return license.Length <= 24 ? license : null;
    }

    /// <summary>"LFM 1234 5678" → 0.22. Unknown totals ("NA") give null.</summary>
    internal static double? ParseProgress(string line)
    {
        if (!line.StartsWith("LFM ", StringComparison.Ordinal)) return null;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double done)) return null;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double total) || total <= 0) return null;
        return Math.Clamp(done / total, 0, 1);
    }

    internal static string? LastError(ProcessResult r) =>
        r.StdErr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) is { } err
            ? err[6..].Trim() : null;

    private static string? FirstWarning(ProcessResult r) =>
        r.StdErr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("WARNING:", StringComparison.Ordinal)) is { } w
            ? w[8..].Trim() : null;

    [GeneratedRegex(@"^(.+?)\s+[-–—]\s+(.+)$")]
    private static partial Regex DashSplit();
}
