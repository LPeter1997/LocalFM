using Microsoft.Data.Sqlite;

namespace LocalFM.Library;

/// <summary>A connection + transaction pair with terse query helpers, used by writer jobs.</summary>
public readonly record struct Tx(SqliteConnection Connection, SqliteTransaction Transaction)
{
    public int Exec(string sql, params object?[] args)
    {
        using var cmd = Sql.Command(Connection, sql, Transaction, args);
        return cmd.ExecuteNonQuery();
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args) =>
        Connection.Query(Transaction, sql, map, args);

    /// <summary>First row mapped, or null. Map to a nullable type (e.g. <c>r =&gt; r.IdN(0)</c>) for structs.</summary>
    public T? Single<T>(string sql, Func<SqliteDataReader, T?> map, params object?[] args)
    {
        using var cmd = Sql.Command(Connection, sql, Transaction, args);
        using var r = cmd.ExecuteReader();
        return r.Read() ? map(r) : default;
    }

    public long Long(string sql, params object?[] args)
    {
        using var cmd = Sql.Command(Connection, sql, Transaction, args);
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }
}
