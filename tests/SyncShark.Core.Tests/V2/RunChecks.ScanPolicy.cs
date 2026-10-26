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
    // for the next scan and let the completed run commit its cursors.
    internal static async Task WaitNoticeCleanupTimeoutSurvivesAsync()
    {
        const string reason = "inventory-waiting";
        var folder = Path.Combine(Path.GetTempPath(), "wait-notice-cleanup-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var gate = (FairStoreGate)typeof(SqliteStateStore)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(store)!;
        store.AddNotices([
            new Notice(fx.Pair.PairId, 40, NoticeKind.Info, fx.Pair.A.RootId, "Reading local", reason),
            new Notice(fx.Pair.PairId, 40, NoticeKind.Info, fx.Pair.B.RootId, "Reading cloud", reason)]);
        var settled = 0;
        using var bothSettled = new ManualResetEventSlim();
        using var gateHeld = new ManualResetEventSlim();
        // Both side scans reach the wait-notice close only after their inventory is stored. Hold the gate from
        // then on, so it is the close's admission - not a fixture sleep - that outlasts the deadline.
        FairStoreGate.BeforeAdmissionForTest = () =>
        {
            if (Interlocked.Increment(ref settled) == 2) bothSettled.Set();
            if (!gateHeld.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("the fixture gate holder did not take the store");
        };
        var holder = Task.Run(() =>
        {
            if (!bothSettled.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("both scan sides did not reach the wait-notice close");
            using (gate.Enter())
            {
                gateHeld.Set();
                // Bounded: outlast the helper's 5 s admission deadline, then release for the completion steps.
                using var release = new ManualResetEventSlim();
                release.Wait(TimeSpan.FromSeconds(6));
            }
        });
        RunReport? report = null;
        try
        {
            report = await new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System)
                .IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        }
        finally
        {
            FairStoreGate.BeforeAdmissionForTest = null;
            await holder.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Require(report!.Outcome == RunOutcomes.Complete && report.Failed == 0,
            "a completed scan survives a wait-notice close that misses its admission deadline");
        Require(store.Cursor(fx.Pair.A.RootId) is not null && store.Cursor(fx.Pair.B.RootId) is not null,
            "the surviving run commits both sides' cursors instead of replaying the same records");
        Require(store.UnresolvedNotices(fx.Pair.PairId).Count(n => n.ReasonKey == reason) == 2,
            "the missed close leaves the transient info notices for the next scan");
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
