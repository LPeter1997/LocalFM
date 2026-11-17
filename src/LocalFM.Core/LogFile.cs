using System.Collections.Concurrent;
using System.Text;

namespace LocalFM.Core;

/// <summary>
/// The session log: one file per run (<c>localfm-yyyyMMdd-HHmmss-pid.log</c>) in a folder that keeps the newest
/// <see cref="KeepSessions"/>. Writing only queues the line; a background thread does the disk I/O, so the engine
/// and UI threads never wait on the disk. Per-run files (rather than rotating one) let two instances run side by
/// side and work on Windows, where an open file cannot be renamed.
/// </summary>
public sealed class LogFile : IDisposable
{
    public const int KeepSessions = 10;
    /// <summary>A session's log stops growing here (a runaway loop must not fill the disk).</summary>
    public const long MaxBytes = 8 * 1024 * 1024;
    private const int MaxQueued = 10_000;

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _drained = new(true);
    private readonly Thread _thread;
    private readonly StreamWriter _writer;
    private readonly bool _echo;
    private int _queued, _dropped;
    private long _bytes;
    private bool _full;
    private volatile bool _running = true;

    private LogFile(string path, StreamWriter writer, bool echo)
    {
        FilePath = path;
        _writer = writer;
        _echo = echo;
        _thread = new Thread(Run) { Name = "LocalFM log", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>This session's file.</summary>
    public string FilePath { get; }

    /// <summary>Starts a new session file in <paramref name="dir"/> and deletes the oldest beyond
    /// <see cref="KeepSessions"/>. <paramref name="echo"/> also copies every line to stderr (terminal runs).</summary>
    public static LogFile Open(string dir, bool echo = true)
    {
        Directory.CreateDirectory(dir);
        string name = $"localfm-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
        string path = Path.Combine(dir, name);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var log = new LogFile(path, new StreamWriter(stream, new UTF8Encoding(false)), echo);
        Prune(dir, path);
        return log;
    }

    private static void Prune(string dir, string keep)
    {
        try
        {
            // The names sort by start time.
            var old = Directory.GetFiles(dir, "localfm-*.log").Where(f => f != keep)
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(KeepSessions - 1);
            foreach (var f in old)
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } // still open elsewhere
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A line for <see cref="Log.Sink"/>. Never blocks; drops lines if the disk cannot keep up.</summary>
    public void Write(LogLevel level, string area, string message, Exception? ex = null)
    {
        if (!_running) return;
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(Format(DateTime.Now, level, area, message, ex));
        _drained.Reset();
        _wake.Set();
    }

    public static string Format(DateTime time, LogLevel level, string area, string message, Exception? ex)
    {
        string tag = level switch { LogLevel.Debug => "DEBUG", LogLevel.Info => "INFO ", LogLevel.Warning => "WARN ", _ => "ERROR" };
        var line = $"{time:yyyy-MM-dd HH:mm:ss.fff} {tag} {area}: {message}";
        return ex is null ? line : $"{line}\n    {ex.ToString().Replace("\n", "\n    ")}";
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) until everything queued so far is on disk.</summary>
    public void Flush(TimeSpan timeout)
    {
        _wake.Set();
        _drained.Wait(timeout);
    }

    private void Run()
    {
        while (true)
        {
            bool running = _running;
            _wake.WaitOne(running ? 1000 : 0);
            Drain();
            if (!running) return;
        }
    }

    private void Drain()
    {
        bool wrote = false;
        while (_queue.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            if (_echo) { try { Console.Error.WriteLine(line); } catch { } }
            wrote |= Append(line);
        }
        int dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) wrote |= Append(Format(DateTime.Now, LogLevel.Warning, "Log", $"{dropped} lines dropped (logging faster than the disk)", null));
        if (wrote) { try { _writer.Flush(); } catch (IOException) { } }
        if (_queue.IsEmpty) _drained.Set();
    }

    private bool Append(string line)
    {
        if (_full) return false;
        try
        {
            _bytes += line.Length + 1;
            if (_bytes > MaxBytes)
            {
                _full = true;
                _writer.WriteLine(Format(DateTime.Now, LogLevel.Warning, "Log", $"Log full ({MaxBytes / (1024 * 1024)} MB); nothing more is written this session", null));
                return true;
            }
            _writer.WriteLine(line);
            return true;
        }
        catch (IOException) { return false; } // disk full or gone: logging must never take the app down
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        try { _writer.Dispose(); } catch (IOException) { }
    }
}
