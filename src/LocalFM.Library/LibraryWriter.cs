using System.Collections.Concurrent;
using LocalFM.Core;
using Microsoft.Data.Sqlite;

namespace LocalFM.Library;

/// <summary>
/// Serializes every write to the knowledge base on one low-priority background thread.
/// Each job runs in its own transaction; callers just await the returned task.
/// </summary>
public sealed class LibraryWriter : IDisposable
{
    private readonly BlockingCollection<Action<SqliteConnection>> _jobs = new();
    private readonly Thread _thread;
    private readonly Database _db;

    internal LibraryWriter(Database db)
    {
        _db = db;
        _thread = new Thread(Run) { Name = "LocalFM db writer", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Raised (on the writer thread) after each committed job; the UI uses it to refresh views.</summary>
    public event Action? Committed;

    public Task<T> Run<T>(Func<SqliteConnection, SqliteTransaction, T> job)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(c =>
        {
            using var tx = c.BeginTransaction();
            try
            {
                T result = job(c, tx);
                tx.Commit();
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { /* connection may be broken; ignore */ }
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task Run(Action<SqliteConnection, SqliteTransaction> job) =>
        Run<bool>((c, tx) => { job(c, tx); return true; });

    private void Run()
    {
        using var c = _db.Open();
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(c); }
            catch (Exception ex) { Log.Error("Library", "Writer job failed", ex); }
            try { Committed?.Invoke(); } catch (Exception ex) { Log.Warn("Library", "Committed handler failed", ex); }
        }
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}
