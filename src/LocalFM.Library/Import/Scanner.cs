using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Threading.Channels;
using LocalFM.Core;
using LocalFM.Native;

namespace LocalFM.Library.Import;

public sealed record ScanProgress(string Source, int Found, int Processed, int Added, int Updated, int Failed, bool Done);

/// <summary>
/// Scans sources into the knowledge base. Probing/hashing runs in parallel at low priority; writes
/// are batched through the <see cref="LibraryWriter"/>. Unchanged files (same size + mtime) are skipped
/// without being opened, so rescans are cheap. Nothing here touches the playback engine.
/// </summary>
public sealed class Scanner
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".m4b", ".mp4", ".aac", ".alac", ".wav", ".wave",
        ".aif", ".aiff", ".aifc", ".wv", ".ape", ".mpc", ".wma", ".mka", ".dsf", ".dff", ".tta", ".spx", ".webm",
    };

    private static readonly string[] FolderImageNames =
        ["cover", "folder", "front", "album", "albumart", "albumartsmall", "thumb"];

    private const int BatchSize = 64;

    private readonly Database _db;
    private readonly Importer _importer;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    private volatile string[] _excluded;

    public Scanner(Database db, ImageStore images)
    {
        _db = db;
        _importer = new Importer(images);
        _excluded = [.. Sources.Excluded(db)];
    }

    /// <summary>True when the path is inside a folder the user excluded (as of the last scan).</summary>
    public bool IsExcluded(string path)
    {
        foreach (var dir in _excluded)
            if (Sources.IsUnder(path, dir)) return true;
        return false;
    }

    public event Action<ScanProgress>? Progress;

    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        var sources = Sources.List(_db).Where(s => s.Enabled).ToList();
        foreach (var s in sources) await ScanAsync(s, ct).ConfigureAwait(false);
    }

    public async Task ScanAsync(Source source, CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ScanCoreAsync(source, ct).ConfigureAwait(false); }
        finally { _scanLock.Release(); }
    }

    private async Task ScanCoreAsync(Source source, CancellationToken ct)
    {
        if (!Directory.Exists(source.Path))
        {
            Log.Warn("Scan", $"Source folder not found: {source.Path}");
            return;
        }

        // What we already know about this source: path → (size, mtime).
        Dictionary<string, (long Size, long MTime)> known;
        using (var c = _db.Open())
        {
            known = c.Query("SELECT path, size, mtime FROM audio_file WHERE source_id = $1 AND missing = 0",
                    r => (Path: r.GetString(0), Size: r.GetInt64(1), MTime: r.GetInt64(2)), source.Id)
                .ToDictionary(x => x.Path, x => (x.Size, x.MTime));
        }

        _excluded = [.. Sources.Excluded(_db)];

        // Metadata integrations left for files they delivered (see ImportHints).
        var hints = ImportHints.Under(_db, source.Path);

        var seen = new ConcurrentDictionary<string, byte>();
        int found = 0, processed = 0, added = 0, updated = 0, failed = 0;
        void Report(bool done) => Progress?.Invoke(new ScanProgress(source.Path, found, processed, added, updated, failed, done));

        var work = Channel.CreateBounded<string>(256);
        var results = Channel.CreateBounded<ScannedFile>(BatchSize * 2);

        var enumerate = Task.Run(async () =>
        {
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                // Excluded folders are pruned, not walked: they may hold lots of unrelated files.
                var files = new FileSystemEnumerable<string>(source.Path, (ref FileSystemEntry e) => e.ToFullPath(), opts)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && AudioExtensions.Contains(Path.GetExtension(e.FileName).ToString()),
                    ShouldRecursePredicate = (ref FileSystemEntry e) => !IsExcluded(e.ToFullPath()),
                };
                foreach (var path in files)
                {
                    if (IsExcluded(path)) continue; // the source itself lies inside an excluded folder
                    Interlocked.Increment(ref found);
                    seen[path] = 0;
                    var fi = new FileInfo(path);
                    if (known.TryGetValue(path, out var k) && k.Size == fi.Length && k.MTime == MTime(fi))
                    {
                        Interlocked.Increment(ref processed);
                        continue; // unchanged: no need to open it
                    }
                    await work.Writer.WriteAsync(path, ct).ConfigureAwait(false);
                }
            }
            finally { work.Writer.Complete(); }
        }, ct);

        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        var probing = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var path in work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var scanned = ProbeFile(source.Id, path, hints.GetValueOrDefault(path));
                if (scanned is null)
                {
                    Interlocked.Increment(ref failed);
                    Interlocked.Increment(ref processed);
                    continue;
                }
                await results.Writer.WriteAsync(scanned, ct).ConfigureAwait(false);
            }
        }, ct)).ToArray();
        _ = Task.WhenAll(probing).ContinueWith(_ => results.Writer.Complete(), TaskScheduler.Default);

        var batch = new List<ScannedFile>(BatchSize);
        var lastReport = DateTime.UtcNow;
        async Task Commit()
        {
            if (batch.Count == 0) return;
            var items = batch.ToArray();
            batch.Clear();
            var outcomes = await _db.Writer.Run((c, t) =>
            {
                var tx = new Tx(c, t);
                var list = new List<ImportOutcome>(items.Length);
                foreach (var f in items)
                {
                    try { list.Add(_importer.Import(tx, f)); }
                    catch (Exception ex)
                    {
                        Log.Warn("Scan", $"Import failed for '{f.Path}': {ex.Message}");
                        list.Add(ImportOutcome.Unchanged);
                    }
                }
                return list;
            }).ConfigureAwait(false);
            foreach (var o in outcomes)
            {
                Interlocked.Increment(ref processed);
                if (o == ImportOutcome.Added) Interlocked.Increment(ref added);
                else if (o != ImportOutcome.Unchanged) Interlocked.Increment(ref updated);
            }
            Report(false);
        }

        await foreach (var f in results.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            batch.Add(f);
            if (batch.Count >= BatchSize || DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
            {
                await Commit().ConfigureAwait(false);
                lastReport = DateTime.UtcNow;
            }
        }
        await Commit().ConfigureAwait(false);
        await enumerate.ConfigureAwait(false);

        // Files we knew about but did not see are missing (kept, so history and edits survive a remount).
        var gone = known.Keys.Where(p => !seen.ContainsKey(p)).ToList();
        await _db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            foreach (var p in gone) tx.Exec("UPDATE audio_file SET missing = 1 WHERE path = $1", p);
            tx.Exec("UPDATE source SET last_scan_at = $2 WHERE id = $1", source.Id, Sql.Now());
        }).ConfigureAwait(false);

        Log.Info("Scan", $"{source.Path}: {found} files, {added} added, {updated} updated, {gone.Count} missing, {failed} unreadable");
        Report(true);
    }

    /// <summary>Probes and hashes a file. Returns null for unreadable/non-audio files.</summary>
    public static ScannedFile? ProbeFile(Ulid sourceId, string path, ImportHint? hint = null)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!MediaProbe.TryOpen(path, out var probe) || probe is null) return null;
            using (probe)
            {
                var tagPairs = probe.Tags;
                var tags = FileTags.Parse(tagPairs);
                if (hint is not null) tags = ImportHints.Apply(tags, hint);
                var pic = probe.Picture;
                string audioHash = AudioIdentity.Compute(probe);
                return new ScannedFile(
                    sourceId, path, fi.Length, MTime(fi), audioHash,
                    AudioIdentity.HashTags(tagPairs, pic?.Data.Length ?? 0),
                    probe.Codec, probe.Container,
                    probe.Duration is { } d ? (long)d.TotalMilliseconds : null,
                    probe.BitRate, probe.SampleRate, probe.Channels, probe.BitsPerSample,
                    tags, pic?.Data, pic?.Mime,
                    pic is null ? FindFolderImage(Path.GetDirectoryName(path)!) : null,
                    hint);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Scan", $"Cannot read '{path}': {ex.Message}");
            return null;
        }
    }

    private static string? FindFolderImage(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(f);
                if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileNameWithoutExtension(f);
                if (FolderImageNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))) return f;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static long MTime(FileInfo fi) => new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
}
