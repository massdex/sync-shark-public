namespace Massdex.SyncShark.V2;

/// <summary>Retired cached file references request guarded inventory, never absence or content proof.</summary>
internal static class CachedIdentityRefresh
{
    internal const string PendingReason = "cached file identity changed; guarded path rediscovery pending";

    internal static bool Pending(SyncRetry retry) => retry.Kind == "scan" && retry.LastError == PendingReason;

    /// <summary>Read debt a completed inventory may retire; a quiet refresh is not read debt.</summary>
    internal static bool InventoryDebt(SyncRetry retry) =>
        !Pending(retry) && (retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 });

    /// <summary>An unvisited refresh is protected work, not an observation failure for a notice.</summary>
    internal static bool QuietUnvisited(SyncRetry? previous, string? diagnostic) =>
        previous is not null && Pending(previous) && diagnostic is null;

    /// <summary>Being due does not authorize effects for an unvisited guarded identity refresh.</summary>
    internal static HashSet<string> ActiveRetryPaths(IEnumerable<SyncRetry> retries) =>
        retries.Where(item => !Pending(item)).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Retire each scope the re-read observed with no incomplete ancestor or descendant.</summary>
    internal static void SettleObserved(IStateStore known, string pairId, IEnumerable<string> scopes,
        IReadOnlyCollection<string> incomplete, string rootId)
    {
        foreach (var scope in scopes)
            if (!incomplete.Any(path => path.Length == 0 ||
                PairPath.IsSelfOrUnder(scope, path) || PairPath.IsSelfOrUnder(path, scope)))
                Observed(known, pairId, scope, rootId);
    }

    /// <summary>An invalid cached-ID open queues rediscovery, or reports the unchanged unverifiable result.</summary>
    internal static LocalGapHashResult OpenFailure(IStateStore known, RootDef root, ItemState item, int error)
    {
        if (TryQueue(known, root, item, "open-failed(" + error + ")"))
            return new(LocalGapHashOutcome.IdentityChanged, Reason: PendingReason);
        return new(LocalGapHashOutcome.Unverifiable, Reason: "content open failed: Windows error " + error,
            QuarantineCode: error is 225 or 226 ? error : null);
    }

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
