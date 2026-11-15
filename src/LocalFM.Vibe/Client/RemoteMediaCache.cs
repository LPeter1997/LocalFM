using LocalFM.Core;
using Microsoft.Win32.SafeHandles;

namespace LocalFM.Vibe.Client;

/// <summary>Where remote media bytes come from: the host's data connection (a fake in tests).</summary>
public interface IRangeSource
{
    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes of <paramref name="mediaId"/> at
    /// <paramref name="offset"/>; fewer only at the end of the file. Throws when the connection is gone.</summary>
    ValueTask<int> ReadAsync(string mediaId, long offset, Memory<byte> buffer, CancellationToken ct);
}

/// <summary>
/// The listener's copy of the host's files, filled on demand. Each file is a sparse temp file plus a map of
/// the 64 KB chunks that have arrived. One scheduler pulls chunks from <see cref="IRangeSource"/>, in order:
/// chunks a decoder is blocked on, then the files in <see cref="SetPriority"/> order (the playing track
/// first), each from where it is being read onwards, then from its start. Everything is deleted on dispose.
/// </summary>
public sealed class RemoteMediaCache : IDisposable
{
    public const int ChunkSize = 64 * 1024;
    private const string Area = "Vibe";
    private const int MaxInFlight = 4;
    /// <summary>Files kept after they leave the priority list (the engine may still be finishing one).</summary>
    private const int KeepUnlisted = 3;

    private readonly string _dir;
    private readonly IRangeSource _source;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteFile> _files = new();
    private readonly List<RemoteFile> _unlisted = new(); // oldest first
    private List<RemoteFile> _priority = new();
    private readonly SemaphoreSlim _work = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _inFlight;
    private bool _disposed;

    public RemoteMediaCache(string dir, IRangeSource source)
    {
        _dir = dir;
        _source = source;
        Directory.CreateDirectory(dir);
        _loop = Task.Run(Loop);
    }

    /// <summary>Deletes everything a previous session left behind (call at startup).</summary>
    public static void Sweep(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception ex) { Log.Warn(Area, $"Could not clear the vibe cache: {ex.Message}"); }
    }

    /// <summary>Starts tracking a file (no-op when it is already known).</summary>
    public RemoteFile Add(string mediaId, long length)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_files.TryGetValue(mediaId, out var f)) return f;
            f = new RemoteFile(this, mediaId, length, Path.Combine(_dir, $"{_files.Count:D4}-{Sanitize(mediaId)}.bin"));
            _files[mediaId] = f;
            _unlisted.Add(f);
            Trim();
            return f;
        }
    }

    public RemoteFile? Get(string mediaId)
    {
        lock (_gate) return _files.GetValueOrDefault(mediaId);
    }

    /// <summary>Which files to fetch, most important first. Files left out are only fetched when a reader
    /// waits on them, and are deleted once more than a few pile up.</summary>
    public void SetPriority(IReadOnlyList<string> mediaIds)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var list = mediaIds.Select(id => _files.GetValueOrDefault(id)).OfType<RemoteFile>().Distinct().ToList();
            foreach (var f in _priority) if (!list.Contains(f) && !_unlisted.Contains(f)) _unlisted.Add(f);
            _unlisted.RemoveAll(list.Contains);
            _priority = list;
            Trim();
        }
        Wake();
    }

    private void Trim()
    {
        while (_unlisted.Count > KeepUnlisted)
        {
            var old = _unlisted[0];
            _unlisted.RemoveAt(0);
            _files.Remove(old.MediaId);
            old.Close();
        }
    }

    internal void Wake() => _work.Release();

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(40).ToArray());

    // ------------------------------------------------------------------ scheduler

    private async Task Loop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (Volatile.Read(ref _inFlight) < MaxInFlight && Pick() is { } job)
                {
                    Interlocked.Increment(ref _inFlight);
                    _ = Fetch(job.File, job.Chunk, ct);
                }
                await _work.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private (RemoteFile File, int Chunk)? Pick()
    {
        lock (_gate)
        {
            foreach (var f in _files.Values)
                if (f.TakeUrgent() is int u) return (f, u);
            foreach (var f in _priority)
                if (f.TakeNext() is int c) return (f, c);
            return null;
        }
    }

    private async Task Fetch(RemoteFile file, int chunk, CancellationToken ct)
    {
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
        bool ok = false;
        try
        {
            long offset = (long)chunk * ChunkSize;
            int want = (int)Math.Min(ChunkSize, file.Length - offset);
            int got = 0;
            while (got < want)
            {
                int n = await _source.ReadAsync(file.MediaId, offset + got, buf.AsMemory(got, want - got), ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException($"Host sent {got} of {want} bytes at {offset}.");
                got += n;
            }
            ok = file.Complete(chunk, buf.AsSpan(0, want));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn(Area, $"Fetching {file.MediaId} chunk {chunk} failed: {ex.Message}");
            try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            if (!ok) file.Abandon(chunk);
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            Interlocked.Decrement(ref _inFlight);
            if (!ct.IsCancellationRequested) Wake();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var f in _files.Values) f.Close();
            _files.Clear();
            _priority.Clear();
            _unlisted.Clear();
        }
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) { Log.Debug(Area, $"Could not delete {_dir}: {ex.Message}"); }
    }
}

/// <summary>One remote file being filled in. Thread-safe.</summary>
public sealed class RemoteFile
{
    private enum ChunkState : byte { Missing, Fetching, Done }

    private readonly RemoteMediaCache _owner;
    private readonly SafeFileHandle _handle;
    private readonly ChunkState[] _chunks;
    private readonly object _gate = new(); // not Lock: readers block with Monitor.Wait
    private readonly Queue<int> _urgent = new();
    private int _done;
    private long _readHint;
    private bool _closed;
    private event Action? Arrived;

