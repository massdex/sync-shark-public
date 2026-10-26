namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ScanBoundaryRetirementAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var pair = fx.Pair with { Excluded = [@"\Parent\Child\", ""],
            IgnoreRules = ["FolderName:.git", "FileName:ignored.txt", "FileExtension:.tmp"] };
        store.SavePair(pair);
        store.SavePair(pair with { PairId = "nested-pair", A = pair.A with
            { RootId = "nested-root", RootPath = Path.Combine(pair.A.RootPath, "Nested"), RootObjectId = "nested-source" },
            B = pair.B with { RootId = "nested-remote", RootPath = "/NestedDestination", RootObjectId = "nested-destination" } });
        Require(PairAdmission.DecideSaved(pair, store.Pairs()) is null,
            "nested fixture has distinct root identities and destinations, not an exact-root collision");
        // Adding a pair invalidates every cursor. Bootstrap after layout setup so this test
        // measures retirement during a delta, not a full walk that legitimately observes absence.
        await FirstSyncAsync(fx.Run(), fx);
        var fullReads = fx.A.FullCalls + fx.B.FullCalls;
        string[] filtered = ["Parent/Child/item.txt", "Parent/.git/config", "ignored.txt", "scratch.tmp",
            "Owned/item.txt", ".syncshark-control-test/item.txt", "Nested/item.txt"];
        foreach (var path in filtered)
            store.SaveSyncRetry(pair.PairId, new(path, "scan", 0, CachedIdentityRefresh.PendingReason,
                DateTimeOffset.MaxValue, ReadRoots: [pair.A.RootId]));
        const string sibling = "Parent/Other/x.txt";
        const string prefixSibling = "Parent/ChildX/x.txt";
        foreach (var path in new[] { sibling, prefixSibling })
            store.SaveSyncRetry(pair.PairId, new(path, "scan", 0, CachedIdentityRefresh.PendingReason,
                DateTimeOffset.MaxValue, ReadRoots: [pair.A.RootId]));
        var held = new SyncRetry("Parent/Child/held.txt", "scan", 1, CachedIdentityRefresh.OpenReason,
            DateTimeOffset.MaxValue, Held: new("Parent/Child/held.txt", Side.A), ReadRoots: [pair.A.RootId]);
        var reviewed = new SyncRetry("Parent/Child/reviewed.txt", "scan", 0, CachedIdentityRefresh.PendingReason,
            DateTimeOffset.MaxValue, ReviewedActions:
            [new SyncAction("reviewed", 2, ActionKind.Hold, Side.B, "Parent/Child/reviewed.txt", null, null, null, false, null)],
            ReadRoots: [pair.A.RootId]);
        store.SaveSyncRetry(pair.PairId, held);
        store.SaveSyncRetry(pair.PairId, reviewed);
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System, _ => ["Owned"]);
        Console.WriteLine($"Scan policy before: {string.Join("; ", store.ReadSyncRetries(pair.PairId).Select(r => r.Path + "=" + r.LastError))}");
        Console.WriteLine($"Scan policy siblings excluded: {PairInclude.NotSynced(pair, sibling, false, store.Pairs(), ["Owned"])}, {PairInclude.NotSynced(pair, prefixSibling, false, store.Pairs(), ["Owned"])}");
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Console.WriteLine($"Scan policy after: {string.Join("; ", store.ReadSyncRetries(pair.PairId).Select(r => r.Path + "=" + r.LastError))}");
        Require(fx.A.FullCalls + fx.B.FullCalls == fullReads, "retirement fixture uses retained delta cursors");
        Require(filtered.All(path => store.SyncRetryAt(pair.PairId, path) is null),
            "all permanent boundaries and ignore kinds retire even without Live rows");
        Require(store.SyncRetryAt(pair.PairId, sibling) is { } a && CachedIdentityRefresh.Pending(a) &&
            store.SyncRetryAt(pair.PairId, prefixSibling) is { } b && CachedIdentityRefresh.Pending(b),
            "included siblings survive the same run that retires excluded children");
        Require(store.SyncRetryAt(pair.PairId, held.Path)?.Held == held.Held &&
            store.SyncRetryAt(pair.PairId, reviewed.Path)?.ReviewedActions is { Count: 1 },
            "RQ-052: scan retirement retains held and reviewed deletion evidence through exclusion");
        Require(fx.A.WriteCalls == 0 && fx.B.WriteCalls == 0, "boundary retirement has no endpoint effects");
        Require(PairInclude.NotSynced(pair, "Parent/Child/x.txt", false) &&
            !PairInclude.NotSynced(pair, "Parent/Child/x.txt", false, rebuildable: true),
            "normalized exclusions share the retry policy while reviewed rebuildable removal keeps its carve-out");
    }

    // 2026-10-25 livelock: a busy store left the transient "reading" info notice close unadmitted past its
    // 5 s deadline, and the tripwire then failed a scan that had already completed. No journal cursor advanced,
    // so every retry replayed the same records. The close owns no safety authority: a miss must leave the notice
    // for the next scan and let the completed run commit its cursors. The seam waits on the cleanup's own
    // admission token, so each case is driven by the real cancellation outcome, never a fixed sleep.
    internal static async Task WaitNoticeCleanupTimeoutSurvivesAsync()
    {
        const string reason = "inventory-waiting";
        void WaitForAdmissionDeadline(CancellationToken deadline)
        {
            if (!deadline.CanBeCanceled) throw new InvalidOperationException("the seam did not receive an admission deadline");
            if (!deadline.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("the cleanup admission deadline did not fire");
        }
        void AddWaitNotices(IStateStore target, PairDef pair) => target.AddNotices([
            new Notice(pair.PairId, 40, NoticeKind.Info, pair.A.RootId, "Reading local", reason),
            new Notice(pair.PairId, 40, NoticeKind.Info, pair.B.RootId, "Reading cloud", reason)]);

        // (1) Admission never happens: the close cannot reach the store, so the completed scan must survive,
        // commit its cursors and leave both notices for the next scan.
        var blockedFolder = Path.Combine(Path.GetTempPath(), "wait-notice-blocked-" + Guid.NewGuid().ToString("N"));
        using (var store = SqliteStateStore.Open(blockedFolder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            AddWaitNotices(store, fx.Pair);
            FairStoreGate.BestEffortAdmissionForTest = WaitForAdmissionDeadline;
            try
            {
                var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(report.Outcome == RunOutcomes.Complete && report.Failed == 0,
                    "a wait-notice close that never reaches admission does not fail the completed run");
            }
            finally { FairStoreGate.BestEffortAdmissionForTest = null; }
            Require(store.UnresolvedNotices(fx.Pair.PairId).Count(n => n.ReasonKey == reason) == 2,
                "every unadmitted close leaves its info notice open for the next scan");
            Require(store.Cursor(fx.Pair.A.RootId) is not null && store.Cursor(fx.Pair.B.RootId) is not null,
                "the surviving run still commits both cursors instead of replaying the same records");
        }

        // (2) The live shape: admission succeeds and the close completes, then the post-check deadline expires.
        // The notice must be closed and the completed scan's cursor must advance to the newer increment.
        var completedFolder = Path.Combine(Path.GetTempPath(), "wait-notice-completed-" + Guid.NewGuid().ToString("N"));
        using (var store = SqliteStateStore.Open(completedFolder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var baseline = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(baseline.Outcome == RunOutcomes.Complete && store.Cursor(fx.Pair.A.RootId) == "0",
                "fixture commits the local journal cursor before the newer increment");
            fx.A.Seed("newer.txt", "newer");
            AddWaitNotices(store, fx.Pair);
            FairStoreGate.CleanupBodySettledForTest = (bestEffort, deadline) =>
            {
                if (bestEffort) WaitForAdmissionDeadline(deadline);
            };
            try
            {
                var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(report.Outcome == RunOutcomes.Complete && report.Failed == 0,
                    "a wait-notice close that completes after the admission deadline leaves the run complete");
            }
            finally { FairStoreGate.CleanupBodySettledForTest = null; }
            Require(store.Cursor(fx.Pair.A.RootId) == "1",
                "the completed scan advances the local journal cursor exactly to the newer increment");
            Require(!store.UnresolvedNotices(fx.Pair.PairId).Any(n => n.ReasonKey == reason),
                "the completed close retires the info notice instead of leaving it for another scan");
        }

        // (3) An ordinary effectful cleanup keeps the fail-the-run contract under the same expired post-check.
        var effectFolder = Path.Combine(Path.GetTempPath(), "wait-notice-effectful-" + Guid.NewGuid().ToString("N"));
        using (var store = SqliteStateStore.Open(effectFolder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            fx.A.Seed("effect.txt", "effect");
            FairStoreGate.CleanupBodySettledForTest = (bestEffort, deadline) =>
            {
                if (!bestEffort) WaitForAdmissionDeadline(deadline);
            };
            RunReport report;
            try
            {
                report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            }
            finally { FairStoreGate.CleanupBodySettledForTest = null; }
            // The shared failure owner turns the timeout into retained retry debt, so the run must not complete.
            Require(report.Outcome != RunOutcomes.Complete,
                $"an effectful cleanup that completes after the admission deadline still fails the run (outcome {report.Outcome})");
        }
    }

    internal static async Task LocalCatchUpNoticeLifecycleAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        ulong journal = 1;
        var fail = false;
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        {
            WalkCheckpointForTest = () => (new NtfsUsnCheckpoint(fail ? ++journal : journal, 0, 100, 0), false),
            JournalReadForTest = NoJournalChanges,
            GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => true)
        };
        fx.Provider.AddInventory(root, inventory);
        var snapshot = await inventory.FullAsync(store.BeginGeneration(root.RootId, "full"), _ => true, null!, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(snapshot.Gen, snapshot.CursorAfter, true);
        fail = true;
        string? noticeId = null;
        Notice? fallback = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            var notice = store.OpenNotices(pair.PairId).Single(n => n.ReasonKey == "local-walk-catch-up");
            Require(notice.Path == root.RootId && (noticeId is null || noticeId == notice.NoticeId),
                "repeated delta and post-walk fallbacks share one root notice");
            noticeId = notice.NoticeId;
            fallback = notice;
        }
        fail = false;
        // Exercise both successful catch-up and ordinary delta through the real Run entry point.
        store.ForgetCursor(root.RootId);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(store.NoticeById(pair.PairId, noticeId!) is { Closed: true }, "successful catch-up closes its root notice");
        // Reopen the same persisted episode to isolate the ordinary-delta close path.
        store.AddNotices([fallback!]);
        Require(store.NoticeById(pair.PairId, noticeId!) is { Closed: false }, "fixture reopened the same notice");
        inventory.FullInventoryForTest = () => throw new InvalidOperationException("successful delta must not walk");
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(store.NoticeById(pair.PairId, noticeId!) is { Closed: true }, "successful ordinary delta closes its root notice");
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(!store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "local-walk-catch-up"),
            "later successful delta does not resurrect the fallback notice");
    }
}
