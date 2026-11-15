using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace LocalFM.Vibe.Protocol;

/// <summary>
/// Framing: <c>[u8 type][u32 length, little endian][payload]</c>.
/// <list type="bullet">
/// <item>Json: a <see cref="Message"/>.</item>
/// <item>RangeRequest: <c>[u32 request][i64 offset][i32 length][utf-8 media id]</c>.</item>
/// <item>RangeReply: <c>[u32 request][i32 status: 0 ok, else error][bytes]</c>.</item>
/// </list>
/// Writes are serialized; one reader per connection.
/// </summary>
public sealed class Wire : IAsyncDisposable
{
    public const int Version = 1;
    public const int MaxPayload = 1 << 20;
    public const int MaxRange = 256 * 1024;

    public enum FrameType : byte { Json = 1, RangeRequest = 2, RangeReply = 3 }

    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly byte[] _header = new byte[5];

    public Wire(Stream stream) => _stream = stream;

    public async Task SendAsync(Message m, CancellationToken ct = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(m, WireJson.Default.Message);
        await WriteFrameAsync(FrameType.Json, json, default, ct).ConfigureAwait(false);
    }

    public Task SendRangeRequestAsync(uint request, string mediaId, long offset, int length, CancellationToken ct = default)
    {
        byte[] id = Encoding.UTF8.GetBytes(mediaId);
        var p = new byte[16 + id.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, request);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(12), length);
        id.CopyTo(p, 16);
        return WriteFrameAsync(FrameType.RangeRequest, p, default, ct);
    }

    public Task SendRangeReplyAsync(uint request, int status, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(p, request);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), status);
        return WriteFrameAsync(FrameType.RangeReply, p, data, ct);
    }

    private async Task WriteFrameAsync(FrameType type, ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b, CancellationToken ct)
    {
        int len = a.Length + b.Length;
        if (len > MaxPayload) throw new InvalidOperationException("Frame too large.");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _header[0] = (byte)type;
            BinaryPrimitives.WriteUInt32LittleEndian(_header.AsSpan(1), (uint)len);
            await _stream.WriteAsync(_header, ct).ConfigureAwait(false);
            if (a.Length > 0) await _stream.WriteAsync(a, ct).ConfigureAwait(false);
            if (b.Length > 0) await _stream.WriteAsync(b, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    /// <summary>Reads the next frame; null at a clean end of stream. The payload buffer is fresh per frame.</summary>
    public async Task<Frame?> ReadAsync(CancellationToken ct = default)
    {
        var head = new byte[5];
        if (!await FillAsync(head, ct).ConfigureAwait(false)) return null;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(1));
        if (len > MaxPayload) throw new InvalidDataException("Frame too large.");
        var payload = new byte[len];
        if (len > 0 && !await FillAsync(payload, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return new Frame((FrameType)head[0], payload);
    }

    private async Task<bool> FillAsync(Memory<byte> buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await _stream.ReadAsync(buf[got..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException();
            }
            got += n;
        }
        return true;
    }

    /// <summary>Reads a JSON message, skipping nothing: anything else is a protocol error.</summary>
    public async Task<Message?> ReadMessageAsync(CancellationToken ct = default)
    {
        var f = await ReadAsync(ct).ConfigureAwait(false);
        if (f is null) return null;
        return f.Value.AsMessage() ?? throw new InvalidDataException($"Expected a message, got {f.Value.Type}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _write.Dispose();
    }

    public readonly record struct Frame(FrameType Type, byte[] Payload)
    {
        public Message? AsMessage() => Type == FrameType.Json ? JsonSerializer.Deserialize(Payload, WireJson.Default.Message) : null;

        public (uint Request, string MediaId, long Offset, int Length) AsRangeRequest() => (
            BinaryPrimitives.ReadUInt32LittleEndian(Payload),
            Encoding.UTF8.GetString(Payload.AsSpan(16)),
            BinaryPrimitives.ReadInt64LittleEndian(Payload.AsSpan(4)),
            BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(12)));

        public (uint Request, int Status, ReadOnlyMemory<byte> Data) AsRangeReply() => (
            BinaryPrimitives.ReadUInt32LittleEndian(Payload),
            BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(4)),
            Payload.AsMemory(8));
    }
}
