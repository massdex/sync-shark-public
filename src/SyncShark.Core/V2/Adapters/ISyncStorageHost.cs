namespace Massdex.SyncShark;

/// <summary>The managed-storage surface the window calls.</summary>
public interface ISyncStorageHost
{
    /// <summary>Re-evaluates files-on-demand for a pair and returns the pair set.</summary>
    Task<SyncPairSet> DisableFilesOnDemandAsync(string pairId, CancellationToken cancellationToken = default, string? expectedConnectionId = null, long? expectedPolicyRevision = null);

    /// <summary>Applies the pending directory storage for a pair.</summary>
    Task<SyncCloudAvailabilityResult> ApplyPendingDirectoryStorageAsync(string pairId, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Changes managed storage for several folders at once.</summary>
    Task<SyncCloudAvailabilityResult> ChangeManagedStorageBatchAsync(string pairId, string connectionId, IReadOnlyList<string>? folderPaths, SyncDirectoryStorage? storage, long expectedPolicyRevision, CancellationToken cancellationToken = default, bool applyExistingRootItems = false, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Resets a managed file's storage rule.</summary>
    Task<SyncCloudAvailabilityResult> ResetManagedFileStorageAsync(string pairId, string connectionId, SyncFileStorageRule expectedRule, long expectedPolicyRevision, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Keeps a path on device or online-only.</summary>
    Task<SyncCloudAvailabilityResult> SetPathAvailabilityAsync(string pairId, string relativePath, bool keepOnDevice, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Loads the storage overview for one pair connection.</summary>
    Task<SyncStorageOverview> LoadStorageOverviewAsync(string pairId, string connectionId, int offset = 0, string? search = null, string? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Loads a page of the storage inventory.</summary>
    Task<SyncStorageInventoryPage> LoadStorageInventoryAsync(string? accountId = null, string? pairId = null, int offset = 0, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Inspects one storage root.</summary>
    Task<SyncStorageInventoryPage> InspectStorageRootAsync(string root, string? pairId = null, string? accountId = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Changes managed storage for one folder.</summary>
    Task<SyncCloudAvailabilityResult> ChangeManagedStorageAsync(string pairId, string connectionId, string? folderPath, SyncDirectoryStorage? storage, long expectedPolicyRevision, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Changes managed storage for a pair's root.</summary>
    Task<SyncCloudAvailabilityResult> ChangeManagedRootStorageAsync(string pairId, string connectionId, SyncDirectoryStorage storage, long expectedPolicyRevision, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Changes the storage rule for one managed file.</summary>
    Task<SyncCloudAvailabilityResult> ChangeManagedFileStorageAsync(string pairId, string connectionId, string relativePath, SyncDirectoryStorage storage, long expectedPolicyRevision, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);
}
