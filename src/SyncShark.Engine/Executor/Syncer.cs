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

    internal async Task SyncWorkAsync(CancellationToken ct)
    {
        await ReconcileReceiptsAsync(ct).ConfigureAwait(false);
        var pairId = _context.Pair.PairId;
        SelectionPass? pass = null;
        _context.Events(RunEvent.Stage(TextKey.StageRows));
        bool retired;
        do
        {
            try
            {
                while (_context.Store.SelectWork(pairId, _context.ExpectedGeneration, _context.Scope) is { } selection)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_ports.Live.TryHoldGeneration(pairId))
                    {
                        var error = new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                        _context.Events(RunEvent.Failed(error, path: selection.Key));
                        throw error;
                    }
                    pass ??= new SelectionPass(_context, _items, _ancestors, _ports.Live, _executor, _ports.RunBatchAsync);
                    await SyncSelectionAsync(pass, selection, ct).ConfigureAwait(false);
                }
            }
            finally { retired = _context.Store.EndWorkPass(pairId); }
            // Retirement or a newly released endpoint justifies another pass; re-registering failure does not.
        } while (retired);
        await _folders.ContinueReleasedFollowUpsAsync(ct).ConfigureAwait(false);
        _context.Events(RunEvent.Stage(TextKey.StageRows, ended: true, count: pass?.Completed ?? 0));
    }

    private async Task SyncSelectionAsync(SelectionPass pass, WorkSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();
        var current = string.Empty;
        try
        {
            selection.RequireFor(_context.Pair.PairId, _context.ExpectedGeneration);
            var pending = new Stack<WorkSelection>();
            var visited = new HashSet<long> { selection.AnchorWorkId };
            pending.Push(selection);
            while (pending.TryPeek(out var next))
            {
                current = next.Key;
                if (PendingAncestor(next) is { } found)
                {
                    if (!visited.Add(found.Ancestor.AnchorWorkId))
                        throw AncestorFence.Stale(found.Ancestor, found.Side, pending);
                    pending.Push(found.Ancestor);
                    continue;
                }
                if (!await pass.RunAsync(next, ct).ConfigureAwait(false))
                {
                    if (pending.Count > 1) throw AncestorFence.Stale(next, next.Side, pending);
                    return;
                }
                pending.Pop();
            }
        }
        catch (LiveAdmissionStaleException error)
        {
            // A refused opportunity keeps its work pending and reports why without creating a notice.
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
            progressOnly ? (operationId, token) => _folders.FinishFolderRemovalAsync(operationId, null, token) : null);

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
