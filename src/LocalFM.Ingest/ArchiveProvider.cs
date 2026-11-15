using System.IO.Compression;
using LocalFM.Core;
using LocalFM.Library.Import;
using LocalFM.Native;

namespace LocalFM.Ingest;

/// <summary>
/// Music the person already downloaded elsewhere: a store's zip (Bandcamp, Qobuz), a Google Takeout export of
/// YouTube Music uploads, a folder, or loose files. Zips are unpacked into a preview folder first, so each file
/// can be listed with its tags; nothing reaches the library until it is imported.
/// </summary>
public sealed class ArchiveProvider(string previewRoot) : IImportProvider
{
    public const string ProviderId = "archive";
    public string Id => ProviderId;
    public string Name => "Files and archives";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart"];

    public Task<ProviderStatus> DetectAsync(CancellationToken ct = default) =>
        Task.FromResult(new ProviderStatus(true, Note: "Zips from Bandcamp, Qobuz or Google Takeout, folders, or audio files."));

    public bool CanHandle(string input)
    {
        string p = input.Trim();
        if (p.Length == 0 || p.Contains("://", StringComparison.Ordinal)) return false;
        return Directory.Exists(p) || (File.Exists(p) && (IsZip(p) || Scanner.AudioExtensions.Contains(Path.GetExtension(p))));
    }

    private static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public Task<ImportPreview> ResolveAsync(string input, CancellationToken ct = default) => Task.Run(() =>
    {
        string path = input.Trim();
        string root;
        string title;
        if (Directory.Exists(path)) { root = path; title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)); }
        else if (IsZip(path)) { root = Extract(path, ct); title = Path.GetFileNameWithoutExtension(path); }
        else
        {
            var one = Describe(path);
            return new ImportPreview(one.Title, [one]);
        }

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var files = Directory.EnumerateFiles(root, "*", opts)
            .Where(f => Scanner.AudioExtensions.Contains(Path.GetExtension(f)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = new List<ImportCandidate>(files.Count);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(Describe(f));
        }
        if (items.Count == 0) throw new ImportException("No audio files found there.");
        return new ImportPreview(title, items);
    }, ct);

    public Task<FetchedFile> FetchAsync(ImportCandidate candidate, string stagingDir, IProgress<double> progress, CancellationToken ct = default) => Task.Run(() =>
    {
        if (!File.Exists(candidate.Input)) throw new ImportException("The file is no longer there.");
        Directory.CreateDirectory(stagingDir);
        string target = Path.Combine(stagingDir, Path.GetFileName(candidate.Input));
        File.Copy(candidate.Input, target, overwrite: true);
        progress.Report(1);
        return new FetchedFile(target, candidate, FindCover(Path.GetDirectoryName(candidate.Input)!));
    }, ct);

    /// <summary>A file's tags as a candidate. Untagged files keep their name as the title.</summary>
    internal static ImportCandidate Describe(string path)
    {
        string fallback = Path.GetFileNameWithoutExtension(path);
        if (!MediaProbe.TryOpen(path, out var probe) || probe is null)
            return new ImportCandidate(ProviderId, path, fallback);
        using (probe)
        {
            var t = FileTags.Parse(probe.Tags);
            return new ImportCandidate(ProviderId, path, t.Title ?? fallback)
            {
                Artist = t.Artist,
                AlbumArtist = t.AlbumArtist,
                Album = t.Album,
                TrackNumber = t.TrackNumber,
                Date = t.Date,
                DurationMs = probe.Duration is { } d ? (long)d.TotalMilliseconds : null,
            };
        }
    }

    /// <summary>Unpacks audio and cover images from a zip into a fresh preview folder. Entries that would land outside
    /// it ("../" paths) are refused.</summary>
    internal string Extract(string zipPath, CancellationToken ct)
    {
        string dir = Path.Combine(previewRoot, "preview-" + Ulid.NewUlid());
        Directory.CreateDirectory(dir);
        string rootFull = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            string ext = Path.GetExtension(entry.FullName);
            if (!Scanner.AudioExtensions.Contains(ext) && !ImageExtensions.Contains(ext)) continue;
            string target = Path.GetFullPath(Path.Combine(dir, entry.FullName.Replace('\\', '/')));
            if (!target.StartsWith(rootFull, StringComparison.Ordinal))
            {
                Log.Warn("Import", $"Skipping unsafe zip entry '{entry.FullName}'");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        return dir;
    }

    private static string? FindCover(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .FirstOrDefault(f => CoverNames.Any(n => Path.GetFileNameWithoutExtension(f).Equals(n, StringComparison.OrdinalIgnoreCase)));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
