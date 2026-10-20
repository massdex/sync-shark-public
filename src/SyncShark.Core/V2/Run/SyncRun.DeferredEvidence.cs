using static Massdex.SyncShark.V2.RunLog;
using System.Diagnostics;
using System.Threading.Channels;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private readonly object _heldRefreshSync = new();
    private long _heldRefreshVersion; // Protected by _heldRefreshSync, as are held inventory writes.

    private sealed record PreparedEvidence(BothScan Scan, IReadOnlyList<HeldDeletion> Held, int Resolved);
    private sealed record HeldPlanResult(Plan Plan, bool RecoverySkipped);

    private async Task<PreparedEvidence> PrepareDeferredEvidenceAsync(PairDef pair, RunScope.Scope scope,
        IReadOnlyList<string> rebuildables, IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare, BothScan scan, CancellationToken ct)
    {
        if (RebuildableSelection.Sides(pair) is not { } sides ||
            _endpoints.Inventory(sides.Cloud == Side.A ? pair.A : pair.B) is not IDeferredDeletionEvidence deferred)
            return new(await WithDeletionTimesAsync(pair, scope, rebuildables, matchedAtPrepare, scan, ct), [], 0);
        var probed = DeletionProbePaths(pair,
            sides.Local == Side.A ? OnlyIn(scope.A, scope.B) : OnlyIn(scope.B, scope.A), rebuildables);
        // finding 2: a prepare-matched path is ordinary planning input, never deletion evidence.
        var paths = matchedAtPrepare is null
            ? probed
            : probed.Where(path => !matchedAtPrepare.ContainsKey(path)).ToArray();
        var localItems = sides.Local == Side.A ? scope.A : scope.B;
        var candidates = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = localItems.Where(item => item.Kind == ItemKind.Folder && candidates.Contains(item.Path))
            .Select(item => item.Path).ToArray();
        var files = localItems.Where(item => item.Kind == ItemKind.File && candidates.Contains(item.Path))
            .Select(item => item.Path).ToArray();
        // Establish the complete shared classification first. Folder classification then needs
        // no network lane: the same cursor-bound tombstones answer it with indexed path reads.
        var prepared = await deferred.PrepareDeletionEvidenceAsync(files, ct);
        var times = prepared.Resolved.Where(entry => entry.Value.DeletedUtc is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value.DeletedUtc, StringComparer.OrdinalIgnoreCase);
        scan = sides.Cloud == Side.A ? scan with { A = scan.A with { Deleted = times } }
            : scan with { B = scan.B with { Deleted = times } };
        var pending = prepared.Pending.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Cached deletion evidence must use the same held resolver as fresh evidence: the ordinary
        // scan carries deletion times only and would lose the revision hash needed by decision 78.
        // The resolver also observes/hashes the current local file before deciding.
        pending.UnionWith(localItems.Where(item => item.Kind == ItemKind.File &&
            times.TryGetValue(item.Path, out var deleted) && deleted > item.Mtime).Select(item => item.Path));
        // Tombstone folders wait for child outcomes even when every file result was cached.
        // Otherwise only ancestors of held paths wait; new folders stay on the ordinary planner path.
        pending.UnionWith(deferred.FolderDeletionCandidates(folders));
        var heldAncestors = pending.SelectMany(PairPath.Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Intersect with local-only candidates so shared ancestors never enter this lane.
        pending.UnionWith(folders.Where(heldAncestors.Contains));
        return new(scan, pending.Select(path => new HeldDeletion(path, sides.Local)).ToArray(),
            prepared.Resolved.Keys.Count(path => !pending.Contains(path)));
    }

    private static RunScope.Scope WithoutHeld(RunScope.Scope scope, IReadOnlyList<HeldDeletion> held)
    {
        if (held.Count == 0) return scope;
        var paths = held.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(scope.A.Where(item => !paths.Contains(item.Path)).ToArray(),
            scope.B.Where(item => !paths.Contains(item.Path)).ToArray(),
            scope.Baselines.Where(item => !paths.Contains(item.A.Path) && !paths.Contains(item.B.Path)).ToArray());
    }

    private sealed record DeferredExecution(Plan Plan, Tally Tally, IReadOnlyList<string> Pending,
        bool EvidenceUnavailable = false);

    // A single result consumer owns the tally and progress throttle. Evidence and ordinary effects run concurrently;
    // each resolved path re-enters the normal executor, retaining its recovery and conditional-write guards.
    private async Task<DeferredExecution> ExecuteWithEvidenceAsync(Plan approved, PairDef pair,
        ProgressThrottle throttle, IReadOnlySet<string>? reviewedDeletes, CancellationToken ct,
        bool allowUnreviewedEvidence = false)
    {
        if (approved.HeldDeletions.Count == 0)
        {
            approved = HoldUnavailableRecovery(pair, approved, reviewedDeletes);
            Trace("0 held deletes skipped Recovery (identical to Dropbox history)");
            return new(approved, await ExecuteAsync(approved, pair, throttle, ct), approved.RecoveryPending);
        }
        var parkedFootprints = LinkExclusions.None.WithPending(OutstandingFootprints(pair.PairId));
        var held = approved.HeldDeletions.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var unapproved = held.Values.Where(item => item.Rule != HeldDeletion.NewerDeletionRule ||
            !allowUnreviewedEvidence && (reviewedDeletes is null || !reviewedDeletes.Contains(FolderDelete.VictimKey(item.Local, item.Path))))
            .Select(item => item.Path).ToArray();
        foreach (var path in unapproved)
        {
            var reason = "This deletion cannot proceed without valid reviewed authority. Review the item or Ignore it; nothing was deleted.";
            SaveFailedRetry(pair, path, reason, _store.SyncRetryAt(pair.PairId, path));
            held.Remove(path);
        }
        var authorityGuard = LinkExclusions.None.WithPending(unapproved);
        approved = ApplySafetyFilter(pair, ActionPredicates(pair, authorityGuard), approved) with { HeldDeletions = held.Values.ToArray() };
        approved = HoldUnavailableRecovery(pair, approved, reviewedDeletes);
        held = approved.HeldDeletions.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        if (held.Count == 0)
            return new(approved, await ExecuteAsync(approved, pair, throttle, ct), unapproved.Concat(approved.RecoveryPending).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        // RQ-141: one exclusion snapshot for the lane, not a root scan per action. Folder refreshes
        // invalidate it under the same gate as their writes. Retain every protection seen this run
        // (decisions 57/59); the filesystem probe and executor's effect-time checks remain per item.
        var leftAlone = LinkExclusions.None;
        var evidenceHolds = LinkExclusions.None;
        ActionGuard? heldGuard = null;
        var guardVersion = -1L;
        ActionGuard HeldGuard(bool refresh = false)
        {
            lock (_heldRefreshSync)
            {
                if (refresh || heldGuard is null || guardVersion != _heldRefreshVersion)
                {
                    var footprints = OutstandingFootprints(pair.PairId);
                    leftAlone = leftAlone.With(LeftAlone(pair)).WithPending(footprints);
                    evidenceHolds = evidenceHolds.With(LinkExclusions.HoldsOf(_store, pair)).WithPending(footprints);
                    heldGuard = ActionPredicates(pair, leftAlone);
                    guardVersion = _heldRefreshVersion;
                }
                return heldGuard;
            }
        }
        HeldGuard();
        var actions = new List<SyncAction>(approved.Actions);
        var skipped = new List<SyncAction>(approved.SkippedActions);
        var notices = new List<Notice>(approved.Notices);
        var pending = held.Keys.Concat(unapproved).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only classification is needed here. Descendants are refreshed at resolution time,
        // after child effects finish; retaining their earlier snapshots wastes memory.
        var heldFolders = held.Values.Where(item =>
        {
            var root = item.Local == Side.A ? pair.A : pair.B;
            return _store.Live(root.RootId, [item.Path]).FirstOrDefault()?.Kind == ItemKind.Folder;
        }).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedDeletes = new System.Collections.Concurrent.ConcurrentDictionary<string, SyncAction>(StringComparer.OrdinalIgnoreCase);
        void RecordDelete(SyncAction? action, ActionResult result)
        {
            if (action?.Kind == ActionKind.Delete && result.Outcome == Outcome.Done)
                completedDeletes[FolderDelete.VictimKey(action.Target, action.Path)] = action;
        }
        var tally = new Tally(_settings);
        // The denominator grows by each resolved plan's own non-Hold actions; a held path that resolves to
        // nothing is not pre-counted, so the planned progress can still reach its total.
        tally.Plan(approved.Actions);
        tally.Plan(approved.SkippedActions);
        foreach (var action in approved.SkippedActions)
            tally.Add(pair, action, SafetySkipped(action));
        var skippedRecovery = 0;
        var evidenceUnavailable = false;
        // The merged ordinary/held lane must propagate the executor's backpressure instead of
        // draining bounded executor queues into an unbounded second queue. All writes use stop.Token;
        // if the consumer fails or is cancelled, finally cancels blocked writers before joining them.
        var results = Channel.CreateBounded<(SyncAction? Action, ActionResult Result)>(
            new BoundedChannelOptions(_settings.ResultBufferCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        var evidence = Channel.CreateUnbounded<(string Path, SyncRemoteDeletionEvidence Evidence)>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var ordinary = Task.Run(async () =>
        {
            var byId = approved.Actions.ToDictionary(action => action.ActionId);
            await foreach (var result in _executor.ApplyAsync(approved, _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), token))
            {
                var action = byId.GetValueOrDefault(result.ActionId);
                RecordDelete(action, result);
                await results.Writer.WriteAsync((action, result), token);
            }
        }, token);
        var probe = Task.Run(async () =>
        {
            // A transient failure (an exhausted read budget, a dropped connection), thrown or reported per path, ends one probe pass, never the
            // lane: the pass is retried after a backoff for the paths not yet resolved. Resolved paths are
            // checkpointed as they finish, so a retry repeats no completed lookup. A non-transient failure, or the
            // retry allowance running out, leaves the remaining paths pending for the next run, as before.
            var answered = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var backoff = TimeSpan.FromSeconds(30);
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        foreach (var group in held.Values.GroupBy(item => item.Local))
                        {
                            var cloud = group.Key == Side.A ? pair.B : pair.A;
                            if (_endpoints.Inventory(cloud) is not IDeferredDeletionEvidence source) continue;
                            var paths = group.Where(item => !heldFolders.Contains(item.Path) && !answered.ContainsKey(item.Path))
                                .Select(item => item.Path).ToArray();
                            // Released history failures already require a user choice. Do not make that
                            // choice depend on the same unavailable history probe ever succeeding.
                            var released = paths.Where(path => _store.SyncRetryAt(pair.PairId, path) is { Kind: "deletion-evidence", Held: not null })
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            foreach (var path in released)
                            {
                                answered.TryAdd(path, 0);
                                evidence.Writer.TryWrite((path, new SyncRemoteDeletionEvidence(null, "Released evidence requires a decision.", LookupFailed: true)));
                            }
                            paths = paths.Where(path => !released.Contains(path)).ToArray();
                            await source.ResolveDeletionEvidenceAsync(paths, (path, resolved) =>
                            {
                                answered.TryAdd(path, 0);
                                evidence.Writer.TryWrite((path, resolved));
                            }, token);
                        }
                        // A per-path lookup failure comes back as LookupFailed without a callback and without an
                        // exception, so the pass ends normally; any path still unanswered gets another pass.
                        var unanswered = held.Values.Count(item => !heldFolders.Contains(item.Path) && !answered.ContainsKey(item.Path));
                        if (unanswered == 0 || attempt >= 2) break;
                        Trace($"deletion evidence pass {attempt} left {unanswered} path(s) unanswered; retrying in {backoff.TotalSeconds:N0}s");
                        await Task.Delay(backoff, token);
                        backoff = TimeSpan.FromSeconds(Math.Min(300, backoff.TotalSeconds * 2));
                    }
                    catch (Exception error) when (error is not PairPausedException && !ct.IsCancellationRequested && attempt < 2 &&
                        ProviderTransientFailure.IsTransient(error, ct))
                    {
                        Trace($"deletion evidence pass {attempt} interrupted ({error.GetType().Name}); {answered.Count} resolved, " +
                            $"retrying the rest in {backoff.TotalSeconds:N0}s");
                        await Task.Delay(backoff, token);
                        backoff = TimeSpan.FromSeconds(Math.Min(300, backoff.TotalSeconds * 2));
                    }
                }
            }
            catch (Exception error) when (error is not PairPausedException && !ct.IsCancellationRequested)
            {
                // A failed service-level probe with no answers is an outage, not a collection of failed items.
                // Keep the reviewed run resumable; ordinary siblings that already succeeded remain committed.
                evidenceUnavailable = answered.IsEmpty && ProviderTransientFailure.IsTransient(error, ct);
                Trace("deletion evidence pending: " + error.GetType().Name);
            }
            finally { evidence.Writer.TryComplete(); }
        }, token);
        var resolve = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            var last = TimeSpan.Zero;
            var uploads = 0;
            var deletes = 0;
            var filesDone = 0;
            var foldersDone = 0;
            // Completed held paths sampled by time, so the rate reflects recent work instead of the whole run.
            var samples = new Queue<(TimeSpan At, int Count)>();
            samples.Enqueue((TimeSpan.Zero, 0));
            var folders = held.Values.Where(item => heldFolders.Contains(item.Path)).ToArray();
            var sync = new object();
            var gate = new SemaphoreSlim(16);
            var inflight = new List<Task>();
            try
            {
                await foreach (var item in evidence.Reader.ReadAllAsync(token))
                {
                    if (!held.TryGetValue(item.Path, out var rule))
                    { Trace("deletion evidence path mismatch: " + item.Path); continue; }
                    if (!string.Equals(rule.Path, item.Path, StringComparison.Ordinal))
                        Trace("deletion evidence spelling mismatch: " + rule.Path + " -> " + item.Path);
                    lock (sync) { if (heldFolders.Contains(rule.Path) || !pending.Remove(rule.Path)) continue; }
                    // B-185: resolved held files run a bounded number at a time (one at a time measured 0.1/s live,
                    // ~24 h for 8,684 paths). Each keeps its own plan, guard and executor pass; folders run
                    // afterwards, one depth level at a time, once their held descendants have outcomes.
                    inflight.RemoveAll(task => task.IsCompleted);
                    await gate.WaitAsync(token);
                    var evidenceFor = item.Evidence;
                    inflight.Add(Task.Run(async () =>
                    {
                        try { await ExecuteResolvedAsync(rule, evidenceFor); }
                        finally { gate.Release(); }
                    }, token));
                }
                await Task.WhenAll(inflight);
                // Ordinary descendants must finish too before a held folder's planner expansion.
                await ordinary;
                foreach (var level in folders.GroupBy(item => PairPath.Depth(item.Path)).OrderByDescending(group => group.Key))
                {
                    HeldGuard(refresh: true);
                    inflight.Clear();
                    foreach (var folder in level)
                    {
                        await gate.WaitAsync(token);
                        inflight.Add(Task.Run(async () =>
                        {
                            try
                            {
                                lock (sync) pending.Remove(folder.Path);
                                await ExecuteResolvedAsync(folder, null);
                            }
                            finally { gate.Release(); }
                        }, token));
                    }
                    // Same-depth folders are disjoint; every child finishes before any parent starts.
                    // Their store refreshes stay serial below (one open generation per root).
                    await Task.WhenAll(inflight);
                }
            }
            catch (Exception error) when (error is not PairPausedException && !ct.IsCancellationRequested)
            {
                Trace("deletion evidence classification pending: " + error.GetType().Name);
                // pending already contains every unresolved path. Do not re-hold completed
                // siblings just because a later channel/ordinary-stage operation failed.
            }
            finally
            {
                // Every launched held worker finishes (or observes cancellation) before the lane completes
                // and before its gate is disposed, so no effect runs or commits after the run reports done.
                try { await Task.WhenAll(inflight); }
                catch (Exception error) when (error is not PairPausedException && !ct.IsCancellationRequested) { Trace("held file worker: " + error.GetType().Name); }
                catch (PairPausedException) { throw; }
                catch (Exception) { /* Cancelled run: the workers have finished; the original cause stands. */ }
                finally { gate.Dispose(); }
            }

            async Task ExecuteResolvedAsync(HeldDeletion rule, SyncRemoteDeletionEvidence? deletionEvidence)
            {
                try
                {
                    var heldRetry = _store.SyncRetryAt(pair.PairId, rule.Path);
                    // A released-evidence decision owns the path until the user chooses. A replayed held
                    // lane (a repeated first-sync apply, for example) must never act under stale prepare
                    // authority while its decision notice is still open.
                    if (heldRetry?.Kind == "decision")
                    { lock (sync) pending.Add(rule.Path); return; }
                    if (parkedFootprints.Covers(rule.Path) ||
                        heldRetry?.Kind != "deletion-evidence" && parkedFootprints.Contains(rule.Path))
                    { lock (sync) pending.Add(rule.Path); return; }
                    var isFolder = heldFolders.Contains(rule.Path);
                    var cloud = rule.Local == Side.A ? pair.B : pair.A;
                    if (heldRetry is { Kind: "deletion-evidence" } released)
                    {
                        // Protection also gates evidence retirement, not just the eventual delete.
                        // A held root may not be observable as an item; that is not proof of absence.
                        bool evidenceHeld;
                        lock (_heldRefreshSync)
                        {
                            HeldGuard();
                            evidenceHeld = evidenceHolds.Covers(rule.Path);
                        }
                        if (evidenceHeld)
                        { lock (sync) pending.Add(rule.Path); return; }
                        var survivorRoot = rule.Local == Side.A ? pair.A : pair.B;
                        var survivor = await _endpoints.Writer(survivorRoot).ObserveAsync(rule.Path, token);
                        var remoteNow = await _endpoints.Writer(cloud).ObserveAsync(rule.Path, token);
                        if (remoteNow is not null || survivor is null)
                        {
                            _store.ClearSyncRetry(pair.PairId, rule.Path);
                            _store.RemoveDeletionEvidence(pair.PairId, cloud.RootId, [rule.Path]);
                            foreach (var oldNotice in _store.OpenNotices(pair.PairId).Where(item =>
                                PairPath.Same(item.Path, rule.Path) && item.ReasonKey is "deletion-review" or "deletion-history-unavailable"))
                                if (oldNotice.NoticeId is not null) _store.CloseNotice(oldNotice.NoticeId, "recreated");
                            if (remoteNow is not null)
                                _store.SaveSyncRetry(pair.PairId, new(rule.Path, "post-preview", 0, "", DateTimeOffset.MinValue));
                            lock (sync) pending.Remove(rule.Path);
                            return; // Current inventory is ordinary sync input on the next run, never old delete authority.
                        }
                        // Released history always ends in an explicit decision; it is never retried forever.
                        // A hash is not required for Delete: the proposal is stamped and Recovery-first, so
                        // the executor re-checks the victim. A folder or a flagged survivor gets no deletion
                        // authority at all: only keep/restore and ignore are offered, and the ordinary safety
                        // guards still refuse a restore that would write over protected content.
                        var deletable = survivor.Kind == ItemKind.File && survivor.Flags == ItemFlags.None;
                        var proposal = new SyncAction(PlanIds.ActionId(pair.PairId, approved.Gen, 40, ActionKind.Delete,
                            rule.Local, rule.Path), 40, ActionKind.Delete, rule.Local, rule.Path, null, null,
                            survivor, true, null) { OppositeAbsent = true };
                        var attention = new Notice(pair.PairId,
                            deletable ? NoticeChoices.DeletionReviewRule : NoticeChoices.DeletionReviewRestoreOnlyRule,
                            NoticeKind.UserDecision, rule.Path,
                            deletable
                                ? "Dropbox reports this file deleted. Nothing was deleted or uploaded. Choose Delete with Recovery, Keep and restore to Dropbox, or Ignore."
                                : "Dropbox reports this item deleted. Nothing was deleted or uploaded. Choose Keep and restore to Dropbox, or Ignore to exclude this path from sync.",
                            "deletion-review");
                        _store.SaveSyncRetry(pair.PairId, released with { Kind = "decision", RetryAfter = DateTimeOffset.MinValue,
                            HeldReviewed = false, ReviewedActions = null, ProposedActions = [proposal], PendingNotice = attention });
                        WriteNotices([attention]);
                        foreach (var oldNotice in _store.OpenNotices(pair.PairId).Where(item => PairPath.Same(item.Path, rule.Path) &&
                            item.ReasonKey is "sync-retry" or "deletion-evidence-pending"))
                            if (oldNotice.NoticeId is not null) _store.CloseNotice(oldNotice.NoticeId, "decision-required");
                        lock (sync) { notices.Add(attention); pending.Add(rule.Path); }
                        return;
                    }
                    var deleteFolder = isFolder &&
                        _endpoints.Inventory(cloud) is IDeferredDeletionEvidence source &&
                        source.IsAtOrUnderExactTombstone(rule.Path);
                    var one = await ResolveHeldPlanAsync(pair, approved, rule, deletionEvidence,
                        !isFolder ? [] : held.Values.ToArray(),
                        !isFolder ? [] : completedDeletes.Values.ToArray(), deleteFolder, token);
                    if (one is null) { lock (sync) pending.Add(rule.Path); return; }
                    var resolved = one.Plan;
                    var guard = HeldGuard();
                    resolved = ApplySafetyFilter(pair, guard,
                        resolved with { NarrowedIgnoreRules = approved.NarrowedIgnoreRules });
                    if (resolved.SkippedActions.Count > 0)
                    {
                        lock (sync)
                        {
                            skipped.AddRange(resolved.SkippedActions);
                            notices.AddRange(resolved.Notices);
                            pending.Remove(rule.Path);
                        }
                        WriteNotices(resolved.Notices);
                        tally.Plan(resolved.SkippedActions);
                        foreach (var action in resolved.SkippedActions)
                            await results.Writer.WriteAsync((action, SafetySkipped(action)), token);
                        // A resolved row is atomic: do not apply the other half when a protected effect was skipped.
                        return;
                    }
                    if (DropUnderNewLocalLinks(pair, resolved).Actions.Count != resolved.Actions.Count)
                    { lock (sync) pending.Add(rule.Path); return; }
                    // A folder may only expand once every held descendant has an outcome. Unknown or
                    // unfinished descendants keep the folder pending; the normal victim review still applies.
                    bool blocked;
                    lock (sync) blocked = resolved.Actions.Any(FolderDelete.Expands) && pending.Any(path => PairPath.IsUnder(path, rule.Path));
                    if (blocked) { lock (sync) pending.Add(rule.Path); return; }
                    if (allowUnreviewedEvidence && resolved.Actions.Any(action => action.Kind == ActionKind.Delete) &&
                        (reviewedDeletes is null || !reviewedDeletes.Contains(FolderDelete.VictimKey(rule.Local, rule.Path))))
                    {
                        // Evidence proves history, never consent. Preserve the proposed stamped actions separately
                        // from reviewed actions until the user explicitly chooses delete.
                        var retry = _store.SyncRetryAt(pair.PairId, rule.Path) ??
                            new SyncRetry(rule.Path, "decision", 0, "", DateTimeOffset.MaxValue, Held: rule);
                        var notice = new Notice(pair.PairId, 40, NoticeKind.UserDecision, rule.Path,
                            "Dropbox history indicates a deletion that was not reviewed. Choose delete, keep/restore, or ignore; nothing was deleted.",
                            "deletion-review");
                        // Persist protection first, then the notice. A failed notice write retains
                        // PendingNotice on this same row and remains scheduled for retry.
                        _store.SaveSyncRetry(pair.PairId, retry with
                        {
                            Kind = "decision", RetryAfter = DateTimeOffset.MinValue, PendingNotice = notice,
                            ReviewedActions = null, HeldReviewed = false, ProposedActions = resolved.Actions
                        });
                        WriteNotices([notice]);
                        foreach (var oldNotice in _store.OpenNotices(pair.PairId).Where(item =>
                            PairPath.Same(item.Path, rule.Path) && item.ReasonKey is "sync-retry" or "deletion-evidence-pending"))
                            if (oldNotice.NoticeId is not null) _store.CloseNotice(oldNotice.NoticeId, "decision-required");
                        lock (sync) { notices.Add(notice); pending.Add(rule.Path); }
                        return;
                    }
                    RefuseUnreviewedDeletes(pair, resolved, reviewedDeletes);
                    int count, uploadsNow, deletesNow;
                    var report = false;
                    var filesLeft = 0;
                    var foldersLeft = 0;
                    var rate = 0d;
                    lock (sync)
                    {
                        actions.AddRange(resolved.Actions);
                        notices.AddRange(resolved.Notices);
                        if (resolved.Actions.Any(action => action.Kind == ActionKind.Delete)) deletes++; else uploads++;
                        if (isFolder) foldersDone++; else filesDone++;
                        count = uploads + deletes; uploadsNow = uploads; deletesNow = deletes;
                        if (watch.Elapsed - last >= TimeSpan.FromSeconds(10))
                        {
                            last = watch.Elapsed;
                            var now = watch.Elapsed;
                            samples.Enqueue((now, count));
                            // Keep one sample just outside the window as the baseline for the last sixty seconds.
                            while (samples.Count > 2 && now - samples.Peek().At > TimeSpan.FromSeconds(60))
                                samples.Dequeue();
                            var (sampleAt, sampleCount) = samples.Peek();
                            var seconds = (now - sampleAt).TotalSeconds;
                            rate = seconds > 0 ? (count - sampleCount) / seconds : 0;
                            filesLeft = Math.Max(0, held.Count - folders.Length - filesDone);
                            foldersLeft = Math.Max(0, folders.Length - foldersDone);
                            report = true;
                        }
                    }
                    WriteNotices(resolved.Notices);
                    if (report)
                    {
                        var eta = rate > 0 && count < held.Count
                            ? $", ETA {Math.Ceiling((held.Count - count) / rate / 60):N0}m" : string.Empty;
                        await SyncByteProgressStream.ReportCurrentProcessAsync("Deletion evidence",
                            $"{count:N0}/{held.Count:N0} resolved ({uploadsNow:N0} upload, {deletesNow:N0} delete), " +
                            $"{filesLeft:N0} files and {foldersLeft:N0} folders remaining, {rate:F1}/s{eta}",
                            ProcessMessageKind.Notice);
                    }
                    tally.Plan(resolved.Actions);
                    await foreach (var result in _executor.ApplyAsync(resolved, _endpoints.Writer(pair.A), _endpoints.Writer(pair.B), token))
                    {
                        if (result.Outcome != Outcome.Done && !result.ProviderNameRefused && !result.ParentNameRefused && !result.Quarantined && !result.DefenderGrantHeld)
                            lock (sync) pending.Add(rule.Path);
                        var action = resolved.Actions.FirstOrDefault(action => action.ActionId == result.ActionId);
                        // Decision 69 counts only a no-Recovery delete that actually finished.
                        if (one.RecoverySkipped && result.Outcome == Outcome.Done &&
                            action is { Kind: ActionKind.Delete, RecoveryFirst: false })
                            Interlocked.Increment(ref skippedRecovery);
                        RecordDelete(action, result);
                        await results.Writer.WriteAsync((action, result), token);
                    }
                }
                catch (Exception error) when (error is not PairPausedException && ScopeFailure(error, ct))
                {
                    lock (sync) pending.Add(rule.Path);
                    SaveFailedRetry(pair, rule.Path, "Evidence: " + error.Message, _store.SyncRetryAt(pair.PairId, rule.Path));
                    Trace("held path pending: " + error.GetType().Name);
                }
            }
        }, token);
        var finish = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await Task.WhenAll(ordinary, probe, resolve); }
            // WhenAll has joined every producer. Cancelling the reader token here would mask
            // the original pause/failure with an unrelated OperationCanceledException.
            catch (Exception error) { failure = error; }
            finally { results.Writer.TryComplete(failure); }
        }, CancellationToken.None);
        try
        {
            await foreach (var item in results.Reader.ReadAllAsync(token))
            {
                tally.Add(pair, item.Action, item.Result);
                throttle.Report(RunPhases.Applying, item.Action?.Path, tally.ProgressDone(), tally.Total, tally.ExtraDone, tally.KindProgress());
            }
            await finish;
        }
        catch (PairPausedException paused)
        {
            paused.Report = BuildReport(pair.PairId, approved, tally, false) with { Outcome = RunOutcomes.Paused };
            throw;
        }
        finally
        {
            stop.Cancel();
            try { await finish; }
            finally { AddProviderNameRefusalNotice(pair); }
        }
        Trace($"{skippedRecovery} held deletes skipped Recovery (identical to Dropbox history)");
        return new(approved with { Actions = actions, Notices = notices, SkippedActions = skipped }, tally,
            pending.Order(StringComparer.Ordinal).ToArray(), evidenceUnavailable);
    }

    private async Task<IReadOnlyList<ItemState>> RefreshDescendantsAsync(RootDef root, Side side,
        string folder, IReadOnlyList<HeldDeletion> held, IReadOnlyList<SyncAction> completedDeletes,
        CancellationToken ct)
    {
        var heldPaths = held.Where(item => PairPath.IsUnder(item.Path, folder))
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletedPaths = completedDeletes.Where(item => item.Target == side)
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _store.LiveUnder(root.RootId, [folder]).Where(item => PairPath.IsUnder(item.Path, folder)).ToArray();
        var changes = new List<Change>();
        var current = new List<ItemState>();
        foreach (var row in rows)
        {
            if (!heldPaths.Contains(row.Path) && !deletedPaths.Contains(row.Path))
            {
                current.Add(row);
                continue;
            }
            var now = deletedPaths.Contains(row.Path) ? null : await _endpoints.Writer(root).ObserveAsync(row.Path, ct);
            if (now is null) changes.Add(new Change(ChangeKind.Delete, row.ObjectId, row.Path, null, null));
            else
            {
                // Observation has no application flags; retain an identity-bound refusal in this
                // planner context just as storage retains it when the refreshed row is written.
                if (now.ObjectId == row.ObjectId && now.Path == row.Path)
                    now = now with { Flags = now.Flags | (row.Flags & ItemFlags.ProviderNameRefused) };
                current.Add(now);
                changes.Add(new Change(ChangeKind.Upsert, now.ObjectId, now.Path, now, null));
            }
        }
        // Committed effects update baselines, not inventory. Refresh the known descendants before
        // normal folder expansion so it cannot replay already-finished child deletes from stale rows.
        if (changes.Count > 0)
        {
            var cursor = _store.Cursor(root.RootId) ?? string.Empty;
            // The preview's scan generations are already closed. Use a short-lived refresh
            // generation rather than writing into the approved plan's generation.
            // Same-depth folders are disjoint, but refreshes must stay serial: the store allows
            // only one open generation per root, and BeginGeneration closes its predecessor.
            lock (_heldRefreshSync)
            {
                var refreshGen = _store.BeginGeneration(root.RootId, "held-refresh");
                // Invalidate before writing: even a failed write must not leave a reusable stale guard.
                _heldRefreshVersion++;
                _store.WriteDelta(new Delta(root.RootId, side, refreshGen, cursor, cursor, changes, false, []));
                _store.CompleteGeneration(refreshGen, cursor, false);
            }
        }
        return current;
    }

    private async Task<HeldPlanResult?> ResolveHeldPlanAsync(PairDef pair, Plan approved, HeldDeletion held,
        SyncRemoteDeletionEvidence? deletionEvidence, IReadOnlyList<HeldDeletion> heldPaths,
        IReadOnlyList<SyncAction> completedDeletes, bool deleteFolder, CancellationToken ct)
    {
        // Answered Unknown settles the probe, not the held item. Never turn failed history into
        // a one-sided create (restoration), deletion authority or a baseline; the caller retains pending.
        if (deletionEvidence is { LookupFailed: true }) return null;
        var local = held.Local == Side.A ? pair.A : pair.B;
        var cloud = held.Local == Side.A ? pair.B : pair.A;
        if (!string.Equals(local.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cloud.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase)) return null;
        var current = await _endpoints.Writer(local).ObserveAsync(held.Path, ct);
        if (current is null || current.Flags != ItemFlags.None ||
            current.Kind == ItemKind.File && current.Hash is null) return null;
        var remote = await _endpoints.Writer(cloud).ObserveAsync(held.Path, ct);
        // Decision 78 compares current content with the deleted revision, supplied by the same probe.
        var deleted = deletionEvidence?.DeletedUtc;
        var revisionHash = deletionEvidence?.ContentHash;
        var row = new PlanRow(null, held.Local == Side.A ? current : remote,
            held.Local == Side.A ? remote : current,
            new Evidence(held.Local == Side.B && deleted is not null, held.Local == Side.A && deleted is not null,
                true, true, held.Local == Side.B ? deleted : null, held.Local == Side.A ? deleted : null,
                DeletedHashA: held.Local == Side.B ? revisionHash : null,
                DeletedHashB: held.Local == Side.A ? revisionHash : null));
        var rows = new List<PlanRow> { row };
        if (current.Kind == ItemKind.Folder)
        {
            // Surviving descendants must be visible to the planner's keep-folder postpass. Their own
            // effects have already finished; joining committed rows prevents a folder delete from
            // removing a child whose newer edit won its deletion decision.
            var a = await RefreshDescendantsAsync(pair.A, Side.A, held.Path, heldPaths, completedDeletes, ct);
            var b = await RefreshDescendantsAsync(pair.B, Side.B, held.Path, heldPaths, completedDeletes, ct);
            var ignoreRules = PairInclude.IgnoreRules(pair);
            var folderRules = ignoreRules.Where(rule => rule.Kind == SyncIgnoreKind.FolderName).ToArray();
            var fileNameRules = ignoreRules.Where(rule => rule.Kind == SyncIgnoreKind.FileName).ToArray();
            var (refusedA, refusedB) = ProviderRefusedPaths.Load(_store, pair);
            ItemState HeldDescendant(ItemState item, ProviderRefusedPaths refused)
            {
                var providerRefused = refused.Contains(item);
                if (!providerRefused && !SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, ignoreRules)) return item;
                // Decision 80(b): held-folder emptiness only discounts small FileName-rule files.
                // Other ignored content stays inert but MUST block the planner's fallback folder delete too.
                var disposable = !providerRefused && item.Kind == ItemKind.File && item.Size <= 64 * 1024 &&
                    (item.Flags & ~(ItemFlags.Ignored | ItemFlags.CarriedIgnore)) == ItemFlags.None &&
                    SyncIgnorePolicy.IsIgnored(item.Path, false, fileNameRules) &&
                    !SyncIgnorePolicy.IsIgnored(item.Path, false, folderRules);
                return item with { Flags = (item.Flags | ItemFlags.Ignored) & ~ItemFlags.CarriedIgnore |
                    (disposable ? ItemFlags.CarriedIgnore : ItemFlags.None) };
            }
            a = a.Select(item => HeldDescendant(item, refusedA)).ToArray();
            b = b.Select(item => HeldDescendant(item, refusedB)).ToArray();
            if (deleteFolder && remote is null && a.All(item => item.Flags.HasFlag(ItemFlags.CarriedIgnore)) &&
                b.All(item => item.Flags.HasFlag(ItemFlags.CarriedIgnore)))
            {
                // An exact current tombstone authorizes an empty folder, discounting only the small
                // FileName-rule files above. Ignored data needing Recovery keeps a held folder instead.
                var action = new SyncAction(PlanIds.ActionId(pair.PairId, approved.Gen, 23, ActionKind.Delete,
                    held.Local, held.Path), 23, ActionKind.Delete, held.Local, held.Path, null, null, current, false, null)
                    { OppositeAbsent = true };
                return new HeldPlanResult(FreezeFolderDeletes(pair, new Plan(pair.PairId, approved.Gen, [action], [])),
                    false);
            }
            // Either endpoint can have moved outside the folder; query both indexed prefixes,
            // deduplicate shared baselines, and retain the original strict-descendant semantics.
            var baselines = _store.BaselinesTouching(pair.PairId, Side.A, [], [held.Path])
                .Concat(_store.BaselinesTouching(pair.PairId, Side.B, [], [held.Path])).Distinct()
                .Where(item => PairPath.IsUnder(item.A.Path, held.Path) || PairPath.IsUnder(item.B.Path, held.Path)).ToArray();
            rows.AddRange(Planner.Join(baselines, a, b, NoDeletions, NoDeletions,
                new Completeness(true, true, new HashSet<string>(), new HashSet<string>()), true));
        }
        var options = (approved.FirstSyncOptions ?? Options(pair, true, null, null)) with
            { ObservedAt = _time.GetUtcNow() };
        // Folder decisions are derived from executed children, never a folder timestamp or a remove choice
        // that could remove a surviving child. Descendant rows are context only, not new work or notices.
        if (current.Kind == ItemKind.Folder) options = options with { OneSided = null };
        var plan = Planner.Decide(pair.PairId, approved.Gen, rows, options);
        var filtered = plan with
        {
            Actions = plan.Actions.Where(action => PairPath.Same(action.Path, held.Path)).ToArray(),
            Notices = plan.Notices.Where(notice => PairPath.Same(notice.Path, held.Path)).ToArray()
        };
        // Unknown content is still pending, not a resolved zero-action upload.
        if (filtered.Notices.Any(notice => notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key)) return null;
        // Decisions 69/78: rule 19 deletes only when the current local file carries an
        // exactly equal, known content hash from Dropbox's last revision.
        var skippedRecovery = current.Kind == ItemKind.File && filtered.Actions.Any(action =>
            action.Kind == ActionKind.Delete && !action.RecoveryFirst &&
            action.Target == held.Local && PairPath.Same(action.Path, held.Path));
        return new HeldPlanResult(FreezeFolderDeletes(pair, filtered), skippedRecovery);
    }
}
