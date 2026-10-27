namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task CaseConflictColdReopenAsync()
    {
        foreach (var scenario in new[] { "between-legs", "before-recovery", "after-recovery", "original-name" })
        {
            using var h = new Harness();
            var directory = Path.Combine(_root, "case-conflict-" + scenario);
            var pair = new PairDef("case-conflict", "Case conflict", new RootDef("case-a", "Fake", "account", "/A", "id-a"), Root(), [], [], true);
            var a = new FakeEndpoint(Side.A, pair.A.RootId) { DropboxHashes = true };
            var source = a.Seed("doc.txt", "content");
            h.Wire.SeedFile("Doc.txt", "content");
            var writer = h.Writer();
            var original = (await writer.ObserveAsync("Doc.txt", CancellationToken.None))!;
            var action = new SyncAction("case-conflict-intent", 20, ActionKind.Move, Side.B, "doc.txt", "Doc.txt",
                source, original, false, "case-key");
            var state = Path.Combine(directory, "state");
            using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                store.SavePair(pair);
                var basisSource = source with { Path = "Doc.txt" };
                var adopt = new SyncAction("seed-case", 1, ActionKind.Adopt, Side.B, "Doc.txt", null,
                    basisSource, original, false, "case-key");
                store.RecordIntent(pair.PairId, adopt);
                store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, basisSource, original, null, null));
                Require(store.Baselines(pair.PairId, ["case-key"]).SingleOrDefault() is { } baseline &&
                    baseline.A.ObjectId == basisSource.ObjectId && baseline.B.ObjectId == original.ObjectId &&
                    Content.SameContent(baseline.A, baseline.B) == true && store.UnresolvedIntents(pair.PairId).Count == 0,
                    "case conflict starts with a content-verified baseline and a settled adoption");
                store.RecordIntent(pair.PairId, action);
                var previous = ProviderEffectContext.Current.Value;
                using var crash = new CancellationTokenSource();
                try
                {
                    ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
                    {
                        action = action with { ProviderEffect = checkpoint,
                            ExpectedTarget = checkpoint.RetainedTarget ?? action.ExpectedTarget };
                        store.RecordIntent(pair.PairId, action);
                        using var json = System.Text.Json.JsonDocument.Parse(checkpoint.Id);
                        var step = json.RootElement.GetProperty("Step").GetInt32();
                        if (step == 2) h.Wire.SeedFile("doc.txt", "occupant");
                        if (step == 3 && scenario == "before-recovery")
                            throw new OperationCanceledException("crash after recovery checkpoint");
                        if (step == 3 && scenario == "after-recovery" &&
                            json.RootElement.GetProperty("Retained").GetString() != "Doc.txt")
                            h.Wire.BeforeMetadataResponse = path =>
                            {
                                if (!path.EndsWith("/doc.txt", StringComparison.Ordinal)) return;
                                h.Wire.BeforeMetadataResponse = null;
                                crash.Cancel();
                                crash.Token.ThrowIfCancellationRequested();
                            };
                        return Task.CompletedTask;
                    });
                    try { await writer.MoveAsync(action, CancellationToken.None); }
                    catch (UncertainEffectException) { }
                    if (scenario is "before-recovery" or "after-recovery")
                    {
                        var interrupted = false;
                        try { await writer.ResolveProviderEffectAsync(action, null, crash.Token); }
                        catch (UncertainEffectException) { interrupted = true; }
                        catch (OperationCanceledException) when (crash.IsCancellationRequested) { interrupted = true; }
                        Require(interrupted, "the scripted crash interrupts provider reconciliation at its requested boundary");
                    }
                }
                finally { ProviderEffectContext.Current.Value = previous; }
            }
            using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var executor = new Executor(reopened, StorageChecks.Fast, ExecutorChecks.Admission(),
                isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            if (scenario == "original-name")
                h.Wire.BeforeMetadataResponse = path =>
                {
                    if (!path.EndsWith("/Doc.txt", StringComparison.Ordinal)) return;
                    h.Wire.BeforeMetadataResponse = null;
                    h.Wire.MoveOutsideRoot("doc.txt", "/Elsewhere/occupant.txt");
                };
            var pending = reopened.UnresolvedIntents(pair.PairId);
            Require(pending.Count == 1, "cold reopen retains the interrupted provider intent");
            var results = await executor.ReconcileAsync(pending, 2, a, writer, CancellationToken.None);
            Require(results.Single().Outcome == (scenario == "original-name" ? Outcome.Done : Outcome.Failed),
                "visible recovery is a known failed rename, not an indefinitely uncertain effect");
            var located = await h.Client.FindIdentityForMoveAsync(original.ObjectId, CancellationToken.None);
            Require(located.Entry is not null && !LocalItems.IsArtifactPath(located.Entry.RelativePath) &&
                located.Entry.ContentHash == original.Hash, "incoming identity and bytes survive at a visible sibling");
            if (scenario == "original-name")
            {
                Require(located.Entry!.RelativePath == "doc.txt" && reopened.UnresolvedIntents(pair.PairId).Count == 0,
                    "original rollback retries the case spelling when the occupant has gone");
                Require(reopened.OpenNotices(pair.PairId).Count == 0, "a disappeared occupant leaves no stale decision notice");
                continue;
            }
            var retained = reopened.UnresolvedIntents(pair.PairId).Single();
            Require(retained.ExpectedTarget!.Path == located.Entry!.RelativePath &&
                retained.FromPath == "Doc.txt" && retained.Path == "doc.txt", "failed intent protects all original and retained paths");
            Require((await writer.ObserveAsync("doc.txt", CancellationToken.None))!.ObjectId != original.ObjectId,
                "unrelated destination occupant survives unchanged");
            Require(!h.Wire.Calls.Any(call => call.Contains("delete", StringComparison.Ordinal)), "recovery never deletes");
            await CaseConflictRetryLifecycleAsync(reopened, pair, a, writer, h, original);
        }
    }

    private static async Task CaseConflictRetryLifecycleAsync(SqliteStateStore store, PairDef pair, FakeEndpoint a,
        DropboxWriter writer, Harness h, ItemState original)
    {
        var provider = new FakeEndpointProvider().Add(pair.A, a).AddWriter(pair.B, writer)
            .AddInventory(pair.B, new DropboxInventory(pair.B, Side.B, h.Client, store, pair.PairId));
        store.MarkFirstSyncDone(pair.PairId);
        var run = new SyncRun(store, new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(),
            isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            provider, StorageChecks.Fast, TimeProvider.System);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            store.RequestSyncRetryNow(pair.PairId, "doc.txt");
            await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.UnresolvedIntents(pair.PairId).Count == 1, "persistent occupancy keeps one durable retry intent");
            Require(store.OpenNotices(pair.PairId).Count(n => n.ReasonKey == "sync-retry") == 1 &&
                store.OpenNotices(pair.PairId).All(n => n.Rule != 14), "ordinary failure owner emits one retry notice, not conflict choices");
            Require(a.WriteCalls == 0, "pending fallback is protected, never propagated as a user move");
        }
        h.Wire.MoveOutsideRoot("doc.txt", "/Elsewhere/occupant.txt");
        store.RequestSyncRetryNow(pair.PairId, "doc.txt");
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.UnresolvedIntents(pair.PairId).Count == 0 &&
            !store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "sync-retry"), "retry success clears intent and ordinary failure notice");
        var moved = (await writer.ObserveAsync("doc.txt", CancellationToken.None))!;
        Require(moved.ObjectId == original.ObjectId && moved.Path == "doc.txt", "retry retains identity and completes requested spelling");
    }
}
