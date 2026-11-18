using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Ingest;
using LocalFM.Library;

namespace LocalFM.UI.ViewModels;

/// <summary>One row of the preview: something that can be imported, ticked by default unless already owned.</summary>
public sealed partial class CandidateViewModel(ImportCandidate c) : ObservableObject
{
    public ImportCandidate Candidate { get; } = c;
    [ObservableProperty] private bool _selected = !c.AlreadyInLibrary;

    public string Title => Candidate.TrackNumber is { } n && Candidate.Album is not null ? $"{n}. {Candidate.Title}" : Candidate.Title;
    public string Detail => string.Join(" · ", new[] { Candidate.Artist, Candidate.Album }.Where(s => !string.IsNullOrEmpty(s)));
    public string Duration => Candidate.DurationMs is { } ms ? Converters.Time(TimeSpan.FromMilliseconds(ms)) : "";
    public bool Owned => Candidate.AlreadyInLibrary;
    public string? License => Candidate.License;
    public bool HasLicense => Candidate.License is not null;
}

/// <summary>A job row; refreshed from <see cref="ImportService.JobChanged"/>.</summary>
public sealed partial class ImportJobViewModel(ImportJob job) : ObservableObject
{
    public ImportJob Job { get; } = job;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _canOpen;
    [ObservableProperty] private bool _failed;

    public void Update()
    {
        var c = Job.Candidate;
        Title = c.Title;
        Detail = string.Join(" · ", new[] { c.Artist, c.Album }.Where(s => !string.IsNullOrEmpty(s)));
        Progress = Job.Progress;
        Running = Job.Status == ImportStatus.Running;
        Failed = Job.Status == ImportStatus.Failed;
        CanCancel = Job.Status is ImportStatus.Queued or ImportStatus.Running;
        CanRetry = Job.Status is ImportStatus.Failed or ImportStatus.Cancelled;
        CanOpen = Job.Status == ImportStatus.Done && Job.FilePath is not null;
        State = Job.Status switch
        {
            ImportStatus.Queued => "Waiting",
            ImportStatus.Running => Job.Progress > 0 ? $"{Job.Progress:P0}" : "Starting…",
            ImportStatus.Done => "Imported",
            ImportStatus.Failed => Job.Error ?? "Failed",
            _ => "Cancelled",
        };
    }
}

/// <summary>
/// Pulls music in from integrations: a link for yt-dlp (when installed), or zips, folders and files from stores
/// and exports. Everything lands in the import folder, a normal source, and is scanned like any other file.
/// </summary>
public sealed partial class ImportViewModel : PageViewModel
{
    private readonly Dictionary<Ulid, ImportJobViewModel> _jobRows = new();
    private bool _detected;
    private readonly HashSet<Ulid> _doneSinceNotice = new();

    public ImportViewModel(MainViewModel main) : base(main)
    {
        Host.Import.JobChanged += job => Dispatcher.UIThread.Post(() => OnJobChanged(job));
        Host.Import.JobsChanged += () => Dispatcher.UIThread.Post(SyncJobs);
    }

    public override string Title => "Import";

    public ObservableCollection<CandidateViewModel> Candidates { get; } = new();
    public ObservableCollection<ImportJobViewModel> Jobs { get; } = new();

    [ObservableProperty] private string? _input;
    [ObservableProperty] private bool _resolving;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string? _previewTitle;
    [ObservableProperty] private string? _previewNote;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _hasFinished;

    [ObservableProperty] private bool _ytDlpAvailable;
    [ObservableProperty] private string _ytDlpStatus = "Looking…";
    [ObservableProperty] private string? _ytDlpNote;
    [ObservableProperty] private bool _updating;
    [ObservableProperty] private string _importFolder = "";

    public string ImportHint => "Import only music you have the rights to keep.";

