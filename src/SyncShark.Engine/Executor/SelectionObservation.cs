using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Projects one branch observation with its Local echo authority and exact failure row.</summary>
internal readonly struct SelectionObservation(SelectionPass pass, ExecutorContext _context, Observer _observer,
    Parents _parents, RowContexts _rows, ActionExecutor _executor)
{
    // One observation of both sides projects one row; the error paths fail that same observation.
    internal async ValueTask<Row?> ReadAsync(SelectionBranch branch, ItemFailedException? failedWalk,
        Action<Row, string?> observed, CancellationToken ct)
    {
        var selection = branch.Selection;
        var notice = branch.Notice;
        Row? raw = null;
        branch.Evidence = null;
        branch.Failed = false;
        try
        {
            if (failedWalk is not null) throw failedWalk;
            var suppliedPrimary = branch.TakePrimary();
            var local = await _observer.ObserveLocalAsync(notice.Side, notice.Path, ct, suppliedPrimary).ConfigureAwait(false);
            // One scope answer settles an unbound Local entry (B-10); repeating Excludes for in-scope rows is
            // the accepted cost of saving an excluded row's Dropbox read.
            if (branch.Excluded && notice is { Side: Side.Local, Kind: ChangeKind.Changed, OldPath: null } &&
                local is { Local: { } entry, Synced: null } &&
                _context.Store.SyncedById(selection.PairId, Side.Local, entry.Id) is null)
                return null;
            if (notice is { Side: Side.Local, Kind: not ChangeKind.Deleted, IdentityDeleted: false } &&
                Decide.LocalMatchesBaseline(local.Local, local.Synced) &&
                (pass.LocalEcho ??= _context.Store.CreateLocalEchoProbe(selection))(local.Synced!,
                    branch.ThroughPeer ?? _context.Store.ReadWorkWatermark()))
            {
                if (local.Local!.IsFolder) return null;
                var evidence = _context.Evidence.Read(local.Local, local.Synced);
                // No observation means no peer settlement authority.
                if (!evidence.Dirty) return null;
                branch.Evidence = new LocalEvidence.DirtyProof(evidence.Dirty, evidence.Boundary, evidence.Version);
            }
            raw = await _observer.ObserveDropboxAsync(notice.Side, notice.Path, local, ct, suppliedPrimary).ConfigureAwait(false);
            var other = PathNames.Other(notice.Side);
            var otherKey = branch.Original is null || branch.Peer is null && branch.ThroughPeer is not null
                ? PathNames.KeyFor(other, raw.PathOn(other) ?? _parents.Counterpart(other, notice.Path, raw.Synced)) : null;
            if (branch.Original is null && branch.SeparateWork!(other, otherKey!, raw.EntryOn(other)?.Id)) return null;
            observed(raw, otherKey);
            return raw with { Context = await _rows.ForSelectionAsync(raw, ct).ConfigureAwait(false) };
        }
        catch (Exception error) when (ItemFailures.IsItemError(error) && error is not ChangedUnderneathException)
        {
            raw ??= Row.ForOneSide(notice.Side, branch.Key, notice.Observed, branch.Saved, [], null, RowContext.Neutral);
            var outcome = await _executor.Failures.FailAsync(raw, null, error, ct, issue => branch.Retain(raw, issue),
                selection: selection).ConfigureAwait(false);
            // A temporary observation failure keeps its selection out of the rest of the run, as an execution one does.
            if (outcome.None == NoneReason.Unfinished) pass.RecordTemporary?.Invoke(selection);
            branch.Failed = true;
            return null;
        }
    }
}
