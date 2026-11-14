using LocalFM.Core;

namespace LocalFM.Library;

public sealed record PlaylistSummary(Ulid Id, string Name, string? Description, int EntryCount, long UpdatedAt);

/// <summary>User playlists. Entries reference recordings, so they survive file moves and re-rips.</summary>
public sealed class Playlists(Database db)
{
    public List<PlaylistSummary> List()
    {
        using var c = db.Open();
        return c.Query("""
            SELECT p.id, p.name, p.description, (SELECT COUNT(*) FROM playlist_entry e WHERE e.playlist_id = p.id), p.updated_at
            FROM playlist p ORDER BY p.name COLLATE NOCASE
            """, r => new PlaylistSummary(r.Id(0), r.GetString(1), r.Str(2), r.GetInt32(3), r.GetInt64(4)));
    }

    /// <summary>Recording ids in playlist order.</summary>
    public List<Ulid> Entries(Ulid playlistId)
    {
        using var c = db.Open();
        return c.Query("SELECT recording_id FROM playlist_entry WHERE playlist_id = $1 ORDER BY position", r => r.Id(0), playlistId);
    }

    public Task<Ulid> CreateAsync(string name, IReadOnlyList<Ulid>? recordings = null) => db.Writer.Run((c, t) =>
    {
        var tx = new Tx(c, t);
        var id = Ulid.NewUlid();
        long now = Sql.Now();
        tx.Exec("INSERT INTO playlist(id, name, created_at, updated_at) VALUES ($1,$2,$3,$3)", id, name.Trim(), now);
        if (recordings is not null) Append(tx, id, recordings, now);
        return id;
    });

    public Task RenameAsync(Ulid id, string name) =>
        db.Writer.Run((c, t) => new Tx(c, t).Exec("UPDATE playlist SET name = $2, updated_at = $3 WHERE id = $1", id, name.Trim(), Sql.Now()));

    public Task DeleteAsync(Ulid id) =>
        db.Writer.Run((c, t) => new Tx(c, t).Exec("DELETE FROM playlist WHERE id = $1", id));

    public Task AppendAsync(Ulid id, IReadOnlyList<Ulid> recordings) =>
        db.Writer.Run((c, t) => Append(new Tx(c, t), id, recordings, Sql.Now()));

    /// <summary>Replaces the whole entry list (used for reorder/remove; playlists are small).</summary>
    public Task SetEntriesAsync(Ulid id, IReadOnlyList<Ulid> recordings) => db.Writer.Run((c, t) =>
    {
        var tx = new Tx(c, t);
        tx.Exec("DELETE FROM playlist_entry WHERE playlist_id = $1", id);
        Append(tx, id, recordings, Sql.Now());
    });

    private static void Append(Tx tx, Ulid playlistId, IReadOnlyList<Ulid> recordings, long now)
    {
        long pos = tx.Long("SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_entry WHERE playlist_id = $1", playlistId);
        foreach (var rec in recordings)
            tx.Exec("INSERT INTO playlist_entry(id, playlist_id, position, recording_id, added_at) VALUES ($1,$2,$3,$4,$5)",
                Ulid.NewUlid(), playlistId, pos++, rec, now);
        tx.Exec("UPDATE playlist SET updated_at = $2 WHERE id = $1", playlistId, now);
    }
}
