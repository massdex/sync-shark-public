namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static IEnumerable<string> ScanFailures(BothScan scan) =>
        scan.A.IncompleteSubtrees.Concat(scan.B.IncompleteSubtrees).Distinct(StringComparer.OrdinalIgnoreCase);

    private async Task<(IReadOnlyList<string> Touched, IReadOnlyList<string> Incomplete,
        IReadOnlyDictionary<string, string> Reasons)> RetryScanScopesAsync(
        PairDef pair, RootDef root, Side side, IInventorySource inventory, string cursor, long gen,
        Func<string, bool> include, Action<string> changed, ProgressThrottle throttle, CancellationToken ct, bool rootFeedComplete = false)
    {
        var touched = new List<string>();
        var incomplete = new List<string>();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var remaining = 256;
        var rootPlanned = false;
        foreach (var batch in _store.ReadSyncRetries(pair.PairId)
            .Where(retry => (retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 }) &&
                (retry.ReadRoots is null ? root.Provider == SyncProviders.Local : retry.ReadRoots.Contains(root.RootId)))
            .Chunk(64))
        {
            ct.ThrowIfCancellationRequested();
            // Read-resilience F-R (approved in review-read-resilience-2): a provider replay from the
            // UNADVANCED cursor already covers the whole root, so root debt must not stay asleep after
            // it succeeds. A completed Dropbox feed and a local journal replay are authoritative;
            // other inventories keep the conservative read (a due retry re-reads, a sleeping one stays
            // debt) so a ready restore keeps its protection until the scheduled retry.
            var authoritativeReplay = inventory is LocalInventory || inventory is DropboxInventory && rootFeedComplete;
            var scopes = batch.Where(retry => !(authoritativeReplay && retry.Path.Length == 0 && retry.Kind == "scan"))
                .Where(retry => include(retry.Path)).Where(retry =>
            {
                if (remaining > 0 && retry.RetryAfter <= _time.GetUtcNow()) { remaining--; return true; }
                incomplete.Add(retry.Path); // Sleeping work still protects both sides and its notice.
                return false;
            }).Select(retry => retry.Path).ToArray();
            // Finding 3 (F4 memory): a root retry's job is planning coverage, not a second read. Plan
            // the whole pair from the stored rows (LoadTouched expands the empty path to every stored
            // directory) instead of re-enumerating a root as a delta-shaped change list. Follow-up:
            // ONLY do that in the run where the whole-root debt actually settles or is re-read. While
            // the retry sleeps, during a transient journal failure or while the provider feed is still
            // root-incomplete, the stored path set stays protected by ScanFailures -> links pending, so
            // there is no reason to load and join every row on each poll for the whole episode.
            var rootRetry = batch.FirstOrDefault(retry => retry.Path.Length == 0 && retry.Kind == "scan");
            var rootSettles = rootRetry is not null && rootFeedComplete &&
                (authoritativeReplay || rootRetry.RetryAfter <= _time.GetUtcNow() && scopes.Contains(string.Empty));
            if (scopes.Length == 0)
            {
                if (rootSettles && !rootPlanned) { rootPlanned = true; touched.Add(string.Empty); }
                continue;
            }
            if (inventory is LocalInventory local)
            {
                var reread = await local.ChangesForPathsAsync(scopes, cursor, gen, include, ct);
                if (reread.CursorInvalid)
                {
                    incomplete.AddRange(scopes);
                    // Follow-up 3: carry the real cause (exception kind and message) so the notice
                    // says why the reread failed instead of the generic "could not be completely read".
                    var reason = reread.IncompleteReasons.GetValueOrDefault(string.Empty) ?? local.LastFallbackReason;
                    if (!string.IsNullOrWhiteSpace(reason))
                        foreach (var scope in scopes) reasons[scope] = reason!;
                    // A root-open failure means the scope was not observed, so the root debt is not settled.
                    if (scopes.Contains(string.Empty)) rootSettles = false;
                    continue;
                }
                _store.WriteDelta(reread with { ChangedObject = changed });
                // WriteDelta's callback covers deletions; re-read identities widen the dirty set too, exactly
                // as the main scan's delta does, so a hidden change is never merely touched.
                foreach (var change in reread.Changes)
                    if (change.ObjectId.Length > 0) changed(change.ObjectId);
                foreach (var path in reread.IncompleteSubtrees)
                {
                    incomplete.Add(path);
                    if (reread.IncompleteReasons.GetValueOrDefault(path) is { Length: > 0 } detail) reasons[path] = detail;
                }
                touched.AddRange(reread.Changes.Select(change => change.Path));
                if (reread.IncompleteSubtrees.Contains(string.Empty)) rootSettles = false;
            }
            else if (inventory is DropboxInventory dropbox && _store is SqliteStateStore sql)
            {
                // Prefix hydration has its own disk staging and never replaces the root's
                // complete tombstone-classification header with a partial inventory.
                try
                {
                    sql.BeginPrefixHydration(root.RootId, ct);
                    await dropbox.HydratePrefixesAsync(scopes, rows =>
                    {
                        sql.StagePrefixHydration(root.RootId, rows, include);
                        return Task.CompletedTask;
                    }, ct);
                    sql.InstallPrefixHydration(root, side, gen, scopes, include, cursor, ct, changed);
                }
                catch (Exception error) when (ScopeFailure(error, ct))
                {
                    incomplete.AddRange(scopes);
                    if (scopes.Contains(string.Empty)) rootSettles = false;
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
                if (reread.IncompleteSubtrees.Contains(string.Empty)) rootSettles = false;
                touched.AddRange(reread.Items.Select(item => item.Path));
            }
            touched.AddRange(scopes);
            if (rootSettles && !rootPlanned) { rootPlanned = true; touched.Add(string.Empty); }
        }
        return (touched, incomplete, reasons);
    }

    private void RecordScanRetries(PairDef pair, BothScan scan)
    {
        var failed = ScanFailures(scan).ToArray();
        // A retry is retired only after its endpoints have observed the scope. A promoted
        // cursor excludes read debt through these durable targeted-reread rows.
        foreach (var retry in _store.ReadSyncRetries(pair.PairId))
            if ((retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 }) &&
                (retry.Path.Length == 0 ? !failed.Contains(string.Empty) : !failed.Any(path => path.Length == 0 ||
                    PairPath.IsSelfOrUnder(retry.Path, path) || PairPath.IsSelfOrUnder(path, retry.Path))))
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
                    foreach (var notice in _store.OpenNotices(pair.PairId))
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
            // Preserve existing reviewed authority; the scan must not replace it.
            var diagnostic = scan.A.IncompleteReasons.GetValueOrDefault(path) ?? scan.B.IncompleteReasons.GetValueOrDefault(path);
            var reason = diagnostic is null && previous is { LastError.Length: > 0 } ? previous.LastError :
                (diagnostic ?? "This subtree could not be completely read.") +
                " Correct the reported access, placeholder or name issue, then Retry; Ignore excludes this scope from sync.";
            // Name only the side(s) that actually failed to read. The healthy side's planning
            // coverage comes from the empty touched path (see RetryScanScopesAsync), not from
            // pretending it is unread: naming it here would keep whole-root debt alive on a side
            // whose replay already succeeded.
            var roots = new[] { (pair.A.RootId, scan.A), (pair.B.RootId, scan.B) }
                .Where(entry => entry.Item2.IncompleteSubtrees.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(entry => entry.RootId).ToArray();
            if (previous is { Kind: not "scan" })
            {
                _store.SaveSyncRetry(pair.PairId, previous with { ReadRoots = roots });
                continue; // Keep its bespoke notice, authority, reason and scheduling intact.
            }
            if (previous?.RetryAfter > _time.GetUtcNow()) continue;
            var attempts = (previous?.Attempts ?? 0) + 1;
            var updated = (previous ?? new SyncRetry(path, "scan", 0, "", DateTimeOffset.MinValue)) with
            {
                Attempts = attempts, LastError = reason, ReadRoots = roots,
                DeleteBlockedDetail = null, DeleteBlockedDeferred = false,
                RetryAfter = _time.GetUtcNow().AddSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(attempts - 1, 5))))
            };
            var suppressed = (aggregateTransient || keepTransientSummary) && path.Length > 0 &&
                IsTransientObservation(scan, path);
            // The one pair notice represents these paths; never leave a queued per-file notice behind.
            _store.SaveSyncRetry(pair.PairId, suppressed ? updated with { PendingNotice = null } : updated);
            if (suppressed) continue;
            WriteNotices([new Notice(pair.PairId, 40, NoticeKind.Info, path, reason +
                " Nothing in this subtree was synced or deleted. Options: retry now / exclude / ignore.", "sync-retry")]);
        }
        if (storage is not null)
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
