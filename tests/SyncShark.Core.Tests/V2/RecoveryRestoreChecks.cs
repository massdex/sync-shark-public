using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Registered by the coordinating test runner; no provider credentials or user paths.
    public static async Task RecoveryRestoreChecksAsync()
    {
        foreach (var mode in new[] { "dry", "identical", "conflict", "apply", "copy", "failure", "no-version" })
        {
            var root = LocalChecks.FreshRoot("recovery-page-" + mode + "-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archived-entity", "revision-1",
                DropboxContentHash.Compute(bytes), bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var removed = new SyncAction("retained-action", 0, ActionKind.Delete, Side.A, source.Path,
                null, null, source, true, "old-key");
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "fixture");
            store.CommitResult(fx.Pair.PairId, gen, removed, new ActionResult(removed.ActionId, Outcome.Done,
                null, null, new RecoveryRef(Side.A, "fixture-archive", source.VersionId, source.Hash!, source.Size), null));
            var recovery = store.RecoveryPage(fx.Pair.PairId, new RecoveryQuery()).Single();
            if (mode == "apply")
            {
                var opposite = fx.B.Seed(source.Path, "retained bytes");
                var stale = new SyncAction("stale-baseline", 0, ActionKind.Adopt, Side.A, source.Path,
                    null, opposite, source, false, "stale-key");
                store.CommitResult(fx.Pair.PairId, gen, stale,
                    new ActionResult(stale.ActionId, Outcome.Done, source, opposite, null, null));
            }
            if (mode is "identical" or "conflict" or "copy")
                fx.A.Seed(source.Path, mode == "identical" ? "retained bytes" : "current bytes");
            if (mode == "failure")
            {
                var second = removed with { ActionId = "second-retained-action", Path = "other.txt",
                    ExpectedTarget = source with { Path = "other.txt" } };
                store.CommitResult(fx.Pair.PairId, gen, second, new ActionResult(second.ActionId, Outcome.Done,
                    null, null, new RecoveryRef(Side.A, "second-archive", source.VersionId, source.Hash!, source.Size), null));
            }
            var archive = new RecoveryFixtureWriter(fx.WriterA, source, bytes)
                { FailOpen = mode == "failure", Missing = mode == "no-version" };
            fx.Provider.AddWriter(fx.Pair.A, archive);
            var provider = new RecoveryFixtureProvider(fx.Provider);
            var run = new SyncRun(store, fx.Executor, provider, Settings, TimeProvider.System);
            var dry = mode is "dry" or "identical" or "conflict";
            var noticesBefore = store.OpenNotices(fx.Pair.PairId).Count;
            var result = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId),
                mode == "copy", dry, CancellationToken.None)).Single();
            var expected = mode switch
            {
                "dry" => "RESTORE", "identical" => "SKIP-PRESENT", "conflict" => "CONFLICT",
                "failure" => "FAILED", "no-version" => "NO-VERSION", _ => "RESTORED"
            };
            Require(result.Classification == expected, "Recovery page classification: " + mode);
            if (dry)
            {
                Require(provider.WritePreparations == 0 && provider.ReadPreparations == 1 && archive.Opens == 0 &&
                    fx.WriterA.Calls == 0 && store.OpenNotices(fx.Pair.PairId).Count == noticesBefore &&
                    store.CountUnresolvedIntents(fx.Pair.PairId) == 0, "dry restore has no mutation or historical-byte download");
                continue;
            }
            Require(store.RecoveryRestoreProtected(fx.Pair.PairId, result.Destination ?? source.Path),
                "mutation retains durable next-run protection, including byte-read failures");
            if (mode == "no-version")
            {
                fx.A.Seed(source.Path, "retained bytes");
                store.MarkFirstSyncDone(fx.Pair.PairId);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(!store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                    await fx.B.ObserveAsync(source.Path, CancellationToken.None) is { Hash: var restoredHash } && restoredHash == source.Hash,
                    "actionless unavailable-version notice releases safely when identical bytes arrive");
                continue;
            }
            if (mode == "failure")
            {
                var batch = await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(), false, false, CancellationToken.None);
                Require(batch.Count == 2 && batch.Count(item => item.Classification == "FAILED") == 1 &&
                    batch.Count(item => item.Classification == "RESTORED") == 1, "one failed archive cannot stop its sibling restore");
                Require(store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Count == 2, "repeated failure deduplicates its notice");
                archive.FailOpen = false;
                store.MarkFirstSyncDone(fx.Pair.PairId);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(await fx.A.ObserveAsync(source.Path, CancellationToken.None) is not null &&
                    await fx.B.ObserveAsync(source.Path, CancellationToken.None) is not null,
                    "automatic archive retry creates and propagates without a manual Retry");
                continue;
            }
            Require(fx.WriterA.Preserves == 0, "restore is always create-only, never an overwrite");
            var again = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId),
                mode == "copy", false, CancellationToken.None)).Single();
            Require(again.Classification == "SKIP-PRESENT" && again.Destination == result.Destination && fx.WriterA.Puts == 1,
                "same recovery identity reuses one destination, including copy retries");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await fx.A.ObserveAsync(result.Destination!, CancellationToken.None) is { Hash: var hashA } &&
                await fx.B.ObserveAsync(result.Destination!, CancellationToken.None) is { Hash: var hashB } &&
                hashA == source.Hash && hashB == source.Hash && store.BaselinesAt(fx.Pair.PairId, Side.A, [result.Destination!]).Count == 1,
                "the following run adopts or propagates restored bytes, never deletes either side");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var oldDelete = new SyncAction("old-approved-delete", 0, ActionKind.Delete, Side.B, result.Destination!,
                null, null, await fx.B.ObserveAsync(result.Destination!, CancellationToken.None), true, "old-key");
            var adopt = oldDelete with { ActionId = "fresh-adopt", Kind = ActionKind.Adopt };
            var replay = run.GuardReviewedRecoveryRestores(fx.Pair, new Plan(fx.Pair.PairId, gen, [oldDelete, adopt], []));
            Require(replay.Actions.Count == 1 && replay.Actions[0].Kind == ActionKind.Adopt &&
                replay.SkippedActions.Single().ActionId == oldDelete.ActionId &&
                replay.Notices.Single().Detail.Contains("fresh incremental", StringComparison.Ordinal),
                "old reviewed deletion stays blocked after restore notice closes; fresh adoption remains available");
        }
        RecoveryPlanningSnapshotCheck();
        RecoveryDeletionEvidenceAttributionCheck();
        Require(SyncRun.RecoveryCopyPath("Folder/report.txt", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 2) ==
            "Folder/report (restored 2026-01-02) 2.txt", "bounded copy names preserve directory and extension");
    }

    private static void RecoveryDeletionEvidenceAttributionCheck()
    {
        foreach (var restoredSide in new[] { Side.A, Side.B })
        {
            var root = LocalChecks.FreshRoot("recovery-tombstone-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var source = new ItemState("restored.txt", ItemKind.File, "verified-restored-object", "restored-revision",
                DropboxContentHash.Compute(Encoding.UTF8.GetBytes("original")), 8, DateTimeOffset.UnixEpoch, ItemFlags.None);
            var action = new SyncAction(ShortId.Make("rrs", fx.Pair.PairId, "old-recovery"), 0, ActionKind.CopyNew,
                restoredSide, source.Path, null, source, null, false, null) { SuppressReconciledBaseline = true };
            store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, source.Path, "verified", SyncRun.RecoveryRestoreReady,
                ConflictOperation: new ConflictNoticeOperation("old-recovery", source.Path, [action], []))]);
            // Both sides were deleted after the archived mtime, with exactly the same bytes. Only one was restored.
            var row = new PlanRow(null, restoredSide == Side.A ? source : null, restoredSide == Side.B ? source : null,
                new Evidence(true, true, true, true, source.Mtime.AddDays(1), source.Mtime.AddDays(1), source.Hash, source.Hash));
            foreach (var firstSync in new[] { false, true })
            {
                var protectedRows = InterruptedDeleteRepair.Protect(new[] { row }, store, fx.Pair.PairId);
                var plan = Planner.Decide(fx.Pair.PairId, 1, protectedRows,
                    new(TimeSpan.Zero, firstSync, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10));
                Require(plan.Actions.All(item => item.Kind is not (ActionKind.Delete or ActionKind.Hold)) &&
                    plan.Actions.Any(item => item.Kind == ActionKind.CopyNew && item.Target != restoredSide),
                    "explicit old-mtime recovery overrides old identical tombstone only for its verified restored entity");
            }
            var unrelated = source with { ObjectId = "replacement-object" };
            var changedRow = row with { A = restoredSide == Side.A ? unrelated : null, B = restoredSide == Side.B ? unrelated : null };
            var unchangedEvidence = InterruptedDeleteRepair.Protect(new[] { changedRow }, store, fx.Pair.PairId)[0];
            Require(unchangedEvidence.Ev == changedRow.Ev, "restore attribution cannot bless an unrelated object at the same path");
            var scope = new RunScope.Scope(restoredSide == Side.A ? new[] { source } : [],
                restoredSide == Side.B ? new[] { source } : [], []);
            Require(InterruptedDeleteRepair.SourcePaths(scope, store, fx.Pair.PairId).Contains(source.Path),
                "first-sync deletion-history probes skip identity-bound explicit Recovery repairs");
        }
    }

    private static void RecoveryPlanningSnapshotCheck()
    {
        var root = LocalChecks.FreshRoot("recovery-snapshot-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var pending = new Notice(fx.Pair.PairId, 0, NoticeKind.UserDecision, "Folder/file.txt", "pending",
            SyncRun.RecoveryRestorePending, ConflictOperation: new ConflictNoticeOperation("snapshot-recovery", null, [], []));
        store.AddNotices([pending]);
        using (var snapshot = store.OpenPlanningProtection(fx.Pair))
        {
            Require(snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "folder/file.txt", false) &&
                snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "Folder", true) &&
                !snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "Folder-other", true),
                "snapshot restore guard uses indexed self/ancestor/descendant paths with separator boundaries");
            store.AddNotices([pending with { ReasonKey = SyncRun.RecoveryRestoreReady, Kind = NoticeKind.Info }]);
            Require(snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "Folder/file.txt", false) &&
                !store.RecoveryRestoreProtected(fx.Pair.PairId, "Folder/file.txt"),
                "both sides of one planning join keep the same protection despite a live ready transition");
        }
        using var fresh = store.OpenPlanningProtection(fx.Pair);
        Require(!fresh.RecoveryRestoreProtected(fx.Pair.PairId, "Folder/file.txt", false),
            "a later planning snapshot releases verified ready restores");
    }

    private sealed class RecoveryFixtureProvider(IEndpointProvider inner) : IEndpointProvider
    {
        public int WritePreparations { get; private set; }
        public int ReadPreparations { get; private set; }
        public IInventorySource Inventory(RootDef root) => inner.Inventory(root);
        public IEndpointWriter Writer(RootDef root) => inner.Writer(root);
        public Task PrepareAsync(PairDef pair, CancellationToken ct) { WritePreparations++; return Task.CompletedTask; }
        public Task PrepareRecoveryReadAsync(PairDef pair, CancellationToken ct) { ReadPreparations++; return Task.CompletedTask; }
    }

    private sealed class RecoveryFixtureWriter(IEndpointWriter inner, ItemState source, byte[] bytes) : IEndpointWriter, IRecoveryRecordReader
    {
        public bool FailOpen { get; set; }
        public bool Missing { get; init; }
        public int Opens { get; private set; }
        public Task<ItemState> DescribeRecoveryAsync(RecoveryRecord record, CancellationToken ct) => Missing
            ? throw new FileNotFoundException("fixture unavailable") : Task.FromResult(source with { Path = record.Path });
        public Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord record, CancellationToken ct)
        {
            Opens++;
            if (FailOpen && record.Path == "file.txt") throw new IOException("fixture read failure");
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            if (SyncRun.IsRecoveryRestoreIntent(action))
                Require(action.Kind == ActionKind.CopyNew && action.ExpectedTarget is null && action.SuppressReconciledBaseline,
                    "restore effect is create-only and cannot claim an archived opposite-side baseline");
            return inner.PutAsync(action, content, ct);
        }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => inner.PreserveAsync(victim, actionId, ct);
    }
}
