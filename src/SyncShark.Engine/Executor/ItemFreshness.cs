using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Validates prepared endpoints, bindings and current digest evidence in one observation.</summary>
internal readonly struct ItemFreshness(ExecutorContext _context, ItemSync items)
{
    // One freshness decision compares endpoints, bindings and dirty proof with the prepared row; its target is observed
    // in this hold, or an eligible create-only upload's absence is enforced by its commit.
    internal async ValueTask<(bool Fresh, (Side Side, string Path, Entry? Entry)? ObservedTarget)> IsFreshAsync(
        Row row, SyncAction action, bool restated, CancellationToken ct, Guid? listedScanId = null)
    {
        if (row.Context.DeletionEligible && !items.Rows.RefreshDeletionEligibility(row).Context.DeletionEligible)
            return (false, null);
        var paths = Row.Paths(row, action.Target);
        var localKey = PathNames.KeyFor(Side.Local, paths.Local);
        var dropboxKey = PathNames.KeyFor(Side.Dropbox, paths.Dropbox);
        var storeOnly = action.Kind == ActionKind.Record;
        if (storeOnly && listedScanId is { } scan && !_context.Store.CanUseListedEvidence(_context.Pair.PairId,
            scan, localKey, dropboxKey, false, row.Synced)) return (false, null);
        var local = storeOnly ? row.Local : await _context.Local.ReadOneAsync(paths.Local, ct);
        var dropboxBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Dropbox, dropboxKey);
        // A new-item upload commits create-only (ISide: expected null needs a free path), so an occupant is refused at the
        // commit and mapped to D20 there; a Restated ancestor or a binding at the target keeps the read.
        var createOnly = action.Kind == ActionKind.Upload && !restated && action.CreatesNewItem();
        var dropbox = storeOnly || createOnly ? row.Dropbox : await _context.Dropbox.ReadOneAsync(paths.Dropbox, ct);
        var localBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, localKey);
        // A binding of this new row's identity committed since preparation is a move-in: reobserve, the seam leaves it.
        if (_context.BatchScope is not null && row.Synced is null && items.Preparation.BoundById(row)) return (false, null);
        if (_context.Evidence.Capture(row) is { Hash: not null } proof &&
            (!LocalEvidence.SameFile((proof.Dirty, proof.Boundary, proof.Version),
                _context.Evidence.Read(row.Local!, row.Synced)) ||
                _context.Evidence.CurrentHash(row, proof) is null)) return (false, null);
        var existingFolder = false;
        // An occupied create-only target has the same D20 outcome before admission as at the adapter.
        if (action.Kind is ActionKind.Upload or ActionKind.Download or ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder)
        {
            var written = action.WrittenSide();
            var source = written == Side.Local ? dropbox : local;
            var target = written == Side.Local ? local : dropbox;
            if (row.EntryOn(written) is null && target is not null && source == row.EntryOn(PathNames.Other(written)))
            {
                existingFolder = target.IsFolder && action.Kind is ActionKind.EnsureDropboxFolder or ActionKind.EnsureLocalFolder;
                if (!existingFolder)
                {
                    if (Decide.CrossedFar(local, dropbox, written == Side.Local ? localBinding : dropboxBinding)
                        == PathNames.Other(written)) return (false, null);
                    throw new PathOccupiedException(target.Path);
                }
            }
        }
        // A one-sided unlinked move writes only its own side; the opposite side is not a freshness precondition.
        var fresh = action.Kind == ActionKind.MoveUnlinked
            ? action.WrittenSide() == Side.Local
                ? local == row.Local && localBinding == row.Synced
                : dropbox == row.Dropbox && dropboxBinding == row.Synced
            : row.Synced is null && Decide.IsFreeIncoming(row.Local, row.Dropbox, localBinding, dropboxBinding)
                ? (row.Local is null || local == row.Local) && (row.Dropbox is null || dropbox == row.Dropbox)
                : (local == row.Local || existingFolder && row.Local is null) &&
                  (dropbox == row.Dropbox || existingFolder && row.Dropbox is null) &&
                  (Decide.CrossedFar(row.Local, row.Dropbox, row.Synced) is { } far
                      ? far == Side.Local ? localBinding is null && dropboxBinding == row.Synced
                          : localBinding == row.Synced && dropboxBinding is null
                      : localBinding == row.Synced && dropboxBinding == row.Synced);
        if (!fresh || action.Kind is not (ActionKind.Upload or ActionKind.Download or ActionKind.EnsureDropboxFolder or
            ActionKind.EnsureLocalFolder or ActionKind.ReplaceKind)) return (fresh, null);
        var side = action.WrittenSide();
        return (true, (side, side == Side.Local ? paths.Local : paths.Dropbox, side == Side.Local ? local : dropbox));
    }

}
