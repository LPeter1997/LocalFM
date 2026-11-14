using System.Runtime.InteropServices;

namespace LocalFM.Native;

/// <summary>Reads stream info, tags, embedded art and raw audio packets of a file without decoding it.</summary>
public sealed unsafe class MediaProbe : IDisposable
{
    private nint _h;
    private ProbeInfo _info;

    private MediaProbe(nint h)
    {
        _h = h;
        NativeMethods.ProbeGetInfo(h, out _info);
    }

    public static MediaProbe Open(string path)
    {
        int r = NativeMethods.ProbeOpen(path, out nint h);
        if (r != 0) throw new IOException($"Not a readable audio file: '{path}' (error {r}).");
        return new MediaProbe(h);
    }

    public static bool TryOpen(string path, out MediaProbe? probe)
    {
        probe = NativeMethods.ProbeOpen(path, out nint h) == 0 ? new MediaProbe(h) : null;
        return probe is not null;
    }

    public TimeSpan? Duration => _info.DurationMs >= 0 ? TimeSpan.FromMilliseconds(_info.DurationMs) : null;
    public long BitRate => _info.BitRate;
    public int SampleRate => _info.SampleRate;
    public int Channels => _info.Channels;
    public int BitsPerSample => _info.BitsPerSample;

    public string Codec { get { fixed (byte* p = _info.Codec) return Utf8(p) ?? ""; } }
    public string Container { get { fixed (byte* p = _info.Container) return Utf8(p) ?? ""; } }

    /// <summary>All tags (container + stream level) in file order; keys as FFmpeg reports them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Tags
    {
        get
        {
            var list = new List<KeyValuePair<string, string>>(_info.TagCount);
            for (int i = 0; i < _info.TagCount; i++)
            {
                if (NativeMethods.ProbeGetTag(Handle, i, out byte* k, out byte* v) == 0)
                    list.Add(new(Utf8(k) ?? "", Utf8(v) ?? ""));
            }
            return list;
        }
    }

    /// <summary>The embedded front cover (or first picture), if any.</summary>
    public (byte[] Data, string Mime)? Picture
    {
        get
        {
            if (_info.PictureSize <= 0) return null;
            byte* data = NativeMethods.ProbeGetPicture(Handle, out byte* mime);
            if (data == null) return null;
            return (new ReadOnlySpan<byte>(data, _info.PictureSize).ToArray(), Utf8(mime) ?? "image/jpeg");
        }
    }

    /// <summary>Feeds every audio packet payload to <paramref name="sink"/>. Tags are not part of the
    /// payload, so this is the basis for a tag-independent content identity.</summary>
    public void ReadPackets(Action<ReadOnlySpan<byte>> sink)
    {
        for (;;)
        {
            int n = NativeMethods.ProbeNextPacket(Handle, out byte* data);
            if (n == 0) return;
            if (n < 0) throw new IOException("Failed to read audio packets.");
            sink(new ReadOnlySpan<byte>(data, n));
        }
    }

    private static string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);

    private nint Handle => _h != 0 ? _h : throw new ObjectDisposedException(nameof(MediaProbe));

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _h, 0);
        if (h != 0) NativeMethods.ProbeClose(h);
    }
}
