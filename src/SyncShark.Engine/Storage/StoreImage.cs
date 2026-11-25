using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

internal static class StoreImage
{
    internal static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            db.Open();
            db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
            db.CreateFunction<string, int>("plandepth", PathNames.Depth, isDeterministic: true);
            db.CreateFunction<string, int>("localaccessretrycause", cause => cause is nameof(TextKey.FailLocalInUse) or nameof(TextKey.FailLocalInUseOrAccessDenied) ? 1 : 0, isDeterministic: true);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    internal static object? Scalar(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd.ExecuteScalar();
    }

    internal static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    internal static void Checkpoint(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var rd = cmd.ExecuteReader();
        if (!rd.Read() || rd.GetInt32(rd.GetOrdinal("busy")) != 0)
            throw new IOException(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, db.DataSource)));
    }

    internal static void Verify(SqliteConnection db, int version, SqliteTransaction? tx = null)
    {
        if (!Equals(Scalar(db, "PRAGMA user_version", tx), (long)version) || !Equals(Scalar(db, "PRAGMA integrity_check", tx), "ok") || Scalar(db, "PRAGMA foreign_key_check", tx) != null)
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, db.DataSource)));
        if (version == Schema.Version)
            ValidateShape(db, tx);
    }

    internal static void ValidateShape(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT p.def_json,s.local_published_usn,h.last_key,d.deleted_ticks,j.next_usn,m.skip_cause,a.launch FROM pair p,synced_entry s,discovery_deleted_block h,discovery_deleted_delta d,journal_position j,issue_member m,accepted_job a LIMIT 0";
        using var reader = cmd.ExecuteReader();
    }
}
