namespace LocalFM.Native;

/// <summary>
/// Stereo float32 output backed by a native ring buffer. One producer thread writes; the device
/// (or an offline caller) consumes. Volume/pause/flush are lock-free and take effect on the audio thread.
/// </summary>
public sealed unsafe class AudioOutput : IDisposable
{
    private nint _h;

    private AudioOutput(nint h)
    {
        _h = h;
        NativeMethods.OutputGetInfo(h, out var info);
        SampleRate = info.SampleRate;
        Channels = info.Channels;
        RingFrames = info.RingFrames;
    }

    public static AudioOutput Open(OutputMode mode, int sampleRate = 0, int ringMs = 1000)
    {
        int r = NativeMethods.OutputOpen((int)mode, sampleRate, ringMs, out nint h);
        if (r != 0) throw new InvalidOperationException($"Could not open audio output ({mode}), error {r}.");
        return new AudioOutput(h);
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long RingFrames { get; }

    public long Writable => NativeMethods.OutputWritable(Handle);
    public long WriteIndex => NativeMethods.OutputWriteIndex(Handle);
    public long ReadIndex => NativeMethods.OutputReadIndex(Handle);

    /// <summary>How long until the device takes audio again (it takes a period at a time): new audio written
    /// after a flush is heard from then on. Zero when unknown or offline.</summary>
    public TimeSpan UntilNextTake
    {
        get
        {
            NativeMethods.OutputReadClock(Handle, out _, out long period, out long ageNs);
            if (period <= 0) return TimeSpan.Zero;
            double periodS = period / (double)SampleRate;
            double wait = periodS - ageNs / 1e9;
            // A take due within a few ms (or overdue) will most likely happen before the new audio is written:
            // then the one after it is the one that counts.
            while (wait < 0.015) wait += periodS;
            return TimeSpan.FromSeconds(wait);
        }
    }

    /// <summary>The read index between device callbacks: the frames the last callback took are counted as they
    /// play out (devices take whole periods of up to 100 ms, which would make positions jump in steps).
    /// Never ahead of what the device has actually taken.</summary>
    public long SmoothReadIndex
    {
        get
        {
            long idx = NativeMethods.OutputReadClock(Handle, out long adv, out _, out long ageNs);
            if (adv <= 0 || ageNs <= 0) return idx;
            long played = (long)(ageNs * (SampleRate / 1e9));
            return idx - adv + Math.Min(adv, played);
        }
    }
    public long Underruns => NativeMethods.OutputUnderruns(Handle);

    /// <summary>Totals of what the device went through (see lfm_output_stats); reading resets the worst gap.</summary>
    public OutputStats ReadStats()
    {
        NativeMethods.OutputGetStats(Handle, out var s);
        return s;
    }

    /// <summary>Backend, device name, rates and buffering, in one line.</summary>
    public string Describe()
    {
        byte* buf = stackalloc byte[512];
        buf[0] = 0;
        NativeMethods.OutputDescribe(Handle, buf, 512);
        return System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buf));
    }

    public int Write(ReadOnlySpan<float> interleaved)
    {
        fixed (float* p = interleaved) return NativeMethods.OutputWrite(Handle, p, interleaved.Length / 2);
    }

    /// <summary>Drops queued audio from <paramref name="writeIndex"/> on, if it is far enough ahead of the playhead.</summary>
    public bool Truncate(long writeIndex) => NativeMethods.OutputTruncate(Handle, writeIndex) != 0;

    /// <summary>Frames between the ring's read position and the speaker (0 when unknown or offline).</summary>
    public long LatencyFrames => NativeMethods.OutputLatencyFrames(Handle);

    public void Flush() => NativeMethods.OutputFlush(Handle);
    public void SetVolume(float linear) => NativeMethods.OutputSetVolume(Handle, linear);
    public void SetPaused(bool paused) => NativeMethods.OutputSetPaused(Handle, paused ? 1 : 0);

    /// <summary>Applies an equalizer on the audio thread (heard within one device period). Empty = flat.</summary>
    /// <summary>
    /// Copies the most recently heard frames (interleaved stereo, before volume) into <paramref name="interleaved"/>.
    /// Lock-free; returns the total number of frames rendered so far.
    /// </summary>
    public long ReadTap(Span<float> interleaved)
    {
        int frames = Math.Min(interleaved.Length / 2, NativeMethods.TapFrames);
        fixed (float* p = interleaved) return NativeMethods.OutputTap(Handle, p, frames);
    }

    public void SetEq(ReadOnlySpan<EqBand> bands, float preampDb)
    {
        fixed (EqBand* p = bands) NativeMethods.OutputSetEq(Handle, p, bands.Length, preampDb);
    }

    /// <summary>Pulls frames as the device would. Only valid for <see cref="OutputMode.Offline"/>.</summary>
    public void Render(Span<float> interleaved)
    {
        fixed (float* p = interleaved) NativeMethods.OutputRender(Handle, p, interleaved.Length / 2);
    }

    private nint Handle => _h != 0 ? _h : throw new ObjectDisposedException(nameof(AudioOutput));

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _h, 0);
        if (h != 0) NativeMethods.OutputClose(h);
    }
}
