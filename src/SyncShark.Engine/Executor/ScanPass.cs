using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One completed scan's ordered sync stages share row outcomes, holds and folder barriers.</summary>
internal sealed class ScanPass(ExecutorContext context, ItemSync items, FolderSync folders,
    FirstRunFolders firstRunFolders, ExclusionRunner exclusions, AncestorFence ancestors, SyncPorts ports)
{
    private readonly ExecutorContext _context = context;
    private readonly ItemSync _items = items;
    private readonly FolderSync _folders = folders;
    private readonly FirstRunFolders _firstRunFolders = firstRunFolders;
    private readonly ExclusionRunner _exclusions = exclusions;
    private readonly AncestorFence _ancestors = ancestors;
    private readonly SyncPorts _ports = ports;
    private long _completedFolders;
    // The first live Local-write refusal among a batch's children; the batch rethrows it after its siblings (R6-5).
    private LiveAdmissionStaleException? _refused;

    // The producer's ordinary allowance: the account's current level plus the Local hash gate (8.3).
    internal static int RowCapacity(IAdmission admission, int hashSlots) =>
        Math.Min(admission.Level, admission.WorkCapacity) + hashSlots;
    // The row pass's live-row ceiling, including uploads that released their ordinary share.
    internal static int TotalCapacity(IAdmission admission, int hashSlots) =>
        checked(admission.WorkCapacity + admission.WorkCapacity + hashSlots);

    internal async Task RunAsync(Guid scanToken, ListedSkips listed,
        IReadOnlySet<long>? retryIssues, CancellationToken ct, WorkSelection? selection = null)
    {
        var scan = _context.Store.ReadScan(_context.Pair.PairId, scanToken);
        // Only Repair's whole-pair listing covers acquired work; a folder batch is fenced like any live selection.
        if (scan.Scope is null) _ancestors.CoverListedWork(scan.ThroughWorkId);
        Func<string, bool>? deletedName = _context.BatchScope is not null
            ? key => _context.Store.SavedDeletedNameExact(_context.Pair.PairId, key)
            : scan.Discovery && _context.Store.HasDeletedNames(_context.Pair.PairId, _context.ScanId)
                ? key => _context.Store.DeletedNameExact(_context.Pair.PairId, _context.ScanId, key) : null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new TaskSet(cancel);
        Exception? failure = null;
        try
        {
            await RunRowsAsync(listed, scanToken, scan.Scope is null && scan.FirstRun, deletedName, retryIssues, tasks, cancel.Token, ct, selection);
        }
        catch (Exception error) { failure = error; }
        failure = await tasks.JoinAsync(failure, failure is not null,
            joined => _context.Events(RunEvent.Failed(joined)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        // The refused child's work is the batch's own selection, which stays pending for the pair's next wake.
        if (_refused is not null) ExceptionDispatchInfo.Capture(_refused).Throw();
    }

    private async Task RunRowsAsync(ListedSkips scan, Guid scanToken, bool firstRun,
        Func<string, bool>? deletedName, IReadOnlySet<long>? retryIssues, TaskSet tasks,
        CancellationToken runToken, CancellationToken callerToken, WorkSelection? selection)
    {
        var pair = _context.Pair;
        var store = _context.Store;
        var scope = _context.Scope;
        var batch = _context.BatchScope is not null;
        var earlier = batch ? new HashSet<string>(StringComparer.Ordinal) :
            store.ActiveFolderRemovals(pair.PairId, runToken).Select(operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        Stage(TextKey.StageAfterListing);
        if (!batch) store.ReplaceSkipped(pair.PairId, scanToken, scan.Local.Concat(scan.Dropbox));
        var localUntrusted = new PrefixSet();
        long skipped = 0;
        foreach (var item in scan.Local)
        {
            localUntrusted.Add(PathNames.Key(item.Path));
            _context.Events(RunEvent.Stage(TextKey.StageAfterListing, count: ++skipped,
                inProgress: true, path: item.Path));
        }
        if (!batch)
        {
            var allPairs = store.Pairs();
            var stages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var storedPair in allPairs)
            {
                _context.Events(RunEvent.Stage(TextKey.StageAfterListing, inProgress: true));
                var path = store.Pending(storedPair.PairId) switch
                {
                    CurrentPending current => current.Value.Stage?.Path,
                    LegacyPending legacy => legacy.StagePath,
                    NoPending => null,
                    _ => throw new InvalidDataException()
                };
                if (path is not null) stages.Add(Path.GetFileName(path));
            }
            foreach (var (path, error) in _context.Recovery.RemoveStaleStages(stages))
                _context.Events(RunEvent.Failed(error, TextKey.StageCleanupFailed, path));
        }
        var applied = new PrefixSet();
        var pending = new PrefixSet();
        var batchKey = batch ? PathNames.Key(_context.BatchScope!) : null;
        foreach (var exclusion in pair.Exclusions)
        {
            var key = PathNames.KeyFor(exclusion.Side, exclusion.Path);
            if (batch)
            {
                if (exclusion.Keep || !PathNames.IsSelfOrUnderKey(key, batchKey!) &&
                    !PathNames.IsSelfOrUnderKey(batchKey!, key)) continue;
                try
                {
                    await using var permit = await _context.EnterOperationAsync(runToken);
                    if (await _exclusions.ReadTargetAsync(exclusion, runToken) is not null) pending.Add(key);
                }
                catch (Exception error) when (ItemFailures.IsItemError(error) || error is InventoryIncompleteException)
                {
                    pending.Add(key);
                    _context.Events(RunEvent.Failed(error, path: exclusion.Path));
                }
            }
            else
            {
                _context.Events(RunEvent.Stage(TextKey.StageAfterListing, inProgress: true, path: exclusion.Path));
                (await _exclusions.ApplyAsync(exclusion, retryIssues, runToken) ? applied : pending).Add(key);
            }
        }
        var proofs = batch ? null : ReconcileIssues(localUntrusted, scan.Dropbox, applied, pending);
        var contexts = _items.ScanContexts(firstRun, retryIssues, proofs, deletedName);
        // The eligible in-use issues' own paths, from the state Repair already reads; one read per run, not per row.
        var itemRetries = new PrefixSet();
        if (retryIssues is { Count: > 0 })
            foreach (var candidate in store.ReadItemRetryState(pair.PairId, retryIssues.ToArray()).Values)
                if (candidate is { } value)
                    foreach (var item in value.Token.Subject.Items)
                        itemRetries.Add(PathNames.KeyFor(item.Side, item.Path));
        _context.Events(RunEvent.Stage(TextKey.StageAfterListing, ended: true, count: scan.Local.Count + scan.Dropbox.Count));
        Stage(TextKey.StageRenames);
        long renames = 0;
        // A batch only adds: a listed rename would move a saved item away from its saved path.
        var renameGroups = (batch ? [] : store.ScanRenames(pair.PairId, _context.ScanId))
            .GroupBy(candidate => candidate.Synced).ToDictionary(group => group.Key,
                group => new RenameExecution(group.ToArray()));
        var renameOrder = new PathLocks();
        var renamer = _items.Renames.Renamer();
        var renameGate = new object();
        Task StartRename(RenameExecution entry, IAsyncDisposable? initialOrder = null)
        {
            TaskCompletionSource? completion = null;
            Task task;
            lock (renameGate)
            {
                if (entry.Task is { } existing) task = existing;
                else
                {
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    entry.Task = task = completion.Task;
                }
            }
            if (completion is not null) _ = RunRename(entry, initialOrder, completion);
            else if (initialOrder is not null) return ReleaseUnusedOrder(initialOrder, task);
            return task;
        }

        static async Task ReleaseUnusedOrder(IAsyncDisposable order, Task existing)
        {
            await order.DisposeAsync();
            await existing;
        }

        async Task RunRename(RenameExecution entry, IAsyncDisposable? initialOrder, TaskCompletionSource completion)
        {
            try
            {
                var candidates = entry.Candidates;
                RenameDecision? decision;
                var localById = LocalById(candidates);
                var order = initialOrder ?? await renameOrder.EnterAsync(Scopes(candidates, localById), runToken);
                await using (order)
                {
                    _context.Events(RunEvent.Stage(TextKey.StageRenames, count: Interlocked.Read(ref renames),
                        total: renameGroups.Count, inProgress: true, path: candidates[0].Synced.LocalKey));
                    decision = contexts.HoldsRename(candidates) ? null :
                        await _items.Renames.RenameAsync(candidates, localById, runToken, completedScan: true);
                }
                if (decision is { Kind: RenameKind.Hold, Destination.Identity: DestinationIdentity.Moving })
                {
                    var dependency = renameGroups.GetValueOrDefault(decision.Destination.Binding);
                    var cycle = dependency is null;
                    lock (renameGate)
                    {
                        entry.Prerequisite = dependency;
                        for (var next = dependency; next is not null; next = next.Prerequisite)
                            if (ReferenceEquals(next, entry)) { cycle = true; break; }
                    }
                    if (cycle) Conflict(entry);
                    else
                    {
                        // The dependent lends its existing task slot; neither ordering nor provider lease is held.
                        await StartRename(dependency!);
                        localById = LocalById(candidates);
                        await using var freshOrder = await renameOrder.EnterAsync(Scopes(candidates, localById), runToken);
                        var fresh = await _items.Renames.RenameAsync(candidates, localById, runToken);
                        if (fresh.Kind is RenameKind.Hold or RenameKind.Notice) Conflict(entry);
                    }
                }
                _context.Events(RunEvent.Stage(TextKey.StageRenames, count: Interlocked.Increment(ref renames),
                    total: renameGroups.Count, inProgress: true, path: candidates[0].Synced.LocalKey));
                completion.TrySetResult();
            }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }

        Entry? LocalById(IReadOnlyList<RenameCandidate> candidates)
        {
            var saved = candidates[0].Synced;
            return Renames.FirstOn(candidates, Side.Local)?.Moved ??
                (store.ScanEntry(pair.PairId, _context.ScanId, Side.Local, saved.LocalKey) is { } local &&
                    local.Id == saved.LocalId ? local : null);
        }

        PathLockScope[] Scopes(IReadOnlyList<RenameCandidate> candidates, Entry? localById) =>
            LockKeys.ForPaths(pair, renamer.Footprint(candidates, localById))
                .Select(key => new PathLockScope(key, true)).ToArray();

        void Conflict(RenameExecution entry)
        {
            var candidates = entry.Candidates;
            var saved = candidates[0].Synced;
            var subject = IssueSubject.FromDistinct(candidates.Select(candidate =>
                new IssueItem(candidate.Side, candidate.Moved.Path, candidate.Moved.Id)).Concat([
                    new IssueItem(Side.Local, saved.LocalPath, saved.LocalId),
                    new IssueItem(Side.Dropbox, saved.DropboxPath, saved.DropboxId)]), candidates[0].Side);
            var change = StateChange.Empty with
            { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, candidates[0].Moved.Path, subject)] };
            store.Commit(pair.PairId, change);
            ActionExecutor.EmitIssues(_context.Events, change);
            contexts.HoldRename(candidates);
        }

        foreach (var group in renameGroups)
        {
            runToken.ThrowIfCancellationRequested();
            Task? existing;
            lock (renameGate) existing = group.Value.Task;
            if (existing is not null) { await existing; continue; }
            if (group.Key.IsFolder) await tasks.WhenAllAsync();
            var localById = LocalById(group.Value.Candidates);
            var order = await renameOrder.EnterAsync(Scopes(group.Value.Candidates, localById), runToken);
            lock (renameGate) existing = group.Value.Task;
            if (existing is not null)
            {
                await order.DisposeAsync();
                await existing;
                continue;
            }
            var execution = StartRename(group.Value, order);
            if (group.Key.IsFolder) await execution;
            else
            {
                tasks.Add(execution);
                await tasks.WaitForCapacityAsync(() => _context.Admission.WorkCapacity,
                    callerToken, _context.Admission.WorkCapacity);
            }
        }
        await tasks.WhenAllAsync();
        runToken.ThrowIfCancellationRequested();
        _context.Events(RunEvent.Stage(TextKey.StageRenames, ended: true, count: renames));
        foreach (var (side, key) in scan.LocalFolders.Concat(scan.DropboxFolders))
            contexts.Skipped(side).Add(key);
        EnrollUnreadable(scan, contexts, firstRun);
        var folderPlans = new List<(Row Folder, Side Side, int Depth)>();
        var firstFolders = new List<(Row Folder, int Depth)>();
        var restored = new HashSet<string>(PathNames.Comparer);
        var unresolved = new HashSet<string>(PathNames.Comparer);
        var resultGate = new object();
        void Completed(Row row, SyncOutcome outcome)
        {
            lock (resultGate)
            {
                if (row.Context.UnderFolderDelete is not null && outcome.Effect is not null) restored.Add(row.Key);
                // An ignored completion gives no answer; only a real one may add or clear this row's keys.
                if (outcome.OwesWork(row, store, pair.PairId) is not { } owes) return;
                if (owes && selection is not null && store.HoldingIssue(pair.PairId, row) is { } holder)
                {
                    // A failed batch child stays under its issue in this selection, so its Retry reaches that child alone.
                    var path = row.PathOn(selection.Side) ?? row.Key;
                    var entry = row.EntryOn(selection.Side);
                    var notice = new ChangeNotice(selection.Side, ChangeKind.Changed, path, null, entry?.Id, entry, false, false);
                    store.AttachIssueWork(selection, holder, [(notice, row.IsFolder(), row.Synced)]);
                }
                if (!owes && unresolved.Count == 0) return;
                foreach (var key in Row.Keys(row))
                {
                    if (owes) unresolved.Add(key);
                    else unresolved.Remove(key);
                }
            }
        }
        void Note(Row original, SyncOutcome outcome)
        {
            if (outcome.Deferred)
            {
                if (original.Local is { IsFolder: true } &&
                    (original.Context.DeletionEligible || original.Context.FirstRunDeletion ==
                        IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))
                    firstFolders.Add((original, PathNames.Depth(original.Key)));
                else
                {
                    var side = FolderDeletes.DeletingSide(original);
                    contexts.Deleting(side).Add(original.Key);
                    folderPlans.Add((original, side, PathNames.Depth(original.Key)));
                }
            }
            else if (original.Local is { IsFolder: true } && original.Context.FirstRunDeletion !=
                IssueSubject.FirstRunDeletionKind.None && outcome.Succeeded())
                firstFolders.Add((original, PathNames.Depth(original.Key)));
        }
        long completedRows = 0;
        var held = new PrefixSet();
        async Task RunRow(Row original, Action? release)
        {
            _context.Events(RunEvent.Stage(TextKey.StageRows, count: Interlocked.Read(ref completedRows),
                inProgress: true, path: original.Key));
            SyncOutcome outcome;
            try
            {
                outcome = await _items.RunRowAttemptAsync(contexts.WithProof(original),
                    runToken, release, Completed, itemRetries, selection, batch ? null : _context.ScanId);
            }
            catch (LiveAdmissionStaleException error) when (ItemSync.LiveWriteRefused(error))
            {
                _context.Events(RunEvent.Failed(error, path: original.Key));
                Interlocked.CompareExchange(ref _refused, error, null);
                // A refused folder holds its subtree; folder rows run inline, before any child is read.
                if (FolderRow(original)) held.Add(original.Key);
                return;
            }
            if (FolderRow(original))
            {
                Note(original, outcome);
                // A batch folder that did not succeed holds its subtree; folder rows run inline, before any child is read.
                if (batch && !outcome.Succeeded() && !outcome.Deferred) held.Add(original.Key);
            }
            _context.Events(RunEvent.Stage(TextKey.StageRows, count: Interlocked.Increment(ref completedRows),
                inProgress: true, path: original.Key));
        }
        Stage(TextKey.StageRows);
        long rows = 0;
        foreach (var row in store.ScanRows(pair.PairId, _context.ScanId, contexts.For))
        {
            if (runToken.IsCancellationRequested) break;
            if (batch && held.Covers(row.Key)) continue;
            if (batch && !_ancestors.BatchAdmits(row))
            {
                Completed(row, new SyncOutcome(ActionKind.None, NoneReason.OutOfScope, null, null, false));
                continue;
            }
            rows++;
            if (FolderRow(row)) await RunRow(row, null);
            else
            {
                tasks.Add(release => RunRow(row, release));
                await tasks.WaitForCapacityAsync(() => RowCapacity(_context.Admission, _ports.LocalHashSlots), callerToken, TotalCapacity(_context.Admission, _ports.LocalHashSlots));
            }
        }
        _context.Events(RunEvent.Stage(TextKey.StageRows, count: Interlocked.Read(ref completedRows),
            inProgress: true, total: rows));
        await tasks.WhenAllAsync();
        runToken.ThrowIfCancellationRequested();
        _context.Events(RunEvent.Stage(TextKey.StageRows, ended: true, count: rows));
        _completedFolders = 0;
        Stage(TextKey.StageFolderPlans);
        var continued = batch ? new HashSet<string>(StringComparer.Ordinal) :
            await ContinueFolderFollowUpsAsync(earlier, Completed, unresolved, runToken);
        _context.Events(RunEvent.Stage(TextKey.StageFolderPlans, count: _completedFolders,
            inProgress: true, total: folderPlans.Count + firstFolders.Count + continued.Count));
        var restoredAncestors = new HashSet<string>(restored.SelectMany(PathNames.Ancestors), PathNames.Comparer);
        await RunFolderPlansAsync(folderPlans, continued, restored, restoredAncestors, Completed, unresolved, runToken);
        if (firstFolders.Count != 0 && (!batch || !_context.BatchIncomplete && _context.BatchUnlisted is null))
            await _firstRunFolders.FinalizeFoldersAsync(firstFolders, pending, unresolved, tasks, Completed,
                () => RowCapacity(_context.Admission, _ports.LocalHashSlots), FolderProgress, runToken, callerToken);
        runToken.ThrowIfCancellationRequested();
        _context.Events(RunEvent.Stage(TextKey.StageFolderPlans, ended: true,
            count: folderPlans.Count + firstFolders.Count + continued.Count));
    }

    private sealed class RenameExecution(RenameCandidate[] candidates)
    {
        internal RenameCandidate[] Candidates { get; } = candidates;
        internal Task? Task { get; set; }
        internal RenameExecution? Prerequisite { get; set; }
    }

    private void EnrollUnreadable(ListedSkips scan, RowContexts contexts, bool firstRun)
    {
        var retained = _context.Store.RetainedFirstRun(_context.Pair.PairId);
        foreach (var (_, key) in scan.LocalFolders)
        {
            var entry = _context.Store.ScanEntry(_context.Pair.PairId, _context.ScanId, Side.Local, key);
            if (entry is not { IsFolder: true } || !IssuePolicy.ProtectsSubtree(entry.Skip)) continue;
            var row = Row.ForOneSide(Side.Local, key, entry, null, [], null, RowContext.Neutral);
            var kind = IssuePolicy.FirstRunKind(row, retained, false);
            var under = contexts.UnderRetainedSubtree(retained, key);
            if (!firstRun && kind == IssueSubject.FirstRunDeletionKind.None && !under) continue;
            if (kind != IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
                kind = IssueSubject.FirstRunDeletionKind.UnscannedSubtree;
            var cause = Enum.Parse<TextKey>(entry.Skip!);
            _context.Store.Commit(_context.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, cause, entry.Path,
                    new IssueSubject([new IssueItem(Side.Local, entry.Path, entry.Id)], null, FirstRunDeletion: kind))]
            });
        }
    }

    // A survivor an earlier run left waiting is reached again here: each Repair, until its hold clears (3.8). The
    // pass's own operations finished where they started. Returns the operations this stage already finished.
    private async Task<HashSet<string>> ContinueFolderFollowUpsAsync(IReadOnlySet<string> earlier,
        Action<Row, SyncOutcome> completed, HashSet<string> unresolved, CancellationToken ct)
    {
        var continued = new HashSet<string>(StringComparer.Ordinal);
        var active = _context.Store.ActiveFolderRemovals(_context.Pair.PairId, ct)
            .Where(operation => earlier.Contains(operation.OperationId)).ToArray();
        foreach (var operation in active)
        {
            FolderProgress(operation.RootEntry.Path);
            var result = await _folders.FinishFolderRemovalAsync(operation.OperationId, null, ct, completed);
            FolderProgress(operation.RootEntry.Path, completed: true);
            continued.Add(operation.OperationId);
            if (result.Status != FolderRemovalStatus.Complete)
                unresolved.Add(PathNames.KeyFor(operation.DeletingSide, result.RootPath));
        }
        return continued;
    }

    private async Task RunFolderPlansAsync(List<(Row Folder, Side Side, int Depth)> plans,
        IReadOnlySet<string> continued, IReadOnlySet<string> restored, IReadOnlySet<string> restoredAncestors, Action<Row, SyncOutcome> completed,
        HashSet<string> unresolved, CancellationToken ct)
    {
        foreach (var level in plans.GroupBy(plan => plan.Depth).OrderByDescending(level => level.Key))
        {
            var operations = new List<(Row Folder, string Id)>();
            foreach (var (folder, side, _) in level)
            {
                FolderProgress(folder.Key);
                var id = await _folders.PrepareFolderRemovalAsync(folder, side, restored, restoredAncestors,
                    ct, completed);
                // The folder owner answers with an unfinished operation over this root; the stage above already ran it.
                if (id is not null && !continued.Contains(id)) operations.Add((folder, id));
                else FolderProgress(folder.Key, completed: true);
            }
            await _folders.RunPreparedFolderRemovalsAsync(operations.Select(operation => operation.Id), ct);
            foreach (var (folder, id) in operations)
            {
                FolderProgress(folder.Key);
                var result = await _folders.FinishFolderRemovalAsync(id, null, ct, completed);
                FolderProgress(folder.Key, completed: true);
                foreach (var key in Row.Keys(folder))
                {
                    if (result.Status == FolderRemovalStatus.Complete) unresolved.Remove(key);
                    else unresolved.Add(key);
                }
            }
        }
    }

    private IReadOnlyDictionary<string, IssueSubject>? ReconcileIssues(PrefixSet localUntrusted,
        IReadOnlyList<(string Path, TextKey Cause, IssueSubject Subject)> dropboxSkipped,
        PrefixSet appliedExclusions, PrefixSet pendingExclusions)
    {
        var store = _context.Store;
        var pair = _context.Pair;
        var remoteUntrusted = new PrefixSet();
        foreach (var item in dropboxSkipped) remoteUntrusted.Add(PathNames.Key(item.Path));
        var pendingPath = store.Pending(pair.PairId) switch
        {
            CurrentPending current => current.Value.Path,
            LegacyPending legacy => legacy.Path,
            NoPending => null,
            _ => throw new InvalidDataException()
        };
        var entries = new Dictionary<(Side Side, string Path), Entry?>();
        Dictionary<string, IssueSubject>? proofs = null;
        Entry? Scanned(Side side, string path)
        {
            if (!entries.TryGetValue((side, path), out var entry))
                entries.Add((side, path), entry = store.ScanEntry(pair.PairId, _context.ScanId, side, PathNames.KeyFor(side, path)));
            return entry;
        }
        foreach (var issue in store.Issues(pair.PairId, IssueClosure.AfterHeldInSync))
        {
            _context.Events(RunEvent.Stage(TextKey.StageAfterListing, inProgress: true, path: issue.Path));
            var protectedByOwner = store.RetryNoticeProtected(pair.PairId, issue.Id);
            if (issue.Kind == IssueKind.Failure && pendingPath is not null && PathNames.Same(issue.Path, pendingPath))
                protectedByOwner = true;
            if (IssuePolicy.Obsolete(issue, Scanned, localUntrusted, remoteUntrusted, appliedExclusions,
                pendingExclusions, _context.Scope, protectedByOwner))
            {
                store.CloseScanIssue(pair.PairId, _context.ScanId, issue);
                continue;
            }
            if (issue.Kind is not (IssueKind.Tie or IssueKind.NameClash) || issue.Subject.LocalHash is null) continue;
            foreach (var item in issue.Subject.Items)
                if (item.Side == Side.Local && issue.Subject.ReusableLocalHash(Scanned(item.Side, item.Path)) is not null)
                {
                    proofs ??= new Dictionary<string, IssueSubject>(PathNames.Comparer);
                    proofs.TryAdd(PathNames.Key(item.Path), issue.Subject);
                }
        }
        return proofs;
    }

    private void FolderProgress(string path, bool completed = false) =>
        _context.Events(RunEvent.Stage(TextKey.StageFolderPlans,
            count: completed ? Interlocked.Increment(ref _completedFolders) : Interlocked.Read(ref _completedFolders),
            inProgress: true, path: path));

    private static bool FolderRow(Row row) =>
        row.Local?.IsFolder == true || row.Dropbox?.IsFolder == true || row.Synced?.IsFolder == true;
    private void Stage(TextKey key) =>
        _context.Events(RunEvent.Stage(key));
}
