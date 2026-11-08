namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The Executor's Run-facing facade: the eight workflow entries over the composed item, folder, first-run and ancestor owners, with no Row, context, action, delegate or lease crossing this boundary.</summary>
public sealed class Syncer
{
    private readonly ExecutorContext _context;
    private readonly Parents _parents;
    private readonly KeepBothRunner _keepBoth;
    private readonly ActionExecutor _executor;
    private readonly ExclusionRunner _exclusions;
    private readonly FolderDeleteRunner _folderDelete;
    private readonly SyncPorts _ports;
    private readonly FirstRunFolders _firstRunFolders;
    private readonly FolderSync _folders;
    private readonly ItemSync _items;
    private readonly AncestorFence _ancestors;
    internal Syncer(ExecutorContext context, SyncPorts ports)
    {
        ArgumentNullException.ThrowIfNull(context.Opportunity);
        _context = context;
        _ports = ports;
        _parents = new Parents(context);
        var transfers = new Transfers(context);
        _keepBoth = new KeepBothRunner(context, transfers);
        _executor = new ActionExecutor(context, transfers, _parents, _keepBoth);
        _folderDelete = new FolderDeleteRunner(context, _executor, ports.Recovery);
        _ancestors = new AncestorFence(context);
        _items = new ItemSync(context, _parents, _executor, _keepBoth, _folderDelete, ports, _ancestors);
        _firstRunFolders = new FirstRunFolders(context, _parents, _executor, _items);
        _folders = _items.Folders;
        _exclusions = new ExclusionRunner(context, _parents, _executor, _keepBoth, _items);
    }

    internal static void FailItem(IStateStore store, string pairId, IItemAttempts attempts,
        IProgress<RunEvent>? events, Guid attempt, ItemRetryCandidate? expected, long epoch, string path,
        IssueSubject subject, TextKey cause, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ActionExecutor.FailItem(store, pairId, attempts, events is null ? static _ => { } : events.Report,
            attempt, expected, epoch, path, subject, cause);
    }

    internal Task SyncScannedAsync(Guid scanToken, ListedSkips listed,
        IReadOnlySet<long>? retryIssues, CancellationToken ct, WorkSelection? selection = null) =>
        new Syncer(_context with { ScanId = scanToken }, _ports).RunScanAsync(scanToken, listed, retryIssues, ct, selection);

    private Task RunScanAsync(Guid scanToken, ListedSkips listed,
        IReadOnlySet<long>? retryIssues, CancellationToken ct, WorkSelection? selection) =>
        selection is null
            ? new ScanPass(_context, _items, _folders, _firstRunFolders, _exclusions, _ancestors, _ports).RunAsync(scanToken, listed, retryIssues, ct)
            : SyncBatchAsync(scanToken, selection, listed, retryIssues, ct);

    private async Task SyncBatchAsync(Guid token, WorkSelection selection, ListedSkips listed,
        IReadOnlySet<long>? retryIssues, CancellationToken ct)
    {
        _ancestors.RunSelection(selection);
        try
        {
            await new ScanPass(_context, _items, _folders, _firstRunFolders, _exclusions, _ancestors, _ports)
                .RunAsync(token, listed, retryIssues, ct, selection).ConfigureAwait(false);
        }
        finally { _ancestors.RunSelection(null); }
    }

    private Syncer? _live;

    // This session's one live copy, so every effect live work reaches meets the live refusal.
    private Syncer Live => _context.Live ? this : _live ??= new Syncer(_context with { Live = true }, _ports);

    /// <summary>Runs pending work on this session's one live copy.</summary>
    internal Task SyncWorkAsync(CancellationToken ct) => Live.RunWorkAsync(ct);

