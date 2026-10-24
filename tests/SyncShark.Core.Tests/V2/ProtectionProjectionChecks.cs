using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class ProtectionProjectionChecks
{
    private static PairDef Pair() => new("pair", "Pair A",
        new("left", SyncProviders.Local, null, "/left", "left"),
        new("right", SyncProviders.Local, null, "/right", "right"), [], [], true);

    private static ItemState File(string path, string id, ItemFlags flags = ItemFlags.None) =>
        new(path, ItemKind.File, id, null, new string('a', 64), 1, DateTimeOffset.UnixEpoch, flags);

    private static void Seed(SqliteStateStore store, PairDef pair, Side side, ItemState[] items)
    {
        var root = side == Side.A ? pair.A.RootId : pair.B.RootId;
        var gen = store.BeginGeneration(root, "fixture");
        store.WriteSnapshot(new(root, side, gen, "cursor", items, []));
        store.CompleteGeneration(gen, "cursor", true);
    }

    private static SqliteConnection Writer(string folder)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    internal static void Run(string folder)
    {
        foreach (var storage in new[] { false, true })
            foreach (var side in new[] { Side.A, Side.B })
                Mixed(Path.Combine(folder, $"mixed-{storage}-{side}"), storage, side);
        CopyFailure(Path.Combine(folder, "copy-failure"));
        Retries(Path.Combine(folder, "retries"));
    }

    private static void Mixed(string folder, bool storage, Side discoverySide)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        if (storage) store.EnableStorage();
        var pair = Pair();
        store.SavePair(pair);
        Seed(store, pair, Side.A, [File("A/Nested/Flag", "a-flag", ItemFlags.NeedsRehash),
            File("A/Case", "a-case", ItemFlags.NeedsRehash), File("A/case", "a-case-other"),
            File("A/Range", "z-range"), File("A/Queued", "a-queued"),
            File("A-Other/Clean", "a-clean"), File("Pointer", "a-pointer", ItemFlags.OwnedPointer)]);
        Seed(store, pair, Side.B, [File("B/Queued", "a-queued"), File("B/ReleasedRange", "z-range"),
            File("B/Nested/Flag", "a-flag", ItemFlags.NeedsRehash)]);
        foreach (var root in new[] { pair.A.RootId, pair.B.RootId })
            ((ILocalGapStore)store).BeginGap(root, "cursor", 1, false, "fixture gap");
        using var writer = Writer(folder);
        StoreSchema.Execute(writer, null, """
            UPDATE local_gap SET after_object='m';
            UPDATE local_gap SET discovery_done=1 WHERE root_no=(SELECT root_no FROM root WHERE root_id=$finished);
            INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json)
              SELECT root_no,'a-queued',epoch,'{}' FROM local_gap;
            """, ("$finished", discoverySide == Side.A ? pair.B.RootId : pair.A.RootId));
        string[] paths = ["", "A", "A/Nested", "A/Nested/Flag", "A/Nested/Flag/Child", "A/CASE",
            "A/Case/Child", "A/Range", "A/Queued", "A-Other", "A-Other/Clean", "A/Missing", "B",
            "B/Queued", "B/Queued/Child", "B/ReleasedRange", "B/Nested/Flag", "Pointer", "Missing"];
        var expected = store.ProtectionBatch(pair.A.RootId, ItemFlags.NeedsRehash, paths)
            .Zip(store.ProtectionBatch(pair.B.RootId, ItemFlags.NeedsRehash, paths),
                (a, b) => new ProtectionMatch(a.Covered || b.Covered, a.Contains || b.Contains)).ToArray();
        LocalChecks.Require(expected[0].Contains && expected[3].Covered && expected[4].Covered && expected[5].Covered &&
            expected[7].Covered == (discoverySide == Side.A) && expected[8].Covered && !expected[9].Contains &&
            expected[13].Covered && expected[15].Covered == (discoverySide == Side.B) && !expected[17].Covered,
            "mixed oracle exercises flags, case aliases, both gap branches, ancestors and clean siblings");
        using var frozen = store.OpenPlanningProtection(pair);
        using var stream = frozen.Stream();
        LocalChecks.Require(stream.SourcePasses == 3 && frozen.Read(paths).SequenceEqual(expected) &&
            stream.Read(paths).SequenceEqual(expected), "TEMP exact and indexed reads equal main mixed fixture on both sides");
        StoreSchema.Execute(writer, null, "UPDATE file_state SET flags=0; UPDATE local_gap SET discovery_done=1; DELETE FROM local_gap_item;");
        LocalChecks.Require(store.ProtectionBatch(pair.A.RootId, ItemFlags.NeedsRehash, paths).All(x => !x.Covered && !x.Contains) &&
            store.ProtectionBatch(pair.B.RootId, ItemFlags.NeedsRehash, paths).All(x => !x.Covered && !x.Contains),
            "live release removes both gap branches and flags on both sides");
        using var fallback = frozen.Stream();
        LocalChecks.Require(fallback.SourcePasses == 0 && frozen.Read(paths).SequenceEqual(expected) &&
            stream.Read(paths).SequenceEqual(expected) && fallback.Read(paths).SequenceEqual(expected),
            "range, queued and side B holds remain frozen through all reader paths");
        foreach (Action mismatch in new Action[] {
            () => frozen.RetryLookup("other", new HashSet<string>(), false),
            () => frozen.RetryProtected("other", "", true, new HashSet<string>(), false),
            () => frozen.RecoveryRestoreProtected("other", "", true) })
        {
            var threw = false;
            try { mismatch(); } catch (ArgumentException) { threw = true; }
            LocalChecks.Require(threw, "mismatched pair fails closed before querying TEMP");
        }
    }

    private static void CopyFailure(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = Pair();
        store.SavePair(pair);
        Seed(store, pair, Side.A, [File("Held", "entity", ItemFlags.NeedsRehash)]);
        Seed(store, pair, Side.B, [File("HeldB", "entity-b", ItemFlags.NeedsRehash)]);
        SqliteConnection? copying = null;
        var injected = new IOException("injected after first root copy");
        store.ProtectionRootCopiedForTest = (connection, transaction) =>
        {
            copying = connection;
            LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(connection, transaction,
                "SELECT count(*) FROM temp.file_state;")) == 1, "failure occurs after a real partial copy");
            throw injected;
        };
        Exception? caught = null;
        try { using var unexpected = store.OpenPlanningProtection(pair); }
        catch (Exception error) { caught = error; }
        finally { store.ProtectionRootCopiedForTest = null; }
        LocalChecks.Require(ReferenceEquals(caught, injected) && copying?.State == System.Data.ConnectionState.Closed,
            "mid-copy failure propagates and disposes reader instead of returning live fallback");
        using var writer = Writer(folder);
        using var command = writer.CreateCommand();
        ProtectionCheckpointChecks.Checkpoint(command);
        using var retry = store.OpenPlanningProtection(pair);
        LocalChecks.Require(retry.Read(["Held", "HeldB"]).All(x => x.Covered), "fresh copy succeeds after failed reader cleanup");
    }

    private static void Retries(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = Pair();
        store.SavePair(pair);
        using var writer = Writer(folder);
        StoreSchema.Execute(writer, null, """
            INSERT INTO sync_retry(pair_no,path_key,retry_after,json)
            SELECT pair_no,'RESTORED/FILE',0,'{"ReprobeEvidence":true}' FROM pair;
            INSERT INTO sync_retry(pair_no,path_key,retry_after,json)
            SELECT pair_no,'ORDINARY/FILE',0,'{}' FROM pair;
            INSERT INTO sync_retry(pair_no,path_key,retry_after,json)
            SELECT pair_no,'PARKED/FILE',$max,'{"ReprobeEvidence":true}' FROM pair;
            """, ("$max", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
        using (var frozen = store.OpenPlanningProtection(pair))
        {
            var all = frozen.RetryLookup(pair.PairId, new HashSet<string>(), false);
            var restored = frozen.RetryLookup(pair.PairId, new HashSet<string>(), true);
            StoreSchema.Execute(writer, null, "DELETE FROM sync_retry;");
            LocalChecks.Require(all("Restored/File", false) && all("Ordinary/File", false) && !all("Parked/File", false) &&
                restored("Restored/File", false) && restored("Restored", true) && !restored("Ordinary/File", false) &&
                !restored("Parked/File", false), "projected ReprobeEvidence preserves restored-only and parked eligibility after release");
        }
        StoreSchema.Execute(writer, null, """
            WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<$limit)
            INSERT INTO sync_retry(pair_no,path_key,retry_after,json)
            SELECT p.pair_no,'BACKLOG/'||printf('%07d',n.x),0,'{"ReprobeEvidence":true}' FROM n CROSS JOIN pair p;
            """, ("$limit", RetryPathIndex.PathLimit));
        using var large = store.OpenPlanningProtection(pair);
        var reads = 0;
        var lookup = large.RetryLookup(pair.PairId, new HashSet<string>(), true, () => reads++);
        StoreSchema.Execute(writer, null, "DELETE FROM sync_retry;");
        LocalChecks.Require(lookup("Backlog/0000000", false) && lookup("Backlog", true) && !lookup("Missing", false) && reads == 3,
            "backlog beyond real cap uses exact TEMP fallback after live release");
    }

    // Explicit opt-in; fixture and process TEMP must be on the rehearsal drive, never a live store.
    internal static void Million(string folder)
    {
        if (!Path.GetPathRoot(Path.GetFullPath(folder))!.Equals("F:\\", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetPathRoot(Path.GetTempPath())!.Equals("F:\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The measurement requires fixture and TEMP on F:.");
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = Pair();
        store.SavePair(pair);
        Seed(store, pair, Side.A, [File("Parent/Seed", "seed")]);
        using var writer = Writer(folder);
        StoreSchema.Execute(writer, null, """
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1000000)
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
            SELECT r.root_no,d.dir_no,printf('FILE-%07d',x),printf('file-%07d',x),0,printf('object-%07d',x),1,0,256
              FROM n CROSS JOIN root r CROSS JOIN dir d WHERE r.root_id='left' AND d.path='Parent';
            """);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var frozen = store.OpenPlanningProtection(pair);
        watch.Stop();
        var copyBytes = frozen.TempBytesForTest;
        using var stream = frozen.Stream();
        LocalChecks.Require(stream.SourcePasses == 3 && frozen.Read(["Parent/file-1000000"])[0].Covered,
            "million held rows are copied and indexed");
        Console.WriteLine($"protection-million: held=1000000; copy-ms={watch.ElapsedMilliseconds}; temp-copy-bytes={copyBytes}; temp-with-stream-bytes={frozen.TempBytesForTest}");
        using var command = writer.CreateCommand();
        ProtectionCheckpointChecks.Checkpoint(command);
    }
}
