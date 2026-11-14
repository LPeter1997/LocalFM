using System.Collections.Concurrent;
using System.Threading.Channels;
using LocalFM.Core;
using LocalFM.Native;

namespace LocalFM.Playback;

/// <summary>
/// Gapless playback engine.
///
/// Threads:
/// <list type="bullet">
/// <item>The audio device thread lives in native code and only drains a ring buffer (see localfm_native).
///   Pause, volume and seek fades are applied there, so nothing managed sits on the real-time path.</item>
/// <item>One engine thread owns all mutable state below: it applies commands, decodes into the ring,
///   splices the next track right after the current one ends (gapless), and maps the ring's read
///   position back to "which track, which frame" to raise events.</item>
/// <item>Files are opened on the thread pool, so slow disks never stall decoding of the current track.</item>
/// </list>
/// Public methods only enqueue commands and never block.
///
/// "Next" semantics: <see cref="SetNext"/> names the track that should follow the most recent
/// <see cref="Play"/> or, after a gapless transition, the track that just became audible. Callers
/// re-issue SetNext after every <see cref="TrackStarted"/>; until then the engine will not end
/// playback on its own.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private const int ChunkFrames = 2048;
    private const string Area = "Engine";

    private readonly AudioOutput _out;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Channel<EngineEvent> _events = Channel.CreateUnbounded<EngineEvent>();
    private readonly float[] _buf = new float[ChunkFrames * 2];
    private volatile bool _running = true;

    // published for readers on other threads
    private volatile Segment? _active;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private float _volume = 1f;
    private volatile IAudioProcessor[] _processors = [];

    // engine-thread state
    private readonly List<Segment> _pending = new(); // segments queued in the ring, not yet audible
    private readonly HashSet<Source> _live = new();
    private Source? _current;           // being decoded into the ring
    private Source? _next;              // opened, waiting to be spliced after _current
    private TrackHandle? _nextRequest;  // being opened
    private TrackHandle? _anchor;       // the track the caller's latest SetNext refers to
    private TrackHandle? _nextFor;      // the anchor at the time of the latest SetNext
    private int _playGen, _nextGen;

    private sealed class Source(TrackHandle track, Decoder decoder)
    {
        public TrackHandle Track { get; } = track;
        public Decoder Decoder { get; } = decoder;
        public TimeSpan? Duration { get; } = decoder.DurationFrames >= 0
            ? TimeSpan.FromSeconds((double)decoder.DurationFrames / decoder.OutputRate) : null;
        public bool AtEnd;
    }

    /// <summary>A run of ring frames starting at <see cref="WriteIndex"/>; Source null marks end of playback.</summary>
    private sealed record Segment(long WriteIndex, Source? Source, long StartFrame);

    public PlaybackEngine(OutputMode mode = OutputMode.Device, int sampleRate = 0, int ringMs = 1000)
    {
        _out = AudioOutput.Open(mode, sampleRate, ringMs);
        SampleRate = _out.SampleRate;
        _thread = new Thread(Run) { Name = "LocalFM engine", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public int SampleRate { get; }
    public ChannelReader<EngineEvent> Events => _events.Reader;
    public PlaybackState State => _state;
    public TrackHandle? Current => _active?.Source?.Track;
    public TimeSpan? Duration => _active?.Source?.Duration;
    public long Underruns => _out.Underruns;

    /// <summary>Frames queued in the ring that the device has not consumed yet.</summary>
    public long BufferedFrames => _out.WriteIndex - _out.ReadIndex;

    public TimeSpan Position
    {
        get
        {
            var a = _active;
            if (a?.Source is null) return TimeSpan.Zero;
            long frames = a.StartFrame + Math.Max(0, _out.ReadIndex - a.WriteIndex);
            var pos = TimeSpan.FromSeconds((double)frames / SampleRate);
            return a.Source.Duration is { } d && pos > d ? d : pos;
        }
    }

    /// <summary>Linear output gain, 0..1 (callers map a UI slider to a perceptual curve).</summary>
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); _out.SetVolume(_volume); }
    }

    public IReadOnlyList<IAudioProcessor> Processors
    {
        get => _processors;
        set => _processors = [.. value];
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Starts <paramref name="track"/> now (the current track keeps playing until it is ready).</summary>
    public void Play(TrackHandle track, TimeSpan start = default) => Post(() => DoPlay(track, start));

    /// <summary>Sets what follows the anchor track (see class remarks). Null means "stop after it".</summary>
    public void SetNext(TrackHandle? next) => Post(() => DoSetNext(next));

    public void Pause() => Post(() =>
    {
        if (_state != PlaybackState.Playing) return;
        _out.SetPaused(true);
        SetState(PlaybackState.Paused);
    });

    public void Resume() => Post(() =>
    {
        if (_state != PlaybackState.Paused) return;
        _out.SetPaused(false);
        SetState(PlaybackState.Playing);
    });

    public void Seek(TimeSpan position) => Post(() => DoSeek(position));

    public void Stop() => Post(DoStop);

    /// <summary>Offline mode only: pulls audio as a device would (tests, rendering).</summary>
    public void RenderOffline(Span<float> interleaved) => _out.Render(interleaved);

    private void Post(Action a)
    {
        _commands.Enqueue(a);
        _wake.Set();
    }

    // ------------------------------------------------------------------ engine thread

    private void Run()
    {
        while (_running)
        {
            try
            {
                while (_commands.TryDequeue(out var cmd)) cmd();
                Fill();
                Advance();
            }
            catch (Exception ex)
            {
                // The engine must survive anything a single track can throw at it.
                Log.Error(Area, "Engine loop error", ex);
            }
            _wake.WaitOne(5);
        }
    }

    private void Fill()
    {
        while (_current is { } cur && _out.Writable >= ChunkFrames)
        {
            int n = 0;
            if (!cur.AtEnd)
            {
                try { n = cur.Decoder.Read(_buf); }
                catch (Exception ex)
                {
                    Emit(new TrackFailed(cur.Track, ex.Message));
                    n = 0;
                }
                if (n > 0) Process(_buf.AsSpan(0, n * 2), cur.Track.Gain);
                if (n > 0) _out.Write(_buf.AsSpan(0, n * 2));
                if (n < ChunkFrames) cur.AtEnd = true;
            }
            if (!cur.AtEnd) continue;

            // End of the current file. Only continue if the caller's "next" decision is about this track.
            if (!ReferenceEquals(_nextFor, cur.Track)) return;
            if (_next is { } nx)
            {
                _next = null;
                _current = nx;
                _pending.Add(new Segment(_out.WriteIndex, nx, 0));
                continue;
            }
            if (_nextRequest is not null) return; // still opening; we wait (only audible if it takes > ring length)
            _pending.Add(new Segment(_out.WriteIndex, null, 0));
            _current = null;
            Collect();
            return;
        }
    }

    private void Process(Span<float> samples, float gain)
    {
        if (gain != 1f)
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        foreach (var p in _processors) p.Process(samples, SampleRate);
    }

    /// <summary>Moves the "audible" pointer as the device consumes the ring and raises events.</summary>
    private void Advance()
    {
        if (_pending.Count == 0) return;
        long read = _out.ReadIndex;
        while (_pending.Count > 0 && _pending[0].WriteIndex <= read)
        {
            var seg = _pending[0];
            _pending.RemoveAt(0);
            if (_active?.Source is { } prev) Emit(new TrackEnded(prev.Track, Completed: true));
            if (seg.Source is null)
            {
                _active = null;
                _anchor = null;
                SetState(PlaybackState.Stopped);
                Emit(new PlaybackFinished());
            }
            else
            {
                _active = seg;
                _anchor = seg.Source.Track;
                Emit(new TrackStarted(seg.Source.Track, seg.Source.Duration));
            }
        }
        Collect();
    }

    private void DoPlay(TrackHandle track, TimeSpan start)
    {
        int gen = ++_playGen;
        _anchor = track;
        _nextFor = null;
        CancelNext();
        int rate = SampleRate;
        Task.Run(() =>
        {
            Decoder? dec = null;
            try
            {
                dec = Decoder.Open(track.Path, rate);
                long frame = (long)(start.TotalSeconds * rate);
                if (frame > 0) dec.Seek(frame);
                var opened = dec;
                Post(() => OnPlayOpened(gen, track, opened, Math.Max(0, frame)));
            }
            catch (Exception ex)
            {
                dec?.Dispose();
                Log.Warn(Area, $"Cannot play '{track.Path}': {ex.Message}");
                Post(() => { if (gen == _playGen) Emit(new TrackFailed(track, ex.Message)); });
            }
        });
    }

    private void OnPlayOpened(int gen, TrackHandle track, Decoder dec, long startFrame)
    {
        if (gen != _playGen) { dec.Dispose(); return; }
        var src = new Source(track, dec);
        _live.Add(src);
        _out.Flush();
        if (_active?.Source is { } prev) Emit(new TrackEnded(prev.Track, Completed: false));
        _pending.Clear();
        _current = src;
        _active = new Segment(_out.WriteIndex, src, startFrame);
        _out.SetPaused(false);
        SetState(PlaybackState.Playing);
        Emit(new TrackStarted(track, src.Duration));
        Collect();
    }

    private void DoSetNext(TrackHandle? next)
    {
        var anchorSrc = _active?.Source;
        // A gapless splice already happened (the anchor is audible but we decode its successor).
        if (_current is { } cur && anchorSrc is not null && !ReferenceEquals(cur, anchorSrc)
            && ReferenceEquals(_anchor, anchorSrc.Track))
        {
            if (next is not null && cur.Track == next) return; // same decision, keep the splice
            int idx = _pending.FindIndex(s => ReferenceEquals(s.Source, cur));
            if (idx < 0 || !_out.Truncate(_pending[idx].WriteIndex))
            {
                Log.Debug(Area, "Next changed too late to undo the gapless splice");
                return;
            }
            _pending.RemoveRange(idx, _pending.Count - idx);
            _current = anchorSrc;
        }
        else if (_current is null && anchorSrc is not null && ReferenceEquals(_anchor, anchorSrc.Track))
        {
            // Playback was about to end after the anchor; undo the end marker if possible.
            int idx = _pending.FindIndex(s => s.Source is null);
            if (idx >= 0)
            {
                if (next is null) return;
                if (!_out.Truncate(_pending[idx].WriteIndex)) return;
                _pending.RemoveAt(idx);
                _current = anchorSrc;
            }
        }

        _nextFor = _anchor;
        if (next is not null && _next is { } ready && ready.Track == next) return;
        if (next is not null && _nextRequest == next) return;
        CancelNext();
        if (next is null) return;

        int gen = _nextGen;
        _nextRequest = next;
        int rate = SampleRate;
        Task.Run(() =>
        {
            try
            {
                var dec = Decoder.Open(next.Path, rate);
                Post(() =>
                {
                    if (gen != _nextGen) { dec.Dispose(); return; }
                    var s = new Source(next, dec);
                    _live.Add(s);
                    _next = s;
                    _nextRequest = null;
                });
            }
            catch (Exception ex)
            {
                Log.Warn(Area, $"Cannot open next '{next.Path}': {ex.Message}");
                Post(() =>
                {
                    if (gen != _nextGen) return;
                    _nextRequest = null;
                    Emit(new TrackFailed(next, ex.Message));
                });
            }
        });
    }

    private void CancelNext()
    {
        _nextGen++;
        _nextRequest = null;
        _next = null;
        Collect();
    }

    private void DoSeek(TimeSpan position)
    {
        var a = _active;
        if (a?.Source is not { } src) return;
        long frame = Math.Max(0, (long)(position.TotalSeconds * SampleRate));
        if (src.Duration is { } d) frame = Math.Min(frame, (long)(d.TotalSeconds * SampleRate));

        // If we already spliced the successor, rewind it and make it "next" again.
        if (_current is { } cur && !ReferenceEquals(cur, src) && cur.Decoder.Seek(0))
        {
            cur.AtEnd = false;
            CancelNext();
            _next = cur;
            _nextFor = src.Track;
            _live.Add(cur);
        }
        _out.Flush();
        _pending.Clear();
        src.AtEnd = !src.Decoder.Seek(frame);
        _current = src;
        _active = new Segment(_out.WriteIndex, src, frame);
        Collect();
    }

    private void DoStop()
    {
        _playGen++;
        _out.Flush();
        if (_active?.Source is { } prev) Emit(new TrackEnded(prev.Track, Completed: false));
        _active = null;
        _anchor = _nextFor = null;
        _pending.Clear();
        _current = null;
        CancelNext();
        SetState(PlaybackState.Stopped);
    }

    /// <summary>Disposes decoders nothing refers to any more.</summary>
    private void Collect()
    {
        if (_live.Count == 0) return;
        List<Source>? dead = null;
        foreach (var s in _live)
        {
            if (ReferenceEquals(s, _current) || ReferenceEquals(s, _next) || ReferenceEquals(s, _active?.Source)) continue;
            bool queued = false;
            foreach (var p in _pending) if (ReferenceEquals(p.Source, s)) { queued = true; break; }
            if (!queued) (dead ??= new()).Add(s);
        }
        if (dead is null) return;
        foreach (var s in dead)
        {
            _live.Remove(s);
            s.Decoder.Dispose();
        }
    }

    private void SetState(PlaybackState s)
    {
        if (_state == s) return;
        _state = s;
        Emit(new StateChanged(s));
    }

    private void Emit(EngineEvent e) => _events.Writer.TryWrite(e);

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        foreach (var s in _live) s.Decoder.Dispose();
        _live.Clear();
        _out.Dispose();
        _events.Writer.TryComplete();
        _wake.Dispose();
    }
}
