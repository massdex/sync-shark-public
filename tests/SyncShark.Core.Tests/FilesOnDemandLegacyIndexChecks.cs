using Massdex.SyncShark;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

// Compatibility for stores opted into Files On-Demand before OwnedPointer moved from bit 512 to 1024:
// the legacy partial-index definitions are the proof that 512 meant OwnedPointer and must move; in a
// current store 512 is RequiresExport and must never move. No native API is used.
internal static class FilesOnDemandLegacyIndexChecks
{
    internal static void Run(string root)
    {
        LegacyOwnedPointerMigrates(Path.Combine(root, "legacy-512"));
        ModernRequiresExportIsPreserved(Path.Combine(root, "modern-1024"));
        FailedMigrationRollsBackCompletely(Path.Combine(root, "legacy-rollback"));
    }

    private static void LegacyOwnedPointerMigrates(string root)
    {
        using (var connection = Fixture(root, legacy: true))
        {
            StoreSchema.Execute(connection, null, """
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags) VALUES
                  (1,1,'POINTER','pointer',0,'obj-pointer',3,0,512),
                  (1,1,'POINTER-REHASH','pointer-rehash',0,'obj-pointer-rehash',4,0,768),
                  (1,1,'POINTER-QUARANTINE','pointer-quarantine',0,'obj-pointer-quarantine',5,0,640),
                  (1,1,'POINTER-CARRIED','pointer-carried',0,'obj-pointer-carried',6,0,544),
                  (1,1,'REHASH-ONLY','rehash-only',0,'obj-rehash-only',7,0,256),
                  (1,1,'PLAIN','plain',0,'obj-plain',8,0,0);
                """);
            Require(IndexSql(connection, "fs_owned") is string owned && owned.Contains("512", StringComparison.Ordinal),
                "fixture starts with the legacy owned-pointer index");
            Require(IndexSql(connection, "fs_nonpointer") is string nonpointer && nonpointer.Contains("~512", StringComparison.Ordinal),
                "fixture starts with the legacy non-pointer index");
        }
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(store.IsStorageEnabled, "legacy opted-in store stays enabled");
            Require(Flags(store, "obj-pointer") == 1024, "legacy pointer bit moves to 1024");
            Require(Flags(store, "obj-pointer-rehash") == 1280, "legacy pointer keeps NeedsRehash and gains 1024");
            Require(Flags(store, "obj-pointer-quarantine") == 1152, "legacy pointer keeps Quarantined and gains 1024");
            Require(Flags(store, "obj-pointer-carried") == 1056, "legacy pointer keeps CarriedIgnore and gains 1024");
            Require(Flags(store, "obj-rehash-only") == 256, "non-pointer rehash row is untouched");
            Require(Flags(store, "obj-plain") == 0, "clean row is untouched");
            Require(store.LiveFlagged("fixture-root", ItemFlags.OwnedPointer).Select(i => i.ObjectId).OrderBy(id => id)
                .SequenceEqual(new[] { "obj-pointer", "obj-pointer-carried", "obj-pointer-quarantine", "obj-pointer-rehash" }),
                "owned-pointer reads route only through the rebuilt index");
            Require(store.LiveFlagged("fixture-root", ItemFlags.NeedsRehash).Select(i => i.ObjectId).OrderBy(id => id)
                .SequenceEqual(new[] { "obj-pointer-rehash", "obj-rehash-only" }),
                "needs-rehash reads still see pointer and non-pointer rows");
            var protection = store.ProtectionIndex("fixture-root", ItemFlags.OwnedPointer);
            Require(protection is not null && protection.Touches("Folder/pointer", descendants: false),
                "protection index resolves a migrated pointer");
        }
        using (var connection = Open(root))
        {
            Require(IndexSql(connection, "fs_owned") is string owned && owned.Contains("1024", StringComparison.Ordinal) &&
                !owned.Contains("512", StringComparison.Ordinal), "owned-pointer index is rebuilt on 1024");
            Require(IndexSql(connection, "fs_nonpointer") is string nonpointer && nonpointer.Contains("~1024", StringComparison.Ordinal) &&
                !nonpointer.Contains("512", StringComparison.Ordinal), "non-pointer index is rebuilt on 1024");
            Require(IndexSql(connection, "fs_flagged") is string flagged && flagged.Contains("flags != 0", StringComparison.Ordinal),
                "legacy 512-era shared index is restored");
            Require(StoreSchema.QueryPlan(connection, "owned",
                "SELECT 1 FROM file_state INDEXED BY fs_owned WHERE root_no=1 AND (flags & 1024)!=0;")
                .Any(line => line.Contains("fs_owned", StringComparison.Ordinal)), "1024 owned query has a plan");
            Require(StoreSchema.QueryPlan(connection, "nonpointer",
                "SELECT 1 FROM file_state INDEXED BY fs_nonpointer WHERE root_no=1 AND (flags & ~1024)!=0;")
                .Any(line => line.Contains("fs_nonpointer", StringComparison.Ordinal)), "1024 non-pointer query has a plan");
        }
        using (var reopened = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(Flags(reopened, "obj-pointer") == 1024 && Flags(reopened, "obj-plain") == 0,
                "reopening a migrated store changes nothing");
        }
    }

    private static void ModernRequiresExportIsPreserved(string root)
    {
        using (var connection = Fixture(root, legacy: false))
        {
            StoreSchema.Execute(connection, null, """
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags) VALUES
                  (1,1,'MODERN-POINTER','modern-pointer',0,'obj-modern-pointer',3,0,1024),
                  (1,1,'EXPORT','export',0,'obj-export',4,0,512),
                  (1,1,'EXPORT-REHASH','export-rehash',0,'obj-export-rehash',5,0,768),
                  (1,1,'BOTH','both',0,'obj-both',6,0,1536);
                """);
        }
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(Flags(store, "obj-modern-pointer") == 1024, "modern owned pointer stays 1024");
            Require(Flags(store, "obj-export") == 512, "modern RequiresExport stays 512 without legacy index evidence");
            Require(Flags(store, "obj-export-rehash") == 768, "RequiresExport with NeedsRehash keeps every bit");
            Require(Flags(store, "obj-both") == 1536, "RequiresExport with OwnedPointer keeps every bit");
            Require(store.LiveFlagged("fixture-root", ItemFlags.RequiresExport).Select(i => i.ObjectId).OrderBy(id => id)
                .SequenceEqual(new[] { "obj-both", "obj-export", "obj-export-rehash" }),
                "RequiresExport reads still find unmigrated export rows");
            Require(store.LiveFlagged("fixture-root", ItemFlags.OwnedPointer).Select(i => i.ObjectId).OrderBy(id => id)
                .SequenceEqual(new[] { "obj-both", "obj-modern-pointer" }),
                "OwnedPointer reads use the existing 1024 index");
        }
        using (var connection = Open(root))
        {
            Require(IndexSql(connection, "fs_owned") is string owned && owned.Contains("1024", StringComparison.Ordinal),
                "modern owned-pointer index is kept");
            Require(IndexSql(connection, "fs_nonpointer") is string nonpointer && nonpointer.Contains("~1024", StringComparison.Ordinal),
                "modern non-pointer index is kept");
            Require(IndexSql(connection, "fs_flagged") is string flagged && flagged.Contains("flags != 0", StringComparison.Ordinal),
                "1024-era shared preview index is restored");
        }
    }

    private static void FailedMigrationRollsBackCompletely(string root)
    {
        using var connection = Fixture(root, legacy: true);
        StoreSchema.Execute(connection, null, """
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
              VALUES(1,1,'POINTER','pointer',0,'obj-pointer',3,0,512);
            CREATE TRIGGER legacy_block BEFORE UPDATE ON file_state BEGIN SELECT RAISE(ABORT,'blocked'); END;
            """);
        var failed = false;
        try
        {
            FilesOnDemandSchema.Ensure(connection);
        }
        catch (SqliteException)
        {
            failed = true;
        }
        Require(failed, "a blocked migration surfaces its failure");
        Require(Flags(connection, "obj-pointer") == 512, "the row bit moves only when the transaction commits");
        Require(IndexSql(connection, "fs_owned") is string owned && owned.Contains("512", StringComparison.Ordinal) &&
            !owned.Contains("1024", StringComparison.Ordinal), "the index rebuild rolls back with the rows");
        Require(IndexSql(connection, "fs_flagged") is string flagged && flagged.Contains("~512", StringComparison.Ordinal),
            "the shared-index repair rolls back with the migration");
        Require(IndexSql(connection, "storage_item") is null && IndexSql(connection, "fs_pointer_verify") is null,
            "the schema replay shares the same transaction and rolls back too");

        StoreSchema.Execute(connection, null, "DROP TRIGGER legacy_block;");
        FilesOnDemandSchema.Ensure(connection);
        Require(Flags(connection, "obj-pointer") == 1024, "the unblocked migration completes");
        Require(IndexSql(connection, "fs_owned") is string rebuilt && rebuilt.Contains("1024", StringComparison.Ordinal),
            "the unblocked rebuild completes");
    }

    // The final26.2 base schema with an opted-in storage_policy and either the legacy 512 or the modern
    // 1024 partial indexes. The legacy fixture also carries the 512-era preview fs_flagged predicate.
    private static SqliteConnection Fixture(string root, bool legacy)
    {
        FilesOnDemandV20Fixture.Create(SqliteStateStore.DatabasePath(root));
        var connection = Open(root);
        StoreSchema.Execute(connection, null, legacy ? LegacyIndexes512 : ModernIndexes1024);
        return connection;
    }

    private const string LegacyIndexes512 = """
        CREATE TABLE IF NOT EXISTS storage_policy(root_id TEXT NOT NULL, object_id TEXT NOT NULL,
          mode TEXT NOT NULL CHECK(mode IN ('offline','online-only')), revision INTEGER NOT NULL,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        DROP INDEX IF EXISTS fs_owned;
        DROP INDEX IF EXISTS fs_nonpointer;
        DROP INDEX IF EXISTS fs_flagged;
        CREATE INDEX fs_owned ON file_state(root_no) WHERE (flags & 512) != 0;
        CREATE INDEX fs_nonpointer ON file_state(root_no) WHERE (flags & ~512) != 0;
        CREATE INDEX fs_flagged ON file_state(root_no) WHERE (flags & ~512) != 0;
        """;

    private const string ModernIndexes1024 = """
        CREATE TABLE IF NOT EXISTS storage_policy(root_id TEXT NOT NULL, object_id TEXT NOT NULL,
          mode TEXT NOT NULL CHECK(mode IN ('offline','online-only')), revision INTEGER NOT NULL,
          PRIMARY KEY(root_id,object_id)) WITHOUT ROWID;
        DROP INDEX IF EXISTS fs_owned;
        DROP INDEX IF EXISTS fs_nonpointer;
        DROP INDEX IF EXISTS fs_flagged;
        CREATE INDEX fs_owned ON file_state(root_no) WHERE (flags & 1024) != 0;
        CREATE INDEX fs_nonpointer ON file_state(root_no) WHERE (flags & ~1024) != 0;
        CREATE INDEX fs_flagged ON file_state(root_no) WHERE (flags & ~1024) != 0;
        """;

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SqliteStateStore.DatabasePath(root), Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? IndexSql(SqliteConnection connection, string name) =>
        StoreSchema.Scalar(connection, null, "SELECT sql FROM sqlite_master WHERE name=$name;", ("$name", name)) as string;

    private static long Flags(SqliteStateStore store, string objectId) =>
        (long)(store.ByObject("fixture-root", objectId)?.Flags ?? throw new InvalidOperationException("missing " + objectId));

    private static long Flags(SqliteConnection connection, string objectId) =>
        (long)StoreSchema.Scalar(connection, null, "SELECT flags FROM file_state WHERE object_id=$id;", ("$id", objectId))!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