    private async Task RunWorkAsync(CancellationToken ct)
    {
        await ReconcileReceiptsAsync(ct).ConfigureAwait(false);
        var floor = _context.Store.ReadWorkWatermark();
        var progressGate = new object();
        long completed = 0;
        void Progress(string path, bool finished)
        {
            lock (progressGate)
            {
                if (finished) completed++;
                _context.Events(RunEvent.Stage(TextKey.StageRows, count: completed, inProgress: true, path: path) with
                { BranchFinished = finished });
            }
        }
        _context.Events(RunEvent.Stage(TextKey.StageRows));
        try
        {
            await RunLanesAsync(floor, Progress, ct).ConfigureAwait(false);
            try { await _folders.ContinueReleasedFollowUpsAsync(ct).ConfigureAwait(false); }
            catch (LiveAdmissionStaleException error)
            {
                // A refused follow-up stays durable and pending; the refusal is reported, never silent.
                _context.Events(RunEvent.Failed(error));
                throw;
            }
        }
        finally { _context.Events(RunEvent.Stage(TextKey.StageRows, ended: true, count: completed)); }
    }

    // The finite watermark and pass retirement govern the same lane schedule, including its contention fallback.
    private async Task RunLanesAsync(long floor, Action<string, bool> progress, CancellationToken ct)
    {
        var pairId = _context.Pair.PairId;
        RowContexts? rows = null;
        SelectionPass CreatePass() => new(_context, _items, _ancestors, _ports.Live, _executor,
            (rows ??= _items.Rows).ForConcurrentSelection(), progress, _ports.RunBatchAsync);
        var serial = false;
        bool repeat;
        do
        {
            var lanes = 0;
            var refused = 0;
            var progress0 = 0;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new TaskSet(cancel);
            Exception? failure = null;
            bool retired;
            async Task RunLaneAsync(SelectionPass pass, WorkSelection selection, Action? release)
            {
                await SyncSelectionAsync(pass, selection, cancel.Token, release,
                    () => Interlocked.Increment(ref refused),
                    anchor => { if (anchor <= floor) Interlocked.Increment(ref progress0); });
            }
            try
            {
                SelectionPass? serialPass = null;
                try
                {
                    while (_context.Store.SelectWork(pairId, _context.ExpectedGeneration, _context.Scope) is { } selection)
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        if (!_ports.Live.TryHoldGeneration(pairId))
                        {
                            var error = new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                            _context.Events(RunEvent.Failed(error, path: selection.Key));
                            throw error;
                        }
                        if (serial)
                            await RunLaneAsync(serialPass ??= CreatePass(), selection, null);
                        else
                        {
                            lanes++;
                            var pass = CreatePass();
                            tasks.Add(release => RunLaneAsync(pass, selection, release));
                            await tasks.WaitForCapacityAsync(() => ScanPass.RowCapacity(_context.Admission, _ports.LocalHashSlots),
                                ct, ScanPass.TotalCapacity(_context.Admission, _ports.LocalHashSlots));
                        }
                    }
                }
                catch (Exception error) { failure = error; }
                failure = await tasks.JoinAsync(failure, failure is not null and not LiveAdmissionStaleException,
                    joined => _context.Events(RunEvent.Failed(joined)));
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            finally { retired = _context.Store.EndWorkPass(pairId); }
            // Retirement or a newly released endpoint justifies another pass; re-registering failure does not.
            var competed = !serial && lanes > 1 && Volatile.Read(ref refused) != 0;
            repeat = competed || retired;
            serial = competed && Volatile.Read(ref progress0) == 0;
        } while (repeat);
    }

