namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static IEnumerable<string> ScanFailures(BothScan scan) =>
        scan.A.IncompleteSubtrees.Concat(scan.B.IncompleteSubtrees).Distinct(StringComparer.OrdinalIgnoreCase);

    // A retry is retired only after its endpoints have observed the scope. A promoted cursor
    // excludes read debt through these durable targeted-reread rows.
    private static bool Retirable(SyncRetry retry, IReadOnlyCollection<string> failed) =>
        CachedIdentityRefresh.InventoryDebt(retry) && (retry.Path.Length == 0
            ? !failed.Contains(string.Empty)
            : !failed.Any(path => path.Length == 0 || PairPath.IsSelfOrUnder(retry.Path, path) ||
                PairPath.IsSelfOrUnder(path, retry.Path)));

    // Only an attempted, failed targeted re-read earns a user-facing reason; an absent cause keeps
    // the path incomplete without inventing one. The exact wording is stable for notice support.
    private void RecordIncomplete(string pairId, IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> causes,
        List<string> incomplete, Dictionary<string, string> reasons)
    {
        foreach (var path in paths)
        {
            incomplete.Add(path);
            var reason = CachedIdentityRefresh.RereadFailure(_store.SyncRetryAt(pairId, path), causes.GetValueOrDefault(path));
            if (reason is not null) reasons[path] = reason;
        }
    }

    private async Task<(IReadOnlyList<string> Touched, IReadOnlyList<string> Incomplete,
        IReadOnlyDictionary<string, string> Reasons, bool RootSettles, IReadOnlyList<string> Observed)> RetryScanScopesAsync(
        PairDef pair, RootDef root, Side side, IInventorySource inventory, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, ProgressThrottle throttle, CancellationToken ct, bool rootFeedComplete = false)
    {
        var touched = new List<string>();
        var exactObserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incomplete = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining = V2Settings.ScanRetryScopeLimit;
        var notSynced = ScanPathsNotSynced(pair);
        var rootDebtSettles = false;
        foreach (var batch in _store.ReadSyncRetries(pair.PairId)
            .Where(retry => (retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 }) &&
                (retry.ReadRoots is null ? root.Provider == SyncProviders.Local : retry.ReadRoots.Contains(root.RootId)))
            .Chunk(64))
        {
            ct.ThrowIfCancellationRequested();
            var verified = CachedIdentityRefresh.Verified(_store, batch, root.RootId);
            // A provider replay from the UNADVANCED cursor covers the root; debt must not stay asleep after
            // it succeeds. A completed Dropbox feed and a local journal replay are authoritative;
            // other inventories keep the conservative read (a due retry re-reads, a sleeping one stays
            // debt) so a ready restore keeps its protection until the scheduled retry.
            var authoritativeReplay = RehearsalEndpoints.Unwrap(inventory) is LocalInventory || RehearsalEndpoints.Unwrap(inventory) is DropboxInventory && rootFeedComplete;
            var scopes = batch.Where(retry => !(authoritativeReplay && retry.Path.Length == 0 && retry.Kind == "scan"))
                .Where(retry => include(retry.Path) && (retry.Kind != "scan" || !notSynced(retry.Path))).Where(retry =>
            {
                if (verified.Contains(retry.Path)) return false;
                if (remaining > 0 && retry.RetryAfter <= _time.GetUtcNow()) { remaining--; return true; }
                if (!CachedIdentityRefresh.Pending(retry)) incomplete.Add(retry.Path); // Quiet work is already protected.
                return false;
            }).Select(retry => retry.Path).ToArray();
            // Finding 3 (F4 memory): a root retry's job is planning coverage, not a second read. Plan
            // the whole pair from the stored rows (LoadTouched expands the empty path to every stored
            // directory) instead of re-enumerating a root as a delta-shaped change list. Follow-up:
            // ONLY do that in the run where the whole-root debt actually settles or is re-read. While
            // the retry sleeps, during a transient journal failure or while the provider feed is still
            // root-incomplete, the stored path set stays protected by ScanFailures -> links pending, so
            // there is no reason to load and join every row on each poll for the whole episode.
            // The caller adds the empty path only when BOTH roots completed, so a sleeping retry on one
            // side cannot force a whole-pair load while the other side newly loses its root.
            var rootRetry = batch.FirstOrDefault(retry => retry.Path.Length == 0 && retry.Kind == "scan");
            var rootSettles = rootRetry is not null && rootFeedComplete &&
                (authoritativeReplay || rootRetry.RetryAfter <= _time.GetUtcNow() && scopes.Contains(string.Empty));
            if (rootSettles) rootDebtSettles = true;
            if (scopes.Length == 0) continue;
            var read = await ReadScopesAsync(pair, root, side, inventory, scopes, cursor, gen,
                include, changed, throttle, ct);
            touched.AddRange(read.Touched);
            incomplete.AddRange(read.Incomplete);
            foreach (var reason in read.Reasons) reasons[reason.Key] = reason.Value;
            if (read.Incomplete.Contains(string.Empty)) rootSettles = rootDebtSettles = false;
            if (scopes.Contains(string.Empty))
            {
                if (!read.RootSettles) { rootSettles = false; rootDebtSettles = false; }
                else if (RehearsalEndpoints.Unwrap(inventory) is DropboxInventory && _store is SqliteStateStore)
                    rootSettles = rootDebtSettles = true;
            }
            // Whole-root planning is admitted only after this root's debt settles.
            if (rootSettles && scopes.Contains(string.Empty)) touched.Add(string.Empty);
            exactObserved.UnionWith(SuccessfullyObservedScopes(read.Observed, include, incomplete));
        }
        return (touched, incomplete, reasons, rootDebtSettles, exactObserved.ToArray());
    }

    private static IEnumerable<string> SuccessfullyObservedScopes(IEnumerable<string> attempted,
        Func<string, bool> include, IReadOnlyList<string> incomplete) => attempted.Where(scope =>
        scope.Length > 0 && include(scope) && !incomplete.Any(failed => failed.Length == 0 ||
            PairPath.IsSelfOrUnder(scope, failed) || PairPath.IsUnder(failed, scope)));

    private void RetireScanRetries(PairDef pair, IReadOnlyCollection<string> failed, Func<string, bool> notSynced,
        IReadOnlyList<string>? observed = null)
    {
        foreach (var retry in _store.ReadSyncRetries(pair.PairId))
            if ((observed is null || observed.Any(path => PairPath.IsSelfOrUnder(retry.Path, path))) &&
                (Retirable(retry, failed) || retry.Kind == "scan" && notSynced(retry.Path)) &&
                // RQ-052: scan bookkeeping cannot discard held or reviewed deletion evidence.
                (retry.Kind != "scan" || retry.Held is null && retry.ReviewedActions is not { Count: > 0 }))
            {
                if (retry.Kind != "scan")
                {
                    _store.SaveSyncRetry(pair.PairId, retry with { ReadRoots = null });
                    continue;
                }
                _store.ClearSyncRetry(pair.PairId, retry.Path);
                // SQLite clears its stable notice id atomically; other store implementations
                // keep the same observable closure contract without a schema dependency.
                if (_store is not SqliteStateStore)
                    foreach (var notice in _store.UnresolvedNotices(pair.PairId))
                        if (notice.ReasonKey == "sync-retry" && PairPath.Same(notice.Path, retry.Path) && notice.NoticeId is not null)
                            _store.CloseNotice(notice.NoticeId, "resolved");
            }
    }

    private void RecordScanRetries(PairDef pair, BothScan scan, IReadOnlyList<string>? observed = null)
    {
        var notSynced = ScanPathsNotSynced(pair);
        var failed = ScanFailures(scan).ToArray();
        RetireScanRetries(pair, failed, notSynced, observed);
        // Do not recreate retired scan debt from a filtered targeted reread. Other retry
        // kinds retain their existing read-debt and reviewed-authority bookkeeping.
        failed = failed.Where(path => _store.SyncRetryAt(pair.PairId, path) is { Kind: not "scan" } ||
            !notSynced(path)).ToArray();
        // A transient metadata observation is one durable per-entity retry, but above the limit it is ONE pair
        // notice: a root-wide handle/registration cause must not write a notice per file. Counting and testing
        // the per-path observation inline keeps no second million-entry collection.
        var transientCount = failed.Count(path => path.Length > 0 && IsTransientObservation(scan, path));
        // Aggregation is a SQLite notice-store feature; other stores keep the per-path contract unchanged.
        var storage = _store as SqliteStateStore;
        var summaryState = storage?.OwnedPointerObservationSummaryState(pair.PairId) ?? ObservationSummaryState.None;
        var aggregateTransient = transientCount > StorageNotices.NoticeAggregateLimit && storage is not null;
        // While a summary is open (or the user silenced it) it stands for the remaining failures until none
        // remain: deaggregating below the limit would hide unresolved failures until the next scan.
        var keepTransientSummary = summaryState is ObservationSummaryState.Open or ObservationSummaryState.Ignored;
        foreach (var path in failed)
        {
            var previous = _store.SyncRetryAt(pair.PairId, path);
            var floor = ScanRetryFloor(scan, path);
            // Preserve existing reviewed authority; the scan must not replace it.
            var diagnostic = scan.A.IncompleteReasons.GetValueOrDefault(path) ?? scan.B.IncompleteReasons.GetValueOrDefault(path);
            // A bounded pass can leave queued rediscovery unvisited; that is protected work.
            if (CachedIdentityRefresh.QuietUnvisited(previous, diagnostic)) continue;
            // Finding 4: the durable retry keeps the raw internal cause for support and the next run;
            // the notice says the same thing in plain language with a concrete fix. Both stay one row
            // (and one notice) per path, so repeated runs never duplicate them.
            var technical = diagnostic ?? (previous is { LastError.Length: > 0 } ? previous.LastError : null);
            var reason = ScanFailureText.Describe(technical);
            // Name only the side(s) that actually failed to read. The healthy side's planning
            // coverage comes from the empty touched path (see RetryScanScopesAsync), not from
            // pretending it is unread: naming it here would keep whole-root debt alive on a side
            // whose replay already succeeded.
            var roots = new[] { (pair.A.RootId, scan.A), (pair.B.RootId, scan.B) }
                .Where(entry => entry.Item2.IncompleteSubtrees.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(entry => entry.RootId).ToArray();
            if (previous is { Kind: not "scan" })
            {
                _store.SaveSyncRetry(pair.PairId, WithScanFloor(previous with { ReadRoots = roots }, floor));
                continue; // Keep bespoke notice, authority and reason; only extend its provider cooldown floor.
            }
            if (previous?.RetryAfter > _time.GetUtcNow())
            {
                // A sleeping retry is unchanged unless a new provider floor pushes its deadline later.
                // Rewriting it every run would be up to one upsert per escalated row per run.
                if (floor is not null && floor > previous.RetryAfter)
                    _store.SaveSyncRetry(pair.PairId, WithScanFloor(previous, floor));
                continue;
            }
            var failureState = FailureRetryOwner.Failed(previous?.Attempts ?? 0, _time.GetUtcNow());
            var updated = WithScanFloor((previous ?? new SyncRetry(path, "scan", 0, "", DateTimeOffset.MinValue)) with
            {
                Attempts = failureState.Attempts, LastError = technical ?? reason, ReadRoots = roots,
                DeleteBlockedDetail = null, DeleteBlockedDeferred = false, FailureKind = null,
                RetryAfter = failureState.RetryAfter
            }, floor);
            var suppressed = (aggregateTransient || keepTransientSummary) && path.Length > 0 &&
                IsTransientObservation(scan, path);
            // The one pair notice represents these paths; never leave a queued per-file notice behind.
            _store.SaveSyncRetry(pair.PairId, suppressed ? updated with { PendingNotice = null } : updated);
            if (suppressed) continue;
            WriteNotices([FailureRetryOwner.RetryNotice(pair.PairId, path, updated.LastError, updated.Attempts,
                explanation: reason + " Nothing in this subtree was synced or deleted.")]);
        }
        if (storage is not null) UpdateScanSummary(pair, storage, transientCount, summaryState, aggregateTransient);
    }

    private void UpdateScanSummary(PairDef pair, SqliteStateStore storage, int transientCount,
        ObservationSummaryState summaryState, bool aggregateTransient)
    {
        if (transientCount == 0)
        {
            if (summaryState == ObservationSummaryState.Open)
                storage.CloseOwnedPointerObservationSummary(pair.PairId);
            // An Ignore silences only the failures that were present when it was chosen. Once no transient
            // failure remains, retire it so a later new outage is noticed instead of being suppressed forever.
            else if (summaryState == ObservationSummaryState.Ignored)
                storage.ExpireIgnoredOwnedPointerObservationSummary(pair.PairId);
        }
        else if (aggregateTransient || summaryState == ObservationSummaryState.Open)
        {
            // Above the limit, or an existing summary standing for the remaining failures: refresh the one
            // summary and remove every per-file notice.
            storage.CloseOwnedPointerObservationNotices(pair.PairId, OwnedPointerProof.ObservationMessage);
            WriteNotices([OwnedPointerObservationSummary(pair, transientCount)]);
        }
        else if (summaryState == ObservationSummaryState.Ignored)
        {
            // The user silenced this pair cause; every per-entity retry keeps running silently.
            storage.CloseOwnedPointerObservationNotices(pair.PairId, OwnedPointerProof.ObservationMessage);
        }
        // A run with no verification failures writes no item notice, so the scan itself retires a stale
        // pointer-verification summary once every durable debt is settled.
        storage.RetirePointerVerificationSummaries(pair.PairId);
    }

    private static bool IsTransientObservation(BothScan scan, string path) =>
        scan.A.IncompleteReasons.GetValueOrDefault(path) == OwnedPointerProof.ObservationMessage ||
        scan.B.IncompleteReasons.GetValueOrDefault(path) == OwnedPointerProof.ObservationMessage;

    private static Notice OwnedPointerObservationSummary(PairDef pair, int count) =>
        new(pair.PairId, 40, NoticeKind.Info, string.Empty,
            $"{count} online-only files could not be observed because their metadata was temporarily unavailable. " +
            "Nothing in those subtrees was synced or deleted; each file keeps its durable retry debt and is retried " +
            "automatically without downloading. Close apps that hold the files or restore the Cloud Files binding, " +
            "then Retry; Ignore keeps the files protected and retries silently.",
            NoticeChoices.PointerObservationSummary);
}
