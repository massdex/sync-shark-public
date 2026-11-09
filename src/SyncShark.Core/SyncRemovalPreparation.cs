namespace Massdex.SyncShark;

/// <summary>The removal handoff's storage guard. No registration is removed until every required offline
/// preparation succeeds; callers keep package/startup removal after this guard.</summary>
public static class SyncRemovalPreparation
{
    public static async Task<SyncPairSet> PrepareStorageAsync(SyncPairSet pairs,
        Func<string, Task<SyncPairSet>> disable, Action<SyncPair> unregister, Action<string>? progress = null)
    {
        var required = pairs.Pairs.Where(pair => pair.FilesOnDemandEnabled).ToArray();
        foreach (var pair in required)
        {
            progress?.Invoke("Making Files Local: " + pair.Name);
            pairs = await disable(pair.Id);
            if (pairs.Pairs.Any(current => current.Id == pair.Id && current.FilesOnDemandEnabled))
                throw new IOException("Offline preparation has not been verified. Keep Sync Shark installed and retry after making the files available offline.");
        }
        foreach (var pair in pairs.Pairs) unregister(pair);
        return pairs;
    }

    public static string FailureMessage(Exception error) =>
        "Removal preparation did not complete. Automatic sync remains paused; the app has not been uninstalled. " +
        error.Message + " Next step: resolve the reported cause and Retry removal, or resume automatic sync from the pause control to keep syncing.";
}
