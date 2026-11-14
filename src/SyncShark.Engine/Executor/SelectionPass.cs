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
    internal readonly Dictionary<string, (Row? Held, SyncedEntry? Before, Issue? Holder)> Visited = new(StringComparer.Ordinal);
    internal readonly Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? RunBatch;
    internal readonly Action<WorkSelection, long?>? RecordTemporary;
    private readonly Func<WorkSelection, bool>? _temporaryThisRun;
    // Roots this selection already batched; a folder notice under one is entry-only.
    internal readonly PrefixSet Batched = new();
    // D-1105-1: folder batches start after the pair's first Repair, which cannot complete during a selection.
    internal bool Batches { get; private set; }
    // The selection's counterpart peer once its root is observed; its batch lists that side too.
    internal SelectionPeer? Peer { get; private set; }

    internal SelectionPass(ExecutorContext context, ItemSync items, AncestorFence ancestors, ILiveExecutionBoundary liveBoundary,
        ActionExecutor executor, RowContexts rows, Action<RowStage, bool> progress,
        Func<WorkSelection?, Side, string, IReadOnlySet<long>?, bool, CancellationToken, Task>? runBatch = null,
        Action<WorkSelection, long?>? recordTemporary = null, Func<WorkSelection, bool>? temporaryThisRun = null)
    {
        RunBatch = runBatch;
        RecordTemporary = recordTemporary;
        _temporaryThisRun = temporaryThisRun;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _ancestors = ancestors;
        _executor = executor;
        _boundary = liveBoundary ?? throw new ArgumentNullException(nameof(liveBoundary));
        _rows = rows;
        Progress = progress;
        _parents = new Parents(context);
        _observer = new Observer(context, _parents);
    }

    internal readonly Action<RowStage, bool> Progress;
    private Action? _release;
    internal RowStage? ActiveRow;
    // The branch a Local-write refusal names; ActiveRow is already cleared while the refusal unwinds.
    internal RowStage? RefusedRow;
    internal WorkSelection? Prerequisite;
    // The admitted selection, keyed by Storage at its start; the run's temporary record names this one.
    internal WorkSelection? Begun { get; private set; }
    internal Func<SyncedEntry, long, bool>? LocalEcho { get; set; }


    internal bool Refused { get; private set; }
    // Admission asks scope only where its answer is read: folder admission (the batch and walk gates) and the
    // unbound Local settle. Every other primary — a Dropbox-origin file, a bound notice, a delete or a rename —
    // pays no exclusion evaluation, so the in-scope repeat is exactly B-10's accepted Local Changed file.
    internal static bool ExcludedForAdmission(PathScope scope, Side side, Entry current, ChangeNotice notice) =>
        (current.IsFolder || notice is { Side: Side.Local, Kind: ChangeKind.Changed, OldPath: null }) &&
        scope.Excludes(side == Side.Local ? current.Path : null,
            side == Side.Dropbox ? current.Path : null, current.IsFolder);

    internal SelectionRenames RenameWork => new(this, _context, _items, _parents, _rows, _ancestors, _boundary);

    internal async Task<(bool? Completed, WorkSelection? Prerequisite)> RunAsync(WorkSelection selection, CancellationToken ct, Action? release = null)
    {
        _items.SelectRows(_rows);
        _release = release;
        Prerequisite = null;
        Begun = null;
        Visited.Clear();
        Batched.Clear();
        Peer = null;
        Batches = SyncPorts.AdmitsBatch(_context.Store, selection.PairId);
        _rows.ForSelection();
        if (_context.Store.BeginSelection(selection) is not { } begun) return (null, null);
        Begun = selection = begun;
        Refused = false;
        RefusedRow = null;
        _ancestors.RunSelection(selection, () => Refused = true);
        try
        {
            // Admission can refresh the key to one that already ended temporarily in this run; it is not run again.
            if (_temporaryThisRun?.Invoke(selection) == true) return (false, null);
            return (await RunSelectionAsync(selection, ct).ConfigureAwait(false), Prerequisite);
        }
        catch (ChangedUnderneathException)
        {
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        }
        finally
        {
            try
            {
                if (ActiveRow is { } row) Progress(row, true);
            }
            finally
            {
                ActiveRow = null;
                _context.Store.EndSelection(selection, Prerequisite);
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
        await foreach (var (work, walkFailure, original, initialPrimary, primaryExcluded, gone) in new SelectionBranches(this, _context).ReadBranchesAsync(selection, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            found = true;
            if (work.PairId != selection.PairId || work.PairGeneration != selection.PairGeneration ||
                work.SourceEpoch != selection.SourceEpoch)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            // B-179: a branch whose unsaved identity is proven gone has nothing to observe or effect.
            if (gone) continue;
            var notice = work.Notice;
            RefusedRow = new RowStage(notice.Side, work.WorkId, notice.Path);
            var key = PathNames.KeyFor(notice.Side, notice.Path);
            if (original is not null) separateWork = null;
            if (original is null && walkFailure is null &&
                (separateWork ??= _context.Store.CreateSeparateWorkProbe(selection))(notice.Side, key, notice.Id)) continue;
            selectedScope ??= LockKeys.ForLivePaths(_context.Pair, [(selection.Side, (original ?? notice).Path)], false).Single();
            if (original is not null)
            {
                if (Visited.TryGetValue(key, out var prior))
                {
                    if (prior.Held is not null)
                        ready &= AttachHeld(selection, original, prior.Held, prior.Before, prior.Holder);
                    continue;
                }
                Visited.Add(key, default);
            }
            var throughPeer = peer is null && key == selection.Key
                ? _context.Store.ReadWorkWatermark() : (long?)null;
            var branch = new SelectionBranch(this, _parents, selection, work, original, key, peer,
                throughPeer, separateWork, initialPrimary) { Excluded = primaryExcluded };
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
                    if (Prerequisite is not null) return false;
                    ready &= rename.Ready;
                    continue;
                }
                // The scope answer belongs to the first observation of the primary; the moved identity re-observes.
                branch.Excluded = false;
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
        // No branch is owed the settlement's failures: a stale completion or completion read falls back to the frame.
        RefusedRow = null;
        if (!found) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (!ready) return false;
        return _context.Store.CompleteWork(selection, peer);
    }

    internal void ClaimPeer(WorkSelection selection, SelectionPeer peer)
    {
        Peer = peer;
        _context.Store.ClaimSelectionPeer(selection, peer);
        _ancestors.RunSelection(selection with { Peer = peer });
    }

    internal void RememberHeld(ChangeNotice? original, string key, Row held, SyncedEntry? saved, Issue? holder)
    {
        if (original is not null) Visited[key] = (held, saved, holder);
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
        (ChangeNotice, bool, SyncedEntry?) member = (notice, row.IsFolder(), before);
        try
        {
            // A clash that already registers this work current is left alone: Storage owns that answer (B-123).
            if (holder.Kind == IssueKind.NameClash && _context.Store.ClashRefusalState(selection, holder.Path,
                    holder.Subject, member).State == ClashRefusal.Covered) return true;
            return _context.Store.AttachIssueWork(selection, holder, [member]);
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
