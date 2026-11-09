using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retains a finite selection until its admitted branches and exact residual registrations settle.</summary>
internal sealed class SelectionPass
{
    private readonly ExecutorContext _context;
    private readonly ItemSync _items;
    private readonly ActionExecutor _executor;
    private readonly AncestorFence _ancestors;
    private readonly ILiveExecutionBoundary _boundary;
    private readonly RowContexts _rows;
    private readonly Observer _observer;
    private readonly Parents _parents;
    private readonly Dictionary<string, (Row? Held, SyncedEntry? Before, Issue? Holder)> _visited = new(StringComparer.Ordinal);
    private readonly Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? _runBatch;
    // Roots this selection already batched; a folder notice under one is entry-only.
    private readonly PrefixSet _batched = new();
    // D-1105-1: folder batches start after the pair's first Repair, which cannot complete during a selection.
    private bool _batches;
    // The selection's counterpart peer once its root is observed; its batch lists that side too.
    private SelectionPeer? _peer;

    internal SelectionPass(ExecutorContext context, ItemSync items, AncestorFence ancestors, ILiveExecutionBoundary liveBoundary,
        ActionExecutor executor, RowContexts rows, Action<string, bool> progress,
        Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? runBatch = null)
    {
        _runBatch = runBatch;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _ancestors = ancestors;
        _executor = executor;
        _boundary = liveBoundary ?? throw new ArgumentNullException(nameof(liveBoundary));
        _rows = rows;
        _progress = progress;
        _parents = new Parents(context);
        _observer = new Observer(context, _parents);
    }

    private readonly Action<string, bool> _progress;
    private Action? _release;
    private string? _activeBranch;
    private WorkSelection? _prerequisite;
    internal Func<SyncedEntry, long, bool>? LocalEcho { get; set; }

    internal string? ActiveBranch { get => _activeBranch; set => _activeBranch = value; }
    internal Action<string, bool> Progress => _progress;
    internal Dictionary<string, (Row? Held, SyncedEntry? Before, Issue? Holder)> Visited => _visited;
    internal bool Batches => _batches;
    internal PrefixSet Batched => _batched;
    internal SelectionPeer? Peer => _peer;
    internal Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? RunBatch => _runBatch;

    internal bool Refused { get; private set; }
    internal WorkSelection? Prerequisite { get => _prerequisite; set => _prerequisite = value; }
    internal SelectionRenames RenameWork => new(this, _context, _items, _parents, _rows, _ancestors, _boundary);

    internal async Task<(bool? Completed, WorkSelection? Prerequisite)> RunAsync(WorkSelection selection, CancellationToken ct, Action? release = null)
    {
        _items.SelectRows(_rows);
        _release = release;
        _prerequisite = null;
        _visited.Clear();
        _batched.Clear();
        _peer = null;
        _batches = SyncPorts.AdmitsBatch(_context.Store, selection.PairId);
        _rows.ForSelection();
        if (!_context.Store.BeginSelection(selection)) return (null, null);
        Refused = false;
        _ancestors.RunSelection(selection, () => Refused = true);
        try { return (await RunSelectionAsync(selection, ct).ConfigureAwait(false), _prerequisite); }
        catch (ChangedUnderneathException)
        {
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        }
        finally
        {
            try
            {
                if (_activeBranch is { } path) _progress(path, true);
            }
            finally
            {
                _activeBranch = null;
                _context.Store.EndSelection(selection, _prerequisite);
                _ancestors.RunSelection(null);
            }
        }
    }

