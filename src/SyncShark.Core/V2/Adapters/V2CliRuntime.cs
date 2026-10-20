using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>The v2 CLI operations surface: pairs, first-sync review, runs, the recovery and notice catalogs; everything else says not in v2 yet.</summary>
public sealed partial class V2CliRuntime : ISyncCliOperations
{
    private const string ModeText = "direct-v2";
    private const int IdHexLength = 16;

    private readonly PilotInstanceLock? _instanceLock;
    private readonly IStateStore _store;
    private readonly SyncRun _run;
    private readonly IRootProbe _roots;
    private readonly string _productRoot;
    // The production composition owns the store and the long-poll HTTP client; the runtime disposes it as a whole.
    private EngineComposition? _composition;
    private bool _borrowed;

    internal static V2CliRuntime Borrow(PilotInstanceLock authority, EngineComposition composition, string root) =>
        new(authority, composition.Store, composition.Run, composition.Roots, root) { _borrowed = true };

    // The Dropbox endpoints' client: one per process, like the v1 CLI. Its timeout sits above the explicit batch
    // operation timeout, so a slow finish_batch_v2 or delete_batch is bounded by that timeout, never by the
    // transport's implicit 100-second default.
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = EngineComposition.DropboxHttpTimeoutFor(V2Settings.Default)
    };

    // Serializes the runtime's heavy pair operations - first-sync prepare and apply, run and rescan - so a
    // prepare's preview and its saved record, an apply's check/preview/execution, a run and a cursor reset
    // never interleave. A rescan refuses rather than waits when this gate is already held.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    internal V2CliRuntime(PilotInstanceLock? instanceLock, IStateStore store, SyncRun run, IRootProbe roots,
        string productRoot)
    {
        _instanceLock = instanceLock;
        _store = store;
        _run = run;
        _roots = roots;
        _productRoot = productRoot;
        var authority = instanceLock?.StateAuthority;
        ConnectionRecords = authority is null ? () => [] : () => ReadConnections(authority);
    }

    /// <summary>Reads the retained provider connections (pass-through); a test can replace this reader.</summary>
    internal Func<IReadOnlyList<ProviderConnectionRecord>> ConnectionRecords { get; set; }

    /// <summary>Whether this command asked for detailed traces on the console; the dispatcher sets it, the run log reads it.</summary>
    internal bool Verbose { get; set; }

    // The CLI's outer run gate serializes heavy pair operations; a wait for it is announced before the run's pump
    // exists, so a queued command is never a silent start (RQ-090).
    internal static async Task WaitForDataLockAsync(SemaphoreSlim gate, Action<string> sink, CancellationToken ct,
        TimeSpan? interval = null)
    {
        if (await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) return;
        RunOutput.Write(sink, "Waiting for the Sync Shark data lock");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!await gate.WaitAsync(interval ?? TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
            RunOutput.Write(sink, $"Still waiting for the Sync Shark data lock ({(long)watch.Elapsed.TotalSeconds}s)");
    }

    private static IReadOnlyList<ProviderConnectionRecord> ReadConnections(PilotStateAuthority authority) =>
        new DropboxConnectionStore(authority).LoadConnections()
            .Concat(new GoogleDriveConnectionStore(authority).LoadActiveBindings()
                .Select(item => item.ToProviderRecord()))
            .ToArray();

    /// <summary>Takes the same exclusive product lock as v1 and opens the fresh v2 store under it.</summary>
    public static V2CliRuntime AcquireProduction(string[] args)
    {
        var invocation = SyncCliParser.Parse(args);
        if (invocation.Command is not ("pairs pause" or "pairs resume")) return AcquireProduction();
        // Control-plane only: no endpoint, executor or product lock. SQLite serializes the tiny flag write
        // with the active run, which keeps its exclusive product lock until its safe-point exit.
        var root = SyncDataLocation.ProductRoot;
        var store = SqliteStateStore.OpenPauseControl(root);
        return new V2CliRuntime(null, store, null!, null!, root);
    }

    /// <summary>Opens the ordinary exclusive-lock runtime, never the pause control plane.</summary>
    public static V2CliRuntime AcquireProduction()
    {
        var productRoot = SyncDataLocation.ProductRoot;
        PilotInstanceLock instanceLock;
        try
        {
            instanceLock = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile);
        }
        catch (IOException error) when (SyncCliRuntime.IsLockContention(error))
        {
            throw new SyncCliBusyException(
                "Sync Shark already holds its exclusive product state lock for this Windows user, so this command " +
                "cannot read or change product state here. Use the running application: start Sync Shark and run " +
                "the command again, so it executes inside that process.", error);
        }
        catch (Exception error)
        {
            throw new SyncCliOperationException(
                "The Sync Shark product state could not be opened: " + SyncCli.DescribeException(error), error);
        }
        try
        {
            var composition = EngineComposition.Open(
                new EngineHostInputs(productRoot, instanceLock.StateAuthority, SharedHttp), V2Settings.Default,
                TimeProvider.System);
            return new V2CliRuntime(instanceLock, composition.Store, composition.Run, composition.Roots, productRoot)
            {
                _composition = composition
            };
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Mode => ModeText;

    /// <inheritdoc />
    public string ProductRoot => _productRoot;

    /// <inheritdoc />
    public bool AuthorityStateExists => File.Exists(DatabaseFile);

    /// <inheritdoc />
    public bool RunActive => false;

    /// <inheritdoc />
    public bool RunPaused => false;

    private string DatabaseFile => SqliteStateStore.DatabasePath(_productRoot);

    /// <inheritdoc />
    public Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken)
    {
        var pairs = _store.Pairs();
        var accounts = Accounts();
        var pending = pairs.Sum(pair => _store.UnresolvedIntents(pair.PairId).Count);
        var attention = _store.OpenNotices(null).Count;
        return Task.FromResult(new SyncCliStatusData("Sync Shark", ProductVersion(), BuildIdentity.SourceRevision,
            SyncCli.StateAccessNote, _productRoot, AuthorityStateExists, accounts.Count, accounts,
            new SyncCliPairTallyData(pairs.Count, pairs.Count(pair => pair.Enabled),
                pairs.Count(pair => !pair.Enabled), 0),
            pending, null, SyncCli.StateAccessNote, ModeText, false, false, attention));
    }

    /// <inheritdoc />
    public Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = Accounts();
        return Task.FromResult(new SyncCliAccountListData(accounts.Count, accounts));
    }

    /// <inheritdoc />
    public Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken)
    {
        var pairs = _store.Pairs().Select(Project).ToArray();
        return Task.FromResult(new SyncCliPairListData(pairs.Length, pairs));
    }

    /// <inheritdoc />
    public Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken) =>
        Task.FromResult(Project(RequirePair(pairId)));

    /// <inheritdoc />
    public async Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken, int limit,
        int offset)
    {
        var pair = RequirePair(pairId);
        // The incremental plan preview after the first sync is deferred core v2 plans
        // only the first-sync review, so a later plan says it is not here yet rather than refreshing deltas.
        if (_store.FirstSyncDone(pairId)) throw NotInV2();
        // The same gate as prepare and apply (decision 54): a plan preview must not scan state underneath an
        // apply that is re-reviewing it, or a child captured by that scan could reach a collapsed folder delete
        // without ever being shown at prepare.
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, cancellationToken);
        Plan plan;
        try
        {
            plan = await _run.PreviewFirstSyncAsync(pairId, NoProgress, cancellationToken);
            if (plan.RunUnavailable is { } unavailable)
                throw new SyncRunPendingVerificationException(unavailable, 30);
        }
        catch (SyncRunRefusedException error)
        {
            throw new SyncCliReviewException(error.Message);
        }
        catch (EndpointAccessException error)
        {
            throw new SyncCliReviewException(ReconnectText(error));
        }
        finally
        {
            _runGate.Release();
        }
        var items = plan.Actions.Select(action => Project(pair, action)).ToArray();
        var page = items.Skip(offset).Take(limit).ToArray();
        var localOnly = plan.Actions.Count(action => action.Kind != ActionKind.Hold && action.Target == Side.B);
        var dropboxOnly = plan.Actions.Count(action => action.Kind != ActionKind.Hold && action.Target == Side.A);
        return new SyncCliPlanData(pair.PairId, pair.Name, !_store.FirstSyncDone(pairId), true, items.Length,
            localOnly, dropboxOnly, ConflictCount(plan), page.Length, offset + page.Length < items.Length, page)
        { Limit = limit, Offset = offset };
    }

    /// <inheritdoc />
    public async Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress, CancellationToken ct)
    {
        var pair = RequirePair(pairId);
        // A per-run log file (RQ-130): the run's lines are recorded, and its path is announced before the run starts.
        using var log = RunLog.Open(_productRoot, pair.Name, "run", Verbose);
        var sink = RunLog.Sink(log, progress);
        await WaitForDataLockAsync(_runGate, sink, ct);
        try
        {
            var report = await _run.IncrementalAsync(pairId, new RunProgressForwarder(sink), ct);
            await SettleAfterRunAsync(ct).ConfigureAwait(false);
            return new SyncCliRunData(report.PairId, pair.Name, report.Uploaded, report.Downloaded, 0,
                report.KeptBoth, report.Held,
                string.Equals(report.Outcome, RunOutcomes.Stopped, StringComparison.Ordinal))
            {
                Outcome = report.Outcome,
                Failed = report.Failed,
                Deleted = report.Deleted,
                Moved = report.Moved,
                Diagnostics = report.Diagnostics,
                NeedsVerification = report.Uncertain
            };
        }
        catch (SyncRunRefusedException error)
        {
            throw new SyncCliReviewException(error.Message);
        }
        catch (EndpointAccessException error)
        {
            throw new SyncCliReviewException(ReconnectText(error));
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncCliPreparationData> PrepareFirstSyncAsync(string pairId, Action<string> progress,
        CancellationToken ct, string? choicesJson = null)
    {
        var pair = RequirePair(pairId);
        using var log = RunLog.Open(_productRoot, pair.Name, "prepare", Verbose);
        var sink = RunLog.Sink(log, progress);
        await WaitForDataLockAsync(_runGate, sink, ct);
        try
        {
            // Observation and authorization have different lifetimes: invalidate the reviewed choices,
            // but reuse their complete inventory if its exact coverage still matches this request.
            string? unreadable = null;
            var previous = FirstSyncChoicesState.Load(_productRoot, pairId, _store, reason => unreadable = reason);
            if (unreadable is not null)
            {
                sink("Discarding unreadable first-sync prepare (" + unreadable + "); reading both sides in full.");
                // A discarded record authorizes nothing and supplies no reusable inventory checkpoint.
                foreach (var rootId in new[] { pair.A.RootId, pair.B.RootId })
                {
                    _store.ForgetCursor(rootId);
                    if (_store is SqliteStateStore database) database.ForgetInventoryObservation(rootId);
                }
            }
            var inventoryCursors = previous is null ? null :
                FirstSyncChoicesState.PendingInventoryCursors(previous, _run.CompatibleInventoryScope(pair, previous.InventoryScope));
            _run.RetainInventoryObservation(pair, inventoryCursors);
            // A new prepare supersedes the last one, even when its document is refused; it authorizes an apply
            // only once its preview succeeded.
            FirstSyncChoicesState.Clear(_productRoot, pairId, _store);
            foreach (var retry in _store.ReadSyncRetries(pairId))
                if (retry.Kind is "first-sync" or "post-preview") _store.ClearSyncRetry(pairId, retry.Path);
            var choices = await ParseChoicesAsync(pair, choicesJson, ct);
            // One detection only: the preview returns the resolution its plan used, and the report and the frozen
            // record are both built from that same object.
            var (plan, rebuildables) = await _run.PreviewFirstSyncResolvedAsync(pairId, choices.Winners,
                choices.OneSided, choices.Rebuildables, new RunProgressForwarder(sink), ct, inventoryCursors);
            if (plan.RunUnavailable is { } unavailable)
                throw new SyncRunPendingVerificationException(unavailable, 30);
            var effects = ChoiceEffects(pair, plan, choices.OneSided);
            // Decision 74: warn, never refuse. Retain the diagnostic so apply needs no second detection.
            var warnings = choices.Rebuildables
                .Where(choice => choice.Prefix.Length > 0 && !rebuildables.Detected.Any(choice.Matches))
                .Select(choice => $"rebuildables '{choice.Id}' under '{choice.Prefix}/' matched no folders: check the prefix")
                .ToArray();
            FirstSyncChoicesState.Save(_productRoot, pairId, choices, effects,
                FolderDelete.VictimKeys(_store, pair, plan), rebuildables.Selected, plan.HeldDeletions,
                matched: plan.MatchedBothSides, warnings: warnings,
                carriedIgnoredDeletes: CarriedIgnoredReport(pair, plan), store: _store,
                inventoryCursors: plan.InventoryCursors.Select(entry =>
                    new FirstSyncChoicesState.StoredInventoryCursor(entry.Key, entry.Value)).ToArray(),
                inventoryScope: plan.InventoryScope);
            return Preparation(pair, plan, effects, FirstSyncChoices.Digest(choices, rebuildables.Selected, plan.HeldDeletions),
                RebuildableReport(rebuildables.Detected, choices.Rebuildables, rebuildables.Selected)) with { Warnings = warnings };
        }
        catch (SyncRunRefusedException error)
        {
            throw new SyncCliReviewException(error.Message);
        }
        catch (EndpointAccessException error)
        {
            throw new SyncCliReviewException(ReconnectText(error));
        }
        finally
        {
            _runGate.Release();
        }
    }

    private SyncCliPreparationData Preparation(PairDef pair, Plan plan,
        IReadOnlyList<SyncCliChoiceEffectData> effects, string digest,
        IReadOnlyList<SyncCliRebuildableData> rebuildables)
    {
        var pairId = pair.PairId;
        var localOnly = plan.Actions.Count(action => action.Kind != ActionKind.Hold && action.Target == Side.B);
        var dropboxOnly = plan.Actions.Count(action => action.Kind != ActionKind.Hold && action.Target == Side.A);
        long uploadBytes = 0, downloadBytes = 0;
        foreach (var action in plan.Actions)
        {
            if (action.Source is null) continue;
            if (action.Target == Side.B) uploadBytes += action.Source.Size;
            else downloadBytes += action.Source.Size;
        }
        var stubs = Planner.StubSummary(plan);
        return new SyncCliPreparationData(pair.PairId, pair.Name, 0, "v2", "v2", plan.Actions.Count, localOnly,
            dropboxOnly, ConflictCount(plan), uploadBytes, downloadBytes, 0, [],
            FileBytes(DatabaseFile), FileBytes(DatabaseFile), FileBytes(DatabaseFile + "-wal"),
            FileBytes(DatabaseFile + "-wal"), false, _store.FirstSyncDone(pairId),
            effects, digest, rebuildables)
        { CarriedIgnoredDeletes = CarriedIgnoredReport(pair, plan),
            HeldDeletionCount = plan.HeldDeletions.Count, DeletionEvidenceResolved = plan.DeletionEvidenceResolved,
            HeldDeletionExamples = plan.HeldDeletions.Take(50)
                .Select(held => new SyncCliHeldDeletionData(held.Path, FirstSyncChoices.SideText(held.Local),
                    held.Rule == HeldDeletion.NewerDeletionRule ? HeldDeletion.NewerDeletionDisplayRule : held.Rule)).ToArray(),
            StubDownloads = stubs.Replaced, StubDownloadBytes = stubs.Bytes, StubsWithoutCounterpart = stubs.WithoutCounterpart,
            LinksExcluded = plan.Notices.FirstOrDefault(notice => notice.ReasonKey == LinkExclusions.Reason.Key)?.Detail,
            StubsLeftAlone = plan.Notices.FirstOrDefault(notice => notice.ReasonKey == LinkExclusions.StubReason.Key)?.Detail };
    }

    /// <inheritdoc />
    public async Task<SyncCliFirstSyncResultData> ApplyFirstSyncAsync(string pairId, string? choicesJson,
        bool allowRecommended, long? expectedPolicyRevision, Action<string> progress, CancellationToken ct,
        string? expectedLocalScanId = null)
    {
        var pair = RequirePair(pairId);
        if (choicesJson is null && !allowRecommended)
            throw new SyncCliUsageException("first-sync apply requires --choices or --allow-recommended.");
        using var log = RunLog.Open(_productRoot, pair.Name, "apply", Verbose);
        var sink = RunLog.Sink(log, progress);
        var choices = await ParseChoicesAsync(pair, choicesJson, ct);
        await WaitForDataLockAsync(_runGate, sink, ct);
        try
        {
            // Decision 54: an apply is bound to the reviewed prepare. Its choices must be exactly the ones the
            // prepare recorded, or there is nothing to apply that the user reviewed.
            var prepared = FirstSyncChoicesState.Load(_productRoot, pairId, _store) ?? throw new SyncCliReviewException(
                "No usable first-sync prepare exists for this pair — run prepare again.");
            // Apply re-previews with the same choices and exactly the rebuildable set the prepare froze (never a fresh
            // detection); the run drops any path that no longer qualifies.
            var rebuildables = prepared.Rebuildables
                .Select(entry => new RebuildableFolder(entry.Category, entry.Path, 0, 0)).ToArray();
            // Decision 54: the digest covers the normalized choices AND the frozen (category, path) set, so neither a
            // changed document nor a changed set can be applied under the wrong review. Every frozen category must
            // still be one of the chosen categories.
            var bindingMatches = prepared.NarrowedChoicesDigest is { } narrowedBinding
                ? string.Equals(narrowedBinding, FirstSyncChoices.Digest(choices, []), StringComparison.Ordinal)
                : string.Equals(prepared.Digest, FirstSyncChoices.Digest(choices, rebuildables, prepared.HeldDeletions), StringComparison.Ordinal);
            if (!bindingMatches ||
                rebuildables.Any(folder => !choices.Rebuildables.Any(choice => choice.Matches(folder))))
                throw new SyncCliReviewException(
                    "The first-sync choices differ from the last prepare — run prepare again.");
            var forwarder = new RunProgressForwarder(sink);
            // the paths the reviewed prepare saw on both sides are its baseline. The apply passes
            // them to the planner so a later one-sided survivor is deleted (or copied back if it changed) as ordinary
            // sync, without requiring a persisted baseline row.
            var matched = prepared.Database?.MatchLookup ?? prepared.Matched.ToDictionary(match => match.Path,
                match => new PrepareMatch(match.A, match.B), StringComparer.OrdinalIgnoreCase);
            // The apply's one rebuildable revalidation happens here, before planning; the resolution it returns is
            // handed to the apply so the executed plan and the exclusions can never disagree.
            var currentScope = _run.InventoryScope(pair);
            var pendingCursors = FirstSyncChoicesState.PendingInventoryCursors(prepared,
                _run.CompatibleInventoryScope(pair, prepared.InventoryScope));
            RunLog.Trace(prepared.InventoryScope == currentScope
                ? $"first-sync inventory: compatible pending checkpoint ({pendingCursors.Count} cursor(s))"
                : "first-sync inventory: pending checkpoint unavailable (missing or mismatched scope)");
            var (plan, rebuildableResolution) = await _run.PreviewFirstSyncApplyResolvedAsync(pairId, choices.Winners,
                choices.OneSided, rebuildables, matched, forwarder, ct, pendingCursors, prepared.NarrowedIgnoreRules);
            if (plan.RunUnavailable is { } unavailable)
                throw new SyncRunPendingVerificationException(unavailable, 30);
            plan = _run.FilterNarrowedPrepare(pair, plan, prepared.NarrowedIgnoreRules);
            // Newly held paths are pending a separate review, not effects authorized by this apply.
            // Partition before charging potential removes so they cannot inflate the frozen budget.
            plan = FirstSyncChoicesState.PartitionHeldDeletions(prepared, plan);
            plan = _run.DeferPostPreviewChanges(pair, plan, matched);
            var reviewed = FirstSyncChoicesState.ReviewedDeleteKeys(prepared);
            plan = _run.DeferUnreviewedDeletes(pair, plan, reviewed);
            plan = FirstSyncChoicesState.BindCarriedIgnoredBudgets(prepared, plan);
            var effects = ChoiceEffects(pair, plan, choices.OneSided);
            var grown = FirstSyncChoicesState.GrownRemoveChoices(prepared, choices.OneSided, effects);
            if (grown.Count > 0)
            {
                plan = _run.HoldGrownRemoveChoices(pair, plan, choices.OneSided, grown);
                effects = ChoiceEffects(pair, plan, choices.OneSided);
            }
            // only first-sync choice deletes and unreviewed deletion-evidence deletes need to be in
            // the reviewed set; ordinary deletion propagation after a successful prepare is ongoing sync.
            FirstSyncChoicesState.RefuseUnreviewedDeletes(reviewed,
                FolderDelete.SplitVictims(_store, pair, plan, reviewed).Guarded);
            // The refreshed observation is durable before effects start, independently of execution success.
            // Keep the frozen review unchanged; legacy/reset prepares gain a usable checkpoint after one full
            // fallback. Full snapshots supply their fully drained race-closure cursor, never a new latest-cursor request here.
            prepared = FirstSyncChoicesState.UpdatePendingInventoryCursors(_productRoot, pairId, prepared,
                plan.InventoryCursors, plan.InventoryScope!);
            var warnings = prepared.Warnings ?? [];
            // The plan retains only its indexed matched lookup; the prepare's other collections are no longer needed.
            prepared = null!;
            // The reviewed set goes into the apply path as well: the run re-checks it at execution time under its own
            // gate, so a scan that interleaved after this preview still cannot delete a victim the prepare never showed.
            var report = await _run.ApplyAsync(pairId, plan, forwarder, ct, reviewed, rebuildableResolution);
            await SettleAfterRunAsync(ct).ConfigureAwait(false);
            // The record only authorizes a first sync, so once that is done the record goes. The exclusions were
            // already saved by the run, inside the same completion step that marked the first sync done.
            if (_store.FirstSyncDone(pairId)) FirstSyncChoicesState.Clear(_productRoot, pairId, _store);
            return Result(pair, report, effects) with { Warnings = warnings };
        }
        catch (SyncRunRefusedException error)
        {
            throw new SyncCliReviewException(error.Message);
        }
        catch (EndpointAccessException error)
        {
            throw new SyncCliReviewException(ReconnectText(error));
        }
        finally
        {
            _runGate.Release();
        }
    }

    private SyncCliFirstSyncResultData Result(PairDef pair, RunReport report,
        IReadOnlyList<SyncCliChoiceEffectData> effects) =>
        new(pair.PairId, pair.Name, report.Done, report.Skipped, _store.FirstSyncDone(pair.PairId),
            report.Uploaded, report.Downloaded, 0, report.KeptBoth, report.Held)
        {
            Outcome = report.Outcome,
            Failed = report.Failed,
            Stopped = string.Equals(report.Outcome, RunOutcomes.Stopped, StringComparison.Ordinal),
            NeedsVerification = report.Uncertain,
            ChoiceEffects = effects,
            DeletionEvidencePendingCount = report.DeletionEvidencePending.Count,
            ReviewedDeletionEvidencePendingCount = report.ReviewedDeletionEvidencePendingCount,
            ContentUnconfirmedCount = report.ContentUnconfirmedCount,
            ContentUnconfirmedExamples = report.ContentUnconfirmedExamples,
            UnreviewedPending = report.UnreviewedPending,
            DeletionEvidencePendingExamples = report.DeletionEvidencePending.Take(50).ToArray(),
            Deferred = report.Deferred,
            Diagnostics = report.Diagnostics,
            Incomplete = report.Deferred > 0
                ? EdgeMessages.Text("firstSyncIncomplete", report.Deferred.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                    (report.RebuildablesBlocked.Count == 0 ? "" : " The cloud copy could not be removed for: " +
                        string.Join(", ", report.RebuildablesBlocked) + ".")
                : null,
            BlockedRebuildables = report.RebuildablesBlocked,
            OpenIssues = _store.OpenNotices(pair.PairId).GroupBy(notice => notice.ReasonKey ?? notice.Kind.ToString())
                .ToDictionary(group => group.Key, group => group.Count())
        };

    /// <inheritdoc />
    public Task<bool> SetPairPausedAsync(string pairId, bool paused, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequirePair(pairId);
        if (_store is not SqliteStateStore sql) throw new NotSupportedException("Durable pause requires SQLite.");
        // Do not take the run gate: the running operation must be able to observe this request.
        sql.SetPairPaused(pairId, paused);
        return Task.FromResult(paused);
    }

    /// <inheritdoc />
    public async Task<SyncCliPairData> SetPairEnabledAsync(string pairId, bool enabled, CancellationToken ct)
    {
        // The same gate as remove: a read/save must not resurrect a pair that a removal deleted while this
        // command held an older definition. Ordinary enable/disable semantics are unchanged.
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, ct);
        try
        {
            var pair = RequirePair(pairId);
            if (enabled && PairAdmission.DecideSaved(pair, _store.Pairs()) is { } refusal)
                throw new SyncCliUsageException(refusal.Detail);
            _store.SavePair(pair with { Enabled = enabled });
            return Project(pair with { Enabled = enabled });
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncCliSelectionData> SetSelectionAsync(string pairId, string? path, string selection,
        CancellationToken ct)
    {
        // 'folders include' and 'folders exclude' both land here. Decision 58: this is pair state only - excluding
        // never deletes, moves or writes a file on either side, whether or not a first sync is still incomplete.
        var included = selection.ToLowerInvariant() switch
        {
            "include" => true,
            "exclude" => false,
            _ => throw new SyncCliUsageException("The --selection value must be include or exclude.")
        };
        if (path is null)
            throw new SyncCliUsageException("A folder selection change requires --path <pair-relative folder>.");
        // Validated before the pair lock, so a bad path changes nothing even while another operation holds the gate.
        var relative = CreateExclusions.ValidateSelection(path);
        // The same gate as prepare and apply: an exclusion change must not interleave with the scan whose reviewed
        // record it invalidates, or a prepare started before this save could write a record bound to the old rules.
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, ct);
        try
        {
            var pair = RequirePair(pairId);
            // Exclusions are whole subtrees and core v2 has no inclusion exceptions: drop the path's own and
            // narrower rules, then add the path itself. Including removes only rules at or below it.
            var retained = pair.Excluded.Where(item => !PairPath.IsSelfOrUnder(item, relative)).ToList();
            if (included)
            {
                var parent = retained.FirstOrDefault(item => PairPath.IsSelfOrUnder(relative, item));
                if (parent is not null)
                    throw new SyncCliUsageException($"'{relative}' is still excluded by the parent folder " +
                        $"'{parent}'. Include that folder first. Nothing was changed.");
            }
            else if (!retained.Any(item => PairPath.IsSelfOrUnder(relative, item)))
            {
                retained.Add(relative);
            }
            var excluded = retained.ToArray();
            // Added exclusions only remove reviewed work; inclusion widens coverage and needs a fresh review.
            IReadOnlyList<string> warnings = [];
            if (!excluded.SequenceEqual(pair.Excluded, StringComparer.OrdinalIgnoreCase))
            {
                if (!included) warnings = SaveNarrowerPair(pair, pair with { Excluded = excluded });
                else
                {
                    FirstSyncChoicesState.Clear(_productRoot, pairId, _store);
                    // Partial first-sync applies also own baselines and deletion evidence (decision 81).
                    var verificationIntents = _store.SavePairIncluded(pair, pair with { Excluded = excluded });
                    warnings = [$"Re-included history was forgotten. Files deleted on one side while excluded will be copied back; newer differing files win, with ties or unknown times held. {verificationIntents} retained intents require verify-only reconciliation; none will be blindly replayed."];
                }
            }
            var saved = RequirePair(pairId);
            foreach (var warning in warnings) Console.Error.WriteLine(warning);
            return new SyncCliSelectionData(pairId, relative,
                !saved.Excluded.Any(prefix => PairPath.IsSelfOrUnder(relative, prefix)), saved.Excluded.Count)
                { Warnings = warnings };
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncCliPairData> RetirePairAsync(string pairId, CancellationToken ct)
    {
        // In core v2 retirement is disabling the pair: a pair definition carries no retired flag yet, so the
        // projection reports the pair off and never claims it is retired . No file effects.
        // The same gate as remove, so retirement cannot save back a definition a removal just deleted.
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, ct);
        try
        {
            var pair = RequirePair(pairId);
            _store.SavePair(pair with { Enabled = false });
            return Project(pair with { Enabled = false });
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncCliPairRemovalData> RemovePairAsync(string pairId, string archiveDirectory, bool dropUnfinished,
        CancellationToken ct)
    {
        // Validate the destination and the store before taking the data lock: a bad archive directory or a
        // runtime without the archival store changes nothing, even while another heavy operation holds the gate.
        if (string.IsNullOrWhiteSpace(archiveDirectory))
            throw new SyncCliUsageException("'pairs remove' requires --archive <archive-directory>.");
        string directory;
        try
        {
            directory = Path.GetFullPath(archiveDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SyncCliUsageException("The --archive value must be a valid directory path. Nothing was changed.");
        }
        if (_store is not SqliteStateStore store)
            throw new SyncCliUnsupportedException(
                "Pair archival and removal is available only with the core v2 SQLite store. Nothing was changed.");
        // The same gate as prepare and apply: removal resets enclosing pairs' cursors, so it must not
        // interleave with a scan or a reviewed apply.
        await WaitForDataLockAsync(_runGate, Console.Error.WriteLine, ct);
        try
        {
            var removedPair = RequirePair(pairId);
            // Only remaining pairs enclosing a removed root gain an admitted subtree. Invalidate their
            // prepares, matching the store's cursor resets; unrelated pairs retain their reviewed work.
            // The store invokes this callback after archive verification and before removing any row.
            var pairIds = _store.Pairs().Where(item => item.PairId != pairId &&
                (PairAdmission.NestedPath(item.A, removedPair.A) is not null ||
                 PairAdmission.NestedPath(item.A, removedPair.B) is not null ||
                 PairAdmission.NestedPath(item.B, removedPair.A) is not null ||
                 PairAdmission.NestedPath(item.B, removedPair.B) is not null))
                .Select(item => item.PairId).ToArray();
            PairRemovalArchive removal;
            try
            {
                removal = store.ArchiveAndRemovePair(pairId, directory, dropUnfinished, () =>
                {
                    // The store clears DB generations inside its existing removal transaction.
                    foreach (var id in pairIds.Append(pairId)) FirstSyncChoicesState.Clear(_productRoot, id);
                }, invalidatePrepares: pairIds);
            }
            catch (ArgumentException error)
            {
                // The store's exact-target and archive-name guards are usage-level refusals.
                throw new SyncCliTargetException(error.Message);
            }
            catch (InvalidOperationException error)
            {
                // A still-enabled pair: the store's message names pairs disable and the cancel option.
                throw new SyncCliUsageException(error.Message);
            }
            return new SyncCliPairRemovalData(removal.PairId, removal.ArchivePath, removal.ManifestPath,
                removal.RowCounts, removal.OrphanedRoots, removal.UnfinishedEffects)
            {
                Warning = "Recovery payload files are retained on disk; their catalog rows are discoverable " +
                    "only from the archive database after removal."
            };
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot,
        string? name, int? priority, bool enabled, CancellationToken ct) =>
        CreatePairAsync(accountId, remotePath, localRoot, name, priority, enabled, [], ct, null);

    /// <inheritdoc />
    public async Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot,
        string? name, int? priority, bool enabled, IReadOnlyList<string> excluded, CancellationToken ct,
        Action<string>? progress)
    {
        // Exclusions are validated before anything is observed or saved; each applies to both sides.
        var exclusions = CreateExclusions.Validate(excluded);
        // Paths are canonical before they name ids, are compared, observed or saved, so an alias such as
        // 'C:\x\..\a' or '/a//b' is the same root as its plain form.
        localRoot = CanonicalRoot(SyncProviders.Local, localRoot);
        remotePath = CanonicalRoot(SyncProviders.Dropbox, remotePath);
        var local = new RootDef(RootId(SyncProviders.Local, null, localRoot), SyncProviders.Local, null, localRoot,
            string.Empty);
        var cloud = new RootDef(RootId(SyncProviders.Dropbox, accountId, remotePath), SyncProviders.Dropbox, accountId,
            remotePath, string.Empty);
        var pair = new PairDef(PairId(local, cloud), name ?? remotePath, local, cloud, exclusions, [], enabled);
        // Admission is checked against every saved pair (a re-create of the same roots is an equal root): the
        // paths first, so a refused layout is never observed, then again with the observed root identities, so
        // one folder reached by another path or through another account is refused before anything is saved.
        var saved = _store.Pairs();
        Admit(pair, saved);
        pair = pair with
        {
            A = local with { RootObjectId = await RootObjectIdAsync(SyncProviders.Local, null, localRoot, ct) },
            B = cloud with
            {
                RootObjectId = await RootObjectIdAsync(SyncProviders.Dropbox, accountId, remotePath, ct)
            }
        };
        Admit(pair, saved);
        _store.SavePair(pair);
        return Project(pair);
    }

    private static void Admit(PairDef pair, IEnumerable<PairDef> others)
    {
        if (PairAdmission.Decide(pair, others) is { } refusal) throw new SyncCliUsageException(refusal.Detail);
    }

    private static string CanonicalRoot(string provider, string rootPath)
    {
        try
        {
            return PairAdmission.Canonical(provider, rootPath);
        }
        catch (ArgumentException)
        {
            throw new SyncCliUsageException(PairAdmissionReasons.UnsafePath.Detail);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_borrowed) return; // The host, not a disconnected client, owns the engine and authority lease.
        _storageConnections?.Dispose();
        if (_composition is not null) _composition.Dispose();
        else _store.Dispose();
        _dropboxHttp?.Dispose();
        _instanceLock?.Dispose();
        await Task.CompletedTask;
    }

    private static IProgress<RunProgress> NoProgress { get; } = new RunProgressForwarder(_ => { });

    private PairDef RequirePair(string pairId) =>
        _store.Pair(pairId) ?? throw new SyncCliTargetException("No folder pair has that exact identity.");

    private SyncCliPairData Project(PairDef pair) => new(pair.PairId, pair.Name, pair.B.Provider, pair.B.AccountId ?? "",
        pair.A.RootPath, pair.B.RootPath, pair.Enabled, 0, "TwoWay", 0, _store.FirstSyncDone(pair.PairId),
        !_store.FirstSyncDone(pair.PairId), false, pair.Excluded.Count, pair.IgnoreRules.Count, false)
    {
        SelectionRules = pair.Excluded.Select(path => new SyncSelectionRule(path, Included: false)).ToArray()
    };

    private static SyncCliPlanItemData Project(PairDef pair, SyncAction action)
    {
        var local = action.Source is not null && action.Target == Side.B;
        return new SyncCliPlanItemData(action.Path, action.Target == Side.B ? "Dropbox" : "Local",
            action.Kind is ActionKind.MkDir ? "folder" : "file", FileType(action.Path), Recommendation(action),
            action.Kind.ToString(), local ? action.Source!.Size : null, local ? null : action.Source?.Size,
            local ? action.Source!.Mtime : null, local ? null : action.Source?.Mtime);
    }

    private static string Recommendation(SyncAction action) => action.Kind switch
    {
        ActionKind.Hold => "Conflict",
        ActionKind.Delete => "Delete",
        ActionKind.Move => "Move",
        ActionKind.Adopt or ActionKind.Forget => "Keep",
        _ => action.Target == Side.B ? "Upload" : "Download"
    };

    private static string FileType(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot <= 0 || dot == path.Length - 1 ? string.Empty : path[dot..];
    }

    private static int ConflictCount(Plan plan) =>
        plan.Actions.Count(action => action.Kind == ActionKind.Hold) +
        plan.Notices.Count(notice => notice.Kind == NoticeKind.AutoConflict);

    private static long FileBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return 0; }
    }

    // FirstSyncChoices owns the whole --choices vocabulary; here we only resolve the file-or-inline input and
    // hand it to that validator, so prepare and apply always read one parsed and validated document.
    private async Task<FirstSyncChoices.Parsed> ParseChoicesAsync(PairDef pair, string? choicesJson,
        CancellationToken ct)
    {
        if (choicesJson is null)
            return new FirstSyncChoices.Parsed(new Dictionary<string, Side?>(StringComparer.Ordinal), [], []);
        var json = await SyncCliOperationsBase.ReadFirstSyncChoicesAsync(choicesJson, ct);
        return FirstSyncChoices.Parse(json, pair);
    }

    // One row per rebuildable category, in the catalogue's order. A row appears even when the category is not chosen,
    // so the user always sees the recommendation; at most twenty example paths are listed per row.
    private static IReadOnlyList<SyncCliRebuildableData> RebuildableReport(IReadOnlyList<RebuildableFolder> detected,
        IReadOnlyList<RebuildableChoice> chosen, IReadOnlyList<RebuildableFolder> selected)
    {
        return RebuildableDetector.Categories.Select(category =>
        {
            var folders = detected
                .Where(folder => string.Equals(folder.Category, category.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new SyncCliRebuildableData(category.Id, category.Explanation, folders.Length,
                folders.Sum(folder => folder.Files), folders.Sum(folder => folder.Bytes),
                chosen.Any(choice => choice.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase) &&
                    (choice.Prefix.Length == 0 || folders.Any(choice.Matches))),
                folders.Take(20).Select(folder => folder.Path).ToArray(),
                folders.Select(folder => new SyncCliRebuildableFolderData(folder.Path,
                    chosen.Any(choice => choice.Matches(folder)) &&
                    selected.Any(selection => PairPath.IsSelfOrUnder(folder.Path, selection.Path)))).ToArray());
        }).ToArray();
    }

    private IReadOnlyList<SyncCliCarriedIgnoredData> CarriedIgnoredReport(PairDef pair, Plan plan)
    {
        var folders = plan.Actions.Where(action => action.Kind == ActionKind.Delete &&
            action.ExpectedTarget?.Kind == ItemKind.Folder).ToList();
        foreach (var held in plan.HeldDeletions)
        {
            var item = _store.Live(SideRootId(pair, held.Local), [held.Path]).FirstOrDefault();
            if (item?.Kind == ItemKind.Folder)
                folders.Add(new SyncAction("held", 19, ActionKind.Delete, held.Local, held.Path,
                    null, null, item, false, null));
        }
        var refused = ProviderRefusedPaths.Load(_store, pair);
        return folders.GroupBy(action => FolderDelete.VictimKey(action.Target, action.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var folder = group.First();
                var content = CarriedIgnoredDeletes.Snapshot(_store, pair, folder, folder.Target == Side.A ? refused.A : refused.B);
                return new SyncCliCarriedIgnoredData(folder.Path, FirstSyncChoices.SideText(folder.Target),
                    content.Files, content.Folders, content.Bytes);
            }).ToArray();
    }

    // One entry per declared choice, in document order; a choice that matched nothing appears with zeros. Every
    // item is charged to the choice that owns its own path, so a collapsed folder counts each descendant once.
    private IReadOnlyList<SyncCliChoiceEffectData> ChoiceEffects(PairDef pair, Plan plan,
        IReadOnlyList<OneSidedChoice> choices)
    {
        if (choices.Count == 0) return [];
        var tallies = choices.Select(_ => new ChoiceTally()).ToArray();
        var position = choices.Select((choice, index) => (choice, index))
            .ToDictionary(entry => entry.choice, entry => entry.index);
        var normalized = OneSidedChoiceResolver.Normalize(choices);
        var refused = ProviderRefusedPaths.Load(_store, pair);
        foreach (var action in plan.Actions)
        {
            // Only the planner's choice removes (and the rule-23 collapses of them) are charged; other deletes under
            // a remove prefix (a winner's replace, a collapse of non-choice deletes) are not the choice's effect.
            if (action is { Kind: ActionKind.Delete, ChoiceRemove: true })
                ApplyRemove(tallies, position, normalized, pair, action, action.Target == Side.A ? refused.A : refused.B);
            else if (action.Kind is ActionKind.CopyNew or ActionKind.MkDir && action.Rule is 32 or 24)
                ApplyCopy(tallies, position, normalized, pair, action);
        }
        // A held exact victim may resolve to an explicit remove on apply. Freeze that
        // potential effect during prepare too, rather than reporting a zero remove budget.
        // Held folders authorize only themselves, never an unreviewed descendant.
        foreach (var held in plan.HeldDeletions)
        {
            var item = _store.Live(SideRootId(pair, held.Local), [held.Path]).FirstOrDefault();
            if (item is not null)
                Charge(tallies, position, normalized, held.Local, held.Path, item.Kind, item.Size);
        }
        return choices.Select((choice, index) => new SyncCliChoiceEffectData(choice.Prefix,
            FirstSyncChoices.SideName(pair, choice.Present), FirstSyncChoices.ActionText(choice.Remove),
            tallies[index].Files, tallies[index].Folders, tallies[index].Bytes)).ToArray();
    }

    // A rule-32 remove charges its own item (an uncollapsed folder's descendants have their own deletes); a rule-23
    // collapse, whose victims all share its provenance, covers everything still live under it (the same expansion the
    // Executor performs). Each item goes to the choice that owns that item's own path.
    private void ApplyRemove(ChoiceTally[] tallies, IReadOnlyDictionary<OneSidedChoice, int> position,
        IReadOnlyList<OneSidedChoice> normalized, PairDef pair, SyncAction action, ProviderRefusedPaths refused)
    {
        if (action.Rule != 23)
        {
            Charge(tallies, position, normalized, action.Target, action.Path,
                action.ExpectedTarget?.Kind ?? ItemKind.File, action.ExpectedTarget?.Size ?? 0);
            return;
        }
        var ignores = PairInclude.IgnoreRules(pair);
        var rootId = SideRootId(pair, action.Target);
        foreach (var item in _store.LiveUnder(rootId, [action.Path]))
            if (!refused.Contains(item) &&
                !SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, ignores))
                Charge(tallies, position, normalized, action.Target, item.Path, item.Kind, item.Size);
    }

    private void ApplyCopy(ChoiceTally[] tallies, IReadOnlyDictionary<OneSidedChoice, int> position,
        IReadOnlyList<OneSidedChoice> normalized, PairDef pair, SyncAction action)
    {
        var present = Opposite(action.Target);
        var owner = action.Rule == 24
            ? KeptFolderOwner(normalized, present, action.Path)
            : OneSidedChoiceResolver.Resolve(normalized, present, action.Path);
        if (owner is not { Remove: false })
            return;
        tallies[position[owner]].Count(action.Kind == ActionKind.MkDir ? ItemKind.Folder : ItemKind.File,
            action.Source?.Size ?? 0);
    }

    private static void Charge(ChoiceTally[] tallies, IReadOnlyDictionary<OneSidedChoice, int> position,
        IReadOnlyList<OneSidedChoice> normalized, Side present, string path, ItemKind kind, long bytes)
    {
        if (OneSidedChoiceResolver.Resolve(normalized, present, path) is { Remove: true } owner)
            tallies[position[owner]].Count(kind, bytes);
    }

    // A folder kept for a copied child (rule 24) belongs to the longest copy choice at or under it that kept the child.
    private static OneSidedChoice? KeptFolderOwner(IReadOnlyList<OneSidedChoice> normalized, Side present,
        string folder)
    {
        foreach (var choice in normalized)
            if (choice.Present == present && !choice.Remove && PairPath.IsSelfOrUnder(choice.Prefix, folder))
                return choice;
        return null;
    }

    private static Side Opposite(Side side) => side == Side.A ? Side.B : Side.A;

    private static string SideRootId(PairDef pair, Side side) => side == Side.A ? pair.A.RootId : pair.B.RootId;

    /// <summary>Accumulates one declared choice's resolved Files, Folders and Bytes.</summary>
    private sealed class ChoiceTally
    {
        public int Files { get; private set; }
        public int Folders { get; private set; }
        public long Bytes { get; private set; }

        /// <summary>Charges one item to this choice exactly once.</summary>
        public void Count(ItemKind kind, long bytes)
        {
            if (kind == ItemKind.Folder)
                Folders++;
            else
                Files++;
            Bytes += kind == ItemKind.File ? bytes : 0;
        }
    }

    private async Task<string> RootObjectIdAsync(string provider, string? accountId, string rootPath,
        CancellationToken ct)
    {
        try
        {
            return await _roots.ObjectIdAsync(provider, accountId, rootPath, ct);
        }
        catch (SyncRunRefusedException refused)
        {
            throw new SyncCliTargetException(refused.Message);
        }
        catch (EndpointAccessException error)
        {
            throw new SyncCliReviewException(ReconnectText(error));
        }
    }

    // An account-access refusal (401, revoked sign-in): the user must reconnect that account; nothing was changed.
    private static string ReconnectText(EndpointAccessException error) =>
        "Dropbox refused access for this account. Sign in to Dropbox again, then repeat the command. (" +
        error.Message + ")";

    private IReadOnlyList<SyncCliAccountData> Accounts()
    {
        var records = ConnectionRecords();
        if (records.Count == 0) return [];
        var primary = PrimaryConnectionId(records);
        var pauses = ActivePauses();
        var pairs = _store.Pairs();
        return records.Select(record => new SyncCliAccountData(record.ConnectionId, record.Provider, record.AccountId,
            record.DisplayName, record.AccountType,
            record.IsActive && string.Equals(record.ConnectionId, primary, StringComparison.Ordinal),
            record.IsActive, pauses.Contains(record.ConnectionId),
            pairs.Count(pair => string.Equals(pair.B.AccountId, record.AccountId, StringComparison.Ordinal) ||
                string.Equals(pair.B.AccountId, record.ConnectionId, StringComparison.Ordinal)))).ToArray();
    }

    private string? PrimaryConnectionId(IReadOnlyList<ProviderConnectionRecord> records) =>
        _instanceLock?.StateAuthority is { } authority
            ? new DropboxConnectionStore(authority).Load()?.ConnectionId
            : records[0].ConnectionId;

    private IReadOnlyCollection<string> ActivePauses() => _instanceLock?.StateAuthority is { } authority
        ? PauseStateProjection.ActiveAccountPauses(new UiPreferencesStore(authority).Load(), DateTimeOffset.UtcNow)
            .Select(pause => pause.ConnectionId).ToHashSet(StringComparer.Ordinal)
        : [];

    internal static string RootId(string provider, string? accountId, string rootPath) =>
        "r" + Hex16(provider + "\n" + accountId + "\n" + rootPath);

    private static string PairId(RootDef a, RootDef b) => "p" + Hex16(a.RootId + "\n" + b.RootId);

    private static string Hex16(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..IdHexLength].ToLowerInvariant();

    private static string ProductVersion()
    {
        var informational = BuildIdentity.InformationalVersion;
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        return separator < 0 ? informational : informational[..separator];
    }

    private sealed class RunProgressForwarder(Action<string> sink) : IProgress<RunProgress>
    {
        public void Report(RunProgress value)
        {
            // A process message or heartbeat line is already a complete line; an ordinary report is a phase/bytes one.
            RunOutput.Write(sink, RunLog.Line(value));
            if (value.Diagnostics is { } diagnostics)
                RunOutput.Write(sink, EdgeMessages.Text("runDiagnostics", System.Text.Json.JsonSerializer.Serialize(diagnostics)));
        }
    }
}
