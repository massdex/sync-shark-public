using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Runs selected renames and retains their continuation or prerequisite ownership.</summary>
internal readonly struct SelectionRenames(SelectionPass _pass, ExecutorContext _context, ItemSync _items,
    Parents _parents, RowContexts _rows, AncestorFence _ancestors, ILiveExecutionBoundary _boundary)
{
    internal async Task<(bool Renamed, bool Ready)> RunAsync(WorkSelection selection,
        ChangeNotice notice, ChangeNotice? original, Row row, RenameCandidate candidate,
        LiveEffectScope selectedScope, SelectionPeer? peer, CancellationToken ct)
    {
        var pass = _pass;
        var items = _items;
        var observed = candidate.Moved;
        var savedIdentity = candidate.Synced;
        var held = row with { Synced = savedIdentity };
        void Remember(Issue? issue)
        {
            if (original is null) return;
            var retained = (held, savedIdentity, issue);
            pass.Visited[PathNames.KeyFor(notice.Side, notice.Path)] = retained;
            pass.Visited[PathNames.KeyFor(notice.Side, notice.OldPath ?? savedIdentity.PathOn(notice.Side))] = retained;
        }
        var renamer = new RenameRunner(_context, _parents, _rows, _ancestors);
        renamer.SelectClash(selection);
        if (renamer.HoldBeforeAdmission([candidate], out var holder))
        {
            Remember(holder);
            return (false, _pass.AttachHeld(selection, notice, held, savedIdentity, holder));
        }
        if (HoldPrerequisite(selection, notice, original, candidate)) return (false, false);
        var footprint = LockKeys.ForSelectedBranch(LockKeys.ForRenamePaths(_context.Pair,
            renamer.Footprint([candidate], notice.Side == Side.Local ? observed : null), observed.IsFolder),
            PathNames.KeyFor(notice.Side, notice.Path), selection, selectedScope);
        var coverage = (await _boundary.CaptureCoverageAsync(selection, footprint, ct).ConfigureAwait(false)) with { Peer = peer };
        var retained = false;
        var decision = await _boundary.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
            (execution, token) => items.Renames.RenameAdmittedAsync(execution,
                original is null ? null : selection with { Peer = peer },
                selection, [candidate], notice.Side == Side.Local ? observed : null,
                token, issue => retained |= pass.RetainIssue(selection, held, notice, savedIdentity, issue, Remember)),
            ct).ConfigureAwait(false);
        return await FinishRenameAsync(selection, notice, original is not null, held, candidate, peer,
            decision, retained, Remember, ct).ConfigureAwait(false);
    }

    private bool HoldPrerequisite(WorkSelection selection, ChangeNotice notice, ChangeNotice? original,
        RenameCandidate candidate)
    {
        if (original is null || PathNames.KeyFor(notice.Side, notice.Path) != selection.Key ||
            candidate.Moved.Id != selection.Identity) return false;
        var owners = _context.Store.ReconcileRenameContinuation(selection, candidate.Synced);
        if (owners.Count <= 1) return false;
        RefusePrerequisite(selection, owners.Select(owner => owner.Binding));
        _rows.HoldRename([candidate]);
        return true;
    }

    private async Task<(bool Renamed, bool Ready)> FinishRenameAsync(WorkSelection selection, ChangeNotice notice,
        bool hasOriginal, Row held, RenameCandidate candidate, SelectionPeer? peer, RenameDecision decision,
        bool retained, Action<Issue?> remember, CancellationToken ct)
    {
        if (decision is { Kind: RenameKind.Vacate, Destination: { } vacated })
        {
            if (!hasOriginal) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var selected = selection with { Peer = peer };
            if (!await _items.Renames.RemoveVacatedAsync(candidate, vacated.Binding, selected, ct))
                RefusePrerequisite(selection);
            // Removal evidence reaches the next selection; ordinary old-path and child branches must not inherit its admission.
            _rows.HoldRename([candidate]);
            return (false, false);
        }
        if (decision is { Kind: RenameKind.Hold, Destination.Identity: DestinationIdentity.Moving } && hasOriginal)
        {
            var destination = decision.Destination.Binding;
            _pass.Prerequisite = _context.Store.PendingAncestors(selection.PairId, selection.PairGeneration,
                candidate.Side, [destination.KeyOn(candidate.Side)]).FirstOrDefault(work =>
                    work.Identity == destination.IdOn(candidate.Side));
            if (_pass.Prerequisite is not null && _pass.Prerequisite.AnchorWorkId != selection.AnchorWorkId)
                return (false, false);
            _pass.Prerequisite = null;
            RefusePrerequisite(selection);
            _rows.HoldRename([candidate]);
            return (false, false);
        }
        if (decision.Kind is RenameKind.None or RenameKind.Leave &&
            _context.Store.ReadRenameContinuation(selection) is not null)
        {
            _rows.HoldRename([candidate]);
            return (false, false);
        }
        if (decision.Kind is not (RenameKind.Hold or RenameKind.Notice)) return (true, true);
        if (retained) return (false, true);
        var current = _context.Store.HoldingIssue(selection.PairId, held);
        remember(current);
        return (false, _pass.AttachHeld(selection, notice, held, candidate.Synced, current));
    }

    internal void RefusePrerequisite(WorkSelection selection, IEnumerable<SyncedEntry>? blockers = null)
    {
        var context = _context;
        try
        {
            foreach (var work in _context.Store.ReadWork(selection))
            {
                var notice = work.Notice;
                var subjects = new List<IssueItem> { new(notice.Side, notice.Path, notice.Id ?? string.Empty) };
                if (work.Before is { } saved)
                {
                    subjects.Add(new IssueItem(Side.Local, saved.LocalPath, saved.LocalId));
                    subjects.Add(new IssueItem(Side.Dropbox, saved.DropboxPath, saved.DropboxId));
                }
                if (blockers is not null)
                    foreach (var blocker in blockers)
                    {
                        subjects.Add(new IssueItem(Side.Local, blocker.LocalPath, blocker.LocalId));
                        subjects.Add(new IssueItem(Side.Dropbox, blocker.DropboxPath, blocker.DropboxId));
                    }
                var subject = IssueSubject.FromDistinct(subjects, notice.Side);
                var change = StateChange.Empty with
                { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, notice.Path, subject)] };
                context.Store.Commit(selection.PairId, change, selection, issue =>
                    context.Store.AttachIssueWork(selection, issue, [(notice, notice.Observed?.IsFolder == true, work.Before)]));
                ActionExecutor.EmitIssues(_context.Events, change);
            }
        }
        finally { _context.Store.EndSelection(selection); }
    }

}