    // Branch sequencing shares one peer, selected scope and readiness answer until the selection settles.
    private async Task<bool> RunSelectionAsync(WorkSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.PairId != _context.Pair.PairId)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var ready = true;
        var found = false;
        SelectionPeer? peer = null;
        LiveEffectScope? selectedScope = null;
        Func<Side, string, string?, bool>? separateWork = null;
        LocalEcho = null;
        var observation = new SelectionObservation(this, _context, _observer, _parents, _rows, _executor);
        await foreach (var (work, walkFailure, original, initialPrimary) in new SelectionBranches(this, _context).ReadBranchesAsync(selection, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            found = true;
            if (work.PairId != selection.PairId || work.PairGeneration != selection.PairGeneration ||
                work.SourceEpoch != selection.SourceEpoch)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var notice = work.Notice;
            var key = PathNames.KeyFor(notice.Side, notice.Path);
            if (original is not null) separateWork = null;
            if (original is null && walkFailure is null &&
                (separateWork ??= _context.Store.CreateSeparateWorkProbe(selection))(notice.Side, key, notice.Id)) continue;
            selectedScope ??= LockKeys.ForLivePaths(_context.Pair, [(selection.Side, (original ?? notice).Path)], false).Single();
            if (original is not null)
            {
                if (_visited.TryGetValue(key, out var prior))
                {
                    if (prior.Held is not null)
                        ready &= AttachHeld(selection, original, prior.Held, prior.Before, prior.Holder);
                    continue;
                }
                _visited.Add(key, default);
            }
            var throughPeer = peer is null && key == selection.Key
                ? _context.Store.ReadWorkWatermark() : (long?)null;
            var branch = new SelectionBranch(this, _parents, selection, work, original, key, peer,
                throughPeer, separateWork, initialPrimary);
            // A walked child or old path refuses from its notice alone, before any provider call; the enumerator
            // already refused the selection's own primary branch.
            if (walkFailure is null && key != selection.Key) RefuseBeforeProvider(notice, work.Before);
            var row = await observation.ReadAsync(branch, walkFailure, branch.ClaimPeer, ct).ConfigureAwait(false);
            peer = branch.Peer;
            if (branch.Failed) ready &= branch.Attached;
            if (row is null) continue;
            var observed = row.EntryOn(notice.Side);
            var candidate = Renames.Candidate(notice.Side, observed,
                observed is null ? null : _context.Store.SyncedById(selection.PairId, notice.Side, observed.Id));
            if (candidate is not null)
            {
                var rename = await RenameWork.RunAsync(selection, notice, original, row, candidate,
                    selectedScope, peer, ct).ConfigureAwait(false);
                if (!rename.Renamed)
                {
                    if (_prerequisite is not null) return false;
                    ready &= rename.Ready;
                    continue;
                }
                row = await observation.ReadAsync(branch, null, branch.RefreshBefore, ct).ConfigureAwait(false);
                if (branch.Failed) ready &= branch.Attached;
                if (row is null) continue;
            }
            var dispatched = await new SelectionExecution(this, _context, _items, _executor, _boundary).RunAsync(
                selection, notice, original, key, row, branch.Before, peer, selectedScope, branch.Evidence,
                branch.Retain, _release, ct).ConfigureAwait(false);
            ready &= dispatched.Ready || dispatched.MayRetain && branch.Attached;
            if (dispatched.DiscardPeer) peer = null;
        }
        if (!found) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (!ready) return false;
        return _context.Store.CompleteWork(selection, peer);
    }

    internal void ClaimPeer(WorkSelection selection, SelectionPeer peer)
    {
        _peer = peer;
        _context.Store.ClaimSelectionPeer(selection, peer);
        _ancestors.RunSelection(selection with { Peer = peer });
    }

    internal void RememberHeld(ChangeNotice? original, string key, Row held, SyncedEntry? saved, Issue? holder)
    {
        if (original is not null) _visited[key] = (held, saved, holder);
    }

    internal bool RetainIssue(WorkSelection selection, Row row, ChangeNotice notice, SyncedEntry? before,
        Issue issue, Action<Issue> remember)
    {
        if (!_context.Store.AttachIssueWork(selection, issue,
            [(notice, row.IsFolder(), before)])) return false;
        remember(issue);
        return true;
    }

    // A holder that changed or closed since the lookup refuses exactly; the branch stays unfinished and reachable.
    internal bool AttachHeld(WorkSelection selection, ChangeNotice notice, Row row, SyncedEntry? before, Issue? holder)
    {
        if (holder is null) return false;
        try
        {
            return _context.Store.AttachIssueWork(selection, holder,
                [(notice, row.IsFolder(), before)]);
        }
        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged)
        {
            return false;
        }
    }

    // A held branch never reaches the fence: it attaches to its holder, so refusing it early would replace that.
    // The notice's own side and path are the evidence here: an unobserved notice still names the path it is about.
    internal void RefuseBeforeProvider(ChangeNotice notice, SyncedEntry? before)
    {
        var path = notice.Observed?.Path ?? notice.Path;
        if (_rows.Covers(PathNames.KeyFor(notice.Side, path))) return;
        _ancestors.RequireClear(notice, before, _parents);
    }


}
