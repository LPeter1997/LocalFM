using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library.Enrich;

namespace LocalFM.UI.ViewModels;

/// <summary>Search MusicBrainz for an album, pick the right edition, apply it.</summary>
public sealed partial class MatchViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Ulid _releaseId;
    private readonly Func<Task> _afterApply;

    public MatchViewModel(AppHost host, Ulid releaseId, string title, string? artist, Func<Task> afterApply)
    {
        _host = host;
        _releaseId = releaseId;
        _afterApply = afterApply;
        _title = title;
        _artist = artist;
    }

    public ObservableCollection<ReleaseCandidate> Candidates { get; } = new();

    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private ReleaseCandidate? _selected;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _fetchCover = true;

    private void OnRetrying(string provider, TimeSpan delay) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Busy) Status = $"{provider} is busy, retrying in {delay.TotalSeconds:0.#} s…"; });

    [RelayCommand]
    private async Task Search()
    {
        if (!_host.Metadata.OnlineEnabled) { Status = "Online lookups are turned off (Sources page)."; return; }
        Busy = true;
        Status = "Searching MusicBrainz…";
        _host.Metadata.Http.Retrying += OnRetrying;
        try
        {
            var q = await Task.Run(() => _host.Metadata.DescribeAlbum(_releaseId));
            var hits = await Task.Run(() => _host.Metadata.SearchAlbumAsync(q, Title, Artist));
            Candidates.Clear();
            foreach (var h in hits) Candidates.Add(h);
            Selected = Candidates.FirstOrDefault();
            Status = hits.Count == 0 ? "Nothing found (or MusicBrainz is unavailable). Try a shorter title or no artist." : $"{hits.Count} releases. The best fit for your {q.TrackCount} tracks is first.";
        }
        catch (Exception ex)
        {
            Status = $"Search failed: {ex.Message}";
            Log.Warn("Match", "Search failed", ex);
        }
        finally { Busy = false; _host.Metadata.Http.Retrying -= OnRetrying; }
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (Selected is not { } c) return;
        Busy = true;
        Status = FetchCover ? "Downloading release data and cover…" : "Downloading release data…";
        _host.Metadata.Http.Retrying += OnRetrying;
        try
        {
            var result = await Task.Run(() => _host.Metadata.ApplyReleaseAsync(_releaseId, c.Id, FetchCover));
            if (result is null) { Status = "MusicBrainz did not return that release. Try again later."; return; }
            if (!result.Applied)
            {
                Status = "None of your tracks fit this release (titles and lengths differ), so nothing was changed. Try another edition.";
                return;
            }
            Status = $"Applied: {result.TracksMatched} tracks matched" +
                     (result.TracksUnmatched > 0 ? $", {result.TracksUnmatched} not matched (left as they were)" : "") +
                     (result.CoverStored ? ", cover saved" : "") + ".";
            await _afterApply();
        }
        catch (Exception ex)
        {
            Status = $"Applying failed: {ex.Message}";
            Log.Warn("Match", "Apply failed", ex);
        }
        finally { Busy = false; _host.Metadata.Http.Retrying -= OnRetrying; }
    }
}
