using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Massdex.SyncShark.V2;

/// <summary>Applies a plan's effects in a safe order; never decides, holds, or replays an uncertain effect blind.</summary>
public sealed partial class Executor : IExecutor
{
    private readonly IStateStore _store;
    private readonly V2Settings _settings;
    private readonly SyncTransferResourceController _admission;
    private readonly SemaphoreSlim _storeGate = new(1, 1);

    /// <summary>Creates the executor over one store, its settings and the shared admission controller.</summary>
    public Executor(IStateStore store, V2Settings settings, SyncTransferResourceController admission)
    {
        if (settings.UnknownCommitGracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settings), "Unknown commit observation grace must be positive.");
        _store = store;
        _settings = settings;
        _admission = admission;
    }

    /// <summary>Applies the plan's non-Hold actions in phase order and yields each committed result as it returns.</summary>
    public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var superseded = new List<ActionResult>();
        var trees = new List<WholeFolder>();
        var actions = await ExpandFolderDeletesAsync(p, ct, superseded, trees, WholeFolderTarget(p, a, b))
            .ConfigureAwait(false);
        var outcomes = new ConcurrentDictionary<string, Outcome>(StringComparer.Ordinal);
        var committer = new StoreCommitter(_store, _storeGate, p.PairId, p.Gen);
        // whole-folder deletes run first, before any upload, so the feed after them stays small.
        if (trees.Count > 0)
            actions = await DeleteWholeFoldersAsync(p, trees, a, b, committer, outcomes, superseded, actions, ct)
                .ConfigureAwait(false);
        var steps = ActionPhases.Assign(actions);
        var ordered = actions
            .Select(action => (Action: action, Step: steps[action.ActionId]))
            .OrderBy(entry => entry.Step.Phase).ThenBy(entry => entry.Step.Level).ToArray();
        var dependencies = actions.Concat(trees.Where(tree => tree.Handled).SelectMany(tree => tree.Actions))
            .Where(action => action.Kind is ActionKind.MkDir or ActionKind.Move or ActionKind.Delete).ToArray();
        var runner = new ActionRunner(committer, _settings, _admission, p.PairId, a, b,
            dependencies, outcomes, new DropboxQuota(_store.Pair(p.PairId),
                _store.NoticeById(p.PairId, DropboxQuota.Id(p.PairId))?.Notice.UploadSuspended == true), actions);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Two worker waves can finish while Run consumes results. Capacity follows the existing worker
        // setting (128 at 64 workers); waiting happens only after the result's durable commit, outside store locks.
        var channel = Channel.CreateBounded<ActionResult>(new BoundedChannelOptions(_settings.ResultBufferCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // These outcomes already exist before ordinary workers start. Yield them directly, rather than
        // filling a bounded queue before its reader exists (large superseded folders can exceed capacity).
        foreach (var skip in superseded) yield return skip;
        // Run cancellation still drains committed outcomes. Only abandoning enumeration cancels publication.
        using var consumerGone = new CancellationTokenSource();
        var producer = ProduceAsync(ordered, runner, channel.Writer, stop.Token, consumerGone.Token);
        try
        {
            await foreach (var result in channel.Reader.ReadAllAsync())
                yield return result;
        }
        finally
        {
            // The caller may stop reading early: stop launching and wait for in-flight work to drain.
            consumerGone.Cancel();
            stop.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How many intents' observations may be in flight at once. Each observation is an independent provider read (for
    /// Dropbox a root identity check plus one get_metadata), so the serial loop paid that latency once per intent.
    /// Sixteen keeps the reads overlapped without flooding the provider; it is the same order as the adapter's own
    /// membership reads (eight) while covering both endpoints.
    /// </summary>
    internal const int ReconcileObservationParallelism = 16;

    /// <summary>Resolves recorded intents without replaying their effects. Whole-folder groups resume their
    /// change-feed check and required restoration; ordinary intents use read-only observation.</summary>
    /// <remarks>
    /// Each intent is observed and committed on its own, exactly as before; only independent intents overlap, a bounded
    /// number at a time. A result that is still Uncertain stays unresolved (Committing it changes nothing), and a
    /// completed resolution is durably written before its progress count advances, so a stop mid-reconcile keeps every
    /// finished item. Results keep the caller's input order.
    /// </remarks>
    public async Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
        IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null)
    {
        var results = new ActionResult[intents.Count];
        var completed = 0;
        // One batch job backs many intents (a 1,000-entry folder create is one job): check each job id once for
        // this pass and share that read, instead of paying one provider check per entry. The cache dies with the
        // call, so a later run asks the provider again.
        var jobChecks = new ProviderJobCheckCache();
        var grouped = await ReconcileWholeFoldersAsync(intents, gen, a, b, ct).ConfigureAwait(false);
        for (var index = 0; index < intents.Count; index++)
            if (grouped.TryGetValue(intents[index].ActionId, out var result))
            {
                results[index] = result;
                completed++;
                try { progress?.Report(completed); }
                catch (Exception) { /* Reporting cannot undo a durable reconciliation result. */ }
            }
        await Parallel.ForEachAsync(Enumerable.Range(0, intents.Count)
            .Where(index => !grouped.ContainsKey(intents[index].ActionId)), new ParallelOptions
        {
            MaxDegreeOfParallelism = ReconcileObservationParallelism,
            CancellationToken = ct,
        }, async (index, token) =>
        {
            var intent = intents[index];
            var target = intent.Target == Side.A ? a : b;
            var source = intent.Target == Side.A ? b : a;
            var pairId = await PairForAsync(intent, token).ConfigureAwait(false);
            string? materializationBlock = null;
            if (pairId is not null && intent.ProviderEffect?.Kind == SyncDirectDownloadCheckpoint.Kind)
            {
                DefenderGrant? grant = null;
                await StoreAsync(() => { grant = _store.DefenderGrantConflict(pairId, intent); }, token).ConfigureAwait(false);
                if (grant is not null)
                {
                    materializationBlock = QuarantineFailure.GrantWarning(grant.Path);
                    var notice = QuarantineFailure.Notice(pairId, intent, materializationBlock);
                    await StoreAsync(() => _store.AddNotices([notice with { Quarantine = notice.Quarantine! with
                        { ConflictingGrantId = grant.GrantId, GrantedExclusionPath = grant.Path, GrantedStagePath = grant.StagePath } }]), token).ConfigureAwait(false);
                }
            }
            ItemState? historicalDeleteSource = null;
            if (pairId is not null && intent.Kind == ActionKind.Delete)
                await StoreAsync(() => historicalDeleteSource = StoreCommitter.DeleteSource(_store, pairId, intent),
                    token).ConfigureAwait(false);
            var result = await EffectResolver.ResolveAsync(intent, target, source, token,
                _settings.UnknownCommitGracePeriod, jobChecks: jobChecks, materializationBlock: materializationBlock,
                historicalDeleteSource: historicalDeleteSource).ConfigureAwait(false);
            if (pairId is not null)
            {
                await StoreAsync(() => _store.CommitResult(pairId, gen, intent, result), token).ConfigureAwait(false);
                // Astra review of 4c00da0: a delete job that stays unresolved leaves its intent Uncertain forever,
                // which blocks the whole pair's run (SyncRun) with no user-visible signal. Past the escalation window
                // raise a stored notice naming the path and job; the effect stays Uncertain, never replayed. Once the
                // intent resolves (Done, Failed or Skipped) that notice is closed explicitly: a completed delete drops
                // its baseline, so the path leaves the rows the generic closure (SyncRun.CloseResolvedNotices)
                // evaluates and the notice would otherwise stay open forever.
                if (result.Outcome == Outcome.Uncertain)
                {
                    if (StuckJobNotice(pairId, intent) is { } notice)
                        await StoreAsync(() => _store.AddNotices(new[] { notice }), token).ConfigureAwait(false);
                }
                else
                {
                    await StoreAsync(() => CloseStuckJobNotice(pairId, intent), token).ConfigureAwait(false);
                }
            }
            results[index] = result;
            // Only now, after this intent's durable commit, has one item actually completed.
            var count = Interlocked.Increment(ref completed);
            try { progress?.Report(count); }
            catch (Exception) { /* Reporting cannot undo a durable reconciliation result. */ }
        }).ConfigureAwait(false);
        return results;
    }

    // The escalation notice for a delete or folder-create job unresolved past V2Settings.StuckJobEscalation, or null
    // while it is fresh, has no stored job identity, or is not an asynchronous job at all. It never changes the
    // effect's Uncertain outcome.
    private Notice? StuckJobNotice(string pairId, SyncAction intent)
    {
        if (intent.ProviderEffect is not { } checkpoint) return null;
        var recorded = checkpoint.RecordedAtUtc;
        string jobId;
        if (checkpoint.Kind == WholeFolderGroupKind)
        {
            // Group checkpoints are refreshed during recovery. Escalate from the original submission,
            // and name only the job, never the potentially huge frozen victim snapshot.
            try
            {
                using var group = JsonDocument.Parse(checkpoint.Id);
                using var provider = JsonDocument.Parse(group.RootElement.GetProperty("ProviderState").GetString()!);
                recorded = provider.RootElement.GetProperty("SubmittedAt").GetDateTimeOffset();
                jobId = provider.RootElement.GetProperty("JobId").GetString() ?? "unknown whole-folder job";
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                return null;
            }
        }
        else if (checkpoint.Kind is "delete-job" or "mkdir-job") jobId = ProviderJobId(checkpoint.Id);
        else return null;
        if (recorded is null || DateTimeOffset.UtcNow - recorded.Value < _settings.StuckJobEscalation) return null;
        return new Notice(pairId, intent.Rule, NoticeKind.Info, intent.Path,
            string.Format(ExecutionReasons.StuckProviderJob.Detail, intent.Path, jobId),
            ExecutionReasons.StuckProviderJob.Key);
    }

    // Closes the open "provider-job-stuck" notice of a delete or folder-create job intent that has just resolved
    // (Done, Failed or Skipped). A completed delete removes its baseline, so its path no longer appears in the
    // evaluated rows and the generic closure cannot reach the notice; closing it here is what
    // lets it go away at all.
    private void CloseStuckJobNotice(string pairId, SyncAction intent)
    {
        if (intent.ProviderEffect is not { Kind: "delete-job" or "mkdir-job" or WholeFolderGroupKind }) return;
        foreach (var open in _store.OpenNotices(pairId))
            if (open.NoticeId is not null &&
                string.Equals(open.ReasonKey, ExecutionReasons.StuckProviderJob.Key, StringComparison.Ordinal) &&
                string.Equals(open.Path, intent.Path, StringComparison.OrdinalIgnoreCase))
                _store.CloseNotice(open.NoticeId, "resolved");
    }

    // The job id is the stable identity inside the opaque delete- or folder-create-job checkpoint
    // ({jobId, entryIndex, entryCount}); fall back to the raw evidence when it is absent or unreadable, so the
    // notice always names something checkable.
    private static string ProviderJobId(string checkpointId)
    {
        try
        {
            using var document = JsonDocument.Parse(checkpointId);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("jobId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 } jobId
                ? jobId
                : checkpointId;
        }
        catch (JsonException)
        {
            return checkpointId;
        }
    }

    /// <summary>Runs each group of equal phase and level in order with a barrier, launching up to the worker cap.</summary>
    private async Task ProduceAsync(IReadOnlyList<(SyncAction Action, Step Step)> ordered, ActionRunner runner,
        ChannelWriter<ActionResult> writer, CancellationToken ct, CancellationToken consumerGone)
    {
        try
        {
            if (_settings.MaxWorkers < 1 || _settings.PendingUploadCommitLimit < 1)
                throw new ArgumentOutOfRangeException(nameof(_settings), "Worker and pending-effect bounds must be positive.");
            using var gate = new SemaphoreSlim(_settings.MaxWorkers, _settings.MaxWorkers);
            var capacity = checked(_settings.MaxWorkers + _settings.PendingUploadCommitLimit +
                _settings.UploadBatchSize * _settings.UploadCommitConcurrency);
            using var pending = new SemaphoreSlim(capacity, capacity);
            var index = 0;
            while (index < ordered.Count && !ct.IsCancellationRequested)
            {
                PairPause.Check();
                var level = ordered[index].Step;
                var group = new List<SyncAction>();
                while (index < ordered.Count && ordered[index].Step == level)
                    group.Add(ordered[index++].Action);
                await RunGroupAsync(group, gate, pending, runner, writer, ct, consumerGone).ConfigureAwait(false);
            }
            writer.TryComplete();
        }
        catch (Exception e)
        {
            writer.TryComplete(e);
        }
    }

    private static async Task RunGroupAsync(IReadOnlyList<SyncAction> group, SemaphoreSlim gate,
        SemaphoreSlim pending, ActionRunner runner, ChannelWriter<ActionResult> writer, CancellationToken ct,
        CancellationToken consumerGone)
    {
        var running = new List<Task>();
        try
        {
            foreach (var action in InterleaveTargets(group))
            {
                PairPause.Check();
                await RunOperations.WaitAsync(pending, "pending effect slots", ct, RunOperationKind.TransferSlot).ConfigureAwait(false);
                try { await RunOperations.WaitAsync(gate, "executor worker slots", ct, RunOperationKind.TransferSlot).ConfigureAwait(false); }
                catch { pending.Release(); throw; }
                try { PairPause.Check(); }
                catch { gate.Release(); pending.Release(); throw; }
                running.Add(LaunchAsync(action, gate, pending, runner, writer, ct, consumerGone));
                // Observe every completed task (including faults) without retaining one task per plan row.
                for (var i = running.Count - 1; i >= 0; i--)
                {
                    if (!running[i].IsCompleted) continue;
                    await running[i].ConfigureAwait(false);
                    running.RemoveAt(i);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(running).ConfigureAwait(false); }
    }

    // Round-robin only inside an existing dependency barrier. Keep each side's original order,
    // including deepest-first deletes; a failed parent still reaches BlockingParent unchanged.
    private static IEnumerable<SyncAction> InterleaveTargets(IReadOnlyList<SyncAction> group)
    {
        using var a = group.Where(action => action.Target == Side.A).GetEnumerator();
        using var b = group.Where(action => action.Target == Side.B).GetEnumerator();
        var first = group.Count > 0 && group[0].Target == Side.B ? b : a;
        var second = ReferenceEquals(first, a) ? b : a;
        bool hasFirst, hasSecond;
        do
        {
            hasFirst = first.MoveNext();
            if (hasFirst) yield return first.Current;
            hasSecond = second.MoveNext();
            if (hasSecond) yield return second.Current;
        } while (hasFirst || hasSecond);
    }

    private static async Task LaunchAsync(SyncAction action, SemaphoreSlim gate, SemaphoreSlim pending,
        ActionRunner runner, ChannelWriter<ActionResult> writer, CancellationToken ct, CancellationToken consumerGone)
    {
        var previous = SyncEffectStaging.Current.Value;
        var staging = new SyncEffectStaging(() => gate.Release());
        SyncEffectStaging.Current.Value = staging;
        try
        {
            if (await runner.RunAsync(action, ct).ConfigureAwait(false) is { } result)
                await writer.WriteAsync(result, consumerGone).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (PairPausedException or OperationCanceledException or EndpointAccessException or StateDamagedException) &&
            error is not Microsoft.Data.Sqlite.SqliteException)
        {
            // The intent, if any, remains unresolved for next-run reconciliation. Do not
            // claim that an effect failed before it happened; an unknown post-write error is uncertain.
            await writer.WriteAsync(new(action.ActionId, Outcome.Uncertain, null, null, null, error.Message), consumerGone).ConfigureAwait(false);
        }
        finally
        {
            staging.Release();
            SyncEffectStaging.Current.Value = previous;
            pending.Release();
        }
    }

    private async Task<IReadOnlyList<SyncAction>> ExpandFolderDeletesAsync(Plan plan, CancellationToken ct,
        List<ActionResult> superseded, List<WholeFolder> trees, Func<SyncAction, int, bool> whole)
    {
        var pair = await GateAsync(() => _store.Pair(plan.PairId), ct).ConfigureAwait(false);
        var expanded = new List<SyncAction>(plan.Actions.Count);
        // Check every folder before launching any effects, including folders whose children already have
        // actions. Otherwise those child actions could run before discovering that their parent grew.
        var blockedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var carried = new List<(SyncAction Action, CarriedIgnoredContent Content)>();
        var needsRefusedSnapshot = plan.Actions.Any(action => action.Kind == ActionKind.Delete &&
            action.ExpectedTarget?.Kind == ItemKind.Folder && !(action.ContentsSettled && action.DescendantsPlanned));
        var refused = pair is null || !needsRefusedSnapshot ? (new ProviderRefusedPaths([]), new ProviderRefusedPaths([])) :
            await GateAsync(() => ProviderRefusedPaths.Load(_store, pair), ct).ConfigureAwait(false);
        if (pair is not null)
            foreach (var folder in plan.Actions.Where(action => action.Kind == ActionKind.Delete &&
                         action.ExpectedTarget?.Kind == ItemKind.Folder))
            {
                var coverage = folder.Target == Side.A ? refused.Item1 : refused.Item2;
                var settled = folder.ContentsSettled && folder.DescendantsPlanned;
                var protectedFolder = settled
                    ? await GateAsync(() => _store.ChoiceRemovalProtected(pair, folder.Target, folder.Path, true), ct).ConfigureAwait(false)
                    : coverage.BlocksDelete(folder.Path);
                if (protectedFolder)
                {
                    (folder.Target == Side.A ? blockedA : blockedB).Add(folder.Path);
                    await StoreAsync(() =>
                    {
                        if (!_store.OpenNotices(plan.PairId).Any(notice =>
                                notice.ReasonKey == ProviderRefusedPaths.RetainedNoticeKey && PairPath.Same(notice.Path, folder.Path)))
                            _store.AddNotices([new Notice(plan.PairId, folder.Rule, NoticeKind.Info, folder.Path,
                                ProviderRefusedPaths.RetainedNoticeDetail, ProviderRefusedPaths.RetainedNoticeKey)]);
                    }, ct).ConfigureAwait(false);
                    continue;
                }
                // Provider-refused coverage above still applies. Only a settled, never-expanded choice folder (its
                // frozen contents removed one by one, growth checked in SQLite) skips the subtree-wide listing; the
                // writer still removes it only when empty apart from decision-80 ignored children.
                if (folder.ContentsSettled && folder.DescendantsPlanned) continue;
                var current = await GateAsync(() => CarriedIgnoredDeletes.Snapshot(_store, pair, folder, coverage), ct)
                    .ConfigureAwait(false);
                if (!CarriedIgnoredDeletes.WithinBudget(current, folder.CarriedIgnoredBudget))
                {
                    (folder.Target == Side.A ? blockedA : blockedB).Add(folder.Path);
                    await StoreAsync(() => _store.AddNotices([
                        CarriedIgnoredDeletes.GrowthNotice(plan.PairId, folder, current)]), ct).ConfigureAwait(false);
                }
                else if ((current.Files > 0 || current.Folders > 0) && !current.TrivialFileNamesOnly)
                    carried.Add((folder, current));
            }
        var ancestorsA = blockedA.SelectMany(PairPath.Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ancestorsB = blockedB.SelectMany(PairPath.Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Blocked(SyncAction action)
        {
            var paths = action.Target == Side.A ? blockedA : blockedB;
            var ancestors = action.Target == Side.A ? ancestorsA : ancestorsB;
            return paths.Contains(action.Path) || PairPath.Ancestors(action.Path).Any(paths.Contains) ||
                (action.Kind == ActionKind.Delete && action.ExpectedTarget?.Kind == ItemKind.Folder &&
                    ancestors.Contains(action.Path));
        }
        foreach (var entry in carried.Where(entry => !Blocked(entry.Action)))
            await StoreAsync(() => _store.AddNotices([
                CarriedIgnoredDeletes.Notice(plan.PairId, entry.Action, entry.Content)]), ct).ConfigureAwait(false);
        foreach (var action in plan.Actions)
        {
            if (action.Kind == ActionKind.Hold) continue;
            if (Blocked(action))
            {
                superseded.Add(new ActionResult(action.ActionId, Outcome.Skipped, null, null, null,
                    EffectResolver.Superseded));
                continue;
            }
            // A folder Delete whose descendants have their own Deletes (DescendantsPlanned) is never expanded.
            if (pair is null || !FolderDelete.Expands(action))
            {
                expanded.Add(action);
                continue;
            }
            await ExpandFolderAsync(action, pair, expanded, superseded, trees, whole, action.Target == Side.A ? refused.Item1 : refused.Item2, ct).ConfigureAwait(false);
        }
        return expanded;
    }

    // the Planner keeps one Delete per folder; here each file gets its own
    // RecoveryFirst Delete and the emptied folders are removed bottom-up by the phase order.
    // finding 4: a plan freezes the folder's live descendants; if the set or any
    // descendant's state changed since, the folder is skipped (Superseded) and the next run replans.
    // a large Dropbox folder keeps the same victims and child actions, but is deleted whole.
    private async Task ExpandFolderAsync(SyncAction folder, PairDef pair, List<SyncAction> expanded,
        List<ActionResult> superseded, List<WholeFolder> trees, Func<SyncAction, int, bool> whole,
        ProviderRefusedPaths refused, CancellationToken ct)
    {
        var rootId = folder.Target == Side.A ? pair.A.RootId : pair.B.RootId;
        var descendants = await GateAsync(() => FolderDelete.SyncedDescendants(_store, pair, rootId, folder.Path, refused), ct)
            .ConfigureAwait(false);
        if (folder.FrozenVictimSignature is { } frozen &&
            !string.Equals(frozen, FolderDelete.Signature(descendants), StringComparison.Ordinal))
        {
            superseded.Add(new ActionResult(folder.ActionId, Outcome.Skipped, null, null, null,
                EffectResolver.Superseded));
            return;
        }
        var keys = descendants.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await GateAsync(() => BaselineKeysByObject(pair.PairId, folder.Target, descendants), ct)
                .ConfigureAwait(false);
        var children = descendants.Select(item => new SyncAction(folder.ActionId + ":" + item.Path.ToLowerInvariant(),
            folder.Rule, ActionKind.Delete, folder.Target, item.Path, null, null, item,
            RecoveryFirst: item.Kind == ItemKind.File, keys.GetValueOrDefault(item.ObjectId))
            {
                ChoiceRemove = folder.ChoiceRemove,
                RebuildableRemove = folder.RebuildableRemove,
                RemovalReviewChoice = folder.RemovalReviewChoice,
                FrozenChoiceId = folder.FrozenChoiceId,
                SuppressReconciledBaseline = folder.SuppressReconciledBaseline,
                FirstSyncChoice = folder.FirstSyncChoice
            }).ToList();
        children.Add(folder with { RecoveryFirst = false });
        if (whole(folder, descendants.Count)) trees.Add(new WholeFolder(children, descendants));
        else expanded.AddRange(children);
    }

    private Dictionary<string, string> BaselineKeysByObject(string pairId, Side side, IReadOnlyList<ItemState> items)
    {
        var ids = items.Select(item => item.ObjectId).ToArray();
        var byObject = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var baseline in _store.BaselinesTouching(pairId, side, ids, Array.Empty<string>()))
        {
            var state = side == Side.A ? baseline.A : baseline.B;
            byObject.TryAdd(state.ObjectId, baseline.ObjectKey);
        }
        return byObject;
    }

    // One indexed point lookup per reconciled intent (action_id is the pending_effect primary key). The previous
    // implementation materialized every unresolved intent of every pair once per intent: at ~1M rows that was
    // gigabytes and tens of minutes for a 256-intent reconcile page.
    private async Task<string?> PairForAsync(SyncAction intent, CancellationToken ct) =>
        await GateAsync(() => _store.PairForIntent(intent.ActionId), ct).ConfigureAwait(false);

    private async Task StoreAsync(Action work, CancellationToken ct)
    {
        await RunOperations.WaitAsync(_storeGate, "executor store gate", ct).ConfigureAwait(false);
        try
        {
            work();
        }
        finally
        {
            _storeGate.Release();
        }
    }

    private async Task<T> GateAsync<T>(Func<T> work, CancellationToken ct)
    {
        await RunOperations.WaitAsync(_storeGate, "executor store gate", ct).ConfigureAwait(false);
        try
        {
            return work();
        }
        finally
        {
            _storeGate.Release();
        }
    }
}
