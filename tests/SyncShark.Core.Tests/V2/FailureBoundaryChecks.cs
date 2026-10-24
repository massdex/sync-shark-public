namespace Massdex.SyncShark.V2;

internal static class FailureBoundaryChecks
{
    internal static async Task RunAsync()
    {
        StorageClassifierSeamChecks.Run();
        foreach (var code in new[] { 11, 26, 5, 6, 13, 19 })
        {
            var raw = new Microsoft.Data.Sqlite.SqliteException("synthetic", code);
            foreach (var error in new Exception[] { raw, new IOException("wrapped", raw), new AggregateException(new IOException("other"), raw) })
            {
                var damage = code is 11 or 26;
                LocalChecks.Require(StorageFailureClassifier.ScopeOf(error, default) == (damage
                    ? FailureRetryOwner.FailureScope.State : FailureRetryOwner.FailureScope.Item), "damage-only traversal leaves other codes unchanged");
                LocalChecks.Require(DateRepair.ItemFailure(error, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage) == !damage,
                    "date failure filter rejects nested damage only");
            }
        }
        var rejected = false;
        try { FailureRetryOwner.ScopeOf(new IOException(), default, null!); }
        catch (ArgumentNullException) { rejected = true; }
        LocalChecks.Require(rejected, "classifier rejects null");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        LocalChecks.Require(StorageFailureClassifier.ScopeOf(new OperationCanceledException(), stop.Token) == FailureRetryOwner.FailureScope.Stop &&
            StorageFailureClassifier.ScopeOf(new OutOfMemoryException(), default) == FailureRetryOwner.FailureScope.Stop &&
            StorageFailureClassifier.ScopeOf(new OperationCanceledException("wrapped", new Microsoft.Data.Sqlite.SqliteException("synthetic", 11)), stop.Token) == FailureRetryOwner.FailureScope.State,
            "state damage takes precedence without changing ordinary stop handling");
        foreach (Action construct in new Action[] {
            () => { _ = new Executor(null!, null!, null!, null!, isStoreException: StorageFailureClassifier.IsStoreException); },
            () => { _ = new LocalInventory(null!, default, null!, null!, null!, null!, null!); },
            () => { _ = new DropboxWriter(null!, default, null!, "", null!); },
            () => { _ = new AdmissionAbsenceBatch(null!); },
            () => { _ = new LocalGapWorker(null!, null!, null!); },
            () => { _ = new ActionRunner(null!, null!, null!, "", null!, null!, [], new(), null!); }
        })
        {
            rejected = false;
            try { construct(); } catch (ArgumentNullException error) { rejected = error.ParamName == "isStateDamage"; }
            LocalChecks.Require(rejected, "receiving constructors reject a missing predicate before other work");
        }
        await BatchAsync();
        await GapAsync();
    }

    private static async Task BatchAsync()
    {
        var item = new ItemState("item", ItemKind.File, "id", null, null, 0, DateTimeOffset.UnixEpoch, ItemFlags.None);
        (ItemState Historical, string Path)[] requests = [(item, "item"), (item, "other")];
        var fallback = new Probe();
        var rows = await SourceAbsenceBatch.ObserveAsync(fallback, requests, default, StorageFailureClassifier.IsStateDamage);
        LocalChecks.Require(rows.Count == 2 && fallback.Calls == 2, "sentinel invokes indexed fallback");
        var native = new Probe { NativeEmpty = true };
        rows = await SourceAbsenceBatch.ObserveAsync(native, requests, default, StorageFailureClassifier.IsStateDamage);
        LocalChecks.Require(rows.Count == 0 && native.Calls == 0, "native empty is not the fallback sentinel");
        foreach (var code in new[] { 11, 26, 5 })
        {
            var probe = new Probe { Error = new IOException("wrapped", new Microsoft.Data.Sqlite.SqliteException("synthetic", code)) };
            Exception? observed = null;
            try { rows = await SourceAbsenceBatch.ObserveAsync(probe, requests, default, StorageFailureClassifier.IsStateDamage); }
            catch (Exception error) { observed = error; }
            LocalChecks.Require(code == 5 ? observed is null && rows.Count == 2 : observed is not null &&
                StorageFailureClassifier.ScopeOf(observed, default) == FailureRetryOwner.FailureScope.State,
                "fallback preserves item failure and propagates corruption");
        }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = false;
        try { await SourceAbsenceBatch.ObserveAsync(new Probe(), requests, stop.Token, StorageFailureClassifier.IsStateDamage); }
        catch (OperationCanceledException) { cancelled = true; }
        LocalChecks.Require(cancelled, "fallback preserves cancellation");
    }

