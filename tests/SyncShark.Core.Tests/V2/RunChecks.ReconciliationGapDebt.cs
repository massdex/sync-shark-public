namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationUnreadableFolder()
    {
        await ReconciliationUnreadableFolder(false);
        await ReconciliationUnreadableFolder(true);
    }

    private static async Task ReconciliationUnreadableFolder(bool legacyItems)
    {
        var clock = new LocalChecks.FixedClock(DateTimeOffset.UtcNow);
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "reconcile-unreadable-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, clock, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Denied/held.txt", "original");
        fx.A.Seed("Readable/changed.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("Readable/changed.txt", "changed");
        var inventory = new ScriptedInventory(fx.A)
        {
            Unreadable = ["Denied"],
            UnreadableReasons = new Dictionary<string, string> { ["Denied"] = "Access denied while enumerating folder" }
        };
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(fx.Pair.A.RootId, new NtfsUsnCheckpoint(2, 0, 200, 0).Cursor, 100, false, protect: false);
        var held = store.Live(fx.Pair.A.RootId, ["Denied/held.txt"]).Single();
        if (legacyItems) gaps.GapDiscoveredBatch(fx.Pair.A.RootId, gaps.GapEpoch(fx.Pair.A.RootId)!, [(held, true)]);
        var logFolder = FixtureFolder("gap-transfer-log");
        RunLog? log = null;
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock,
            beginRunLog: (pairId, command) => log = RunLog.Open(logFolder, pairId, command, false));
        var writes = fx.WriterB.Puts;
        var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
            StateMonitor.FromGap(gaps.GapEpoch(fx.Pair.A.RootId)!) with { RootId = fx.Pair.A.RootId },
            new RecordingProgress(), default);
        await log!.Completion;
        var completionLog = File.ReadAllText(log.Path!);
        Require(completionLog.Contains($"{(legacyItems ? 1 : 0)} transferred to unreadable-folder retries", StringComparison.Ordinal) &&
            completionLog.Contains("epoch rows updated=1, remaining items=0", StringComparison.Ordinal),
            "completion reports actual transfer and epoch update counts");
        Require(gaps.GapObligations(fx.Pair.A.RootId, [held.ObjectId]).Count == 0,
            "unreadable legacy item transfers out of epoch debt while its rehash and scan owners remain");
        Require(store.CompleteGapInventory(fx.Pair.A.RootId, "not-current-epoch") == 0,
            "a stale completion reports zero updated rows");
        var retry = store.SyncRetryAt(fx.Pair.PairId, "Denied");
        Require(!report.CompletionFailed && fx.WriterB.Puts == writes + 1,
            "one unreadable folder does not prevent the same run repairing the readable change");
        Require(retry is { Kind: "scan" } && retry.RetryAfter > clock.GetUtcNow() &&
            retry.LastError.Contains("Access denied", StringComparison.Ordinal) &&
            store.UnresolvedNotices(fx.Pair.PairId).Any(notice => notice.Path == "Denied" &&
                notice.Detail.Contains("Access denied", StringComparison.OrdinalIgnoreCase)),
            "unreadable folder retains a specific visible reason and the scan owner's backoff");
        var walks = fx.A.FullCalls;
        Require(!store.PendingGapInventory(fx.Pair.A.RootId) &&
            store.ProtectionBatch(fx.Pair.A.RootId, ItemFlags.NeedsRehash, ["Denied/held.txt"])[0].Covered &&
            !await run.MonitorAsync(fx.Pair.PairId, new RecordingProgress(), default) && fx.A.FullCalls == walks,
            "readable remainder clears the epoch while unreadable proof and scan retry prevent premature rewalking");
        gaps.BeginGap(fx.Pair.A.RootId, new NtfsUsnCheckpoint(3, 0, 300, 0).Cursor, 200, false, protect: false);
        var interrupted = gaps.GapEpoch(fx.Pair.A.RootId)!;
        inventory.FullError = new IOException("root enumeration interrupted");
        report = await run.ReconcileScopeAsync(fx.Pair.PairId,
            StateMonitor.FromGap(interrupted) with { RootId = fx.Pair.A.RootId }, new RecordingProgress(), default);
        Require(store.PendingGapInventory(fx.Pair.A.RootId) && gaps.GapEpoch(fx.Pair.A.RootId)!.Epoch == interrupted.Epoch &&
            store.Cursor(fx.Pair.A.RootId) == interrupted.JournalCursor && report.Deferred > 0 &&
            store.SyncRetryAt(fx.Pair.PairId, "") is { Kind: "scan" },
            "root-scope read failure retains the exact pending epoch checkpoint and full obligation with a Partial retry");
    }

    private static Task ReconciliationClearsGapDebt() => ReconcileGapDebt(false);
    private static Task ReconciliationClearsLegacyEpoch() => ReconcileGapDebt(true);

    private static async Task ReconcileGapDebt(bool legacy)
    {
        var folder = FixtureFolder("reconcile-gap-debt");
        using var store = SqliteStateStore.Open(folder,
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.ContentProofStore = store;
        var items = Enumerable.Range(0, 50).Select(i => fx.A.Seed($"Parent/file-{i:D2}.txt", "original")).ToArray();
        await FirstSyncAsync(fx.Run(), fx);
        var proofTrace = TraceRehashProofs(folder, store, fx, items.Take(3).Select(item => item.ObjectId).ToHashSet());
        var gaps = (ILocalGapStore)store;
        var root = fx.Pair.A.RootId;
        gaps.BeginGap(root, new NtfsUsnCheckpoint(2, 0, 200, 0).Cursor, 100, false, protect: legacy);
        var epoch = gaps.GapEpoch(root)!;
        if (legacy)
        {
            gaps.GapDiscoveredBatch(root, epoch, items.Select(item => (item, true)).ToArray());
            Require(gaps.HasGapProtection(root) && gaps.GapObligations(root, items.Select(item => item.ObjectId).ToArray()).Count > 0,
                "legacy fixture retains both discovery protection and concrete gap debt");
        }
        for (var i = 0; i < 3; i++) fx.A.Seed(items[i].Path, "changed content");
        var writes = fx.WriterB.Calls;
        fx.A.HashedPaths.Clear(); fx.B.HashedPaths.Clear();
        var run = new SyncRun(store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(5, 64) }, TimeProvider.System);
        var report = await run.ReconcileScopeAsync(fx.Pair.PairId,
            StateMonitor.FromGap(epoch) with { RootId = root }, new RecordingProgress(), default);
        Require(!report.CompletionFailed && report.Failed == 0 && fx.WriterB.Calls - writes == 3,
            "one gap reconciliation uploads exactly the three changed files");
        Require(!epoch.TrustFileUsn, "50-file fixture uses an untrusted epoch");
        Require(items.All(item => fx.A.HashedPaths.Contains(item.Path)),
            "every retained file was hashed: missing=" + string.Join(",", items.Where(item => !fx.A.HashedPaths.Contains(item.Path)).Select(item => item.Path)));
        Require(fx.B.HashedPaths.Count == 0, "trusted counterpart has zero hash requests: actual=" + fx.B.HashedPaths.Count);
        Require(store.RehashPendingCount(root) == 0, "all 50 files have zero remaining proof debt: " + RehashDiagnostics(folder, fx.Pair) + " | " + string.Join(" | ", proofTrace));
        Require(report.Deferred == 0, "all 50 files settle within one run with zero deferred work");
        var remaining = ReconciliationCompletionDiagnostics(folder, fx.Pair);
        Require(!store.PendingGapInventory(root), "reconciliation clears pending inventory: " + remaining);
        Require(!gaps.HasGapProtection(root), "reconciliation clears legacy frontier protection: " + remaining);
        Require(gaps.GapObligations(root, items.Select(item => item.ObjectId).ToArray()).Count == 0,
            "reconciliation clears concrete gap items: " + remaining);
        Require(store.PlanningBacklogPage(fx.Pair.PairId).Count == 0,
            "reconciliation clears observation planning debt: " + remaining);
        await MirrorAsync(fx);
    }
}
