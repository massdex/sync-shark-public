namespace Massdex.SyncShark.V2;

/// <summary>Host-owned bounded settlement lane; independent of upload and foreground hydration.</summary>
internal sealed class StorageSettlementService(SqliteStateStore store, CloudFilesLifetime connections,
    Func<PairDef, string, CancellationToken, Task<PilotRemoteEntry?>>? observeRemote = null,
    StorageNotice? target = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    internal void Start() => _loop ??= Task.Run(LoopAsync);

    private PairDef? Pair(StorageSettlement job) => store.Pairs().FirstOrDefault(pair =>
        pair.A.RootId == job.RootId && pair.B.RootId == job.RemoteBinding &&
        pair.A.Provider == SyncProviders.Local && pair.B.Provider == SyncProviders.Dropbox);

    private bool Selected(StorageSettlement job) => target is null ||
        target.RootId == job.RootId && target.ObjectId == job.ObjectId && target.RemoteBinding == job.RemoteBinding;

    private bool Included(PairDef pair, string path) =>
        !pair.Excluded.Any(prefix => PairPath.IsSelfOrUnder(path, prefix)) &&
        !SyncIgnorePolicy.IsIgnored(path, false, PairInclude.IgnoreRules(pair));

    private bool Eligible(StorageSettlement job) => target?.Operation != "remote-update" && Selected(job) && Pair(job) is { } pair &&
        Included(pair, job.Remote.RelativePath) &&
        store.ResolveStoragePolicy(pair.A, job.Remote.RelativePath) == "online-only" &&
        !store.StorageOperationIgnored(pair.PairId, Metadata(job, "settlement"));

    internal async Task DrainAsync(CancellationToken ct)
    {
        var worker = new FilesOnDemandSettlement(store, ConvertAsync, Report, Eligible, Complete);
        long cursor = 0;
        do { cursor = await worker.RunPageAsync(cursor, ct).ConfigureAwait(false); }
        while (cursor != 0);
        string pointerCursor = "";
        do { pointerCursor = await UpdatePointersPageAsync(pointerCursor, ct).ConfigureAwait(false); }
        while (pointerCursor.Length != 0);
    }

    private async Task LoopAsync()
    {
        var worker = new FilesOnDemandSettlement(store, ConvertAsync, Report, Eligible, Complete);
        long cursor = 0;
        string pointerCursor = "";
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var settlements = worker.RunPageAsync(cursor, _stop.Token);
                var pointers = UpdatePointersPageAsync(pointerCursor, _stop.Token);
                await Task.WhenAll(settlements, pointers).ConfigureAwait(false);
                cursor = await settlements.ConfigureAwait(false);
                pointerCursor = await pointers.ConfigureAwait(false);
                // Yield between bounded pages; failed jobs get another turn after the cursor wraps.
                await Task.Delay(cursor == 0 && pointerCursor.Length == 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMilliseconds(100), _stop.Token).ConfigureAwait(false);
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
        if (Pair(job) is { } pair)
            StorageNotices.Report(store, pair, job.Remote.RelativePath, Metadata(job, "settlement"), error);
    }

    private static StorageNotice Metadata(StorageSettlement job, string operation) =>
        new(job.RootId, job.ObjectId, job.RemoteBinding, operation);

    private void Complete(StorageSettlement job)
    {
        if (Pair(job) is { } pair) StorageNotices.Complete(store, pair.PairId, Metadata(job, "settlement"));
    }

    internal async Task<string> UpdatePointersPageAsync(string after, CancellationToken ct)
    {
        if (target is not null && target.Operation != "remote-update") return "";
        var observe = observeRemote ?? connections.ObserveRemote;
        if (observe is null) return "";
        var page = store.ChangedStoragePointers(after);
        await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (job, token) =>
        {
            var pair = Pair(job);
            if (pair is null || !Selected(job) || !Included(pair, job.Remote.RelativePath)) return;
            try
            {
                var local = store.ByObject(job.RootId, job.ObjectId);
                if (local is null || local.Path != job.Remote.RelativePath ||
                    (local.Flags & (ItemFlags.Ignored | ItemFlags.Quarantined | ItemFlags.Link | ItemFlags.Unreadable)) != 0 ||
                    store.StorageOperationIgnored(pair.PairId, Metadata(job, "remote-update"))) return;
                var provider = connections.RequireProvider(pair, token);
                var full = LocalItems.FullPath(pair.A.RootPath, local.Path);
                // Hydrated/edited files are left entirely to the ordinary planner and conflict rules.
                if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var current) || current is null ||
                    current.State.Availability != SyncCloudFileAvailability.OnlineOnly) return;
                var remote = await observe(pair, local.Path, token).ConfigureAwait(false);
                if (remote is null || remote.Kind != PilotEntryKind.File || remote.Id != job.Remote.Id ||
                    remote.Revision is null || remote.ContentHash is null || remote.RelativePath != local.Path) return;
                remote = remote with { ModifiedUtc = remote.ModifiedUtc ?? local.Mtime };
                var (volume, id) = LocalItems.ParseObjectId(job.ObjectId);
                var (rootVolume, rootId) = LocalItems.ParseObjectId(pair.A.RootObjectId);
                using var heldRoot = PilotDirectoryLease.Open(pair.A.RootPath, new(rootVolume, rootId));
                provider.UpdateDehydratedPlaceholder(job.Remote, remote, new(volume, id), () =>
                {
                    token.ThrowIfCancellationRequested();
                    heldRoot.VerifyBinding();
                    PairMapping.CloudFilesBinding(pair).RequireSame(PairMapping.CloudFilesBinding(
                        Pair(job) ?? throw new IOException("The pair binding was removed.")));
                });
                // Publish the confirmed common revision through the ordinary commit path. Otherwise a
                // later local edit would be compared with the pre-update baseline and look both-edited.
                var baseline = store.BaselinesAt(pair.PairId, Side.A, [local.Path]).FirstOrDefault(point =>
                    point.A.ObjectId == local.ObjectId && point.B.ObjectId == remote.Id);
                if (baseline is not null)
                {
                    var updated = local with { Hash = remote.ContentHash, Size = remote.Size,
                        Mtime = remote.ModifiedUtc!.Value, Flags = ItemFlags.OwnedPointer };
                    var remoteItem = updated with { ObjectId = remote.Id!, VersionId = remote.Revision, Flags = ItemFlags.None };
                    var action = new SyncAction(ShortId.Make("ptr", pair.PairId, local.ObjectId, remote.Revision!),
                        42, ActionKind.Adopt, Side.A, local.Path, null, remoteItem, local, false, baseline.ObjectKey);
                    store.CommitResult(pair.PairId, baseline.Gen, action,
                        new(action.ActionId, Outcome.Done, updated, remoteItem, null, null));
                }
                store.CompleteStoragePointerUpdate(job, remote);
                StorageNotices.Complete(store, pair.PairId, Metadata(job, "remote-update"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { StorageNotices.Report(store, pair, job.Remote.RelativePath, Metadata(job, "remote-update"), error); }
        }).ConfigureAwait(false);
        return page.Count == 0 ? "" : System.Text.Json.JsonSerializer.Serialize(
            new[] { page[^1].RootId, page[^1].ObjectId, page[^1].RemoteBinding });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
