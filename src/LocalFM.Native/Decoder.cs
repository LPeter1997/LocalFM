using System.Runtime.InteropServices;

namespace LocalFM.Native;

/// <summary>Decodes any FFmpeg-supported audio file to interleaved stereo float32 at a fixed rate.
/// Encoder delay/padding is trimmed by FFmpeg, so consecutive tracks join gaplessly. Not thread-safe.</summary>
public sealed unsafe class Decoder : IDisposable
{
    private nint _h;
    private GCHandle _stream; // custom IO: the Stream the native callbacks read from

    private Decoder(nint h, GCHandle stream = default)
    {
        _h = h;
        _stream = stream;
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

    /// <summary>Decodes from a stream (e.g. a file still arriving over the network). The stream must be seekable;
    /// its reads may block. It is called from whichever thread reads or seeks this decoder, and is disposed
    /// with it.</summary>
    public static Decoder Open(Stream stream, int outputRate)
    {
        var gc = GCHandle.Alloc(stream);
        int r = NativeMethods.DecoderOpenIo(&IoRead, &IoSeek, GCHandle.ToIntPtr(gc), outputRate, out nint h);
        if (r != 0)
        {
            gc.Free();
            stream.Dispose();
            throw new IOException($"Could not decode stream (error {r}).");
        }
        return new Decoder(h, gc);
    }

    [UnmanagedCallersOnly]
    private static int IoRead(nint user, byte* buf, int size)
    {
        try { return ((Stream)GCHandle.FromIntPtr(user).Target!).Read(new Span<byte>(buf, size)); }
        catch { return -1; }
    }

    [UnmanagedCallersOnly]
    private static long IoSeek(nint user, long offset, int whence)
    {
        try
        {
            var s = (Stream)GCHandle.FromIntPtr(user).Target!;
            if (whence == NativeMethods.SeekSize) return s.CanSeek ? s.Length : -1;
            return s.Seek(offset, whence switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin });
        }
        catch { return -1; }
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
        if (h != 0 && _stream.IsAllocated)
        {
            (_stream.Target as Stream)?.Dispose();
            _stream.Free();
        }
    }
}
