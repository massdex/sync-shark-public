namespace Massdex.SyncShark;

/// <summary>The first-sync review surface the window calls.</summary>
public interface ISyncFirstSyncHost
{
    /// <summary>Loads the retained first-sync review items for a pair.</summary>
    Task<IReadOnlyList<SyncBootstrapReviewItem>> LoadBootstrapReviewAsync(string pairId, string? search = null, string? group = null, string? itemKind = null, string? fileType = null, CancellationToken cancellationToken = default, string? reviewedLocalScanId = null, string? reviewedRemoteScanId = null);

    /// <summary>Loads a page of the first-sync review for a pair.</summary>
    Task<SyncBootstrapReviewPage> LoadBootstrapReviewPageAsync(string pairId, int limit = 250, int offset = 0, string? search = null, string? group = null, string? itemKind = null, string? fileType = null, CancellationToken cancellationToken = default, string? reviewedLocalScanId = null, string? reviewedRemoteScanId = null);

    /// <summary>Refreshes the first-sync inventory for a pair.</summary>
    Task RefreshBootstrapInventoryAsync(string pairId, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);

    /// <summary>Runs an approved first-sync action plan.</summary>
    Task<SyncBootstrapActionResult> RunBootstrapActionPlanAsync(SyncBootstrapActionPlan plan, Func<SyncAuthorityLiveAction, Task>? onAction = null, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null, string? retryOfRunId = null, long? expectedPolicyRevision = null, Action<string>? onPreparationProgress = null);

    /// <summary>Loads the retained first-sync problems.</summary>
    Task<IReadOnlyList<SyncFirstSyncProblem>> LoadRetainedFirstSyncProblemsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the retained command problems and the last successful run start per pair.</summary>
    Task<(IReadOnlyList<SyncCommandProblem> Problems, IReadOnlyDictionary<string, DateTimeOffset> SuccessfulRunStartedUtcByPair)> LoadRetainedCommandProblemProjectionAsync(CancellationToken cancellationToken = default);

    /// <summary>Repairs completed copy-moves for a pair.</summary>
    Task<SyncCompletedCopyMoveRepairResult> RepairCompletedCopyMovesAsync(string pairId, Action<string>? progress = null, CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null);
}
