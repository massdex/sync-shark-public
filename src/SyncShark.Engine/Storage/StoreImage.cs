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
            PathFunctions(db);
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

    /// <summary>The one registration of the path functions the stored-path SQL calls (keys, migration split, folder rebase).</summary>
    internal static void PathFunctions(SqliteConnection db)
    {
        db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
        db.CreateFunction<string, string>("pathfolder", PathNames.Folder, isDeterministic: true);
        db.CreateFunction<string, string>("pathname", PathNames.Name, isDeterministic: true);
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
        // Columns alone are not the shape: the path tables' indexes and key triggers must match the created schema exactly.
        if (!Shape(db, tx).SequenceEqual(Model.Value))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, db.DataSource)));
    }

    /// <summary>The (type, name, sql) rows of the path tables, in creation order (tables, indexes, triggers).</summary>
    internal static List<(string Type, string Name, string? Sql)> Shape(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = Sql.Get(SqlKey.StoreImageValidateShape2);
        using var rd = cmd.ExecuteReader();
        var rows = new List<(string, string, string?)>();
        while (rd.Read())
            rows.Add((rd.GetString(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2)));
        return rows;
    }

    /// <summary>The shape of a freshly created schema, built once per process from the constant create text.</summary>
    internal static readonly Lazy<List<(string Type, string Name, string? Sql)>> Model = new(() =>
    {
        using var db = Open(":memory:", SqliteOpenMode.ReadWriteCreate);
        Execute(db, Schema.Create);
        return Shape(db);
    });
}
