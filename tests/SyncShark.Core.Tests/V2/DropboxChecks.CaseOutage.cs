namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task CaseOutageColdReopenAsync()
    {
        using var h = new Harness();
        var directory = Path.Combine(_root, "case-outage");
        var local = Path.Combine(directory, "local");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "doc.txt"), "content");
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        var pair = new PairDef("case-outage", "Case outage", LocalChecks.RootOf(local, "case-outage-local"), Root(), [], [], true);
        var writer = h.Writer();
        h.Wire.SeedFile("Doc.txt", "content");
        var original = (await writer.ObserveAsync("Doc.txt", CancellationToken.None))!;
        var localWriter = await LocalWriter.OpenAsync(pair.A, Side.A, pair.PairId, LocalChecks.InstallationId,
            Path.Combine(directory, "recovery"), CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var source = (await localWriter.ObserveAsync("doc.txt", CancellationToken.None))!;
        var action = new SyncAction("outage-case-intent", 20, ActionKind.Move, Side.B, "doc.txt", "Doc.txt", source, original, false, "case-key");
        var state = Path.Combine(directory, "state");
        using (var store = SqliteStateStore.Open(state, settings, TimeProvider.System))
        {
            store.SavePair(pair); store.MarkFirstSyncDone(pair.PairId);
            store.RecordIntent(pair.PairId, action);
            var previous = ProviderEffectContext.Current.Value;
            var legs = 0;
            try
            {
                ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
                {
                    action = action with { ProviderEffect = checkpoint };
                    store.RecordIntent(pair.PairId, action);
                    if (++legs == 2) throw new OperationCanceledException("interrupted before second move");
                    return Task.CompletedTask;
                });
                try { await writer.MoveAsync(action, CancellationToken.None); }
                catch (OperationCanceledException) { }
            }
            finally { ProviderEffectContext.Current.Value = previous; }
        }
        using var reopened = SqliteStateStore.Open(state, settings, TimeProvider.System);
        var endpoints = new FakeEndpointProvider().AddWriter(pair.A, localWriter).AddWriter(pair.B, writer)
            .AddInventory(pair.A, new LocalInventory(pair.A, Side.A, reopened, new HashSet<string>(), settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage))
            .AddInventory(pair.B, new DropboxInventory(pair.B, Side.B, h.Client, reopened, pair.PairId));
        var preview = new PlanOnlyEndpoints(endpoints);
        var calls = h.Wire.Calls.Count;
        var previewResult = await ((IProviderEffectReconciler)preview.Writer(pair.B)).ResolveProviderEffectAsync(action, null, CancellationToken.None);
        Require(previewResult is null && h.Wire.Calls.Count == calls, "preview cannot resume a provider mutation");
        var clock = new ResilienceChecks.Clock();
        h.Client.ReadTransientBudget = TimeSpan.FromSeconds(1);
        h.Client.ReadRetryTime = clock;
        h.Client.ReadRetryDelay = (delay, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(delay); return Task.CompletedTask; };
        h.Wire.UnavailableMetadataIdentity = original.ObjectId;
        var run = new SyncRun(reopened, new Executor(reopened, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), endpoints, settings, TimeProvider.System);
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(reopened.UnresolvedIntents(pair.PairId).Single().ActionId == action.ActionId,
            "cold-reopen lookup outage retains the interrupted intent");
        Require(reopened.OpenNotices(pair.PairId).Count(n => n.ReasonKey == "sync-retry") == 1 &&
            reopened.OpenNotices(pair.PairId).All(n => n.ReasonKey != ExecutionReasons.StuckProviderJob.Key),
            "shared retry owner alone reports the lookup outage");
        Require(File.Exists(Path.Combine(local, "doc.txt")) && !h.Wire.Calls.Skip(calls).Any(c => c.Contains("delete", StringComparison.Ordinal)),
            "lookup outage never turns intermediate-hidden content into a propagated deletion");
        var nested = pair with { PairId = "covering-pair", Name = "Covering pair" };
        // The production boundary predicate must retain resumable effects even when ownership changes.
        var boundary = typeof(SyncRun).GetMethod("PermanentIntentBoundary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Require(boundary.Invoke(run, [pair, action, new[] { pair, nested }, new[] { "doc.txt" }]) is null &&
            reopened.UnresolvedIntents(pair.PairId).Count == 1,
            "a new permanent boundary cannot retire an intermediate-held rename");
    }
}
