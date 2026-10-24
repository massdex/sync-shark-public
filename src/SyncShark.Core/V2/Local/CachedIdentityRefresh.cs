namespace Massdex.SyncShark.V2;

/// <summary>Retired cached file references request guarded inventory, never absence or content proof.</summary>
internal static class CachedIdentityRefresh
{
    internal const string PendingReason = "cached file identity changed; guarded path rediscovery pending";

    internal static bool Pending(SyncRetry retry) => retry.Kind == "scan" && retry.LastError == PendingReason;
    internal static bool NeedsObservation(SyncRetry retry) => Pending(retry) ||
        retry.Kind == "scan" && retry.LastError == OpenReason;

    // Indexing can settle this failure before its next read deadline. Do not turn accepted
    // content proof back into scan incompleteness; RecordScanRetries owns retirement/closure.
    internal static bool Verified(IStateStore known, SyncRetry retry, string rootId) =>
        Verified(known, [retry], rootId).Contains(retry.Path);

    internal static IReadOnlySet<string> Verified(IStateStore known, IReadOnlyList<SyncRetry> retries, string rootId)
    {
        var paths = retries.Where(retry => !Pending(retry) && NeedsObservation(retry)).Select(retry => retry.Path).ToArray();
        if (paths.Length == 0 || known is not ILocalGapStore gaps) return new HashSet<string>();
        var items = known.Live(rootId, paths);
        var pending = gaps.GapObligations(rootId, items.Select(item => item.ObjectId).ToArray());
        return items.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1 && group.Single() is { Hash: not null } item &&
                (item.Flags & ItemFlags.NeedsRehash) == 0 && !pending.Contains(item.ObjectId))
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Read debt a completed inventory may retire; a quiet refresh is not read debt.</summary>
    internal static bool InventoryDebt(SyncRetry retry) =>
        !Pending(retry) && (retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 });

    /// <summary>An unvisited refresh is protected work, not an observation failure for a notice.</summary>
    internal static bool QuietUnvisited(SyncRetry? previous, string? diagnostic) =>
        previous is not null && Pending(previous) && diagnostic is null;

    /// <summary>Being due does not authorize effects for an unvisited guarded identity refresh.</summary>
    internal static HashSet<string> ActiveRetryPaths(IEnumerable<SyncRetry> retries) =>
        retries.Where(item => !Pending(item)).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal const string OpenReason = "open-failed(87)";
    internal static string RereadReason => MessageCatalog.Default.Message("guardedInventoryIncomplete");

    internal static string? RereadFailure(SyncRetry? previous, string? reason) =>
        !string.IsNullOrWhiteSpace(reason) ? reason : previous is not null && Pending(previous) ? RereadReason : null;

    /// <summary>Unchanged identity is a real failure, handed to the existing scan retry/notice owner.</summary>
    internal static void SettleObserved(IStateStore known, string pairId, IEnumerable<string> scopes,
        IReadOnlyCollection<string> incomplete, string rootId, IReadOnlyList<ItemState> before,
        List<string> failed, Dictionary<string, string> reasons)
    {
        var after = known.Live(rootId, before.Select(item => item.Path)).ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var retries = scopes.Select(scope => known.SyncRetryAt(pairId, scope)).OfType<SyncRetry>().ToArray();
        var verified = Verified(known, retries, rootId);
        var byPath = retries.ToDictionary(retry => retry.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            if (incomplete.Any(path => path.Length == 0 ||
                PairPath.IsSelfOrUnder(scope, path) || PairPath.IsSelfOrUnder(path, scope))) continue;
            var old = before.FirstOrDefault(item => PairPath.Same(item.Path, scope));
            if (old is not null && after.TryGetValue(scope, out var current) && old.ObjectId == current.ObjectId &&
                byPath.TryGetValue(scope, out var retry) && NeedsObservation(retry) && !verified.Contains(scope))
            {
                failed.Add(scope);
                reasons[scope] = OpenReason;
            }
            else Observed(known, pairId, scope, rootId);
        }
    }

    /// <summary>An invalid cached-ID open queues rediscovery, or reports the unchanged unverifiable result.</summary>
    internal static LocalGapHashResult OpenFailure(IStateStore known, RootDef root, ItemState item, int error,
        DateTimeOffset? now = null)
    {
        if (TryQueue(known, root, item, error, now))
            return new(LocalGapHashOutcome.IdentityChanged, Reason: PendingReason);
        return new(LocalGapHashOutcome.Unverifiable,
            Reason: string.Format(MessageCatalog.Default.Message("cachedContentOpenFailed"), error),
            QuarantineCode: error is 225 or 226 ? error : null);
    }

    internal static void Observed(IStateStore known, string pairId, string path, string rootId) =>
        known.UpdateSyncRetry(pairId, path, retry =>
        {
            if (retry is null || !Pending(retry)) return retry;
            var remaining = (retry.ReadRoots ?? []).Where(id => id != rootId).ToArray();
            return remaining.Length == 0 ? null : retry with { ReadRoots = remaining };
        });

    internal static bool TryQueue(IStateStore known, RootDef root, ItemState item, string? reason) =>
        TryQueue(known, root, item, reason == OpenReason ? 87 : null);

    internal static bool TryQueue(IStateStore known, RootDef root, ItemState item, int? openError, DateTimeOffset? now = null)
    {
        if (openError != 87) return false;
        // A probe may finish after inventory has already replaced its entity. Do not enqueue
        // obsolete work, but it is still not a verification failure for the replacement.
        if (known.ByObject(root.RootId, item.ObjectId) is null) return true;
        var queued = false;
        foreach (var pair in known.Pairs().Where(pair => pair.A.RootId == root.RootId || pair.B.RootId == root.RootId))
        {
            // Deliberately skipped work is handled, not an indexing failure. Other pairs
            // sharing this root still decide independently whether they need rediscovery.
            queued = true;
            if (PairInclude.Excludes(pair, item.Path) ||
                SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, PairInclude.IgnoreRules(pair))) continue;
            known.UpdateSyncRetry(pair.PairId, item.Path, prior =>
            {
                if (prior is null) return new(item.Path, "scan", 0, PendingReason,
                    now ?? TimeProvider.System.GetUtcNow(), ReadRoots: [root.RootId]);
                if (prior.Kind == "scan" && (prior.ReadRoots is null || prior.ReadRoots.Contains(root.RootId)))
                    return prior;
                // Keep reviewed authority, attempts, notices and deadlines owned by the existing retry.
                return prior with
                { ReadRoots = (prior.ReadRoots ?? []).Append(root.RootId).Distinct(StringComparer.Ordinal).ToArray() };
            });
        }
        // Never hide a failure if no pair owns the requested refresh.
        return queued;
    }
}
