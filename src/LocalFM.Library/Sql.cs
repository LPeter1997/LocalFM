using Microsoft.Data.Sqlite;

namespace LocalFM.Library;

/// <summary>Tiny reflection-free helpers over ADO.NET (AOT-safe). Parameters are positional: $1, $2, …</summary>
public static class Sql
{
    public static int Exec(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params object?[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    public static int Exec(this SqliteConnection c, string sql, params object?[] args) => Exec(c, sql, null, args);

    public static T? Scalar<T>(this SqliteConnection c, string sql, params object?[] args)
    {
        using var cmd = Command(c, sql, null, args);
        object? v = cmd.ExecuteScalar();
        if (v is null || v is DBNull) return default;
        return (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = Command(c, sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static T? QuerySingle<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = Command(c, sql, null, args);
        return QuerySingle(c, cmd, map);
    }

    /// <summary>Runs a prepared command (e.g. one bound to a transaction) and maps the first row.</summary>
    public static T? QuerySingle<T>(this SqliteConnection c, SqliteCommand cmd, Func<SqliteDataReader, T> map)
    {
        using (cmd)
        using (var r = cmd.ExecuteReader())
            return r.Read() ? map(r) : default;
    }

    public static List<T> Query<T>(this SqliteConnection c, SqliteTransaction tx, string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx, object?[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$" + (i + 1), ToDb(args[i]));
        return cmd;
    }

    private static object ToDb(object? v) => v switch
    {
        null => DBNull.Value,
        Core.Ulid u => u.ToString(),
        bool b => b ? 1 : 0,
        DateTimeOffset d => d.ToUnixTimeMilliseconds(),
        _ => v,
    };

    // reader helpers
    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? Long(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static int? Int(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static double? Dbl(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public static Core.Ulid Id(this SqliteDataReader r, int i) => Core.Ulid.Parse(r.GetString(i));
    public static Core.Ulid? IdN(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Core.Ulid.Parse(r.GetString(i));

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
