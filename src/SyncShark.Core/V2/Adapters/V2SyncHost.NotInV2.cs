namespace Massdex.SyncShark.V2;

// Every "not in core v2 yet" answer lives here: the E members return a neutral empty result and the X members
// throw the one message. The file is a table of one-line bodies (over the ~300-line trigger, which is a review
// note for a file that is one member per line rather than a second job).
public sealed partial class V2SyncHost
{
    // ---- Run host queries not in v2 yet (E): neutral empty answers; never the "not in v2" error. ----

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncAuthorityQueueProjection>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionPageAsync(int limit, int offset,
        string? search, string? filter, CancellationToken cancellationToken = default, string? pairId = null,
        string? itemType = null, DateTimeOffset? sinceUtc = null, string? connectionId = null) =>
        Task.FromResult<IReadOnlyList<SyncAuthorityQueueProjection>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncAuthorityDatabase.SyncInterruptedPublicationIncident>>
        InterruptedPublicationIncidentsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncAuthorityDatabase.SyncInterruptedPublicationIncident>>([]);

    /// <inheritdoc />
    public Task AppendDiagnosticActivityAsync(SyncActivityEvent item, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // ---- Pair, first-sync, storage and queue queries not in v2 yet (E). ----

    /// <inheritdoc />
    public Task<SyncFolderSelectionPage> LoadFolderSelectionPageAsync(string pairId, int limit = 250, int offset = 0,
        string? search = null, string? filter = null, bool refreshIndex = false,
        CancellationToken cancellationToken = default) =>
        // A valid but empty page: the window reads Page.Rollup and Page.Folders on the pair-preview path.
        Task.FromResult(new SyncFolderSelectionPage(pairId, [], offset, 0, 0, false, 0, 0, 0, 0,
            new SyncFolderRollup(0, 0, 0, 0, 0, 0)));

    /// <inheritdoc />
    public Task<SyncStorageOverview> LoadStorageOverviewAsync(string pairId, string connectionId, int offset = 0,
        string? search = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        var pair = _store.Pair(pairId) ?? throw new InvalidDataException("This folder pair is no longer available.");
        return Task.FromResult(new SyncStorageOverview(Mapped(pair), null, 0, 0, null));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncBootstrapReviewItem>> LoadBootstrapReviewAsync(string pairId, string? search = null,
        string? group = null, string? itemKind = null, string? fileType = null,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null) => Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>([]);

    /// <inheritdoc />
    public Task<SyncBootstrapReviewPage> LoadBootstrapReviewPageAsync(string pairId, int limit = 250, int offset = 0,
        string? search = null, string? group = null, string? itemKind = null, string? fileType = null,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null) => Task.FromResult(new SyncBootstrapReviewPage([], offset, 0, 0, 0, 0, 0,
        false, false, null, null));

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncFirstSyncProblem>> LoadRetainedFirstSyncProblemsAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SyncFirstSyncProblem>>([]);

    /// <inheritdoc />
    public Task<(IReadOnlyList<SyncCommandProblem> Problems, IReadOnlyDictionary<string, DateTimeOffset> SuccessfulRunStartedUtcByPair)>
        LoadRetainedCommandProblemProjectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult((Problems: (IReadOnlyList<SyncCommandProblem>)[], SuccessfulRunStartedUtcByPair:
            (IReadOnlyDictionary<string, DateTimeOffset>)new Dictionary<string, DateTimeOffset>()));

    // ---- Activity and run-retry queries not in v2 yet (E); retained run history is in V2SyncHost.RunHistory.cs. ----

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadActivityProjectionPageAsync(int limit, int offset,
        string? search, string? filter, string? itemType = null, string? pairId = null, DateTimeOffset? sinceUtc = null,
        CancellationToken cancellationToken = default, string? connectionId = null, SyncActivityQuery? query = null) =>
        Task.FromResult<IReadOnlyList<SyncAuthorityActivityProjection>>([]);

    /// <inheritdoc />
    public Task<SyncActivityRetirementResult?> MaintainOrdinaryActivityHistoryAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<SyncActivityRetirementResult?>(null);

    /// <inheritdoc />
    public Task RetryPendingActivityPersistenceAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public int ActivityLogPendingCount => 0;

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadRunRetriesAsync(string runId,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SyncAuthorityRunHistory>>([]);

    /// <inheritdoc />
    public Task<SyncActivityRetentionWindow> LoadActivityRetentionWindowAsync(
        CancellationToken cancellationToken = default, string? runId = null) =>
        Task.FromResult(new SyncActivityRetentionWindow(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, false, false));

    /// <inheritdoc />
    public Task<long> LoadActivitySequenceAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);

    /// <inheritdoc />
    public Task<DateTimeOffset?> LoadLastSuccessfulSyncUtcAsync(CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? allowedPairIds = null) => Task.FromResult<DateTimeOffset?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRewindSession>> LoadRewindSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncRewindSession>>([]);

    /// <inheritdoc />
    public Task<SyncStorageInventoryPage> LoadStorageInventoryAsync(string? accountId = null, string? pairId = null,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyStoragePage(pairId ?? accountId ?? string.Empty));

    /// <inheritdoc />
    public Task<SyncStorageInventoryPage> InspectStorageRootAsync(string root, string? pairId = null,
        string? accountId = null, int limit = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyStoragePage(root));

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncExplorerItemStatus>> LoadExplorerStatusPageAsync(string pairId,
        string? afterPath = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncExplorerItemStatus>>([]);

    // ---- Commands not in v2 yet (X): throw the one not-in-v2 message; the UI shows its Message. ----

    /// <inheritdoc />
    public Task<SyncDiscoveryCheckResult> CheckFastDiscoveryAsync(string pairId,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("discoveryCheck");

    /// <inheritdoc />
    public Task<bool> RecoverInterruptedPublicationAsync(string pairId, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("interruptedPublication");

    /// <inheritdoc />
    public Task<SyncPairSet> RetirePairAsync(string pairId, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("retirePair");

    /// <inheritdoc />
    public Task<SyncPairBindingResult> BindPairAsync(SyncPairBindingRequest request,
        CancellationToken cancellationToken = default, Action<string>? progress = null) =>
        throw EdgeMessages.NotInV2("bindPair");

    /// <inheritdoc />
    public Task<SyncPairBindingResult> BindEndpointPairAsync(SyncPair draft, string? replacingPairId = null,
        long? expectedPolicyRevision = null, CancellationToken cancellationToken = default,
        DropboxFolderEntry? recoveryFolder = null, Action<string>? progress = null) =>
        throw EdgeMessages.NotInV2("bindPair");

    /// <inheritdoc />
    public Task<SyncPairDraftPreview> LoadPairDraftPreviewAsync(string localBrowseRoot,
        DropboxFolderEntry dropboxBrowseRoot, CancellationToken cancellationToken = default, string? connectionId = null) =>
        throw EdgeMessages.NotInV2("pairPreview");

    /// <inheritdoc />
    public Task<SyncFolderSelectionTree> LoadFolderSelectionTreeAsync(string pairId,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("folderSelection");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> ApplyPendingDirectoryStorageAsync(string pairId,
        CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null) =>
        throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> ChangeManagedStorageBatchAsync(string pairId, string connectionId,
        IReadOnlyList<string>? folderPaths, SyncDirectoryStorage? storage, long expectedPolicyRevision,
        CancellationToken cancellationToken = default, bool applyExistingRootItems = false,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> ResetManagedFileStorageAsync(string pairId, string connectionId,
        SyncFileStorageRule expectedRule, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> SetPathAvailabilityAsync(string pairId, string relativePath,
        bool keepOnDevice, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> ChangeManagedStorageAsync(string pairId, string connectionId,
        string? folderPath, SyncDirectoryStorage? storage, long expectedPolicyRevision,
        CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null) =>
        throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> ChangeManagedRootStorageAsync(string pairId, string connectionId,
        SyncDirectoryStorage storage, long expectedPolicyRevision, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncCloudAvailabilityResult> ChangeManagedFileStorageAsync(string pairId, string connectionId,
        string relativePath, SyncDirectoryStorage storage, long expectedPolicyRevision,
        CancellationToken cancellationToken = default, Func<SyncAuthorityBatchProgress, Task>? onProgress = null) =>
        throw EdgeMessages.NotInV2("onlineOnlyStorage");

    /// <inheritdoc />
    public Task<SyncIgnorePreview> PreviewIgnoreRulesAsync(string pairId, IReadOnlyList<SyncIgnoreRule> rules,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("ignorePreview");

    /// <inheritdoc />
    public Task<DropboxFolderEntry> EnsureRecommendedRecoveryFolderAsync(CancellationToken cancellationToken = default,
        string? connectionId = null) => throw EdgeMessages.NotInV2("recoveryFolder");

    /// <inheritdoc />
    public Task RefreshBootstrapInventoryAsync(string pairId, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => throw EdgeMessages.NotInV2("firstSyncReview");

    /// <inheritdoc />
    public Task<SyncBootstrapActionResult> RunBootstrapActionPlanAsync(SyncBootstrapActionPlan plan,
        Func<SyncAuthorityLiveAction, Task>? onAction = null, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, string? retryOfRunId = null,
        long? expectedPolicyRevision = null, Action<string>? onPreparationProgress = null) =>
        throw EdgeMessages.NotInV2("firstSyncReview");

    /// <inheritdoc />
    public Task<SyncCompletedCopyMoveRepairResult> RepairCompletedCopyMovesAsync(string pairId,
        Action<string>? progress = null, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null) => throw EdgeMessages.NotInV2("copyMoveRepair");

    /// <inheritdoc />
    public Task<SyncRunRetryPlan> LoadRunRetryAsync(string runId, CancellationToken cancellationToken = default,
        string? selectedPairId = null, Action<string>? onReadProgress = null) => throw EdgeMessages.NotInV2("runRetry");

    /// <inheritdoc />
    public Task<SyncBootstrapActionResult> RetryRunAsync(string runId, IReadOnlyCollection<string>? selectedPaths,
        string mode, Func<SyncAuthorityBatchProgress, Task>? onProgress = null,
        CancellationToken cancellationToken = default, string? selectedPairId = null,
        Action<string>? onPreparationProgress = null) => throw EdgeMessages.NotInV2("runRetry");

    /// <inheritdoc />
    public Task<SyncAuthorityBatchResult> VerifyRunOutcomesAsync(string runId,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, CancellationToken cancellationToken = default,
        Action<string>? onReadProgress = null) => throw EdgeMessages.NotInV2("runRetry");

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncRecoveryCatalogItem>> LoadDropboxRevisionCatalogAsync(string pairId,
        string relativePath, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("recoveryRestore");

    /// <inheritdoc />
    public Task<SyncRecoveryEffectPreview> PreviewRecoveryAsync(SyncRecoveryCatalogItem item, bool restoreAsCopy,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("recoveryRestore");

    /// <inheritdoc />
    public Task<SyncRecoveryRestoreResult> RestoreRecoveryAsync(SyncRecoveryCatalogItem item, bool restoreAsCopy,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("recoveryRestore");

    /// <inheritdoc />
    public Task<SyncRecoveryCleanupResult> RemoveRecoveryCopyAsync(SyncRecoveryCatalogItem item,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("recoveryRestore");

    /// <inheritdoc />
    public Task<SyncRewindHandoff> PrepareRewindAsync(string pairId, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("rewind");

    /// <inheritdoc />
    public Task<SyncRewindSession> PreviewRewindImportAsync(string pairId,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("rewind");

    /// <inheritdoc />
    public Task<SyncRewindApplyResult> ApplyRewindImportAsync(string pairId,
        SyncRewindApplyConfirmation confirmation, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("rewind");

    /// <inheritdoc />
    public Task<SyncRewindSession> ResumeAfterRewindAsync(string pairId, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("rewind");

    // ---- Pair-scoped sharing not in v2 yet (X): the v2 pair ids never reach the v1 pair store; the account-only
    // members (CreateAccountSharingClientAsync, RevokeSharedLinksAsync) still forward. ----

    /// <inheritdoc />
    public Task<DropboxSharingItem> GetSharingItemAsync(string pairId, string relativePath,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("sharing");

    /// <inheritdoc />
    public Task<DropboxSharedLink> CreateSharedLinkAsync(string pairId, DropboxSharingItem item,
        DropboxLinkAudience audience, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("sharing");

    /// <inheritdoc />
    public Task<DropboxSharedLink> ChangeSharedLinkAudienceAsync(string pairId, DropboxSharingItem item, string url,
        DropboxLinkAudience audience, CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("sharing");

    /// <inheritdoc />
    public Task RevokeSharedLinkAsync(string pairId, DropboxSharingItem item, string url,
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("sharing");

    /// <inheritdoc />
    public Task<GoogleDriveResolvedWebLocation> ResolveGoogleDriveWebLocationAsync(string pairId,
        string expectedAccountId, string relativePath = ".", CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("sharing");

    /// <inheritdoc />
    public Task<GoogleDriveResolvedWebLocation> ResolveGoogleDriveEndpointWebLocationAsync(string pairId,
        SyncPairSide side, SyncEndpointContext expectedContext, string relativePath = ".",
        CancellationToken cancellationToken = default) => throw EdgeMessages.NotInV2("sharing");

    /// <inheritdoc />
    public Task<string> GetGoogleDriveWebLocationAsync(string pairId, string expectedAccountId,
        string relativePath = ".", CancellationToken cancellationToken = default) =>
        throw EdgeMessages.NotInV2("sharing");

    private SyncStorageInventoryPage EmptyStoragePage(string scope) =>
        new([], false, 0, _time.GetUtcNow(), scope, EdgeMessages.NotInV2Text("storageInventory"));
}
