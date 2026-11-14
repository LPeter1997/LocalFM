using LocalFM.Core;

namespace LocalFM.Library;

public sealed record Source(Ulid Id, string Kind, string Path, bool Enabled, long? LastScanAt);

public static class Sources
{
    public static List<Source> List(Database db)
    {
        using var c = db.Open();
        return c.Query("SELECT id, kind, path, enabled, last_scan_at FROM source ORDER BY path",
            r => new Source(r.Id(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.Long(4)));
    }

    public static Task<Source> AddFolderAsync(Database db, string path)
    {
        string full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return db.Writer.Run((c, t) =>
        {
            var tx = new Tx(c, t);
            var existing = tx.Single("SELECT id, kind, path, enabled, last_scan_at FROM source WHERE path = $1",
                r => new Source(r.Id(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.Long(4)), full);
            if (existing is not null) return existing;
            var s = new Source(Ulid.NewUlid(), "folder", full, true, null);
            tx.Exec("INSERT INTO source(id, kind, path, enabled, added_at) VALUES ($1,$2,$3,1,$4)", s.Id, s.Kind, s.Path, Sql.Now());
            return s;
        });
    }

    /// <summary>Removes the source. Its files stay in the knowledge base flagged missing, so edits and
    /// history are kept if the folder is added back.</summary>
    public static Task RemoveAsync(Database db, Ulid id) => db.Writer.Run((c, t) =>
    {
        var tx = new Tx(c, t);
        tx.Exec("UPDATE audio_file SET missing = 1 WHERE source_id = $1", id);
        tx.Exec("DELETE FROM source WHERE id = $1", id);
    });
}
