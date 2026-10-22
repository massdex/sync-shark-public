using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    // Creates or reuses a test-owned 1M SQLite fixture; no journal or physical file writes.
    private static async Task ProtectionIndexAsync(string folder)
    {
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast, TimeProvider.System);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(folder, "store", "v2", "state.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM file_state;";
        if ((long)command.ExecuteScalar()! == 0)
        {
            var seed = new ItemState("edit.txt", ItemKind.File, "benchmark-edit", null,
                new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None);
            var gen = store.BeginGeneration("root-local", "fixture");
            store.WriteSnapshot(new Snapshot("root-local", Side.A, gen, "", [seed], []));
            store.CompleteGeneration(gen, "", true);
            command.CommandText = """
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<999999),
                seed AS MATERIALIZED (SELECT * FROM file_state WHERE object_id='benchmark-edit')
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags,identity_json)
                SELECT f.root_no,f.dir_no,printf('SYNTHETIC-%07d',n.x),printf('synthetic-%07d',n.x),
                  f.kind,printf('synthetic-%07d',n.x),NULL,f.hash,f.size,f.mtime,0,NULL
                FROM n CROSS JOIN seed f;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "SELECT COUNT(*) FROM file_state;";
        LocalChecks.Require((long)command.ExecuteScalar()! >= 1_000_000, "index benchmark has 1M database rows");
        // Reset only synthetic benchmark holds left by an earlier invocation; retain all fixture rows.
        command.CommandText = "UPDATE file_state SET flags=flags & ~256 WHERE flags!=0 AND (flags & 256)!=0 AND object_id LIKE 'synthetic-%';";
        command.ExecuteNonQuery();
        var held = store.Live("root-local", ["edit.txt"]).Single();
        store.RequireRehash("root-local", held, "protection-index benchmark");
        var build = Stopwatch.StartNew();
        var index = store.ProtectionIndex("root-local", ItemFlags.NeedsRehash)!;
        build.Stop();
        LocalChecks.Require(index is not null && index.Count == 1, "bounded flag snapshot excludes verified queue-only obligations");
        var queries = new[] { "", "edit.txt", "EDIT.TXT", "edit.txt/child/deep", "edit.txt-other", "other/deep/file" };
        var batch = store.ProtectionBatch("root-local", ItemFlags.NeedsRehash, queries);
        for (var n = 0; n < queries.Length; n++)
        {
            LocalChecks.Require(index!.Touches(queries[n], false) == store.RehashTouches("root-local", queries[n], false) &&
                index.Touches(queries[n], true) == store.RehashTouches("root-local", queries[n], true), "snapshot matches old ancestor/self/descendant semantics");
            LocalChecks.Require(batch[n] == new ProtectionMatch(index.Touches(queries[n], false), index.Touches(queries[n], true)),
                "batch matches snapshot");
        }
        var unicode = PathProtectionIndex.TryCreate(["Ä/é.txt", "ſ/file", "deep/nested/held"]);
        LocalChecks.Require(unicode!.Touches("ä/É.TXT/child", false) && !unicode.Touches("S/file", false) &&
            unicode.Touches("deep", true) && !unicode.Touches("dee", true), "Unicode case and segment boundaries preserved");
        LocalChecks.Require(PathProtectionIndex.TryCreate(["long/path"], characterLimit: 3) is null,
            "character cap bounds retained strings without ancestor multiplication");
        var special = new[] { "Ä/é.txt", "ſ/file", "deep/nested/held", "￿/held", "Case", "case" }
            .Select((path, n) => held with { Path = path, ObjectId = "unicode-" + n,
                Flags = ItemFlags.NeedsRehash | ItemFlags.Ignored | (ItemFlags)128 }).ToArray();
        store.WriteSnapshot(new Snapshot("unicode-index", Side.A, store.BeginGeneration("unicode-index", "full"), "", special, []));
        foreach (var item in special) store.RequireRehash("unicode-index", item, "Unicode protection equivalence");
        var specialIndex = store.ProtectionIndex("unicode-index", ItemFlags.NeedsRehash)!;
        var specialPaths = new[] { "", "ä/É.TXT", "Ä", "ſ", "S", "deep", "dee", "deep/nested/held/child", "￿", "CASE" };
        var specialBatch = store.ProtectionBatch("unicode-index", ItemFlags.NeedsRehash, specialPaths);
        for (var n = 0; n < specialPaths.Length; n++)
        {
            var expected = new ProtectionMatch(store.RehashTouches("unicode-index", specialPaths[n], false),
                store.RehashTouches("unicode-index", specialPaths[n], true));
            LocalChecks.Require(specialBatch[n] == expected &&
                new ProtectionMatch(specialIndex.Touches(specialPaths[n], false), specialIndex.Touches(specialPaths[n], true)) == expected,
                "SQLite Unicode, ignored holds and descendant bounds equal old oracle");
        }
        LocalChecks.Require(specialIndex.Count == 5, "Unicode fixture has five distinct protected spellings");
        LocalChecks.Require(store.ProtectionIndex("unicode-index", ItemFlags.NeedsRehash, pathLimit: 5) is null,
            "raw-row cap detects case-collapse truncation");
        LocalChecks.Require(store.ProtectionBatch("unicode-index", (ItemFlags)128, specialPaths).SequenceEqual(specialBatch),
            "128 flag selection preserves independent protection semantics");
        // A high folder must produce one containment answer, not materialize its held subtree.
        var dense = Enumerable.Range(0, 4096).Select(n => held with
        { Path = $"dense/child/file-{n}", ObjectId = "dense-" + n, Flags = (ItemFlags)128 });
        store.WriteSnapshot(new Snapshot("dense-index", Side.A, store.BeginGeneration("dense-index", "full"), "", dense.ToArray(), []));
        var denseQueries = store.ProtectionBatchQueries;
        var denseMatches = store.ProtectionBatch("dense-index", (ItemFlags)128,
            ["dense", "dense/child", "dense/child/file-0", "dens", "dense/missing", ""]);
        LocalChecks.Require(denseMatches.SequenceEqual(new[]
        {
            new ProtectionMatch(false, true), new ProtectionMatch(false, true),
            new ProtectionMatch(true, true), new ProtectionMatch(false, false),
            new ProtectionMatch(false, false), new ProtectionMatch(false, true)
        }) && store.ProtectionBatchQueries == denseQueries + 1,
            "dense descendant EXISTS preserves high-folder and boundary answers in one batch");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var managed = GC.GetTotalMemory(false); var privateBytes = process.PrivateMemorySize64;
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var hits = 0;
        for (var n = 0; n < 1_000_000; n++)
        {
            var path = n % 2 == 0 ? $"folder/child/deep/missing-{n}.txt" : "edit.txt/child/deep";
            if (index!.Touches(path, false)) hits++;
        }
        watch.Stop(); process.Refresh();
        Console.WriteLine($"1M actual planned-path checks / 1M SQLite rows: snapshot={build.ElapsedMilliseconds}ms, elapsed={watch.ElapsedMilliseconds}ms, hits={hits}, allocated={GC.GetTotalAllocatedBytes(true)-allocated:N0}, managedDelta={GC.GetTotalMemory(false)-managed:N0}, privateDelta={process.PrivateMemorySize64-privateBytes:N0}");
        LocalChecks.Require(hits == 500_000, "all one-million lookup answers correct");
        LocalChecks.Require(store.CompleteRehash("root-local", held, new string('a', 64)), "verified queue retained but unheld");
        LocalChecks.Require(store.ProtectionIndex("root-local", ItemFlags.NeedsRehash)!.Count == 0,
            "verified planning obligation is not a protection hold");
        // Real overflow beyond the production count cap; never load these 60k items into managed rows.
        command.CommandText = "UPDATE file_state SET flags=flags|256 WHERE object_id IN (SELECT object_id FROM file_state WHERE object_id LIKE 'synthetic-%' ORDER BY object_id LIMIT 60000);";
        command.ExecuteNonQuery();
        LocalChecks.Require(store.ProtectionIndex("root-local", ItemFlags.NeedsRehash) is null, "60k held paths select bounded batch mode");
        var queryCount = store.ProtectionBatchQueries;
        var overflowWatch = Stopwatch.StartNew();
        var overflow = store.ProtectionBatch("root-local", ItemFlags.NeedsRehash, ["", "synthetic-0000001", "missing/path"]);
        overflowWatch.Stop();
        Console.WriteLine($"60k protected overflow: 3 path batch={overflowWatch.ElapsedMilliseconds}ms, SQL commands={store.ProtectionBatchQueries-queryCount}");
        LocalChecks.Require(store.ProtectionBatchQueries == queryCount + 1, "one SQL command per overflow batch, not per path");
        LocalChecks.Require(overflow[0].Contains && !overflow[0].Covered && overflow[1].Covered && !overflow[2].Covered,
            "overflow batch protects root/self without protecting unrelated paths");
        var realistic = Enumerable.Range(0, 1024).Select(n => n == 0 ? "synthetic-0000001" : $"clear-{n}").ToArray();
        overflowWatch.Restart(); queryCount = store.ProtectionBatchQueries;
        var realisticMatches = store.ProtectionBatch("root-local", ItemFlags.NeedsRehash, realistic);
        overflowWatch.Stop();
        Console.WriteLine($"60k protected overflow: 1024 path batch={overflowWatch.ElapsedMilliseconds}ms, SQL commands={store.ProtectionBatchQueries-queryCount}");
        LocalChecks.Require(realisticMatches.Count(match => match.Covered) == 1 && store.ProtectionBatchQueries == queryCount + 1,
            "realistic full batch gives exact answers in one command");
        LocalChecks.Require(store.ProtectionIndex("root-local", (ItemFlags)128)!.Count == 0 &&
            !store.ProtectionBatch("root-local", (ItemFlags)128, ["synthetic-0000001"])[0].Covered,
            "flag parameter does not conflate independent holds");
        await OverflowConsumers(store, folder, held);
        Console.WriteLine("PASS bounded protection index and overflow batch");
    }

    private static async Task OverflowConsumers(SqliteStateStore store, string folder, ItemState seed)
    {
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Directory.CreateDirectory(Path.Combine(folder, "root"));
        var a = LocalChecks.RootOf(Path.Combine(folder, "root"), "root-local");
        var pair = new PairDef("index-check", "index", a,
            new RootDef("unused", SyncProviders.Dropbox, null, "/", "unused"), [], [], true);
        var protection = new RehashProtection(store, pair);
        var missedQueries = store.ProtectionBatchQueries;
        LocalChecks.Require(protection.Touches("synthetic-0000001", false) &&
            protection.Touches("SYNTHETIC-0000001/child", false) && protection.Touches("", true) &&
            !protection.Touches("missing/path", false) && store.ProtectionBatchQueries == missedQueries + 4,
            "unprepared overflow resolves exceptional paths safely without failing the run");
        var prepared = protection.ForPaths(["clear-prepared"]);
        missedQueries = store.ProtectionBatchQueries;
        LocalChecks.Require(prepared.Touches("synthetic-0000001", false) && !prepared.Touches("clear-prepared", false) &&
            store.ProtectionBatchQueries == missedQueries + 1,
            "prepared overflow miss falls back only for missing path; prepared hit adds no SQL");
        var links = LinkExclusions.Of(store, pair, false);
        var scope = new RunScope(store, _ => true, _ => true, links);
        var rows = Enumerable.Range(0, 1025).Select(n => seed with { Path = n == 0 ? "synthetic-0000001" : $"clear-{n}" }).ToArray();
        var queries = store.ProtectionBatchQueries;
        var filtered = (IReadOnlyList<ItemState>)typeof(RunScope).GetMethod("Hide", hidden)!.Invoke(scope, [rows, Side.A, null])!;
        LocalChecks.Require(filtered.Count == 1024 && filtered.All(row => row.Path != "synthetic-0000001"), "production row filtering protects overflow holds");
        LocalChecks.Require(store.ProtectionBatchQueries - queries == 3,
            "standalone row consumer uses three bounded 512-item batches for 1025 rows (at most 1024 mapped paths each)");
        var mapped = seed with { Path = "provider-spelling", IdentityJson = "{\"dropboxWindowsPath\":\"synthetic-0000001\"}" };
        var mappedResult = (IReadOnlyList<ItemState>)typeof(RunScope).GetMethod("Hide", hidden)!
            .Invoke(scope, [new[] { mapped }, Side.B, null])!;
        LocalChecks.Require(mappedResult.Count == 0, "overflow row filtering prepares actual and mapped alias paths");
        store.SavePair(pair);
        var endpointA = new FakeEndpoint(Side.A, pair.A.RootId);
        var endpointB = new FakeEndpoint(Side.B, pair.B.RootId);
        var provider = new FakeEndpointProvider().Add(pair.A, endpointA).Add(pair.B, endpointB);
        var run = new SyncRun(store, new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission()),
            provider, StorageChecks.Fast, TimeProvider.System);
        var guard = typeof(SyncRun).GetMethod("ActionPredicates", hidden)!.Invoke(run, [pair, links, null]);
        var actions = rows.Select((row, n) => new SyncAction("protection-" + n, 1, ActionKind.CopyNew,
            Side.B, row.Path, null, row, null, false, null)).ToArray();
        queries = store.ProtectionBatchQueries;
        var plan = (Plan)typeof(SyncRun).GetMethod("ApplySafetyFilter", hidden)!.Invoke(run,
            [pair, guard, new Plan(pair.PairId, 1, actions, [])])!;
        LocalChecks.Require(plan.Actions.Count == 1024 && plan.SkippedActions.Count == 1,
            "production source/target safety filter batches overflow");
        LocalChecks.Require(store.ProtectionBatchQueries - queries == 5, "action consumer uses five bounded batches, zero per-path SQL");

        var variants = new[]
        {
            actions[0] with { Path = "clear-target", Source = seed with { Path = "synthetic-0000001" } },
            actions[0] with { Path = "clear-target", Source = null, FromPath = "synthetic-0000001" },
            actions[0] with { Path = "SYNTHETIC-0000001/child", Source = null },
            actions[0] with { Path = "", Source = null, Kind = ActionKind.Delete },
        };
        var guarded = (Plan)typeof(SyncRun).GetMethod("ApplySafetyFilter", hidden)!.Invoke(run,
            [pair, guard, new Plan(pair.PairId, 1, variants, [])])!;
        LocalChecks.Require(guarded.Actions.Count == 0, "overflow guards source, from, case-folded descendant and root deletion");

        // Reach the public restore flow with a valid retained-version record, but no archive reads or effects.
        var loser = endpointA.Seed("archive-test.txt", "old");
        var winner = endpointA.Seed("archive-test.txt", "new");
        var other = endpointB.Seed("archive-test.txt", "new");
        var original = new SyncAction("protection-archive", 4, ActionKind.Update, Side.A,
            winner.Path, null, other, loser, true, "protection-archive-key");
        store.CommitResult(pair.PairId, store.BeginGeneration(pair.A.RootId, "run"), original,
            new ActionResult(original.ActionId, Outcome.Done, winner, other,
                new RecoveryRef(Side.A, Path.Combine(folder, "not-read.recovery"), null, loser.Hash!, loser.Size), null));
        store.AddNotices([new Notice(pair.PairId, 4, NoticeKind.AutoConflict, "synthetic-0000001", "overflow restore", "protection-restore",
            RetainedVersion: new RetainedNoticeVersion(original.ActionId, Side.A, loser, winner))]);
        var notice = store.OpenNotices(pair.PairId).Single(item => item.ReasonKey == "protection-restore");
        queries = store.ProtectionBatchQueries;
        var restored = await run.RestoreNoticeVersionAsync(pair.PairId, notice.NoticeId!, true, CancellationToken.None);
        LocalChecks.Require(restored.Outcome == Outcome.Skipped && restored.Reason!.Contains("protected by exclusions") &&
            endpointA.WriteCalls == 0 && endpointB.WriteCalls == 0 && store.ProtectionBatchQueries == queries + 1,
            "public notice restore prepares overflow batch and protects notice path before content reads");

        foreach (var action in actions)
            store.RecordIntent(pair.PairId, action with { Path = "synthetic-0000001/" + action.ActionId, Source = null });
        queries = store.ProtectionBatchQueries;
        var preflight = (Task<int>)typeof(SyncRun).GetMethod("CompletionIntentPreflightAsync", hidden)!.Invoke(run, [pair])!;
        LocalChecks.Require(await preflight == 1025 && store.ProtectionBatchQueries == queries,
            "completion preflight reports real exclusions without querying temporary hash-debt protection");
        queries = store.ProtectionBatchQueries;
        var reconcile = (Task)typeof(SyncRun).GetMethod("ReconcileAsync", hidden)!.Invoke(run,
            [pair, new ProgressThrottle(null, TimeProvider.System, TimeSpan.Zero), CancellationToken.None, null])!;
        await reconcile;
        LocalChecks.Require(store.ProtectionBatchQueries == queries + 5 && endpointA.WriteCalls == 0 && endpointB.WriteCalls == 0 &&
            store.UnresolvedIntents(pair.PairId).Count == 1025,
            "reconciliation classifies five bounded batches and retains every hash-debt-held intent without effects");
        queries = store.ProtectionBatchQueries;
        await (Task)typeof(SyncRun).GetMethod("ReconcileAsync", hidden)!.Invoke(run,
            [pair, new ProgressThrottle(null, TimeProvider.System, TimeSpan.Zero), CancellationToken.None, null])!;
        LocalChecks.Require(store.ProtectionBatchQueries == queries + 5 && endpointA.WriteCalls == 0 && endpointB.WriteCalls == 0 &&
            store.UnresolvedIntents(pair.PairId).Count == 1025,
            "next run keeps unresolved hash debt held rather than retiring it as an exclusion");
    }
}
