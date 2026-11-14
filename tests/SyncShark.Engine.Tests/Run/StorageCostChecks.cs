using System.Runtime.CompilerServices;
using System.Text.Json;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Section 9.4 current storage shapes at two sizes (the larger four times the smaller): per-statement VM
/// steps, path-key callbacks, pair/JSON source occurrences, rows changed and preparation through
/// <see cref="CostProbe"/>; top-level store commands, commits, reader connections, rows and process allocations through
/// <see cref="StoreCounters"/> and <see cref="GC.GetTotalAllocatedBytes(bool)"/>. It is also the shared MEASURE packet
/// owner for the page-retry, record-drain, one-pass upgrade and adopted StoragePageSize operands.</summary>
internal static class StorageCostChecks
{
    private const int Small = 200;
    private const int Large = Small * 4;
    // The approved descendant workloads; the second is four times the first.
    private const int SmallFiles = 5_000;
    private const int LargeFiles = SmallFiles * 4;
    // Fixed involved retry rows; only the unrelated pair inventory grows between the two sizes.
    private const int FixedRetryPairs = 250;
    private const int FixedIssues = 3;
    // The coordinator-adopted StoragePageSize (engine-settings.json owns the value; this is the expected operand).
    private const int AdoptedPageSize = 100;
    // Baseline 16b21509 selected the generated dropbox_key column in slot 2; ordinal 2 now holds dropbox_key_delta.
    private const string BaselineSyncedColumns = "pathkey(local_path), local_path, dropbox_key, dropbox_path, is_folder, local_id, local_size, local_mtime, dropbox_id, dropbox_hash";
    private const string BaselineByLocalKey = "SELECT " + BaselineSyncedColumns + " FROM synced_entry WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND pathkey(local_path) = $k;";
    private const string BaselineByLocalId = "SELECT " + BaselineSyncedColumns + " FROM synced_entry INDEXED BY synced_local_id WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND local_id = $id;";
    private const string BaselineByDropboxKey = "SELECT " + BaselineSyncedColumns + " FROM synced_entry WHERE rowid IN (SELECT rowid FROM synced_entry INDEXED BY synced_dropbox_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND dropbox_key_delta = $k UNION ALL SELECT rowid FROM synced_entry INDEXED BY synced_local_key WHERE pair_id = (SELECT id FROM pair WHERE pair_id = $p) AND pathkey(local_path) = $k AND dropbox_key_delta IS NULL);";
    // Fixture setup only: retry rows carry the same deadline so continuation exercises the ordinal tie-break.
    private const string SeedRetryPairsSql =
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < $retries) " +
        "INSERT INTO pair(pair_id, def_json, execution_generation, retry_attempts, retry_after_ticks) " +
        "SELECT 'retry-' || i, '{}', 0, 1, 1 FROM n;" +
        "INSERT INTO source_state(pair_id, side, source_epoch, provenance_json, cursor, repair_reason, repair_revision) " +
        "SELECT id, 0, 0, NULL, NULL, 'Bootstrap', 0 FROM pair WHERE pair_id LIKE 'retry-%';" +
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < $unrelated) " +
        "INSERT INTO pair(pair_id, def_json) SELECT 'other-' || i, '{}' FROM n;";
    private static readonly RowContext Context = new(false, false, false, null, false, false, true, false);
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "engine-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Console.WriteLine("section 9.4 current storage shapes");
            Header();
            StatementCosts(folder);
            ClosureProbes(folder);
            ScanBatches(folder);
            CommitShapes(folder);
            RebaseCosts(folder);
            ReaderPages(folder);
            SyncedReads(folder);
            UpgradeAccounting(folder);
            RecordDrain(folder);
            PageRetryCosts(folder);
            StoragePaging(folder);
            FixedInvolvedCosts(folder);
            TwoSizes(folder);
            EvidenceLedger();
            ScanFootprint(folder);
            Storage.DeletedHistoryChecks.RunRestoreFootprint();
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Check.Require(!Directory.Exists(folder), "the storage cost temporary folder is removed after cleanup: " + folder);
    }

    // One execution of each statement the current engine runs, so VM steps, callbacks and pair source occurrences are
    // visible per statement, not only aggregated per phase.
    private static void StatementCosts(string folder)
    {
        using var probe = NewProbe(Path.Combine(folder, "statement-probe.db"));
        probe.AttachScan(Path.Combine(folder, "statement-probe.scan"));
        var index = PathNames.Key(ItemPath(0));
        Statement("DeleteSyncedEitherKey", probe.Execute(StateSchema.DeleteSyncedEitherKey, ("$p", "cost"),
            ("$localKey", index), ("$dropboxKey", index)));
        Statement("InsertSynced", probe.Execute(StateSchema.InsertSynced, InsertArguments(0)));
        Statement("ClashWithInherited", probe.ExecuteFirst(StateSchema.ClashWithInherited, ("$p", "cost"),
            ("$localKey", index), ("$dropboxKey", index)));
        Statement("ClashWithOverride", probe.ExecuteFirst(StateSchema.ClashWithOverride, ("$p", "cost"),
            ("$localKey", index), ("$dropboxKey", index)));
        var subject = JsonSerializer.Serialize(new IssueSubject([], null));
        Statement("SelectIssueByPathKind", probe.ExecuteFirst(StateSchema.SelectIssueByPathKind, ("$p", "cost"),
            ("$path", ItemPath(0)), ("$kind", (long)IssueKind.Failure)));
        Statement("InsertIssue", probe.Execute(StateSchema.InsertIssue, ("$p", "cost"), ("$path", ItemPath(0)),
            ("$kind", (long)IssueKind.Failure), ("$cause", TextKey.FailChanged.ToString()), ("$subject", subject),
            ("$at", 1L)));
        Statement("ReplaceIssue", probe.Execute(StateSchema.ReplaceIssue, ("$p", "cost"), ("$path", ItemPath(0)),
            ("$kind", (long)IssueKind.Failure), ("$cause", TextKey.FailChanged.ToString()), ("$subject", subject),
            ("$at", 1L)));
        Statement("DeleteIssueByPathKind", probe.Execute(StateSchema.DeleteIssueByPathKind, ("$p", "cost"),
            ("$path", ItemPath(0)), ("$kind", (long)IssueKind.Failure)));
        Statement("SelectClosureKeys", probe.Execute(StateSchema.SelectClosureKeys,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamKinds, ClosureKindsJson)));
        Statement("ScanUpsert", probe.Execute(StateSchema.ScanUpsert, ScanArguments(0)));
        Statement("ScanDeleteSelf", probe.ExecuteFirst(StateSchema.ScanDeleteSelf, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), ("$p", "cost"), ("$side", 0L),
            ("$k", index)));
        Statement("ScanDeleteDescendants", probe.Execute(StateSchema.ScanDeleteDescendants, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), ("$p", "cost"),
            ("$side", 0L), ("$k", index)));
        Statement("NextScanKey", probe.ExecuteFirst(StateSchema.NextScanKey, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), ("$p", "cost"), ("$after", ""),
            ("$first", true)));
        Statement("ScanRows", probe.Execute(StateSchema.ScanRows, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), ("$p", "cost"), ("$k", index)));
    }

    private static void Statement(string name, StatementCost cost) => Console.WriteLine(
        "  " + ("statement " + name).PadRight(30) + 1.ToString().PadLeft(6) + cost.VmSteps.ToString().PadLeft(10) +
        cost.PathKeyCalls.ToString().PadLeft(9) + cost.PairSourceOccurrences.ToString().PadLeft(9) +
        cost.JsonSourceOccurrences.ToString().PadLeft(9) + cost.ChangedRows.ToString().PadLeft(9) +
        cost.ProbePrepares.ToString().PadLeft(11));

    private static readonly IssueKind[] ClosureKinds =
        [IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash, IssueKind.DriveChange];
    private static readonly string ClosureKindsJson = JsonSerializer.Serialize(ClosureKinds);

    private static void ClosureProbes(string folder)
    {
        var costs = new List<StatementCost>();
        foreach (var count in new[] { 0, ClosureKinds.Length, ClosureKinds.Length * 4 })
        {
            using var probe = NewProbe(Path.Combine(folder, "closure-probe-" + count + ".db"));
            probe.Begin();
            for (var index = 0; index < count; index++)
                probe.Execute(StateSchema.InsertIssue, (StateSchema.ParamPair, "cost"),
                    (StateSchema.ParamPath, ItemPath(index)),
                    (StateSchema.ParamKind, (int)ClosureKinds[index % ClosureKinds.Length]),
                    (StateSchema.ParamCause, TextKey.FailChanged.ToString()),
                    (StateSchema.ParamSubject, "{"), (StateSchema.ParamAt, 1L));
            probe.Commit();
            // Malformed subjects must never be decoded by the narrow membership load.
            var cost = probe.Execute(StateSchema.SelectClosureKeys, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamKinds, ClosureKindsJson));
            costs.Add(cost);
            Row("closure membership " + count, count, cost, 0, 0, 0, 0, 0);
            Check.Require(cost.PathKeyCalls == 0 && cost.ChangedRows == 0 && cost.ProbePrepares == 1,
                "closure membership reads exact paths without key callbacks or writes");
            Check.Require(cost.PairSourceOccurrences == 1 && cost.JsonSourceOccurrences == 1,
                "one membership load has one pair source and one kind-list JSON source, not per-path JSON");
        }
        Check.Require(costs[1].VmSteps > costs[0].VmSteps && costs[2].VmSteps > costs[1].VmSteps &&
            costs[2].VmSteps - costs[0].VmSteps <= 4 * (costs[1].VmSteps - costs[0].VmSteps),
            "the fully stepped membership query grows with the pair's issue rows (H, 4H)");

        using var rig = NewRig(Path.Combine(folder, "closure-real.db"));
        foreach (var count in new[] { 0, 1, ClosureKinds.Length })
        {
            var closures = Closures(count);
            var measured = rig.Measure(() =>
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Close = closures }));
            Row("closure no-match " + count, count, default, measured.Allocated, measured.Commands,
                measured.CommitCalls, measured.Reads, measured.Rows);
        }
        Console.WriteLine("  closure no-match SQL/JSON execution and transaction counts unavailable; top-level calls only");
    }

    // A first Local scan batch of one item versus a large batch, one transaction each.
    private static void ScanBatches(string folder)
    {
        foreach (var count in new[] { 1, Large })
        {
            var path = Path.Combine(folder, "scan-" + count + ".db");
            using var rig = NewRig(path);
            rig.Store.BeginScan(rig.Pair.PairId, 0, includeDeleted: false);
            var measured = rig.Measure(() =>
            {
                var sink = rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
                for (var index = 0; index < count; index++) sink.AddAsync(Listed(index), default).GetAwaiter().GetResult();
                sink.CompleteAsync().GetAwaiter().GetResult();
            });
            using var probe = NewProbe(path + ".probe");
            probe.AttachScan(path + ".probe.scan");
            probe.Begin();
            var cost = new StatementCost();
            for (var index = 0; index < count; index++) cost += probe.Execute(StateSchema.ScanUpsert, ScanArguments(index));
            probe.Commit();
            Row("scan batch " + count, count, cost, measured.Allocated, measured.Commands, 1, 0, 0);
            Check.Require(cost.ChangedRows == count, "a fresh scan upsert changes exactly one row per item");
            Check.Require(measured.Commands == 1, "one scan batch after BeginScan is one OpenScan command");
        }
        // A completed empty discovery answers one existence query, not one membership read per pair item.
        using (var rig = NewRig(Path.Combine(folder, "discovery-empty.db")))
        {
            rig.Store.BeginScan(rig.Pair.PairId, 0, includeDeleted: true);
            var sink = rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
            sink.CompleteAsync().GetAwaiter().GetResult();
            var measured = rig.Measure(() =>
                Check.Require(!rig.Store.HasDeletedNames(rig.Pair.PairId), "an empty discovery holds no deleted name"));
            Row("empty discovery", 0, default, measured.Allocated, measured.Commands, measured.CommitCalls, 0, 0);
            Check.Require(measured.Commands == 1,
                "a completed empty discovery is one existence query, not per-item membership or history work");
        }
    }

    private static void ScanFootprint(string folder)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var small = ScanFootprintAt(Path.Combine(folder, "footprint-small"), SmallFiles);
        var large = ScanFootprintAt(Path.Combine(folder, "footprint-large"), LargeFiles);
        // At four times the entries, linear per-entry growth is 1 and quadratic is 4; 2 separates them.
        var linear = large.LogicalBytes * small.Entries <= 2 * small.LogicalBytes * large.Entries;
        Console.WriteLine($"SCAN-FOOTPRINT linear={linear} elapsed_total_ms={timer.ElapsedMilliseconds}");
        Check.Require(linear, "scan footprint: logical bytes per entry stay linear");
    }

    private static (long Entries, long LogicalBytes) ScanFootprintAt(string folder, int count)
    {
        Directory.CreateDirectory(folder);
        try
        {
            var entries = ScanFootprintEntries(count);
            using var rig = NewRig(Path.Combine(folder, "state.db"));
            rig.Store.BeginScan(rig.Pair.PairId, 0, includeDeleted: false);
            var local = rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
            var dropbox = rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows);
            foreach (var entry in entries)
                local.AddAsync(new Listed(entry.Path, entry), default).GetAwaiter().GetResult();
            var shuffled = Enumerable.Range(0, count).ToArray();
            var random = new Random(0); // Fixed seed makes the undocumented provider order reproducibly adverse.
            for (var index = shuffled.Length - 1; index > 0; index--)
            {
                var other = random.Next(index + 1);
                (shuffled[index], shuffled[other]) = (shuffled[other], shuffled[index]);
            }
            foreach (var index in shuffled)
            {
                var entry = entries[index] with
                {
                    Id = "id:" + index.ToString("D22", System.Globalization.CultureInfo.InvariantCulture),
                    Revision = entries[index].IsFolder ? "" : index.ToString("x25", System.Globalization.CultureInfo.InvariantCulture),
                    ContentHash = entries[index].IsFolder ? "" : index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)
                };
                dropbox.AddAsync(new Listed(entry.Path, entry), default).GetAwaiter().GetResult();
            }
            Task.WhenAll(local.CompleteAsync(), dropbox.CompleteAsync()).GetAwaiter().GetResult();
            // The writer stays open; closing this read-only connection cannot checkpoint as the last connection.
            var scanFile = Directory.EnumerateFiles(folder, "state.db" + StateSchema.ScanFileSuffix + "*" + StateSchema.DatabaseExtension).Single();
            var walFileBytes = new FileInfo(scanFile + StateSchema.WalSuffix).Length;
            var dbFileBytes = new FileInfo(scanFile).Length;
            using var connection = StateStore.Connect(scanFile, SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            var pages = (long)command.ExecuteScalar()!;
            command.CommandText = "PRAGMA page_size;";
            var logicalBytes = pages * (long)command.ExecuteScalar()!;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"SCAN-FOOTPRINT entries={2L * count} mean_path={entries.Average(entry => entry.Path.Length):F2} " +
                $"scan_wal_file_bytes={walFileBytes} scan_db_file_bytes={dbFileBytes} footprint=physical " +
                $"scan_logical_bytes={logicalBytes} logical_bytes_per_entry={(double)logicalBytes / (2 * count):F2} " +
                $"wal_file_bytes_per_entry={(double)walFileBytes / (2 * count):F2}"));
            command.CommandText = "SELECT 1 FROM pragma_module_list WHERE name='dbstat';";
            if (command.ExecuteScalar() is null) Console.WriteLine("SCAN-FOOTPRINT dbstat=absent");
            else
            {
                command.CommandText = "SELECT name,count(*),sum(unused) FROM dbstat GROUP BY name ORDER BY name;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    Console.WriteLine($"SCAN-FOOTPRINT btree={reader.GetString(0)} pages={reader.GetInt64(1)} unused_bytes={reader.GetInt64(2)}");
            }
            return (2L * count, logicalBytes);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static Entry[] ScanFootprintEntries(int count)
    {
        // R01 folder share and R02 mean key length; pad file names to keep the whole tree's mean at 132 ASCII bytes.
        var folders = count * 224 / 1000;
        var entries = new Entry[count];
        for (var index = 0; index < count; index++)
        {
            var isFolder = index < folders;
            var parent = index == 0 ? "" : entries[isFolder ? (index - 1) / 4 : (index - folders) % folders].Path;
            var name = (isFolder ? "d" : "f") + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            var path = PathNames.Child(parent, name);
            entries[index] = new Entry(path, isFolder, isFolder ? 0 : 1_048_576 + index,
                isFolder ? default : BaseTime.AddSeconds(index),
                ((1L << 48) + index).ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", null);
        }
        var padding = count * 132 - entries.Sum(entry => entry.Path.Length);
        var files = count - folders;
        for (var index = 0; index < files; index++)
        {
            var entry = entries[folders + index];
            entries[folders + index] = entry with { Path = entry.Path + new string('x', padding / files + (index < padding % files ? 1 : 0)) };
        }
        var children = entries.GroupBy(entry => PathNames.Parent(entry.Path))
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => PathNames.Key(entry.Path), StringComparer.Ordinal).ToArray());
        var ordered = new List<Entry>(count);
        var stack = new Stack<string>();
        stack.Push("");
        while (stack.TryPop(out var parent))
        {
            if (!children.TryGetValue(parent, out var items)) continue;
            foreach (var entry in items)
            {
                ordered.Add(entry);
                if (entry.IsFolder) stack.Push(entry.Path);
            }
        }
        Check.Require(ordered.Count == count && ordered.Sum(entry => entry.Path.Length) == count * 132,
            "scan footprint uses every generated entry at the representative mean path length");
        return ordered.ToArray();
    }

    // One first-run record and a raise-only commit.
    private static void CommitShapes(string folder)
    {
        using (var rig = NewRig(Path.Combine(folder, "record.db")))
        {
            var item = new Item(0);
            var measured = rig.Measure(() => rig.Store.Commit(rig.Pair.PairId, Record(item.Saved)));
            using var probe = NewProbe(Path.Combine(folder, "record-probe.db"));
            probe.Begin();
            var cost = RecordCost(probe, item);
            probe.Commit();
            Row("record commit (one item)", 1, cost, measured.Allocated, measured.Commands, measured.CommitCalls, 0, 0);
            Check.Require(cost.ChangedRows == 1, "one first-run record changes exactly one row");
        }
        using (var rig = NewRig(Path.Combine(folder, "raise.db")))
        {
            var measured = rig.Measure(() => rig.Store.Commit(rig.Pair.PairId,
                StateChange.Empty with { Raise = [Raise(ItemPath(0))] }));
            using var probe = NewProbe(Path.Combine(folder, "raise-probe.db"));
            probe.Begin();
            var subject = JsonSerializer.Serialize(new IssueSubject([], null));
            var cost = probe.ExecuteFirst(StateSchema.SelectIssueByPathKind, ("$p", "cost"), ("$path", ItemPath(0)),
                ("$kind", (long)IssueKind.Failure));
            cost += probe.Execute(StateSchema.InsertIssue, ("$p", "cost"), ("$path", ItemPath(0)),
                ("$kind", (long)IssueKind.Failure), ("$cause", TextKey.FailChanged.ToString()),
                ("$subject", subject), ("$at", 1L));
            probe.Commit();
            Row("raise-only commit", 1, cost, measured.Allocated, measured.Commands, measured.CommitCalls, 0, 0);
            Check.Require(cost.ChangedRows == 1, "one raise inserts one issue row");
        }
    }

    private static void RebaseCosts(string folder)
    {
        foreach (var count in new[] { SmallFiles, LargeFiles })
        {
            var baseline = RebaseProbe(folder, "baseline-" + count, count, fused: false);
            var current = RebaseProbe(folder, "fused-" + count, count, fused: true);
            Row("rebase baseline " + count, count, baseline, 0, 0, 0, 0, 0);
            Row("rebase fused " + count, count, current, 0, 0, 0, 0, 0);
            Check.Require(baseline.ChangedRows == 2L * count && current.ChangedRows == count,
                "a both-side rebase updates every descendant once instead of twice");
            Check.Require(current.PathKeyCalls <= baseline.PathKeyCalls,
                "fusing a both-side rebase adds no path-key callbacks");
        }
    }

    private static StatementCost RebaseProbe(string folder, string name, int count, bool fused)
    {
        using var probe = NewProbe(Path.Combine(folder, "rebase-" + name + ".db"));
        probe.Begin();
        for (var index = 0; index < count; index++)
            probe.Execute(StateSchema.InsertSynced, InsertArguments(index));
        probe.Commit();
        probe.Begin();
        var cost = fused
            ? probe.Execute(StateSchema.RebaseDescendantsBoth,
                (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamOldLocalKey, PathNames.Key("Parent")),
                (StateSchema.ParamOldLocalPath, "Parent"),
                (StateSchema.ParamNewLocalKey, PathNames.Key("Moved")),
                (StateSchema.ParamNewLocalPath, "Moved"),
                (StateSchema.ParamOldDropboxKey, PathNames.DropboxKey("Parent")),
                (StateSchema.ParamOldDropboxPath, "Parent"),
                (StateSchema.ParamNewDropboxKey, PathNames.DropboxKey("Moved")),
                (StateSchema.ParamNewDropboxPath, "Moved"))
            : probe.Execute(StateSchema.RebaseDescendantsLocal, RebaseArguments("Parent", "Moved")) +
                probe.Execute(StateSchema.RebaseDescendantsDropbox, RebaseArguments("Parent", "Moved"));
        probe.Commit();
        return cost;
    }

    private static (string Name, object? Value)[] RebaseArguments(string before, string after) =>
    [
        (StateSchema.ParamPair, "cost"), (StateSchema.ParamOldKey, PathNames.Key(before)),
        (StateSchema.ParamNewKey, PathNames.Key(after)), (StateSchema.ParamOldPath, before),
        (StateSchema.ParamNewPath, after),
    ];

    // Reader pages with linked, free and skipped keys, one page per key.
    private static void ReaderPages(string folder)
    {
        foreach (var count in new[] { Small, Large })
        {
            var path = Path.Combine(folder, "reader-" + count + ".db");
            using var rig = NewRig(path);
            rig.Store.BeginScan(rig.Pair.PairId, 0, includeDeleted: false);
            var sink = rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
            for (var index = 0; index < count; index++) sink.AddAsync(Listed(index), default).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Record = Enumerable.Range(0, count).Select(index => new Item(index).Saved).ToArray(),
            });
            var measured = rig.Measure(() =>
            {
                var rows = 0;
                foreach (var row in rig.Store.ScanRows(rig.Pair.PairId, _ => Context))
                {
                    rows++;
                    _ = row.Key;
                }
                Check.Require(rows == count, "the reader yields one group per key: " + rows + " of " + count);
            });

            using var probe = NewProbe(path + ".probe");
            probe.AttachScan(path + ".probe.scan");
            probe.Begin();
            for (var index = 0; index < count; index++) probe.Execute(StateSchema.ScanUpsert, ScanArguments(index));
            for (var index = 0; index < count; index++) probe.Execute(StateSchema.InsertSynced, InsertArguments(index));
            probe.Commit();
            var cost = ReaderCost(probe, Enumerable.Range(0, count).Select(index => new Item(index).Key));
            Row("reader pages " + count, count, cost, measured.Allocated, measured.Commands, measured.CommitCalls,
                measured.Reads, measured.Rows);
            Check.Require(measured.Reads == 1, "one ScanRows enumeration opens one read connection");
            Check.Require(measured.Rows == count, "the counted reader yields every group");
        }
    }

    // Section 9.6 shapes at two sizes: unchanged, 1%-changed and first run, per item, plus retained bytes.
    private static void TwoSizes(string folder)
    {
        var small = Sized(folder, "small", Small);
        var large = Sized(folder, "large", Large);
        Check.Require(large.Items >= small.Items * 4, "the larger size has at least four times the items");
        foreach (var (name, left, right, leftBytes, rightBytes) in new[]
        {
            ("unchanged", small.Unchanged, large.Unchanged, small.UnchangedBytes, large.UnchangedBytes),
            ("1%-changed", small.Changed, large.Changed, small.ChangedBytes, large.ChangedBytes),
            ("first run", small.First, large.First, small.FirstBytes, large.FirstBytes),
        })
        {
            Check.Require(PerItem(right.Sql.VmSteps, large.Items) <= PerItem(left.Sql.VmSteps, small.Items) * 1.5 &&
                PerItem(right.Sql.PathKeyCalls, large.Items) <= PerItem(left.Sql.PathKeyCalls, small.Items) * 1.5 &&
                PerItem(right.Sql.PairSourceOccurrences, large.Items) <= PerItem(left.Sql.PairSourceOccurrences, small.Items) * 1.5 &&
                PerItem(right.Allocated, large.Items) <= PerItem(left.Allocated, small.Items) * 1.5,
                name + ": per-item work must not grow with the item count");
            Row(name + " " + small.Items, small.Items, left.Sql, left.Allocated, left.Commands, left.CommitCalls, 0, 0);
            Row(name + " " + large.Items, large.Items, right.Sql, right.Allocated, right.Commands, right.CommitCalls, 0, 0);
            // The inherited StorageCostChecks 1.5 factor, kept for its own packet only.
            Check.Require(PerItem(rightBytes, large.Items) <= PerItem(leftBytes, small.Items) * 1.5,
                name + ": retained bytes per item must not grow with the item count: " +
                PerItem(rightBytes, large.Items) + " vs " + PerItem(leftBytes, small.Items));
            Console.WriteLine("  retained bytes " + name + " " + small.Items + "=" + leftBytes + " B, " +
                large.Items + "=" + rightBytes + " B; " + PerItem(rightBytes, large.Items).ToString("F1") + " B/item at the larger size");
        }
    }

    // A whole size's three shapes, each measured on its own fresh durable database and matching probe database.
    private static SizeCosts Sized(string folder, string name, int count)
    {
        var items = Enumerable.Range(0, count).Select(index => new Item(index)).ToArray();
        var first = MeasureRecords(folder, name + "-first", items);
        var changedCount = Math.Max(1, count / 100);
        var changed = MeasureRecords(folder, name + "-changed", items[..changedCount], seed: items);
        var unchanged = MeasureReader(folder, name + "-unchanged", items);
        return new SizeCosts(count, first, changed, unchanged,
            RetainedBytes(Path.Combine(folder, name + "-first.db")),
            RetainedBytes(Path.Combine(folder, name + "-changed.db")),
            RetainedBytes(Path.Combine(folder, name + "-unchanged.db")));
    }

    private static Measure MeasureRecords(string folder, string name, Item[] items, Item[]? seed = null)
    {
        using (var rig = NewRig(Path.Combine(folder, name + ".db")))
        {
            if (seed is not null)
                rig.Store.Commit(rig.Pair.PairId,
                    StateChange.Empty with { Record = seed.Select(item => item.Saved).ToArray() });
            var records = items.Select(item => item.Saved).ToArray();
            var measured = rig.Measure(() => rig.Store.Commit(rig.Pair.PairId,
                StateChange.Empty with { Record = records }));
            using var probe = NewProbe(Path.Combine(folder, name + "-probe.db"));
            probe.Begin();
            if (seed is not null)
                foreach (var item in seed) probe.Execute(StateSchema.InsertSynced, item.InsertArguments);
            var cost = new StatementCost();
            foreach (var item in items) cost += RecordCost(probe, item);
            probe.Commit();
            return new Measure(cost, measured.Allocated, measured.Commands, measured.CommitCalls);
        }
    }

    private static Measure MeasureReader(string folder, string name, Item[] items)
    {
        var path = Path.Combine(folder, name + ".db");
        using (var rig = NewRig(path))
        {
            rig.Store.BeginScan(rig.Pair.PairId, 0, includeDeleted: false);
            var sink = rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows);
            foreach (var item in items) sink.AddAsync(new Listed(item.Path, item.LocalEntry), default).GetAwaiter().GetResult();
            sink.CompleteAsync().GetAwaiter().GetResult();
            var measured = rig.Measure(() =>
            {
                foreach (var row in rig.Store.ScanRows(rig.Pair.PairId, _ => Context)) _ = row.Key;
            });
            using var probe = NewProbe(path + ".probe");
            probe.AttachScan(path + ".probe.scan");
            probe.Begin();
            foreach (var item in items) probe.Execute(StateSchema.ScanUpsert, item.ScanArguments(0));
            probe.Commit();
            var cost = ReaderCost(probe, items.Select(item => item.Key));
            return new Measure(cost, measured.Allocated, measured.Commands, measured.CommitCalls);
        }
    }

    private static StatementCost ReaderCost(CostProbe probe, IEnumerable<string> keys)
    {
        var cost = new StatementCost();
        var after = "";
        var first = true;
        foreach (var key in keys)
        {
            probe.Begin();
            cost += probe.ExecuteFirst(StateSchema.NextScanKey, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamAfter, after), (StateSchema.ParamFirst, first));
            cost += probe.Execute(StateSchema.ScanRows, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamTargetKey, key));
            probe.Commit();
            after = key;
            first = false;
        }
        probe.Begin();
        cost += probe.ExecuteFirst(StateSchema.NextScanKey, (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamPair, "cost"),
            (StateSchema.ParamAfter, after), (StateSchema.ParamFirst, first));
        probe.Commit();
        return cost;
    }

    private static StatementCost RecordCost(CostProbe probe, Item item)
    {
        var cost = probe.Execute(StateSchema.DeleteSyncedEitherKey, ("$p", "cost"), ("$localKey", item.Key),
            ("$dropboxKey", item.DropboxKey));
        cost += probe.Execute(StateSchema.InsertSynced, item.InsertArguments);
        cost += probe.ExecuteFirst(StateSchema.ClashWithOverride, ("$p", "cost"), ("$localKey", item.Key),
            ("$dropboxKey", item.DropboxKey));
        return cost;
    }

    private static (string Path, IReadOnlyList<IssueKind> Kinds)[] Closures(int count) =>
        Enumerable.Range(0, count).Select(index =>
            ("Missing/" + index, (IReadOnlyList<IssueKind>)new[] { IssueKind.Failure, IssueKind.Tie })).ToArray();

    /// <summary>One advertised item: the only source of the real store values and the probe bindings.</summary>
    private sealed record Item(int Index)
    {
        public string Path => "Parent/" + Index.ToString("D4") + "/File" + Index.ToString("D8") + ".txt";
        public string Key => PathNames.Key(Path);
        public string DropboxKey => PathNames.DropboxKey(Path);
        public long Ticks => BaseTime.AddSeconds(Index).UtcTicks;
        public string? Skip => Index % 50 == 0 ? "skipped" : null;
        public Entry LocalEntry => new(Path, false, 1, BaseTime.AddSeconds(Index), "L" + Index, "", "", Skip);

        public SyncedEntry Saved => new(Key, Path, DropboxKey, Path, false, "L" + Index, 1, Ticks,
            "D" + Index, "");

        public (string Name, object? Value)[] ScanArguments(int side) =>
        [
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamSide, (long)side),
            (StateSchema.ParamJoinKey, side == 0 ? Key : DropboxKey), (StateSchema.ParamOwnKey, Key),
            (StateSchema.ParamPath, Path),
            (StateSchema.ParamIsFolder, false), (StateSchema.ParamSize, 1L),
            (StateSchema.ParamMtime, Ticks), (StateSchema.ParamId, "L" + Index),
            (StateSchema.ParamRev, ""), (StateSchema.ParamHash, Array.Empty<byte>()),
            (StateSchema.ParamSkip, Skip),
        ];

        public (string Name, object? Value)[] InsertArguments =>
        [
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamLocalKey, Key),
            (StateSchema.ParamLocalPath, Path),
            (StateSchema.ParamDropboxKey, DropboxKey), (StateSchema.ParamDropboxPath, Path),
            (StateSchema.ParamIsFolder, false), (StateSchema.ParamLocalId, "L" + Index),
            (StateSchema.ParamLocalSize, 1L), (StateSchema.ParamLocalMtime, Ticks),
            (StateSchema.ParamDropboxId, "D" + Index), (StateSchema.ParamDropboxHash, Array.Empty<byte>()),
            (StateSchema.ParamPublishedJournal, null), (StateSchema.ParamPublishedUsn, null),
        ];
    }

    private static Listed Listed(int index) => new(new Item(index).Path, new Item(index).LocalEntry);
    private static string ItemPath(int index) => new Item(index).Path;
    private static (string Name, object? Value)[] ScanArguments(int index) => new Item(index).ScanArguments(0);
    private static (string Name, object? Value)[] InsertArguments(int index) => new Item(index).InsertArguments;

    private static (IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject) Raise(string path) =>
        (IssueKind.Failure, TextKey.FailChanged, path, new IssueSubject([], null));

    private static StateChange Record(SyncedEntry entry) => StateChange.Empty with { Record = [entry] };


    // The SyncedColumns projection: the baseline selected the generated dropbox_key column, while the engine reads the
    // nullable dropbox_key_delta and rebuilds the effective key in StateEncoding.ReadSynced. Both projections run over
    // the same inherited-key row through one probe (path-key calls) and one reader harness (allocation per read).
    private static void SyncedReads(string folder)
    {
        var item = new Item(0);
        var path = Path.Combine(folder, "synced-reads.db");
        using (var rig = NewRig(path))
            rig.Store.Commit(rig.Pair.PairId, Record(item.Saved));
        using var probe = NewProbe(Path.Combine(folder, "synced-reads-probe.db"));
        probe.Execute(StateSchema.InsertSynced, item.InsertArguments);

        var nowKey = probe.ExecuteFirst(StateSchema.SelectSyncedByLocalKey,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamTargetKey, item.Key));
        var baselineKey = probe.ExecuteFirst(BaselineByLocalKey,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamTargetKey, item.Key));
        var nowId = probe.ExecuteFirst(StateSchema.SelectSyncedByLocalId,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamId, item.Saved.LocalId));
        var baselineId = probe.ExecuteFirst(BaselineByLocalId,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamId, item.Saved.LocalId));
        var nowDropbox = probe.ExecuteFirst(StateSchema.SelectSyncedByDropboxKey,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamTargetKey, item.Key));
        var baselineDropbox = probe.ExecuteFirst(BaselineByDropboxKey,
            (StateSchema.ParamPair, "cost"), (StateSchema.ParamTargetKey, item.Key));

        Row("synced read by key, baseline", 1, baselineKey, 0, 0, 0, 0, 0);
        Row("synced read by key, now", 1, nowKey, 0, 0, 0, 0, 0);
        Row("synced read by id, baseline", 1, baselineId, 0, 0, 0, 0, 0);
        Row("synced read by id, now", 1, nowId, 0, 0, 0, 0, 0);
        Row("synced read by dropbox, baseline", 1, baselineDropbox, 0, 0, 0, 0, 0);
        Row("synced read by dropbox, now", 1, nowDropbox, 0, 0, 0, 0, 0);
        Check.Require(nowKey.PathKeyCalls <= baselineKey.PathKeyCalls && nowId.PathKeyCalls <= baselineId.PathKeyCalls &&
            nowDropbox.PathKeyCalls <= baselineDropbox.PathKeyCalls,
            "the delta projection adds no path-key call: by key " + nowKey.PathKeyCalls + "/" + baselineKey.PathKeyCalls +
            ", by id " + nowId.PathKeyCalls + "/" + baselineId.PathKeyCalls + ", by dropbox " +
            nowDropbox.PathKeyCalls + "/" + baselineDropbox.PathKeyCalls);

        // Pooling keeps the file locked after Dispose, so the run's temporary folder cannot be removed.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        StateStore.OpenConnection(connection);
        var (nowKeyCommand, nowKeyRead) = Reader(connection, StateSchema.SelectSyncedByLocalKey,
            StateSchema.ParamTargetKey, item.Key, static reader => StateEncoding.ReadSynced(reader,
                reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath));
        var (baselineKeyCommand, baselineKeyRead) = Reader(connection, BaselineByLocalKey,
            StateSchema.ParamTargetKey, item.Key, BaselineSynced);
        var (nowIdCommand, nowIdRead) = Reader(connection, StateSchema.SelectSyncedByLocalId, StateSchema.ParamId,
            item.Saved.LocalId, static reader => StateEncoding.ReadSynced(reader,
                reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath));
        var (baselineIdCommand, baselineIdRead) = Reader(connection, BaselineByLocalId, StateSchema.ParamId,
            item.Saved.LocalId, BaselineSynced);
        Check.Require(nowKeyRead() is { } rebuiltKey && rebuiltKey.DropboxKey == item.Key,
            "the inherited Dropbox key rebuilds from the local key");
        Check.Require(baselineKeyRead() is { } selectedKey && selectedKey.DropboxKey == item.Key,
            "the baseline projection reads the same inherited key");
        Check.Require(nowIdRead() is { } rebuiltId && rebuiltId.DropboxKey == item.Key &&
            baselineIdRead() is { } selectedId && selectedId.DropboxKey == item.Key,
            "the id read rebuilds the same inherited key from both projections");

        var nowKeyBytes = AllocProbe.Measure(index => SinkSynced(nowKeyRead()), 5_000, 1_000) / 5_000;
        var baselineKeyBytes = AllocProbe.Measure(index => SinkSynced(baselineKeyRead()), 5_000, 1_000) / 5_000;
        var nowIdBytes = AllocProbe.Measure(index => SinkSynced(nowIdRead()), 5_000, 1_000) / 5_000;
        var baselineIdBytes = AllocProbe.Measure(index => SinkSynced(baselineIdRead()), 5_000, 1_000) / 5_000;
        Console.WriteLine("  synced read allocation per read: by key baseline=" + baselineKeyBytes + " B now=" +
            nowKeyBytes + " B, by id baseline=" + baselineIdBytes + " B now=" + nowIdBytes + " B");
        Check.Require(nowKeyBytes <= baselineKeyBytes && nowIdBytes <= baselineIdBytes,
            "the delta projection adds no allocation per read: by key " + nowKeyBytes + "/" + baselineKeyBytes +
            " B, by id " + nowIdBytes + "/" + baselineIdBytes + " B");
        nowKeyCommand.Dispose();
        baselineKeyCommand.Dispose();
        nowIdCommand.Dispose();
        baselineIdCommand.Dispose();
    }

    // Baseline 16b21509 decoded slot 2 as the generated dropbox_key column.
    private static SyncedEntry BaselineSynced(SqliteDataReader reader) => new(
        reader.GetString(StateSchema.SyncedLocalKey), reader.GetString(StateSchema.SyncedLocalPath),
        reader.GetString(2), reader.GetString(StateSchema.SyncedDropboxPath),
        reader.GetInt64(StateSchema.SyncedIsFolder) != 0, reader.GetString(StateSchema.SyncedLocalId),
        reader.GetInt64(StateSchema.SyncedLocalSize), reader.GetInt64(StateSchema.SyncedLocalModified),
        reader.GetString(StateSchema.SyncedDropboxId), StateEncoding.ReadHash(reader, StateSchema.SyncedDropboxHash));

    private static (SqliteCommand Command, Func<SyncedEntry?> Read) Reader(SqliteConnection connection, string sql,
        string parameter, string value, Func<SqliteDataReader, SyncedEntry> decode)
    {
        var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue(StateSchema.ParamPair, "cost");
        command.Parameters.AddWithValue(parameter, value);
        return (command, () =>
        {
            using var reader = command.ExecuteReader();
            return reader.Read() ? decode(reader) : null;
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SinkSynced(SyncedEntry? value) => SyncedSink = value;

    private static SyncedEntry? SyncedSink;

    // V13-B42: one upgrade pass validates and converts only the saved mapped rows. A growing plain inventory raises the
    // validation work (one row pass) but never the registered pathkey work of the conversion.
    private static void UpgradeAccounting(string folder)
    {
        var mappedPath = "Parent/0000/Bad_Name.txt";
        var rawPath = "Parent/0000/Bad:Name.txt";
        foreach (var count in new[] { Small, Large })
        {
            using var probe = NewProbe(Path.Combine(folder, "upgrade-" + count + ".db"));
            probe.Begin();
            probe.Execute(StateSchema.InsertSynced, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamLocalKey, PathNames.Key(mappedPath)),
                (StateSchema.ParamLocalPath, mappedPath),
                (StateSchema.ParamDropboxKey, PathNames.Key(mappedPath)),
                (StateSchema.ParamDropboxPath, rawPath),
                (StateSchema.ParamIsFolder, false), (StateSchema.ParamLocalId, "Lmapped"),
                (StateSchema.ParamLocalSize, 1L), (StateSchema.ParamLocalMtime, 1L),
                (StateSchema.ParamDropboxId, "Dmapped"), (StateSchema.ParamDropboxHash, Array.Empty<byte>()),
                (StateSchema.ParamPublishedJournal, null), (StateSchema.ParamPublishedUsn, null));
            for (var index = 0; index < count; index++)
                probe.Execute(StateSchema.InsertSynced, InsertArguments(index));
            probe.Commit();

            var collisions = probe.ExecuteFirst(StateSchema.EffectiveDropboxKeyCollisions);
            var conversion = probe.Execute(StateSchema.ConvertMappedDropboxKeys);
            Row("upgrade validation " + count, count + 1, collisions, 0, 0, 0, 0, 0);
            Row("upgrade conversion " + count, count + 1, conversion, 0, 0, 0, 0, 0);
            Check.Require(collisions.ChangedRows == 0 && collisions.ProbePrepares == 1,
                "the effective-key guard is one read-only statement");
            Check.Require(collisions.PathKeyCalls >= count,
                "the one-pass guard evaluates every saved row's final key: " + collisions.PathKeyCalls + " of " + count);
            Check.Require(conversion.ChangedRows == 1 && conversion.ProbePrepares == 1,
                "one conversion pass changes only the mapped saved row: " + conversion.ChangedRows);
            Check.Require(conversion.PathKeyCalls <= 4,
                "conversion key callbacks stay bounded by mapped rows, not the plain inventory: " + conversion.PathKeyCalls);
        }
        Console.WriteLine("  upgrade: one validation plus one conversion statement; no runtime preserved-lookup fallback");
    }

    // CV-RECORD / BATCHING 4.7: acknowledged first-run Records drain through one record queue. The explicit
    // record-drain transaction count is not externally observable (MEASURE G1); member durability and outcome
    // distribution are. A bad member must fault only itself.
    private static void RecordDrain(string folder)
    {
        var drainCosts = new Dictionary<int, StatementCost>();
        foreach (var count in new[] { Small, Large })
        {
            var path = Path.Combine(folder, "drain-" + count + ".db");
            using (var rig = new Rig(path))
            {
                rig.Counted.Reset();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var tasks = Enumerable.Range(0, count)
                    .Select(index => Task.Run(() => rig.Store.CommitRecordAsync(rig.Pair.PairId,
                        Record(new Item(index).Saved), null)))
                    .ToArray();
                Task.WhenAll(tasks).GetAwaiter().GetResult();
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                Row("record drain " + count, count, default, allocated, rig.Counted.Commands,
                    rig.Counted.CommitCalls, 0, 0);
                Check.Require(rig.Counted.RecordCommittedMembers == count,
                    "every eligible first-run Record member is durably committed: " +
                    rig.Counted.RecordCommittedMembers + " of " + count);
                Check.Require(rig.Counted.RecordCommitFailures == 0, "no eligible Record member failed");
                Check.Require(rig.Counted.RecordCommitCalls == count, "one submission is recorded per Record call");
                Check.Require(tasks.All(task => task.IsCompletedSuccessfully), "every Record submission completed");
            }
            using (var reopened = StateStore.Open(path))
            {
                Check.Require(reopened.SyncedByKey("cost", Side.Local, new Item(0).Key) is not null &&
                    reopened.SyncedByKey("cost", Side.Local, new Item(count - 1).Key) is not null,
                    "committed Records survive reopen at both ends of the batch");
            }
            using (var probe = NewProbe(path + ".probe"))
            {
                probe.Begin();
                var cost = new StatementCost();
                for (var index = 0; index < count; index++) cost += RecordCost(probe, new Item(index));
                probe.Commit();
                drainCosts[count] = cost;
                Row("record drain statements " + count, count, cost, 0, 0, 0, 0, 0);
                Check.Require(cost.ChangedRows == count,
                    "each drained Record changes its own row once: " + cost.ChangedRows + " of " + count);
            }
        }
        Check.Require(PerItem(drainCosts[Large].VmSteps, Large) <= PerItem(drainCosts[Small].VmSteps, Small) * 1.5 &&
            PerItem(drainCosts[Large].PathKeyCalls, Large) <= PerItem(drainCosts[Small].PathKeyCalls, Small) * 1.5,
            "record-drain SQL/VM per item does not grow with the batch size: " +
            PerItem(drainCosts[Large].VmSteps, Large) + " vs " + PerItem(drainCosts[Small].VmSteps, Small));

        using (var rig = new Rig(Path.Combine(folder, "drain-reject.db")))
        {
            // rev3h G05: raising and folder Records are the synchronous entry, not rejections.
            var raising = StateChange.Empty with { Raise = [Raise(ItemPath(0))] };
            rig.Store.CommitRecordAsync(rig.Pair.PairId, raising, null).GetAwaiter().GetResult();
            Check.Require(rig.Store.Issues(rig.Pair.PairId, new[] { IssueKind.Failure })
                    .Any(issue => issue.Path == ItemPath(0)),
                "a raising first-run Record commits now and reports its issue once");

            var folderRecord = Record(new Item(0).Saved with { IsFolder = true });
            rig.Store.CommitRecordAsync(rig.Pair.PairId, folderRecord, null).GetAwaiter().GetResult();
            Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, new Item(0).Key) is not null,
                "a first-run folder Record commits now");

            var malformed = StateChange.Empty with { ForgetSelf = [(Side.Local, PathNames.Key(ItemPath(1)))] };
            var recordRejected = false;
            try { rig.Store.CommitRecordAsync(rig.Pair.PairId, malformed, null).GetAwaiter().GetResult(); }
            catch (ArgumentException) { recordRejected = true; }
            Check.Require(recordRejected, "CommitRecordAsync still rejects a malformed change");

            var immediate = rig.Measure(() =>
                rig.Store.CommitAsync(rig.Pair.PairId, raising).GetAwaiter().GetResult());
            Row("ineligible CommitAsync", 1, default, immediate.Allocated, immediate.Commands, immediate.CommitCalls, 0, 0);
            Check.Require(immediate.Commands == 1 && immediate.CommitCalls == 1,
                "an ineligible ordered change takes the immediate commit path");
            Check.Require(rig.Store.Issues(rig.Pair.PairId, new[] { IssueKind.Failure }).Count == 1,
                "the immediate path commits its issue");
        }

        using (var rig = new Rig(Path.Combine(folder, "drain-isolation.db")))
        {
            const int valid = 5;
            var seeds = Enumerable.Range(0, valid + 2).Select(index => new Item(index).Saved).ToArray();
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = seeds[..1] });
            rig.Counted.Reset();
            // A per-invocation lambda parameter: a for-loop variable would be shared by every queued member.
            var tasks = Enumerable.Range(1, valid)
                .Select(index => Task.Run(() => rig.Store.CommitRecordAsync(rig.Pair.PairId, Record(seeds[index]), null)))
                .ToList();
            // A record whose key is fine but whose required path column is missing is a deterministic constraint refusal.
            var firstBad = Task.Run(() => rig.Store.CommitRecordAsync(rig.Pair.PairId,
                Record(seeds[valid] with { LocalPath = null! }), null));
            var secondBad = Task.Run(() => rig.Store.CommitRecordAsync(rig.Pair.PairId,
                Record(seeds[valid + 1] with { LocalPath = null! }), null));
            tasks.Add(firstBad);
            tasks.Add(secondBad);
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
            catch (Exception) { }
            Check.Require(tasks.Take(valid).All(task => task.IsCompletedSuccessfully),
                "valid siblings complete while a bad member is isolated");
            Check.Require(firstBad.IsFaulted && secondBad.IsFaulted, "both bad members fault on their own");
            Check.Require(rig.Counted.RecordCommittedMembers == valid,
                "every valid sibling is durably settled: " + rig.Counted.RecordCommittedMembers + " of " + valid);
            Check.Require(rig.Counted.RecordCommitFailures == 2,
                "each refused member faults once with no singleton retry: " + rig.Counted.RecordCommitFailures);
        }
        using (var reopened = StateStore.Open(Path.Combine(folder, "drain-isolation.db")))
        {
            for (var index = 1; index <= 5; index++)
                Check.Require(reopened.SyncedByKey("cost", Side.Local, new Item(index).Key) is not null,
                    "valid sibling survives reopen after isolation: " + index);
            Check.Require(reopened.SyncedByKey("cost", Side.Local, new Item(0).Key) is not null,
                "the seeded binding survives a bad member");
        }
        Console.WriteLine("  explicit record-drain transactions: UNAVAILABLE/NOT PROVEN (G1); notification groups are not " +
            "transactions. The isolation path bounds work at one grouped attempt plus one singleton per member " +
            "(1 + N); exact transaction attribution and post-commit member certainty remain MEASURE NOT PROVEN");
    }

    // V13-A28 / V16-Q5: an in-flight page retry retries only the unpublished page. It adds page attempts, never a whole
    // acquisition and never a durable pair Attempts/RetryAfter update; the page-wait notice is a separately counted
    // IssueTable write and repeated failures keep one notice.
    private static void PageRetryCosts(string folder)
    {
        const int pageAttempts = 3;
        foreach (var count in new[] { Small, Large })
        {
            var path = Path.Combine(folder, "page-retry-" + count + ".db");
            using (var rig = new Rig(path))
            {
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                {
                    Record = Enumerable.Range(0, count).Select(index => new Item(index).Saved).ToArray(),
                });
                var acquisition = rig.Measure(() => rig.Store.BeginScan(rig.Pair.PairId, 0, includeDeleted: false));
                var operation = new object();
                Issue notice = null!;
                var attempts = rig.Measure(() =>
                {
                    for (var attempt = 0; attempt < pageAttempts; attempt++)
                        notice = rig.Store.RecordPrerequisitePageWait(rig.Pair.PairId, 0, operation, PageWaitChange());
                });
                Row("page acquire " + count, 1, default, acquisition.Allocated, acquisition.Commands,
                    acquisition.CommitCalls, 0, 0);
                Row("page attempts " + count, pageAttempts, default, attempts.Allocated, attempts.Commands,
                    attempts.CommitCalls, 0, 0);
                Check.Require(acquisition.Commands == 1, "a whole acquisition is one BeginScan command");
                Check.Require(attempts.Commands == pageAttempts,
                    "each failed page attempt is one page-wait command: " + attempts.Commands);
                Check.Require(rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is null,
                    "page attempts leave durable pair Attempts/RetryAfter unwritten");
                var notices = rig.Store.Issues(rig.Pair.PairId, new[] { IssueKind.Failure });
                Check.Require(notices.Count == 1 && notices[0].Cause == TextKey.RequiredRepairRetry,
                    "repeated page failures keep one RequiredRepairRetry notice (an IssueTable write, not a pair write)");
                rig.Store.CompletePrerequisitePageWait(rig.Pair.PairId, 0, operation, notice);
                Check.Require(rig.Store.Issues(rig.Pair.PairId, new[] { IssueKind.Failure }).Count == 0,
                    "page success clears only its page-wait notice");
                Check.Require(rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is null,
                    "page success leaves durable pair Attempts/RetryAfter unwritten");
            }
            using (var reopened = StateStore.Open(path))
            {
                Check.Require(reopened.ReadPrerequisiteRetry("cost") is null,
                    "reopen confirms zero page-only durable pair retry state");
                Check.Require(reopened.Issues("cost", new[] { IssueKind.Failure }).Count == 0,
                    "reopen confirms the page-wait notice was closed");
            }
        }
    }

    // B10: the coordinator-adopted StoragePageSize is data-owned. The retry reader is measured with that exact value at
    // two unrelated-pair inventories while the involved retry rows stay fixed; continuation has no skip or duplicate.
    private static void StoragePaging(string folder)
    {
        var settings = AdoptedSettings();
        var pageSize = settings.StoragePageSize;
        Check.Require(pageSize == AdoptedPageSize,
            "the adopted StoragePageSize default is " + AdoptedPageSize + ": " + pageSize);
        StatementCost? smallPageSql = null;
        foreach (var unrelated in new[] { SmallFiles, LargeFiles })
        {
            var path = Path.Combine(folder, "paging-" + unrelated + ".db");
            using (var schema = StateStore.Open(path)) { }
            SeedRetryPairs(path, FixedRetryPairs, unrelated);
            using var rig = new Rig(path);
            PageWalk walk = default;
            var measured = rig.Measure(() => walk = WalkRetryPages(rig.Store, pageSize));
            Row("retry pages " + unrelated, FixedRetryPairs, default, measured.Allocated, measured.Commands,
                measured.CommitCalls, 0, 0);
            Check.Require(walk.Pages == (FixedRetryPairs + pageSize - 1) / pageSize,
                "page count at the adopted page size: " + walk.Pages);
            Check.Require(walk.Rows == FixedRetryPairs, "every seeded retry row is returned once: " + walk.Rows);
            Check.Require(walk.LargestPage <= pageSize, "no page exceeds the requested size: " + walk.LargestPage);
            Check.Require(measured.Commands == walk.Pages, "one reader call per page");
            Console.WriteLine("  storage page size " + pageSize + " at " + unrelated + " unrelated pairs: requested=" +
                pageSize + " per page, returned=" + walk.Rows + " rows over " + walk.Pages + " pages");

            using var probe = new CostProbe(Path.Combine(folder, "paging-probe-" + unrelated + ".db"));
            probe.CreateSchema();
            probe.Begin();
            probe.Execute(SeedRetryPairsSql, ("$retries", FixedRetryPairs), ("$unrelated", unrelated));
            probe.Commit();
            var first = probe.Execute(StateSchema.EarliestRetry, (StateSchema.ParamAfterTicks, -1L),
                (StateSchema.ParamAfterPair, -1L), (StateSchema.ParamTake, (long)pageSize));
            var continuation = probe.Execute(StateSchema.EarliestRetry, (StateSchema.ParamAfterTicks, 1L),
                (StateSchema.ParamAfterPair, (long)pageSize), (StateSchema.ParamTake, (long)pageSize));
            Row("retry page sql " + unrelated, 1, first, 0, 0, 0, 0, 0);
            Check.Require(first.ProbePrepares == 1 && continuation.ProbePrepares == 1 && first.ChangedRows == 0 &&
                first.PathKeyCalls == 0, "the retry page is one read-only indexed statement");
            Check.Require(continuation.VmSteps <= first.VmSteps * 2,
                "equal-deadline continuation repeats the same bounded page work");
            if (smallPageSql is { } previous)
                Check.Require(first.VmSteps <= previous.VmSteps * 2,
                    "the retry page work does not grow with unrelated pair inventory: " + first.VmSteps + " vs " +
                    previous.VmSteps);
            else
                smallPageSql = first;
        }
    }

    private static PageWalk WalkRetryPages(IStateStore store, int pageSize)
    {
        var seen = new HashSet<long>();
        var pages = 0;
        var largest = 0;
        long rows = 0;
        long? afterTicks = null;
        long? afterPair = null;
        while (true)
        {
            var page = store.ReadPrerequisiteRetries(afterTicks, afterPair, pageSize).ToList();
            pages++;
            rows += page.Count;
            largest = Math.Max(largest, page.Count);
            foreach (var (pairRow, pairId, _, state) in page)
            {
                Check.Require(pairId.StartsWith("retry-", StringComparison.Ordinal),
                    "iteration returns only seeded retry pairs: " + pairId);
                Check.Require(seen.Add(pairRow), "one continuation visits each retry pair once: " + pairRow);
                afterTicks = state.RetryAfter.UtcTicks;
                afterPair = pairRow;
            }
            if (page.Count < pageSize) break;
        }
        return new PageWalk(pages, rows, largest);
    }

    // V13-E59: a fixed involved subject set is read with one page-batched statement and the same rows however the
    // unrelated inventory grows. No fabricated in-use lifecycle or timed hold exists without a real failure.
    private static void FixedInvolvedCosts(string folder)
    {
        foreach (var count in new[] { Small, Large })
        {
            using (var rig = new Rig(Path.Combine(folder, "involved-" + count + ".db")))
            {
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                {
                    Record = Enumerable.Range(0, count).Select(index => new Item(index).Saved).ToArray(),
                });
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                {
                    Raise =
                    [
                        (IssueKind.Failure, TextKey.FailChanged, ItemPath(0), new IssueSubject([], null)),
                        (IssueKind.Failure, TextKey.FailChanged, ItemPath(1), new IssueSubject([], null)),
                        (IssueKind.Failure, TextKey.FailLocalInUse, ItemPath(2), new IssueSubject([], null)),
                    ],
                });
                var ids = rig.Store.Issues(rig.Pair.PairId).Select(issue => issue.Id).ToArray();
                Check.Require(ids.Length == FixedIssues, "the fixed involved issue set is present");
                var measured = rig.Measure(() =>
                {
                    var issues = rig.Store.Issues(rig.Pair.PairId);
                    var held = issues.Count(issue => IssuePolicy.Holds(issue.Kind, issue.Cause));
                    var counts = rig.Store.IssueCounts(rig.Pair.PairId);
                    var retry = rig.Store.ReadItemRetryState(rig.Pair.PairId, ids);
                    Check.Require(issues.Count == FixedIssues, "fixed issues stay readable");
                    Check.Require(held == 0, "the in-use family is opportunity-only, not a timed hold");
                    Check.Require(counts.Values.Sum() == FixedIssues, "fixed issue counts stay bounded");
                    Check.Require(retry.Count == FixedIssues && retry.Values.All(value => value is null),
                        "no in-use lifecycle or attempt is fabricated without a real failure");
                });
                Row("fixed involved " + count, count, default, measured.Allocated, measured.Commands,
                    measured.CommitCalls, 0, 0);
                Check.Require(measured.Commands == 3,
                    "issues, counts and retry are three commands; held policy reuses the loaded issues");
            }

            using var probe = NewProbe(Path.Combine(folder, "involved-probe-" + count + ".db"));
            probe.Begin();
            for (var index = 0; index < count; index++) probe.Execute(StateSchema.InsertSynced, InsertArguments(index));
            var subject = JsonSerializer.Serialize(new IssueSubject([], null));
            for (var index = 0; index < FixedIssues; index++)
                probe.Execute(StateSchema.InsertIssue, (StateSchema.ParamPair, "cost"),
                    (StateSchema.ParamPath, ItemPath(index)),
                    (StateSchema.ParamKind, (int)IssueKind.Failure),
                    (StateSchema.ParamCause, TextKey.FailChanged.ToString()),
                    (StateSchema.ParamSubject, subject), (StateSchema.ParamAt, 1L));
            probe.Commit();
            var issues = probe.Execute(StateSchema.SelectIssues, (StateSchema.ParamPair, "cost"));
            var held = probe.Execute(StateSchema.SelectHoldingSubjects, (StateSchema.ParamPair, "cost"));
            var counts = probe.Execute(StateSchema.CountIssues,
                [(StateSchema.ParamPair, "cost"), (StateSchema.ParamIgnored, null), .. IssueTable.PublicCategoryParameters]);
            Row("fixed issue sql " + count, count, issues, 0, 0, 0, 0, 0);
            Check.Require(issues.ProbePrepares == 1 && held.ProbePrepares == 1 && counts.ProbePrepares == 1,
                "each fixed issue read is one statement");
            Check.Require(issues.PathKeyCalls == 0 && held.PathKeyCalls == 0 && counts.PathKeyCalls == 0,
                "fixed issue reads need no path-key callback");
        }
        Console.WriteLine("  fixed involved set: no per-inventory membership scan and no due/backoff state");
    }

    // V13-D57 / CV-COST: the P6 hash-cost operands are workload-scoped. W01's eligible hashes/bytes/elapsed are
    // UNKNOWN; the exceptional W05 byte total is never the W01 operand. Two-endpoint and W=100 totals are derived
    // arithmetic, not measured acceptance. Other unavailable fields are labelled, not fabricated.
    private static void EvidenceLedger()
    {
        const int w01RemoteEntries = 1_125_932;
        const int w01LocalEntries = 632_489;
        const int w02LiveEntries = 1_526_314;
        const int w05FileHashes = 255_500;
        const long w05HashBytes = 20_136_304_543;
        Check.Require(w01LocalEntries <= w01RemoteEntries && w01RemoteEntries < w02LiveEntries,
            "the recorded W01 and W02 anchors stay distinct scopes");
        Check.Require(w05FileHashes < w01RemoteEntries,
            "the W05 example is not the W01 workload; its byte bound is never substituted");
        Console.WriteLine("  P6 hash cost W01: at most " + w01LocalEntries + " local candidate entries; eligible hashes/bytes/elapsed UNKNOWN");
        Console.WriteLine("  P6 hash cost W05 (separate workload): at most " + w05FileHashes + " file hashes and " +
            w05HashBytes + " local bytes if every file is eligible; never applied to W01/W02");
        Console.WriteLine("  CV-COST anchors S=" + w01RemoteEntries + " L=" + w02LiveEntries +
            ": W=1 and W=100 logical passes and two-endpoint totals are DERIVED, not measured");
        Console.WriteLine("  executed pair/JSON function counts inside SQL: UNAVAILABLE; registered pathkey callbacks and VM steps are measured");
        Console.WriteLine("  elapsed time, provider calls, native reads and physical flush counts: NOT MEASURED here");
        Console.WriteLine("  explicit managed transaction outcomes and record-drain transactions: UNAVAILABLE (MEASURE 3.3/G1)");
    }

    private static EngineSettings AdoptedSettings()
    {
        using var stream = typeof(EngineSettings).Assembly.GetManifestResourceStream(ResourceNames.Settings)
            ?? throw new InvalidOperationException("the embedded engine settings resource is missing");
        using var reader = new StreamReader(stream);
        return SettingsParser.Parse(reader.ReadToEnd(), null);
    }

    private static void SeedRetryPairs(string path, int retryPairs, int unrelatedPairs)
    {
        using var connection = StateStore.Connect(path, SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SeedRetryPairsSql;
        command.Parameters.AddWithValue("$retries", retryPairs);
        command.Parameters.AddWithValue("$unrelated", unrelatedPairs);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static long RetainedBytes(string path)
    {
        var bytes = new FileInfo(path).Length;
        var wal = path + "-wal";
        return File.Exists(wal) ? bytes + new FileInfo(wal).Length : bytes;
    }

    private static StateChange PageWaitChange() => StateChange.Empty with { Raise = PageWaitRaise };

    private static readonly (IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject)[] PageWaitRaise =
        [(IssueKind.Failure, TextKey.RequiredRepairRetry, "", new IssueSubject([], null))];

    private static CostProbe NewProbe(string path)
    {
        var probe = new CostProbe(path);
        probe.CreateSchema();
        probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, "cost"), (StateSchema.ParamDef, "{}"));
        return probe;
    }

    private static Rig NewRig(string path) => new(path);

    private static double PerItem(long value, int items) => (double)value / items;

    private static void Header() => Console.WriteLine(
        "  " + "shape".PadRight(30) + "items".PadLeft(6) + "vm".PadLeft(10) + "pathkey".PadLeft(9) +
        "pair-src".PadLeft(9) + "json-src".PadLeft(9) + "changed".PadLeft(9) + "probe-prep".PadLeft(11) +
        "alloc/kB".PadLeft(10) + "cmds".PadLeft(6) + "commit".PadLeft(8) + "read".PadLeft(6) + "rows".PadLeft(7));

    private static void Row(string shape, int items, StatementCost cost, long allocated, long commands,
        long commits, long reads, long rows) => Console.WriteLine(
        "  " + shape.PadRight(30) + items.ToString().PadLeft(6) + cost.VmSteps.ToString().PadLeft(10) +
        cost.PathKeyCalls.ToString().PadLeft(9) + cost.PairSourceOccurrences.ToString().PadLeft(9) +
        cost.JsonSourceOccurrences.ToString().PadLeft(9) + cost.ChangedRows.ToString().PadLeft(9) +
        cost.ProbePrepares.ToString().PadLeft(11) + (allocated / 1024).ToString().PadLeft(10) +
        commands.ToString().PadLeft(6) + commits.ToString().PadLeft(8) + reads.ToString().PadLeft(6) +
        rows.ToString().PadLeft(7));

    /// <summary>One real store under a command counter; a measurement reports its own delta.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(string path)
        {
            Counted = new StoreCounters(StateStore.Open(path));
            Store = Counted;
            Store.SavePair(Pair);
        }

        public PairConfig Pair { get; } = new("cost", "Pair A", "X:/Parent", "/Parent", "account", [], [],
            FirstRunPolicy.NewestWins, true, null);
        public IStateStore Store { get; }
        public StoreCounters Counted { get; }

        public (long Allocated, long Commands, long CommitCalls, long Reads, long Rows) Measure(Action action)
        {
            Counted.Reset();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            action();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            return (allocated, Counted.Commands, Counted.CommitCalls, Counted.ReadConnections, Counted.RowsRead);
        }

        public void Dispose() => Counted.Dispose();
    }

    private readonly record struct PageWalk(int Pages, long Rows, int LargestPage);

    private readonly record struct Measure(StatementCost Sql, long Allocated, long Commands, long CommitCalls);

    private readonly record struct SizeCosts(int Items, Measure First, Measure Changed, Measure Unchanged,
        long FirstBytes, long ChangedBytes, long UnchangedBytes);
}
