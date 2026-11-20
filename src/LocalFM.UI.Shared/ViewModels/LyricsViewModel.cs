using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Metadata;

namespace LocalFM.UI.ViewModels;

public sealed partial class LyricLineViewModel(LyricLine line, int index, Func<TimeSpan> clock) : ObservableObject
{
    public LyricLine Line { get; } = line;
    public int Index { get; } = index;
    public string Text => Line.Text.Length == 0 ? "♪" : Line.Text;
    /// <summary>Playback position the word highlight follows (shared by all lines).</summary>
    public Func<TimeSpan> Clock { get; } = clock;
    /// <summary>When the next line starts (the end of this one, for synced lyrics).</summary>
    public TimeSpan? End { get; init; }
    /// <summary>When each word is sung; empty when the line is only highlighted as a whole.</summary>
    [ObservableProperty] private IReadOnlyList<WordTiming> _words = [];
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
    private LyricsView? _view;
    /// <summary>A tiny lead makes the highlight feel in time with the voice.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(150);

    private const string MiniLyricsKey = "ui.miniLyrics";

    public LyricsViewModel(AppHost host)
    {
        _host = host;
        _miniLyrics = host.History.Get(MiniLyricsKey) != "0";
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Follow());
        _timer.Start();
        Lines.CollectionChanged += (_, _) =>
        {
            if (Lines.Count == 0) CurrentLine = null;
            OnPropertyChanged(nameof(MiniLineShown));
        };
        host.Player.Changed += () => Dispatcher.UIThread.Post(OnTrackMaybeChanged);
        OnTrackMaybeChanged();
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _synced;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _source;
    /// <summary>The line being sung (synced lyrics only); null before the first line.</summary>
    [ObservableProperty] private LyricLineViewModel? _currentLine;

    /// <summary>The miniplayer shows the line being sung (the person's choice, remembered).</summary>
    [ObservableProperty] private bool _miniLyrics;
    /// <summary>Set while the miniplayer is on screen.</summary>
    [ObservableProperty] private bool _miniVisible;
    /// <summary>The miniplayer has a line to show: switched on, and this track has synced lyrics.</summary>
    public bool MiniLineShown => MiniLyrics && Synced && Lines.Count > 0;

    /// <summary>Raised when the highlighted line changes (the view scrolls it into view).</summary>
    public event Action<int>? CurrentLineChanged;

    /// <summary>Someone is looking at the lyrics: the panel, or the miniplayer's line.</summary>
    private bool Watched => IsOpen || (MiniVisible && MiniLyrics);

    partial void OnIsOpenChanged(bool value) => LoadIfWatched();
    partial void OnMiniVisibleChanged(bool value) => LoadIfWatched();
    partial void OnSyncedChanged(bool value) => OnPropertyChanged(nameof(MiniLineShown));

    partial void OnMiniLyricsChanged(bool value)
    {
        _ = _host.History.SetAsync(MiniLyricsKey, value ? "1" : "0");
        OnPropertyChanged(nameof(MiniLineShown));
        LoadIfWatched();
    }

    [RelayCommand]
    private void ToggleMiniLyrics() => MiniLyrics = !MiniLyrics;

    private void LoadIfWatched()
    {
        if (Watched && Lines.Count == 0 && _host.Player.Current?.Track is { } t) _ = Load(t, allowFetch: true);
    }

    private void OnTrackMaybeChanged()
    {
        var track = _host.Player.Current?.Track;
        if (track?.RecordingId == _recording) return;
        _recording = track?.RecordingId;
        _view = null;
        Lines.Clear();
        _current = -1;
        Status = null;
        Source = null;
        if (track is null) return;
        // Fetch in the background when the track starts (cached; LRCLIB is asked once per track).
        _ = Load(track, allowFetch: Watched || _host.Metadata.AutoFetchLyrics);
    }

    [RelayCommand]
    private void Refetch()
    {
        if (_host.Player.Current?.Track is { } t) _ = Load(t, allowFetch: true, force: true);
    }

    /// <summary>LRCLIB often has several timings for one song; step to the next when this one is off.</summary>
    [RelayCommand]
    private async Task OtherVersion()
    {
        if (_host.Player.Current?.Track is not { } track) return;
        if (_host.Vibe.Follower is not null) { Source = "Only the host can change the lyrics."; return; }
        IsLoading = true;
        try
        {
            var next = await Task.Run(() => _host.Metadata.NextLyricsVersionAsync(track));
            if (track.RecordingId != _recording) return;
            if (next is not { } n) { Source = "No other synced version on LRCLIB."; return; }
            Show(n.View);
            Source = $"Lyrics from LRCLIB · version {n.Version} of {n.Versions}";
        }
        catch (Exception ex) { Source = $"Lookup failed: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private async Task Load(TrackInfo track, bool allowFetch, bool force = false)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        Status = null;
        try
        {
            // Listening to a vibe: the host's lyrics (they may have edited them; the track is not in our library).
            var view = _host.Vibe.Follower is { } vibe
                ? await vibe.LyricsAsync(track, cts.Token)
                : await Task.Run(() => _host.Metadata.GetLyricsAsync(track, allowFetch, cts.Token, force));
            if (cts.IsCancellationRequested || track.RecordingId != _recording) return;
            Lines.Clear();
            _view = null;
            _current = -1;
            if (view is null)
            {
                Status = _host.Vibe.Follower is not null ? "The host has no lyrics for this song."
                    : !_host.Metadata.OnlineEnabled ? "No lyrics stored (online lookups are off)."
                    : allowFetch ? "No lyrics found." : "No lyrics stored.";
                return;
            }
            Show(view);
        }
        catch (OperationCanceledException) { }
        finally { if (_cts == cts) IsLoading = false; }
    }

    private void Show(LyricsView view)
    {
        Lines.Clear();
        _current = -1;
        Status = null;
        _view = view;
        Func<TimeSpan> clock = () => _host.Player.Position + Lead;
        for (int i = 0; i < view.Lines.Count; i++)
        {
            var end = i + 1 < view.Lines.Count ? view.Lines[i + 1].Time : null;
            Lines.Add(new LyricLineViewModel(view.Lines[i], i, clock) { End = end });
        }
        Synced = view.Synced;
        Retime();
        Source = view.Data.Provider switch { "lrclib" => "Lyrics from LRCLIB", "tags" => "Lyrics from file tags", var p => $"Lyrics from {p}" };
    }

    /// <summary>Recomputes the word timings (after the estimate setting changes).</summary>
    public void Retime()
    {
        if (_view is null || Lines.Count != _view.Lines.Count) return;
        var timings = _view.Synced ? Karaoke.Timings(_view.Lines, _host.Metadata.EstimateWordTiming) : null;
        for (int i = 0; i < Lines.Count; i++) Lines[i].Words = timings?[i] ?? [];
    }

    public void SeekTo(LyricLineViewModel line)
    {
        if (line.Line.Time is { } t && !_host.Player.IsFollowing) _host.Player.Seek(t, "lyrics line");
    }

    private void Follow()
    {
        if (!Watched || !Synced || Lines.Count == 0) return;
        var pos = _host.Player.Position + Lead;
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
        CurrentLine = idx >= 0 ? Lines[idx] : null;
        CurrentLineChanged?.Invoke(idx);
    }
}
