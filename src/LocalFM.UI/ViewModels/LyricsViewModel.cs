using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Metadata;

namespace LocalFM.UI.ViewModels;

public sealed partial class LyricLineViewModel(LyricLine line, int index) : ObservableObject
{
    public LyricLine Line { get; } = line;
    public int Index { get; } = index;
    public string Text => Line.Text.Length == 0 ? "♪" : Line.Text;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPast;
}

/// <summary>Lyrics of the current track, following playback for synced lyrics.</summary>
public sealed partial class LyricsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private Ulid? _recording;
    private CancellationTokenSource? _cts;
    private int _current = -1;

    public LyricsViewModel(AppHost host)
    {
        _host = host;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Follow());
        _timer.Start();
        host.Player.Changed += () => Dispatcher.UIThread.Post(OnTrackMaybeChanged);
        OnTrackMaybeChanged();
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _synced;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _source;

    /// <summary>Raised when the highlighted line changes (the view scrolls it into view).</summary>
    public event Action<int>? CurrentLineChanged;

    partial void OnIsOpenChanged(bool value)
    {
        if (value && Lines.Count == 0 && _host.Player.Current?.Track is { } t) _ = Load(t, allowFetch: true);
    }

    private void OnTrackMaybeChanged()
    {
        var track = _host.Player.Current?.Track;
        if (track?.RecordingId == _recording) return;
        _recording = track?.RecordingId;
        Lines.Clear();
        _current = -1;
        Status = null;
        Source = null;
        if (track is null) return;
        // Fetch in the background when the track starts (cached; LRCLIB is asked once per track).
        _ = Load(track, allowFetch: IsOpen || _host.Metadata.AutoFetchLyrics);
    }

    [RelayCommand]
    private void Refetch()
    {
        if (_host.Player.Current?.Track is { } t) _ = Load(t, allowFetch: true);
    }

    private async Task Load(TrackInfo track, bool allowFetch)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        Status = null;
        try
        {
            var view = await Task.Run(() => _host.Metadata.GetLyricsAsync(track, allowFetch, cts.Token));
            if (cts.IsCancellationRequested || track.RecordingId != _recording) return;
            Lines.Clear();
            _current = -1;
            if (view is null)
            {
                Status = !_host.Metadata.OnlineEnabled ? "No lyrics stored (online lookups are off)."
                    : allowFetch ? "No lyrics found." : "No lyrics stored.";
                return;
            }
            int i = 0;
            foreach (var l in view.Lines) Lines.Add(new LyricLineViewModel(l, i++));
            Synced = view.Synced;
            Source = view.Data.Provider switch { "lrclib" => "Lyrics from LRCLIB", "tags" => "Lyrics from file tags", var p => $"Lyrics from {p}" };
        }
        catch (OperationCanceledException) { }
        finally { if (_cts == cts) IsLoading = false; }
    }

    public void SeekTo(LyricLineViewModel line)
    {
        if (line.Line.Time is { } t) _host.Player.Seek(t);
    }

    private void Follow()
    {
        if (!IsOpen || !Synced || Lines.Count == 0) return;
        var pos = _host.Player.Position + TimeSpan.FromMilliseconds(150); // tiny lead feels in time
        int idx = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Line.Time is { } t && t <= pos) idx = i;
            else break;
        }
        if (idx == _current) return;
        _current = idx;
        for (int i = 0; i < Lines.Count; i++)
        {
            Lines[i].IsCurrent = i == idx;
            Lines[i].IsPast = i < idx;
        }
        CurrentLineChanged?.Invoke(idx);
    }
}
