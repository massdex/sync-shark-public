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
                var claim = $"move:{root.SourceId}";
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
                var closed = false;
                void Close()
                {
                    context.Store.Commit(context.Pair.PairId, StateChange.Empty with { Close = [(issue.Path, [issue.Kind])] });
                    closed = true;
                }
                var result = await renames.SettleMoveAsync(issue, Close, ct, Tick);
                var current = context.Store.FolderMoves(context.Pair.PairId, root.SourceId)
                    .FirstOrDefault(row => row.ParentId is null);
                advanced |= closed || current is null || current.Phase != root.Phase;
                if (result is null || result.Value.Kept) ended.Add(root.SourceId);
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
