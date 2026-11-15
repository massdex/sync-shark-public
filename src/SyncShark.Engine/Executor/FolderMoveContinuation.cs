namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Serially continues eligible split roots across lane passes, leaving removal admission to its owner.</summary>
internal sealed class FolderMoveContinuation(ExecutorContext context, ItemRenames renames, FolderSync folders)
{
    private TaskCompletionSource _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task NextTick => Volatile.Read(ref _tick).Task;

    private void Tick() => Interlocked.Exchange(ref _tick,
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    internal async Task<bool> RunAsync(HashSet<string> ended, CancellationToken ct)
    {
        var advanced = false;
        foreach (var root in context.Store.FolderMoveRoots(context.Pair.PairId, ct))
        {
            if (ended.Contains(root.SourceId)) continue;
            var issue = context.Store.MoveResponsibility(context.Pair.PairId,
                root.Phase == FolderMovePhase.Relabelled ? root.DestinationId! : root.SourceId);
            if (issue is null && root.Phase == FolderMovePhase.Relabelled)
            {
                var claim = PairRuntimes.FolderMoveClaim(root.SourceId);
                if (!PairRuntimes.TryClaimFolderJob(claim)) continue;
                try
                {
                    var result = await new FolderMoveCleanup(context, folders).RunAsync(root.SourceId, Tick, ct);
                    advanced |= result.Advanced;
                    if (result.Ended) ended.Add(root.SourceId);
                }
                finally { PairRuntimes.ReleaseFolderJob(claim); }
                continue;
            }
            if (issue?.Cause != TextKey.FailMoveInParts)
            {
                ended.Add(root.SourceId);
                continue;
            }
            try
            {
                var result = await renames.SettleMoveAsync(issue, ct, Tick);
                advanced |= result.Advanced;
                if (!result.Skipped && (result.Outcome is null || result.Kept)) ended.Add(root.SourceId);
            }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                context.Events(RunEvent.Failed(error, path: issue.Path));
                ended.Add(root.SourceId);
            }
        }
        return advanced;
    }
}
