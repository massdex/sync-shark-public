namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // RQ-034: a protected scan is not evidence of deletion or successful retry settlement.
    private static async Task HeldRetriesSurviveAsync()
    {
        foreach (var mode in new[] { "root", "quarantine", "both" })
        {
            var rootHold = mode != "quarantine";
            var quarantine = mode != "root";
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "held-retries-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var local = fx.A.Seed("held.txt", "surviving bytes");
            await FirstSyncAsync(fx.Run(), fx);
            var remote = await fx.B.ObserveAsync(local.Path, default);
            await fx.B.DeleteAsync(new("fixture-delete", 6, ActionKind.Delete, Side.B, local.Path, null, null, remote, false, null), default);
            if (quarantine) store.HoldQuarantine(fx.Pair.A.RootId, local);
            if (quarantine) store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.UserDecision, local.Path, "held", QuarantineFailure.NoticeKey,
                Quarantine: new(Side.A, null, local, true))]);
            fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
                { Deleted = DateTimeOffset.UtcNow, ContentHash = local.Hash });
            var paths = rootHold ? new[] { "", local.Path } : new[] { local.Path };
            var evidence = paths.Select(path => new DeletionEvidenceRecord(path, null,
                Failure: new(3, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 1, false))).ToArray();
            store.StoreDeletionEvidence(fx.Pair.PairId, fx.Pair.B.RootId, evidence);
            foreach (var path in paths)
            {
                store.SaveSyncRetry(fx.Pair.PairId, new(path, "deletion-evidence", 3, "history unavailable",
                    DateTimeOffset.MinValue, Held: new(path, Side.A)));
                store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, path, "retry remains open", "sync-retry")]);
            }
            var notices = store.OpenNotices(fx.Pair.PairId).Where(n => n.ReasonKey == "sync-retry").Select(n => n.NoticeId).ToArray();
            for (var pass = 0; pass < 2; pass++)
            {
                await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(paths.All(path => store.SyncRetryAt(fx.Pair.PairId, path) is { Kind: "deletion-evidence", Held: not null }),
                    "due root and quarantined sibling deletion retries survive repeated runs");
                Require(evidence.All(row => store.DeletionEvidenceRows(fx.Pair.PairId, fx.Pair.B.RootId).Contains(row)),
                    "held retries keep their exact deletion evidence");
                Require(notices.All(id => store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == id)),
                    "failure notices remain open without successful evaluation");
                Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                    !store.UnresolvedIntents(fx.Pair.PairId).Any(a => a.Kind == ActionKind.Delete),
                    "held root and sibling cannot plan or apply a delete");
            }
        }
        Console.WriteLine("PASS HeldRetriesSurvive");
    }

    private static async Task HeldRetryRotationAndReleaseAsync()
    {
        var clock = new CompletionClock();
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "retry-rotation-" + Guid.NewGuid().ToString("N")), Settings, clock);
        var fx = Build(suppliedStore: store);
        await FirstSyncAsync(fx.Run(), fx);
        store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.UserDecision, "held", "hold", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, null, true))]);
        for (var i = 0; i < 300; i++)
        {
            var path = $"held/{i:D4}.txt";
            store.SaveSyncRetry(fx.Pair.PairId, new(path, "sync", 3, "unchanged error", DateTimeOffset.MinValue));
        }
        store.SaveSyncRetry(fx.Pair.PairId, new("ready.txt", "sync", 1, "ordinary debt", DateTimeOffset.MinValue.AddSeconds(1)));
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.SyncRetryAt(fx.Pair.PairId, "ready.txt") is not null, "ordinary debt starts behind the bounded held page");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.SyncRetryAt(fx.Pair.PairId, "ready.txt") is null &&
            store.ReadSyncRetries(fx.Pair.PairId).All(r => r.Attempts == 3 && r.LastError == "unchanged error" && r.RetryAfter > clock.Now),
            "held rows rotate without an attempt or error rewrite and ordinary debt settles");

        var retryPath = "held/0000.txt";
        store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, retryPath, "retained failure", "sync-retry")]);
        var noticeId = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "sync-retry").NoticeId;
        foreach (var notice in store.OpenNotices(fx.Pair.PairId).Where(n => n.Quarantine is not null))
            store.CloseNotice(notice.NoticeId!, "released");
        clock.Now = clock.Now.AddMinutes(6);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.SyncRetryAt(fx.Pair.PairId, retryPath) is null && !store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == noticeId),
            "after release successful evaluation closes the retained failure notice");
    }

    private static async Task HeldRetryExclusionAndContainersAsync()
    {
        foreach (var scenario in new[] { "excluded", "container", "rebuildable" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "retry-scope-" + Guid.NewGuid().ToString("N")), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            await FirstSyncAsync(fx.Run(), fx);
            var path = scenario == "rebuildable" ? "junction/gone" : "folder";
            var evidence = new DeletionEvidenceRecord(path, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
            if (scenario == "rebuildable")
            {
                fx.A.Seed("junction", "", ItemKind.Folder);
                fx.Provider.AddInventory(fx.Pair.A, new ScriptedInventory(fx.A) { LinkPath = "junction" });
            }
            else store.SavePair(fx.Pair with { Excluded = [scenario == "excluded" ? path : path + "/child"] });
            if (scenario == "excluded")
                store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.UserDecision, path, "hold", QuarantineFailure.NoticeKey,
                    Quarantine: new(Side.A, null, null, true))]);
            IReadOnlyList<SyncAction>? reviewed = scenario == "rebuildable"
                ? [new SyncAction("reviewed", 2, ActionKind.Hold, Side.B, path, null, null, null, false, null) { RebuildableRemove = true }]
                : null;
            HeldDeletion? held = scenario == "excluded" ? new(path, Side.A) : null;
            if (scenario == "excluded")
            {
                fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B));
                store.StoreDeletionEvidence(fx.Pair.PairId, fx.Pair.B.RootId, [evidence]);
            }
            store.SaveSyncRetry(fx.Pair.PairId, new(path, "sync", 1, "pending", DateTimeOffset.MinValue,
                Held: held, ReviewedActions: reviewed));
            store.AddNotices([new(fx.Pair.PairId, 40, NoticeKind.Info, path, "pending", "sync-retry")]);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            if (scenario == "excluded")
            {
                // RQ-052: an excluded hold is suspended, not settled. Its provenance and the exact stored
                // deletion evidence are retained while excluded; re-inclusion reprobes from fresh evidence.
                var suspended = store.SyncRetryAt(fx.Pair.PairId, path);
                Require(suspended is { Kind: "deletion-evidence", ReprobeEvidence: true, RetryAfter: var dormantDue } &&
                    suspended.Held == held && dormantDue == DateTimeOffset.MaxValue,
                    "excluded hold is suspended with its provenance and no due time");
                var rows = store.DeletionEvidenceRows(fx.Pair.PairId, fx.Pair.B.RootId);
                Require(rows.Count == 1 && rows[0] == evidence, "excluded hold keeps its exact deletion evidence");
                Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry"),
                    "exclusion closes the retry notice without resolving the hold");
                Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
                    "excluded hold cannot execute a delete");
                await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                var again = store.SyncRetryAt(fx.Pair.PairId, path);
                var againRows = store.DeletionEvidenceRows(fx.Pair.PairId, fx.Pair.B.RootId);
                Require(again == suspended && againRows.Count == 1 && againRows[0] == evidence &&
                    !store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry") &&
                    fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
                    "repeated excluded run keeps the same suspended hold, evidence and closed notice without a delete");
                var current = store.Pair(fx.Pair.PairId)!;
                store.SavePairIncluded(current, current with { Excluded = [] });
                var restored = store.SyncRetryAt(fx.Pair.PairId, path);
                Require(restored is { Kind: "deletion-evidence", ReprobeEvidence: true, RetryAfter: var restoredDue } &&
                    restored.Held == held && restoredDue == DateTimeOffset.MinValue,
                    "re-inclusion restores the same hold as an immediately due re-probe");
                Require(store.DueSyncRetries(fx.Pair.PairId, DateTimeOffset.UtcNow, 256).Any(row => row.Path == path),
                    "restored hold appears on the due retry page");
                continue;
            }
            Require(store.SyncRetryAt(fx.Pair.PairId, path) is null &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry"),
                scenario + ": real exclusion or successful absence evaluation retires retry and notice");
            Require(store.DeletionEvidenceRows(fx.Pair.PairId, fx.Pair.B.RootId).Count == 0 &&
                fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
                "true exclusion retires held evidence without executing a delete");
        }
    }

    private static async Task HeldIntentsDoNotStarveAsync()
    {
        foreach (var quarantine in new[] { true, false })
        {
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "held-intents-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var sources = Enumerable.Range(0, 300).Select(i => fx.A.Seed($"held/{i:D4}.txt", "held content")).ToArray();
            store.WriteSnapshot(new(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "seed"), "", sources, []));
            foreach (var source in sources)
            {
                if (quarantine) store.HoldQuarantine(fx.Pair.A.RootId, source);
                store.RecordIntent(fx.Pair.PairId, new("held-" + source.Path, 2, ActionKind.CopyNew, Side.B,
                    source.Path, null, source, null, false, null));
            }
            // A typed subtree hold covers protected but non-quarantined intent rows too.
            if (!quarantine)
                store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.UserDecision, "held", "subtree hold", QuarantineFailure.NoticeKey,
                    Quarantine: new(Side.A, null, sources[0], true))]);
            var ready = fx.A.Seed("ready.txt", "ready content");
            store.RecordIntent(fx.Pair.PairId, new("ready", 2, ActionKind.CopyNew, Side.B, ready.Path, null, ready, null, false, null));
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(store.IntentById(fx.Pair.PairId, "ready") is null && await fx.B.ObserveAsync(ready.Path, default) is not null,
                "more than 256 held intents cannot starve a dispatchable sibling");
            Require(store.UnresolvedIntents(fx.Pair.PairId).Count == sources.Length &&
                sources.All(source => store.IntentById(fx.Pair.PairId, "held-" + source.Path) is not null),
                "held intents are retained, not settled as excluded");
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && fx.WriterB.Puts == 1,
                "only the independent sibling is written");
        }
        Console.WriteLine("PASS HeldIntentsDoNotStarve");
    }
}
