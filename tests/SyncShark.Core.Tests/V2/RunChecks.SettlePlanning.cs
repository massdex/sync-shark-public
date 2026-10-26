using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task SettlePlanningChecksAsync()
    {
        var folder = FixtureFolder("settle-planning");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System);
            // Real backlog shape: 5k equal baselines, 2k held, 1k changed, 1k urgent,
            // 1k equal-content adopts. Every path is a file, so no fixture folder effects.
            for (var n = 0; n < 10000; n++)
            {
                fx.A.Seed($"File{n:D5}", "same");
                fx.B.Seed($"File{n:D5}", "same");
            }
            await FirstSyncAsync(run, fx);
            var a = store.Live(fx.Pair.A.RootId).Where(item => item.Kind == ItemKind.File)
                .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            Require(a.Length == 10000, "settle fixture has ten thousand file entities");
            foreach (var item in a.Skip(7000).Take(1000)) fx.A.Seed(item.Path, "changed");
            using var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(folder)};Pooling=False");
            connection.Open();
            // Fixture mutation only: simulate the observation handoff and stale-baseline backlog
            // without manufacturing a second provider delta that would bypass the settle lane.
            StoreSchema.Execute(connection, null, """
                INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,reason)
                SELECT f.root_no,f.object_id,p.pair_no,f.name,1,$reason
                FROM file_state f CROSS JOIN pair p JOIN root r ON r.root_no=f.root_no
                WHERE r.root_id=$root AND f.kind=0;
                """, ("$reason", SqliteStateStore.ObservationPlanningReason), ("$root", fx.Pair.A.RootId));
            var boundWatch = System.Diagnostics.Stopwatch.StartNew();
            var boundBytes = GC.GetTotalAllocatedBytes(true);
            // Measurement only: a capped read now keeps its cursor mid-lap (N1), so this probe must not
            // move scheduling state before the full-backlog assertions below.
            var bounded = store.SettlePlanningCandidates(fx.Pair.PairId, DateTime.UtcNow.Ticks, 8192, [], advance: false);
            boundWatch.Stop();
            Require(bounded.Count == 8192, "candidate allocation is capped at 8192 on a ten-thousand-row backlog");
            Console.WriteLine($"settle bound=8192 elapsedMs={boundWatch.Elapsed.TotalMilliseconds:F2} allocatedBytes={GC.GetTotalAllocatedBytes(true) - boundBytes}");
            bounded = [];
            AssertSettleUnknownAndAlias(store, connection, fx.Pair, a[0]);
            StoreSchema.Execute(connection, null, """
                DELETE FROM sync_point WHERE a_name>='File09000';
                UPDATE file_state SET hash=$hash WHERE root_no=(SELECT root_no FROM root WHERE root_id=$root)
                  AND name>='File07000' AND name<'File08000';
                UPDATE planning_deferred SET reason=$user WHERE path>='File08000' AND path<'File09000';
                """, ("$reason", SqliteStateStore.ObservationPlanningReason), ("$root", fx.Pair.A.RootId),
                ("$hash", Convert.FromHexString(Hash("changed"))), ("$user", SqliteStateStore.UserChangeReason));
            foreach (var item in a.Skip(5000).Take(400)) store.RequireRehash(fx.Pair.A.RootId, item, "fixture rehash");
            foreach (var item in a.Skip(5400).Take(400))
                store.SaveSyncRetry(fx.Pair.PairId, new(item.Path, "sync", 1, "fixture retry", DateTimeOffset.UtcNow.AddDays(1)));
            store.AddNotices(a.Skip(5800).Take(400).Select(item =>
                new Notice(fx.Pair.PairId, 4, NoticeKind.UserDecision, item.Path, "fixture decision", "first-sync-tie")).ToArray());
            store.AddNotices(a.Skip(6200).Take(400).Select(item =>
                new Notice(fx.Pair.PairId, 40, NoticeKind.Info, item.Path, "fixture quarantine", "defender-quarantine")
                { Quarantine = new(Side.A, null, item, true) }).ToArray());
            StoreSchema.Execute(connection, null, """
                INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json)
                SELECT root_no,object_id,'fixture','{}' FROM file_state
                WHERE root_no=(SELECT root_no FROM root WHERE root_id=$root) AND name>='File06600' AND name<'File07000';
                """, ("$root", fx.Pair.A.RootId));
            StoreSchema.Execute(connection, null, """
                INSERT INTO local_gap(root_no,epoch,journal_cursor,boundary,trust_usn,discovery_done)
                SELECT root_no,'fixture','cursor',0,0,0 FROM root WHERE root_id=$root;
                """, ("$root", fx.Pair.A.RootId));
            Require(store.SettlePlanningCandidates(fx.Pair.PairId, DateTime.UtcNow.Ticks, 8192, []).Count == 0,
                "unfinished discovery prevents all settle intake");
            StoreSchema.Execute(connection, null, "UPDATE local_gap SET discovery_done=1;");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var candidates = store.SettlePlanningCandidates(fx.Pair.PairId, DateTime.UtcNow.Ticks, 8192, []);
            watch.Stop();
            Console.WriteLine($"settle candidates={candidates.Count} elapsedMs={watch.Elapsed.TotalMilliseconds:F2} allocatedBytes={GC.GetTotalAllocatedBytes(true) - allocated}");
            Require(candidates.Count == 7200, "stage one excludes rehash, gap, changed and urgent; leaves stage-two holds");
            Require(candidates.Any(row => row.Path == "File05400") && candidates.Any(row => row.Path == "File05800") &&
                candidates.Any(row => row.Path == "File06200"), "retry, decision and quarantine reach stage two");
            AssertSettleQueryPlan(connection);
            store.SavePair(fx.Pair with { IgnoreRules = ["FileName:File04999"] });
            var journal = new SettleBacklogInventory(fx.A);
            fx.Provider.AddInventory(fx.Pair.A, journal);
            var progress = new RecordingProgress();
            var report = await run.IncrementalAsync(fx.Pair.PairId, progress, default);
            Require(report.Failed == 0, "mixed settle run completes without failure");
            Require(report.JournalBacklog && report.PlanningBacklog > 0, "both backlog signals survive incremental completion");
            journal.Backlog = false;
            // CommitDone writes the baseline; inventory catches the destination up on its next feed.
            // Verify the actual endpoint here, not the deliberately older inventory projection.
            var copied = 0;
            foreach (var item in a.Skip(7000).Take(1000))
                if ((await fx.B.ObserveAsync(item.Path, default))?.Hash == Hash("changed")) copied++;
            Console.WriteLine($"ordinary changed files verified at destination: {copied}/1000");
            Require(copied == 1000, "changed files are copied by ordinary planning");
            var callsBefore = fx.WriterA.Calls + fx.WriterB.Calls;
            await run.IncrementalAsync(fx.Pair.PairId, progress, default);
            Require(fx.WriterA.Calls + fx.WriterB.Calls == callsBefore && fx.WriterA.Deletes + fx.WriterB.Deletes == 0,
                "settle-only continuation emits no endpoint writes or deletes");
            Require(Convert.ToInt64(StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM planning_deferred WHERE (path<'File05000' OR path>='File09000') AND path<>'File04999';")) == 0,
                "accepted neighbours and effect-free adopts drain despite one planner-rejected entity");
            Require(Convert.ToInt64(StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM planning_deferred WHERE path='File04999';")) == 0,
                // Settle stage 2 drops the ignored candidate; the run's ordinary acknowledgement then retires its
                // debt, since an ignored path has nothing to sync. Re-inclusion is owned by the reincluded-evidence path.
                "ignored entity is retired by ordinary acknowledgement, not left cycling in the queue");
            Require(Convert.ToInt64(StoreSchema.Scalar(connection, null,
                "SELECT COUNT(*) FROM planning_deferred WHERE path>='File05000' AND path<'File07000';")) == 2000,
                "gap, rehash and stage-two retry, decision and quarantine work stays queued");
            var summaries = progress.Reports.Where(item => item.Message?.StartsWith("settled ", StringComparison.Ordinal) == true).ToArray();
            Require(summaries.Length == 2, "one settle summary is emitted per completed run");
            foreach (var summary in summaries) Console.WriteLine(summary.Message);
            Require(store.Baselines(fx.Pair.PairId).Count(basis => basis.A.Path.CompareTo("File09000") >= 0) == 1000,
                "effect-free adopts commit baselines through the normal executor");
            // A re-mark after the candidate read must survive exact-version acknowledgement.
            var row = candidates.First();
            store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, a.First(item => item.ObjectId == row.ObjectId))]);
            var count = store.CountDeferredPlanning(fx.Pair.PairId);
            var retired = 0;
            store.AcknowledgeDeferredPlanning(fx.Pair.PairId, [row], _ => true, acknowledged: _ => retired++);
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == count && retired == 0,
                "stale candidate cannot acknowledge newer work or inflate settlement count");
            Require(!Planner.IsEffectFreeSettlement(new(fx.Pair.PairId, 1,
                [new("effect", 6, ActionKind.Delete, Side.A, "File", null, null, null, false, null)], [])),
                "settle gate rejects a planner delete");
        }
        finally { DeleteFixture(folder); }
    }

    private static void AssertSettleUnknownAndAlias(SqliteStateStore store, SqliteConnection connection,
        PairDef pair, ItemState item)
    {
        StoreSchema.Execute(connection, null, "UPDATE file_state SET hash=NULL WHERE object_id=$id;", ("$id", item.ObjectId));
        // Read-only assertions must not move the scheduling cursor: a capped read now keeps its
        // position, which would hide entities behind it from the next probe.
        Require(store.SettlePlanningCandidates(pair.PairId, DateTime.UtcNow.Ticks, 8192, [], advance: false)
            .All(row => row.ObjectId != item.ObjectId), "unknown hash never passes settlement prefilter");
        StoreSchema.Execute(connection, null, "UPDATE file_state SET hash=$hash WHERE object_id=$id;",
            ("$hash", Convert.FromHexString(item.Hash!)), ("$id", item.ObjectId));
        StoreSchema.Execute(connection, null, """
            INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags)
            SELECT root_no,dir_no,'ALIAS','Alias',kind,object_id,version_id,hash,size,mtime,flags
            FROM file_state WHERE object_id=$id;
            """, ("$id", item.ObjectId));
        Require(store.SettlePlanningCandidates(pair.PairId, DateTime.UtcNow.Ticks, 8192, [], advance: false)
            .All(row => row.ObjectId != item.ObjectId), "an unbaselined alias excludes the whole entity");
        StoreSchema.Execute(connection, null, "DELETE FROM file_state WHERE name='Alias' AND object_id=$id;", ("$id", item.ObjectId));
    }

    private static void AssertSettleQueryPlan(SqliteConnection connection)
    {
        using var command = StoreSchema.Command(connection, null, "EXPLAIN QUERY PLAN " + SqliteStateStore.SettlePlanningCandidatesSql);
        foreach (var (key, value) in new (string, object)[] { ("$p", 1), ("$a", 1), ("$b", 2),
            ("$before", long.MaxValue), ("$scan", SqliteStateStore.SettleExaminationCap), ("$v", 0), ("$r", -1), ("$o", ""),
            ("$observation", SqliteStateStore.ObservationPlanningReason),
            ("$flags", (long)ItemFlags.NeedsRehash), ("$gap", 1), ("$done", 1), ("$after", ""), ("$observed", "[]") })
            command.Parameters.AddWithValue(key, value);
        using var reader = command.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) steps.Add(reader.GetString(3));
        Require(steps.Any(step => step.Contains("SEARCH planning_deferred USING INDEX planning_deferred_pair_version")) &&
            steps.Any(step => step.Contains("MATERIALIZE window")),
            "settle fences indexed bounded window before probes and bounded output sorting");
    }
}
