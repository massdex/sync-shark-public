using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>One AutoSync folder batch (D-1105-1): a scan of both sides scoped to the folder, then the Syncer batch, which
/// only adds (D-1105-22). The session and the contract checks share this one sequence.</summary>
internal static class FolderBatch
{

    // One folder batch: the scan token, both sinks, the Syncer batch, child tasks and EndScan are one session with one failure and cleanup path.
    internal static async Task RunAsync(ExecutorContext context, SyncPorts ports, ISide dropbox, RunLog log,
        WorkSelection? selection, Side side, string scope, IReadOnlySet<long>? retryIssues, bool settlesFirstRun,
        Func<CancellationToken, Task> checkRoots,
        ScanOwnership ownership, Func<bool> acceptGrant, CancellationToken ct)
    {
        var store = context.Store;
        var pairId = context.Pair.PairId;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new TaskSet(cancel);
        await using var reservation = ownership.GrantScoped(pairId, acceptGrant, ct);
        reservation.Require(ownership, pairId);
        var token = store.BeginScan(pairId, reservation.ScanId, context.ExpectedGeneration, includeDeleted: false, scope);
        Exception? failure = null;
        try
        {
            var unreadable = store.UnreadableUnder(pairId, PathNames.Key(scope)).ToHashSet(StringComparer.Ordinal);
            var acquired = new Dictionary<string, string>(StringComparer.Ordinal);
            var batchRows = context.Settings.StoreBatchRows;
            var localSink = store.OpenScan(pairId, token, Side.Local, batchRows);
            IScanSink remoteSink;
            try { remoteSink = store.OpenScan(pairId, token, Side.Dropbox, batchRows); }
            catch (Exception primary)
            {
                try { await localSink.AbortAsync(); }
                catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                throw;
            }
            var scan = new PairScan(context.Local, dropbox, localSink, remoteSink,
                store, pairId, token, null, batchRows);
            await scan.RunAsync(tasks, log, false, 0, cancel.Token, scope, (path, id) =>
            {
                var key = PathNames.Key(path);
                if (unreadable.Contains(key)) acquired[key] = id;
            }).ConfigureAwait(false);
            if (scan.Incomplete is { } failed && scan.Unlisted is null) throw failed;
            await checkRoots(ct).ConfigureAwait(false);
            // Only a complete listing for a Local notice proves that a recorded unreadable folder became readable (A11).
            if (scan.Incomplete is null && side == Side.Local)
                foreach (var (key, id) in acquired)
                {
                    ct.ThrowIfCancellationRequested();
                    store.ReleaseUnreadable(pairId, context.ExpectedGeneration, key, id);
                }
            await new Syncer(context with { ScanId = token, BatchScope = scope, Live = selection is not null, BatchUnlisted = scan.Unlisted, BatchIncomplete = scan.Incomplete is not null, BatchSettlesFirstRun = settlesFirstRun }, ports).SyncScannedAsync(token,
                new ListedSkips(scan.LocalSkipped, scan.DropboxSkipped, scan.LocalSkippedFolders,
                    scan.DropboxSkippedFolders), retryIssues, ct, selection).ConfigureAwait(false);
            // What the listing missed stays one failure on the folder, whose selection retries it.
            if (scan.Incomplete is { } incomplete) throw incomplete;
        }
        catch (Exception error) { failure = error; }
        failure = await tasks.JoinAsync(failure, failure is not null,
            joined => log.Failed(joined), replaceCancellation: false).ConfigureAwait(false);
        try { await store.EndScanAsync(pairId, token, context.Settings.StoragePageSize).ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            if (failure is null) failure = cleanup;
            else log.Failed(cleanup);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
