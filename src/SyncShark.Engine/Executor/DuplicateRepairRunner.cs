using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Executes the pair's duplicate-folder obligations at the top of every lane iteration (-6 3.5): one
/// classified page at a time; every action re-checks inside its own commit or admission, so a stale class only defers.</summary>
internal sealed class DuplicateRepairRunner(ExecutorContext _context, FolderSync _folders)
{
    /// <summary>Runs every obligation once; true when one waits on other work.</summary>
    internal async Task<bool> RunAsync(CancellationToken ct)
    {
        var waiting = false;
        var take = _context.Settings.StoragePageSize;
        for (DuplicateRepairKey? after = null; ;)
        {
            var page = _context.Store.DuplicateRepairPage(_context.Pair.PairId, _context.Scope, after, take);
            foreach (var (obligation, state, bindings, own) in page)
            {
                ct.ThrowIfCancellationRequested();
                waiting |= await RunOneAsync(obligation, state, bindings, own, ct);
            }
            if (page.Count < take) return waiting;
            after = page[^1].Obligation.Key;
        }
    }

    // The classifier's row decides the action (-6 3.5.2); Held, Excluded and an inactive own operation do nothing.
    private async Task<bool> RunOneAsync(DuplicateRepair obligation, DuplicateRepairState state,
        IReadOnlyList<SyncedEntry> bindings, FolderRemovalState? own, CancellationToken ct)
    {
        switch (state)
        {
            case DuplicateRepairState.Obsolete:
                return Commit(StateChange.Empty with
                    { SettleRepair = (obligation.Key.Side, obligation.Key.Identity, null, null, null) });
            case DuplicateRepairState.Own when own is { Active: true }:
                await _folders.ContinueOwnAsync(own, null, ct);
                return false;
            case DuplicateRepairState.Waiting:
                return true;
            case DuplicateRepairState.LinkedElsewhere:
                return HoldCommit(obligation, bindings, [TextKey.ReasonLinkedElsewhere, TextKey.ReasonLinkedElsewhere]);
            case DuplicateRepairState.Valid:
                return await AttemptAsync(obligation, bindings, ct);
            default:
                return false;
        }
    }

