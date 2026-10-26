using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Failure-injection seam for ProtectionProjectionChecks, after one root is copied but before commit.
    internal Action<SqliteConnection, SqliteTransaction>? ProtectionRootCopiedForTest { get; set; }

    // These TEMP tables are the private planning reader's projected inputs, not a second
    // state model. Shadowing lets both indexed and exact fallback queries keep one predicate.
    // temp_store=FILE and the bounded TEMP cache are set by OpenProtectionSnapshot.
    private void MaterializeProtectionSnapshot(SqliteConnection connection, SqliteTransaction transaction,
        long? a, long? b, string pairId)
    {
        StoreSchema.Execute(connection, transaction, """
            CREATE TEMP TABLE file_state(root_no INTEGER,dir_no INTEGER,name_key TEXT,name TEXT,
              object_id TEXT,flags INTEGER,kind INTEGER,PRIMARY KEY(root_no,dir_no,name_key,name)) WITHOUT ROWID;
            """);
        // Gap reads must precede the TEMP gap shadows: the shared predicate reads main here.
        foreach (var root in new[] { a, b }.OfType<long>().Distinct())
        {
            MaterializeProtectionRoot(connection, transaction, root, _storageEnabled);
            ProtectionRootCopiedForTest?.Invoke(connection, transaction);
        }
        StoreSchema.Execute(connection, transaction, """
            CREATE TEMP TABLE dir AS SELECT dir_no,path_key FROM main.dir
              WHERE dir_no IN (SELECT dir_no FROM file_state);
            CREATE UNIQUE INDEX temp.planning_dir_number ON dir(dir_no);
            CREATE INDEX temp.dir_key ON dir(path_key);
            CREATE INDEX temp.fs_obj ON file_state(root_no,object_id);
            CREATE INDEX temp.fs_flagged ON file_state(root_no) WHERE flags!=0;
            CREATE INDEX temp.fs_nonpointer ON file_state(root_no) WHERE (flags & ~1024)!=0;
            CREATE TEMP TABLE local_gap AS SELECT root_no,discovery_done,after_object FROM main.local_gap
              WHERE root_no IN ($a,$b);
            CREATE TEMP TABLE local_gap_item AS SELECT root_no,object_id FROM main.local_gap_item
              WHERE root_no IN ($a,$b);
            CREATE INDEX temp.planning_gap_object ON local_gap_item(root_no,object_id);
            """, ("$a", (object?)a ?? DBNull.Value), ("$b", (object?)b ?? DBNull.Value));
        MaterializeProtectionObligations(connection, transaction, pairId);
    }

    private static void MaterializeProtectionRoot(SqliteConnection connection, SqliteTransaction transaction,
        long root, bool storageEnabled)
    {
        var (gap, done, after) = ReadProtectionGap(connection, transaction, root, ItemFlags.NeedsRehash);
        // Sources only narrow candidates; ProtectionHeldSql remains the single held filter.
        var sources = new[]
        {
            "FROM main.file_state f INDEXED BY fs_flagged WHERE f.root_no=$r AND f.flags!=0",
            "FROM main.file_state f INDEXED BY fs_obj WHERE f.root_no=$r AND f.object_id>$after AND $gap=1 AND $done=0",
            "FROM main.local_gap_item gi CROSS JOIN main.file_state f INDEXED BY fs_obj " +
                "ON f.root_no=gi.root_no AND f.object_id=gi.object_id WHERE gi.root_no=$r AND $gap=1"
        };
        foreach (var source in sources)
        {
            using var held = StoreSchema.Command(connection, transaction, StorageProtectionSql(
                "INSERT OR IGNORE INTO temp.file_state SELECT f.root_no,f.dir_no,f.name_key,f.name,f.object_id,f.flags,f.kind " +
                source + " AND " + ProtectionHeldSql + ";", ItemFlags.NeedsRehash, storageEnabled));
            BindProtection(held, root, ItemFlags.NeedsRehash, gap, done, after);
            held.ExecuteNonQuery();
        }
    }

    private static void MaterializeProtectionObligations(SqliteConnection connection, SqliteTransaction transaction,
        string pairId) => StoreSchema.Execute(connection, transaction, """
            CREATE TEMP TABLE pair AS SELECT pair_no,pair_id FROM main.pair WHERE pair_id=$id;
            CREATE TEMP TABLE sync_retry AS SELECT pair_no,path_key,retry_after,
              json_object('ReprobeEvidence',json_extract(json,'$.ReprobeEvidence')) AS json
              FROM main.sync_retry WHERE pair_no IN (SELECT pair_no FROM temp.pair);
            CREATE INDEX temp.planning_retry_path ON sync_retry(pair_no,path_key);
            CREATE TEMP TABLE notice AS SELECT pair_no,path_key,reason_key,state,restore_hold
              FROM main.notice WHERE pair_no IN (SELECT pair_no FROM temp.pair)
              AND reason_key='recovery-restore-pending' AND restore_hold=1;
            CREATE INDEX temp.planning_restore_path ON notice(pair_no,path_key);
            """, ("$id", pairId));
}
