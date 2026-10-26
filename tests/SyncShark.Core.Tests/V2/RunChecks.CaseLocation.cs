namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task CaseLocationAsync()
    {
        foreach (var unknown in new[] { false, true })
        {
            using var store = SqliteStateStore.Open(FixtureFolder("case-location"), Settings,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Parent", "", ItemKind.Folder);
            fx.B.Seed("Parent", "", ItemKind.Folder);
            for (var i = 0; i < 5; i++)
            {
                fx.A.Seed($"Parent/file{i}.txt", "equal bytes");
                fx.B.Seed($"Parent/file{i}.txt", "equal bytes");
            }
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await fx.A.MoveAsync(new("fixture-case", 25, ActionKind.Move, Side.A, "parent", "Parent",
                null, null, false, null), default);
            fx.A.InventoryWithoutHashes = unknown;
            fx.A.OmitVersionIds = true;
            if (unknown)
                for (var i = 0; i < 5; i++) fx.A.Unhashable.Add($"parent/file{i}.txt");
            var calls = fx.WriterA.Calls + fx.WriterB.Calls;
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(report.Moved == (unknown ? 0 : 1) && report.Uploaded == 0 && report.Downloaded == 0 && report.Deleted == 0 &&
                fx.WriterA.Calls + fx.WriterB.Calls == calls + (unknown ? 0 : 1),
                "the folder leaf rename dispatches exactly one Move and no separate child effects");
            if (unknown)
            {
                Require(store.ReadSyncRetries(fx.Pair.PairId).Any() && fx.A.HashedPaths.Count >= 5,
                    "unknown case-only content is verified and retained as retry debt when hashing fails");
                fx.A.Unhashable.Clear();
                foreach (var retry in store.ReadSyncRetries(fx.Pair.PairId))
                    store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
                report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            }
            Require(report.Failed == 0 && report.Moved == 1 && store.Baselines(fx.Pair.PairId).Count == 6 &&
                store.Baselines(fx.Pair.PairId).All(b => b.A.Path.StartsWith("parent", StringComparison.Ordinal)),
                "verified case-only rows adopt the actual endpoint spellings");
            Require(!store.UnresolvedIntents(fx.Pair.PairId).Any(), "case-only rows leave no failed Move intents");
        }
        await FolderSpellingChoicesAsync();
        await NonCaseFolderChoiceRefusedAsync();
        await StaleFolderIdentityRefusedAsync();
        await AncestorOnlyCaseLocationAsync();
        await AncestorDriftEditColdReopenAsync();
        Console.WriteLine("PASS CaseLocation: known and unknown content through SQLite and SyncRun");
    }

    private static async Task StaleFolderIdentityRefusedAsync()
    {
        foreach (var winner in new[] { Side.A, Side.B })
        {
            var fx = Build();
            fx.A.Seed("Parent", "", ItemKind.Folder);
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await fx.A.MoveAsync(new("rename-a", 25, ActionKind.Move, Side.A, "PARENT", "Parent", null, null, false, null), default);
            await fx.B.MoveAsync(new("rename-b", 25, ActionKind.Move, Side.B, "parent", "Parent", null, null, false, null), default);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.Rule == 20);
            var snapshot = (await run.ObserveDecisionsAsync(fx.Pair.PairId, [(notice.NoticeId!, notice.Path)], default)).Single();
            var loser = winner == Side.A ? fx.B : fx.A;
            var path = winner == Side.A ? "parent" : "PARENT";
            await loser.MoveAsync(new("retain-original", 25, ActionKind.Move, winner == Side.A ? Side.B : Side.A,
                "Retained", path, null, null, false, null), default);
            var replacement = loser.Seed(path, "", ItemKind.Folder);
            var calls = fx.WriterA.Calls + fx.WriterB.Calls;
            var refused = false;
            try { await run.ResolveDecisionAsync(fx.Pair.PairId, notice.Path, snapshot.Token, winner, new RecordingProgress(), default); }
            catch (SyncRunRefusedException error) { refused = error.Message.Contains("identity or exact spelling changed", StringComparison.Ordinal); }
            Require(refused && fx.WriterA.Calls + fx.WriterB.Calls == calls &&
                (await loser.ObserveAsync(path, default))?.ObjectId == replacement.ObjectId &&
                await loser.ObserveAsync("Retained", default) is not null,
                "stale folder identity cannot transfer spelling authority onto a replacement");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "stale-spelling") &&
                !fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == notice.NoticeId),
                "obsolete identity authority is replaced by a visible refresh notice");
        }
    }

    private static async Task NonCaseFolderChoiceRefusedAsync()
    {
        var fx = Build();
        fx.A.Seed("Parent", "", ItemKind.Folder);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 5, NoticeKind.UserDecision, "Parent", "Choose a side", "needs-decision")]);
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.Kind == NoticeKind.UserDecision);
        var snapshot = (await run.ObserveDecisionsAsync(fx.Pair.PairId, [(notice.NoticeId!, notice.Path)], default)).Single();
        var calls = fx.WriterA.Calls + fx.WriterB.Calls;
        var refused = false;
        try { await run.ResolveDecisionAsync(fx.Pair.PairId, notice.Path, snapshot.Token, Side.A, new RecordingProgress(), default); }
        catch (SyncRunRefusedException error) { refused = error.Message == "folder decisions can't be resolved yet - decide the files inside, or use a per-path choice"; }
        Require(refused && fx.WriterA.Calls + fx.WriterB.Calls == calls,
            "non-case folder choices remain refused without effects");
    }

    private static async Task FolderSpellingChoicesAsync()
    {
        foreach (var winner in new[] { Side.A, Side.B })
        foreach (var both in new[] { false, true })
        foreach (var stale in both ? new[] { false, true } : new[] { false })
        {
            using var store = SqliteStateStore.Open(FixtureFolder("folder-spelling"), Settings,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("Parent", "", ItemKind.Folder);
            fx.A.Seed("Parent/child.txt", "child bytes");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var childA = await fx.A.ObserveAsync("Parent/child.txt", default);
            var childB = await fx.B.ObserveAsync("Parent/child.txt", default);
            foreach (var side in new[] { Side.A, Side.B })
            {
                if (!both && side != winner) continue;
                var endpoint = side == Side.A ? fx.A : fx.B;
                await endpoint.MoveAsync(new("folder-rename", 25, ActionKind.Move, side,
                    side == Side.A ? "PARENT" : "parent", "Parent", null, null, false, null), default);
            }
            var calls = fx.WriterA.Calls + fx.WriterB.Calls;
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            if (both)
            {
                // Work arriving below a held folder belongs to AutoSync, not the folder spelling choice.
                childA = fx.A.Seed("PARENT/child.txt", "queued child edit");
                var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Rule == 20 && n.Kind == NoticeKind.UserDecision);
                var snapshot = (await run.ObserveDecisionsAsync(fx.Pair.PairId, [(notice.NoticeId!, notice.Path)], default)).Single();
                if (stale)
                {
                    var loser = winner == Side.A ? fx.B : fx.A;
                    await loser.MoveAsync(new("stale-spelling", 25, ActionKind.Move, winner == Side.A ? Side.B : Side.A,
                        "pArEnT", winner == Side.A ? "parent" : "PARENT", null, null, false, null), default);
                    var refused = false;
                    try { await run.ResolveDecisionAsync(fx.Pair.PairId, notice.Path, snapshot.Token, winner, new RecordingProgress(), default); }
                    catch (SyncRunRefusedException error) { refused = error.Message.Contains("exact spelling changed", StringComparison.Ordinal); }
                    Require(refused && (await loser.ObserveAsync("pArEnT", default))?.Path == "pArEnT",
                        "stale spelling choice refuses without a move");
                    notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.Rule == 20 && n.Kind == NoticeKind.UserDecision);
                    snapshot = (await run.ObserveDecisionsAsync(fx.Pair.PairId, [(notice.NoticeId!, notice.Path)], default)).Single();
                }
                report = await run.ResolveDecisionAsync(fx.Pair.PairId, notice.Path, snapshot.Token, winner,
                    new RecordingProgress(), default);
                Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == notice.NoticeId),
                    "folder spelling choice closes only after convergence");
            }
            var chosen = winner == Side.A ? "PARENT" : "parent";
            Require(report.Moved == 1 && report.Deleted == 0 && report.KeptBoth == 0 &&
                fx.WriterA.Calls + fx.WriterB.Calls == calls + 1 &&
                (await fx.A.ObserveAsync(chosen, default))?.Path == chosen &&
                (await fx.B.ObserveAsync(chosen, default))?.Path == chosen,
                "baseline rename or explicit folder choice performs one move in either direction");
            Require((await fx.A.ObserveAsync(chosen + "/child.txt", default))?.ObjectId == childA!.ObjectId &&
                (await fx.B.ObserveAsync(chosen + "/child.txt", default))?.ObjectId == childB!.ObjectId &&
                (await fx.A.ObserveAsync(chosen + "/child.txt", default))?.Hash == childA.Hash &&
                (await fx.B.ObserveAsync(chosen + "/child.txt", default))?.Hash == childB.Hash,
                "folder spelling changes preserve child identities without child moves or keep-both");
            var followup = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(followup.Moved == 0 && followup.KeptBoth == 0 && followup.Deleted == 0,
                "ancestor-only child drift remains quiet after a folder spelling choice");
        }
    }

    private static async Task AncestorDriftEditColdReopenAsync()
    {
        foreach (var driftSide in new[] { Side.A, Side.B })
        {
            var folder = FixtureFolder("drift-edit-cold");
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            const string path = "Parent/capsules/work/item.json";
            const string driftPath = "Parent/Capsules/work/item.json";
            fx.A.Seed(path, "original");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var key = store.Baselines(fx.Pair.PairId).Single().ObjectKey;
            var drifted = driftSide == Side.A ? fx.A : fx.B;
            var edited = driftSide == Side.A ? fx.B : fx.A;
            await drifted.MoveAsync(new("ancestor-drift", 20, ActionKind.Move, driftSide, driftPath, path,
                null, null, false, null), default);
            var content = edited.Seed(path, "updated bytes");
            var first = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(first.Done == 1 && first.Uploaded + first.Downloaded == 1 && first.KeptBoth == 0 && first.Deleted == 0 &&
                first.Failed == 0 && (await drifted.ObserveAsync(driftPath, default))?.Hash == content.Hash,
                $"live-shaped ancestor drift produces only the opposite edit, no Forget/split: {first}");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            store.Dispose();
            using var reopened = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var restarted = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, Settings, TimeProvider.System);
            var third = await restarted.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var baseline = reopened.Baselines(fx.Pair.PairId).Single();
            Require(third.Done == 0 && third.Outcome == RunOutcomes.Complete && baseline.ObjectKey == key &&
                (driftSide == Side.A ? baseline.A.Path : baseline.B.Path) == driftPath &&
                reopened.OpenNotices(fx.Pair.PairId).Count == 0 && !reopened.ReadSyncRetries(fx.Pair.PairId).Any(),
                $"cold third run is quiet with the original correspondence and actual spelling: {third}");
        }
        Console.WriteLine("PASS AncestorDriftEditColdReopen");
    }

    private static async Task AncestorOnlyCaseLocationAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("ancestor-case-location"), Settings,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Parent", "", ItemKind.Folder);
        fx.B.Seed("Parent", "", ItemKind.Folder);
        fx.A.Seed("Parent/file.txt", "equal bytes");
        fx.B.Seed("Parent/file.txt", "equal bytes");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        // Only the child's observed ancestor spelling changes. The folder's own name and
        // baseline stay unchanged, as with provider listings using different ancestor casing.
        await fx.A.MoveAsync(new("fixture-ancestor", 20, ActionKind.Move, Side.A, "parent/file.txt",
            "Parent/file.txt", null, null, false, null), default);
        var calls = fx.WriterA.Calls + fx.WriterB.Calls;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Moved == 0 && report.Uploaded == 0 && report.Downloaded == 0 && report.Deleted == 0 &&
            fx.WriterA.Calls + fx.WriterB.Calls == calls,
            "ancestor-only spelling with an unchanged parent dispatches no provider effects");
    }

    private static async Task CaseSensitiveRenameAndCreateAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("case-sensitive-create"), Settings,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store, caseSensitiveA: true);
        var original = fx.A.Seed("Doc.txt", "original bytes");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await fx.A.MoveAsync(new("fixture-case", 9, ActionKind.Move, Side.A, "doc.txt", "Doc.txt",
            null, original, false, null), default);
        var added = fx.A.Seed("Doc.txt", "new distinct bytes");
        Require(added.ObjectId != original.ObjectId, "case-sensitive folder holds two distinct file identities");
        for (var turn = 0; turn < 2; turn++)
        {
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var renamed = await fx.A.ObserveAsync("doc.txt", default);
            var created = await fx.A.ObserveAsync("Doc.txt", default);
            var remote = await fx.B.ObserveAsync("Doc.txt", default);
            Require(report.Deleted == 0 && renamed?.ObjectId == original.ObjectId && renamed.Hash == original.Hash &&
                created?.ObjectId == added.ObjectId && created.Hash == added.Hash && remote?.Hash == original.Hash,
                $"case rename plus same-name create retains both local files and the occupied remote: {report}");
            Require(store.OpenNotices(fx.Pair.PairId).Any(), "case-colliding new file remains visible for resolution");
        }
        Console.WriteLine("PASS CaseSensitiveRenameAndCreate: distinct identities and occupied remote preserved");
    }

    private static async Task MoveEditedBaselineProofAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("move-edit-proof"), Settings,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("before.txt", "old bytes");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var baseline = store.Baselines(fx.Pair.PairId).Single();
        Require(baseline.A.Hash is not null && baseline.B.Hash == baseline.A.Hash,
            "committed SQLite file baselines retain the shared verified hash used by rule 28");
        await fx.A.MoveAsync(new("fixture-move", 28, ActionKind.Move, Side.A, "after.txt", "before.txt",
            null, null, false, null), default);
        fx.A.Seed("after.txt", "edited bytes");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Failed == 0 && report.Moved == 1 &&
            !store.OpenNotices(fx.Pair.PairId).Any(n => n.Kind == NoticeKind.KeepBoth) &&
            (await fx.B.ObserveAsync("after.txt", default))?.Hash == Hash("edited bytes") &&
            await fx.B.ObserveAsync("before.txt", default) is null,
            $"rule 28 completes its move and dependent update without a false conflict: {report}");
        Console.WriteLine("PASS MoveEditedBaselineProof: verified baseline admits dependent update");
    }

    private static async Task MoveSettlementReportAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("move-settlement"), Settings,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("before.txt", "same bytes");
        fx.B.Seed("before.txt", "same bytes");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await fx.A.MoveAsync(new("fixture-move", 9, ActionKind.Move, Side.A, "after.txt", "before.txt",
            null, null, false, null), default);
        fx.A.InventoryWithoutHashes = true;
        fx.A.OmitVersionIds = true;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Failed == 1 && report.Moved == 0 && report.Failures.Any(f => f.Outcome == Outcome.Failed),
            $"normalized failed move must be reported as failed, not moved: failed={report.Failed}, moved={report.Moved}");
        Require(store.StreamUnresolvedIntentRows(fx.Pair.PairId).Single().State == "failed" &&
            store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry") &&
            store.SyncRetryAt(fx.Pair.PairId, "after.txt") is { Attempts: > 0, RetryAfter: var due } && due > DateTimeOffset.UtcNow,
            "failed move retains its intent, notice and existing backoff");
        var summary = store.ReadRunHistory(fx.Pair.PairId, 1).Single();
        Require(summary.Failed == report.Failed && summary.Moved == 0,
            "durable run summary preserves the normalized failure count");
        var calls = fx.WriterB.Calls;
        var intent = store.UnresolvedIntents(fx.Pair.PairId).Single().ActionId;
        var retryBefore = store.SyncRetryAt(fx.Pair.PairId, "after.txt")!;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterB.Calls == calls && store.UnresolvedIntents(fx.Pair.PairId).Single().ActionId == intent &&
            store.SyncRetryAt(fx.Pair.PairId, "after.txt")!.Attempts == retryBefore.Attempts,
            "an immediate follow-up retains the failed move and attempt count during backoff");
        fx.A.InventoryWithoutHashes = false;
        store.SaveSyncRetry(fx.Pair.PairId, retryBefore with { RetryAfter = DateTimeOffset.MinValue });
        var recovered = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(recovered.Failed == 0 && fx.WriterB.Calls == calls && !store.UnresolvedIntents(fx.Pair.PairId).Any() &&
            store.SyncRetryAt(fx.Pair.PairId, "after.txt") is null &&
            await fx.B.ObserveAsync("before.txt", default) is null &&
            await fx.B.ObserveAsync("after.txt", default) is not null,
            "after backoff, available proof settles the completed move without replay or duplicate files");
        Console.WriteLine("PASS MoveSettlementReport: normalized outcome, notice, retry backoff and convergence");
    }
}
