namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    // Decisions 54/71, RQ-043: exercise the production persistence adapter, not only Planner.Decide.
    private static async Task RunSqlitePrepareApplyChecksAsync()
    {
        foreach (var change in new[] { "none", "bytes", "unreviewed-child" })
            await SqliteReviewedChoiceAsync(change);
        foreach (var deletedSide in new[] { Side.A, Side.B })
        foreach (var changed in new[] { false, true })
            await SqliteMatchedSurvivorAsync(deletedSide, changed);
        await SqliteUnreviewedEvidenceAsync();
    }

    private static async Task SqliteReviewedChoiceAsync(string change)
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            fx.A.Seed("a", "", ItemKind.Folder);
            fx.A.Seed("a/x.txt", "hi");
            const string choices = """{"oneSided":[{"prefix":"a/","side":"Local","action":"remove"}]}""";
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var header = store.ReadPrepare(fx.Pair.PairId);
            var prepared = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            Require(header is not null && prepared.Database is not null &&
                FirstSyncChoicesState.ReviewedDeleteKeys(prepared).Contains("A:a/x.txt"),
                "SQLite prepare persists the collapsed folder's reviewed child");
            if (change == "bytes") fx.A.Seed("a/x.txt", "much longer after review");
            // No extra file or bytes: only the reviewed-victim guard can reject this growth.
            if (change == "unreviewed-child") fx.A.Seed("a/new", "", ItemKind.Folder);
            if (change != "none")
            {
                var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, default);
                Require(result.FirstSyncComplete && result.Failed == 0 &&
                    fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "reviewed-folder-grew"),
                    "SQLite grown folder choice completes with a decision notice and zero effects: " + change);
                Require(await fx.A.ObserveAsync("a/x.txt", CancellationToken.None) is not null &&
                    await fx.A.ObserveAsync("a", default) is not null &&
                    (change != "unreviewed-child" || await fx.A.ObserveAsync("a/new", default) is not null),
                    "the entire grown SQLite folder choice survives, including the new child");
            }
            else
            {
                var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null,
                    _ => { }, CancellationToken.None);
                Require(result.Failed == 0 && result.FirstSyncComplete &&
                    await fx.A.ObserveAsync("a/x.txt", CancellationToken.None) is null &&
                    await fx.A.ObserveAsync("a", CancellationToken.None) is null,
                    "SQLite reviewed collapsed delete applies when its victims are unchanged");
            }
        }
        finally { Cleanup(root); }
    }

    private static async Task SqliteMatchedSurvivorAsync(Side deletedSide, bool changed)
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            // Production local items have no revision ID; prepare must retain the historical content hash.
            // A synthetic v: stamp cannot authorize a later local deletion (nor prove edit-vs-delete).
            fx.A.OmitVersionIds = true;
            fx.A.Seed("d/x.txt", "same");
            fx.B.Seed("d/x.txt", "same");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            var prepared = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            Require(prepared.Database?.MatchLookup.ContainsKey("D/X.TXT") == true,
                "actual CLI prepare persists SQL matched stamps before any apply");
            Require(prepared.Database!.MatchLookup["D/X.TXT"].A.Contains("|h:", StringComparison.Ordinal),
                "SQLite prepare retains a real local historical hash rather than a fake revision stamp");
            var removed = deletedSide == Side.A ? fx.A : fx.B;
            var survivor = deletedSide == Side.A ? fx.B : fx.A;
            var old = await removed.ObserveAsync("d/x.txt", CancellationToken.None);
            await removed.DeleteAsync(new SyncAction("seed-delete", 0, ActionKind.Delete, deletedSide,
                "d/x.txt", null, null, old, false, null), CancellationToken.None);
            if (changed) survivor.Seed("d/x.txt", "edited after prepare");
            var expected = await survivor.ObserveAsync("d/x.txt", CancellationToken.None)
                ?? throw new InvalidOperationException("The survivor fixture must exist before apply.");
            // Reconstruct the runtime so apply must reload the persisted review.
            var result = await Runtime(fx, root).ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null,
                _ => { }, CancellationToken.None);
            Require(await survivor.ObserveAsync("d/x.txt", default) is not null &&
                fx.WriterA.Deletes + fx.WriterB.Deletes == 0 && fx.WriterA.Puts + fx.WriterB.Puts == 0,
                "SQLite first-sync apply defers post-preview changes without deleting or copying the survivor");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            var a = await fx.A.ObserveAsync("d/x.txt", CancellationToken.None);
            var b = await fx.B.ObserveAsync("d/x.txt", CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                $"SQLite matched apply completes: deleted={deletedSide}, changed={changed}");
            Require(changed ? a?.Hash == expected.Hash && b?.Hash == expected.Hash : a is null && b is null,
                "SQL matched stamps propagate unchanged deletion but copy changed survivor bytes back");
            Require(changed ? fx.WriterA.Deletes + fx.WriterB.Deletes == 0 :
                fx.WriterA.Puts + fx.WriterB.Puts == 0,
                "SQLite matched decision does not execute the opposite destructive/copy effect");
        }
        finally { Cleanup(root); }
    }

    private static async Task SqliteUnreviewedEvidenceAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            var local = fx.A.Seed("d/x.txt", "local only");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, EmptyChoices);
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!.Database is not null,
                "unreviewed evidence fixture uses the SQL review adapter");
            fx.B.Seed("ordinary.txt", "unrelated");
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B)
                { ResolveDeleted = local.Mtime.AddHours(1), ContentHash = local.Hash });
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, EmptyChoices, false, null,
                _ => { }, CancellationToken.None);
            Require(result.UnreviewedPending == 1 && result.FirstSyncComplete && result.Failed == 0 &&
                fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0 && fx.WriterB.Calls == 0 &&
                (await fx.A.ObserveAsync("d/x.txt", CancellationToken.None))?.Hash == local.Hash,
                "SQL reviewed set holds a newly evidenced victim without deleting or preserving it");
            Require(await fx.A.ObserveAsync("ordinary.txt", CancellationToken.None) is not null,
                "unrelated work proceeds while SQL-unreviewed deletion waits");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Calls == 0 &&
                (await fx.A.ObserveAsync("d/x.txt", default))?.Hash == local.Hash &&
                store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "d/x.txt" && notice.ReasonKey == "deletion-review"),
                "SQL unreviewed evidence stays inert in ordinary sync until an explicit decision");
        }
        finally { Cleanup(root); }
    }
}
