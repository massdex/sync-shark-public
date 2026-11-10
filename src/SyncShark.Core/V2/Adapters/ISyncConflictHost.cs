namespace Massdex.SyncShark;

/// <summary>The open-conflict (v2 notice) surface the window calls.</summary>
public interface ISyncConflictHost
{
    /// <summary>Loads the open conflict comparisons.</summary>
    Task<IReadOnlyList<SyncConflictComparison>> LoadOpenConflictComparisonsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a page of the open conflict comparisons.</summary>
    Task<IReadOnlyList<SyncConflictComparison>> LoadOpenConflictComparisonsPageAsync(int limit, int offset, string? search, string? group = null, CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Captures the open conflicts matching a filter, without paging.</summary>
    Task<IReadOnlyList<SyncConflictSelection>> CaptureOpenConflictSelectionAsync(string? search, string? group = null, CancellationToken cancellationToken = default, string? connectionId = null);

    /// <summary>Loads the content metadata for one conflict's path.</summary>
    Task<SyncConflictContent> LoadConflictContentAsync(string pairId, string relativePath, long maximumContentBytes = 8L * 1024 * 1024, CancellationToken cancellationToken = default, string? conflictId = null, long? conflictGeneration = null);

    /// <summary>Resolves the selected conflicts and returns the batch result.</summary>
    Task<SyncAuthorityBatchResult> ResolveConflictSelectionCommandAsync(IReadOnlyList<SyncConflictResolutionRequest> requests, string? connectionId = null, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, Action<string>? admissionProgress = null);

    /// <summary>The items a "Held by your choice" notice holds, for choosing one to release or stop syncing.</summary>
    Task<IReadOnlyList<string>> LoadHeldItemsAsync(string pairId, string noticeId, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
