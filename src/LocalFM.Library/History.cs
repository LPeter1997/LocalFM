using LocalFM.Core;

namespace LocalFM.Library;

/// <summary>Listening history (the source of all statistics) and a small key/value store.</summary>
public sealed class History(Database db)
{
    public Task RecordPlayAsync(Ulid recordingId, Ulid fileId, DateTimeOffset startedAt, TimeSpan listened, bool completed) =>
        db.Writer.Run((c, t) => new Tx(c, t).Exec(
            "INSERT INTO play_event(id, recording_id, audio_file_id, started_at, ms_listened, completed) VALUES ($1,$2,$3,$4,$5,$6)",
            Ulid.NewUlid(), recordingId, fileId, startedAt, (long)listened.TotalMilliseconds, completed));

    public int PlayCount(Ulid recordingId)
    {
        using var c = db.Open();
        return (int)c.Scalar<long>("SELECT COUNT(*) FROM play_event WHERE recording_id = $1 AND (completed = 1 OR ms_listened >= 30000)", recordingId);
    }

    public string? Get(string key)
    {
        using var c = db.Open();
        return c.Scalar<string>("SELECT value FROM kv WHERE key = $1", key);
    }

    public Task SetAsync(string key, string value) =>
        db.Writer.Run((c, t) => new Tx(c, t).Exec("INSERT OR REPLACE INTO kv(key, value) VALUES ($1,$2)", key, value));
}
