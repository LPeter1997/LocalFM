using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Library.Import;

namespace LocalFM.Ingest;

public enum ImportStatus { Queued, Running, Done, Failed, Cancelled }

/// <summary>One import, persisted so the queue survives a restart. Progress is in memory only.</summary>
public sealed class ImportJob(Ulid id, ImportCandidate candidate, ImportStatus status)
{
    public Ulid Id { get; } = id;
    public ImportCandidate Candidate { get; internal set; } = candidate;
    public ImportStatus Status { get; internal set; } = status;
    public double Progress { get; internal set; }
    public string? Error { get; internal set; }
    public string? FilePath { get; internal set; }
    public long CreatedAt { get; internal init; }
}

/// <summary>
/// The import queue. Each provider works through its own jobs one at a time (downloads never pile up), on
/// background tasks and separate low-priority processes. A job fetches into a private staging folder, records
/// what it knows as an <see cref="ImportHint"/>, moves the file into the import folder and asks the scanner to
/// pick it up. The playback engine is never involved.
/// </summary>
public sealed class ImportService : IDisposable
{
    public const string FolderSetting = "import.folder";
    public const string YtDlpPathSetting = "import.ytdlp.path";

    private readonly Database _db;
    private readonly AppPaths _paths;
    private readonly Scanner _scanner;
    private readonly FolderWatcher _watcher;
    private readonly History _settings;
    private readonly HttpClient _http;
    private readonly Lock _lock = new();
    private readonly List<ImportJob> _jobs = [];
    private readonly Dictionary<Ulid, CancellationTokenSource> _running = new();
    private readonly CancellationTokenSource _shutdown = new();

    public ImportService(Database db, AppPaths paths, Scanner scanner, FolderWatcher watcher, History settings,
        IProcessRunner? runner = null, HttpClient? http = null)
    {
        _db = db;
        _paths = paths;
        _scanner = scanner;
        _watcher = watcher;
        _settings = settings;
        _http = http ?? CreateHttp();
        runner ??= new ProcessRunner();
        YtDlp = new YtDlpProvider(runner, () => _settings.Get(YtDlpPathSetting));
        Archive = new ArchiveProvider(StagingRoot);
        Providers = [YtDlp, Archive];
    }

    public YtDlpProvider YtDlp { get; }
    public ArchiveProvider Archive { get; }
    public IReadOnlyList<IImportProvider> Providers { get; }

    /// <summary>A job was added, changed state or made progress (raised on a background thread).</summary>
    public event Action<ImportJob>? JobChanged;

    /// <summary>The job list itself changed (jobs added or cleared).</summary>
    public event Action? JobsChanged;

    public string StagingRoot => Path.Combine(_paths.Root, "import-staging");

