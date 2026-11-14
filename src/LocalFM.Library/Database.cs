using System.Reflection;
using LocalFM.Core;
using Microsoft.Data.Sqlite;

namespace LocalFM.Library;

/// <summary>
/// The knowledge base. Reads open short-lived pooled connections from any thread; all writes go
/// through <see cref="Writer"/>, a single background thread, so the UI and the audio engine never
/// wait on SQLite locks or disk flushes.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly string _connectionString;

    public Database(string path)
    {
        FilePath = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        using (var c = OpenRaw())
        {
            c.Exec("PRAGMA journal_mode = WAL;");
            Migrate(c);
        }
        Writer = new LibraryWriter(this);
    }

    public string FilePath { get; }

    public LibraryWriter Writer { get; }

    /// <summary>Opens a connection for reading (or for the writer thread).</summary>
    public SqliteConnection Open() => OpenRaw();

    private SqliteConnection OpenRaw()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Exec("PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
        return c;
    }

    private static void Migrate(SqliteConnection c)
    {
        long version = c.Scalar<long>("PRAGMA user_version;");
        var asm = typeof(Database).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".Migrations.", StringComparison.Ordinal))
            .Select(n => (Name: n, Version: int.Parse(n.Split(".Migrations.")[1].Split('_')[0])))
            .OrderBy(x => x.Version)
            .ToList();

        foreach (var (name, v) in scripts)
        {
            if (v <= version) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            string sql = new StreamReader(stream).ReadToEnd();
            using var tx = c.BeginTransaction();
            c.Exec(sql, tx);
            c.Exec($"PRAGMA user_version = {v};", tx);
            tx.Commit();
            Log.Info("Library", $"Migrated database to v{v}");
        }
    }

    public void Dispose()
    {
        Writer.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
