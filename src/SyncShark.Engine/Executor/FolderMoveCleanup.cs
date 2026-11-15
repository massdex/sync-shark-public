using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Retires relabelled source custody through the existing checked empty-folder removal owner.</summary>
internal sealed class FolderMoveCleanup(ExecutorContext context, FolderSync folders)
{
    internal async Task<(bool Advanced, bool Ended)> RunAsync(string rootId, Action tick, CancellationToken ct)
    {
        var advanced = false;
        var rows = context.Store.FolderMoves(context.Pair.PairId, rootId);
        var children = rows.Where(row => row.ParentId is not null)
            .GroupBy(row => row.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var move in rows.OrderByDescending(row => PathNames.Depth(row.SourcePath)))
        {
            ct.ThrowIfCancellationRequested();
            if (move.Phase != FolderMovePhase.Relabelled || context.Store.HasPendingUnder(context.Pair.PairId, move.SourcePath))
                continue;
            var entry = new Entry(move.SourcePath, true, 0, default, move.SourceId, string.Empty, string.Empty, null);
            var row = Row.ForOneSide(Side.Dropbox, PathNames.KeyFor(Side.Dropbox, move.SourcePath), entry,
                null, [], null, RowContext.Neutral);
            CheckedFolderRemoval result;
            try
            {
                result = await folders.RemoveCheckedAsync(new FolderRemovalRequest(row, Side.Dropbox, FolderRemovalRoute.SplitCleanup)
                {
                    ExpectedRootId = move.SourceId, Live = true, Accepts = FolderRemovalRequest.EmptyPlan
                }, ct);
            }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                await new ItemFailures(context).FailAsync(row, null, error, ct);
                return (advanced, true);
            }
            finally { tick(); }
            switch (result.Check)
            {
                case FolderRemovalCheck.ForeignOperation:
                    return (advanced, true);
                case FolderRemovalCheck.Waiting:
                case FolderRemovalCheck.Passed when result.Removal?.Status == FolderRemovalStatus.Partial:
                    continue;
                case FolderRemovalCheck.Passed when result.Removal?.Status is FolderRemovalStatus.Complete
                    or FolderRemovalStatus.Retained or FolderRemovalStatus.Unknown:
                case FolderRemovalCheck.Absent:
                case FolderRemovalCheck.Replaced:
                case FolderRemovalCheck.NotAccepted:
                case FolderRemovalCheck.Mount:
                case FolderRemovalCheck.NotJustified:
                    // Keeping every live child's parent preserves reachability from the streamed root set.

                    context.Store.SetFolderMove(context.Pair.PairId, context.ExpectedGeneration, move, null);
                    if (move.ParentId is { } parent) children[parent]--;
                    advanced = true;
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
        return (advanced, false);
    }
}
