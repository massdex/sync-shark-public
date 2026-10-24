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
        // Inventory flags are reconciled against durable entity debt, not accepted as its authority.
        foreach (var item in items.Where(item => (item.Flags & ItemFlags.NeedsRehash) != 0))
            store.RequireRehash(root, item, "fixture protection debt");
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
        var context = $"storage={storage}, discoverySide={discoverySide}";
        var none = new ProtectionMatch(false, false);
        var held = new ProtectionMatch(true, true);
        var contains = new ProtectionMatch(false, true);
        var ancestor = new ProtectionMatch(true, false);
        ProtectionMatch[] fixture = [contains, contains, contains, held, ancestor, held, ancestor,
            discoverySide == Side.A ? held : none, held, none, none, none, contains, held, ancestor,
            discoverySide == Side.B ? held : none, held, none, none];
        RequireMatches(paths, fixture, expected, "main fixture oracle " + context);
        using var frozen = store.OpenPlanningProtection(pair);
        using var stream = frozen.Stream();
        LocalChecks.Require(stream.SourcePasses == 3, "mixed fixture builds indexed TEMP stream " + context);
        RequireMatches(paths, expected, frozen.Read(paths), "TEMP exact versus main " + context);
        RequireMatches(paths, expected, stream.Read(paths), "TEMP stream versus main " + context);
        StoreSchema.Execute(writer, null, "UPDATE file_state SET flags=0; UPDATE local_gap SET discovery_done=1; DELETE FROM local_gap_item;");
        LocalChecks.Require(store.ProtectionBatch(pair.A.RootId, ItemFlags.NeedsRehash, paths).All(x => !x.Covered && !x.Contains) &&
            store.ProtectionBatch(pair.B.RootId, ItemFlags.NeedsRehash, paths).All(x => !x.Covered && !x.Contains),
            "live release removes both gap branches and flags on both sides");
        using var fallback = frozen.Stream();
        LocalChecks.Require(fallback.SourcePasses == 0, "mixed fixture exercises exact fallback " + context);
        RequireMatches(paths, expected, frozen.Read(paths), "frozen exact after release " + context);
        RequireMatches(paths, expected, stream.Read(paths), "frozen stream after release " + context);
        RequireMatches(paths, expected, fallback.Read(paths), "frozen fallback after release " + context);
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

    private static void RequireMatches(IReadOnlyList<string> paths, IReadOnlyList<ProtectionMatch> expected,
        IReadOnlyList<ProtectionMatch> actual, string context)
    {
        LocalChecks.Require(expected.Count == actual.Count, context + " result count");
        var differences = Enumerable.Range(0, paths.Count).Where(i => expected[i] != actual[i]).Select(i =>
            $"path='{paths[i]}': expected={expected[i]}, actual={actual[i]}, " +
            $"missing={(expected[i].Covered && !actual[i].Covered) || (expected[i].Contains && !actual[i].Contains)}, " +
            $"extra={(actual[i].Covered && !expected[i].Covered) || (actual[i].Contains && !expected[i].Contains)}");
        var detail = string.Join("; ", differences);
        LocalChecks.Require(detail.Length == 0, context + (detail.Length == 0 ? " matches" : ": " + detail));
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
        Seed(store, pair, Side.A, [File("Parent/file-0000000", "object-0000000", ItemFlags.NeedsRehash)]);
        using var writer = Writer(folder);
        StoreSchema.Execute(writer, null, """
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<999999)
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
            SELECT r.root_no,d.dir_no,printf('FILE-%07d',x),printf('file-%07d',x),0,printf('object-%07d',x),1,0,256
              FROM n CROSS JOIN root r CROSS JOIN dir d WHERE r.root_id='left' AND d.path='Parent';
            """);
        LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(writer, null,
            "SELECT count(*) FROM file_state;")) == 1_000_000, "measurement inventory has exactly one million rows");
        MeasureMillion(store, pair, writer, "flags");
        // Dispose the first private snapshot before measuring the unflagged fresh-gap range.
        StoreSchema.Execute(writer, null, "UPDATE file_state SET flags=0;");
        ((ILocalGapStore)store).BeginGap(pair.A.RootId, "cursor", 1, false, "fixture fresh gap");
        LocalChecks.Require(Convert.ToInt64(StoreSchema.Scalar(writer, null,
            "SELECT count(*) FROM local_gap WHERE after_object='' AND discovery_done=0;")) == 1,
            "fresh-gap measurement starts at the empty discovery frontier");
        MeasureMillion(store, pair, writer, "fresh-gap");
    }

    private static void MeasureMillion(SqliteStateStore store, PairDef pair, SqliteConnection writer, string variant)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var frozen = store.OpenPlanningProtection(pair);
        watch.Stop();
        var copyBytes = frozen.TempBytesForTest;
        using var stream = frozen.Stream();
        LocalChecks.Require(stream.SourcePasses == 3 && frozen.Read(["Parent/file-0999999"])[0].Covered,
            "million held rows are copied and indexed: " + variant);
        Console.WriteLine($"protection-million: variant={variant}; held=1000000; copy-ms={watch.ElapsedMilliseconds}; temp-copy-bytes={copyBytes}; temp-with-stream-bytes={frozen.TempBytesForTest}");
        using var command = writer.CreateCommand();
        ProtectionCheckpointChecks.Checkpoint(command);
    }
}