    // The attempt (-7 1.4): one checked entry call per binding in key order, never nested; the first binding with no
    // unmet proof is the victim. With none, any waiting binding writes nothing; otherwise one held Failure.
    private async Task<bool> AttemptAsync(DuplicateRepair obligation, IReadOnlyList<SyncedEntry> bindings,
        CancellationToken ct)
    {
        var deleting = PathNames.Other(obligation.Key.Side);
        var footprint = LockKeys.ForLivePaths(_context.Pair, bindings.SelectMany(binding => new[]
            { (Side.Local, binding.LocalPath), (Side.Dropbox, binding.DropboxPath) }), true);
        var waiting = false;
        var reasons = new TextKey[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            (bool Proceed, bool Settled, TextKey? Reason) step = default;
            CheckedFolderRemoval removal;
            try
            {
                removal = await _folders.RemoveCheckedAsync(new FolderRemovalRequest(
                    new Row(binding.LocalKey, null, null, binding, [], null, RowContext.Neutral), deleting,
                    FolderRemovalRoute.Repair)
                {
                    ExpectedRootId = binding.IdOn(deleting), Footprint = footprint, Live = true,
                    Preconditions = async (observed, _, token) =>
                        (step = await PreconditionsAsync(obligation, binding, observed, token)).Proceed,
                    Accepts = FolderRemovalRequest.EmptyPlan
                }, ct);
            }
            catch (ChangedUnderneathException)
            {
                waiting = true;
                continue;
            }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                reasons[index] = ItemFailures.FailureCause(error);
                continue;
            }
            switch (removal.Check)
            {
                case FolderRemovalCheck.Passed:
                    return false;
                case FolderRemovalCheck.Declined when step.Settled:
                    return false;
                case FolderRemovalCheck.Declined when step.Reason is { } reason:
                    reasons[index] = reason;
                    continue;
                case FolderRemovalCheck.Mount:
                    reasons[index] = TextKey.ReasonShared;
                    continue;
                case FolderRemovalCheck.NotAccepted:
                    reasons[index] = TextKey.ReasonNotEmpty;
                    continue;
                default:
                    waiting = true;
                    continue;
            }
        }
        return waiting || HoldCommit(obligation, bindings, reasons);
    }

    // D4p (-7 1.4), in order: the store proofs, scope, a holding issue, the state-only settle of an absent endpoint,
    // then the endpoint's first listing page. A null reason without Settled is Waiting.
    private async Task<(bool Proceed, bool Settled, TextKey? Reason)> PreconditionsAsync(DuplicateRepair obligation,
        SyncedEntry binding, Row observed, CancellationToken ct)
    {
        var shared = obligation.Key.Side;
        var deleting = PathNames.Other(shared);
        switch (_context.Store.CheckDuplicateRepair(_context.Pair.PairId, obligation.Key, binding))
        {
            case DuplicateRepairProof.LinkedElsewhere:
                return (false, false, TextKey.ReasonLinkedElsewhere);
            case DuplicateRepairProof.History:
                return (false, false, TextKey.ReasonHistory);
            case not DuplicateRepairProof.Valid:
                return (false, false, null);
        }
        if (FolderDeletes.OutOfScope(_context.Scope, binding)) return (false, false, TextKey.ReasonOutOfScope);
        if (_context.Store.HoldingIssue(_context.Pair.PairId, observed) is not null) return (false, false, null);
        if (observed.EntryOn(deleting) is not { } endpoint)
        {
            // The handoff names only where X was just read present (-6 3.5.5); a change the in-commit check finds waits.
            (Side Side, string Path, string Id)? handoff =
                observed.EntryOn(shared) is { } present ? (shared, present.Path, present.Id) : null;
            var changed = Commit(StateChange.Empty with
            {
                ForgetSelf = [(Side.Local, binding.LocalKey)],
                SettleRepair = (shared, obligation.Key.Identity, binding, null, handoff)
            });
            return (false, !changed, null);
        }
        try
        {
            await using var children = _context.For(deleting).ReadAllAsync(endpoint.Path,
                new ListOptions(Recursive: false, IncludeOutOfScope: true), ct).GetAsyncEnumerator(ct);
            if (await children.MoveNextAsync()) return (false, false, TextKey.ReasonNotEmpty);
        }
        catch (InventoryIncompleteException error)
        {
            throw ItemFailures.InventoryFailure(error, endpoint.Path, deleting);
        }
        return (true, false, null);
    }

    // The hold commit (-6 3.5.4): one Failure at the first binding's shared-side path naming both reasons, and the
    // obligation bound to it by a CAS on the issue it recorded.
    private bool HoldCommit(DuplicateRepair obligation, IReadOnlyList<SyncedEntry> bindings, IReadOnlyList<TextKey> reasons)
    {
        var side = obligation.Key.Side;
        var subject = IssueSubject.DuplicateFolderItems(side, bindings) with
            { Folders = new IssueSubject.DuplicateFolders(reasons[0], reasons[^1]) };
        return Commit(StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDuplicateFolderBinding, bindings[0].PathOn(side), subject)],
            HoldRepair = (side, obligation.Key.Identity, obligation.IssueId)
        });
    }

    // One state-only commit; true when its in-commit check found the obligation changed, which only defers it.
    private bool Commit(StateChange change)
    {
        List<RunEvent>? settled = null;
        try
        {
            _context.Store.Commit(_context.Pair.PairId, change,
                settledIssue: (issue, isNew) => ActionChanges.CollectSettlement(ref settled, issue, isNew));
        }
        catch (ChangedUnderneathException)
        {
            return true;
        }
        ActionChanges.EmitSettled(_context.Events, settled);
        return false;
    }
}
