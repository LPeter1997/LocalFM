using LocalFM.Core;

namespace LocalFM.Library.Import;

/// <summary>Watches source folders and triggers a (cheap, incremental) rescan after changes settle.</summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly Scanner _scanner;
    private readonly Dictionary<Ulid, (FileSystemWatcher Watcher, Timer Debounce)> _watchers = new();
    private readonly TimeSpan _delay;
    private readonly Lock _lock = new();

    public FolderWatcher(Scanner scanner, TimeSpan? debounce = null)
    {
        _scanner = scanner;
        _delay = debounce ?? TimeSpan.FromSeconds(3);
    }

    public void Watch(Source source)
    {
        lock (_lock)
        {
            if (_watchers.ContainsKey(source.Id) || !Directory.Exists(source.Path)) return;
            Timer debounce = new(_ => Rescan(source), null, Timeout.Infinite, Timeout.Infinite);
            FileSystemWatcher w;
            try
            {
                w = new FileSystemWatcher(source.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
            }
            catch (Exception ex)
            {
                debounce.Dispose();
                Log.Warn("Watch", $"Cannot watch '{source.Path}': {ex.Message}");
                return;
            }
            void Poke() => debounce.Change(_delay, Timeout.InfiniteTimeSpan);
            // Activity in excluded folders (another app's recordings, say) is no reason to rescan.
            void Changed(object? s, FileSystemEventArgs e) { if (!_scanner.IsExcluded(e.FullPath)) Poke(); }
            w.Created += Changed;
            w.Changed += Changed;
            w.Deleted += Changed;
            w.Renamed += (s, e) => { if (!_scanner.IsExcluded(e.FullPath) || !_scanner.IsExcluded(e.OldFullPath)) Poke(); };
            w.Error += (_, e) => { Log.Warn("Watch", $"Watcher overflow on '{source.Path}', rescanning"); Poke(); };
            w.EnableRaisingEvents = true;
            _watchers[source.Id] = (w, debounce);
        }
    }

    public void Unwatch(Ulid sourceId)
    {
        lock (_lock)
        {
            if (!_watchers.Remove(sourceId, out var entry)) return;
            entry.Watcher.Dispose();
            entry.Debounce.Dispose();
        }
    }

    private void Rescan(Source source) =>
        _ = Task.Run(async () =>
        {
            try { await _scanner.ScanAsync(source).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("Watch", $"Rescan of '{source.Path}' failed: {ex.Message}"); }
        });

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (w, t) in _watchers.Values) { w.Dispose(); t.Dispose(); }
            _watchers.Clear();
        }
    }
}
