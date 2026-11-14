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
    public long Underruns => NativeMethods.OutputUnderruns(Handle);

    public int Write(ReadOnlySpan<float> interleaved)
    {
        fixed (float* p = interleaved) return NativeMethods.OutputWrite(Handle, p, interleaved.Length / 2);
    }

    /// <summary>Drops queued audio from <paramref name="writeIndex"/> on, if it is far enough ahead of the playhead.</summary>
    public bool Truncate(long writeIndex) => NativeMethods.OutputTruncate(Handle, writeIndex) != 0;

    public void Flush() => NativeMethods.OutputFlush(Handle);
    public void SetVolume(float linear) => NativeMethods.OutputSetVolume(Handle, linear);
    public void SetPaused(bool paused) => NativeMethods.OutputSetPaused(Handle, paused ? 1 : 0);

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
