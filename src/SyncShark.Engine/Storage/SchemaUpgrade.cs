using Microsoft.Data.Sqlite;
using static Massdex.SyncShark.Engine.Storage.StoreImage;

namespace Massdex.SyncShark.Engine.Storage;

internal static class SchemaUpgrade
{
    private static readonly string[] Rebuilt =
        {
            Sql.Get(SqlKey.SchemaUpgradeRebuilt1),
            Sql.Get(SqlKey.SchemaUpgradeRebuilt2),
            Sql.Get(SqlKey.SchemaUpgradeRebuilt3),
            Sql.Get(SqlKey.SchemaUpgradeRebuilt4)
        };
    private static readonly string[] Retired =
        {
            Sql.Get(SqlKey.SchemaUpgradeRetired1),
            Sql.Get(SqlKey.SchemaUpgradeRetired2),
            Sql.Get(SqlKey.SchemaUpgradeRetired3),
            Sql.Get(SqlKey.SchemaUpgradeRetired4),
            Sql.Get(SqlKey.SchemaUpgradeRetired5),
            Sql.Get(SqlKey.SchemaUpgradeRetired6),
            Sql.Get(SqlKey.SchemaUpgradeRetired7)
        };

    // The v16 body still produces v17; the v17 body then splits stored paths into folder rows.
    private const int V17 = 17;

    internal static void Upgrade(SqliteConnection db, int version, CancellationToken ct)
    {
        if (version == 16) UpgradeV16(db, ct);
        UpgradeV17(db, ct);
    }

