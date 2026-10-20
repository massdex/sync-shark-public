namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    private CloudFilesLifetime? _storageConnections;
    internal CloudFilesLifetime? StorageNoticeConnections { get; set; }
    internal Func<PairDef, bool>? StoragePaused { get; set; }

    private bool IsStoragePaused(PairDef pair)
    {
        if (StoragePaused is not null) return StoragePaused(pair);
        if (_instanceLock is null) return false;
        var saved = new UiPreferencesStore(_instanceLock.StateAuthority).Load();
        var now = DateTimeOffset.UtcNow;
        return PauseStateProjection.GlobalPaused(saved, now) ||
            PauseStateProjection.AccountPaused(saved, pair.A.AccountId, now) ||
            PauseStateProjection.AccountPaused(saved, pair.B.AccountId, now);
    }

    private CloudFilesLifetime RequireStorageConnections()
    {
        if (StorageNoticeConnections is not null) return StorageNoticeConnections;
        if (_composition is null || _store is not SqliteStateStore store)
            throw new IOException("Storage provider is unavailable. Retry through the background host.");
        return _storageConnections ??= new CloudFilesLifetime(_composition.Endpoints.CreateCloudFiles,
            pair => CloudFilesLifetime.IsRegisteredPair(pair, V2Settings.Default),
            store, _composition.Endpoints.ObserveStorageRemoteAsync);
    }

    private async Task SettleAfterRunAsync(CancellationToken ct)
    {
        // Borrowed CLI calls already have the independent host lane.
        if (_borrowed || _composition is null || _store is not SqliteStateStore store || !store.HasStorageWork()) return;
        await using var settlement = new StorageSettlementService(store, RequireStorageConnections(), paused: IsStoragePaused);
        await settlement.DrainAsync(ct).ConfigureAwait(false);
    }
}
