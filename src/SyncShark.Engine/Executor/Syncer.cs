using System.Globalization;

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
        ItemFailures.FailItem(store, pairId, attempts, events is null ? static _ => { } : events.Report,
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
    // DESIGN-B70-TRANSIENT-10 §6.3: selections that ended temporarily this RunWorkAsync, by their full responsibility,
    // and by exact side, key and identity for folder roots and survivors; their work stays durable and is skipped
    // until the next run, so a lane repeat or the end-of-run continuation cannot retry them. Each probe is one hash.
    private readonly HashSet<(Side Side, string Key, string Domain, string? Identity, string? Obligation)> _temporary = [];
    private readonly HashSet<(Side Side, string Key, string? Identity)> _temporaryAt = [];

    // This session's one live copy, so every effect live work reaches meets the live refusal.
    private Syncer Live => _context.Live ? this : _live ??= new Syncer(_context with { Live = true }, _ports);

    /// <summary>Runs pending work on this session's one live copy.</summary>
    internal Task<bool> SyncWorkAsync(CancellationToken ct) => Live.RunWorkAsync(ct);

    private async Task<bool> RunWorkAsync(CancellationToken ct)
    {
        await ReconcileReceiptsAsync(ct).ConfigureAwait(false);
        lock (_temporary)
        {
            _temporary.Clear();
            _temporaryAt.Clear();
        }
        var floor = _context.Store.ReadWorkWatermark();
        // One queue snapshot per pass, taken before the progress lock, so the count never runs per row.
        var queued = _context.Store.CountWork(_context.Pair.PairId, _context.ExpectedGeneration, floor);
        _context.Events(RunEvent.Snapshot(TextKey.AutoSyncQueueAtStart, Text.Get(TextKey.AutoSyncQueueAtStart,
            (TextArg.Count, (queued.Local + queued.Dropbox).ToString(CultureInfo.InvariantCulture)),
            (TextArg.Local, queued.Local.ToString(CultureInfo.InvariantCulture)),
            (TextArg.Dropbox, queued.Dropbox.ToString(CultureInfo.InvariantCulture)))));
        var progressGate = new object();
        long completed = 0;
        void Progress(RowStage row, bool finished)
        {
            lock (progressGate)
            {
                if (finished) completed++;
                _context.Events(RunEvent.Stage(TextKey.StageRows, count: completed, inProgress: true,
                    path: row.Path, side: row.Side, workId: row.WorkId) with { BranchFinished = finished });
            }
        }
        _context.Events(RunEvent.Stage(TextKey.StageRows));
        try
        {
            var moreWork = await RunLanesAsync(floor, Progress, ct).ConfigureAwait(false);
            try { await _folders.ContinueReleasedFollowUpsAsync(ct, TemporaryAt).ConfigureAwait(false); }
            catch (LiveAdmissionStaleException error) // durable and pending; reported, never silent
            {
                _context.Events(RunEvent.Failed(error));
                throw;
            }
            return moreWork;
        }
        finally { _context.Events(RunEvent.Stage(TextKey.StageRows, ended: true, count: completed)); }
    }

    // The finite watermark and pass retirement govern the same lane schedule, including its contention fallback.
    private async Task<bool> RunLanesAsync(long floor, Action<RowStage, bool> progress, CancellationToken ct)
    {
        var pairId = _context.Pair.PairId;
        RowContexts? rows = null;
        SelectionPass CreatePass() => new(_context, _items, _ancestors, _ports.Live, _executor,
            (rows ??= _items.Rows).ForConcurrentSelection(), progress, _ports.RunBatchAsync, RecordTemporary,
            TemporaryThisRun);
        var serial = false;
        bool repeat;
        var moreWork = false;
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
                            _context.Events(RunEvent.Failed(error, path: selection.Key)
                                with { Side = selection.Side, WorkId = selection.AnchorWorkId });
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
            // A contended round keeps its one serial fallback (competed ignores the floor); a retirement opens another
            // pass only with floor progress, so continuous churn cannot hold one pass open. When the loop ends having
            // retired or released only post-floor work (no floor progress), the caller re-arms one wake for it.
            var competed = !serial && lanes > 1 && Volatile.Read(ref refused) != 0;
            repeat = competed || (retired && Volatile.Read(ref progress0) > 0);
            serial = competed && Volatile.Read(ref progress0) == 0;
            moreWork = false;
        } while (repeat);
        return moreWork;
    }

    // Reason: one traversal state machine; the pending stack, visited set and stale-run boundary share one selection
    // lifetime, so splitting would only pass that state between methods.
    private async Task SyncSelectionAsync(SelectionPass pass, WorkSelection selection, CancellationToken ct,
        Action? release, Action refused, Action<long> retired)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();
        var current = string.Empty;
        var currentSide = selection.Side;
        var currentWorkId = selection.AnchorWorkId;
        try
        {
            selection.RequireFor(_context.Pair.PairId, _context.ExpectedGeneration);
            var pending = new Stack<(WorkSelection Work, bool RenameDependency)>();
            var visited = new HashSet<long> { selection.AnchorWorkId };
            pending.Push((selection, false));
            while (pending.TryPeek(out var frame))
            {
                ct.ThrowIfCancellationRequested();
                var next = frame.Work;
                current = next.Key;
                currentSide = next.Side;
                currentWorkId = next.AnchorWorkId;
                // B-146: this frame owns no branch yet, so no earlier selection's row may answer for it.
                pass.RefusedRow = null;
                if (_ancestors.PendingAncestor(next, _parents) is { } found)
                {
                    if (_context.Store.WorkAttempted(found.Ancestor)) return;
                    if (TemporaryThisRun(found.Ancestor))
                    {
                        RetainTemporary(pending);
                        return;
                    }
                    if (!visited.Add(found.Ancestor.AnchorWorkId))
                        throw AncestorFence.Stale(found.Ancestor, found.Side, pending.Select(item => item.Work));
                    pending.Push((found.Ancestor, false));
                    continue;
                }
                if (TemporaryThisRun(next))
                {
                    RetainTemporary(pending);
                    return;
                }
                (bool? Completed, WorkSelection? Prerequisite) result;
                try
                {
                    // B-133: wholly excluded work completes in storage, not by an observation that only settles it
                    // out of scope; a stale completion takes the same dependent cleanup as a stale run.
                    if (_context.Store.CompleteOutOfScope(next, _context.Scope)) result = (true, null);
                    else
                    {
                        result = await pass.RunAsync(next, ct, release).ConfigureAwait(false);
                        // The frame carries the begun selection, whose key is the one a temporary outcome recorded.
                        if (pass.Begun is { } begun)
                        {
                            pending.Pop();
                            pending.Push((next = begun, frame.RenameDependency));
                        }
                    }
                }
                catch (LiveAdmissionStaleException error) when (frame.RenameDependency)
                {
                    pending.Pop();
                    var held = ItemSync.LiveWriteRefused(error);
                    var retry = true;
                    try { retry = ArrivedAfterProgress(next, error.Reason); }
                    finally
                    {
                        // B-140: a held Local write is not a prerequisite clash; it ends the pass, so the dependents'
                        // selection is closed the same way and their work stays pending for the owed Repair.
                        if (retry || held)
                            foreach (var dependent in pending) _context.Store.EndSelection(dependent.Work);
                    }
                    // The outer catch reports the held path; only a prerequisite clash is refused here.
                    if (retry || held) throw;
                    foreach (var dependent in pending) pass.RenameWork.RefusePrerequisite(dependent.Work);
                    return;
                }
                if (result.Prerequisite is { } prerequisite)
                {
                    if (!visited.Add(prerequisite.AnchorWorkId))
                    {
                        foreach (var dependent in pending) pass.RenameWork.RefusePrerequisite(dependent.Work);
                        return;
                    }
                    pending.Push((prerequisite, true));
                    continue;
                }
                if (result.Completed is null)
                {
                    // B-147: already attempted this pass is an unavailable prerequisite, not an established clash, so
                    // its dependents close and stay pending beside its retained row and wait for that row's next real
                    // wake (no pass synthesizes one); a held one's is the owed Repair's completion wake. Only
                    // Completed false is a real clash.
                    if (!frame.RenameDependency)
                    {
                        refused();
                        return;
                    }
                    pending.Pop();
                    foreach (var dependent in pending) _context.Store.EndSelection(dependent.Work);
                    return;
                }
                if (result.Completed is false)
                {
                    // A prerequisite or ancestor that just ended temporarily is neither a clash nor a stale refusal;
                    // a branch the selection itself refused still counts.
                    if (TemporaryThisRun(next))
                    {
                        RetainTemporary(pending);
                        if (pass.Refused) refused();
                        return;
                    }
                    if (!frame.RenameDependency)
                    {
                        if (pending.Count > 1) throw AncestorFence.Stale(next, next.Side, pending.Select(item => item.Work));
                        if (pass.Refused) refused();
                        return;
                    }
                    pending.Pop();
                    foreach (var dependent in pending) pass.RenameWork.RefusePrerequisite(dependent.Work);
                    return;
                }
                retired(next.AnchorWorkId);
                pending.Pop();
            }
        }
        catch (LiveAdmissionStaleException error)
        {
            // B-146: report the actual attempted subject from the branch boundary; the selection frame is only the
            // fallback for a failure before any subject exists (an unacquirable or already-attempted selection).
            var row = pass.RefusedRow;
            var path = row?.Path ?? current;
            var side = row?.Side ?? currentSide;
            var workId = row?.WorkId ?? currentWorkId;
            // B-140: a refused Local write waits for the owed Repair and reports the branch it refused, never a notice.
            if (ItemSync.LiveWriteRefused(error))
            {
                // Each pass reports a refused path at most once: the refused selection never re-runs in the pass that
                // refused it, and a live batch child already reported its own refusal.
                if (!error.Reported)
                    _context.Events(RunEvent.Failed(error, TextKey.LocalWriteHeld, path: path)
                        with { Side = side, WorkId = workId });
                return;
            }
            // A refused opportunity keeps its work pending and reports why without creating a notice.
            refused();
            _context.Events(RunEvent.Failed(error, path: path) with { Side = side, WorkId = workId });
            return;
        }
    }

    // The selection's own owner fields: same-identity work at another path (a hard link) or under another Storage
    // obligation is other work; a new row with the same canonical obligation is the same responsibility.
    private static (Side, string, string, string?, string?) Responsibility(WorkSelection work) =>
        (work.Side, work.Key, work.Domain, work.Identity, work.Obligation);

    private void RecordTemporary(WorkSelection work)
    {
        lock (_temporary)
        {
            _temporary.Add(Responsibility(work));
            _temporaryAt.Add((work.Side, work.Key, work.Identity));
        }
    }

    // A skipped selection is neither a refusal nor a retirement, so it cannot cause a lane repeat (§6.3).
    private bool TemporaryThisRun(WorkSelection work)
    {
        if (_context.ItemAttempts.HeldForRetry(work.Side, work.Key)) return true;
        lock (_temporary)
            return _temporary.Contains(Responsibility(work));
    }

    // A folder root or survivor carries no selection: any selection that ended temporarily at its exact key on that
    // side, path-only or naming its identity there, owns it for this run: at most two hash probes.
    private bool TemporaryAt(Side side, string key, string? identity)
    {
        if (_context.ItemAttempts.HeldForRetry(side, key)) return true;
        lock (_temporary)
            return _temporaryAt.Contains((side, key, identity)) || _temporaryAt.Contains((side, key, null));
    }

    // Every selection waiting on a temporary one stays pending, quietly, for the run after this one: none is refused
    // as a prerequisite clash, and each one's suspension is ended.
    private void RetainTemporary(IEnumerable<(WorkSelection Work, bool RenameDependency)> pending)
    {
        foreach (var (work, _) in pending)
        {
            RecordTemporary(work);
            _context.Store.EndSelection(work);
        }
    }

    // Residual WorkId may outpace its Acquisition, but each registration already has a visible issue.
    // shortcut: deleted-only old-key echoes still clash, widen only with a reviewed arrival contract.
    private bool ArrivedAfterProgress(WorkSelection next, LiveAdmissionStaleReason reason) =>
        reason == LiveAdmissionStaleReason.RelevantEvidenceChanged && next.Identity is { } id &&
        _context.Store.SyncedById(next.PairId, next.Side, id) is { } moved && moved.KeyOn(next.Side) == next.Key &&
        new[] { Side.Local, Side.Dropbox }.Any(side => _context.Store.LaterAncestorNews(next.PairId,
            next.PairGeneration, side, [moved.KeyOn(side)], next.ThroughWorkId).News != AncestorNews.None);

    internal async Task<bool> ReconcileReceiptsAsync(CancellationToken ct, bool progressOnly = false)
    {
        var finished = false;
        await new Recovery(_context, _ports.Recovery, _folderDelete).ReconcileReceiptsAsync(ct, progressOnly,
            // Receipt settlement keeps recovery authority; the survivors it then reaches are new live effects (R6-2).
            progressOnly ? async (operationId, token) =>
            {
                await Live._folders.FinishFolderRemovalAsync(operationId, null, token);
                finished = true;
            } : null);
        return finished;
    }

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
