namespace Massdex.SyncShark.V2;

/// <summary>Coordinates one pair's runs: reconcile, inventory, hash, plan, execute and commit cursors.</summary>
public sealed partial class SyncRun : ISyncRun
{
    private const int OutsidePairRule = 33;
    private const int UnconfirmedRule = 30;
    private const string IncompleteFirstSync =
        "A side could not be read in full, so no first-sync plan was made. Preview the first sync again.";

    private readonly IStateStore _store;
    private readonly IExecutor _executor;
    private readonly IEndpointProvider _endpoints;
    private readonly V2Settings _settings;
    private readonly TimeProvider _time;
    private readonly Func<PairDef, IReadOnlyList<string>> _productOwned;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stopGate = new();
    private CancellationTokenSource? _activeSource;
    private TaskCompletionSource? _activeDone;

    /// <param name="productOwned">
    /// The pair-relative folders the product owns inside a pair's local roots (data, spool, Recovery); they are
    /// protected like exclusions on both sides. Null means none.
    /// </param>
    public SyncRun(IStateStore store, IExecutor executor, IEndpointProvider endpoints, V2Settings settings,
        TimeProvider time, Func<PairDef, IReadOnlyList<string>>? productOwned = null)
    {
        _productOwned = productOwned ?? (_ => []);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (settings.LocalHashCheckpointFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings.LocalHashCheckpointFiles));
    }

    /// <inheritdoc />
    public Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        PlanOfAsync(pairId, null, null, null, null, progress, ct);

    /// <summary>Previews the first sync, applying the user's per-path winners where supplied.</summary>
    public Task<Plan> PreviewFirstSyncAsync(string pairId, IReadOnlyDictionary<string, Side?>? winners,
        IProgress<RunProgress> progress, CancellationToken ct) =>
        PlanOfAsync(pairId, winners, null, null, null, progress, ct);

    /// <summary>Previews the first sync with the user's per-path winners and per-folder one-sided choices.</summary>
    public Task<Plan> PreviewFirstSyncAsync(string pairId, IReadOnlyDictionary<string, Side?>? winners,
        IReadOnlyList<OneSidedChoice>? oneSided, IProgress<RunProgress> progress, CancellationToken ct) =>
        PlanOfAsync(pairId, winners, oneSided, null, null, progress, ct);

    /// <summary>
    /// Previews the first sync with the user's winners, one-sided choices and rebuildable folder types to stop
    /// syncing (docs/design/rebuildables.md). The chosen types are detected over the local inventory this scan
    /// persists, so the same store the report reads decides the plan.
    /// </summary>
    public Task<Plan> PreviewFirstSyncAsync(string pairId, IReadOnlyDictionary<string, Side?>? winners,
        IReadOnlyList<OneSidedChoice>? oneSided, IReadOnlyList<string>? rebuildableTypes,
        IProgress<RunProgress> progress, CancellationToken ct) =>
        PlanOfAsync(pairId, winners, oneSided, rebuildableTypes?.Select(id => new RebuildableChoice(id)).ToArray(), null, progress, ct);

    /// <summary>
    /// The prepare's preview, returning the resolved rebuildable selection alongside the plan: the caller reports and
    /// freezes exactly this one detection, never a second one of its own.
    /// </summary>
    internal Task<(Plan Plan, RebuildableResolution Rebuildables)> PreviewFirstSyncResolvedAsync(string pairId,
        IReadOnlyDictionary<string, Side?>? winners, IReadOnlyList<OneSidedChoice>? oneSided,
        IReadOnlyList<RebuildableChoice>? rebuildableTypes, IProgress<RunProgress> progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? inventoryCursors = null) =>
        RunExclusiveAsync(progress,
            (sink, token) => PreviewCoreAsync(pairId, winners, oneSided, rebuildableTypes, null, null, sink, token,
                inventoryCursors), ct);

    /// <summary>
    /// The apply's preview: its rebuildable folders come from the reviewed prepare, never from detection, so an apply
    /// can only use a subset of the paths the user saw — it never adds one. This is the apply's single revalidation,
    /// and it hands back the very resolution the plan is generated from, so the executed plan and the completion
    /// status (and the exclusions) can never describe different sets.
    /// </summary>
    internal Task<(Plan Plan, RebuildableResolution Rebuildables)> PreviewFirstSyncApplyResolvedAsync(string pairId,
        IReadOnlyDictionary<string, Side?>? winners, IReadOnlyList<OneSidedChoice>? oneSided,
        IReadOnlyList<RebuildableFolder>? rebuildables, IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare,
        IProgress<RunProgress> progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? inventoryCursors = null,
        IReadOnlyList<string>? narrowedIgnoreRules = null) =>
        RunExclusiveAsync(progress,
            (sink, token) => PreviewCoreAsync(pairId, winners, oneSided, null, rebuildables, matchedAtPrepare, sink,
                token, inventoryCursors, captureMatchedBothSides: false, narrowedIgnoreRules: narrowedIgnoreRules), ct);

    private Task<Plan> PlanOfAsync(string pairId, IReadOnlyDictionary<string, Side?>? winners,
        IReadOnlyList<OneSidedChoice>? oneSided, IReadOnlyList<RebuildableChoice>? rebuildableTypes,
        IReadOnlyList<RebuildableFolder>? frozenRebuildables, IProgress<RunProgress> progress, CancellationToken ct) =>
        RunExclusiveAsync(progress, async (sink, token) =>
            (await PreviewCoreAsync(pairId, winners, oneSided, rebuildableTypes, frozenRebuildables, null, sink, token))
                .Plan, ct);

    /// <summary>
    /// The rebuildable resolution for one preview: the chosen types detected over the local inventory the scan already
    /// persisted, or — at apply — exactly the set the reviewed prepare froze, minus any path whose local folder is
    /// gone, whose category's markers no longer qualify it, or whose cloud counterpart is not a folder. Nothing is
    /// resolved when the pair is not one local side plus one Dropbox side.
    /// </summary>
    private RebuildableResolution ResolveRebuildables(PairDef pair, IReadOnlyList<RebuildableChoice>? types,
        IReadOnlyList<RebuildableFolder>? frozen)
    {
        if (types is null && frozen is null) return RebuildableResolution.None;
        if (RebuildableSelection.Sides(pair) is not { } sides) return RebuildableResolution.None;
        // An empty reviewed selection needs no inventories, but still describes the pair's cloud side.
        if (frozen is { Count: 0 }) return new RebuildableResolution([], [], sides.Cloud);
        var localRoot = sides.Local == Side.A ? pair.A : pair.B;
        var cloudRoot = sides.Cloud == Side.A ? pair.A : pair.B;
        if (frozen is not null)
        {
            var prefixes = frozen.Select(folder => folder.Path).ToArray();
            // Some markers are siblings (package.json, *.csproj, next.config.*), not descendants.
            // Read their immediate directories, never widen to the parents' entire subtrees.
            var parents = prefixes.Select(path => RowCodec.Split(path).Parent).Distinct(StringComparer.OrdinalIgnoreCase);
            var local = prefixes.SelectMany(path => _store.LiveChildren(localRoot.RootId, path))
                .Concat(parents.SelectMany(parent => _store.LiveChildren(localRoot.RootId, parent)))
                .DistinctBy(item => item.Path, StringComparer.Ordinal).ToArray();
            return new RebuildableResolution([], RebuildableDetector.Retained(frozen,
                local, localRoot.RootPath, _store.Live(cloudRoot.RootId, prefixes)), sides.Cloud);
        }
        // Detection must still see all local candidates. Only detected folders need cloud occupancy.
        var detected = RebuildableDetector.Detect(_store.Live(localRoot.RootId), RebuildableDetector.DiskReader(localRoot.RootPath));
        var cloud = _store.Live(cloudRoot.RootId, detected.Select(folder => folder.Path));
        return new RebuildableResolution(detected, RebuildableDetector.ChosenScoped(detected, cloud, types!), sides.Cloud);
    }

    /// <summary>The settings this run was built with; the CLI edge reads retention values for its reports.</summary>
    internal V2Settings Settings => _settings;

    /// <inheritdoc />
    public Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> progress,
        CancellationToken ct) => ApplyAsync(pairId, approved, progress, ct, null);

    /// <summary>
    /// Applies a first-sync plan while re-checking its delete victims at execution time (decision 54 addendum): if the
    /// plan would now delete anything outside <paramref name="reviewedDeletes"/> (the set the reviewed prepare showed),
    /// the apply is refused before any effect. A null set skips the check (non-review callers).
    /// </summary>
    public Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> progress,
        CancellationToken ct, IReadOnlySet<string>? reviewedDeletes) =>
        ApplyAsync(pairId, approved, progress, ct, reviewedDeletes, null);

    /// <summary>
    /// The first-sync apply with the resolution its plan was generated from: the paths whose cloud removal finished,
    /// or that the cloud never held, are saved as pair exclusions in the same durable step that marks the first sync
    /// done, so there is never a window in which the first sync is done but its exclusions are missing. The resolution
    /// is used exactly as given — the apply never revalidates it again.
    /// </summary>
    internal Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> progress,
        CancellationToken ct, IReadOnlySet<string>? reviewedDeletes, RebuildableResolution? rebuildables) =>
        RunExclusiveAsync(progress,
            (sink, token) => ApplyCoreAsync(pairId, approved, sink, reviewedDeletes, rebuildables, token), ct);

    /// <inheritdoc />
    public Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        RunExclusiveAsync(progress, (sink, token) => IncrementalCoreAsync(pairId, null, null, sink, token), ct);

    /// <inheritdoc />
    public async Task StopAsync()
    {
        TaskCompletionSource? done;
        lock (_stopGate)
        {
            _activeSource?.Cancel();
            done = _activeDone;
        }
        if (done is not null) await done.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the named roots' stored cursors so each root's next scan is a full scan. It takes the same exclusive
    /// gate as a run, so a cursor is never reset underneath a run in flight: that run's CompleteGeneration would
    /// otherwise write its old cursor back and lose the reset.
    /// </summary>
    public Task RequestRescanAsync(IReadOnlyList<string> rootIds, CancellationToken ct) =>
        RunExclusiveAsync(null, (_, _) =>
        {
            foreach (var rootId in rootIds)
            {
                _store.ForgetCursor(rootId);
                if (_store is SqliteStateStore sql) sql.ForgetInventoryObservation(rootId);
            }
            return Task.FromResult(true);
        }, ct);

    // The single gate every run passes through. It opens the run's live-progress pump around the caller's sink, so
    // the whole run gets the shared byte-progress seam's process messages and the quiet-run heartbeat, and hands the
    // body that pump's sink (a no-op when the caller passed none) rather than the raw sink.
    private async Task<T> RunExclusiveAsync<T>(IProgress<RunProgress>? progress,
        Func<IProgress<RunProgress>, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        // The pump and the in-flight table open before the gate, so a run queued behind another is still visible
        // (the gate wait is itself a tracked operation) rather than silently blocked.
        var queuedAt = _time.GetUtcNow();
        using var activity = RunActivity.Begin(progress, _settings, _time);
        var gateWait = RunOperations.Begin(RunOperationKind.Gate, "run gate");
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gateWait.Dispose();
        }
        // Admission: a caller that withdrew (pause, stop) while queued behind another run never starts.
        if (ct.IsCancellationRequested)
        {
            _gate.Release();
            ct.ThrowIfCancellationRequested();
        }
        using var instrumentation = RunInstrumentation.Begin(_settings, _time);
        var queued = _time.GetUtcNow() - queuedAt;
        if (queued >= TimeSpan.FromSeconds(1))
            activity.Progress.Report(new RunProgress("Queued", null, 0, 0) { Message = $"Run queued for {queued.TotalSeconds:N1}s" });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stopGate)
        {
            _activeSource = linked;
            _activeDone = done;
        }
        try
        {
            // One execution-batch account-verification flight per adapter+binding+token: the scope is begun
            // before the run body starts, so every worker task, concurrent queue and effect it spawns inherits
            // the AsyncLocal and shares one get_current_account flight instead of verifying per operation. A token
            // refresh produces a new key and therefore exactly one new verification for the new generation.
            await using var accountVerification = SyncProviderAccountVerificationScope.Begin(linked.Token);
            var result = await body(activity.Progress, linked.Token).ConfigureAwait(false);
            if (result is not RunReport report) return result;
            var space = FreeSpacePresentation.Group(_store.OpenNotices(report.PairId));
            foreach (var summary in space)
                activity.Progress.Report(new RunProgress("Pending", null, 0, 0) { Message = summary.Detail });
            return (T)(object)(report with { Diagnostics = instrumentation?.Snapshot(), FreeSpacePending = space });
        }
        finally
        {
            lock (_stopGate)
            {
                _activeSource = null;
                _activeDone = null;
            }
            done.TrySetResult();
            _gate.Release();
        }
    }

    // A saved layout is re-checked before every run, ahead of reconcile (which can finish pending effects): a pair
    // saved before admission existed, or one whose neighbour changed, never touches a root another pair owns.
    private void RefuseLayout(PairDef pair)
    {
        if (PairAdmission.DecideSaved(pair, _store.Pairs()) is { } refusal) throw new SyncRunRefusedException(refusal);
    }

    // Decision 54 addendum, narrowed by decision 71: refuse an apply that would carry out a first-sync choice delete
    // or an unreviewed deletion-evidence delete outside the reviewed prepare's set, expanding collapsed folder
    // deletes over the live store exactly as the executor will. Ordinary deletion propagation (baseline rule 6,
    // reviewed evidence deletes, collision and move deletes) is ongoing sync and proceeds. Null skips the check.
    private void RefuseUnreviewedDeletes(PairDef pair, Plan approved, IReadOnlySet<string>? reviewedDeletes)
    {
        if (reviewedDeletes is null) return;
        var (guarded, propagated) = FolderDelete.SplitVictims(_store, pair, approved, reviewedDeletes);
        if (guarded.Count > 0)
            throw new SyncRunRefusedException("The first sync would delete " + guarded.Count +
                " item(s) not shown in the last prepare — run prepare again. For example: " +
                string.Join(", ", guarded.Take(10)) + ".");
        // report the deletions the prepare did not show that are ordinary sync, so a long first
        // sync visibly keeps working instead of freezing on user activity.
        if (propagated.Count > 0)
            RunLog.Trace(propagated.Count + " deletion(s) after prepare propagated as ordinary sync (examples: " +
                string.Join(", ", propagated.Take(10)) + ")");
    }

    // finding 4: freeze each folder Delete's live descendants at plan time. The Executor expands it
    // only while the live set still matches this signature; otherwise it skips the folder as Superseded and the next
    // run replans, so a new child is never removed by an unplanned expansion.
    private Plan FreezeFolderDeletes(PairDef pair, Plan plan)
    {
        if (!plan.Actions.Any(action => action.Kind == ActionKind.Delete &&
                action.ExpectedTarget?.Kind == ItemKind.Folder)) return plan;
        var refused = ProviderRefusedPaths.Load(_store, pair);
        var actions = new SyncAction[plan.Actions.Count];
        for (var index = 0; index < actions.Length; index++)
        {
            var action = plan.Actions[index];
            if (action.Kind == ActionKind.Delete && action.ExpectedTarget?.Kind == ItemKind.Folder)
                action = action with
                {
                    CarriedIgnoredBudget = action.CarriedIgnoredBudget ??
                        CarriedIgnoredDeletes.Snapshot(_store, pair, action, action.Target == Side.A ? refused.A : refused.B),
                };
            if (FolderDelete.Expands(action))
            {
                var rootId = action.Target == Side.A ? pair.A.RootId : pair.B.RootId;
                action = action with
                {
                    FrozenVictimSignature = FolderDelete.Signature(
                        FolderDelete.SyncedDescendants(_store, pair, rootId, action.Path, action.Target == Side.A ? refused.A : refused.B)),
                };
            }
            actions[index] = action;
        }
        return plan with { Actions = actions };
    }

    /// <summary>One reviewed rebuildable path's outcome: a planned cloud delete that stayed unfinished, or a path the
    /// cloud still holds that no delete reached (blocked).</summary>
    private sealed record RebuildableStatus(string Path, bool RemovalUnfinished, bool Blocked, bool Skipped = false);

    // The status of every reviewed rebuildable path after execution: CloudAbsent (the cloud held nothing, so no delete
    // was planned), a finished delete, an unfinished one, or blocked. The paths come from the reviewed set, never from
    // a fresh detection.
    private IReadOnlyList<RebuildableStatus> RebuildableStatuses(PairDef pair, Side? cloud, Plan plan, Tally tally,
        IReadOnlyList<string> resolved)
    {
        if (resolved.Count == 0 || cloud is not { } side) return Array.Empty<RebuildableStatus>();
        var planned = new Dictionary<string, SyncAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan.Actions)
            if (action.RebuildableRemove && action.ExpectedTarget is { Kind: ItemKind.Folder })
                planned[action.Path] = action;
        var rootId = side == Side.A ? pair.A.RootId : pair.B.RootId;
        var wanted = new HashSet<string>(resolved, StringComparer.OrdinalIgnoreCase);
        var held = new HashSet<string>(_store.LiveUnder(rootId, resolved)
            .Where(item => wanted.Contains(item.Path)).Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var skipped = plan.SkippedActions.Where(action => action.RebuildableRemove)
            .Select(action => action.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<RebuildableStatus>(resolved.Count);
        foreach (var path in resolved)
            statuses.Add(skipped.Contains(path) ? new RebuildableStatus(path, false, false, true)
                : planned.TryGetValue(path, out var action)
                ? new RebuildableStatus(path, !tally.WasDone(action.ActionId), false)
                : new RebuildableStatus(path, false, held.Contains(path)));
        return statuses;
    }

    // The user's rebuildable exclusion survives completion even when its cloud removal needs a later retry.
    // Otherwise ordinary sync could upload the deliberately excluded local tree again.
    private void PersistRebuildables(PairDef pair, IReadOnlyList<RebuildableStatus> statuses)
    {
        var saved = statuses.Select(status => status.Path).ToArray();
        if (saved.Length == 0) return;
        var merged = pair.Excluded.Concat(saved).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (merged.Length != pair.Excluded.Count) _store.SavePair(pair with { Excluded = merged });
    }

    private async Task<(Plan Plan, RebuildableResolution Rebuildables)> PreviewBodyAsync(string pairId,
        IReadOnlyDictionary<string, Side?>? winners, IReadOnlyList<OneSidedChoice>? oneSided,
        IReadOnlyList<RebuildableChoice>? rebuildableTypes, IReadOnlyList<RebuildableFolder>? frozenRebuildables,
        IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare, IProgress<RunProgress> progress,
        CancellationToken ct, IReadOnlyDictionary<string, string>? inventoryCursors = null,
        bool captureMatchedBothSides = true, IReadOnlyList<string>? narrowedIgnoreRules = null)
    {
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
        if (_store.FirstSyncDone(pairId))
            throw new SyncRunRefusedException("First sync is already applied; use run.");
        RefuseLayout(pair);
        await _endpoints.PrepareAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        var reconcile = await ReconcileAsync(pair, throttle, ct, narrowedIgnoreRules);
        var before = LeftAlone(pair).WithPending(reconcile.Protected);
        var inventoryScope = InventoryScope(pair);
        var scan = await ScanBothAsync(pair, throttle, ct, inventoryCursors, firstSync: true);
        // Unknown root coverage still cannot authorize any effects. Named unreadable subtrees
        // are quarantined on both sides; healthy siblings retain their ordinary authority.
        if (scan.A.IncompleteSubtrees.Contains(string.Empty) || scan.B.IncompleteSubtrees.Contains(string.Empty))
            throw new SyncRunRefusedException(IncompleteFirstSync);
        RecordScanRetries(pair, scan);
        var current = LeftAlone(pair);
        var links = before.With(current).WithPending(ScanFailures(scan)).WithPending(PendingDecisions(pairId, null));
        var predicates = Predicates(pair, links);
        // Resolved once, before the deletion probe: its persisted-snapshot selection is what the report and the
        // plan both read, and it also keeps its rebuildable paths out of the deletion-evidence lookup below.
        var rebuildables = ResolveRebuildables(pair, rebuildableTypes, frozenRebuildables);
        var rebuildablePaths = rebuildables.Selected.Select(folder => folder.Path).ToArray();
        var selection = rebuildables.Cloud is { } cloudSide
            ? new RebuildableSelection(cloudSide, rebuildablePaths)
            : null;
        // The chosen folders' cloud rows stay visible under a local link, so their single cloud delete is planned.
        var scanner = new RunScope(_store, predicates.A, predicates.B, links, selection, PairInclude.IgnoreRules(pair));
        // finding 2: paths the reviewed prepare saw on both sides never enter the deletion-history
        // probe or the held lane. They are ordinary planning input (persisted baselines are excluded the same way),
        // so a cache miss can neither hold nor block their post-prepare propagation.
        // Keep the prepared lookup lazy: a database-backed dictionary must not enumerate all matches.
        RunScope.Scope? inventory = scanner.LoadFull(pair);
        var evidence = await PrepareDeferredEvidenceAsync(pair, inventory, rebuildablePaths, matchedAtPrepare,
            scan, ct);
        scan = evidence.Scan;
        // (third-review finding 1): held paths are hidden from the planner's rows, so the planner
        // must know them to refuse an ordinary folder delete that would expand over one.
        var heldPaths = evidence.Held.Count == 0
            ? null
            : evidence.Held.Select(held => held.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        inventory = WithoutHeld(inventory, evidence.Held);
        void refresh(HashRequest request) => inventory = scanner.RefreshHashes(pair, inventory!, request);
        var rows = await HashAndJoinAsync(pair, () => inventory!, scan, true, throttle, ct, refresh);
        throttle.Report(RunPhases.Planning, null, 0, 0);
        var deciding = DecideWithHashedCopiesAsync(pair, scan, rows,
            () => JoinRows(inventory!, scan, true),
            pass => ApplySafetyFilter(pair, ActionPredicates(pair, links, selection), Planner.Decide(pairId,
                Math.Max(scan.A.Gen, scan.B.Gen), pass,
                Options(pair, true, winners, oneSided, selection, matchedAtPrepare, heldPaths))
                with { NarrowedIgnoreRules = narrowedIgnoreRules ?? [] }),
            throttle, ct, refresh);
        rows = [];
        var (decided, planningRows) = await deciding;
        deciding = null!;
        var plan = WithLinksSummary(pair, current, decided);
        // Preview retains inventory, not a committed sync cursor. The CLI stores the safe observation cursors
        // with its reviewed prepare; apply/resume replays those into this inventory and still plans LoadFull.
        _store.CompleteGeneration(scan.A.Gen, scan.A.CursorAfter, false);
        _store.CompleteGeneration(scan.B.Gen, scan.B.CursorAfter, false);
        // the CLI persists what the prepare saw on both sides, and each folder Delete freezes its
        // live descendants so the Executor never expands it over a child the plan did not see. The matched states come
        // from the FINAL planning rows (after any hash pass), never the pre-hash input.
        // Apply consumes the saved matches through FirstSyncOptions (including deferred replans),
        // but does not persist a new prepare, so it needs no second matched-pair collection.
        if (captureMatchedBothSides)
            plan = plan with
            {
                MatchedBothSides = planningRows.Where(row => row.A is not null && row.B is not null)
                    .Select(row => new MatchedPair(row.A!.Path, row.A!, row.B!)).ToArray(),
            };
        planningRows = [];
        inventory = null;
        plan = FreezeFolderDeletes(pair, plan) with
        {
            ScanCursorA = scan.A.CursorAfter, ScanCursorB = scan.B.CursorAfter,
            InventoryScope = inventoryScope,
            InventoryCursors = PreparedInventoryCursors(pair, scan, inventoryCursors),
        };
        throttle.Flush();
        return (plan with { HeldDeletions = evidence.Held, DeletionEvidenceResolved = evidence.Resolved,
            FirstSyncOptions = Options(pair, true, winners, oneSided, selection, matchedAtPrepare, heldPaths) }, rebuildables);
    }

    private async Task<RunReport> ApplyBodyAsync(string pairId, Plan approved, IProgress<RunProgress> progress,
        IReadOnlySet<string>? reviewedDeletes, RebuildableResolution? rebuildables, CancellationToken ct)
    {
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
        if (!string.Equals(approved.PairId, pairId, StringComparison.Ordinal))
            throw new SyncRunRefusedException("The approved plan belongs to another pair.");
        if (!pair.Enabled) throw new SyncRunRefusedException("This pair is off.");
        if (_store.FirstSyncDone(pairId))
            throw new SyncRunRefusedException("First sync is already applied; use run.");
        RefuseLayout(pair);
        // The resolution the reviewed plan was generated from, used exactly as given: the apply revalidates the frozen
        // set once, before planning (in its own preview), never again here, so the plan and the completion status and
        // the exclusions all describe the same set.
        var rebuildablePaths = rebuildables?.Selected.Select(folder => folder.Path).ToArray() ?? Array.Empty<string>();
        // The approval is re-checked against the current exclusions and nested roots: if anything it touches
        // is now protected, its effects are skipped with a notice; the remaining approval can proceed.
        // Only the reviewed resolution passed in can exempt a rebuildable cloud delete from local links.
        var reviewedSelection = rebuildables?.Cloud is { } reviewedCloud
            ? new RebuildableSelection(reviewedCloud, rebuildablePaths)
            : null;
        var actionPredicates = ActionPredicates(pair, LeftAlone(pair).WithPending(
            _store.ReadSyncRetries(pairId).Where(retry => retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 }).Select(retry => retry.Path)), reviewedSelection);
        approved = ApplySafetyFilter(pair, actionPredicates, approved);
        // Astra fix 1: a link or stub created on the local side after the preview is not in the store yet. Every
        // destructive effect is re-checked against the local filesystem (its path and ancestors, listing only) and
        // dropped when it reaches one; the standing notice says so.
        var reviewed = approved.Actions.Count;
        approved = DropUnderNewLocalLinks(pair, approved, actionPredicates);
        var dropped = reviewed - approved.Actions.Count;
        await _endpoints.PrepareAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        var reconcile = await ReconcileAsync(pair, throttle, ct, approved.NarrowedIgnoreRules);
        approved = ApplySafetyFilter(pair,
            ActionPredicates(pair, LeftAlone(pair).WithPending(reconcile.Protected), reviewedSelection), approved);
        // Decision 54 addendum, execution-time half: re-check the plan's delete victims here, under this run's gate,
        // right before executing. A scan that interleaved after apply's own preview (e.g. a concurrent plan command)
        // may have added a child to the store; the Executor's folder expansion reads it live, so a child the reviewed
        // prepare never listed is refused rather than deleted.
        if (reviewedDeletes is not null) approved = DeferUnreviewedDeletes(pair, approved, reviewedDeletes);
        approved = HoldUnreviewedAtApply(pair, approved, reviewedDeletes);
        if (approved.FirstSyncOptions?.MatchedAtPrepare is { } matches)
            approved = DeferPostPreviewChanges(pair, approved, matches);
        foreach (var held in approved.UnreviewedHeldDeletions)
            if (_store.SyncRetryAt(pairId, held.Path)?.Kind != "decision")
                _store.SaveSyncRetry(pairId, new(held.Path, "post-preview", 0, "", DateTimeOffset.MinValue, Held: held));
        var openNotices = _store.OpenNotices(pair.PairId);
        RetainPlanningFailures(pair, approved);
        WriteNotices(approved.Notices);
        approved = ApplySafetyFilter(pair, ActionPredicates(pair, LeftAlone(pair).WithPending(
            _store.ReadSyncRetries(pairId).Where(retry => retry.PendingNotice is not null).Select(retry => retry.Path)), reviewedSelection), approved);
        var execution = await ExecuteWithEvidenceAsync(approved, pair, throttle, reviewedDeletes, ct);
        approved = execution.Plan;
        var tally = execution.Tally;
        if (execution.Pending.Count > 0 && execution.EvidenceUnavailable)
        {
            var alreadyPending = openNotices.Where(notice => notice.ReasonKey == "deletion-evidence-pending")
                .Select(notice => notice.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            WriteNotices(execution.Pending.Where(path => !alreadyPending.Contains(path))
                .Select(path => new Notice(pairId, 4, NoticeKind.Info, path,
                    "Deletion evidence is pending; this item will be retried without blocking other files.", "deletion-evidence-pending")));
        }
        // The approved plan came from a full-scope preview, so every path was re-evaluated for it.
        var pendingEvidence = execution.Pending.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!ct.IsCancellationRequested) CloseResolvedNotices(openNotices, approved, tally, path => !pendingEvidence.Contains(path));
        // Decision 85: unfinished reviewed work is retained as item-owned retry input and visible issues.
        // The reviewed exclusion persists even when its removal is unfinished; exclusion grants no deletion authority.
        var statuses = RebuildableStatuses(pair, rebuildables?.Cloud, approved, tally, rebuildablePaths);
        var blockedRebuildables = statuses.Where(status => status.Blocked).Select(status => status.Path).ToArray();
        // Decision 64: user-decision Holds (ties, needs-decision rows) do not block completion; their paths stay
        // pending (left alone on both sides) while their notices are open, and the user's decision is applied later.
        // Evidence-blocked work is not a user decision. Retain its evidence input before promoting cursors;
        // the later ordinary run must still prove deletion safety rather than interpret missing evidence as absence.
        var evidenceBlocked = approved.Notices.Where(notice => notice.ReasonKey == PlanNotices.DeletionEvidencePending.Key)
            .Select(notice => notice.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unfinished = approved.Actions.Where(action =>
            (action.Kind != ActionKind.Hold || evidenceBlocked.Contains(action.Path)) &&
            !tally.WasSettled(action.ActionId)).ToArray();
        var alreadyDeferred = execution.Pending.Concat(blockedRebuildables).Concat(unfinished.Select(action => action.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Hash failures remove executable rows from the plan, but are not user-decision Holds.
        // Keep first-sync choices available until those content checks can actually finish.
        var unconfirmed = approved.Notices.Where(notice => notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key || notice.ReasonKey == "sync-retry")
            .Select(notice => notice.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var unresolvedIntents = await CompletionIntentPreflightAsync(pair);
        var deferred = execution.Pending.Count + dropped + blockedRebuildables.Length + unfinished.Length +
            unconfirmed.Count(path => !alreadyDeferred.Contains(path)) + unresolvedIntents;
        if (!ct.IsCancellationRequested && !execution.EvidenceUnavailable)
        {
            ParkFirstSyncWork(pair, approved, tally,
                execution.Pending.Concat(_store.UnresolvedIntents(pairId).Select(action => action.Path)),
                unconfirmed, blockedRebuildables);
            // The exclusions are written immediately before the done marker (the store offers no combined
            // transaction, and none is added for this): a crash between the two leaves them saved without the marker,
            // so the apply simply re-runs, and there is never a window in which the first sync is done but its
            // exclusions are missing.
            PromoteFirstSyncInventory(pair, approved);
            PersistRebuildables(pair, statuses);
            _store.MarkFirstSyncDone(pairId);
        }
        throttle.Flush();
        var report = WithReconciliation(BuildReport(pairId, approved, tally, ct.IsCancellationRequested), reconcile);
        return report with
        {
            Outcome = ct.IsCancellationRequested ? RunOutcomes.Stopped : deferred > 0 ? RunOutcomes.Partial : report.Outcome,
            Deferred = _store.FirstSyncDone(pairId) ? 0 : deferred,
            DeletionEvidencePending = execution.Pending,
            ReviewedDeletionEvidencePendingCount = approved.HeldDeletions
                .Select(held => held.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(pendingEvidence.Contains),
            UnreviewedPending = approved.UnreviewedHeldDeletions.Count,
            ContentUnconfirmedCount = unconfirmed.Length,
            ContentUnconfirmedExamples = unconfirmed.Take(50).ToArray(),
            RebuildablesBlocked = blockedRebuildables
        };
    }

    private async Task<RunReport> IncrementalBodyAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct)
    {
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
        if (!pair.Enabled) throw new SyncRunRefusedException("This pair is off.");
        if (!_store.FirstSyncDone(pairId)) throw new SyncRunRefusedException("Review and apply the first sync first.");
        RefuseLayout(pair);
        await _endpoints.PrepareAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        var reviewScopes = _store.DueSyncRetries(pairId, _time.GetUtcNow(), 256)
            .SelectMany(retry => retry.ReviewedActions ?? []).Where(action => action.RebuildableRemove)
            .Select(action => action.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Exclusion remains the user's ordinary-sync policy. Pending reviewed removals still need
        // current inventory for their original stamp/signature check; never validate against hidden stale rows.
        var observationPair = reviewScopes.Count == 0 ? pair : pair with
        { Excluded = pair.Excluded.Where(path => !reviewScopes.Contains(path)).ToArray() };
        var reconcile = await ReconcileAsync(pair, throttle, ct);
        BothScan scan;
        var before = LeftAlone(pair).WithPending(reconcile.Protected);
        try
        {
            scan = await ScanBothAsync(observationPair, throttle, ct);
        }
        catch (Exception error) when (RunFailure(error, ct))
        {
            throttle.Flush();
            RecordUnavailable(pairId, error);
            return FailedRun(pairId, [new ActionResult(string.Empty, Outcome.Failed, null, null, null, error.Message)]);
        }

        RecordScanRetries(pair, scan);
        // Finding 1 (Astra): a link or stub that disappears in this run still protects its path for the whole run.
        var current = LeftAlone(pair);
        var retryCount = _store.CountSyncRetries(pairId);
        var retries = _store.DueSyncRetries(pairId, _time.GetUtcNow(), 256);
        var activeRetries = retries.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retryHeld = retries.Where(item => item.Held is not null).Select(item => item.Held!).ToArray();
        var pendingEvidence = scan.A.PendingDeletionEvidence.Concat(scan.B.PendingDeletionEvidence)
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        var heldPaths = PendingDecisions(pairId, decisions).Concat(pendingEvidence.Keys)
            .Concat(retryHeld.Select(item => item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var links = before.With(current).WithPending(heldPaths).WithPending(ScanFailures(scan))
            .WithRetryProtection((path, descendants) => retryCount > activeRetries.Count &&
                _store.RetryProtected(pairId, path, activeRetries, descendants));
        var predicates = Predicates(pair, links);
        var scanner = new RunScope(_store, predicates.A, predicates.B, links, rules: PairInclude.IgnoreRules(pair), decisions: decisions);
        IReadOnlyDictionary<string, Side?>? winners = decisions?.ToDictionary(entry => entry.Key,
            entry => (Side?)entry.Value, StringComparer.OrdinalIgnoreCase);
        // Execution-time space refusals have no intent. Their durable notices, like unconfirmed content,
        // must bring the paths back into an otherwise empty incremental run until they resolve.
        var retryPaths = _store.OpenNotices(pairId)
            .Where(notice => (notice.Rule == UnconfirmedRule && notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key) ||
                notice.ReasonKey == "name-mapping-collision" ||
                notice.ReasonKey is FreeSpaceRefusal.InsufficientReasonKey or FreeSpaceRefusal.UnknownSpaceReasonKey or
                    FreeSpaceRefusal.UnknownReserveReasonKey)
            .Select(notice => notice.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        retryPaths = retryPaths.Concat(retries.Select(item => item.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var touched = new List<string>(scan.Touched(reconcile.Touched.Concat(retryPaths)
            .Concat(decisions?.Keys ?? Enumerable.Empty<string>())));
        RunScope.Scope mappingContext = new([], [], []);
        RunScope.Scope? inventory = null;
        Func<RunScope.Scope> loadScope = () =>
        {
            if (inventory is not null) return inventory;
            var scope = scan.AnyFull ? scanner.LoadFull(pair) :
                scanner.LoadTouched(pair, touched, scan.A.ChangeObjectIds, scan.B.ChangeObjectIds);
            inventory = scope;
            // Keep only small ancestor context, not a second full inventory, between planning passes.
            mappingContext = new([], [], []) { ContextA = scope.ContextA, ContextB = scope.ContextB };
            return scope;
        };

        if (!scan.AnyFull && !scan.HasChanges && reconcile.Touched.Count == 0 && retryPaths.Length == 0 &&
            decisions is null && pendingEvidence.Count == 0)
        {
            // Path decisions were not re-evaluated; standing summaries still reflect current stored state.
            RaiseLinksSummary(pair, current);
            AddProviderNameRefusalNotice(pair);
            AdvanceCursors(scan);
            throttle.Flush();
            return WithReconciliation(new RunReport(pairId, RunOutcomes.Complete, 0, 0, 0, 0, 0, 0, 0, 0, [])
                { Deferred = retryCount }, reconcile);
        }

        void refresh(HashRequest request) => inventory = scanner.RefreshHashes(pair, loadScope(), request);
        var rows = await HashAndJoinAsync(pair, loadScope, scan, false, throttle, ct, refresh);
        throttle.Report(RunPhases.Planning, null, 0, 0);
        var rowEvaluated = EvaluatedPaths(rows);
        var mappingRetries = _store.OpenNotices(pairId).Where(n => n.ReasonKey == "name-mapping-collision")
            .Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool evaluated(string path) => rowEvaluated(path) || mappingRetries.Contains(path);
        var deciding = DecideWithHashedCopiesAsync(pair, scan, rows,
            () => JoinRows(loadScope(), scan, false),
            pass => ExpandOccupancyOnce(pair, loadScope, scan,
                ApplySafetyFilter(pair, ActionPredicates(pair, links), Decide(pair, scan, pass, winners, mappingContext, links)), touched, links,
                winners, () => inventory = null),
            throttle, ct, refresh);
        rows = [];
        var (decided, _) = await deciding;
        deciding = null!;
        inventory = null;
        var plan = WithLinksSummary(pair, current, decided);
        plan = plan with { Notices = plan.Notices.Concat(pendingEvidence.Select(entry =>
            new Notice(pairId, 19, NoticeKind.Info, entry.Key, entry.Value,
                PlanNotices.DeletionEvidencePending.Key))).ToArray() };
        plan = FreezeFolderDeletes(pair, plan) with
        {
            ScanCursorA = scan.A.CursorAfter, ScanCursorB = scan.B.CursorAfter,
        };
        // Never re-freeze retained folder victims: their original signature still guards new children.
        var rebuildableRetries = retries.SelectMany(item => item.ReviewedActions ?? [])
            .Where(action => action.RebuildableRemove).Select(action => action.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var retryPair = pair with { Excluded = pair.Excluded.Where(path => !rebuildableRetries.Contains(path,
            StringComparer.OrdinalIgnoreCase)).ToArray() };
        var retrySelection = RebuildableSelection.Sides(pair) is { } retrySides
            ? new RebuildableSelection(retrySides.Cloud, rebuildableRetries) : null;
        plan = ApplySafetyFilter(pair, ActionPredicates(retryPair, links, retrySelection),
            await ApplyReviewedRetriesAsync(pair, plan, retries, ct));
        RaiseLinksSummary(pair, current);
        var openNotices = _store.OpenNotices(pair.PairId);
        RetainPlanningFailures(pair, plan);
        WriteNotices(plan.Notices);
        plan = ApplySafetyFilter(pair, ActionPredicates(retryPair, links.WithPending(
            _store.ReadSyncRetries(pairId).Where(retry => retry.PendingNotice is not null).Select(retry => retry.Path)), retrySelection), plan);
        plan = plan with { HeldDeletions = retryHeld, FirstSyncOptions = Options(pair, false, winners, null) };
        var execution = await ExecuteWithEvidenceAsync(plan, pair, throttle,
            retries.Where(item => item.HeldReviewed && item.Held is not null && item.HeldExpected is { } expected &&
                _store.Live(item.Held.Local == Side.A ? pair.A.RootId : pair.B.RootId, [item.Path]).FirstOrDefault() is { } currentHeld &&
                expected.ObjectId == currentHeld.ObjectId && Content.SideStamp(expected) == Content.SideStamp(currentHeld))
                .Select(item => FolderDelete.VictimKey(item.Held!.Local, item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase), ct,
            allowUnreviewedEvidence: true);
        plan = execution.Plan;
        var tally = execution.Tally;
        if (!ct.IsCancellationRequested)
        {
            UpdateRetryWork(pair, plan, tally, retries, execution.Pending, evaluated, scan.A.Complete && scan.B.Complete);
            RetainPlanningFailures(pair, plan);
            CloseResolvedNotices(openNotices, plan, tally, evaluated);
            CloseAppliedDecisions(openNotices, plan, tally, decisions, resolvingNoticeId);
            AcknowledgeVerifiedRehash(pair, scan, plan, tally, evaluated, predicates.A, predicates.B, ct);
            AdvanceCursors(scan);
            _store.Prune();
        }
        throttle.Report(RunPhases.Finishing, null, tally.Done, 0);
        throttle.Flush();
        var report = WithReconciliation(BuildReport(pairId, plan, tally, ct.IsCancellationRequested), reconcile)
            with { Deferred = _store.CountSyncRetries(pairId) };
        return pendingEvidence.Count == 0 ? report : report with
        {
            Outcome = string.Equals(report.Outcome, RunOutcomes.Complete, StringComparison.Ordinal)
                ? RunOutcomes.Partial : report.Outcome,
            DeletionEvidencePending = pendingEvidence.Keys.ToArray()
        };
    }

    // After the first Decide, load any create targets the touched scope missed and decide once more.
    private Plan ExpandOccupancyOnce(PairDef pair, Func<RunScope.Scope> loadScope, BothScan scan, Plan plan,
        List<string> touched, LinkExclusions links, IReadOnlyDictionary<string, Side?>? winners, Action invalidate)
    {
        if (scan.AnyFull) return plan;
        var scope = loadScope();
        var loadedA = new HashSet<string>(scope.A.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var loadedB = new HashSet<string>(scope.B.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();
        foreach (var action in plan.Actions)
        {
            if (action.Kind is not (ActionKind.CopyNew or ActionKind.MkDir or ActionKind.KeepBoth or ActionKind.Move))
                continue;
            var loaded = action.Target == Side.A ? loadedA : loadedB;
            if (loaded.Contains(action.Path)) continue;
            var target = action.Target == Side.A ? pair.A : pair.B;
            if (_store.Live(target.RootId, [action.Path]).Count == 0) continue;
            extra.Add(action.Path);
        }
        if (extra.Count == 0) return plan;
        touched.AddRange(extra);
        invalidate(); // The cached scope belongs to the previous touched-path identity.
        scope = loadScope();
        var rows = JoinRows(scope, scan, false);
        return ApplySafetyFilter(pair, ActionPredicates(pair, links), Decide(pair, scan, rows, winners, scope, links));
    }

    private Plan Decide(PairDef pair, BothScan scan, IReadOnlyList<PlanRow> rows,
        IReadOnlyDictionary<string, Side?>? winners, RunScope.Scope scope, LinkExclusions links) =>
        Planner.Decide(pair.PairId, Math.Max(scan.A.Gen, scan.B.Gen), rows,
            // Hidden held children must prevent collapse before the action guard drops their parent delete.
            // Otherwise independent matched child deletes disappear with that collapsed parent.
            Options(pair, false, winners, null, matchedAtPrepare: new RetryMatches(_store, pair.PairId))
                with { MappingContextA = scope.ContextA, MappingContextB = scope.ContextB,
                    ProtectedDescendants = links.Contains });

    // Decision 64: the paths of open user-decision notices, except the ones being decided in this run.
    private IEnumerable<string> PendingDecisions(string pairId, IReadOnlyDictionary<string, Side>? deciding) =>
        _store.OpenNotices(pairId).Where(notice => (notice.Kind == NoticeKind.UserDecision ||
                                                   notice.ReasonKey == "deletion-history-unavailable") &&
                                                   notice.ReasonKey != "name-mapping-collision" &&
                                                   (deciding is null || !deciding.ContainsKey(notice.Path)))
            .Select(notice => notice.Path).ToArray();

    // Decision 64: a decided notice closes with the chosen side only on evidence (Astra): the winner's effect
    // completed Done, or after the run both sides hold equal content at the path. Anything else — the chosen side
    // gone, the path protected (excluded, a link or unreadable), or the effect failed — keeps the notice open so
    // the user can decide again; ResolveDecisionAsync reports it as "decision could not be applied".
    private void CloseAppliedDecisions(IReadOnlyList<Notice> open, Plan plan, Tally tally,
        IReadOnlyDictionary<string, Side>? decisions, string? resolvingNoticeId)
    {
        if (decisions is null) return;
        var pair = _store.Pair(plan.PairId);
        foreach (var notice in open)
        {
            if (notice.Kind != NoticeKind.UserDecision || notice.NoticeId is null ||
                !decisions.TryGetValue(notice.Path, out var winner)) continue;
            // Astra (9b2b161): close only the exact notice the user decided, never a case-variant notice at the same
            // path, and never while any effect on the decision's subtree — its path or a from-path under it — is
            // unfinished, so one successful descendant cannot close a partially applied folder decision. Then close
            // only on positive evidence: the winner's effect completed, or both sides equal.
            if (resolvingNoticeId is not null &&
                !string.Equals(notice.NoticeId, resolvingNoticeId, StringComparison.Ordinal)) continue;
            var refused = plan.Actions.Any(action => tally.WasRefused(action.ActionId) &&
                PairPath.IsSelfOrUnder(action.Path, notice.Path));
            if (refused)
            {
                if (!notice.Detail.Contains(ProviderNameRefusedException.Detail, StringComparison.Ordinal))
                    WriteNotices([notice with { Detail = notice.Detail + " " + ProviderNameRefusedException.Detail }]);
                continue;
            }
            var unfinished = plan.Actions.Any(action => !tally.WasDone(action.ActionId) &&
                (PairPath.IsSelfOrUnder(action.Path, notice.Path) ||
                 action.FromPath is { } from && PairPath.IsSelfOrUnder(from, notice.Path)));
            var carried = plan.Actions.Any(action => tally.WasDone(action.ActionId) &&
                action.Source is not null && action.Target != winner &&
                PairPath.IsSelfOrUnder(action.Path, notice.Path));
            if (!unfinished && (carried || pair is not null && SidesEqual(pair, notice.Path)))
                _store.CloseNotice(notice.NoticeId, winner == Side.A ? "A" : "B");
        }
    }

    // Both sides hold equal file content at the path: the same kind and the same non-empty content hash.
    private bool SidesEqual(PairDef pair, string path)
    {
        var a = _store.Live(pair.A.RootId, [path]).FirstOrDefault();
        var b = _store.Live(pair.B.RootId, [path]).FirstOrDefault();
        return a is not null && b is not null && a.Kind == b.Kind && a.Hash is not null && b.Hash is not null &&
            string.Equals(a.Hash, b.Hash, StringComparison.Ordinal);
    }

    private PlanOptions Options(PairDef pair, bool firstSync, IReadOnlyDictionary<string, Side?>? winners,
        IReadOnlyList<OneSidedChoice>? oneSided, RebuildableSelection? rebuildables = null,
        IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare = null, IReadOnlySet<string>? heldPaths = null) =>
        new(_settings.TieWindow, firstSync, LabelFor(pair), _settings.MaxLabelAttempts, winners, oneSided, rebuildables,
            matchedAtPrepare, heldPaths)
        { ObservedAt = _time.GetUtcNow(), ProviderA = pair.A.Provider, ProviderB = pair.B.Provider };

    private LabelRule LabelFor(PairDef pair) => new(_settings.KeepBothLabelFormat, _settings.KeepBothTimeFormat,
        TimeZoneInfo.Local.GetUtcOffset(_time.GetUtcNow()),
        SyncProviders.DisplayName(pair.A.Provider), SyncProviders.DisplayName(pair.B.Provider));

    // Decision 59: both sides' predicates also exclude every recorded link, its subtree and (for effects) its folders.
    private (Func<string, bool> A, Func<string, bool> B) Predicates(PairDef pair, LinkExclusions links)
    {
        var (a, b) = BuildPredicates(pair, PairInclude.For);
        return (links.NarrowInventory(a), links.NarrowInventory(b));
    }

    // Every effect must stay out of excluded, nested-pair and product-owned areas; a move or delete must also not
    // CONTAIN one (it would carry or remove the hidden subtree). Creating a folder that holds one is harmless.
    // Rebuildable*: the reviewed rebuildable selection this guard was built with (null: no selection, no exemption).
    private sealed record ActionGuard(Func<string, bool> A, Func<string, bool> B, Func<string, bool> StrictA,
        Func<string, bool> StrictB, Side? RebuildableCloud = null, IReadOnlySet<string>? RebuildableFolders = null,
        Func<string, bool>? RebuildableDelete = null, IReadOnlyList<SyncIgnoreRule>? IgnoreRules = null,
        Func<IEnumerable<string>, ActionGuard>? Prepare = null);

    private static IEnumerable<(SyncAction Action, ActionGuard Guard)> GuardedActions(
        IEnumerable<SyncAction> actions, ActionGuard guard)
    {
        foreach (var batch in actions.Chunk(256))
        {
            var prepared = guard.Prepare?.Invoke(batch.SelectMany(action =>
                new[] { action.Path, action.FromPath, action.Source?.Path }.OfType<string>())) ?? guard;
            foreach (var action in batch) yield return (action, prepared);
        }
    }

    // Decision 59: links narrow both: never at or under a link; a move or delete also never contains one.
    // A chosen rebuildable folder's cloud-only delete is the one exception (local links only), and only when the
    // caller passes the reviewed selection that names that folder on the pair's own cloud side.
    private ActionGuard ActionPredicates(PairDef pair, LinkExclusions links, RebuildableSelection? rebuildables = null)
    {
        var plain = BuildPredicates(pair, PairInclude.For);
        var strict = BuildPredicates(pair, PairInclude.ForAction);
        var guard = new ActionGuard(links.Narrow(plain.A), links.Narrow(plain.B), links.NarrowAction(strict.A),
            links.NarrowAction(strict.B), IgnoreRules: PairInclude.IgnoreRules(pair),
            Prepare: links.RequiresBatch ? paths => ActionPredicates(pair, links.ForPaths(paths), rebuildables) : null);
        if (rebuildables is not { Folders.Count: > 0 } chosen ||
            RebuildableSelection.Sides(pair)?.Cloud is not { } cloud || chosen.Cloud != cloud)
            return guard;
        return guard with
        {
            RebuildableCloud = cloud,
            RebuildableFolders = chosen.Folders.ToHashSet(StringComparer.OrdinalIgnoreCase),
            RebuildableDelete = links.NarrowRebuildableDelete(cloud == Side.A ? strict.A : strict.B),
        };
    }

    // The planner's single cloud-only folder Delete for a folder in the guard's reviewed selection: no source, no
    // from-path, so it never reads or changes the local side. Flags alone never qualify an action.
    private static bool IsApprovedRebuildableDelete(ActionGuard guard, SyncAction action) =>
        action is { Kind: ActionKind.Delete, RebuildableRemove: true, FirstSyncChoice: true,
            ExpectedTarget.Kind: ItemKind.Folder, Source: null, FromPath: null } &&
        guard.RebuildableCloud is { } cloud && action.Target == cloud &&
        guard.RebuildableFolders is { } folders && folders.Contains(action.Path) &&
        guard.RebuildableDelete is not null;

    // Astra (dfab3c5) fixes: each destructive effect is re-checked on the local side right before the apply.
    // * Its path or an ancestor is now a link or a left-alone stub: dropped.
    // * Its path could not be inspected (access denied, I/O): dropped (a failed check is never "nothing there").
    // * A delete on the other side of a path the local side did not hold at preview (one-sided removal) whose local
    //   counterpart now exists: dropped - the row is no longer one-sided, and a collapsed folder delete would
    //   otherwise be expanded from stored descendants. The next run re-plans these paths.
    // An approved rebuildable cloud-only delete (see IsApprovedRebuildableDelete) skips only the "now a local link"
    // drop: it never touches the local side. A new stub or a path that cannot be inspected still drops it.
    private Plan DropUnderNewLocalLinks(PairDef pair, Plan approved, ActionGuard? guard = null)
    {
        var stubs = !_settings.ReplaceOnlineOnlyStubs;
        var locals = new[] { (Root: pair.A, Side: Side.A), (Root: pair.B, Side: Side.B) }
            .Where(local => string.Equals(local.Root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            .Select(local => (local.Root, local.Side, Probe: new LocalLeftAloneProbe(local.Root.RootPath, stubs)))
            .ToArray();
        if (locals.Length == 0) return approved;
        var lockedRemovals = new HashSet<string>(StringComparer.Ordinal);
        bool Drops(SyncAction action)
        {
            if (action.Kind is not (ActionKind.Delete or ActionKind.Move or ActionKind.Update)) return false;
            var rebuildable = guard is not null && IsApprovedRebuildableDelete(guard, action);
            foreach (var local in locals)
            {
                foreach (var path in new[] { action.Path, action.FromPath })
                {
                    if (path is null) continue;
                    var state = local.Probe.Inspect(path);
                    if (state == LocalLeftAloneProbe.State.Unknown)
                    {
                        if (action is { Kind: ActionKind.Delete, FirstSyncChoice: true }) lockedRemovals.Add(action.ActionId);
                        return true;
                    }
                    if (state == LocalLeftAloneProbe.State.Protected) return true;
                    if (state == LocalLeftAloneProbe.State.Linked && !rebuildable) return true;
                    if (state == LocalLeftAloneProbe.State.Present && action is { Kind: ActionKind.Delete, OppositeAbsent: true } &&
                        action.Target != local.Side)
                        return true;
                }
            }
            return false;
        }
        var dropped = approved.Actions.Where(Drops).Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        var lockNotices = new List<Notice>();
        if (dropped.Count == 0) return approved;
        // A lock does not revoke a reviewed removal. Keep its original stamps/signature;
        // other changed-path skips carry only ordinary input, never invented deletion authority.
        foreach (var action in approved.Actions.Where(action => dropped.Contains(action.ActionId)))
        {
            if (_store.SyncRetryAt(pair.PairId, action.Path)?.Kind == "decision") continue;
            if (lockedRemovals.Contains(action.ActionId))
            {
                if (_store.SyncRetryAt(pair.PairId, action.Path)?.ReviewedActions is not { Count: > 0 })
                    _store.SaveSyncRetry(pair.PairId, new(action.Path, "reviewed-removal", 1,
                        "The local path could not be checked; the reviewed removal will retry without copying it back.",
                        DateTimeOffset.MinValue, ReviewedActions: [action]));
                var lockNotice = new Notice(pair.PairId, 40, NoticeKind.Info, action.Path,
                    "The local path could not be checked. The reviewed removal is retained and will retry; nothing was copied or deleted.",
                    "sync-retry");
                lockNotices.Add(lockNotice);
                WriteNotices([lockNotice]);
            }
            else if (_store.SyncRetryAt(pair.PairId, action.Path) is null)
                _store.SaveSyncRetry(pair.PairId, new(action.Path, "post-preview", 0, "", DateTimeOffset.MinValue));
        }
        var notice = new Notice(pair.PairId, LinkExclusions.Rule, NoticeKind.Info, string.Empty,
            $"the local folder changed after the preview (a new link, placeholder or item, or a path that could not be " +
            $"checked); {dropped.Count} planned effects there were not applied and will be planned again",
            ChangedSinceReview.Key);
        return approved with
        {
            Actions = approved.Actions.Where(action => !dropped.Contains(action.ActionId)).ToArray(),
            Notices = approved.Notices.Concat(lockNotices).Append(notice).ToArray()
        };
    }

    /// <summary>Planned effects dropped at apply because the local side changed since the preview.</summary>
    internal static readonly NoticeReason ChangedSinceReview = new("local-changed-since-review",
        "the local folder changed after the preview; affected effects were not applied");

    // Links always, and online-only stubs while their replacement is off (decision 57), are left alone this run.
    private LinkExclusions LeftAlone(PairDef pair) =>
        LinkExclusions.Of(_store, pair, stubsLeftAlone: !_settings.ReplaceOnlineOnlyStubs);

    // Decisions 57/59: the summaries are standing pair-level notices, carried by every plan while their items exist.
    private static Plan WithLinksSummary(PairDef pair, LinkExclusions current, Plan plan) =>
        current.Summaries(pair.PairId) is { Count: > 0 } summaries
            ? plan with { Notices = plan.Notices.Concat(summaries).ToArray() }
            : plan;


    // Keeps the stored links notice current: refreshed when its count or folders change, closed once no link remains.
    private void RaiseLinksSummary(PairDef pair, LinkExclusions links)
    {
        var summaries = links.Summaries(pair.PairId);
        foreach (var open in _store.OpenNotices(pair.PairId))
        {
            if (!LinkExclusions.IsSummary(open) || open.NoticeId is null) continue;
            var now = summaries.FirstOrDefault(summary => summary.ReasonKey == open.ReasonKey);
            if (now is null || !string.Equals(open.Detail, now.Detail, StringComparison.Ordinal))
                _store.CloseNotice(open.NoticeId, now is null ? "resolved" : "refreshed");
        }
        if (summaries.Count > 0) WriteNotices(summaries);
    }

    private (Func<string, bool> A, Func<string, bool> B) BuildPredicates(PairDef pair,
        Func<PairDef, Side, IReadOnlyList<PairDef>, IReadOnlyList<string>,
            Func<string, bool>> make)
    {
        var pairs = _store.Pairs();
        var owned = _productOwned(pair);
        return (make(pair, Side.A, pairs, owned), make(pair, Side.B, pairs, owned));
    }

    private static bool Allowed(ActionGuard guard, SyncAction action)
    {
        var directory = (action.ExpectedTarget ?? action.Source)?.Kind == ItemKind.Folder || action.Kind == ActionKind.MkDir;
        if (guard.IgnoreRules is { Count: > 0 } rules &&
            (SyncIgnorePolicy.IsIgnored(action.Path, directory, rules) ||
             action.FromPath is { } from && SyncIgnorePolicy.IsIgnored(from, directory, rules) ||
             action.Source is { } source && SyncIgnorePolicy.IsIgnored(source.Path, source.Kind == ItemKind.Folder, rules))) return false;
        // A copy can name an allowed destination but still read an excluded source (e.g. a rebased move).
        var sourceInclude = action.Target == Side.A ? guard.B : guard.A;
        if (action.Source is { } sourceState && !sourceInclude(sourceState.Path)) return false;
        // Only a reviewed rebuildable folder's cloud-only removal ignores local links; every other guard still applies.
        if (IsApprovedRebuildableDelete(guard, action)) return guard.RebuildableDelete!(action.Path);
        var strict = action.Kind is ActionKind.Move or ActionKind.Delete;
        var include = (action.Target, strict) switch
        {
            (Side.A, true) => guard.StrictA,
            (Side.A, false) => guard.A,
            (_, true) => guard.StrictB,
            _ => guard.B
        };
        return include(action.Path) && (action.FromPath is null || include(action.FromPath));
    }

    internal Plan FilterNarrowedPrepare(PairDef pair, Plan plan, IReadOnlyList<string> rules) =>
        rules.Count == 0 ? plan : ApplySafetyFilter(pair, null, plan with { NarrowedIgnoreRules = rules });

    private bool CarriesNewlyIgnored(PairDef pair, SyncAction action, IReadOnlyList<SyncIgnoreRule> rules)
    {
        if (rules.Count == 0 || action.Kind is not (ActionKind.Delete or ActionKind.Move) ||
            (action.ExpectedTarget ?? action.Source)?.Kind != ItemKind.Folder) return false;
        var root = action.Target == Side.A ? pair.A : pair.B;
        return _store.AnyLiveIgnoredUnder(root.RootId, action.Path, rules) ||
            action.FromPath is { } from && _store.AnyLiveIgnoredUnder(root.RootId, from, rules);
    }

    private Plan ApplySafetyFilter(PairDef pair, ActionGuard? predicates, Plan plan)
    {
        var kept = new List<SyncAction>();
        var skipped = new List<SyncAction>(plan.SkippedActions);
        var rules = SyncIgnorePolicy.Normalize(plan.NarrowedIgnoreRules.SelectMany(SyncIgnorePolicy.Parse).ToArray());
        var notices = new List<Notice>(plan.Notices);
        var blocked = predicates is null
            ? plan.Actions.Where(action => CarriesNewlyIgnored(pair, action, rules)).ToHashSet()
            : GuardedActions(plan.Actions, predicates).Where(entry =>
                !Allowed(entry.Guard, entry.Action) || CarriesNewlyIgnored(pair, entry.Action, rules))
                .Select(entry => entry.Action).ToHashSet();
        // Collision cleanup can have the occupant's ObjectKey rather than the move's. Follow shared paths
        // as well as keys, transitively, so no destination delete or baseline forget runs without its move.
        // Index only the skipped component so a single skip cannot multiply whole-plan memory.
        if (blocked.Count > 0 || plan.SkippedActions.Count > 0)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in blocked.Concat(plan.SkippedActions)) Index(action);
            // Keep only the blocked connected component, never dictionaries of the whole plan.
            // Repeat a streaming pass for reverse-ordered links; most plans settle in one pass.
            bool changed;
            do
            {
                changed = false;
                foreach (var action in plan.Actions)
                {
                    if (blocked.Contains(action) ||
                        !(action.ObjectKey is { } key && keys.Contains(key) || RowPaths(action).Any(paths.Contains))) continue;
                    blocked.Add(action);
                    Index(action);
                    changed = true;
                }
            } while (changed);
            void Index(SyncAction action)
            {
                foreach (var path in RowPaths(action)) paths.Add(path);
                if (action.ObjectKey is { } key) keys.Add(key);
            }
        }
        foreach (var action in plan.Actions)
        {
            if (!blocked.Contains(action))
            {
                kept.Add(action);
                continue;
            }
            skipped.Add(action);
            if (action.RebuildableRemove && CarriesNewlyIgnored(pair, action, rules))
                notices.Add(new Notice(pair.PairId, 40, NoticeKind.UserDecision, action.Path,
                    "The reviewed cloud removal was skipped because new ignore rules protect files inside. Nothing was removed." +
                    NoticeChoices.RemovalAttentionHelp, "reviewed-removal-skipped"));
            notices.Add(new Notice(pair.PairId, OutsidePairRule, NoticeKind.Info, action.Path,
                PlanNotices.OutsidePairArea.Detail +
                " Entire planned effect, including any folder contents and related collision cleanup, skipped; no changes applied." +
                " Normal subsequent sync may retain or copy included contents. Reviewed rebuildable exclusions are retained at completion." +
                " Leave the protection in place, or include the item and resync.",
                PlanNotices.OutsidePairArea.Key));
        }
        // An active exclusion is one pair-level notice, raised by every plan so it stays open while it applies.
        if (pair.Excluded.Count > 0 && !notices.Any(notice => notice.ReasonKey == PlanNotices.PairExcluded.Key))
            notices.Add(new Notice(pair.PairId, OutsidePairRule, NoticeKind.Info, string.Empty,
                PlanNotices.PairExcluded.Detail + ": " + string.Join(", ", pair.Excluded), PlanNotices.PairExcluded.Key));
        return plan with { Actions = kept, Notices = notices, SkippedActions = skipped };
    }

    private void AdvanceCursors(BothScan scan)
    {
        Advance(scan.A);
        Advance(scan.B);
        void Advance(SideScan side)
        {
            var complete = !side.IncompleteSubtrees.Contains(string.Empty) && side.PendingDeletionEvidence.Keys.All(side.ReleasedDeletionEvidence.Contains);
            _store.CompleteGeneration(side.Gen, side.CursorAfter, complete);
            // Observation coverage advances with effect replay, never merely with ingestion. A crash
            // between these writes can cause extra hydration, but cannot skip unfinished changes.
            if (complete && side.ObservedCoverage is { } coverage && _store is SqliteStateStore sql)
                sql.SaveInventoryObservation(coverage, side.CursorAfter);
        }
    }

    private RunReport FailedRun(string pairId, IReadOnlyList<ActionResult> failures) =>
        new(pairId, RunOutcomes.Failed, 0, failures.Count(result => result.Outcome == Outcome.Failed), 0, 0, 0, 0, 0, 0,
            failures.Take(_settings.FailureSampleLimit).ToArray())
        { Uncertain = failures.Count(result => result.Outcome == Outcome.Uncertain) };

    private static string Unverified(IReadOnlyList<ActionResult> uncertain) =>
        "Unresolved effects could not be verified: " +
        string.Join("; ", uncertain.Select(result => result.ActionId + ": " + result.Reason));

    private static RunReport BuildReport(string pairId, Plan plan, Tally tally, bool cancelled)
    {
        var held = plan.Actions.Count(action => action.Kind == ActionKind.Hold);
        var outcome = cancelled ? RunOutcomes.Stopped
            : tally.Failed + tally.Uncertain + tally.Skipped + held == 0 ? RunOutcomes.Complete
            : tally.Done == 0 && tally.Failed > 0 ? RunOutcomes.Failed
            : RunOutcomes.Partial;
        return new RunReport(pairId, outcome, tally.Done, tally.Failed, held, tally.Uploaded, tally.Downloaded,
            tally.Deleted, tally.Moved, tally.KeptBoth, tally.Failures) { Skipped = tally.Skipped, Uncertain = tally.Uncertain };
    }
}
