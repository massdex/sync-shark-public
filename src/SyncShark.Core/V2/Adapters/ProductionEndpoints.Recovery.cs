namespace Massdex.SyncShark.V2;

public sealed partial class ProductionEndpoints
{
    /// <summary>Recovery preview opens read-only exact-path observers, never a local control namespace.</summary>
    public async Task PrepareRecoveryReadAsync(PairDef pair, CancellationToken ct)
    {
        var aTask = CheckSideAsync(pair.A, ct);
        var bTask = CheckSideAsync(pair.B, ct);
        await Task.WhenAll(aTask, bTask).ConfigureAwait(false);
        Open(pair.A, Side.A, await aTask.ConfigureAwait(false));
        Open(pair.B, Side.B, await bTask.ConfigureAwait(false));

        void Open(RootDef root, Side side, CheckedSide checkedSide)
        {
            IInventorySource inventory = checkedSide.Client is { } client
                ? new DropboxInventory(root, side, client, _store, pair.PairId)
                : new LocalInventory(root, side, _store, OwnedInside(root, checkedSide.Recovery!), _settings, _time);
            _open[root.RootId] = (inventory, new RecoveryReadOnlyWriter(pair.PairId, root, side, checkedSide), null);
        }
    }

    private sealed class RecoveryReadOnlyWriter(string pairId, RootDef root, Side side, CheckedSide checkedSide)
        : IEndpointWriter, IRecoveryRecordReader
    {
        public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
        {
            if (checkedSide.Client is { } client)
            {
                var cloud = await client.FindEntryAtPathAsync(path, ct).ConfigureAwait(false);
                return cloud is null ? null : DropboxItems.ToItem(cloud);
            }
            var (volume, fileId) = LocalItems.ParseObjectId(root.RootObjectId);
            var local = await SyncLocalEndpointStore.ObserveReadOnlyAsync(
                new SyncLocalEndpointBinding(root.RootPath, volume, fileId), path, SyncProviders.Local, ct)
                .ConfigureAwait(false);
            return local is null ? null : LocalItems.FromStore(local, LocalItems.FullPath(root.RootPath, local.RelativePath));
        }

        public Task<ItemState> DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct)
        {
            if (checkedSide.Client is { } client)
                return RecoveryRecordPreviews.DescribeDropboxAsync(client, pairId, side, recovery, ct);
            if (checkedSide.RecoveryUnavailable is { } unavailable)
                throw new IOException("Recovery location unavailable: " + unavailable);
            return Task.FromResult(RecoveryRecordPreviews.DescribeLocal(pairId, side, checkedSide.Recovery!, recovery, ct));
        }

        private static NotSupportedException ReadOnly() => new("Recovery preview cannot change files or open retained content. Run an explicitly confirmed restore instead.");
        public Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct) => throw ReadOnly();
        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) => throw ReadOnly();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw ReadOnly();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw ReadOnly();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw ReadOnly();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => throw ReadOnly();
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => throw ReadOnly();
    }
}
