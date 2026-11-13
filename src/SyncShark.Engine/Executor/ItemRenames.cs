using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Admits ordinary renames and proves vacated destinations before their removal.</summary>
internal readonly struct ItemRenames(ExecutorContext _context, Parents _parents, ActionExecutor _executor,
    AncestorFence _ancestors, ILiveExecutionBoundary _live, ItemSync _items)
{
    /// <summary>Reconciles ordinary rename work inside one outer admission scope.</summary>
    internal async Task<RenameDecision> RenameAsync(IReadOnlyList<RenameCandidate> group, Entry? localById, CancellationToken ct,
        bool completedScan = false)
    {
        await using var work = await _context.Admission.EnterWorkAsync(false, ct);
        if (completedScan)
        {
            var listed = await Renamer(true).RunAsync(group, localById, null, ct);
            if (listed.Kind != RenameKind.NeedsFresh) return listed;
            _context.Store.Commit(_context.Pair.PairId, StateChange.Empty);
        }
        await using var permit = await _context.EnterOperationAsync(ct);
        return await Renamer().RunAsync(group, localById, null, ct);
    }

    // Closing releases the footprint only for this act; unfinished work restores its visible owner.
    internal async Task<(SyncOutcome Outcome, bool Kept)> SettleUnknownMoveAsync(Issue issue, Entry observed,
        SyncedEntry? bound, bool remainingHop, Action close, CancellationToken ct)
    {
        var move = issue.Subject.Move!;
        var hadLocalCandidate = false;
        close();
        try
        {
            SyncOutcome outcome;
            if (bound is null) outcome = await _items.SyncPathAsync(Side.Dropbox, observed.Path, ct);
            else
            {
                Entry? local = null;
                var readSaved = false;
                for (var i = 0; i < issue.Subject.Items.Count; i++)
                {
                    var item = issue.Subject.Items[i];
                    if (item.Side != Side.Local || item.Id != bound.LocalId) continue;
                    var entry = await _context.Local.ReadOneAsync(item.Path, ct);
                    readSaved = PathNames.Same(item.Path, bound.LocalPath);
                    if (entry?.Id == bound.LocalId) local = entry;
                    break;
                }
                if (local is null && !readSaved)
                {
                    var entry = await _context.Local.ReadOneAsync(bound.LocalPath, ct);
                    if (entry?.Id == bound.LocalId) local = entry;
                }
                var group = new List<RenameCandidate>();
                var localCandidate = Renames.Candidate(Side.Local, local, bound);
                hadLocalCandidate = localCandidate is not null;
                if (localCandidate is not null) group.Add(localCandidate);
                if (Renames.Candidate(Side.Dropbox, observed, bound) is { } remote) group.Add(remote);
                if (group.Count == 0) outcome = await _items.SyncPathAsync(Side.Dropbox, bound.DropboxPath, ct);
                else
                {
                    var decision = await RenameAsync(group, local, ct);
                    remainingHop = decision.Kind == RenameKind.FinishCaseRename;
                    if (remainingHop && localCandidate is not null)
                    {
                        var updated = _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, move.ExpectedId);
                        decision = updated is null ? Renames.None() :
                            await RenameAsync([localCandidate with { Synced = updated }], local, ct);
                    }
                    if (decision.Kind is RenameKind.Adopt or RenameKind.Follow or RenameKind.FinishCaseRename)
                        return (SyncOutcome.InSync, false);
                    if (decision.NoticeKind is { } kind)
                        return (new SyncOutcome(ActionKind.Notice, null, null, kind, false, decision.Cause), false);
                    outcome = new SyncOutcome(ActionKind.None, NoneReason.Held, null, null, false);
                }
            }
            if (outcome.Succeeded() || outcome.Raised is not null) return (outcome, false);
        }
        catch
        {
            foreach (var owner in _context.Store.Issues(_context.Pair.PairId,
                [IssueKind.Failure, IssueKind.NameClash, IssueKind.Tie]))
                if (IssuePolicy.OwnsItem(owner, Side.Dropbox, move.ExpectedId) ||
                    bound is not null && IssuePolicy.OwnsItem(owner, Side.Local, bound.LocalId)) throw;
            var now = _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, move.ExpectedId);
            var unsettled = bound is null ? now is null : now is not null && (now == bound ||
                remainingHop && hadLocalCandidate && now.LocalPath == bound.LocalPath && now.LocalId == bound.LocalId);
            if (unsettled) RestoreUnknownMove(issue);
            throw;
        }
        RestoreUnknownMove(issue);
        return (new SyncOutcome(ActionKind.None, NoneReason.Held, null, IssueKind.Failure, false, issue.Cause), true);
    }

    /// <summary>The one by-ID observation of an unknown move and its decision, for Retry and Repair; null when the
    /// read failed with an item error, which is reported as Failed.</summary>
    internal async Task<(UnknownMoveCheck.Outcome Decision, (IdentityLocation Location, bool InScope)? Observed)?>
        ObserveUnknownMoveAsync(Issue issue, CancellationToken ct)
    {
        var move = issue.Subject.Move!;
        (IdentityLocation Location, bool InScope)? observed;
        try
        {
            await using var permit = await _context.EnterOperationAsync(ct);
            observed = await ((IIdentitySide)_context.Dropbox).ReadByIdAsync(move.ExpectedId, ct);
        }
        catch (Exception error) when (ItemFailures.IsItemError(error))
        {
            _context.Events(RunEvent.Failed(error, path: issue.Path));
            return null;
        }
        return (UnknownMoveCheck.Decide(move, observed), observed);
    }

    private void RestoreUnknownMove(Issue issue)
    {
        var change = StateChange.Empty with { Raise = [(issue.Kind, issue.Cause, issue.Path, issue.Subject)] };
        List<RunEvent>? settled = null;
        _context.Store.Commit(_context.Pair.PairId, change,
            settledIssue: (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew));
        ActionChanges.EmitSettled(_context.Events, settled);
    }

    internal Task<RenameDecision> RenameAdmittedAsync(IAdmittedExecution execution, WorkSelection? selection, object opportunity,
        IReadOnlyList<RenameCandidate> group, Entry? localById, CancellationToken ct,
        Action<Issue>? retainUnfinished = null) =>
        Renamer().RunAdmittedAsync(execution, selection, opportunity, group, localById, retainUnfinished, ct);

    internal async Task<bool> RemoveVacatedAsync(RenameCandidate incoming, SyncedEntry destination,
        WorkSelection selection, CancellationToken ct)
    {
        if (!destination.IsFolder) return await RemoveVacatedFileAsync(incoming, destination, selection, ct);
        var side = PathNames.Other(incoming.Side);
        var occupant = await _context.For(side).ReadOneAsync(destination.PathOn(side), ct);
        var row = Row.ForOneSide(side, destination.LocalKey, occupant, destination, [], null, RowContext.Neutral);
        var result = await _items.Folders.RemoveVacatedAsync(row, incoming, selection, ct);
        return result?.Status == FolderRemovalStatus.Complete;
    }

    internal async Task<bool> RemoveVacatedFileAsync(RenameCandidate incoming, SyncedEntry destination,
        WorkSelection selection, CancellationToken ct, IAdmittedExecution? execution = null)
    {
        var renames = this;
        var paths = new[] { (Side.Local, destination.LocalPath), (Side.Dropbox, destination.DropboxPath),
            (incoming.Side, incoming.Moved.Path) };
        if (_context.Live && execution is null)
        {
            var footprint = LockKeys.ForLivePaths(_context.Pair, paths, false);
            var coverage = (await _live.CaptureCoverageAsync(selection, footprint, ct)) with { Peer = selection.Peer };
            return await _live.ExecuteAsync([new LiveExecutionRequest(selection, coverage, footprint)],
                (admitted, token) => renames.RemoveVacatedFileAsync(incoming, destination, selection, token, admitted), ct);
        }
        var side = PathNames.Other(incoming.Side);
        await using var work = execution is null ? await _context.Admission.EnterWorkAsync(false, ct) : null;
        await using var permit = execution is null ? await _context.EnterOperationAsync(ct) : null;
        var keys = LockKeys.ForPaths(_context.Pair, paths).ToArray();
        execution?.Require(_context.Pair.PairId, _context.ExpectedGeneration, keys);
        await using var hold = execution is null ? await PathLocks.Shared.EnterAsync(keys, ct) : null;
        var row = await ObserveVacatedFileAsync(incoming, destination, selection, side, ct);
        if (row is null) return false;
        var occupant = row.EntryOn(side)!;
        var removal = await _executor.SelectRemovalAsync(row, side, occupant, ct);
        execution?.Revalidate();
        var change = StateChange.Empty with { ForgetSelf = [(side, destination.KeyOn(side))] };
        ActionExecutor.RequireReadable(_context.Store, _context.Pair.PairId, occupant.Path,
            paths.Select(path => (path.Item1, PathNames.KeyFor(path.Item1, path.Item2))).ToArray(), false, ct);
        // ObserveVacatedFileAsync proved the occupant obsolete and bound its removal to the saved content hash.
        var result = await _executor.RemoveAsync(side, occupant, removal, change, ct, authorized: true);
        return result.Outcome == RemovalOutcome.Removed;
    }

    // Binding, alias, scope and content checks jointly prove this exact occupant obsolete; a failed check returns no removal row.
    private async Task<Row?> ObserveVacatedFileAsync(RenameCandidate incoming, SyncedEntry destination,
        WorkSelection selection, Side side, CancellationToken ct)
    {
        if (_context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, incoming.Synced.LocalKey) != incoming.Synced ||
            _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, destination.LocalKey) != destination ||
            await _context.For(incoming.Side).ReadOneAsync(incoming.Moved.Path, ct) != incoming.Moved ||
            _context.For(incoming.Side) is not IIdentitySide identities) return null;
        var after = SyncedEntries.RenameTarget(incoming, _parents.Destination(side, incoming.Moved.Path));
        var blockers = _context.Store.RenameDestinationOwners(_context.Pair.PairId, incoming.Synced, after);
        if (blockers.Count != 1 || blockers[0].Binding != destination) return null;
        if (await identities.ReadByIdAsync(destination.IdOn(incoming.Side), ct) is { InScope: true }) return null;
        var occupant = await _context.For(side).ReadOneAsync(destination.PathOn(side), ct);
        if (occupant is not { IsFolder: false, Skip: null } || occupant.Id != destination.IdOn(side)) return null;
        var row = Row.ForOneSide(side, destination.LocalKey, occupant, destination, [], null, RowContext.Neutral);
        if (_context.Scope.Excludes(destination.LocalPath, destination.DropboxPath, false) ||
            _context.Scope.HoldsNestedRoot(Side.Local, destination.LocalPath) ||
            _context.Scope.HoldsNestedRoot(Side.Dropbox, destination.DropboxPath)) return null;
        var clash = _context.Store.ReadRenameContinuation(selection)?.Clash;
        if (clash is not null)
        {
            var current = _context.Store.IssueById(_context.Pair.PairId, clash.Id);
            if (current is null || current.Ignored || current.Dismissed || !current.Matches(clash)) return null;
        }
        if (clash is null ? _context.Store.HoldingIssue(_context.Pair.PairId, row) is not null :
            _context.Store.HeldByOtherIssue(_context.Pair.PairId, row, clash.Id)) return null;
        var dirty = false;
        if (side == Side.Local)
        {
            var proof = _context.Evidence.Read(occupant, destination);
            dirty = proof.Dirty;
            if (Decide.LocalChanged(occupant, destination, null, dirty) is null)
                row = row with { LocalHash = (await _context.Evidence.HashAsync(_context.Local, occupant, destination, proof, ct)).Hash };
        }
        if (Decide.Changed(side, occupant, destination, row.LocalHash, dirty)) return null;
        return row;
    }

    internal RenameRunner Renamer(bool completedScan = false) => new(_context, _parents, _items.Rows, _ancestors, completedScan);

}
