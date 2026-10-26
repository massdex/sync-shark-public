using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    public static async Task RunIncludePreviewChecksAsync()
    {
        await IncludeResetsOnlyNewScopesAsync();
        await IncludeDuringPartialFirstSyncAsync();
        await IncludePreviewCannotWriteStoreAsync();
        await IncludeRetainsVerificationOnlyIntentsAsync();
        await IncludeHoldsAndOrdinaryRulesAsync();
        await Cli_RetainedNoticeChoicesAsync();
        IncludePagesAndPersistsIntents();
        var localRoot = TempRoot();
        try { await IncludePreviewChecks.LocalScopeAsync(Path.Combine(localRoot, "local-scope")); }
        finally { Cleanup(localRoot); }
    }

    internal static async Task IncludeDuringPartialFirstSyncAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(store: store);
            // Production local full scans have no provider revision-history API after history is forgotten.
            fx.A.ReportsDeletionHistory = false;
            var a = fx.A.Seed("again/survivor.txt", "same");
            var b = fx.B.Seed("again/survivor.txt", "same");
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "run");
            var adopt = new SyncAction("partial-adopt", 2, ActionKind.Adopt, Side.B, a.Path, null, a, b, false, "partial");
            store.CommitResult(fx.Pair.PairId, gen, adopt, new ActionResult(adopt.ActionId, Outcome.Done, a, b, null, null));
            Require(!store.FirstSyncDone(fx.Pair.PairId) && store.Baselines(fx.Pair.PairId).Count == 1,
                "partial first sync has committed history but is not complete");
            var runtime = Runtime(fx, root);
            await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "exclude", CancellationToken.None);
            await fx.A.DeleteAsync(new SyncAction("outside-delete", 0, ActionKind.Delete, Side.A,
                a.Path, null, null, a, false, null), CancellationToken.None);
            store.StoreDeletionEvidence(fx.Pair.PairId, fx.Pair.A.RootId,
                [new DeletionEvidenceRecord(a.Path, DateTimeOffset.UtcNow)]);
            await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "include", CancellationToken.None);
            Require(store.Baselines(fx.Pair.PairId).Count == 0 &&
                store.DeletionEvidenceRows(fx.Pair.PairId, fx.Pair.A.RootId).Count == 0,
                "partial-first-sync include forgets subtree baselines and deletion evidence (decision 81)");
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null, _ => { }, CancellationToken.None);
            Require((await fx.A.ObserveAsync(a.Path, CancellationToken.None))?.Hash == b.Hash &&
                (await fx.B.ObserveAsync(b.Path, CancellationToken.None))?.Hash == b.Hash &&
                fx.WriterA.Deletes + fx.WriterB.Deletes == 0,
                "partial-first-sync reinclude copies excluded deletion back instead of propagating it");
        }
        finally { Cleanup(root); }
    }

    private static async Task IncludeResetsOnlyNewScopesAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(store: store);
            foreach (var path in new[] { "again/survivor.txt", "again/clash.txt", "again/cloud-newer.txt", "outside.txt" })
            {
                fx.A.Seed(path, "same");
                fx.B.Seed(path, "same");
            }
            await FirstSyncAsync(fx);
            Require(store.FirstSyncDone(fx.Pair.PairId), "include fixture has completed first sync");
            var runtime = Runtime(fx, root);
            await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "exclude", CancellationToken.None);
            var before = store.Baselines(fx.Pair.PairId).Count;
            var deleted = await fx.A.ObserveAsync("again/survivor.txt", CancellationToken.None);
            await fx.A.DeleteAsync(new SyncAction("outside-delete", 0, ActionKind.Delete, Side.A,
                "again/survivor.txt", null, null, deleted, false, null), CancellationToken.None);
            var clock = DateTimeOffset.UtcNow.AddDays(-2);
            var localWinner = fx.A.Seed("again/clash.txt", "local edited", mtime: clock.AddMinutes(1));
            fx.B.Seed("again/clash.txt", "cloud edited differently", mtime: clock);
            fx.A.Seed("again/cloud-newer.txt", "older local", mtime: clock);
            var cloudWinner = fx.B.Seed("again/cloud-newer.txt", "newer cloud", mtime: clock.AddMinutes(1));
            store.StoreDeletionEvidence(fx.Pair.PairId, fx.Pair.A.RootId,
                [new DeletionEvidenceRecord("again/survivor.txt", DateTimeOffset.UtcNow),
                 new DeletionEvidenceRecord("outside-evidence.txt", DateTimeOffset.UtcNow)]);
            await runtime.SetSelectionAsync(fx.Pair.PairId, "AGAIN", "include", CancellationToken.None);
            Require(before == 4 && store.Baselines(fx.Pair.PairId).Count == 1 &&
                store.Baselines(fx.Pair.PairId)[0].A.Path == "outside.txt",
                "include forgets only newly included subtree baselines, ignoring case");
            Require(store.DeletionEvidenceRows(fx.Pair.PairId, fx.Pair.A.RootId).Select(row => row.Path)
                .SequenceEqual(["outside-evidence.txt"]), "include clears only scoped stale deletion evidence");
            Require(store.Cursor(fx.Pair.A.RootId) is null && store.Cursor(fx.Pair.B.RootId) is null &&
                store.FirstSyncDone(fx.Pair.PairId), "include resets coverage without reopening first sync");
            var deletes = fx.WriterA.Deletes + fx.WriterB.Deletes;
            var report = await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require(await fx.A.ObserveAsync("again/survivor.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("again/survivor.txt", CancellationToken.None) is not null &&
                fx.WriterA.Deletes + fx.WriterB.Deletes == deletes,
                "next run restores survivor, never propagates deletion made while excluded");
            Require(report.Conflicts == 0 &&
                (await fx.B.ObserveAsync("again/clash.txt", CancellationToken.None))!.Hash == localWinner.Hash &&
                (await fx.A.ObserveAsync("again/cloud-newer.txt", CancellationToken.None))!.Hash == cloudWinner.Hash,
                "reinclude uses newest-wins in both directions, not keep-both or blocking conflicts");
            Require(fx.WriterA.Preserves == 1 && fx.WriterB.Preserves == 1 &&
                store.Recoveries(fx.Pair.PairId).Count == 2,
                "each overwritten older version is preserved by its endpoint before newest-wins");
            var stale = store.Pair(fx.Pair.PairId)! with { Excluded = ["again"] };
            var rejected = false;
            try { store.SavePairIncluded(stale, stale with { Excluded = [] }); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.Baselines(fx.Pair.PairId).Count > 1,
                "stale include transaction leaves newly formed baselines intact");
        }
        finally { Cleanup(root); }
    }

    private static async Task IncludePreviewCannotWriteStoreAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        await FirstSyncAsync(fx);
        fx.Store.SavePair(fx.Pair with { Excluded = ["again"] });
        var readOnly = DispatchProxy.Create<IStateStore, PreviewReadOnlyStore>();
        ((PreviewReadOnlyStore)(object)readOnly).Inner = fx.Store;
        var runtime = new V2CliRuntime(null, readOnly, fx.Run(), new EndpointRootProbe(fx.Provider), "unused-preview-root");
        var expected = new SyncCliIncludePreviewData(fx.Pair.PairId, "again", true, 1, 0,
            new(1, 0, 0, 0, 0, 0), [], [], ["Read-only; selection unchanged."]);
        runtime.IncludePreviewReader = (pair, path, ct) =>
        {
            Require(pair.Excluded.SequenceEqual(["again"]) && path == "again",
                "preview passes original selection and exact scope to inventory-only reader");
            return Task.FromResult(expected);
        };
        var result = await DispatchAsync(runtime,
            ["--cli", "folders", "preview-include", "--pair", fx.Pair.PairId, "--path", "again"]);
        Require(result is SyncCliIncludePreviewData actual && actual.Summary == expected.Summary &&
            actual.Warnings.Any(w => w.Contains("copied back", StringComparison.Ordinal)),
            "read-only preview dispatch needs no --yes and returns scoped summary plus history warning");
        Require(fx.Store.Pair(fx.Pair.PairId)!.Excluded.SequenceEqual(["again"]),
            "preview leaves saved selection unchanged");
    }

    private static async Task IncludeRetainsVerificationOnlyIntentsAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(store: store);
            var local = fx.A.Seed("again/x", "same");
            var remote = fx.B.Seed("again/x", "same");
            await FirstSyncAsync(fx);
            var runtime = Runtime(fx, root);
            await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "exclude", CancellationToken.None);
            var delete = new SyncAction("old-delete", 6, ActionKind.Delete, Side.B, "again/x", null,
                null, remote, false, "old-delete-key");
            var upload = new SyncAction("uncertain-upload", 18, ActionKind.CopyNew, Side.B, "again/x", null,
                local, null, false, "old-upload-key");
            store.RecordIntent(fx.Pair.PairId, delete);
            store.RecordIntent(fx.Pair.PairId, upload);
            store.CommitResult(fx.Pair.PairId, 0, upload, new ActionResult(upload.ActionId, Outcome.Uncertain,
                null, null, null, "commit response lost"));
            await fx.A.DeleteAsync(new SyncAction("source-deleted", 0, ActionKind.Delete, Side.A, "again/x",
                null, null, local, false, null), CancellationToken.None);
            var selection = await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "include", CancellationToken.None);
            Require(selection.Warnings.Any(w => w.Contains("2 retained intents", StringComparison.Ordinal)) &&
                store.UnresolvedIntents(fx.Pair.PairId).Count == 2,
                "include reports and retains started-zero and uncertain intents for verification");
            var calls = fx.WriterA.Calls + fx.WriterB.Calls;
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "include-reconcile-test");
            var executor = new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            // Pass stale in-memory actions from before include: durable metadata must win.
            var results = await executor.ReconcileAsync([delete, upload], gen,
                fx.WriterA, fx.WriterB, CancellationToken.None);
            Require(results.Single(r => r.ActionId == delete.ActionId).Outcome == Outcome.Skipped &&
                await fx.B.ObserveAsync("again/x", CancellationToken.None) is not null,
                "old pending delete is verified not applied, never replayed after include");
            Require(results.Single(r => r.ActionId == upload.ActionId).Outcome == Outcome.Done &&
                fx.WriterA.Calls + fx.WriterB.Calls == calls,
                "uncertain already-landed upload verifies without another write");
            Require(store.Baselines(fx.Pair.PairId).Count == 0,
                "verified pre-include upload cannot recreate a stale baseline from its deleted source");
            var outsideSource = fx.A.Seed("outside-intent", "outside");
            var outsideTarget = fx.B.Seed("outside-intent", "outside");
            var outside = new SyncAction("outside-intent", 18, ActionKind.CopyNew, Side.B, "outside-intent", null,
                outsideSource, null, false, "outside-key");
            store.RecordIntent(fx.Pair.PairId, outside);
            await executor.ReconcileAsync([outside], gen, fx.WriterA, fx.WriterB, CancellationToken.None);
            Require(store.Baselines(fx.Pair.PairId).Single().ObjectKey == "outside-key",
                "unmarked outside intent retains ordinary baseline reconciliation");
            var deletes = fx.WriterA.Deletes + fx.WriterB.Deletes;
            await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require(await fx.A.ObserveAsync("again/x", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("again/x", CancellationToken.None) is not null &&
                fx.WriterA.Deletes + fx.WriterB.Deletes == deletes,
                "next run restores deleted local source instead of deleting verified Dropbox upload");
        }
        finally { Cleanup(root); }
    }

    private static async Task IncludeHoldsAndOrdinaryRulesAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(store: store);
            var time = DateTimeOffset.UtcNow.AddDays(-2);
            foreach (var path in new[] { "again/tie", "again/unknown", "baseline.txt" })
            {
                fx.A.Seed(path, "original", mtime: time);
                fx.B.Seed(path, "original", mtime: time);
            }
            await FirstSyncAsync(fx);
            var runtime = Runtime(fx, root);
            await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "exclude", CancellationToken.None);
            var tieA = fx.A.Seed("again/tie", "tie A", mtime: time);
            var tieB = fx.B.Seed("again/tie", "tie B", mtime: time.AddSeconds(2));
            var unknownA = fx.A.Seed("again/unknown", "unknown A", mtime: DateTimeOffset.MinValue);
            var unknownB = fx.B.Seed("again/unknown", "known B", mtime: time.AddDays(1));
            // A changed baseline wins even with an older timestamp; this is not no-baseline newest-wins.
            var baselineWinner = fx.A.Seed("baseline.txt", "changed but older timestamp", mtime: time.AddDays(-1));
            fx.A.Seed("outside-new.txt", "outside A", mtime: time);
            var outsideWinner = fx.B.Seed("outside-new.txt", "outside B", mtime: time.AddMinutes(1));
            await runtime.SetSelectionAsync(fx.Pair.PairId, "again", "include", CancellationToken.None);
            await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require((await fx.A.ObserveAsync("again/tie", CancellationToken.None))!.Hash == tieA.Hash &&
                (await fx.B.ObserveAsync("again/tie", CancellationToken.None))!.Hash == tieB.Hash &&
                (await fx.A.ObserveAsync("again/unknown", CancellationToken.None))!.Hash == unknownA.Hash &&
                (await fx.B.ObserveAsync("again/unknown", CancellationToken.None))!.Hash == unknownB.Hash,
                "reinclude tie boundary and unknown time hold both original versions without choosing a winner");
            Require((await fx.B.ObserveAsync("baseline.txt", CancellationToken.None))!.Hash == baselineWinner.Hash,
                "existing baseline propagation takes precedence over timestamp age");
            Require((await fx.A.ObserveAsync("outside-new.txt", CancellationToken.None))!.Hash == outsideWinner.Hash &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.Kind == NoticeKind.KeepBoth && n.Path == "outside-new.txt"),
                "outside no-baseline differences also use global newest-wins, not keep-both");
            await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require((await fx.A.ObserveAsync("again/tie", CancellationToken.None))!.Hash == tieA.Hash &&
                (await fx.B.ObserveAsync("again/tie", CancellationToken.None))!.Hash == tieB.Hash &&
                (await fx.A.ObserveAsync("again/unknown", CancellationToken.None))!.Hash == unknownA.Hash &&
                (await fx.B.ObserveAsync("again/unknown", CancellationToken.None))!.Hash == unknownB.Hash &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.Kind == NoticeKind.KeepBoth && n.Path.StartsWith("again/")),
                "persisted held decisions survive the next run without reverting to keep-both");
        }
        finally { Cleanup(root); }
    }

    private static void IncludePagesAndPersistsIntents()
    {
        var root = TempRoot();
        const int affected = 300;
        try
        {
            using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                var fx = Build(store: store);
                var excluded = fx.Pair with { Excluded = ["again"] };
                store.SavePair(excluded);
                for (var i = 0; i < affected; i++)
                    store.RecordIntent(fx.Pair.PairId, new SyncAction($"pending-{i:D4}", 18,
                        ActionKind.CopyNew, Side.B, $"again/{i}", null, null, null, false, $"key-{i}"));
                store.RecordIntent(fx.Pair.PairId, new SyncAction("outside", 18, ActionKind.CopyNew,
                    Side.B, "outside", null, null, null, false, "outside-key"));
                Require(store.SavePairIncluded(excluded, excluded with { Excluded = [] }) == affected,
                    "include flags all 300 affected intents exactly once across the 256-row page boundary");
                var pending = store.UnresolvedIntents(fx.Pair.PairId);
                Require(pending.Count == affected + 1 && pending.Count(a => a.SuppressReconciledBaseline) == affected &&
                    !pending.Single(a => a.ActionId == "outside").SuppressReconciledBaseline,
                    "paged include preserves every intent and never marks the outside intent");
                var included = store.Pair(fx.Pair.PairId)!;
                Require(store.SavePairIncluded(included, included) == 0,
                    "repeating an include with no new scope never recounts retained intents");
            }
            using (var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                Require(reopened.UnresolvedIntents("p1").Count(a => a.SuppressReconciledBaseline) == affected,
                    "verification-only intent flags persist across SQLite reopen");
            }
        }
        finally { Cleanup(root); }
    }

    public class PreviewReadOnlyStore : DispatchProxy
    {
        public IStateStore Inner { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name is not (nameof(IStateStore.Pair) or nameof(IStateStore.FirstSyncDone)))
                throw new InvalidOperationException("Preview attempted a store write or unexpected access: " + targetMethod?.Name);
            return targetMethod.Invoke(Inner, args);
        }
    }
}
