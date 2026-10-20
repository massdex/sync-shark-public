namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // RQ-034: a protected scan is not evidence of deletion or successful retry settlement.
    private static async Task HeldRetriesSurviveAsync()
    {
        foreach (var rootHold in new[] { true, false })
        {
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "held-retries-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var local = fx.A.Seed("held.txt", "surviving bytes");
            await FirstSyncAsync(fx.Run(), fx);
            var remote = await fx.B.ObserveAsync(local.Path, default);
            await fx.B.DeleteAsync(new("fixture-delete", 6, ActionKind.Delete, Side.B, local.Path, null, null, remote, false, null), default);
            store.HoldQuarantine(fx.Pair.A.RootId, local);
            store.AddNotices([new(fx.Pair.PairId, 30, NoticeKind.UserDecision, local.Path, "held", QuarantineFailure.NoticeKey,
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
