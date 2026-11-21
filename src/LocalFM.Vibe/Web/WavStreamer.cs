using System.Buffers;
using System.Buffers.Binary;
using LocalFM.Native;
using LocalFM.Vibe.Library;

namespace LocalFM.Vibe.Web;

/// <summary>
/// Serves a track a player can't decode itself (APE, WavPack, DSD, WMA, …) as 16-bit stereo PCM WAV, decoded
/// on the fly (our FFmpeg build has no encoders, and on a home network the bandwidth is fine). The size is known
/// up front from the duration, so players see an ordinary seekable file: a byte range maps to a frame, and the
/// decoder seeks there. Short or long decodes are padded or cut to the promised size.
/// </summary>
public sealed class WavStreamer
{
    public const int HeaderSize = 44;
    public const int Channels = 2;
    public const int BytesPerFrame = Channels * 2;

    public static int RateFor(int sourceRate) => sourceRate is >= 8000 and <= 96000 ? sourceRate : sourceRate > 96000 ? 88200 : 44100;

    /// <summary>A canonical PCM header for <paramref name="frames"/> frames.</summary>
    public static byte[] Header(int rate, long frames)
    {
        long data = frames * BytesPerFrame;
        uint dataSize = (uint)Math.Min(data, uint.MaxValue - HeaderSize);
        var h = new byte[HeaderSize];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), dataSize + HeaderSize - 8);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(rate * BytesPerFrame));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), dataSize);
        return h;
    }

    /// <summary>False when the connection should close.</summary>
    public async Task<bool> ServeAsync(Stream s, HttpRequest req, MediaFile file, CancellationToken ct)
    {
        Decoder dec;
        try
        {
            dec = await Task.Run(() => Open(file.Path), ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WebGateway.WriteHeadAsync(s, 404, 0, null, req.KeepAlive, ct).ConfigureAwait(false);
            return true;
        }
        using (dec)
        {
            int rate = dec.OutputRate;
            long frames = dec.DurationFrames >= 0 ? dec.DurationFrames
                : file.DurationMs is { } ms ? ms * rate / 1000 : -1;
            if (frames <= 0)
            {
                await WebGateway.WriteHeadAsync(s, 404, 0, null, req.KeepAlive, ct).ConfigureAwait(false);
                return true;
            }
            long length = HeaderSize + frames * BytesPerFrame;
            var range = Http.Range(req.Header("Range"), length);
            if (range is (-1, -1))
            {
                await WebGateway.WriteHeadAsync(s, 416, 0, null, req.KeepAlive, ct, ("Content-Range", $"bytes */{length}")).ConfigureAwait(false);
                return true;
            }
            long start = range?.Start ?? 0, end = range?.End ?? length - 1;
            var headers = new List<(string, string)> { ("Accept-Ranges", "bytes"), ("Cache-Control", "private, no-cache") };
            if (range is not null) headers.Add(("Content-Range", $"bytes {start}-{end}/{length}"));
            await WebGateway.WriteHeadAsync(s, range is null ? 200 : 206, end - start + 1, "audio/wav", req.KeepAlive, ct, [.. headers]).ConfigureAwait(false);
            if (req.Method == "HEAD") return true;
            await WriteBodyAsync(s, dec, rate, frames, start, end, ct).ConfigureAwait(false);
            return true;
        }
    }

    private static Decoder Open(string path)
    {
        var dec = Decoder.Open(path, 44100);
        int want = RateFor(dec.SourceRate);
        if (want == dec.OutputRate) return dec;
        dec.Dispose();
        return Decoder.Open(path, want);
    }

    /// <summary>Writes bytes [start, end] of the virtual WAV file.</summary>
    internal static async Task WriteBodyAsync(Stream s, Decoder dec, int rate, long frames, long start, long end, CancellationToken ct)
    {
        long at = start;
        if (at < HeaderSize)
        {
            var header = Header(rate, frames);
            int n = (int)(Math.Min(end + 1, HeaderSize) - at);
            await s.WriteAsync(header.AsMemory((int)at, n), ct).ConfigureAwait(false);
            at += n;
        }
        if (at > end) return;
        long frame = (at - HeaderSize) / BytesPerFrame;
        int skip = (int)((at - HeaderSize) % BytesPerFrame);
        if (frame > 0 && !dec.Seek(frame)) frame = 0; // can't seek: decode from the start (rare)
        if (frame == 0 && at - HeaderSize >= BytesPerFrame) await SkipAsync(dec, (at - HeaderSize) / BytesPerFrame, ct).ConfigureAwait(false);

        const int Chunk = 4096;
        var floats = ArrayPool<float>.Shared.Rent(Chunk * Channels);
        var bytes = ArrayPool<byte>.Shared.Rent(Chunk * BytesPerFrame);
        try
        {
            bool ended = false;
            while (at <= end)
            {
                int got = 0;
                if (!ended)
                {
                    got = dec.Read(floats.AsSpan(0, Chunk * Channels));
                    if (got == 0) ended = true;
                }
                if (ended)
                {
                    got = Chunk; // pad with silence up to the promised size
                    Array.Clear(bytes, 0, Chunk * BytesPerFrame);
                }
                else ToPcm16(floats.AsSpan(0, got * Channels), bytes);
                int from = skip;
                skip = 0;
                int count = (int)Math.Min(got * BytesPerFrame - from, end - at + 1);
                await s.WriteAsync(bytes.AsMemory(from, count), ct).ConfigureAwait(false);
                at += count;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floats);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static Task SkipAsync(Decoder dec, long frames, CancellationToken ct) => Task.Run(() =>
    {
        var buf = new float[4096 * Channels];
        while (frames > 0 && !ct.IsCancellationRequested)
        {
            int n = dec.Read(buf.AsSpan(0, (int)Math.Min(4096, frames) * Channels));
            if (n == 0) return;
            frames -= n;
        }
    }, ct);

    internal static void ToPcm16(ReadOnlySpan<float> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float v = Math.Clamp(src[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(dst[(i * 2)..], (short)MathF.Round(v * 32767f));
        }
    }
}
