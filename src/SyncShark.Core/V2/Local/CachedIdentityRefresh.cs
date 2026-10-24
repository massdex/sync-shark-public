namespace Massdex.SyncShark.V2;

/// <summary>Retired cached file references request guarded inventory, never absence or content proof.</summary>
internal static class CachedIdentityRefresh
{
    internal const string PendingReason = "cached file identity changed; guarded path rediscovery pending";

    internal static bool Pending(SyncRetry retry) => retry.Kind == "scan" && retry.LastError == PendingReason;

    internal static void Observed(IStateStore known, string pairId, string path, string rootId) =>
        known.UpdateSyncRetry(pairId, path, retry =>
        {
            if (retry is null || !Pending(retry)) return retry;
            var remaining = (retry.ReadRoots ?? []).Where(id => id != rootId).ToArray();
            return remaining.Length == 0 ? null : retry with { ReadRoots = remaining };
        });

    internal static bool TryQueue(IStateStore known, RootDef root, ItemState item, string? reason)
    {
        // This exact diagnostic is emitted only by cached file-ID opens, not path opens or delete admission.
        if (reason != "open-failed(87)") return false;
        // A probe may finish after inventory has already replaced its entity. Do not enqueue
        // obsolete work, but it is still not a verification failure for the replacement.
        if (known.ByObject(root.RootId, item.ObjectId) is null) return true;
        var queued = false;
        foreach (var pair in known.Pairs().Where(pair => pair.A.RootId == root.RootId || pair.B.RootId == root.RootId))
        {
            known.UpdateSyncRetry(pair.PairId, item.Path, prior =>
            {
                if (prior is null) return new(item.Path, "scan", 0, PendingReason,
                    DateTimeOffset.MinValue, ReadRoots: [root.RootId]);
                if (prior.Kind == "scan" && prior.ReadRoots is null || prior.ReadRoots?.Contains(root.RootId) == true)
                    return prior;
                // Keep reviewed authority, attempts, notices and deadlines owned by the existing retry.
                return prior with
                { ReadRoots = (prior.ReadRoots ?? []).Append(root.RootId).Distinct(StringComparer.Ordinal).ToArray() };
            });
            queued = true;
        }
        // Never hide a failure if there is no durable consumer for the requested refresh.
        return queued;
    }
}
