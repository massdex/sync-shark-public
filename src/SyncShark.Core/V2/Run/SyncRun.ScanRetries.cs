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
        IReadOnlyDictionary<string, string> Reasons, bool RootSettles)> RetryScanScopesAsync(
        PairDef pair, RootDef root, Side side, IInventorySource inventory, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, ProgressThrottle throttle, CancellationToken ct, bool rootFeedComplete = false)
    {
        var touched = new List<string>();
        var incomplete = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining = V2Settings.ScanRetryScopeLimit;
        var rootDebtSettles = false;
        foreach (var batch in _store.ReadSyncRetries(pair.PairId)
            .Where(retry => (retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 }) &&
                (retry.ReadRoots is null ? root.Provider == SyncProviders.Local : retry.ReadRoots.Contains(root.RootId)))
            .Chunk(64))
        {
            ct.ThrowIfCancellationRequested();
            var verified = CachedIdentityRefresh.Verified(_store, batch, root.RootId);
            // Read-resilience F-R: a provider replay from the
            // UNADVANCED cursor already covers the whole root, so root debt must not stay asleep after
            // it succeeds. A completed Dropbox feed and a local journal replay are authoritative;
            // other inventories keep the conservative read (a due retry re-reads, a sleeping one stays
            // debt) so a ready restore keeps its protection until the scheduled retry.
            var authoritativeReplay = RehearsalEndpoints.Unwrap(inventory) is LocalInventory || RehearsalEndpoints.Unwrap(inventory) is DropboxInventory && rootFeedComplete;
            var scopes = batch.Where(retry => !(authoritativeReplay && retry.Path.Length == 0 && retry.Kind == "scan"))
                .Where(retry => include(retry.Path)).Where(retry =>
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
            if (RehearsalEndpoints.Unwrap(inventory) is LocalInventory local)
            {
                // Exact-path read: at most the 256 admitted scopes, never their descendants.
                var before = _store.Live(root.RootId, scopes);
                var reread = RehearsalEndpoints.Apply(inventory, await local.ChangesForPathsAsync(scopes, cursor, gen, include, ct), include);
                if (reread.CursorInvalid)
                {
                    var reason = reread.IncompleteReasons.GetValueOrDefault(string.Empty) ?? local.LastFallbackReason;
                    RecordIncomplete(pair.PairId, scopes, scopes.ToDictionary(path => path, _ => reason!), incomplete, reasons);
                    // A root-open failure means the scope was not observed, so the root debt is not settled.
                    if (scopes.Contains(string.Empty)) { rootSettles = false; rootDebtSettles = false; }
                    continue;
                }
                _store.WriteDelta(reread with { ChangedObject = changed });
                // WriteDelta's callback covers deletions; re-read identities widen the dirty set too, exactly
                // as the main scan's delta does, so a hidden change is never merely touched.
                foreach (var change in reread.Changes)
                    if (change.ObjectId.Length > 0) changed(change.ObjectId);
                RecordIncomplete(pair.PairId, reread.IncompleteSubtrees, reread.IncompleteReasons, incomplete, reasons);
                touched.AddRange(reread.Changes.Select(change => change.Path));
                CachedIdentityRefresh.SettleObserved(_store, pair.PairId, scopes, reread.IncompleteSubtrees, root.RootId, before, incomplete, reasons);
                if (reread.IncompleteSubtrees.Contains(string.Empty)) { rootSettles = false; rootDebtSettles = false; }
            }
            else if (RehearsalEndpoints.Unwrap(inventory) is DropboxInventory dropbox && _store is SqliteStateStore sql)
            {
                // Prefix hydration has its own disk staging and never replaces the root's
                // complete tombstone-classification header with a partial inventory.
                try
                {
                    sql.BeginPrefixHydration(root.RootId, ct);
                    var failures = await RehearsalEndpoints.HydrateAsync(inventory, dropbox, scopes, rows =>
                    {
                        sql.StagePrefixHydration(root.RootId, rows.Where(row => include(row.Path))
                        .Select(row => (row.Path, row.Entry is null ? null : DropboxItems.ToItem(row.Entry))).ToArray());
                        return Task.CompletedTask;
                    }, ct);
                    bool observed(string path) => include(path) && !DropboxPathIdentity.InScope(failures, path);
                    sql.InstallPrefixHydration(root, side, gen, scopes, observed, cursor, ct, changed);
                    foreach (var failure in failures)
                    {
                        incomplete.Add(failure.Key);
                        reasons[failure.Key] = failure.Value;
                    }
                    if (scopes.Contains(string.Empty))
                    {
                        rootSettles = !failures.ContainsKey(string.Empty);
                        rootDebtSettles = rootSettles;
                    }
                }
                catch (Exception error) when (ScopeFailure(error, ct))
                {
                    incomplete.AddRange(scopes);
                    if (scopes.Contains(string.Empty)) { rootSettles = false; rootDebtSettles = false; }
                    RunLog.Trace("Targeted inventory remains incomplete: " + error.Message);
                }
            }
            else
            {
                // Generic inventories may not expose targeted reads. Narrow both enumeration and
                // snapshot authority; ancestors are traversed, never granted absence authority.
                bool within(string path) => scopes.Any(scope => scope.Length == 0 || PairPath.IsSelfOrUnder(path, scope));
                bool traverse(string path) => include(path) && (within(path) || scopes.Any(scope => PairPath.IsSelfOrUnder(scope, path)));
                var reread = await ReadFullAsync(inventory, gen, traverse, root, throttle, ct);
                _store.WriteSnapshot(reread with
                {
                    IncludesPath = path => include(path) && within(path) && (reread.IncludesPath?.Invoke(path) ?? true),
                    ChangedObject = changed
                });
                foreach (var path in reread.IncompleteSubtrees)
                {
                    incomplete.Add(path);
                    if (reread.IncompleteReasons.GetValueOrDefault(path) is { Length: > 0 } detail) reasons[path] = detail;
                }
                if (reread.IncompleteSubtrees.Contains(string.Empty)) { rootSettles = false; rootDebtSettles = false; }
                touched.AddRange(reread.Items.Select(item => item.Path));
            }
            // Finding 2 (review-rc3-2): a due root retry whose re-read failed observed nothing, so it
            // must not touch "" and plan the whole pair without settling. Ordinary scopes stay touched.
            touched.AddRange(rootSettles ? scopes : scopes.Where(scope => scope.Length > 0));
        }
        return (touched, incomplete, reasons, rootDebtSettles);
    }

    private void RecordScanRetries(PairDef pair, BothScan scan)
    {
        var failed = ScanFailures(scan).ToArray();
        foreach (var retry in _store.ReadSyncRetries(pair.PairId))
            if (Retirable(retry, failed))
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
