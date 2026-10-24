namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ScanBoundaryRetirementAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        await FirstSyncAsync(fx.Run(), fx);
        var pair = fx.Pair with { Excluded = [@"\Parent\Child\", ""],
            IgnoreRules = ["FolderName:.git", "FileName:ignored.txt", "FileExtension:.tmp"] };
        store.SavePair(pair);
        store.SavePair(pair with { PairId = "nested-pair", A = pair.A with
            { RootId = "nested-root", RootPath = Path.Combine(pair.A.RootPath, "Nested") } });
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
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
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