    private static async Task GapAsync()
    {
        var root = new RootDef("root", SyncProviders.Local, null, "/test", "id");
        foreach (var code in new[] { 11, 26, 5 })
        {
            var store = new GapStore(new IOException("wrapped", new Microsoft.Data.Sqlite.SqliteException("synthetic", code)));
            await using var worker = new LocalGapWorker(store, new GapIo(), StorageFailureClassifier.IsStateDamage);
            Exception? observed = null;
            try { worker.Discover(root, store.Epoch, default); }
            catch (Exception error) { observed = error; }
            LocalChecks.Require(code == 5 ? observed is null && store.Done == 1 : observed is not null && store.Done == 0 &&
                StorageFailureClassifier.ScopeOf(observed, default) == FailureRetryOwner.FailureScope.State,
                "diagnostic corruption forbids completion write; non-damage keeps completion");
            if (code == 5) continue;
            var loopStore = new GapStore(store.Error);
            await using var loopWorker = new LocalGapWorker(loopStore, new GapIo(), StorageFailureClassifier.IsStateDamage);
            loopWorker.Start(root);
            await loopStore.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { await loopWorker.StopAsync(root.RootId); } catch (Exception error)
            {
                LocalChecks.Require(StorageFailureClassifier.ScopeOf(error, default) == FailureRetryOwner.FailureScope.State,
                    "background loop preserves State");
            }
            LocalChecks.Require(loopStore.Done == 0 && loopStore.FailureStatuses == 0,
                "outer loop performs no failure-status persistence after corruption");
        }
    }

    private sealed class Probe : ISourceAbsenceProbe
    {
        public bool NativeEmpty;
        public Exception? Error;
        public int Calls;
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Error is not null) throw Error;
            return Task.FromResult<bool?>(true);
        }
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => Task.FromResult<ItemState?>(null);
        public Task<IReadOnlyList<AbsenceObservation>> IsAbsentBatchAsync(IReadOnlyList<(ItemState Historical, string Path)> requests, CancellationToken ct) =>
            Task.FromResult(NativeEmpty ? (IReadOnlyList<AbsenceObservation>)new List<AbsenceObservation>() : ISourceAbsenceProbe.NoNativeBatch);
    }

    private sealed class GapIo : ILocalGapIo
    {
        public bool Paused => true;
        public LocalGapProof? Probe(RootDef root, ItemState item) => null;
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class GapStore(Exception error) : ILocalGapStore
    {
        public Exception Error => error;
        public LocalGapEpoch Epoch { get; } = new("epoch", "cursor", 1, false, "", false);
        public TaskCompletionSource Faulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Done;
        public int FailureStatuses;
        public LocalGapEpoch? GapEpoch(string rootId) => Epoch;
        public void BeginGap(string rootId, string journalCursor, long boundary, bool trustFileUsn, string reason = "journal history discontinuity") { }
        public IReadOnlyList<ItemState> GapDiscoveryPage(string rootId, LocalGapEpoch epoch, int limit) =>
            [new("folder", ItemKind.Folder, "id", null, null, 0, DateTimeOffset.UnixEpoch, ItemFlags.None)];
        public void GapDiscovered(string rootId, LocalGapEpoch epoch, ItemState item, bool requiresHash) { }
        public void GapDiscoveredBatch(string rootId, LocalGapEpoch epoch, IReadOnlyList<(ItemState Item, bool RequiresHash)> items) { }
        public void SetGapDiscoveryReasons(string rootId, LocalGapEpoch epoch, IReadOnlyList<(string ObjectId, string Reason)> reasons)
        { Faulted.TrySetResult(); throw error; }
        public void GapDiscoveryDone(string rootId, LocalGapEpoch epoch) => Done++;
        public IReadOnlyList<ItemState> GapHashPage(string rootId, LocalGapEpoch epoch, int limit) => [];
        public void StageGapHash(string rootId, LocalGapEpoch epoch, ItemState item, string hash, LocalGapProof proof) { }
        public long GapResultWatermark(string rootId) => 0;
        public IReadOnlyList<LocalGapResult> GapResults(string rootId, long through, int limit, long after = 0) => [];
        public bool AcceptGapResult(string rootId, LocalGapResult result) => false;
        public void RejectGapResult(string rootId, LocalGapResult result) { }
        public bool HasGapProtection(string rootId) => true;
        public void RetireGapItem(string rootId, LocalGapEpoch epoch, ItemState item) { }
        public void SetGapStatus(string rootId, bool discovery, string status)
        { if (status.StartsWith("worker failed:", StringComparison.Ordinal)) FailureStatuses++; }
        public string GapProgress(string rootId) => "";
        public void DeferGapResult(string rootId, LocalGapResult result, string reason, bool recordFailure = true) { }
        public void DeferGapHash(string rootId, LocalGapEpoch epoch, ItemState item, string reason) { }
        public void FailGapItem(string rootId, LocalGapEpoch epoch, ItemState item, string reason) { }
        public void RetryGapFailures(string rootId) { }
        public bool IsIncompleteInitialInventory(string rootId) => false;
    }
}
