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
        cmd.CommandText = Sql.Get(SqlKey.StoreImageCheckpoint1);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read() || rd.GetInt32(rd.GetOrdinal("busy")) != 0)
            throw new IOException(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, db.DataSource)));
    }

    internal static void Verify(SqliteConnection db, int version, SqliteTransaction? tx = null)
    {
        if (!Equals(Scalar(db, Sql.Get(SqlKey.StoreImageVerify1), tx), (long)version) || !Equals(Scalar(db, Sql.Get(SqlKey.StoreImageVerify2), tx), "ok") || Scalar(db, Sql.Get(SqlKey.StoreImageVerify3), tx) != null)
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, db.DataSource)));
        if (version == Schema.Version)
            ValidateShape(db, tx);
    }

    internal static void ValidateShape(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = Sql.Get(SqlKey.StoreImageValidateShape1);
            using var reader = cmd.ExecuteReader();
        }
        // The column alone is not the v17 shape: the partial keep index is required (K07).
        if (!Equals(Scalar(db, Sql.Get(SqlKey.StoreImageValidateShape2), tx), Schema.KeepIndex))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, db.DataSource)));
    }
}