    public static string DefaultFolder()
    {
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrEmpty(music))
            music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        return Path.Combine(music, "LocalFM");
    }

    /// <summary>Where imports land. A normal folder source, so scans and watching work as for any other.</summary>
    public string ImportFolder => _settings.Get(FolderSetting) is { Length: > 0 } f ? f : DefaultFolder();

    public async Task SetImportFolderAsync(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        await _settings.SetAsync(FolderSetting, full).ConfigureAwait(false);
        await EnsureSourceAsync().ConfigureAwait(false);
    }

    public Task SetYtDlpPathAsync(string? path) => _settings.SetAsync(YtDlpPathSetting, path ?? "");

    /// <summary>Creates the import folder and registers it as a watched source (no-op when it already is one).</summary>
    public async Task<Source> EnsureSourceAsync()
    {
        string folder = ImportFolder;
        Directory.CreateDirectory(folder);
        var source = await Sources.AddFolderAsync(_db, folder).ConfigureAwait(false);
        _watcher.Watch(source);
        return source;
    }

    public IImportProvider? ProviderFor(string input) => Providers.FirstOrDefault(p => p.CanHandle(input));

    /// <summary>What an input would import, with things already in the library marked.</summary>
    public async Task<ImportPreview> ResolveAsync(string input, CancellationToken ct = default)
    {
        var provider = ProviderFor(input) ?? throw new ImportException("Paste a web link, or choose a zip, a folder or audio files.");
        var preview = await provider.ResolveAsync(input, ct).ConfigureAwait(false);
        var items = preview.Items.Select(c => c with { AlreadyInLibrary = IsKnown(c) }).ToList();
        return preview with { Items = items };
    }

    private bool IsKnown(ImportCandidate c) =>
        c.ProviderId == YtDlpProvider.ProviderId && c.ExternalId is { } id && ImportHints.IsKnown(_db, "youtube", id);

    // ------------------------------------------------------------------ queue

    public IReadOnlyList<ImportJob> Jobs
    {
        get { lock (_lock) return _jobs.ToList(); }
    }

    /// <summary>Loads the persisted queue (jobs cut off by a quit go back to queued) and starts working.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await _db.Writer.Run((c, t) =>
                {
                    var tx = new Tx(c, t);
                    tx.Exec("UPDATE import_job SET status = 'queued', progress = 0 WHERE status = 'running'");
                    return tx.Query("""
                        SELECT id, provider, input, external_id, title, artist, album, album_artist, track_no, date, thumbnail,
                               status, progress, error, file_path, created_at
                        FROM import_job ORDER BY created_at
                        """, r => new ImportJob(r.Id(0),
                        new ImportCandidate(r.GetString(1), r.GetString(2), r.GetString(4))
                        {
                            ExternalId = r.Str(3), Url = r.GetString(1) == YtDlpProvider.ProviderId ? r.GetString(2) : null,
                            Artist = r.Str(5), Album = r.Str(6), AlbumArtist = r.Str(7), TrackNumber = r.Int(8),
                            Date = r.Str(9), ThumbnailUrl = r.Str(10),
                        },
                        ParseStatus(r.GetString(11)))
                    {
                        Progress = r.GetDouble(12), Error = r.Str(13), FilePath = r.Str(14), CreatedAt = r.GetInt64(15),
                    });
                }).ConfigureAwait(false);
                lock (_lock) { _jobs.Clear(); _jobs.AddRange(loaded); }
                JobsChanged?.Invoke();
                CleanPreviews();
                Pump();
            }
            catch (Exception ex) { Log.Error("Import", "Loading the import queue failed", ex); }
        });
    }

    public async Task EnqueueAsync(IEnumerable<ImportCandidate> candidates)
    {
        long now = Sql.Now();
        var jobs = candidates.Select((c, i) => new ImportJob(Ulid.NewUlid(), c, ImportStatus.Queued) { CreatedAt = now + i }).ToList();
        if (jobs.Count == 0) return;
        await _db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            foreach (var j in jobs)
            {
                var k = j.Candidate;
                tx.Exec("""
                    INSERT INTO import_job(id, provider, input, external_id, title, artist, album, album_artist, track_no, date, thumbnail,
                        status, progress, created_at, updated_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,'queued',0,$12,$12)
                    """, j.Id, k.ProviderId, k.Url ?? k.Input, k.ExternalId, k.Title, k.Artist, k.Album, k.AlbumArtist,
                    k.TrackNumber, k.Date, k.ThumbnailUrl, j.CreatedAt);
            }
        }).ConfigureAwait(false);
        lock (_lock) _jobs.AddRange(jobs);
        JobsChanged?.Invoke();
        Pump();
    }

    public void Cancel(Ulid jobId)
    {
        ImportJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is null) return;
            if (_running.TryGetValue(jobId, out var cts)) { cts.Cancel(); return; } // the worker records it
            if (job.Status != ImportStatus.Queued) return;
            job.Status = ImportStatus.Cancelled;
        }
        _ = SaveAsync(job);
        JobChanged?.Invoke(job);
    }

    public void Retry(Ulid jobId)
    {
        ImportJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId && j.Status is ImportStatus.Failed or ImportStatus.Cancelled);
            if (job is null) return;
            job.Status = ImportStatus.Queued;
            job.Error = null;
            job.Progress = 0;
        }
        _ = SaveAsync(job).ContinueWith(_ => Pump(), TaskScheduler.Default);
        JobChanged?.Invoke(job);
    }

    /// <summary>Forgets finished, failed and cancelled jobs.</summary>
    public async Task ClearFinishedAsync()
    {
        await _db.Writer.Run((c, t) => new Tx(c, t).Exec("DELETE FROM import_job WHERE status IN ('done','failed','cancelled')")).ConfigureAwait(false);
        lock (_lock) _jobs.RemoveAll(j => j.Status is ImportStatus.Done or ImportStatus.Failed or ImportStatus.Cancelled);
        JobsChanged?.Invoke();
    }

    /// <summary>Starts the next queued job of every provider that is idle.</summary>
    private void Pump()
    {
        if (_shutdown.IsCancellationRequested) return;
        var start = new List<(ImportJob Job, CancellationTokenSource Cts)>();
        lock (_lock)
        {
            foreach (var group in _jobs.Where(j => j.Status == ImportStatus.Queued).GroupBy(j => j.Candidate.ProviderId))
            {
                bool busy = _jobs.Any(j => j.Status == ImportStatus.Running && j.Candidate.ProviderId == group.Key);
                if (busy) continue;
                var next = group.OrderBy(j => j.CreatedAt).First();
                next.Status = ImportStatus.Running;
                next.Progress = 0;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _running[next.Id] = cts;
                start.Add((next, cts));
            }
        }
        foreach (var (job, cts) in start) _ = Task.Run(() => RunAsync(job, cts));
    }

    private async Task RunAsync(ImportJob job, CancellationTokenSource cts)
    {
        string staging = Path.Combine(StagingRoot, job.Id.ToString());
        try
        {
            JobChanged?.Invoke(job);
            await SaveAsync(job).ConfigureAwait(false);
            var provider = Providers.FirstOrDefault(p => p.Id == job.Candidate.ProviderId)
                           ?? throw new ImportException($"Unknown importer '{job.Candidate.ProviderId}'.");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);

            long lastReport = 0;
            var progress = new Progress<double>(p =>
            {
                job.Progress = p;
                long now = Environment.TickCount64;
                if (now - lastReport < 200 && p < 1) return;
                lastReport = now;
                JobChanged?.Invoke(job);
            });
            var fetched = await provider.FetchAsync(job.Candidate, staging, progress, cts.Token).ConfigureAwait(false);
            job.Candidate = fetched.Info;
            cts.Token.ThrowIfCancellationRequested();

            job.FilePath = await PlaceAsync(fetched, cts.Token).ConfigureAwait(false);
            job.Status = ImportStatus.Done;
            job.Progress = 1;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            job.Status = _shutdown.IsCancellationRequested ? ImportStatus.Queued : ImportStatus.Cancelled;
            job.Progress = 0;
        }
        catch (Exception ex)
        {
            if (ex is not ImportException) Log.Error("Import", $"Import of '{job.Candidate.Title}' failed", ex);
            job.Status = ImportStatus.Failed;
            job.Error = ex is ImportException or FileNotFoundException or IOException ? ex.Message : "Something went wrong (see the log).";
        }
        finally
        {
            lock (_lock) _running.Remove(job.Id);
            cts.Dispose();
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!_shutdown.IsCancellationRequested)
        {
            try { await SaveAsync(job).ConfigureAwait(false); } catch (Exception ex) { Log.Warn("Import", $"Saving job state failed: {ex.Message}"); }
            JobChanged?.Invoke(job);
            Pump();
            if (!Jobs.Any(j => j.Status is ImportStatus.Queued or ImportStatus.Running)) CleanPreviews();
        }
    }

    /// <summary>Moves a fetched file into the import folder (cover first, hint before the file) and scans it in.</summary>
    private async Task<string> PlaceAsync(FetchedFile fetched, CancellationToken ct)
    {
        var source = await EnsureSourceAsync().ConfigureAwait(false);
        var info = fetched.Info;
        string dest = LibraryLayout.Destination(source.Path, info, Path.GetExtension(fetched.Path).ToLowerInvariant());
        string dir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(dir);

        // A folder cover applies to every file in the folder, so only real albums get one.
        if (info.Album is not null && !HasCover(dir))
        {
            try
            {
                if (fetched.CoverPath is { } local && File.Exists(local))
                    File.Copy(local, Path.Combine(dir, "cover" + Path.GetExtension(local).ToLowerInvariant()));
                else if (info.ThumbnailUrl is { } url)
                    await DownloadCoverAsync(url, Path.Combine(dir, "cover.jpg"), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
            {
                Log.Warn("Import", $"No cover for '{info.Album}': {ex.Message}");
            }
        }

        if (info.ProviderId == YtDlpProvider.ProviderId)
            await ImportHints.PutAsync(_db, new ImportHint(dest, info.ProviderId, info.ExternalId, info.Url,
                info.Title, info.Artist, info.AlbumArtist, info.Album, info.Date, info.TrackNumber)).ConfigureAwait(false);

        File.Move(fetched.Path, dest); // .NET copies across volumes
        await _scanner.ScanAsync(source, ct).ConfigureAwait(false);
        return dest;
    }

    private static bool HasCover(string dir) =>
        Directory.EnumerateFiles(dir).Any(f =>
            Path.GetFileNameWithoutExtension(f).Equals("cover", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(f).Equals("folder", StringComparison.OrdinalIgnoreCase));

    private async Task DownloadCoverAsync(string url, string path, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/jpg")) return;
        if (response.Content.Headers.ContentLength is > 10_000_000) return;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        string tmp = path + ".part";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private Task SaveAsync(ImportJob j) => _db.Writer.Run((c, t) =>
        new Tx(c, t).Exec("""
            UPDATE import_job SET status = $2, progress = $3, error = $4, file_path = $5, title = $6, artist = $7, album = $8,
                album_artist = $9, track_no = $10, date = $11, updated_at = $12
            WHERE id = $1
            """, j.Id, StatusText(j.Status), j.Progress, j.Error, j.FilePath, j.Candidate.Title, j.Candidate.Artist,
            j.Candidate.Album, j.Candidate.AlbumArtist, j.Candidate.TrackNumber, j.Candidate.Date, Sql.Now()));

    /// <summary>Removes unpacked zips that no waiting job still needs.</summary>
    private void CleanPreviews()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            var needed = Jobs.Where(j => j.Status is ImportStatus.Queued or ImportStatus.Running or ImportStatus.Failed)
                .Select(j => j.Candidate.Input).ToList();
            foreach (var dir in Directory.EnumerateDirectories(StagingRoot, "preview-*"))
            {
                string prefix = dir + Path.DirectorySeparatorChar;
                if (needed.Any(p => p.StartsWith(prefix, StringComparison.Ordinal))) continue;
                // Keep the newest few minutes: the person may still be looking at that preview.
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < TimeSpan.FromHours(1)) continue;
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Import", $"Cleaning previews failed: {ex.Message}");
        }
    }

    private static string StatusText(ImportStatus s) => s switch
    {
        ImportStatus.Queued => "queued",
        ImportStatus.Running => "running",
        ImportStatus.Done => "done",
        ImportStatus.Failed => "failed",
        _ => "cancelled",
    };

    private static ImportStatus ParseStatus(string s) => s switch
    {
        "queued" or "running" => ImportStatus.Queued,
        "done" => ImportStatus.Done,
        "failed" => ImportStatus.Failed,
        _ => ImportStatus.Cancelled,
    };

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LocalFM/0.1 ( local music player )");
        return client;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_lock) foreach (var cts in _running.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
