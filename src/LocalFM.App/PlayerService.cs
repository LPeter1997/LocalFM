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
            VolumeChanged?.Invoke(_volume);
        }
    }

    /// <summary>Raised after an explicit position change (seek or restart), with the new position.</summary>
    public event Action<TimeSpan>? Seeked;
    public event Action<float>? VolumeChanged;

    /// <summary>Raised when the current item, playback state, or queue content changes.</summary>
    public event Action? Changed;
    public event Action<string>? Error;

    // ------------------------------------------------------------------ transport

    public void PlayTracks(IReadOnlyList<TrackInfo> tracks, int startIndex = 0)
    {
        if (tracks.Count == 0 || Blocked()) return;
        lock (_gate)
        {
            Queue.Replace(tracks, startIndex);
            StartCurrent(TimeSpan.Zero);
        }
    }

    public void PlayItem(Ulid itemId)
    {
        if (Blocked()) return;
        lock (_gate)
        {
            if (Queue.SetCurrent(itemId)) StartCurrent(TimeSpan.Zero);
        }
    }

    public void PlayPause()
    {
        if (Blocked()) return;
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

    public void Play()
    {
        if (_engine.State != PlaybackState.Playing) PlayPause();
    }

    public void Pause()
    {
        if (Blocked()) return;
        if (_engine.State == PlaybackState.Playing) _engine.Pause();
    }

    public void Next()
    {
        if (Blocked()) return;
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
        if (Blocked()) return;
        lock (_gate)
        {
            if (_engine.State != PlaybackState.Stopped && _engine.Position > TimeSpan.FromSeconds(3))
            {
                _engine.Seek(TimeSpan.Zero);
                Seeked?.Invoke(TimeSpan.Zero);
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
        if (Blocked()) return;
        if (_engine.State == PlaybackState.Stopped)
        {
            lock (_gate) if (Queue.Current is { } c) _resume = (c, position);
            Changed?.Invoke();
            Seeked?.Invoke(position);
            return;
        }
        _engine.Seek(position);
        Seeked?.Invoke(position);
    }

    public void Stop() { if (!Blocked()) _engine.Stop(); }

    // ------------------------------------------------------------------ queue editing

    public void Enqueue(IReadOnlyList<TrackInfo> tracks) { lock (_gate) if (!Blocked()) Queue.Append(tracks); }
    public void PlayNext(IReadOnlyList<TrackInfo> tracks) { lock (_gate) if (!Blocked()) Queue.InsertNext(tracks); }
    public void MoveItem(int from, int to) { lock (_gate) if (!Blocked()) Queue.Move(from, to); }
    public void ClearUpcoming() { lock (_gate) if (!Blocked()) Queue.ClearUpcoming(); }
    public void SetShuffle(bool on) { lock (_gate) if (!Blocked()) Queue.SetShuffle(on); }
    public void Reshuffle() { lock (_gate) if (!Blocked()) Queue.Reshuffle(); }

    public void RemoveItem(Ulid itemId)
    {
        lock (_gate)
        {
            if (Blocked()) return;
            bool wasCurrent = Queue.Current?.Id == itemId;
            Queue.Remove(itemId);
            if (wasCurrent && _engine.State != PlaybackState.Stopped)
            {
                if (Queue.Current is not null) StartCurrent(TimeSpan.Zero);
                else _engine.Stop();
            }
        }
    }

    /// <summary>Re-reads the queued tracks' metadata from the library (titles, artists, covers after an edit).</summary>
    public async Task RefreshMetadataAsync()
    {
        var ids = Snapshot(out _).Select(i => i.Track.TrackId).Distinct().ToList();
        var fresh = await Task.Run(() => ids.Select(_queries.ByTrack).OfType<TrackInfo>().ToDictionary(t => t.TrackId)).ConfigureAwait(false);
        lock (_gate) Queue.UpdateTracks(fresh);
    }

    public RepeatMode Repeat
    {
        get => Queue.Repeat;
        set { lock (_gate) { if (Blocked()) return; Queue.Repeat = value; UpdateNext(); } Changed?.Invoke(); }
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
        _engine.SetNext(next is null ? null : Handle(next, item));
    }

    private void OnQueueChanged()
    {
        _dirty = true;
        UpdateNext();
        RaiseChanged();
    }

    /// <summary>Never raises <see cref="Changed"/> while holding the gate: handlers read the player (taking the gate)
    /// under locks of their own (MPRIS), and the two lock orders would deadlock.</summary>
    private void RaiseChanged()
    {
        if (_gate.IsHeldByCurrentThread) ThreadPool.QueueUserWorkItem(static p => p.Changed?.Invoke(), this, preferLocal: false);
        else Changed?.Invoke();
    }

    /// <summary>Tells the engine what follows the current item (it pre-opens it for gapless playback).</summary>
    private void UpdateNext()
    {
        if (_engine.State == PlaybackState.Stopped && _engine.Current is null) return;
        var next = Queue.PeekNext();
        _engine.SetNext(next is null ? null : Handle(next, Queue.Current));
    }

    private TrackHandle Handle(QueueItem item, QueueItem? previous = null) =>
        _follow is { } f ? f.Handle(item, Gain(item.Track))
        : new(item.Track.Path, item.Id, Gain(item.Track), JoinGapless: GaplessAlbums && previous is not null && Continues(previous.Track, item.Track));

    /// <summary>Whether <paramref name="b"/> is the track right after <paramref name="a"/> on the same release.</summary>
    public static bool Continues(TrackInfo a, TrackInfo b) =>
        a.ReleaseId == b.ReleaseId &&
        ((b.Disc == a.Disc && b.Position == a.Position + 1) || (b.Disc == a.Disc + 1 && b.Position == 1));

    /// <summary>With crossfade on, still play consecutive album tracks gaplessly (live albums, DJ mixes, segues).</summary>
    public bool GaplessAlbums { get; set; } = true;

    /// <summary>Applies just the equalizer (cheap; for slider drags).</summary>
    /// <summary>Latest heard audio for visualizations (interleaved stereo); never blocks playback.</summary>
    public long ReadVisualization(Span<float> interleaved) => _engine.ReadVisualization(interleaved);
    public int SampleRate => _engine.SampleRate;

    public void ApplyEqualizer(SoundSettings s) => _engine.SetEqualizer(s.Bands(), s.EffectivePreampDb);

    /// <summary>Applies equalizer, crossfade and loudness settings. The EQ is heard at once; the rest from the next track.</summary>
    public void ApplySound(SoundSettings s)
    {
        _engine.SetEqualizer(s.Bands(), s.EffectivePreampDb);
        lock (_gate)
        {
            // While following a vibe the host's crossfade shapes the shared timeline; ours waits until we leave.
            var fade = TimeSpan.FromSeconds(s.CrossfadeSeconds);
            if (_own is not null) _own = _own with { Crossfade = fade };
            else _engine.Crossfade = fade;
            ReplayGain = s.ReplayGain;
            GaplessAlbums = s.GaplessAlbums;
            UpdateNext();
        }
    }

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
                    if (Queue.Current is { } cur && !IsFollowing) // a vibe's tracks are not ours to count
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
                    if (failedToStart && !IsFollowing) // following, the vibe decides what plays
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
            // Following a vibe: what is ours is the queue put aside when we joined.
            if (_own is { } own) state = new SavedState
            {
                TrackIds = own.Items.Select(i => i.Track.TrackId.ToString()).ToList(),
                CurrentIndex = own.Index,
                PositionMs = (long)own.Position.TotalMilliseconds,
                Volume = _volume,
                Repeat = own.Repeat,
            };
            else state = new SavedState
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

    // ------------------------------------------------------------------ follow mode (listening to a vibe)

    private IFollowSource? _follow;
    private OwnState? _own;
    private sealed record OwnState(List<QueueItem> Items, int Index, TimeSpan Position, RepeatMode Repeat,
        IReadOnlyList<Ulid>? OriginalOrder, TimeSpan Crossfade);

    /// <summary>True while playing someone else's vibe: transport and queue edits are ignored (volume and sound
    /// settings still apply) and nothing is recorded in the listening history.</summary>
    public bool IsFollowing => _follow is not null;

    /// <summary>Whether <see cref="Position"/> moves in real time (not for a moment after a start or seek).</summary>
    public bool PositionIsLive => _engine.PositionIsLive;

    /// <summary>Raised when something tried to play, skip, seek or edit the queue while following a vibe (and
    /// was ignored), so the UI can say why nothing happened.</summary>
    public event Action? IgnoredWhileFollowing;

    private bool Blocked()
    {
        if (_follow is null) return false;
        IgnoredWhileFollowing?.Invoke();
        return true;
    }

    /// <summary>How long audio takes from the engine to the speaker.</summary>
    public TimeSpan OutputLatency => _engine.OutputLatency;
    /// <summary>The crossfade the engine plays with right now.</summary>
    public TimeSpan Crossfade => _engine.Crossfade;

    /// <summary>Puts our own queue and position aside and hands playback to <paramref name="source"/>.</summary>
    public void EnterFollowMode(IFollowSource source)
    {
        SaveState(force: true);
        lock (_gate)
        {
            if (_follow is null)
            {
                FinishListen(false);
                _own = new OwnState([.. Queue.Items], Queue.CurrentIndex, Position, Queue.Repeat, Queue.OriginalOrder?.ToList(), _engine.Crossfade);
                _resume = null;
                _engine.Stop();
                _follow = source;
                Queue.Repeat = RepeatMode.Off;
                Queue.Restore([], 0);
            }
            _follow = source;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops the vibe's audio and brings back our own queue where it was, paused.</summary>
    public void LeaveFollowMode()
    {
        lock (_gate)
        {
            if (_follow is null || _own is not { } own) return;
            _follow = null;
            _own = null;
            _engine.Stop();
            _engine.Crossfade = own.Crossfade;
            Queue.Repeat = own.Repeat;
            Queue.Restore(own.Items, own.Index, own.OriginalOrder);
            if (Queue.Current is { } c) _resume = (c, own.Position);
            _dirty = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Following: the vibe's items around the current one, in play order. The current item stays the
    /// one the engine plays (it follows gapless transitions itself).</summary>
    public void FollowSetQueue(IReadOnlyList<QueueItem> items)
    {
        lock (_gate)
        {
            if (_follow is null) return;
            if (items.SequenceEqual(Queue.Items)) return; // same items, same metadata
            int idx = Queue.Current is { } cur ? items.ToList().FindIndex(i => i.Id == cur.Id) : -1;
            Queue.Restore(items, Math.Max(0, idx));
        }
    }

    /// <summary>Following: starts <paramref name="itemId"/>, at a position asked for once the file is open.</summary>
    public void FollowPlay(Ulid itemId, Func<TimeSpan> startAt)
    {
        lock (_gate)
        {
            if (_follow is null || !Queue.SetCurrent(itemId) || Queue.Current is not { } item) return;
            _engine.Play(Handle(item), startAt);
            var next = Queue.PeekNext();
            _engine.SetNext(next is null ? null : Handle(next, item));
        }
    }

    public void FollowSeek(Func<TimeSpan> position) { if (IsFollowing) _engine.SeekTo(position); }
    public void FollowPause() { if (IsFollowing) _engine.Pause(); }
    public void FollowResume() { if (IsFollowing) _engine.Resume(); }
    public void FollowStop() { if (IsFollowing) _engine.Stop(); }

    public void FollowCrossfade(TimeSpan crossfade)
    {
        if (IsFollowing && _engine.Crossfade != crossfade) _engine.Crossfade = crossfade;
    }

    /// <summary>The item the engine is playing (null when stopped).</summary>
    public Ulid? AudibleItem => _engine.Current?.Token as Ulid?;

    public void Dispose()
    {
        _saveTimer.Dispose();
        FinishListen(false);
        SaveState(force: true);
        _cts.Cancel();
        try { _eventLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}

/// <summary>Supplies engine handles for a followed vibe's items.</summary>
public interface IFollowSource
{
    /// <summary>The handle for <paramref name="item"/>. Must be equal (same opener) each time it is asked for the
    /// same item, or the engine re-opens the upcoming track.</summary>
    TrackHandle Handle(QueueItem item, float gain);
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
