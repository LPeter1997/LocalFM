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
///
/// Crossfade: with <see cref="Crossfade"/> set, the last seconds of a track are mixed with the start of the
/// next (equal-power curves) while decoding, unless the next track asks to be joined gaplessly (consecutive
/// album tracks). The next track's segment starts where the mix starts, so it is "playing" from then on.
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
    private readonly float[] _buf2 = new float[ChunkFrames * 2];
    private long _crossfadeTicks;
    private volatile bool _running = true;

    // published for readers on other threads
    private volatile Segment? _active;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private float _volume = 1f;
    private volatile IAudioProcessor[] _processors = [];

    // engine-thread state
    private readonly List<Segment> _pending = new(); // segments queued in the ring, not yet audible
    private volatile bool _endWritten;  // the ring holds the end of the queue: nothing more is written until a new play
    private readonly HashSet<Source> _live = new();
    private Source? _current;           // being decoded into the ring
    private Source? _next;              // opened, waiting to be spliced after _current
    private TrackHandle? _nextRequest;  // being opened
    private TrackHandle? _anchor;       // the track the caller's latest SetNext refers to
    private TrackHandle? _nextFor;      // the anchor at the time of the latest SetNext
    private Source? _fadeIn;            // being mixed in over the end of _current (crossfade in progress)
    private long _fadeLen, _fadePos;
    private int _playGen, _nextGen;

    private sealed class Source(TrackHandle track, Decoder decoder)
    {
        public TrackHandle Track { get; } = track;
        public Decoder Decoder { get; } = decoder;
        public TimeSpan? Duration { get; } = decoder.DurationFrames >= 0
            ? TimeSpan.FromSeconds((double)decoder.DurationFrames / decoder.OutputRate) : null;
        public bool AtEnd;
        public long Pos;                // frames decoded so far (track time of the next read)
        public long? SpliceOutFrame;    // crossfaded in: where the previous track was when the mix began

        public long DurationFrames => Decoder.DurationFrames;
    }

    /// <summary>The source whose continuation matters for "next" decisions: the one fading in, else the current.</summary>
    private Source? Lead => _fadeIn ?? _current;

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

    /// <summary>The ring holds the end of the queue: what is buffered is all there is until something new plays.
    /// Offline rendering drains a partial last block only then (a busy engine thread is not the end).</summary>
    public bool EndOfQueueBuffered => _endWritten;

    public TimeSpan Position
    {
        get
        {
            var a = _active;
            if (a?.Source is null) return TimeSpan.Zero;
            long frames = a.StartFrame + Math.Max(0, _out.SmoothReadIndex - a.WriteIndex);
            var pos = TimeSpan.FromSeconds((double)frames / SampleRate);
            return a.Source.Duration is { } d && pos > d ? d : pos;
        }
    }

    /// <summary>False for a moment after a start or seek, until the device takes the new audio (up to one device
    /// period): until then <see cref="Position"/> holds still at the new spot instead of moving in real time.</summary>
    public bool PositionIsLive => _active is not { Source: not null } a || _out.SmoothReadIndex >= a.WriteIndex;

    /// <summary>Linear output gain, 0..1 (callers map a UI slider to a perceptual curve).</summary>
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); _out.SetVolume(_volume); }
    }

    /// <summary>Crossfade between tracks; zero (the default) means gapless.</summary>
    public TimeSpan Crossfade
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _crossfadeTicks));
        set => Interlocked.Exchange(ref _crossfadeTicks, Math.Clamp(value.Ticks, 0, TimeSpan.FromSeconds(12).Ticks));
    }

    /// <summary>Sets the equalizer. It runs on the audio thread, so a change is heard right away. Empty = flat.</summary>
    public void SetEqualizer(ReadOnlySpan<EqBand> bands, float preampDb) => _out.SetEq(bands, preampDb);

    /// <summary>The audio just heard, for visualizations (see <see cref="AudioOutput.ReadTap"/>). Any thread.</summary>
    public long ReadVisualization(Span<float> interleaved) => _out.ReadTap(interleaved);

    public int OutputSampleRate => SampleRate;

    public IReadOnlyList<IAudioProcessor> Processors
    {
        get => _processors;
        set => _processors = [.. value];
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Starts <paramref name="track"/> now (the current track keeps playing until it is ready).</summary>
    public void Play(TrackHandle track, TimeSpan start = default) => Post(() => DoPlay(track, () => start, moving: false));

    /// <summary>Like <see cref="Play(TrackHandle, TimeSpan)"/>, but follows a moving target: the start position is
    /// asked for once the file is open, and taken as where the target will be when the device next takes audio
    /// (so neither opening time nor device periods add error). The target must move in real time.</summary>
    public void Play(TrackHandle track, Func<TimeSpan> startAt) => Post(() => DoPlay(track, () => startAt() + _out.UntilNextTake, moving: true));

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

    public void Seek(TimeSpan position) => Post(() => DoSeek(() => position));

    /// <summary>Seeks to a moving target (see <see cref="Play(TrackHandle, Func{TimeSpan})"/>), computed on the
    /// engine thread right before the jump.</summary>
    public void SeekTo(Func<TimeSpan> position) => Post(() => DoSeek(() => position() + _out.UntilNextTake));

    /// <summary>How long audio takes from the ring to the speaker (device buffering); zero when unknown.</summary>
    public TimeSpan OutputLatency => TimeSpan.FromSeconds((double)_out.LatencyFrames / SampleRate);

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
            if (_fadeIn is { } inc) { MixFade(cur, inc); continue; }
            if (!cur.AtEnd && TryStartFade(cur)) continue;
            if (!cur.AtEnd && AwaitingFadePartner(cur)) return;
            if (!cur.AtEnd)
            {
                int n = Read(cur, _buf, ChunkFrames);
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
                nx.SpliceOutFrame = null;
                _pending.Add(new Segment(_out.WriteIndex, nx, 0));
                continue;
            }
            if (_nextRequest is not null) return; // still opening; we wait (only audible if it takes > ring length)
            _pending.Add(new Segment(_out.WriteIndex, null, 0));
            _endWritten = true;
            _current = null;
            Collect();
            return;
        }
    }

    /// <summary>Decodes up to <paramref name="frames"/> frames with the track's gain applied. 0 = ended or failed.</summary>
    private int Read(Source s, float[] buf, int frames)
    {
        int n;
        try { n = s.Decoder.Read(buf.AsSpan(0, frames * 2)); }
        catch (Exception ex)
        {
            Emit(new TrackFailed(s.Track, ex.Message));
            return 0;
        }
        if (n <= 0) return 0;
        Process(buf.AsSpan(0, n * 2), s.Track.Gain);
        s.Pos += n;
        return n;
    }

    /// <summary>Crossfade length for this pair, 0 when there should be none.
    /// Never more than a third of either track (short intros, interludes).</summary>
    private long FadeFrames(Source cur, TrackHandle next, Source? opened)
    {
        long fade = (long)(Crossfade.TotalSeconds * SampleRate);
        if (fade < SampleRate / 10 || next.JoinGapless || cur.DurationFrames <= 0) return 0;
        fade = Math.Min(fade, cur.DurationFrames / 3);
        if (opened?.DurationFrames > 0) fade = Math.Min(fade, opened.DurationFrames / 3);
        return fade;
    }

    /// <summary>Inside the crossfade window while the next track is still opening: hold off decoding further
    /// (or the mix would start late and short), as long as enough is buffered to stay safe.</summary>
    private bool AwaitingFadePartner(Source cur)
    {
        if (_nextRequest is not { } req || !ReferenceEquals(_nextFor, cur.Track)) return false;
        long fade = FadeFrames(cur, req, null);
        if (fade == 0 || cur.DurationFrames - cur.Pos > fade) return false;
        return BufferedFrames > SampleRate / 4;
    }

    /// <summary>Starts mixing the next track in when the current one is within the crossfade of its end.</summary>
    private bool TryStartFade(Source cur)
    {
        if (_next is not { } nx || !ReferenceEquals(_nextFor, cur.Track)) return false;
        long fade = FadeFrames(cur, nx.Track, nx);
        if (fade == 0) return false;
        long remaining = cur.DurationFrames - cur.Pos;
        if (remaining > fade || remaining < ChunkFrames) return false;

        _next = null;
        _fadeIn = nx;
        _fadeLen = remaining;
        _fadePos = 0;
        nx.SpliceOutFrame = cur.Pos;
        _pending.Add(new Segment(_out.WriteIndex, nx, 0));
        return true;
    }

    private void MixFade(Source cur, Source inc)
    {
        int want = (int)Math.Clamp(_fadeLen - _fadePos, 1, ChunkFrames);
        int n1 = cur.AtEnd ? 0 : Read(cur, _buf, want);
        if (n1 < want) cur.AtEnd = true;
        int n2 = inc.AtEnd ? 0 : Read(inc, _buf2, want);
        if (n2 < want) inc.AtEnd = true;
        int n = Math.Max(n1, n2);
        if (n1 < n) _buf.AsSpan(n1 * 2, (n - n1) * 2).Clear();
        if (n2 < n) _buf2.AsSpan(n2 * 2, (n - n2) * 2).Clear();
        for (int i = 0; i < n; i++)
        {
            double t = Math.Clamp((_fadePos + i + 0.5) / _fadeLen, 0, 1);
            float gOut = (float)Math.Cos(t * Math.PI / 2), gIn = (float)Math.Sin(t * Math.PI / 2);
            _buf[2 * i] = _buf[2 * i] * gOut + _buf2[2 * i] * gIn;
            _buf[2 * i + 1] = _buf[2 * i + 1] * gOut + _buf2[2 * i + 1] * gIn;
        }
        if (n > 0) _out.Write(_buf.AsSpan(0, n * 2));
        _fadePos += n;
        // Done when the ramp is complete (any rest of the old track is dropped: it is faded to silence)
        // or the new track already ended (a very short one).
        if (_fadePos >= _fadeLen || inc.AtEnd || n == 0)
        {
            _current = inc;
            _fadeIn = null;
            Collect();
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

    private void DoPlay(TrackHandle track, Func<TimeSpan> startAt, bool moving)
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
                dec = track.OpenDecoder(rate);
                long frame = (long)(startAt().TotalSeconds * rate);
                if (frame > 0) dec.Seek(frame); // for a moving target this fetches the bytes; it is settled later
                var opened = dec;
                Post(() => OnPlayOpened(gen, track, opened, Math.Max(0, frame), moving ? startAt : null));
            }
            catch (Exception ex)
            {
                dec?.Dispose();
                Log.Warn(Area, $"Cannot play '{track.Path}': {ex.Message}");
                Post(() => { if (gen == _playGen) Emit(new TrackFailed(track, ex.Message)); });
            }
        });
    }

    private void OnPlayOpened(int gen, TrackHandle track, Decoder dec, long startFrame, Func<TimeSpan>? retarget)
    {
        if (gen != _playGen) { dec.Dispose(); return; }
        var src = new Source(track, dec) { Pos = startFrame };
        _live.Add(src);
        _fadeIn = null;
        if (_active?.Source is { } prev) Emit(new TrackEnded(prev.Track, Completed: false));
        // A moving target moved while the file opened (and this was posted): settle it here, last thing.
        if (retarget is not null) startFrame = Settle(src, startFrame, retarget);
        _current = src;
        Jump(src, startFrame);
        _out.SetPaused(false);
        SetState(PlaybackState.Playing);
        Emit(new TrackStarted(track, src.Duration));
        Collect();
    }

    /// <summary>Seeks <paramref name="src"/> to wherever <paramref name="target"/> says, again if it moved on while
    /// seeking (a remote file can take a while; the bytes are there the second time). Returns the frame.</summary>
    private long Settle(Source src, long frame, Func<TimeSpan> target)
    {
        for (int i = 0; i < 3; i++)
        {
            long want = ClampFrame(src, target());
            if (Math.Abs(want - frame) < SampleRate / 200) break; // within 5 ms
            frame = want;
            src.AtEnd = !src.Decoder.Seek(frame);
            src.Pos = frame;
        }
        return frame;
    }

    private long ClampFrame(Source src, TimeSpan t)
    {
        long frame = Math.Max(0, (long)(t.TotalSeconds * SampleRate));
        return src.Duration is { } d ? Math.Min(frame, (long)(d.TotalSeconds * SampleRate)) : frame;
    }

    /// <summary>Makes <paramref name="src"/> (already at <paramref name="frame"/>) audible from the device's next take.
    /// The first chunk is decoded before the flush and written right after it, so the device never finds the ring
    /// empty in between (that would delay the audio by a whole device period and put the position off by as much).</summary>
    private void Jump(Source src, long frame)
    {
        int n = src.AtEnd ? 0 : Read(src, _buf, ChunkFrames);
        if (n < ChunkFrames) src.AtEnd = true;
        _out.Flush();
        _pending.Clear();
        _endWritten = false;
        _active = new Segment(_out.WriteIndex, src, frame);
        if (n > 0) _out.Write(_buf.AsSpan(0, n * 2));
    }

    private void DoSetNext(TrackHandle? next)
    {
        var anchorSrc = _active?.Source;
        // A splice already happened (the anchor is audible but we decode its successor, gapless or crossfading).
        if (Lead is { } lead && anchorSrc is not null && !ReferenceEquals(lead, anchorSrc)
            && ReferenceEquals(_anchor, anchorSrc.Track))
        {
            if (next is not null && lead.Track == next) return; // same decision, keep the splice
            int idx = _pending.FindIndex(s => ReferenceEquals(s.Source, lead));
            if (idx < 0 || !_out.Truncate(_pending[idx].WriteIndex))
            {
                Log.Debug(Area, "Next changed too late to undo the splice");
                return;
            }
            _pending.RemoveRange(idx, _pending.Count - idx);
            // A crossfade consumed the anchor's tail: rewind it to where the mix began.
            if (lead.SpliceOutFrame is { } outFrame && anchorSrc.Decoder.Seek(outFrame))
            {
                anchorSrc.Pos = outFrame;
                anchorSrc.AtEnd = false;
            }
            _fadeIn = null;
            _current = anchorSrc;
            _endWritten = false;
            _live.Add(anchorSrc);
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
                _endWritten = false;
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
                var dec = next.OpenDecoder(rate);
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

    private void DoSeek(Func<TimeSpan> target)
    {
        var a = _active;
        if (a?.Source is not { } src) return;

        if (_fadeIn is { } fi)
        {
            _fadeIn = null;
            if (ReferenceEquals(fi, src)) _current = src; // seeking in the track fading in: the old one is done
            else if (fi.Decoder.Seek(0))
            {
                // Seeking in the outgoing track: the incoming one goes back to being "next".
                fi.AtEnd = false;
                fi.Pos = 0;
                CancelNext();
                _next = fi;
                _nextFor = src.Track;
                _live.Add(fi);
            }
        }

        // If we already spliced the successor, rewind it and make it "next" again.
        if (_current is { } cur && !ReferenceEquals(cur, src) && cur.Decoder.Seek(0))
        {
            cur.AtEnd = false;
            cur.Pos = 0;
            CancelNext();
            _next = cur;
            _nextFor = src.Track;
            _live.Add(cur);
        }
        long frame = ClampFrame(src, target());
        src.AtEnd = !src.Decoder.Seek(frame);
        src.Pos = frame;
        frame = Settle(src, frame, target);
        _current = src;
        Jump(src, frame);
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
        _endWritten = false;
        _current = null;
        _fadeIn = null;
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
            if (ReferenceEquals(s, _current) || ReferenceEquals(s, _next) || ReferenceEquals(s, _fadeIn)
                || ReferenceEquals(s, _active?.Source)) continue;
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