    internal RemoteFile(RemoteMediaCache owner, string mediaId, long length, string path)
    {
        _owner = owner;
        MediaId = mediaId;
        Length = length;
        Path = path;
        _chunks = new ChunkState[(int)((length + RemoteMediaCache.ChunkSize - 1) / RemoteMediaCache.ChunkSize)];
        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.DeleteOnClose, preallocationSize: 0);
        RandomAccess.SetLength(_handle, length);
    }

    public string MediaId { get; }
    public long Length { get; }
    public string Path { get; }

    public bool IsComplete { get { lock (_gate) return _done == _chunks.Length; } }

    /// <summary>Bytes that have arrived (anywhere in the file).</summary>
    public long BytesAvailable
    {
        get
        {
            lock (_gate)
            {
                long n = (long)_done * RemoteMediaCache.ChunkSize;
                if (_chunks.Length > 0 && _chunks[^1] == ChunkState.Done) n -= (long)_chunks.Length * RemoteMediaCache.ChunkSize - Length;
                return n;
            }
        }
    }

    /// <summary>Whether all of [offset, offset+count) has arrived.</summary>
    public bool Has(long offset, long count)
    {
        if (count <= 0) return true;
        int first = (int)(offset / RemoteMediaCache.ChunkSize);
        int last = (int)(Math.Min(Length, offset + count) - 1) / RemoteMediaCache.ChunkSize;
        lock (_gate)
        {
            for (int i = first; i <= last && i < _chunks.Length; i++)
                if (_chunks[i] != ChunkState.Done) return false;
            return true;
        }
    }

    /// <summary>Completes when [offset, offset+count) has arrived, fetching it first.</summary>
    public async Task WaitAsync(long offset, long count, CancellationToken ct)
    {
        count = Math.Min(count, Length - offset);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (Has(offset, count) || _closed) tcs.TrySetResult(); }
        Arrived += Check;
        try
        {
            lock (_gate)
            {
                _readHint = offset;
                int first = (int)(offset / RemoteMediaCache.ChunkSize);
                if (first < _chunks.Length && _chunks[first] == ChunkState.Missing) _urgent.Enqueue(first);
            }
            _owner.Wake();
            Check();
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            if (_closed) throw new ObjectDisposedException(nameof(RemoteFile));
        }
        finally { Arrived -= Check; }
    }

    /// <summary>A seekable stream over the file; reads block until the bytes arrive (or the file is dropped).</summary>
    public Stream OpenRead() => new Reader(this);

    // ------------------------------------------------------------------ scheduler side

    internal int? TakeUrgent()
    {
        lock (_gate)
        {
            while (_urgent.TryDequeue(out int c))
                if (_chunks[c] == ChunkState.Missing) { _chunks[c] = ChunkState.Fetching; return c; }
            return null;
        }
    }

    /// <summary>The next missing chunk from where the file is read, wrapping to the start.</summary>
    internal int? TakeNext()
    {
        lock (_gate)
        {
            if (_closed || _done == _chunks.Length) return null;
            int start = (int)Math.Min(_readHint / RemoteMediaCache.ChunkSize, Math.Max(0, _chunks.Length - 1));
            for (int k = 0; k < _chunks.Length; k++)
            {
                int i = (start + k) % _chunks.Length;
                if (_chunks[i] == ChunkState.Missing) { _chunks[i] = ChunkState.Fetching; return i; }
            }
            return null;
        }
    }

    internal bool Complete(int chunk, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_closed) return true;
            RandomAccess.Write(_handle, data, (long)chunk * RemoteMediaCache.ChunkSize);
            if (_chunks[chunk] != ChunkState.Done) _done++;
            _chunks[chunk] = ChunkState.Done;
            Monitor.PulseAll(_gate);
        }
        Arrived?.Invoke();
        return true;
    }

    internal void Abandon(int chunk)
    {
        lock (_gate)
        {
            if (_chunks[chunk] == ChunkState.Fetching) _chunks[chunk] = ChunkState.Missing;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Monitor.PulseAll(_gate);
        }
        Arrived?.Invoke();
        // Readers might be mid-read; the handle closes once they notice (see Read).
        lock (_gate) _handle.Dispose();
    }

    /// <summary>Blocking read for decoders: waits for the chunk at <paramref name="position"/>.</summary>
    private int Read(long position, Span<byte> buffer)
    {
        if (position >= Length || buffer.Length == 0) return 0;
        int chunk = (int)(position / RemoteMediaCache.ChunkSize);
        bool asked = false;
        lock (_gate)
        {
            _readHint = position;
            while (!_closed && _chunks[chunk] != ChunkState.Done)
            {
                if (!asked)
                {
                    if (_chunks[chunk] == ChunkState.Missing) _urgent.Enqueue(chunk);
                    asked = true;
                    _owner.Wake();
                }
                Monitor.Wait(_gate, 250);
            }
            if (_closed) throw new IOException("The vibe's file is gone.");
            // Read as far as the run of arrived chunks goes.
            long end = position;
            int c = chunk;
            while (c < _chunks.Length && _chunks[c] == ChunkState.Done && end < position + buffer.Length)
                end = Math.Min(Length, (long)++c * RemoteMediaCache.ChunkSize);
            int n = (int)Math.Min(buffer.Length, end - position);
            return RandomAccess.Read(_handle, buffer[..n], position);
        }
    }

    private sealed class Reader(RemoteFile file) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => file.Length;
        public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, file.Length); }

        public override int Read(Span<byte> buffer)
        {
            int n = file.Read(_pos, buffer);
            _pos += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Current => _pos + offset,
            SeekOrigin.End => file.Length + offset,
            _ => offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
