namespace LocalFM.Native;

/// <summary>Decodes any FFmpeg-supported audio file to interleaved stereo float32 at a fixed rate.
/// Encoder delay/padding is trimmed by FFmpeg, so consecutive tracks join gaplessly. Not thread-safe.</summary>
public sealed unsafe class Decoder : IDisposable
{
    private nint _h;

    private Decoder(nint h)
    {
        _h = h;
        NativeMethods.DecoderGetInfo(h, out var info);
        DurationFrames = info.DurationFrames;
        SourceRate = info.SourceRate;
        SourceChannels = info.SourceChannels;
        OutputRate = info.OutputRate;
    }

    public static Decoder Open(string path, int outputRate)
    {
        int r = NativeMethods.DecoderOpen(path, outputRate, out nint h);
        if (r != 0) throw new IOException($"Could not decode '{path}' (error {r}).");
        return new Decoder(h);
    }

    /// <summary>Length in output-rate frames, or -1 when the container does not say.</summary>
    public long DurationFrames { get; }
    public int SourceRate { get; }
    public int SourceChannels { get; }
    public int OutputRate { get; }

    /// <summary>Returns frames read; 0 at end of stream. Throws on unrecoverable errors.</summary>
    public int Read(Span<float> interleaved)
    {
        int n;
        fixed (float* p = interleaved) n = NativeMethods.DecoderRead(Handle, p, interleaved.Length / 2);
        if (n < 0) throw new IOException("Decoding failed.");
        return n;
    }

    public bool Seek(long frame) => NativeMethods.DecoderSeek(Handle, frame) == 0;

    private nint Handle => _h != 0 ? _h : throw new ObjectDisposedException(nameof(Decoder));

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _h, 0);
        if (h != 0) NativeMethods.DecoderClose(h);
    }
}
