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
        await AncestorOnlyCaseLocationAsync();
        Console.WriteLine("PASS CaseLocation: known and unknown content through SQLite and SyncRun");
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