    private async Task SyncSelectionAsync(SelectionPass pass, WorkSelection selection, CancellationToken ct,
        Action? release, Action refused, Action<long> retired)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();
        var current = string.Empty;
        try
        {
            selection.RequireFor(_context.Pair.PairId, _context.ExpectedGeneration);
            var pending = new Stack<(WorkSelection Work, bool RenameDependency)>();
            var visited = new HashSet<long> { selection.AnchorWorkId };
            pending.Push((selection, false));
            while (pending.TryPeek(out var frame))
            {
                var next = frame.Work;
                current = next.Key;
                if (PendingAncestor(next) is { } found)
                {
                    if (!visited.Add(found.Ancestor.AnchorWorkId))
                        throw AncestorFence.Stale(found.Ancestor, found.Side, pending.Select(item => item.Work));
                    pending.Push((found.Ancestor, false));
                    continue;
                }
                var result = await pass.RunAsync(next, ct, release).ConfigureAwait(false);
                if (result.Prerequisite is { } prerequisite)
                {
                    if (!visited.Add(prerequisite.AnchorWorkId))
                    {
                        foreach (var dependent in pending) pass.RefusePrerequisite(dependent.Work);
                        return;
                    }
                    pending.Push((prerequisite, true));
                    continue;
                }
                if (!result.Completed)
                {
                    if (!frame.RenameDependency)
                    {
                        if (pending.Count > 1) throw AncestorFence.Stale(next, next.Side, pending.Select(item => item.Work));
                        return;
                    }
                    pending.Pop();
                    foreach (var dependent in pending) pass.RefusePrerequisite(dependent.Work);
                    return;
                }
                retired(next.AnchorWorkId);
                pending.Pop();
            }
        }
        catch (LiveAdmissionStaleException error)
        {
            // A refused opportunity keeps its work pending and reports why without creating a notice.
            if (!ItemSync.LiveWriteRefused(error)) refused();
            _context.Events(RunEvent.Failed(error, path: current));
            return;
        }
    }

    /// <summary>The shallowest pending dependency of this selection on either side, from the same key set the fence
    /// uses: the notice's own path and old path, its saved bindings, the mapped counterpart and the action target.</summary>
    private (WorkSelection Ancestor, Side Side)? PendingAncestor(WorkSelection selection)
    {
        (WorkSelection Ancestor, Side Side)? first = null;
        foreach (var change in _context.Store.ReadWork(selection))
        {
            foreach (var (side, keys) in AncestorFence.NoticeDependencies(change.Notice, change.Before, _parents))
            {
                if (keys.Length == 0) continue;
                foreach (var candidate in _context.Store.PendingAncestors(_context.Pair.PairId,
                    _context.ExpectedGeneration, side, keys))
                    if (candidate.AnchorWorkId != selection.AnchorWorkId &&
                        (first is null || PathNames.Depth(candidate.Key) < PathNames.Depth(first.Value.Ancestor.Key)))
                        first = (candidate, side);
            }
        }
        return first;
    }

    internal Task ReconcileReceiptsAsync(CancellationToken ct, bool progressOnly = false) =>
        new Recovery(_context, _ports.Recovery, _folderDelete).ReconcileReceiptsAsync(ct, progressOnly,
            // Receipt settlement keeps recovery authority; the survivors it then reaches are new live effects (R6-2).
            progressOnly ? (operationId, token) => Live._folders.FinishFolderRemovalAsync(operationId, null, token) : null);

    internal Task<OutstandingWork> SettleOutstandingAsync(CancellationToken ct) =>
        new Recovery(_context, _ports.Recovery, _folderDelete).SettleOutstandingAsync(ct);

    internal Task<RemovalResult> RecoverAsync(RecoveryTarget target, RecoveryChoice choice, CancellationToken ct) =>
        new Recovery(_context, _ports.Recovery, _folderDelete).RecoverAsync(target, choice, ct);

    internal async Task<ResolutionOutcome> ResolveAsync(Issue issue, ResolutionChoice choice, string? newName,
        IReadOnlyList<SyncIgnoreRule>? keepRules, CancellationToken ct)
    {
        var runner = new ResolutionRunner(_context, _executor, _keepBoth, _items, _ports.Selected, _ports.RunBatchAsync);
        var outcome = await runner.ResolveAsync(issue, choice, newName, keepRules, ct);
        return new ResolutionOutcome(outcome, runner.Resolved);
    }

    /// <summary>Previews without performing effects or durable mutations.</summary>
    public Task<ExcludePreview> PreviewExclusionAsync(Exclusion exclusion, CancellationToken ct) =>
        _exclusions.PreviewAsync(exclusion, ct);

}
