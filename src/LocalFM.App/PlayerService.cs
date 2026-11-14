using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalFM.Core;
using LocalFM.Library;
using LocalFM.Playback;

namespace LocalFM.App;

public enum ReplayGainMode { Off, Track, Album }

/// <summary>
/// The application's player: keeps the <see cref="PlayQueue"/> and the <see cref="PlaybackEngine"/> in
/// step, records listening history and remembers where playback was. All public members are thread-safe
/// and non-blocking; events are raised on a background thread (UIs marshal them).
/// </summary>
public sealed class PlayerService : IDisposable
{
    private const string Area = "Player";
    private const string StateKey = "player.state";

    private readonly PlaybackEngine _engine;
    private readonly History _history;
    private readonly LibraryQueries _queries;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _eventLoop;
    private readonly Timer _saveTimer;

    private readonly List<TrackInfo> _session = new(); // everything that started playing this session
    private (QueueItem Item, TimeSpan Position)? _resume; // restored position, applied on first play
    private ListenTracker? _listen;
    private float _volume = 0.8f;
    private bool _dirty;

    public PlayerService(PlaybackEngine engine, History history, LibraryQueries queries)
    {
        _engine = engine;
        _history = history;
        _queries = queries;
        Queue.Changed += OnQueueChanged;
        _engine.Volume = Perceptual(_volume);
        _eventLoop = Task.Run(EventLoop);
        _saveTimer = new Timer(_ => SaveState(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public PlayQueue Queue { get; } = new();

    public PlaybackState State => _engine.State;
    public TimeSpan Position => _resume is { } r && _engine.State == PlaybackState.Stopped ? r.Position : _engine.Position;
    public TimeSpan? Duration => _engine.Duration ?? (Current?.Track.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
    public QueueItem? Current { get { lock (_gate) return Queue.Current; } }
    public ReplayGainMode ReplayGain { get; set; } = ReplayGainMode.Album;
    public IReadOnlyList<TrackInfo> SessionHistory { get { lock (_gate) return [.. _session]; } }

    /// <summary>0..1 slider value; mapped to a perceptual (cubic) gain curve.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            _engine.Volume = Perceptual(_volume);
            _dirty = true;
        }
    }

    /// <summary>Raised when the current item, playback state, or queue content changes.</summary>
    public event Action? Changed;
    public event Action<string>? Error;

    // ------------------------------------------------------------------ transport

    public void PlayTracks(IReadOnlyList<TrackInfo> tracks, int startIndex = 0)
    {
        if (tracks.Count == 0) return;
        lock (_gate)
        {
            Queue.Replace(tracks, startIndex);
            StartCurrent(TimeSpan.Zero);
        }
    }

    public void PlayItem(Ulid itemId)
    {
        lock (_gate)
        {
            if (Queue.SetCurrent(itemId)) StartCurrent(TimeSpan.Zero);
        }
    }

    public void PlayPause()
    {
        switch (_engine.State)
        {
            case PlaybackState.Playing: _engine.Pause(); break;
            case PlaybackState.Paused: _engine.Resume(); break;
            default:
                lock (_gate)
                {
                    var start = _resume is { } r && r.Item == Queue.Current ? r.Position : TimeSpan.Zero;
                    StartCurrent(start);
                }
                break;
        }
    }

    public void Next()
    {
        lock (_gate)
        {
            var next = Queue.PeekNext();
            if (next is null || (Queue.Repeat == RepeatMode.One && Queue.Items.Count > 1))
                next = Queue.CurrentIndex + 1 < Queue.Items.Count ? Queue.Items[Queue.CurrentIndex + 1] : null;
            if (next is null) return;
            Queue.SetCurrent(next.Id);
            StartCurrent(TimeSpan.Zero);
        }
    }

    /// <summary>Restarts the track when more than 3 s in, otherwise goes to the previous one.</summary>
    public void Previous()
    {
        lock (_gate)
        {
            if (_engine.State != PlaybackState.Stopped && _engine.Position > TimeSpan.FromSeconds(3))
            {
                _engine.Seek(TimeSpan.Zero);
                return;
            }
            var prev = Queue.PeekPrevious();
            if (prev is null) { _engine.Seek(TimeSpan.Zero); return; }
            Queue.SetCurrent(prev.Id);
            StartCurrent(TimeSpan.Zero);
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_engine.State == PlaybackState.Stopped)
        {
            lock (_gate) if (Queue.Current is { } c) _resume = (c, position);
            Changed?.Invoke();
            return;
        }
        _engine.Seek(position);
    }

    public void Stop() => _engine.Stop();

    // ------------------------------------------------------------------ queue editing

    public void Enqueue(IReadOnlyList<TrackInfo> tracks) { lock (_gate) Queue.Append(tracks); }
    public void PlayNext(IReadOnlyList<TrackInfo> tracks) { lock (_gate) Queue.InsertNext(tracks); }
    public void MoveItem(int from, int to) { lock (_gate) Queue.Move(from, to); }
    public void ClearUpcoming() { lock (_gate) Queue.ClearUpcoming(); }
    public void SetShuffle(bool on) { lock (_gate) Queue.SetShuffle(on); }
    public void Reshuffle() { lock (_gate) Queue.Reshuffle(); }

    public void RemoveItem(Ulid itemId)
    {
        lock (_gate)
        {
            bool wasCurrent = Queue.Current?.Id == itemId;
            Queue.Remove(itemId);
            if (wasCurrent && _engine.State != PlaybackState.Stopped)
            {
                if (Queue.Current is not null) StartCurrent(TimeSpan.Zero);
                else _engine.Stop();
            }
        }
    }

    public RepeatMode Repeat
    {
        get => Queue.Repeat;
        set { lock (_gate) { Queue.Repeat = value; UpdateNext(); } Changed?.Invoke(); }
    }

    public IReadOnlyList<QueueItem> Snapshot(out int currentIndex)
    {
        lock (_gate)
        {
            currentIndex = Queue.CurrentIndex;
            return [.. Queue.Items];
        }
    }

    // ------------------------------------------------------------------ engine glue

    private void StartCurrent(TimeSpan start)
    {
        var item = Queue.Current;
        if (item is null) return;
        _resume = null;
        _engine.Play(Handle(item), start);
        var next = Queue.PeekNext();
        _engine.SetNext(next is null ? null : Handle(next));
    }

    private void OnQueueChanged()
    {
        _dirty = true;
        UpdateNext();
        Changed?.Invoke();
    }

    /// <summary>Tells the engine what follows the current item (it pre-opens it for gapless playback).</summary>
    private void UpdateNext()
    {
        if (_engine.State == PlaybackState.Stopped && _engine.Current is null) return;
        var next = Queue.PeekNext();
        _engine.SetNext(next is null ? null : Handle(next));
    }

    private TrackHandle Handle(QueueItem item) => new(item.Track.Path, item.Id, Gain(item.Track));

    private float Gain(TrackInfo t)
    {
        double? db = ReplayGain switch
        {
            ReplayGainMode.Track => t.TrackGainDb ?? t.AlbumGainDb,
            ReplayGainMode.Album => t.AlbumGainDb ?? t.TrackGainDb,
            _ => null,
        };
        if (db is null) return 1f;
        double peak = (ReplayGain == ReplayGainMode.Album ? t.AlbumPeak ?? t.TrackPeak : t.TrackPeak ?? t.AlbumPeak) ?? 1.0;
        double gain = Math.Pow(10, db.Value / 20);
        if (peak > 0) gain = Math.Min(gain, 1.0 / peak); // never clip
        return (float)gain;
    }

    private static float Perceptual(float v) => v * v * v;

    private async Task EventLoop()
    {
        try
        {
            await foreach (var e in _engine.Events.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try { Handle(e); }
                catch (Exception ex) { Log.Error(Area, $"Handling {e} failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Handle(EngineEvent e)
    {
        switch (e)
        {
            case TrackStarted ts when ts.Track.Token is Ulid id:
                lock (_gate)
                {
                    // Gapless transitions are decided by the engine; follow it.
                    if (Queue.Current?.Id != id) Queue.SetCurrent(id);
                    if (Queue.Current is { } cur)
                    {
                        _session.Add(cur.Track);
                        _listen = new ListenTracker(cur.Track, _engine.State == PlaybackState.Playing);
                    }
                    UpdateNext();
                }
                break;

            case TrackEnded te:
                FinishListen(te.Completed);
                break;

            case StateChanged sc:
                _listen?.SetPlaying(sc.State == PlaybackState.Playing);
                _dirty = true;
                break;

            case TrackFailed tf when tf.Track.Token is Ulid id:
                Error?.Invoke($"Cannot play {Path.GetFileName(tf.Track.Path)}: {tf.Reason}");
                lock (_gate)
                {
                    Queue.MarkFailed(id);
                    // Did the item we were trying to start fail (rather than the pre-opened next one)?
                    bool failedToStart = Queue.Current?.Id == id && !(_engine.Current?.Token is Ulid audible && audible == id);
                    if (failedToStart)
                    {
                        var next = Queue.PeekNext();
                        if (next is not null && next.Id != id) { Queue.SetCurrent(next.Id); StartCurrent(TimeSpan.Zero); }
                    }
                    else UpdateNext();
                }
                break;

            case PlaybackFinished:
                break;
        }
        Changed?.Invoke();
    }

    private void FinishListen(bool completed)
    {
        var l = Interlocked.Exchange(ref _listen, null);
        if (l is null) return;
        var listened = l.Listened;
        if (listened < TimeSpan.FromSeconds(1)) return;
        _ = _history.RecordPlayAsync(l.Track.RecordingId, l.Track.FileId, l.StartedAt, listened, completed);
    }

    /// <summary>Wall-clock time actually spent playing a track (pauses excluded).</summary>
    private sealed class ListenTracker
    {
        private readonly Stopwatch _sw = new();
        public ListenTracker(TrackInfo track, bool playing) { Track = track; if (playing) _sw.Start(); }
        public TrackInfo Track { get; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public TimeSpan Listened => _sw.Elapsed;
        public void SetPlaying(bool playing) { if (playing) _sw.Start(); else _sw.Stop(); }
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Restores the saved queue and position (without starting playback).</summary>
    public void RestoreState()
    {
        try
        {
            string? json = _history.Get(StateKey);
            if (json is null) return;
            var saved = JsonSerializer.Deserialize(json, SavedStateContext.Default.SavedState);
            if (saved is null) return;
            _volume = saved.Volume;
            _engine.Volume = Perceptual(_volume);
            if (Enum.IsDefined(saved.Repeat)) Queue.Repeat = saved.Repeat;
            var items = new List<QueueItem>();
            int current = -1;
            for (int i = 0; i < saved.TrackIds.Count; i++)
            {
                if (!Ulid.TryParse(saved.TrackIds[i], out var tid) || _queries.ByTrack(tid) is not { } t) continue;
                if (i == saved.CurrentIndex) current = items.Count;
                items.Add(QueueItem.For(t));
            }
            lock (_gate)
            {
                Queue.Restore(items, current < 0 ? 0 : current);
                if (Queue.Current is { } c) _resume = (c, TimeSpan.FromMilliseconds(saved.PositionMs));
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn(Area, "Could not restore player state", ex);
        }
    }

    public void SaveState(bool force = false)
    {
        if (!force && !_dirty && _engine.State != PlaybackState.Playing) return;
        _dirty = false;
        SavedState state;
        lock (_gate)
        {
            state = new SavedState
            {
                TrackIds = Queue.Items.Select(i => i.Track.TrackId.ToString()).ToList(),
                CurrentIndex = Queue.CurrentIndex,
                PositionMs = (long)Position.TotalMilliseconds,
                Volume = _volume,
                Repeat = Queue.Repeat,
            };
        }
        string json = JsonSerializer.Serialize(state, SavedStateContext.Default.SavedState);
        _ = _history.SetAsync(StateKey, json);
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        FinishListen(false);
        SaveState(force: true);
        _cts.Cancel();
        try { _eventLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}

public sealed class SavedState
{
    public List<string> TrackIds { get; set; } = [];
    public int CurrentIndex { get; set; }
    public long PositionMs { get; set; }
    public float Volume { get; set; } = 0.8f;
    public RepeatMode Repeat { get; set; }
}

[JsonSerializable(typeof(SavedState))]
internal sealed partial class SavedStateContext : JsonSerializerContext;
