namespace Massdex.SyncShark.V2;

/// <summary>Host-owned bounded settlement lane; independent of upload and foreground hydration.</summary>
internal sealed class StorageSettlementService(SqliteStateStore store, CloudFilesLifetime connections) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    internal void Start() => _loop ??= Task.Run(LoopAsync);

    private PairDef? Pair(StorageSettlement job) => store.Pairs().FirstOrDefault(pair =>
        pair.A.RootId == job.RootId && pair.B.RootId == job.RemoteBinding &&
        pair.A.Provider == SyncProviders.Local && pair.B.Provider == SyncProviders.Dropbox);

    private bool Eligible(StorageSettlement job) => Pair(job) is { } pair &&
        store.ResolveStoragePolicy(pair.A, job.Remote.RelativePath) == "online-only";

    internal async Task DrainAsync(CancellationToken ct)
    {
        var worker = new FilesOnDemandSettlement(store, ConvertAsync, Report, Eligible);
        long cursor = 0;
        do { cursor = await worker.RunPageAsync(cursor, ct).ConfigureAwait(false); }
        while (cursor != 0);
    }

    private async Task LoopAsync()
    {
        var worker = new FilesOnDemandSettlement(store, ConvertAsync, Report, Eligible);
        long cursor = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                cursor = await worker.RunPageAsync(cursor, _stop.Token).ConfigureAwait(false);
                // Yield between bounded pages; failed jobs get another turn after the cursor wraps.
                await Task.Delay(cursor == 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(100), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                RunOutput.Observe(error);
                try { await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<StorageObserved> ConvertAsync(StorageSettlement job, CancellationToken ct)
    {
        var pair = Pair(job) ?? throw new IOException("The confirmed upload binding is no longer configured.");
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.A.RootPath))!);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
            throw new IOException("Online-only storage requires a fixed NTFS volume. Keep offline or move the pair to a fixed NTFS volume.");
        var (volume, fileId) = LocalItems.ParseObjectId(pair.A.RootObjectId);
        using var root = PilotDirectoryLease.Open(pair.A.RootPath, new PilotDirectoryIdentity(volume, fileId));
        var (itemVolume, itemId) = LocalItems.ParseObjectId(job.ObjectId);
        void Revalidate()
        {
            root.VerifyBinding();
            var current = Pair(job) ?? throw new IOException("The pair binding was removed.");
            PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(current));
            if (!store.IsCurrentStorageSettlement(job) || !Eligible(job))
                throw new IOException("The upload receipt, pair binding or storage policy changed. Keep the file and retry settlement.");
        }
        Revalidate();
        var provider = connections.RequireProvider(pair, ct);
        var result = await provider.ConvertVerifiedSyncedFileAsync(job.Remote, ct, true,
            new PilotDirectoryIdentity(itemVolume, itemId), Revalidate).ConfigureAwait(false);
        return new(result.State.Availability.ToString(), false, result.State.IsInSync);
    }

    private void Report(StorageSettlement job, Exception error)
    {
        if (Pair(job) is not { } pair) return;
        store.AddNotices([new Notice(pair.PairId, 42, NoticeKind.Info, job.Remote.RelativePath,
            "Online-only settlement failed: " + error.Message + " Retry settlement after correcting the cause, or Ignore. The upload is not repeated.",
            "storage-settlement:" + job.ObjectId + ":" + error.GetType().Name + ":" + error.HResult)]);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
