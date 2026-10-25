namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Reconcile_AllPagesAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "reconcile-pages-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        const int count = 600;
        var writer = new ExcludedDeleteJobWriter(fx.WriterB) { Completed = true };
        fx.Provider.AddWriter(fx.Pair.B, writer);
        for (var i = 0; i < count; i++)
        {
            var path = "deleted-" + i;
            var source = fx.A.Seed(path, "same");
            var victim = fx.B.Seed(path, "same");
            var adopt = new SyncAction("adopt-" + i, 3, ActionKind.Adopt, Side.B, path, null,
                source, victim, false, "key-" + i);
            store.CommitResult(fx.Pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, source, victim, null, null));
            var deletion = new SyncAction("delete-" + i, 6, ActionKind.Delete, Side.B, path, null,
                null, victim, false, "key-" + i)
            {
                ProviderEffect = new("delete-job", "job-" + i, DateTimeOffset.UtcNow),
                DeletionSourceObjectId = source.ObjectId
            };
            await fx.A.DeleteAsync(deletion with { Target = Side.A, ExpectedTarget = source }, default);
            await fx.B.DeleteAsync(deletion, default);
            store.RecordIntent(fx.Pair.PairId, deletion);
        }
        var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(writer.Checks == count && store.UnresolvedIntents(fx.Pair.PairId).Count == 0 && report.Uncertain == 0,
            "all 600 saved delete jobs settle in one run, crossing both 256-entry page boundaries");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "reconciliation observes jobs without replaying deletes");
        Console.WriteLine($"PASS reconcile pages: {writer.Checks} saved jobs settled in one run");
        store.Dispose();
        Directory.Delete(folder, true);
        await ReconcilePendingPagesAsync();
        foreach (var anchorFirst in new[] { true, false })
        foreach (var completed in new[] { true, false })
            await ReconcileFolderPagesAsync(anchorFirst, completed);
        foreach (var completed in new[] { true, false })
            await ReconcileFolderPagesAsync(false, completed, selectedMemberOnly: true);
    }

    private static async Task ReconcilePendingPagesAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pending-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var writer = new ExcludedDeleteJobWriter(fx.WriterB) { Completed = true, PendingJobId = "pending-job" };
            fx.Provider.AddWriter(fx.Pair.B, writer);
            for (var i = 0; i < 600; i++)
            {
                var path = "pending-file-" + i;
                var victim = new ItemState(path, ItemKind.File, "target-" + i, "v1", new string('a', 64),
                    1, DateTimeOffset.UnixEpoch, ItemFlags.None);
                store.RecordIntent(fx.Pair.PairId, new("pending-action-" + i, 6, ActionKind.Delete, Side.B,
                    path, null, null, victim, false, null)
                {
                    DeletionSourceObjectId = "source-" + i,
                    ProviderEffect = new("delete-job", i % 2 == 0 ? "pending-job" : "complete-job", DateTimeOffset.UtcNow)
                });
            }
            var progress = new RecordingProgress();
            var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, progress, default);
            Require(writer.Checks == 600 && writer.PendingChecks == 1 && report.Uncertain == 300 &&
                store.UnresolvedIntents(fx.Pair.PairId).Count == 300,
                "one pending job is read once across pages, all 300 pending entries stay unresolved and counted");
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                progress.Reports.Any(p => p.Phase == RunPhases.Reconcile && p.Done == 600 && p.Total == 600),
                "pending reconciliation does not replay effects and progress reaches its real total");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task ReconcileFolderPagesAsync(bool anchorFirst, bool completed, bool selectedMemberOnly = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "folder-pages-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var writer = new ExcludedDeleteJobWriter(fx.WriterB) { Completed = completed };
            fx.Provider.AddWriter(fx.Pair.B, writer);
            var count = selectedMemberOnly ? 3 : 301;
            var actions = Enumerable.Range(0, count).Select(i => new SyncAction("tree-" + i, 6, ActionKind.Delete,
                Side.B, i == count - 1 ? "tree" : "tree/file-" + i, null, null,
                new ItemState(i == count - 1 ? "tree" : "tree/file-" + i, i == count - 1 ? ItemKind.Folder : ItemKind.File,
                    "tree-object-" + i, "v1", new string('a', 64), 1, DateTimeOffset.UnixEpoch, ItemFlags.None),
                false, null)).ToArray();
            var anchor = actions[^1];
            var checkpoint = System.Text.Json.JsonSerializer.Serialize(new
            {
                PairId = fx.Pair.PairId, ScanCursor = "cursor", Trees = new[] { new { Actions = actions } },
                ProviderState = "{}"
            });
            var pending = actions.Select(action => action with
            {
                ProviderEffect = action == anchor ? new("whole-folder-group", checkpoint) :
                    new("whole-folder-member", anchor.ActionId)
            });
            foreach (var action in anchorFirst ? pending.Reverse() : pending) store.RecordIntent(fx.Pair.PairId, action);
            if (selectedMemberOnly)
            {
                var selected = pending.First();
                using var log = RunLog.Open(folder, "pair", "recovery", false);
                await fx.Executor.ReconcileAsync([selected], 1, fx.WriterA, writer, default);
                log.Dispose();
                await log.Completion;
                Require(writer.TreeChecks == 1 && store.UnresolvedIntents(fx.Pair.PairId).Count == (completed ? 0 : count),
                    "one selected member checks the indivisible group, retaining every intent if recovery is uncertain");
                Require(File.ReadAllText(log.Path!).Contains("recovery group expanded: 2 extra paths", StringComparison.Ordinal),
                    "recovery expansion is logged separately with the extra path count");
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                    "group recovery does not replay ordinary effects");
                return;
            }
            var progress = new RecordingProgress();
            var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, progress, default);
            Require(writer.TreeChecks == 1 && store.UnresolvedIntents(fx.Pair.PairId).Count == (completed ? 0 : 301) &&
                report.Uncertain == (completed ? 0 : 301),
                "whole-folder anchor is handled once across pages, before or after members, terminal or uncertain");
            Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "folder-delete-history") == 1 &&
                progress.Reports.Any(p => p.Phase == RunPhases.Reconcile && p.Done == 301 && p.Total == 301),
                "cross-page group keeps one history notice and complete processed-row progress");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
