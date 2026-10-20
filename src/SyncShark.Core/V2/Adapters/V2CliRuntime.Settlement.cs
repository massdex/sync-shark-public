namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private CloudFilesLifetime? _storageConnections;
    internal CloudFilesLifetime? StorageNoticeConnections { get; set; }

    private CloudFilesLifetime RequireStorageConnections()
    {
        if (StorageNoticeConnections is not null) return StorageNoticeConnections;
        if (_composition is null || _store is not SqliteStateStore store)
            throw new IOException("Storage provider is unavailable. Retry through the background host.");
        return _storageConnections ??= new CloudFilesLifetime(_composition.Endpoints.CreateCloudFiles,
            pair => WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(pair.A.RootPath) is not null,
            store, _composition.Endpoints.ObserveStorageRemoteAsync);
    }

    private async Task SettleAfterRunAsync(CancellationToken ct)
    {
        // Borrowed CLI calls already have the independent host lane.
        if (_borrowed || _composition is null || _store is not SqliteStateStore store) return;
        await using var settlement = new StorageSettlementService(store, RequireStorageConnections());
        await settlement.DrainAsync(ct).ConfigureAwait(false);
    }
}
