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
    private readonly PairRunAdmission _admission = new();
    private readonly object _stopGate = new();
    private readonly Dictionary<CancellationTokenSource, (TaskCompletionSource Done, bool Preserve)> _active = [];
    private readonly AsyncLocal<RestoreSnapshot?> _restoreSnapshot = new();
    private sealed class RestoreSnapshot
    {
        internal readonly object Gate = new();
        internal RecoveryRestoreProtection? Protection;
        internal string? PairId;
    }

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
        if (settings.ReconciliationBatch.MaxPaths <= 0 || settings.ReconciliationBatch.MaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings.ReconciliationBatch));
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
                inventoryCursors), ct, admissionPairId: pairId);

    /// <summary>
    /// The apply's preview: its rebuildable folders come from the reviewed prepare, never from detection, so an apply
    /// can only use a subset of the paths the user saw — it never adds one. This is the apply's single revalidation,
    /// and it hands back the very resolution the plan is generated from, so the executed plan and the completion
    /// status (and the exclusions) can never describe different sets.
    /// </summary>
    internal Task<ApplyPreview> PreviewFirstSyncApplyResolvedAsync(string pairId,
        IReadOnlyDictionary<string, Side?>? winners, IReadOnlyList<OneSidedChoice>? oneSided,
        IReadOnlyList<RebuildableFolder>? rebuildables, IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare,
        IProgress<RunProgress> progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? inventoryCursors = null,
        IReadOnlyList<string>? narrowedIgnoreRules = null) =>
        RunExclusiveAsync(progress, async (sink, token) =>
        {
            var preview = await PreviewCoreAsync(pairId, winners, oneSided, null, rebuildables, matchedAtPrepare, sink,
                token, inventoryCursors, captureMatchedBothSides: false, narrowedIgnoreRules: narrowedIgnoreRules);
            var continuation = preview.Plan.RunUnavailable is null && _store.Pair(pairId) is { } pair
                ? _endpoints.CaptureContinuation(pair) : null;
            return new ApplyPreview(preview.Plan, preview.Rebuildables, continuation);
        }, ct, admissionPairId: pairId);

    private Task<Plan> PlanOfAsync(string pairId, IReadOnlyDictionary<string, Side?>? winners,
        IReadOnlyList<OneSidedChoice>? oneSided, IReadOnlyList<RebuildableChoice>? rebuildableTypes,
        IReadOnlyList<RebuildableFolder>? frozenRebuildables, IProgress<RunProgress> progress, CancellationToken ct) =>
        RunExclusiveAsync(progress, async (sink, token) =>
            (await PreviewCoreAsync(pairId, winners, oneSided, rebuildableTypes, frozenRebuildables, null, sink, token))
                .Plan, ct, admissionPairId: pairId);

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
        CancellationToken ct, IReadOnlySet<string>? reviewedDeletes, RebuildableResolution? rebuildables,
        EndpointContinuation? continuation = null) =>
        RunExclusiveAsync(progress,
            (sink, token) => ApplyCoreAsync(pairId, approved, sink, reviewedDeletes, rebuildables, token, continuation), ct, pairId);

    /// <inheritdoc />
    public Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        IncrementalAsync(pairId, progress, ct, new RunOptions());

    /// <summary>Runs with an optional streaming effects ledger and a bound on newly planned work.</summary>
    public Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct,
        RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxPlannedActions < 0) throw new ArgumentOutOfRangeException(nameof(options));
        return RunExclusiveAsync(progress, async (sink, token) =>
        {
            using var ledger = options.EffectsExportPath is null ? null : new EffectsLedger(options.EffectsExportPath, simulated: false);
            var store = _store as SqliteStateStore;
            if (ledger is not null && store is null)
                ledger.Unavailable("This state store does not expose durable committed-result observation.");
            void Observe(PairDef pair, IReadOnlyList<CommittedAction> rows)
            {
                if (pair.PairId != pairId) return;
                try { ledger?.Committed(pair, rows); }
                catch (Exception error)
                {
                    RunLog.Trace("Effects ledger observer failed: " + error);
                    ledger?.Unavailable(error.Message);
                }
            }
            if (ledger is not null && store is not null) store.ResultsCommitted += Observe;
            try
            {
                var report = await IncrementalCoreAsync(pairId, null, null, sink, token, options);
                if (ledger is not null)
                {
                    if (!token.IsCancellationRequested) ledger.Complete();
                    ledger.Dispose();
                    if (ledger.Failure is not null)
                    {
                        WriteNotices([ledger.IncompleteNotice(pairId)]);
                        report = report with { CompletionFailed = true };
                    }
                    else if (!token.IsCancellationRequested)
                        _store.CloseNoticeFamily(pairId, EffectsLedger.IncompleteReason);
                }
                return report;
            }
            finally
            {
                if (ledger is not null && store is not null) store.ResultsCommitted -= Observe;
            }
        }, ct, pairId);
    }

    internal Task<T> WithPairAdmissionAsync<T>(string? pairId, Func<Task<T>> body, CancellationToken ct,
        bool immediate = false) => _admission.WithAsync(pairId, body, ct, immediate);

    internal Task<T> WithPairAdmissionAsync<T>(string? pairId, Func<Task<T>> body, CancellationToken ct,
        Action<string> waitProgress, bool immediate = false) =>
        _admission.WithAsync(pairId, body, ct, immediate, waitProgress);

    /// <inheritdoc />
    public async Task StopAsync()
    {
        KeyValuePair<CancellationTokenSource, (TaskCompletionSource Done, bool Preserve)>[] active;
        Task[] reconciliations;
        lock (_stopGate)
        {
            active = _active.ToArray();
            reconciliations = _reconciling.Values.Select(scope => scope.Drained.Task).ToArray();
        }
        // Signal every worker before waiting for any one pair to drain.
        var signals = new List<Task>();
        foreach (var entry in active.Where(entry => !entry.Value.Preserve))
            try { signals.Add(entry.Key.CancelAsync()); } catch (ObjectDisposedException) { }
        await Task.WhenAll(signals.Concat(active.Select(entry => entry.Value.Done.Task)).Concat(reconciliations)).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the named roots' stored cursors so each root's next scan is a full scan. It takes the same exclusive
    /// gate as a run, so a cursor is never reset underneath a run in flight: that run's CompleteGeneration would
    /// otherwise write its old cursor back and lose the reset.
    /// </summary>
    public Task RequestRescanAsync(IReadOnlyList<string> rootIds, CancellationToken ct) =>
        RequestRescanAsync(null, rootIds, ct);

    public Task RequestRescanAsync(string? pairId, IReadOnlyList<string> rootIds, CancellationToken ct) =>
        RunExclusiveAsync(null, (_, _) =>
        {
            if (pairId is not null)
            {
                var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is unavailable.");
                if (rootIds.Any(rootId => rootId != pair.A.RootId && rootId != pair.B.RootId))
                    throw new SyncRunRefusedException("The rescan roots do not belong to the selected pair.");
            }
            foreach (var rootId in rootIds)
            {
                _store.ForgetCursor(rootId);
                if (_store is SqliteStateStore sql) sql.ForgetInventoryObservation(rootId);
            }
            return Task.FromResult(true);
        }, ct, admissionPairId: pairId);

    // D75/D77: exclusion belongs to the affected pair, not the engine. The null scope is
    // reserved for genuinely all-pair maintenance whose existing call shape has no pair identity.
    private async Task<T> RunExclusiveAsync<T>(IProgress<RunProgress>? progress,
        Func<IProgress<RunProgress>, CancellationToken, Task<T>> body, CancellationToken ct, string? historyPairId = null,
        string? admissionPairId = null, bool preserveAcceptedChoice = false, bool admitBody = true)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stopGate) _active.Add(linked, (done, preserveAcceptedChoice));
        _restoreSnapshot.Value = new RestoreSnapshot();
        try { return await RunAdmittedAsync(progress, body, linked.Token, historyPairId, admissionPairId ?? historyPairId, admitBody).ConfigureAwait(false); }
        finally
        {
            _restoreSnapshot.Value = null;
            lock (_stopGate) _active.Remove(linked);
            done.TrySetResult();
        }
    }

    private async Task<T> RunAdmittedAsync<T>(IProgress<RunProgress>? progress,
        Func<IProgress<RunProgress>, CancellationToken, Task<T>> body, CancellationToken ct, string? historyPairId,
        string? admissionPairId, bool admitBody)
    {
        // The pump and the in-flight table open before the gate, so a run queued behind another is still visible
        // (the gate wait is itself a tracked operation) rather than silently blocked.
        var queuedAt = _time.GetUtcNow();
        using var activity = RunActivity.Begin(progress, _settings, _time);
        var gateWait = RunOperations.Begin(RunOperationKind.Gate, "run gate");
        IDisposable? lease;
        try { lease = admitBody ? await _admission.EnterAsync(admissionPairId, ct).ConfigureAwait(false) : null; }
        finally { gateWait.Dispose(); }
        using var admission = lease;
        ct.ThrowIfCancellationRequested();
        using var instrumentation = RunInstrumentation.Begin(_settings, _time);
        var startedAt = _time.GetUtcNow();
        var queued = startedAt - queuedAt;
        if (queued >= TimeSpan.FromSeconds(1))
            activity.Progress.Report(new RunProgress("Queued", null, 0, 0) { Message = $"Run queued for {queued.TotalSeconds:N1}s" });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return await ExecuteAdmittedAsync(body, activity.Progress, linked, instrumentation, startedAt, historyPairId);
    }

    private async Task<T> ExecuteAdmittedAsync<T>(Func<IProgress<RunProgress>, CancellationToken, Task<T>> body,
        IProgress<RunProgress> progress, CancellationTokenSource linked, RunInstrumentation? instrumentation,
        DateTimeOffset startedAt, string? historyPairId)
    {
        try
        {
            // All worker effects inherit one verification flight and the store cancellation scope.
            await using var accountVerification = SyncProviderAccountVerificationScope.Begin(linked.Token);
            T result;
            using (FairStoreGate.WithCancellation(linked.Token))
                result = await body(progress, linked.Token).ConfigureAwait(false);
            return result is RunReport report
                ? (T)(object)CompleteAdmittedReport(report, progress, linked.Token, instrumentation, startedAt) : result;
        }
        catch (PairPausedException paused) when (typeof(T) == typeof(RunReport))
        {
            var report = (paused.Report ?? new RunReport(paused.PairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, []))
                with { Paused = true, CompletedUtc = _time.GetUtcNow(), Diagnostics = instrumentation?.Snapshot() };
            report = WithIndexingStatus(FinalizeOutcome(report, linked.Token));
            RecordSummarySafely(report, startedAt);
            return (T)(object)report;
        }
        catch (Exception error) when (typeof(T) == typeof(RunReport) && historyPairId is not null &&
            StorageFailureClassifier.ScopeOf(error, linked.Token) == FailureRetryOwner.FailureScope.State)
        {
            return (T)(object)StateFailureReport(error, historyPairId, linked.Token, instrumentation);
        }
        catch (Exception error)
        {
            RunLog.Trace("Run failed: " + error.ToString());
            if (historyPairId is not null)
                RecordSummarySafely(FinalizeOutcome(new RunReport(historyPairId, string.Empty,
                    0, error is OperationCanceledException && linked.IsCancellationRequested ? 0 : 1,
                    0, 0, 0, 0, 0, 0, []) { AttemptFailed = true, HaltedBy = error }, linked.Token), startedAt);
            throw;
        }
    }

    private RunReport CompleteAdmittedReport(RunReport report, IProgress<RunProgress> progress,
        CancellationToken ct, RunInstrumentation? instrumentation, DateTimeOffset startedAt)
    {
        if (report.HaltedBy is { } fatal && StorageFailureClassifier.ScopeOf(fatal, ct) == FailureRetryOwner.FailureScope.State)
            return FinalizeOutcome(report with { CompletedUtc = _time.GetUtcNow(), Diagnostics = instrumentation?.Snapshot() }, ct);
        var completion = new CompletionSteps(this, report.PairId, ct);
        var completed = report;
        completion.Step(() =>
        {
            var space = FreeSpacePresentation.Group(_store.UnresolvedNotices(report.PairId));
            foreach (var summary in space)
                progress.Report(new RunProgress("Pending", null, 0, 0) { Message = summary.Detail });
            completed = report with { CompletedUtc = _time.GetUtcNow(), Diagnostics = instrumentation?.Snapshot(), FreeSpacePending = space };
        });
        completed = WithIndexingStatus(FinalizeOutcome(completion.Finish(completed), ct));
        // Retain only bounded counts; failure examples stay on the live report.
        RecordSummarySafely(completed, startedAt);
        return completed;
    }

    private RunReport StateFailureReport(Exception error, string pairId, CancellationToken ct, RunInstrumentation? instrumentation)
    {
        // Preserve counted effects without completion or history writes after storage loses authority.
        var counted = error.Data["SyncRun.ExecutionReport"] as RunReport ??
            new RunReport(pairId, RunOutcomes.Failed, 0, 1, 0, 0, 0, 0, 0, 0, []);
        return FinalizeOutcome(counted with { HaltedBy = error, CompletionFailed = true,
            CompletedUtc = _time.GetUtcNow(), Diagnostics = instrumentation?.Snapshot() }, ct);
    }

    private void RecordSummarySafely(RunReport report, DateTimeOffset startedAt)
    {
        var summary = $"Run outcome={report.Outcome}; pending={report.Pending}; deferred={report.Deferred}; done={report.Done}; failed={report.Failed}; uncertain={report.Uncertain}";
        if (RunLog.Current is { } log) log.Summary(summary);
        else RunLog.Trace(summary);
        if (report.HaltedBy is { } fatal && StorageFailureClassifier.ScopeOf(fatal, CancellationToken.None) == FailureRetryOwner.FailureScope.State)
            return;
        try
        {
            if (report.HaltedBy is null && (report.ProviderReached || report.Outcome is RunOutcomes.Complete or RunOutcomes.Partial))
                _store.CloseNoticeFamily(report.PairId, "autosync-failed");
        }
        catch (Exception error) { RunOutput.Observe(error); }
        // Empty successful safety polls do not evict meaningful history.
        if (_reconciliationRunId.Value is null && report.Outcome == RunOutcomes.Complete && report.Done == 0 && report.FailureCount == 0 &&
            report.Held == 0 && report.Deferred == 0 && report.Skipped == 0)
            return;
        try
        {
            _store.RecordRunSummary(RunSummary.Of(_reconciliationRunId.Value ?? Guid.NewGuid().ToString("N"), report, startedAt, _time.GetUtcNow()));
            _store.CloseNoticeFamily(report.PairId, "run-history-unavailable");
        }
        catch (Exception error)
        {
            RunOutput.Observe(error);
            try
            {
                _store.AddNotices([new Notice(report.PairId, 0, NoticeKind.Info, string.Empty,
                    "Run effects retain their original outcome, but history could not be saved: " + error.Message +
                    ". Check disk space and store diagnostics. Retry on the next run, or Ignore this notice.", "run-history-unavailable")]);
            }
            catch (Exception noticeError) { RunOutput.Observe(noticeError); }
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
        UpdatePairSettings(() =>
        {
            var current = _store.Pair(pair.PairId);
            if (current is null) return;
            var merged = current.Excluded.Concat(saved).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (merged.Length != current.Excluded.Count) _store.SavePair(current with { Excluded = merged });
        });
    }

    private async Task<(Plan Plan, RebuildableResolution Rebuildables)> PreviewBodyAsync(string pairId,
        IReadOnlyDictionary<string, Side?>? winners, IReadOnlyList<OneSidedChoice>? oneSided,
        IReadOnlyList<RebuildableChoice>? rebuildableTypes, IReadOnlyList<RebuildableFolder>? frozenRebuildables,
        IReadOnlyDictionary<string, PrepareMatch>? matchedAtPrepare, IProgress<RunProgress> progress,
        CancellationToken ct, IReadOnlyDictionary<string, string>? inventoryCursors = null,
        bool captureMatchedBothSides = true, IReadOnlyList<string>? narrowedIgnoreRules = null)
    {
        using var pause = PairPause.Begin(_store, pairId);
        PairPause.Check();
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
        if (_store.FirstSyncDone(pairId))
            throw new SyncRunRefusedException("First sync is already applied; use run.");
        RefuseLayout(pair);
        await _endpoints.PrepareAsync(pair, ct);
        await RefreshDropboxQuotaAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        var reconcile = await ReconcileAsync(pair, throttle, ct, narrowedIgnoreRules);
        var before = LeftAlone(pair, includeRestore: false).WithPending(reconcile.Protected);
        var inventoryScope = InventoryScope(pair);
        var scan = await ScanBothAsync(pair, throttle, ct, inventoryCursors, firstSync: true);
        // Unknown root coverage still cannot authorize any effects. Named unreadable subtrees
        // are quarantined on both sides; healthy siblings retain their ordinary authority.
        RecordScanRetries(pair, scan);
        // Preview does not retry restores, forget their baselines, or change their durable state.
        // Existing frozen intents remain protected until an actual sync verifies them.
        if (scan.A.IncompleteSubtrees.Contains(string.Empty) || scan.B.IncompleteSubtrees.Contains(string.Empty))
        {
            // End the preview without effects or a lost-run exception. The existing unavailable-plan
            // marker prevents apply; root retry debt and provider page checkpoints survive for resume.
            _store.CompleteGeneration(scan.A.Gen, scan.A.CursorAfter, false);
            _store.CompleteGeneration(scan.B.Gen, scan.B.CursorAfter, false);
            throttle.Flush();
            return (new Plan(pairId, Math.Max(scan.A.Gen, scan.B.Gen), [], _store.UnresolvedNotices(pairId))
                { RunUnavailable = IncompleteFirstSync }, RebuildableResolution.None);
        }
        CloseObservedRepairs(pair, scan);
        var current = LeftAlone(pair);
        var restored = _store.DueSyncRetries(pairId, _time.GetUtcNow(), 256, CachedIdentityRefresh.PendingReason)
            .Where(retry => retry.ReprobeEvidence && retry.Held is not null &&
                !pair.Excluded.Any(path => PairPath.IsSelfOrUnder(retry.Path, path))).ToArray();
        // Admit only due, prepare-matched evidence retries to ordinary planning. Their durable
        // rows remain intact; the planner and fresh executor admission still own deletion authority.
        var activeFirstSyncRetries = ActiveFirstSyncRetries(pair).Concat(restored
            .Where(retry => !UsesDeletionEvidenceLane(retry.Path, matchedAtPrepare))
            .Select(retry => retry.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restoreProtection = RecoveryProtection(pair);
        var links = Protection(pair, scan, before, activeRetries: activeFirstSyncRetries);
        var predicates = Predicates(pair, links);
        // Resolved once, before the deletion probe: its persisted-snapshot selection is what the report and the
        // plan both read, and it also keeps its rebuildable paths out of the deletion-evidence lookup below.
        var rebuildables = ResolveRebuildables(pair, rebuildableTypes, frozenRebuildables);
        var rebuildablePaths = rebuildables.Selected.Select(folder => folder.Path).ToArray();
        var selection = rebuildables.Cloud is { } cloudSide
            ? new RebuildableSelection(cloudSide, rebuildablePaths)
            : null;
        // The chosen folders' cloud rows stay visible under a local link, so their single cloud delete is planned.
        var scanner = new RunScope(_store, predicates.A, predicates.B, links, selection, PairInclude.IgnoreRules(pair),
            activeRetries: activeFirstSyncRetries,
            restoreProtection: restoreProtection);
        // finding 2: paths the reviewed prepare saw on both sides never enter the deletion-history
        // probe or the held lane. They are ordinary planning input (persisted baselines are excluded the same way),
        // so a cache miss can neither hold nor block their post-prepare propagation.
        // Keep the prepared lookup lazy: a database-backed dictionary must not enumerate all matches.
        RunScope.Scope? inventory = scanner.LoadFull(pair);
        var repairPaths = InterruptedDeleteRepair.SourcePaths(inventory, _store, pairId);
        matchedAtPrepare = InterruptedDeleteRepair.WithoutMatches(matchedAtPrepare, repairPaths);
        var evidence = await PrepareDeferredEvidenceAsync(pair, inventory, rebuildablePaths, matchedAtPrepare,
            scan, ct, repairPaths);
        evidence = evidence with { Held = evidence.Held.Concat(restored
            .Where(retry => UsesDeletionEvidenceLane(retry.Path, matchedAtPrepare, repairPaths))
            .Select(retry => retry.Held!))
            .DistinctBy(held => held.Path, StringComparer.OrdinalIgnoreCase).ToArray() };
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
                Math.Max(scan.A.Gen, scan.B.Gen), InterruptedDeleteRepair.Protect(pass, _store, pairId),
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
        // RQ-030/140: preview hashes are observations, not effects. Retain their journal
        // position separately so another preview replays writes instead of mistaking the
        // deliberately absent effect cursor for lost history. Planning still loads all rows.
        if (_store is SqliteStateStore observationStore)
            foreach (var (root, side, observed) in new[] { (pair.A, Side.A, scan.A), (pair.B, Side.B, scan.B) })
                if (_endpoints.Inventory(root) is LocalInventory && observed.Complete &&
                    observed.PendingDeletionEvidence.Count == 0 &&
                    NtfsUsnCheckpoint.TryParseCursor(observed.CursorAfter, out _, out _))
                    observationStore.SaveInventoryObservation(Coverage(pair, side), observed.CursorAfter);
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
        IReadOnlySet<string>? reviewedDeletes, RebuildableResolution? rebuildables, CancellationToken ct,
        EndpointContinuation? continuation)
    {
        using var pause = PairPause.Begin(_store, pairId);
        PairPause.Check();
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
        var activeFirstSyncRetries = ActiveFirstSyncRetries(pair);
        var applyLinks = Protection(pair, activeRetries: activeFirstSyncRetries);
        approved = GuardReviewedRecoveryRestores(pair, approved);
        var actionPredicates = ActionPredicates(pair, applyLinks, reviewedSelection);
        approved = ApplySafetyFilter(pair, actionPredicates, approved);
        // Astra fix 1: a link or stub created on the local side after the preview is not in the store yet. Every
        // destructive effect is re-checked against the local filesystem (its path and ancestors, listing only) and
        // dropped when it reaches one; the standing notice says so.
        var reviewed = approved.Actions.Count;
        approved = DropUnderNewLocalLinks(pair, approved, actionPredicates, out var supersededDeletes);
        var dropped = reviewed - approved.Actions.Count - supersededDeletes;
        await _endpoints.PrepareAsync(pair, continuation, ct);
        await RefreshDropboxQuotaAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        var reconcile = await ReconcileAsync(pair, throttle, ct, approved.NarrowedIgnoreRules);
        approved = ApplySafetyFilter(pair,
            ActionPredicates(pair, Protection(pair, additional: reconcile.Protected), reviewedSelection), approved);
        // Classification can change the held count or retire link/stub intents. Replace every summary the reviewed
        // plan carries with the post-classification snapshot: the plan's copy was computed before this apply's
        // reconciliation, and re-raising it later would refresh the stored counts back to the stale preview values.
        var summaryLinks = LeftAlone(pair);
        approved = approved with
        {
            Notices = approved.Notices.Where(notice => !LinkExclusions.IsSummary(notice))
                .Concat(summaryLinks.Summaries(pair.PairId)).ToArray()
        };
        RaiseLinksSummary(pair, summaryLinks);
        // Decision 54 addendum, execution-time half: re-check the plan's delete victims here, under this run's gate,
        // right before executing. A scan that interleaved after apply's own preview (e.g. a concurrent plan command)
        // may have added a child to the store; the Executor's folder expansion reads it live, so a child the reviewed
        // prepare never listed is refused rather than deleted.
        if (reviewedDeletes is not null) approved = DeferUnreviewedDeletes(pair, approved, reviewedDeletes);
        approved = HoldUnreviewedAtApply(pair, approved, reviewedDeletes);
        if (approved.FirstSyncOptions?.MatchedAtPrepare is { } matches)
            approved = DeferPostPreviewChanges(pair, approved, matches);
        foreach (var held in approved.UnreviewedHeldDeletions)
            if (_store.SyncRetryAt(pairId, held.Path) is not { ReprobeEvidence: true } &&
                _store.SyncRetryAt(pairId, held.Path)?.Kind != "decision")
                _store.SaveSyncRetry(pairId, new(held.Path, "post-preview", 0, "", DateTimeOffset.MinValue, Held: held));
        var openNotices = _store.UnresolvedNotices(pair.PairId);
        RetainPlanningFailures(pair, approved);
        WriteNotices(approved.Notices);
        approved = ApplySafetyFilter(pair, ActionPredicates(pair, Protection(pair), reviewedSelection), approved);
        var execution = await ExecuteWithEvidenceAsync(approved, pair, throttle, reviewedDeletes, ct);
        approved = execution.Plan;
        var tally = execution.Tally;
        var completion = new CompletionSteps(this, pairId, ct);
        RunReport? completedReport = null;
        await completion.StepAsync(async () =>
        {
            if (execution.Pending.Count > 0 && execution.EvidenceUnavailable)
            {
                var alreadyPending = openNotices.Where(notice => notice.ReasonKey == "deletion-evidence-pending")
                    .Select(notice => notice.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                WriteNotices(execution.Pending.Where(path => !alreadyPending.Contains(path))
                    .Select(path => new Notice(pairId, 4, NoticeKind.Info, path,
                        "Deletion evidence is pending; this item will be retried without blocking other files.", "deletion-evidence-pending")));
            }
            completion.Step(() => WriteRestoredEvidenceNotices(pairId, execution.Pending));
            // The approved plan came from a full-scope preview, so every path was re-evaluated for it.
            var pendingEvidence = execution.Pending.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!ct.IsCancellationRequested)
                completion.Step(() => CloseResolvedNotices(openNotices, approved, tally, path => !pendingEvidence.Contains(path)));
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
            if (!ct.IsCancellationRequested)
                ParkFirstSyncWorkWithEvidence(pair, approved, tally,
                    execution.Pending.Concat(_store.UnresolvedIntents(pairId).Select(action => action.Path))
                        .Concat(execution.EvidenceUnavailable ? approved.HeldDeletions.Select(item => item.Path) : []),
                    unconfirmed, blockedRebuildables, execution.EvidenceUnavailable);
            if (!ct.IsCancellationRequested && !execution.EvidenceUnavailable)
            {
                // The exclusions are written immediately before the done marker (the store offers no combined
                // transaction, and none is added for this): a crash between the two leaves them saved without the marker,
                // so the apply simply re-runs, and there is never a window in which the first sync is done but its
                // exclusions are missing.
                PromoteFirstSyncInventory(pair, approved);
                PersistRebuildables(pair, statuses);
                _store.MarkFirstSyncDone(pairId);
            }
            // Reporting includes sleeping content debt without re-parking it or changing its deadline.
            var contentSummary = SummarizeContentUnconfirmed(unconfirmed, _store.ReadSyncRetries(pairId));
            var report = WithReconciliation(BuildReport(pairId, approved, tally, ct.IsCancellationRequested), reconcile);
            completedReport = report with
            {
                Deferred = _store.FirstSyncDone(pairId) ? execution.Pending.Count + dropped + unfinished.Length : deferred,
                DeletionEvidencePending = execution.Pending,
                ReviewedDeletionEvidencePendingCount = approved.HeldDeletions
                    .Select(held => held.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(pendingEvidence.Contains),
                UnreviewedPending = approved.UnreviewedHeldDeletions.Count,
                ContentUnconfirmedCount = contentSummary.Count,
                ContentUnconfirmedExamples = contentSummary.Examples,
                RebuildablesBlocked = blockedRebuildables
            };
        });
        completion.Step(() => throttle.Flush());
        return completion.Finish(completedReport ??
            WithReconciliation(BuildReport(pairId, approved, tally, ct.IsCancellationRequested), reconcile));
    }

    internal sealed record IncrementalPlanPreview(Plan Plan, IReadOnlyList<string> Unapproved);

    private sealed record IncrementalPreparation(Plan? Plan, RunReport? Completed, Func<Task<RunReport>>? Execute, Func<IncrementalPlanPreview>? Initial = null);

    private Task<RunReport> IncrementalBodyAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct) =>
        IncrementalBodyAsync(pairId, decisions, resolvingNoticeId, progress, ct, null);

    private async Task<RunReport> IncrementalBodyAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct, RunOptions? options)
    {
        using var pause = PairPause.Begin(_store, pairId);
        using var gapCoverage = options?.Batch is not null && _store is SqliteStateStore sql
            ? sql.BeginReconciliationGapCoverage(pairId) : null;
        if (options?.Batch is { } batch) batch.GapCoverage = gapCoverage;
        var prepared = await PrepareIncrementalAsync(pairId, decisions, resolvingNoticeId, progress, ct, options);
        if (prepared.Completed is { Failed: 0 } && prepared.Plan is not null) ClosePlanBoundNotice(pairId);
        return prepared.Completed ?? await prepared.Execute!();
    }

    private void ClosePlanBoundNotice(string pairId)
    {
        foreach (var notice in _store.OpenNotices(pairId).Where(notice => notice.ReasonKey == PlanNotices.PlanBoundExceededKey))
            _store.CloseNotice(notice.NoticeId!, "resolved");
    }

    internal SyncRun Rehearsal(RehearsalEndpoints endpoints) =>
        new(_store, _executor, endpoints, _settings, _time, _productOwned);

    internal IEndpointProvider RehearsalSource => _endpoints;

    /// <summary>Computes the ordinary run's initial execution plan without applying it. The caller must use an
    /// isolated store: observations, reconciliation and retry bookkeeping are persisted there.</summary>
    internal Task<IncrementalPlanPreview> PreviewIncrementalAsync(string pairId, IProgress<RunProgress> progress, CancellationToken ct) =>
        RunExclusiveAsync(progress, async (sink, token) =>
        {
            using var pause = PairPause.Begin(_store, pairId);
            var endpoints = new PlanOnlyEndpoints(_endpoints);
            var reader = new SyncRun(_store, _executor, endpoints, _settings, _time, _productOwned);
            var prepared = await reader.PrepareIncrementalAsync(pairId, null, null, sink, token);
            var preview = prepared.Initial?.Invoke() ?? new IncrementalPlanPreview(prepared.Plan ?? new Plan(pairId, 0, [], [])
                { RunUnavailable = prepared.Completed?.Failures.FirstOrDefault()?.Reason ?? "Inventory could not be read." }, []);
            if (endpoints.MutationRequested)
                throw new SyncRunRefusedException("Read-only reconciliation needed a provider effect; no effect was performed and this preview is incomplete.");
            return preview;
        }, ct, admissionPairId: pairId);

    private Task<IncrementalPreparation> PrepareIncrementalAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct) =>
        PrepareIncrementalAsync(pairId, decisions, resolvingNoticeId, progress, ct, null);

    private async Task<IncrementalPreparation> PrepareIncrementalAsync(string pairId, IReadOnlyDictionary<string, Side>? decisions,
        string? resolvingNoticeId, IProgress<RunProgress> progress, CancellationToken ct, RunOptions? options)
    {
        PairPause.Check();
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("No pair " + pairId);
        if (!pair.Enabled) throw new SyncRunRefusedException("This pair is off.");
        if (!_store.FirstSyncDone(pairId)) throw new SyncRunRefusedException("Review and apply the first sync first.");
        RefuseLayout(pair);
        await _endpoints.PrepareAsync(pair, ct);
        await RefreshDropboxQuotaAsync(pair, ct);
        var throttle = new ProgressThrottle(progress, _time, _settings.ProgressInterval);
        var reviewScopes = ReviewedRemovalObservationScopes(pair);
        // A retry deadline gates effects, never observations. Lift the exact reviewed-removal
        // exclusion for every removal kind, not unrelated selective exclusions or permanent boundaries.
        var observationPair = reviewScopes.Count == 0 ? pair : pair with
        { Excluded = pair.Excluded.Where(path => !reviewScopes.Contains(path)).ToArray() };
        var batch = options?.Batch;
        var reconcile = await ReconcileAsync(pair, throttle, ct, intentFilter: batch is null ? null : batch.Contains);
        BothScan scan;
        var before = LeftAlone(pair, includeRestore: false).WithPending(reconcile.Protected);
        try
        {
            scan = batch is null ? await ScanBothAsync(observationPair, throttle, ct) :
                await ScanBatchAsync(observationPair, batch, throttle, ct);
            if (batch is null) RetainReviewedRemovalObservations(observationPair, scan, reviewScopes, ct);
        }
        catch (Exception error) when (RunFailure(error, ct))
        {
            throttle.Flush();
            RecordUnavailable(pairId, error);
            return new(null, FailedRun(pairId, [new ActionResult(string.Empty, Outcome.Failed, null, null, null, error.Message)]), null);
        }

        IReadOnlyList<ReconciliationCoverage> coverages = batch is null ? ActiveCoverages(pair, ref scan) : [];
        RecordScanRetries(pair, scan, batch?.Paths);
        var rootsComplete = !scan.A.IncompleteSubtrees.Contains(string.Empty) &&
            !scan.B.IncompleteSubtrees.Contains(string.Empty);
        if (_endpoints is PlanOnlyEndpoints && !rootsComplete)
            return new(new Plan(pairId, 0, [], [])
            { RunUnavailable = "A side could not be read in full. This preview is unavailable; retry after the read outage." }, null, null);
        // Restore verification reads both sides (including SidesEqual's stored inventory). Neither
        // incomplete coverage nor a read-only preview can authorize a retry or retire its protection.
        IReadOnlyList<string> recoveryTouched = batch is null && rootsComplete && _endpoints is not PlanOnlyEndpoints
            ? await RefreshRecoveryRestoresAsync(pair, scan, ct) : [];
        if (batch is null) CloseObservedRepairs(pair, scan);
        // Finding 1 (Astra): a link or stub that disappears in this run still protects its path for the whole run.
        // The restore refresh above may have frozen a new intent; take the run's hold snapshot after it.
        ResetRecoveryProtection();
        var retryCount = _store.CountSyncRetries(pairId);
        var retries = batch is null ? _store.DueSyncRetries(pairId, _time.GetUtcNow(), 256, CachedIdentityRefresh.PendingReason)
            .Where(retry => retry.Path.Length != 0 || retry.Kind != "run").ToArray() : [];
        ct.ThrowIfCancellationRequested();
        var deferredPlanning = batch is null ? AdmitPlanningPage(pairId, pair) : ReconciliationPlanningPage(pair, batch);
        var current = LeftAlone(pair); // Admission may retire non-work; summaries must observe that transition.
        using var planningTiming = new RunBoundaryTiming("planning", _settings.EnableRunInstrumentation);
        var activeRetries = CachedIdentityRefresh.ActiveRetryPaths(retries);
        var retryHeld = retries.Where(item => item.Held is not null).Select(item => item.Held!).ToArray();
        var pendingEvidence = scan.A.PendingDeletionEvidence.Concat(scan.B.PendingDeletionEvidence)
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, Side?>? winners = decisions?.ToDictionary(entry => entry.Key,
            entry => (Side?)entry.Value, StringComparer.OrdinalIgnoreCase);
        // Execution-time space refusals have no intent. Their durable notices, like unconfirmed content,
        // must bring the paths back into an otherwise empty incremental run until they resolve.
        var retryPaths = batch is not null ? batch.Paths.ToArray() : _store.UnresolvedNotices(pairId).Where(PlanNotices.HasPathScope)
            .Where(notice => (notice.Rule == UnconfirmedRule && notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key) ||
                notice.ReasonKey == "name-mapping-collision" ||
                notice.ReasonKey is FreeSpaceRefusal.InsufficientReasonKey or FreeSpaceRefusal.UnknownSpaceReasonKey or
                    FreeSpaceRefusal.UnknownReserveReasonKey)
            .Select(notice => notice.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Content/space notices explicitly rescope their work even before the retry backoff expires.
        // Otherwise that same retry row filters out the very path we just admitted for re-evaluation.
        var noticeHeldPaths = retryPaths.Where(path => _store.SyncRetryAt(pairId, path) is { } retry &&
            (retry is { Held: not null } or { Kind: "decision" or "deletion-evidence" } ||
             CachedIdentityRefresh.Pending(retry))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        activeRetries.UnionWith(retryPaths.Where(path => !noticeHeldPaths.Contains(path)));
        var retryProtectionFactory = RunRetryProtectionFactory(pairId, activeRetries, progress);
        var retryProtection = retryProtectionFactory();
        void RefreshRetryProtection() => retryProtection = retryProtectionFactory();
        var incrementalRestore = RecoveryProtection(pair);
        var links = Protection(pair, scan, before, decisions, activeRetries,
            noticeHeldPaths.Concat(retryHeld.Select(item => item.Path)),
            (path, descendants) => retryProtection(path, descendants));
        var predicates = Predicates(pair, links);
        var scanner = new RunScope(_store, predicates.A, predicates.B, links, rules: PairInclude.IgnoreRules(pair),
            decisions: decisions, activeRetries: activeRetries,
            restoreProtection: incrementalRestore, persistDeferred: true, cancellationToken: ct);
        retryPaths = retryPaths.Concat(recoveryTouched).Concat(retries.Select(item => item.Path))
            .Concat(deferredPlanning.SelectMany(item => item.Paths)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rootDebtSettling = batch is null && rootsComplete && (scan.A.RootDebtSettles || scan.B.RootDebtSettles);
        var recoveryPaths = batch is null ? reconcile.Touched : reconcile.Touched.Where(batch.Contains);
        var touched = new List<string>(scan.Touched(recoveryPaths.Concat(retryPaths)
            .Concat(decisions?.Keys ?? Enumerable.Empty<string>())));
        // Finding 3: a whole-root read retry expands planning to the whole pair only in the run where
        // that debt actually settles. While another root is unreadable its empty-path failure keeps
        // every row protected, and no retry can retire, so an earlier sleeping retry on the healthy
        // side must not load and join the whole pair. The empty path is added only when both whole
        // roots were read and a side's debt settled; otherwise it carries no planning authority.
        if (!rootsComplete) touched.RemoveAll(path => path.Length == 0);
        else if (rootDebtSettling) touched.Add(string.Empty);
        RunScope.Scope mappingContext = new([], [], []);
        RunScope.Scope? inventory = null;
        Func<RunScope.Scope> loadScope = () =>
        {
            if (inventory is not null) return inventory;
            var changesA = scan.A.ChangeObjectIds.Concat(deferredPlanning.Where(item => item.RootId == pair.A.RootId)
                .Select(item => item.ObjectId)).ToArray();
            var changesB = scan.B.ChangeObjectIds.Concat(deferredPlanning.Where(item => item.RootId == pair.B.RootId)
                .Select(item => item.ObjectId)).ToArray();
            scan.PendingChanges(scanner, changesA, changesB);
            var scope = scan.AnyFull ? scanner.LoadFull(pair) :
                scanner.LoadTouched(pair, touched, changesA, changesB);
            inventory = LoadReconciliationCoverage(scope, coverages);
            // Keep only small ancestor context, not a second full inventory, between planning passes.
            mappingContext = new([], [], []) { ContextA = scope.ContextA, ContextB = scope.ContextB };
            return scope;
        };

        if (!scan.AnyFull && !scan.HasChanges && reconcile.Touched.Count == 0 && retryPaths.Length == 0 &&
            decisions is null && pendingEvidence.Count == 0 && !rootDebtSettling)
        {
            // Path decisions were not re-evaluated; standing summaries still reflect current stored state.
            RaiseLinksSummary(pair, current);
            AddProviderNameRefusalNotice(pair);
            AdvanceCursors(scan);
            throttle.Flush();
            var remainingPlanning = ReportSettledPlanning(pairId, 0, 0, progress);
            // Finding 1: an empty delta is still unfinished while retry debt exists (including a retry
            // sleeping on its backoff), so the early return goes through the same outcome rule as execution.
            return new(new Plan(pairId, 0, [], []), IncrementalOutcome(
                WithReconciliation(new RunReport(pairId, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, [])
                    { Deferred = retryCount, JournalBacklog = scan.JournalBacklog, PlanningBacklog = remainingPlanning },
                    reconcile), []), null);
        }

        void refresh(HashRequest request) => inventory = scanner.RefreshHashes(pair, loadScope(), request);
        var rows = await HashAndJoinAsync(pair, loadScope, scan, false, throttle, ct, refresh);
        throttle.Report(RunPhases.Planning, null, 0, 0);
        var rowEvaluated = EvaluatedPaths(rows);
        var mappingRetries = _store.UnresolvedNotices(pairId).Where(n => n.ReasonKey == "name-mapping-collision")
            .Select(n => n.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settleEvaluated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool evaluated(string path) => rowEvaluated(path) || mappingRetries.Contains(path) || settleEvaluated.Contains(path);
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
        var planningBacklog = 0;
        var settlement = batch is null ? PlanSettlePage(pair, scan, links, deferredPlanning, touched, predicates.A, predicates.B, ct) :
            (Rows: (IReadOnlyList<SqliteStateStore.DeferredPlanning>)[], Adopts: (IReadOnlyList<SyncAction>)[], Candidates: 0);
        settleEvaluated.UnionWith(settlement.Rows.SelectMany(row => row.Paths));
        deferredPlanning = deferredPlanning.Concat(settlement.Rows).ToArray();
        decided = decided with { Actions = decided.Actions.Concat(settlement.Adopts).ToArray() };
        // Both lanes retire through the same acknowledgement; drain is measured on the remaining
        // queue, so ordinary-intake retirements count as progress exactly like settle-lane ones.
        var settledCount = 0;
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
        var reviewedRemovalScopes = retries.SelectMany(item => item.ReviewedActions ?? [])
            .Where(DeleteSourceGuard.ReviewedRemoval).Select(action => action.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // All due reviewed-removal kinds may cross their own exact exclusion; only rebuildable
        // removals receive rebuildable selection authority. Child exclusions remain protected.
        var retryPair = pair with { Excluded = pair.Excluded.Where(path => !reviewedRemovalScopes.Contains(path)).ToArray() };
        var retrySelection = RebuildableSelection.Sides(pair) is { } retrySides
            ? new RebuildableSelection(retrySides.Cloud, rebuildableRetries) : null;
        plan = await ApplyReviewedRetriesAsync(pair, plan, retries, ct);
        foreach (var coverage in coverages) plan = coverage.DeferDependencies(plan);
        RefreshRetryProtection();
        plan = ApplySafetyFilter(pair, ActionPredicates(retryPair, links, retrySelection), plan);
        RaiseLinksSummary(pair, current);
        var openNotices = _store.UnresolvedNotices(pair.PairId);
        RetainPlanningFailures(pair, plan);
        WriteNotices(plan.Notices);
        RefreshRetryProtection();
        plan = ApplySafetyFilter(pair, ActionPredicates(retryPair, links.WithPending(
            _store.ReadSyncRetries(pairId).Where(retry => retry.PendingNotice is not null).Select(retry => retry.Path)), retrySelection), plan);
        plan = plan with { HeldDeletions = retryHeld, FirstSyncOptions = Options(pair, false, winners, null) };
        var reviewedDeletes = retries.Where(item => item.HeldReviewed && item.Held is not null && item.HeldExpected is { } expected &&
                    _store.Live(item.Held.Local == Side.A ? pair.A.RootId : pair.B.RootId, [item.Path]).FirstOrDefault() is { } currentHeld &&
                    expected.ObjectId == currentHeld.ObjectId && Content.SideStamp(expected) == Content.SideStamp(currentHeld))
                    .Select(item => FolderDelete.VictimKey(item.Held!.Local, item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        planningTiming.PeakScopeRows = scanner.PeakMaterializedRows;
        planningTiming.Dispose();
        return new(plan, null, ExecutePreparedAsync, () =>
        {
            var initial = PrepareInitialExecution(plan, pair, reviewedDeletes, allowUnreviewedEvidence: true);
            return new IncrementalPlanPreview(initial.Plan, initial.Unapproved);
        });

        async Task<RunReport> ExecutePreparedAsync()
        {
            // Reconciliation finishes effects admitted in earlier runs. This limit gates NEW work,
            // before execution records intents, adopts baselines or advances observation cursors.
            var count = plan.Actions.LongCount(action => action.Kind != ActionKind.Adopt);
            if (options?.MaxPlannedActions is { } bound && count > bound)
            {
                WriteNotices([PlanNotices.PlanBoundExceeded(pairId, count, bound)]);
                throttle.Flush();
                return PlanBoundReport(WithReconciliation(
                    BuildReport(pairId, plan, new Tally(_settings), false), reconcile)
                    with { JournalBacklog = scan.JournalBacklog });
            }
            ClosePlanBoundNotice(pairId);
            var execution = await ExecuteMeasuredPlanAsync(plan, pair, throttle, reviewedDeletes, ct);
            plan = execution.Plan;
            var tally = execution.Tally;
            var completion = new CompletionSteps(this, pairId, ct);
            var unresolvedEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            completion.Step(() =>
            {
                unresolvedEvidence.UnionWith(execution.Pending.Where(path =>
                    _store.SyncRetryAt(pairId, path) is { Held: not null, ReprobeEvidence: true, Kind: not "decision" }));
                WriteRestoredEvidenceNotices(pairId, unresolvedEvidence);
            });
            if (!ct.IsCancellationRequested)
            {
                // Cursor promotion requires durable retry input for every unfinished item.
                // If that write failed, retain the previous cursor for safe replay.
                var retrySaved = completion.Step(() => UpdateRetryWork(pair, plan, tally, CompletionRetries(pairId, batch, plan, retries),
                    execution.Pending, evaluated, scan.A.Complete && scan.B.Complete));
                retrySaved &= completion.Step(() => RetainPlanningFailures(pair, plan));
                completion.Step(() => CloseResolvedNotices(openNotices, plan, tally, path => evaluated(path) &&
                    _store.SyncRetryAt(pairId, path) is not { Held: not null, Kind: not "decision" }));
                await completion.StepAsync(() => CloseArrivedReplacementsAsync(pair, openNotices, plan.Gen, ct));
                completion.Step(() => CloseAppliedDecisions(openNotices, plan, tally, decisions, resolvingNoticeId));
                completion.Step(RefreshRetryProtection);
                using (var acknowledgementTiming = new RunBoundaryTiming("acknowledgement", _settings.EnableRunInstrumentation))
                    completion.Step(() => AcknowledgeVerifiedRehash(pair, scan, plan, tally, evaluated, predicates.A, predicates.B, ct, deferredPlanning,
                        _ => settledCount++, batch));
                completion.Step(() => planningBacklog = ReportSettledPlanning(pairId, settledCount, deferredPlanning.Count,
                    progress, settlement.Candidates - settlement.Rows.Count));
                completion.Step(() => RaiseLinksSummary(pair, LeftAlone(pair)));
                // Inventory settlement and cursor completion share one durable transaction.
                if (retrySaved)
                {
                    completion.Step(() => AdvanceCursor(scan.A));
                    completion.Step(() => AdvanceCursor(scan.B));
                }
                if (batch is null) completion.Step(_store.Prune);
            }
            return FinishIncrementalCompletion(completion, pairId, plan, tally, reconcile, pendingEvidence.Keys,
                unresolvedEvidence, throttle, scan.JournalBacklog, planningBacklog, ct);
        }
    }

    // Retry rows are keyed by the affected path; unrelated reviewed-action JSON is never read.
    private HashSet<string> ReviewedRemovalObservationScopes(PairDef pair) => pair.Excluded.Where(path =>
        (_store.SyncRetryAt(pair.PairId, path)?.ReviewedActions ?? []).Any(action =>
            DeleteSourceGuard.ReviewedRemoval(action) &&
            string.Equals(action.Path, path, StringComparison.OrdinalIgnoreCase)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
        Planner.Decide(pair.PairId, Math.Max(scan.A.Gen, scan.B.Gen),
            InterruptedDeleteRepair.Protect(rows, _store, pair.PairId),
            // Hidden held children must prevent collapse before the action guard drops their parent delete.
            // Otherwise independent matched child deletes disappear with that collapsed parent.
            Options(pair, false, winners, null, matchedAtPrepare: new RetryMatches(_store, pair.PairId))
                with { MappingContextA = scope.ContextA, MappingContextB = scope.ContextB, ProtectedDescendants = links.Contains });

    // Decision 64: the paths of open user-decision notices, except the ones being decided in this run.
    private IEnumerable<string> PendingDecisions(string pairId, IReadOnlyDictionary<string, Side>? deciding) =>
        _store.UnresolvedNotices(pairId).Where(notice => (notice.Kind == NoticeKind.UserDecision ||
                                                   notice.ReasonKey == "deletion-history-unavailable" || notice.Quarantine is not null) &&
                                                   notice.ReasonKey != "name-mapping-collision" &&
                                                   PlanNotices.HasPathScope(notice) &&
                                                   // A finished replace no longer holds its path: the incoming item must arrive.
                                                   (!NoticeChoices.AwaitsArrival(notice) || notice.ConflictOperation is { Restoring: true, RestorePath: null }) &&
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
        return a is not null && b is not null && Content.SameContent(a, b) == true;
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
        Func<IEnumerable<string>, ActionGuard>? Prepare = null,
        Func<SyncAction, ItemEligibility.Result>? Explain = null);

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
        var eligibility = new ItemEligibility(_store.Pairs(), _productOwned(pair), (_, _, item, _) =>
            links.CoversLink(item.Path) || links.ContainsLink(item.Path) ? "link" :
            links.CoversStub(item.Path) || links.ContainsStub(item.Path) ? "stub" :
            ProtectionHold(links, item.Path, descendants: true));
        var guard = new ActionGuard(links.Narrow(plain.A), links.Narrow(plain.B), links.NarrowAction(strict.A),
            links.NarrowAction(strict.B), IgnoreRules: PairInclude.IgnoreRules(pair),
            Prepare: links.RequiresBatch ? paths => ActionPredicates(pair, links.ForPaths(paths), rebuildables) : null,
            Explain: action => eligibility.ExplainEffect(pair, action));
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
    private Plan DropUnderNewLocalLinks(PairDef pair, Plan approved, ActionGuard? guard = null) =>
        DropUnderNewLocalLinks(pair, approved, guard, out _);

    private Plan DropUnderNewLocalLinks(PairDef pair, Plan approved, ActionGuard? guard, out int supersededDeletes)
    {
        supersededDeletes = 0;
        var superseded = new HashSet<string>(StringComparer.Ordinal);
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
            var oppositeAppeared = false;
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
                        oppositeAppeared = true;
                }
            }
            // A disproved absence retires the old delete; it is not an unfinished removal.
            // Probe all local endpoints first: unknown/protected paths still own pending work.
            if (oppositeAppeared) superseded.Add(action.ActionId);
            return oppositeAppeared;
        }
        var dropped = approved.Actions.Where(Drops).Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        supersededDeletes = superseded.Count;
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
                var lockNotice = FailureRetryOwner.RetryNotice(pair.PairId, action.Path,
                    "The local path could not be checked. The reviewed removal is retained; nothing was copied or deleted");
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

    private IReadOnlySet<string> ActiveFirstSyncRetries(PairDef pair) =>
        _store.DueSyncRetries(pair.PairId, _time.GetUtcNow(), 256, CachedIdentityRefresh.PendingReason)
            .Where(retry => RetryProtectionDomain.Includes(retry) && retry.Held is null &&
                !retry.ReprobeEvidence && retry.PendingNotice is null &&
                retry.Kind is not ("decision" or "deletion-evidence" or "scan") &&
                retry.ReadRoots is not { Count: > 0 } &&
                !pair.Excluded.Any(path => PairPath.IsSelfOrUnder(retry.Path, path)))
            .Select(retry => retry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal string? EligibilityHold(PairDef pair, Side side, ItemState item, ItemEligibility.Purpose purpose)
    {
        var protection = Protection(pair, purpose: purpose, proofItem: item,
            proofRoot: side == Side.A ? pair.A.RootId : pair.B.RootId).ForPaths([item.Path]);
        return ProtectionHold(protection, item.Path, item.Kind == ItemKind.Folder);
    }

    // One composition owner for preview, reviewed apply, incremental planning and repair admission.
    // Active retries and explicit decisions are input authority, never a different stage policy.
    private LinkExclusions Protection(PairDef pair, BothScan? scan = null, LinkExclusions? before = null,
        IReadOnlyDictionary<string, Side>? decisions = null, IReadOnlySet<string>? activeRetries = null,
        IEnumerable<string>? additional = null, Func<string, bool, bool>? retryLookup = null,
        ItemEligibility.Purpose purpose = ItemEligibility.Purpose.Plan, ItemState? proofItem = null,
        string? proofRoot = null, IReadOnlySet<string>? proofPaths = null)
    {
        var proving = purpose == ItemEligibility.Purpose.ContentProof;
        var retries = _store.ReadSyncRetries(pair.PairId);
        var ownProofPaths = retries.Where(retry => proving &&
            (proofItem is not null && PairPath.Same(retry.Path, proofItem.Path) || proofPaths?.Contains(retry.Path) == true) &&
            CachedIdentityRefresh.NeedsObservation(retry) && retry.PendingNotice?.Kind != NoticeKind.UserDecision &&
            (retry.ReadRoots is null || retry.ReadRoots.Count == 1 && retry.ReadRoots.Contains(proofRoot!)))
            .Select(retry => retry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var initial = proving ? LinkExclusions.HoldsOf(_store, pair) : LeftAlone(pair);
        // Background proof queries do not reuse a run's cached restore snapshot.
        var proofRestore = proving ? RecoveryRestoreProtection.Create(_store, pair.PairId) : null;
        var links = initial.WithPending(PendingDecisions(pair.PairId, decisions))
            .WithPending(OutstandingFootprints(pair.PairId))
            // Identity refresh must remain inventory-visible and uses the bounded retry
            // protection owner below, never a root-sized pending-path collection.
            .WithPending(retries
                .Where(retry => RetryProtectionDomain.Includes(retry) && !CachedIdentityRefresh.Pending(retry))
                .Where(retry => !ownProofPaths.Contains(retry.Path))
                .Where(retry => retry.Kind == "scan" || retry.ReadRoots is { Count: > 0 } ||
                    retry.PendingNotice is not null)
                .Select(retry => retry.Path));
        if (before is not null) links = before.With(links);
        if (scan is not null)
            links = links.WithPending(ScanFailures(scan))
                .WithPending(scan.A.PendingDeletionEvidence.Keys.Concat(scan.B.PendingDeletionEvidence.Keys));
        if (additional is not null) links = links.WithPending(additional);
        var retry = retryLookup ?? RetryProtection.Create(_store, pair.PairId,
            proving ? ownProofPaths :
                activeRetries ?? ActiveFirstSyncRetries(pair));
        return links.WithRetryProtection((path, descendants) => retry(path, descendants) ||
            proofRestore?.Covers(path, descendants) == true);
    }

    // Links always, and online-only stubs while their replacement is off (decision 57), are left alone this run.
    // Restore holds attach the run's one snapshot only when a frozen restore intent exists (finding 6). The pre-scan
    // snapshot passes includeRestore: false: the post-refresh set owns restore protection, so a run takes one gate.
    private LinkExclusions LeftAlone(PairDef pair, bool includeRestore = true)
    {
        var links = LinkExclusions.Of(_store, pair, stubsLeftAlone: !_settings.ReplaceOnlineOnlyStubs);
        if (!includeRestore) return links;
        var restore = RecoveryProtection(pair);
        return restore.Active ? links.WithRetryProtection(restore.Covers) : links;
    }

    /// <summary>
    /// The run's single pending-restore snapshot: one indexed gate and one bounded preload per run, shared by every
    /// link callback and RunScope. Reset when the run releases the gate, so the next run observes new holds.
    /// </summary>
    private RecoveryRestoreProtection RecoveryProtection(PairDef pair)
    {
        var snapshot = _restoreSnapshot.Value ??= new RestoreSnapshot();
        lock (snapshot.Gate)
        {
            if (snapshot.Protection is { } cached && string.Equals(snapshot.PairId, pair.PairId, StringComparison.Ordinal))
                return cached;
            var created = RecoveryRestoreProtection.Create(_store, pair.PairId,
                () => RunLog.Trace("Recovery restore protection snapshot limit reached; using indexed lookups with a bounded cache."));
            snapshot.Protection = created;
            snapshot.PairId = pair.PairId;
            return created;
        }
    }

    /// <summary>Drops the run's cached hold snapshot so the next use observes a pending/ready transition.</summary>
    private void ResetRecoveryProtection()
    {
        if (_restoreSnapshot.Value is not { } snapshot) return;
        lock (snapshot.Gate)
        {
            snapshot.Protection = null;
            snapshot.PairId = null;
        }
    }

    // Decisions 57/59: the summaries are standing pair-level notices, carried by every plan while their items exist.
    private static Plan WithLinksSummary(PairDef pair, LinkExclusions current, Plan plan) =>
        current.Summaries(pair.PairId) is { Count: > 0 } summaries
            ? plan with { Notices = plan.Notices.Concat(summaries).ToArray() }
            : plan;


    // Keeps the stored links notice current: refreshed when its count or folders change, closed once no link remains.
    private void RaiseLinksSummary(PairDef pair, LinkExclusions links)
    {
        var summaries = links.Summaries(pair.PairId);
        foreach (var open in _store.UnresolvedNotices(pair.PairId))
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

    // Finding 1: a run never reports Complete while retry debt or unreleased deletion evidence
    // remains. A retry sleeping on its backoff is still unfinished work that keeps its notice, so
    // every incremental return path passes through this one rule and RunHistory records it honestly.
    private static RunReport IncrementalOutcome(RunReport report, IReadOnlyList<string> evidencePendingPaths) =>
        report with { DeletionEvidencePending = evidencePendingPaths, IncludesRetryDebt = true };

    private RunReport WithIndexingStatus(RunReport report)
    {
        if (report.Outcome == RunOutcomes.StateDamaged) return report;
        // Indexing belongs to roots. Read only for the live report, never schedule a sync
        // or persist this changing status in historical run outcomes.
        try
        {
            if (_store is SqliteStateStore store && store.Pair(report.PairId) is { } pair)
                return report with { UrgentPending = store.UrgentPending(report.PairId), IndexingRemainingByRoot = new[] { pair.A, pair.B }
                    .Where(root => root.Provider == SyncProviders.Local).DistinctBy(root => root.RootId)
                    .ToDictionary(root => root.RootId, root => store.IndexingRemaining(root.RootId), StringComparer.Ordinal) };
        }
        catch (Exception error) { RunOutput.Observe(error); }
        return report;
    }

    // Only the run owner decides the terminal disposition, after all completion/enrichment.
    // An attempt that finishes with item debt is Partial even if no individual effect succeeded.
    internal static RunReport PlanBoundReport(RunReport report) => report with { PlanBoundExceeded = true };

    private static RunReport FinalizeOutcome(RunReport report, CancellationToken ct)
    {
        var outcome = report.HaltedBy is { } fatal && StorageFailureClassifier.ScopeOf(fatal, ct) == FailureRetryOwner.FailureScope.State
            ? RunOutcomes.StateDamaged
            : report.Paused || report.Outcome == RunOutcomes.Paused || report.HaltedBy is PairPausedException ? RunOutcomes.Paused
            : report.Cancelled || ct.IsCancellationRequested || report.Outcome == RunOutcomes.Stopped ? RunOutcomes.Stopped
            : report.PlanBoundExceeded ? RunOutcomes.PlanBoundExceeded
            : report.AttemptFailed || report.Outcome == RunOutcomes.Failed ? RunOutcomes.Failed
            : report.CompletionFailed || report.Pending > 0 || report.Deferred > 0 ||
                report.DeletionEvidencePending.Count > 0 || report.ContentUnconfirmedCount > 0 ||
                report.UnreviewedPending > 0 || report.RebuildablesBlocked.Count > 0 ||
                report.DecisionFailureReason is not null ? RunOutcomes.Partial : RunOutcomes.Complete;
        return report with { Outcome = outcome };
    }

    internal void AdvanceCursors(BothScan scan)
    {
        AdvanceCursor(scan.A);
        AdvanceCursor(scan.B);
    }

    private void AdvanceCursor(SideScan side)
    {
        // Targeted read debt permits a valid newer checkpoint, but an incomplete full
        // listing with no checkpoint must not erase the previously committed cursor.
        var complete = (!side.Full || side.Complete || side.CursorAfter.Length > 0) && !side.IncompleteSubtrees.Contains(string.Empty) && side.PendingDeletionEvidence.Keys.All(side.ReleasedDeletionEvidence.Contains);
        _store.CompleteGeneration(side.Gen, side.CursorAfter, complete,
            settleInventory: side.Full && side.CursorAfter.Length > 0 && !side.IncompleteSubtrees.Contains(string.Empty),
            cursorHeldForEvidence: !side.PendingDeletionEvidence.Keys.All(side.ReleasedDeletionEvidence.Contains));
        // Only a completed full-inventory side settles this obligation. A targeted
        // quarantine refresh (or an ordinary delta) cannot account for the skipped range.
        // Pending evidence retains its cursor/replay and per-item protection, not a full walk.
        // A whole-root read failure still retains the inventory obligation.
        // Finding 2: the whole-root inventory obligation covers what the walk could READ. Once a
        // full walk has covered every readable path, a named unreadable subfolder is scoped debt
        // (its own durable scan retry and notice, which keep it hidden from planning and free of
        // delete or absence authority) rather than a reason to repeat the whole walk each run.
        // Changes inside that folder are observed when its retry re-reads it. Only a whole-root
        // failure (the empty sentinel) keeps this obligation open.
        // Observation coverage advances with effect replay, never merely with ingestion. A crash
        // between these writes can cause extra hydration, but cannot skip unfinished changes.
        if (complete && side.ObservedCoverage is { } coverage && _store is SqliteStateStore sql)
            sql.SaveInventoryObservation(coverage, side.CursorAfter);
    }

    private static string Unverified(IReadOnlyList<ActionResult> uncertain) =>
        "Unresolved effects could not be verified: " +
        string.Join("; ", uncertain.Select(result => result.ActionId + ": " + result.Reason));

    private static RunReport BuildReport(string pairId, Plan plan, Tally tally, bool cancelled)
    {
        var held = plan.Actions.Count(action => action.Kind == ActionKind.Hold);
        var outcome = string.Empty;
        return new RunReport(pairId, outcome, tally.Done, tally.Failed, held, tally.Uploaded, tally.Downloaded,
            tally.Deleted, tally.Moved, tally.KeptBoth, tally.Failures) { Skipped = tally.Skipped, Uncertain = tally.Uncertain, ProviderReached = !cancelled, Cancelled = cancelled };
    }
}
