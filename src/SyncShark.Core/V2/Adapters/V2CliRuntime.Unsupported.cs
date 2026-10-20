namespace Massdex.SyncShark.V2;

// Every command core v2 does not answer yet reports one honest refusal and never touches state.
public sealed partial class V2CliRuntime
{
    private static SyncCliUnsupportedException NotInV2() =>
        new("This command is not in core v2 yet. Run it without --core v2.");

    /// <inheritdoc />
    public Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
        CancellationToken cancellationToken, SyncActivityQuery? query = null, string? search = null,
        string? filter = null, string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken) => LoadHostSettingsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<object> ExecuteAppActionAsync(SyncCliInvocation invocation, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<object> RunBatchAsync(string selection, Action<string> progress, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliAccountDetailData> LoadAccountAsync(string accountId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliAccountCheckData> CheckAccountAsync(string? accountId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliAccountActionData> ConnectAccountAsync(bool reauthorize, string? accountId,
        Action<string> progress, CancellationToken cancellationToken, string provider = SyncProviders.Dropbox) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliAccountActionData> DisconnectAccountAsync(string accountId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliPendingAuthorizationData> LoadPendingAuthorizationAsync(CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> CancelPendingAuthorizationAsync(string attemptId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliPairData> SelectPairAsync(string pairId, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliPairData> RecoverRetainedControlAsync(string pairId, string sourceDatabase, string sourceSha256,
        string localRoot, string? draftJson, Action<string> progress, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<bool> RecoverInterruptedPublicationAsync(string pairId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliQueueListData> LoadQueueAsync(string? accountId, string? pairId, int limit, int offset,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliAttentionData> LoadAttentionAsync(CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliRunListData> LoadRunsAsync(int limit, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliConflictDetailData> LoadConflictAsync(string pairId, string relativePath, string? accountId,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliRecoveryActionData> RestoreRecoveryAsync(string pairId, string key, bool asCopy,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliRecoveryActionData> CleanupRecoveryAsync(string pairId, string key,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliScanData> ScanAsync(string pairId, Action<string> progress, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCompletedCopyMoveRepairResult> RepairCompletedCopyMovesAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliPairData> ApproveFirstSyncAsync(string pairId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> ExitApplicationAsync(CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<object> RunRetryAsync(string runId, string? selection, string mode, bool preview,
        Action<string> progress, CancellationToken cancellationToken, string? pairId = null) => throw NotInV2();

    /// <inheritdoc />
    public Task<object> VerifyRunAsync(string runId, Action<string> progress, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<object> FollowActivityAsync(SyncCliInvocation invocation, Action<string> progress,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public async Task<SyncCliControlData> SetRunPauseAsync(bool paused, CancellationToken cancellationToken)
    {
        await SetPauseAsync(PauseScopes.Global, null, paused, null, paused, cancellationToken);
        if (paused) await _run.StopAsync();
        return new SyncCliControlData(paused ? "paused" : "resumed", true, "Global automatic syncing preference saved; a pause reaches the active run's safe point.");
    }

    /// <inheritdoc />
    public Task<SyncCliControlData> CancelRunAsync(string runId, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliPauseData> SetPauseAsync(string scope, string? target, bool paused, int? minutes,
        bool untilResumed, CancellationToken cancellationToken, DateTimeOffset? untilUtc = null,
        bool untilTomorrow = false, string? timeZoneId = null) =>
        SaveHostPauseAsync(scope, target, paused, minutes, untilResumed, cancellationToken, untilUtc, untilTomorrow, timeZoneId);

    /// <inheritdoc />
    public Task<SyncCliSettingsData> SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken) =>
        SaveHostSettingsAsync(settingsJson, cancellationToken);

    /// <inheritdoc />
    public Task<SyncCliFolderTreeData> LoadFoldersAsync(string pairId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<object> CreateLocalFolderAsync(string parentPath, string name, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path, string availability,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliUpdateData> UpdatesAsync(bool download, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> UpdatesApplyAsync(string version, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> RewindCheckpointAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> RewindPreviewAsync(string pairId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> RewindApplyAsync(string pairId, SyncRewindApplyConfirmation confirmation,
        Action<string> progress, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> RewindResumeAsync(string pairId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliRemoteFolderListData> LoadRemoteFoldersAsync(string accountId, string? parentPath,
        string? search, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliPairData> EditPairAsync(string pairId, string? name, int? priority, bool? enabled,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSharingListData> LoadSharingAsync(string accountId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSharingListData> LoadSharingAsync(string accountId, string? namespaceId, int limit, int offset,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSharedLinkData> ChangeSharedLinkAsync(string accountId, string? namespaceId, string url,
        string changeJson, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestMappingData> LoadFileRequestsAsync(string accountId, string? namespaceId, int limit,
        int offset, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestData> EditFileRequestAsync(string accountId, string? namespaceId, string id,
        string changeJson, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSharedLinkData> CreateSharedLinkAsync(string accountId, string path, string audience,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSharedLinkData> RevokeSharedLinkAsync(string accountId, string url,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliSharedLinkData> RevokeSharedLinkAsync(string accountId, string? namespaceId, string url,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<IReadOnlyList<SharedLinkRevokeResult>> RevokeSharedLinksAsync(string selectionJson,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestMappingData> LoadFileRequestsAsync(string accountId,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestData> CreateFileRequestAsync(string accountId, string title, string destination,
        string? description, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestData> CloseFileRequestAsync(string accountId, string id,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestData> CloseFileRequestAsync(string accountId, string? namespaceId, string id,
        CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliFileRequestData> CreateFileRequestAsync(string accountId, string? namespaceId, string title,
        string destination, string? description, CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliDiscoveryStatusData> LoadDiscoveryStatusAsync(CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliDiscoveryStatusData> CheckDiscoveryAsync(string pairId, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> ConfigureDiscoveryAsync(string mode, CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliExplorerStatusData> LoadExplorerStatusAsync(CancellationToken cancellationToken) =>
        throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliControlData> ShowApplicationAsync(CancellationToken cancellationToken) => throw NotInV2();

    /// <inheritdoc />
    public Task<SyncCliExactFileUploadData> UploadExactFileAsync(SyncExactFileUploadRequest request,
        Action<string> progress, CancellationToken cancellationToken) => throw NotInV2();
}
