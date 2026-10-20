namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
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

    private CloudFilesLifetime RequireStorageConnections() => StorageNoticeConnections ??
        throw new IOException("The storage intent is retained. Start the background host and Retry; a short-lived CLI process cannot own Cloud Files hydration.");

    private Task SettleAfterRunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Only the persistent background host owns Cloud Files connections and settlement. Direct CLI
        // runs retain durable work for that host; they must not create pointers then disconnect on exit.
        return Task.CompletedTask;
    }
}
