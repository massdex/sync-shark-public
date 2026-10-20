namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private CloudFilesLifetime? _storageConnections;

    private async Task SettleAfterRunAsync(CancellationToken ct)
    {
        // Borrowed CLI calls already have the independent host lane.
        if (_borrowed || _composition is null || _store is not SqliteStateStore store) return;
        _storageConnections ??= new CloudFilesLifetime(_composition.Endpoints.CreateCloudFiles,
            pair => WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(pair.A.RootPath) is not null);
        await using var settlement = new StorageSettlementService(store, _storageConnections);
        await settlement.DrainAsync(ct).ConfigureAwait(false);
    }
}
