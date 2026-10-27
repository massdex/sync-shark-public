namespace Massdex.SyncShark.V2;

internal sealed partial class LocalGapWorker
{
    internal static void PromoteDiscovery(ILocalGapStore store, RootDef root, CancellationToken ct,
        Func<Exception, bool> isStateDamage)
    {
        ct.ThrowIfCancellationRequested();
        try { store.PromoteGapDiscovery(root.RootId); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage) != FailureRetryOwner.FailureScope.State)
        {
            store.SetGapStatus(root.RootId, true, $"metadata promotion deferred: {error.Message}; retry next sync");
            RunTrace.Emit($"local gap metadata promotion deferred: {root.RootId}: {error}");
            return;
        }
    }

    internal static void Promote(ILocalGapStore store, IStateStore known, ILocalGapIo io, RootDef root,
        long through, NtfsUsnCheckpoint? runStart, Func<string, bool> include, Action<string> touched, CancellationToken ct,
        Func<Exception, bool> isStateDamage)
    {
        // At most four pages (512 probes) per planning pass; remaining work waits for the next pass.
        // Selection belongs only to planning touches, never entity verification.
        if (runStart is null) return;
        long after = 0;
        for (var pageNumber = 0; pageNumber < 4; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var results = store.GapResults(root.RootId, through, 128, after);
            if (results.Count == 0) return;
            using var probes = io.OpenProbes(root);
            foreach (var result in results)
            {
                ct.ThrowIfCancellationRequested();
                after = result.Sequence;
                try { PromoteResult(store, known, io, root, runStart, probes, result); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct, isStateDamage) != FailureRetryOwner.FailureScope.State)
                {
                    store.DeferGapResult(root.RootId, result, error.Message);
                    RunTrace.Emit($"local gap result validation deferred: {result.Expected.Path}: {error}");
                }
            }
        }
    }

    private static void PromoteResult(ILocalGapStore store, IStateStore known, ILocalGapIo io,
        RootDef root, NtfsUsnCheckpoint runStart, ILocalGapProbe probes, LocalGapResult result)
    {
        var currentItem = known.ByObject(root.RootId, result.Expected.ObjectId);
        if (currentItem is null || MetadataOnly(currentItem) || io.Excluded(root, currentItem))
        {
            if (store.GapEpoch(root.RootId) is { } epoch)
                store.RetireGapItem(root.RootId, epoch, currentItem ?? result.Expected);
            return;
        }
        if (!ProofFollows(runStart.Cursor, result.Proof.JournalCursor))
        {
            store.RejectGapResult(root.RootId, result);
            return;
        }
        var current = probes.Probe(currentItem);
        if (current is null)
        {
            if (CachedIdentityRefresh.TryQueue(known, root, currentItem, probes.LastOpenError, io.Time.GetUtcNow()))
                store.DeferGapResult(root.RootId, result, CachedIdentityRefresh.PendingReason, recordFailure: false);
            else
                store.DeferGapResult(root.RootId, result, probes.UnavailableReason ?? probes.LastFailureReason ??
                    "file identity proof temporarily unavailable");
            return;
        }
        if (!ProofFollows(current.JournalCursor, runStart.Cursor) ||
            current.File != result.Proof.File || !store.AcceptGapResult(root.RootId, result))
        {
            store.RejectGapResult(root.RootId, result);
            return;
        }
        // AcceptGapResult retained the entity's durable publication obligation.
        // The indexer's bounded planning handoff owns alias expansion; no direct touches here.
    }
}