    private static void UpgradeV17(SqliteConnection db, CancellationToken ct)
    {
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade1));
        var model = Model.Value.Where(r => r.Sql != null).ToList();
        using (var tx = db.BeginTransaction())
        {
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade2), tx);
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeRename), tx);
            foreach (var r in model.Where(r => r.Type == "table"))
                Execute(db, r.Sql!, tx);
            foreach (var key in new[] { SqlKey.SchemaUpgradeCopyFolders, SqlKey.SchemaUpgradeCopyEntries, SqlKey.SchemaUpgradeCopyKeys })
            {
                ct.ThrowIfCancellationRequested();
                Execute(db, Sql.Get(key), tx);
            }
            // Every old row must reappear once, field for field and type for type, with exactly one key row.
            long[] n;
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Sql.Get(SqlKey.SchemaUpgradeCount);
                using var rd = cmd.ExecuteReader();
                n = rd.Read() ? new[] { rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2) } : new long[] { -1, 0, 0 };
            }
            if (n[0] != n[1] || n[0] != n[2] || !Equals(Scalar(db, Sql.Get(SqlKey.SchemaUpgradeRoundTrip), tx), n[0]))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, db.DataSource)));
            ct.ThrowIfCancellationRequested();
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeDropOld), tx);
            foreach (var r in model.Where(r => r.Type != "table"))
                Execute(db, r.Sql!, tx);
            Execute(db, $"{Sql.Get(SqlKey.SchemaUpgradeUpgrade11)}{Schema.Version}", tx);
            Verify(db, Schema.Version, tx);
            tx.Commit();
        }
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade12));
        // Drained by reading: one row per freed page; never plain VACUUM, which can renumber rowids.
        using var vacuum = db.CreateCommand();
        vacuum.CommandText = Sql.Get(SqlKey.SchemaUpgradeVacuum);
        using var pages = vacuum.ExecuteReader();
        while (pages.Read()) { }
    }

    private static void UpgradeV16(SqliteConnection db, CancellationToken ct)
    {
        // Keep the source intact. Only the private backup image is rebuilt, and generated columns are never copied as input.
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade1));
        using var tx = db.BeginTransaction();
        // Renaming a rebuilt parent must not retarget the kept tables' foreign keys to its old name.
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade2), tx);
        var counts = new[]
        {
            Sql.Get(SqlKey.SchemaUpgradeUpgrade3),
            Sql.Get(SqlKey.SchemaUpgradeUpgrade4),
            Sql.Get(SqlKey.SchemaUpgradeUpgrade5),
            Sql.Get(SqlKey.SchemaUpgradeUpgrade6)
        }.ToDictionary(t => t, t => Scalar(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade7) + t, tx));
        RebuildTables(db, tx);
        CopyTables(db, tx, ct);
        CopyIssues(db, tx);
        CopyJobs(db, tx);
        // Dropped v16 change work lies behind the saved journal position and cursor; owe a full Repair so it is compared.
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade8), tx);
        // Which Dropbox file deletion a spend consumed; a spend made before this column recorded none and stays NULL.
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade9), tx);
        // synced_entry is kept rather than rebuilt; the retained keep flag is added in place.
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade10), tx);
        Execute(db, Schema.KeepIndex, tx);
        Finish(db, tx);
        VerifyCounts(db, tx, counts);
        Execute(db, $"{Sql.Get(SqlKey.SchemaUpgradeUpgrade11)}{V17}", tx);
        Verify(db, V17, tx);
        tx.Commit();
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeUpgrade12));
    }

    private static string Quote(string name) => Sql.Get(SqlKey.SchemaUpgradeQuote1) + name.Replace(Sql.Get(SqlKey.SchemaUpgradeQuote2), Sql.Get(SqlKey.SchemaUpgradeQuote3), StringComparison.Ordinal) + Sql.Get(SqlKey.SchemaUpgradeQuote4);

    private static List<string> Names(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var names = new List<string>();
        while (rd.Read())
            names.Add(rd.GetString(0));
        return names;
    }

    private static void RebuildTables(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeRebuildTables1), tx);
        foreach (var table in Rebuilt)
        {
            foreach (var i in Names(db, tx, Sql.Get(SqlKey.SchemaUpgradeRebuildTables2) + table + Sql.Get(SqlKey.SchemaUpgradeRebuildTables3)))
                Execute(db, Sql.Get(SqlKey.SchemaUpgradeRebuildTables4) + Quote(i), tx);
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeRebuildTables5) + table + Sql.Get(SqlKey.SchemaUpgradeRebuildTables6) + table, tx);
        }

        // Read the target DDL from a fresh in-memory schema; unchanged million-row tables and their indexes stay put.
        using (var model = Open(":memory:", SqliteOpenMode.ReadWriteCreate))
        {
            Execute(model, Schema.Create);
            using var ddl = model.CreateCommand();
            ddl.CommandText = Sql.Get(SqlKey.SchemaUpgradeRebuildTables7);
            using var rd = ddl.ExecuteReader();
            while (rd.Read())
                Execute(db, rd.GetString(0), tx);
        }
    }

    private static void CopyTables(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        foreach (var table in Rebuilt)
        {
            ct.ThrowIfCancellationRequested();
            var cols = string.Join(Sql.Get(SqlKey.SchemaUpgradeCopyTables1), Names(db, tx, Sql.Get(SqlKey.SchemaUpgradeCopyTables2) + table + Sql.Get(SqlKey.SchemaUpgradeCopyTables3)).Select(Quote));
            var where = table == Sql.Get(SqlKey.SchemaUpgradeCopyTables4) ? Sql.Get(SqlKey.SchemaUpgradeCopyTables5) : "";
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeCopyTables6) + table + Sql.Get(SqlKey.SchemaUpgradeCopyTables7) + cols + Sql.Get(SqlKey.SchemaUpgradeCopyTables8) + cols + Sql.Get(SqlKey.SchemaUpgradeCopyTables9) + table + where, tx);
            if (!Equals(Scalar(db, Sql.Get(SqlKey.SchemaUpgradeCopyTables10) + table, tx), Scalar(db, Sql.Get(SqlKey.SchemaUpgradeCopyTables11) + table + where, tx)))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, table)));
        }
    }

    private static void CopyIssues(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeCopyIssues1), tx);
        if (!Equals(Scalar(db, Sql.Get(SqlKey.SchemaUpgradeCopyIssues2), tx), 0L))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, nameof(NoticeItem))));
    }

    private static void CopyJobs(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeCopyJobs1), tx);
        if (!Equals(Scalar(db, Sql.Get(SqlKey.SchemaUpgradeCopyJobs2), tx), 0L))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, nameof(AcceptedJob))));
    }

    private static void Finish(SqliteConnection db, SqliteTransaction tx)
    {
        // Reparenting may change ownership, never the number of retained physical members.
        if(!Equals(Scalar(db,Sql.Get(SqlKey.SchemaUpgradeFinish1),tx),Scalar(db,Sql.Get(SqlKey.SchemaUpgradeFinish2),tx)))
            throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation,(TextArg.Path,nameof(NoticeItem))));
        Execute(db, Sql.Get(SqlKey.SchemaUpgradeFinish3), tx);
        foreach (var table in Rebuilt.Where(t => t != Sql.Get(SqlKey.SchemaUpgradeFinish4)))
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeFinish5) + table, tx);
        foreach (var table in Retired)
            Execute(db, Sql.Get(SqlKey.SchemaUpgradeFinish6) + table, tx);
    }

    private static void VerifyCounts(SqliteConnection db, SqliteTransaction tx, Dictionary<string,object?> counts)
    {
        foreach (var(table, count)in counts)
            if (!Equals(count, Scalar(db, Sql.Get(SqlKey.SchemaUpgradeVerifyCounts1) + table, tx)))
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, table)));
    }
}