    /// <summary>Failed jobs say why in the accent colour; others stay muted.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter FailedBrush =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.IBrush?>(failed =>
            Avalonia.Application.Current!.TryGetResource(failed ? "Accent" : "Muted", null, out var b) ? b as Avalonia.Media.IBrush : null);

    /// <summary>The page refreshes on every library commit; detection runs a process, so only the first time (or on request).</summary>
    public override async Task RefreshAsync()
    {
        ImportFolder = Host.Import.ImportFolder;
        SyncJobs();
        if (!_detected) await DetectAsync();
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        _detected = true;
        var s = await Host.Import.YtDlp.DetectAsync();
        YtDlpAvailable = s.Available;
        YtDlpStatus = s.Available ? $"yt-dlp {s.Version}" : "yt-dlp not found";
        YtDlpNote = s.Available ? s.Location : s.Note;
    }

    [RelayCommand]
    private async Task LocateYtDlp()
    {
        if (Main.PickProgram is null || await Main.PickProgram() is not { } path) return;
        await Host.Import.SetYtDlpPathAsync(path);
        await DetectAsync();
    }

    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        Updating = true;
        try
        {
            Message = await Task.Run(() => Host.Import.YtDlp.UpdateAsync());
            await DetectAsync();
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { Updating = false; }
    }

    [RelayCommand]
    private async Task ChangeFolder()
    {
        if (Main.PickFolder is null || await Main.PickFolder() is not { } path) return;
        await Host.Import.SetImportFolderAsync(path);
        ImportFolder = Host.Import.ImportFolder;
        _ = Main.Sources.RefreshAsync();
    }

    // ------------------------------------------------------------------ resolving

    /// <summary>Enter in the box, or a pasted link.</summary>
    [RelayCommand]
    private Task Resolve() => string.IsNullOrWhiteSpace(Input) ? Task.CompletedTask : ResolveManyAsync([Input.Trim()]);

    [RelayCommand]
    private async Task PickFiles()
    {
        if (Main.PickImportFiles is null) return;
        var paths = await Main.PickImportFiles();
        if (paths.Count > 0) await ResolveManyAsync(paths);
    }

    [RelayCommand]
    private async Task PickFolder()
    {
        if (Main.PickFolder is null || await Main.PickFolder() is not { } path) return;
        await ResolveManyAsync([path]);
    }

    /// <summary>Files or folders dropped on the page, or chosen in a picker.</summary>
    public async Task ResolveManyAsync(IReadOnlyList<string> inputs)
    {
        if (Resolving) return;
        Resolving = true;
        Message = inputs.Count == 1 && Host.Import.YtDlp.CanHandle(inputs[0]) ? "Asking yt-dlp…" : "Reading…";
        ClearPreview();
        try
        {
            var previews = new List<ImportPreview>();
            foreach (var input in inputs)
                previews.Add(await Task.Run(() => Host.Import.ResolveAsync(input)));
            var items = previews.SelectMany(p => p.Items).ToList();
            foreach (var c in items)
            {
                var row = new CandidateViewModel(c);
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CandidateViewModel.Selected)) CountSelected(); };
                Candidates.Add(row);
            }
            PreviewTitle = previews.Count == 1 ? previews[0].Title : $"{inputs.Count} items";
            PreviewNote = previews.Select(p => p.Note).FirstOrDefault(n => n is not null);
            HasPreview = Candidates.Count > 0;
            int owned = items.Count(c => c.AlreadyInLibrary);
            Message = owned > 0 ? $"{Converters.Count(owned, "track")} already in your library (unticked)." : null;
            CountSelected();
        }
        catch (ImportException ex) { Message = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("Import", "Resolving failed", ex);
            Message = $"Could not read that: {ex.Message}";
        }
        finally { Resolving = false; }
    }

    private void CountSelected()
    {
        SelectedCount = Candidates.Count(c => c.Selected);
        ToggleAllText = SelectedCount == Candidates.Count ? "Select none" : "Select all";
    }

    [ObservableProperty] private string _toggleAllText = "Select all";

    [RelayCommand]
    private void ToggleAll()
    {
        bool select = Candidates.Any(c => !c.Selected);
        foreach (var c in Candidates) c.Selected = select;
    }

    [RelayCommand]
    private void ClearPreview()
    {
        Candidates.Clear();
        HasPreview = false;
        PreviewTitle = null;
        PreviewNote = null;
        SelectedCount = 0;
    }

    [RelayCommand]
    private async Task ImportSelected()
    {
        var chosen = Candidates.Where(c => c.Selected).Select(c => c.Candidate).ToList();
        if (chosen.Count == 0) return;
        if (chosen.Any(c => c.ProviderId == YtDlpProvider.ProviderId) && !YtDlpAvailable)
        {
            Message = "yt-dlp is not available.";
            return;
        }
        ClearPreview();
        Input = null;
        Message = null;
        await Host.Import.EnqueueAsync(chosen);
    }

    // ------------------------------------------------------------------ jobs

    private void SyncJobs()
    {
        var jobs = Host.Import.Jobs;
        var ids = jobs.Select(j => j.Id).ToHashSet();
        foreach (var gone in _jobRows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            Jobs.Remove(_jobRows[gone]);
            _jobRows.Remove(gone);
        }
        // Queue order, so an album reads 1, 2, 3.
        foreach (var job in jobs.OrderBy(j => j.CreatedAt))
        {
            if (_jobRows.ContainsKey(job.Id)) continue;
            var row = new ImportJobViewModel(job);
            row.Update();
            _jobRows[job.Id] = row;
            int at = 0;
            while (at < Jobs.Count && Jobs[at].Job.CreatedAt < job.CreatedAt) at++;
            Jobs.Insert(at, row);
        }
        UpdateJobFlags();
    }

    private void OnJobChanged(ImportJob job)
    {
        if (!_jobRows.TryGetValue(job.Id, out var row)) { SyncJobs(); return; }
        row.Update();
        if (job.Status == ImportStatus.Done) _doneSinceNotice.Add(job.Id);
        UpdateJobFlags();
        bool idle = !Host.Import.Jobs.Any(j => j.Status is ImportStatus.Queued or ImportStatus.Running);
        if (idle && _doneSinceNotice.Count > 0)
        {
            Main.Notify($"Imported {Converters.Count(_doneSinceNotice.Count, "track")}");
            _doneSinceNotice.Clear();
        }
    }

    private void UpdateJobFlags()
    {
        HasJobs = Jobs.Count > 0;
        HasFinished = Jobs.Any(j => !j.CanCancel);
    }

    [RelayCommand] private void CancelJob(ImportJobViewModel row) => Host.Import.Cancel(row.Job.Id);
    [RelayCommand] private void RetryJob(ImportJobViewModel row) => Host.Import.Retry(row.Job.Id);
    [RelayCommand] private Task ClearFinished() => Host.Import.ClearFinishedAsync();

    [RelayCommand]
    private async Task OpenJob(ImportJobViewModel row)
    {
        if (row.Job.FilePath is not { } path) return;
        var album = await Task.Run(() =>
        {
            using var c = Host.Db.Open();
            var releaseId = c.QuerySingle("""
                SELECT m.release_id FROM audio_file f JOIN track t ON t.id = f.track_id JOIN medium m ON m.id = t.medium_id
                WHERE f.path = $1
                """, r => r.GetString(0), path);
            return releaseId is null ? null : Host.Queries.Album(Ulid.Parse(releaseId));
        });
        if (album is not null) Main.ShowAlbum(album);
        else Main.Notify("That file is no longer in the library.");
    }
}
