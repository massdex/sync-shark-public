using System.Runtime.Versioning;
using System.Text.Json;

namespace Massdex.SyncShark;

// One pair removal's archived evidence: the verified archive database, its manifest and every archived
// pair-scoped table count. The roots whose rows are retained are named so the caller can see what outlived
// the pair.
public sealed record SyncCliPairRemovalData(string PairId, string ArchivePath, string ManifestPath,
    IReadOnlyDictionary<string, long> RowCounts, IReadOnlyList<string> OrphanedRoots, long UnfinishedEffects)
{
    /// <summary>Recovery payload files stay on disk; their catalog rows move to the archive, so after removal
    /// they are discoverable only from the archive database.</summary>
    public string Warning { get; init; } = "";
}

// Every command the CLI can answer. The implementation exists once, over an authority host, so the
// direct (lock-owning) mode and the running-application mode cannot diverge in semantics, text, JSON,
// effect rules or exit codes.
public interface ISyncCliOperations : ISyncCliRuntime
{
    Task<object> ExecuteAppActionAsync(SyncCliInvocation invocation, CancellationToken cancellationToken);
    Task<object> ExecuteSurfaceAsync(SyncCliInvocation invocation, CancellationToken cancellationToken);
    Task<object> ExecuteSurfaceAsync(SyncCliInvocation invocation, CancellationToken cancellationToken,
        Action<string>? progress) => ExecuteSurfaceAsync(invocation, cancellationToken);
    Task<object> RunBatchAsync(string selection, Action<string> progress, CancellationToken cancellationToken);
    string Mode { get; }
    string ProductRoot { get; }
    bool AuthorityStateExists { get; }
    bool RunActive { get; }
    bool RunPaused { get; }
    Task<SyncCliAccountDetailData> LoadAccountAsync(string accountId, CancellationToken cancellationToken);
    Task<SyncCliAccountCheckData> CheckAccountAsync(string? accountId, CancellationToken cancellationToken);
    Task<SyncCliAccountActionData> ConnectAccountAsync(bool reauthorize, string? accountId, Action<string> progress,
        CancellationToken cancellationToken, string provider = SyncProviders.Dropbox);
    Task<SyncCliAccountActionData> DisconnectAccountAsync(string accountId, CancellationToken cancellationToken);
    Task<SyncCliPendingAuthorizationData> LoadPendingAuthorizationAsync(CancellationToken cancellationToken);
    Task<SyncCliControlData> CancelPendingAuthorizationAsync(string attemptId, CancellationToken cancellationToken);
    Task<SyncCliPairData> SetPairEnabledAsync(string pairId, bool enabled, CancellationToken cancellationToken);
    Task<SyncCliPairData> SetPairEnabledAsync(string pairId, bool enabled, CancellationToken cancellationToken,
        Action<string>? progress) => SetPairEnabledAsync(pairId, enabled, cancellationToken);
    Task<SyncCliPairData> SelectPairAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliPairData> RetirePairAsync(string pairId, CancellationToken cancellationToken);
    /// <summary>Archives every pair-scoped row, then removes the exact disabled pair. A runtime without the
    /// archival store refuses without touching product state.</summary>
    Task<SyncCliPairRemovalData> RemovePairAsync(string pairId, string archiveDirectory, bool dropUnfinished,
        CancellationToken cancellationToken) => throw new SyncCliUnsupportedException(
            "Pair removal needs the core v2 SQLite store, which this runtime does not provide. Nothing was changed.");
    /// <summary>Clears the pair's stored scan cursors for the chosen side(s), so its next run scans them in full.</summary>
    Task<SyncCliRescanData> RescanPairAsync(string pairId, string? side, CancellationToken cancellationToken);
    Task<SyncCliPairData> RecoverRetainedControlAsync(string pairId, string sourceDatabase, string sourceSha256,
        string localRoot, string? draftJson, Action<string> progress, CancellationToken cancellationToken);
    /// <summary>root723: the same shared recovery the Host exposes, reachable through the ordinary CLI surface.</summary>
    Task<bool> RecoverInterruptedPublicationAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliQueueListData> LoadQueueAsync(string? accountId, string? pairId, int limit, int offset,
        CancellationToken cancellationToken);
    Task<SyncCliAttentionData> LoadAttentionAsync(CancellationToken cancellationToken);
    Task<SyncCliRunListData> LoadRunsAsync(int limit, CancellationToken cancellationToken);
    Task<SyncCliConflictListData> LoadConflictsAsync(string? pairId, string? accountId, string? search, int limit,
        int offset, CancellationToken cancellationToken);
    /// <summary>Open needs-decision notices of one pair, each with both sides' state and a version token (decision 64).</summary>
    Task<SyncCliDecisionListData> LoadDecisionsAsync(string pairId, int limit, int offset,
        CancellationToken cancellationToken);
    /// <summary>Applies the chosen side on one open needs-decision path and reports whether its notice closed.</summary>
    Task<SyncCliDecisionResolveData> ResolveDecisionAsync(string pairId, string relativePath, string keep,
        string token, Action<string> progress, CancellationToken cancellationToken);
    Task<SyncCliConflictDetailData> LoadConflictAsync(string pairId, string relativePath, string? accountId,
        CancellationToken cancellationToken);
    Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice, string? conflictId,
        long? generation, string? accountId, CancellationToken cancellationToken);
    Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice, string? conflictId,
        long? generation, string? accountId, CancellationToken cancellationToken, Action<string>? progress) =>
        ResolveConflictAsync(pairId, relativePath, choice, conflictId, generation, accountId, cancellationToken);
    Task<SyncCliRecoveryListData> LoadRecoveryAsync(string pairId, string? search, int limit,
        CancellationToken cancellationToken);
    Task<SyncCliRecoveryActionData> RestoreRecoveryAsync(string pairId, string key, bool asCopy,
        CancellationToken cancellationToken);
    Task<SyncCliRecoveryActionData> CleanupRecoveryAsync(string pairId, string key,
        CancellationToken cancellationToken);
    Task<SyncCliScanData> ScanAsync(string pairId, Action<string> progress, CancellationToken cancellationToken);
    Task<SyncCliPreparationData> PrepareFirstSyncAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken, string? choicesJson = null);
    Task<SyncCompletedCopyMoveRepairResult> RepairCompletedCopyMovesAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken) => throw new NotSupportedException("Completed-copy move repair is unavailable in this runtime.");
    Task<SyncCliPairData> ApproveFirstSyncAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliFirstSyncResultData> ApplyFirstSyncAsync(string pairId, string? choicesJson,
        bool allowRecommended, long? expectedPolicyRevision, Action<string> progress,
        CancellationToken cancellationToken, string? expectedLocalScanId = null);
    Task<SyncCliControlData> ExitApplicationAsync(CancellationToken cancellationToken);
    Task<object> RunRetryAsync(string runId, string? selection, string mode, bool preview,
        Action<string> progress, CancellationToken cancellationToken, string? pairId = null);
    Task<object> VerifyRunAsync(string runId, Action<string> progress, CancellationToken cancellationToken);
    Task<object> FollowActivityAsync(SyncCliInvocation invocation, Action<string> progress, CancellationToken cancellationToken);
    Task<SyncCliControlData> SetRunPauseAsync(bool paused, CancellationToken cancellationToken);
    Task<SyncCliControlData> CancelRunAsync(string runId, CancellationToken cancellationToken);
    Task<SyncCliPauseData> SetPauseAsync(string scope, string? target, bool paused, int? minutes,
        bool untilResumed, CancellationToken cancellationToken, DateTimeOffset? untilUtc = null,
        bool untilTomorrow = false, string? timeZoneId = null);
    Task<SyncCliSettingsData> SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken);
    Task<SyncCliIgnoreListData> LoadIgnoresAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliIgnoreListData> SaveIgnoresAsync(string pairId, string rules, CancellationToken cancellationToken);
    Task<SyncCliFolderTreeData> LoadFoldersAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliSelectionData> SetSelectionAsync(string pairId, string? path, string selection,
        CancellationToken cancellationToken);
    Task<SyncCliIncludePreviewData> PreviewIncludeAsync(string pairId, string path,
        CancellationToken cancellationToken) => throw new SyncCliUsageException(
            "Folder include preview requires core v2. No selection or files were changed.");
    Task<object> CreateLocalFolderAsync(string parentPath, string name, CancellationToken cancellationToken);
    Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path, string availability,
        CancellationToken cancellationToken);
    Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path, string availability,
        CancellationToken cancellationToken, Action<string>? progress) =>
        SetAvailabilityAsync(pairId, path, availability, cancellationToken);
    Task<SyncCliUpdateData> UpdatesAsync(bool download, CancellationToken cancellationToken);
    Task<SyncCliControlData> UpdatesApplyAsync(string version, CancellationToken cancellationToken);
    Task<SyncCliControlData> RewindCheckpointAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken);
    Task<SyncCliControlData> RewindPreviewAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliControlData> RewindApplyAsync(string pairId, SyncRewindApplyConfirmation confirmation, Action<string> progress,
        CancellationToken cancellationToken);
    Task<SyncCliControlData> RewindResumeAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliRemoteFolderListData> LoadRemoteFoldersAsync(string accountId, string? parentPath, string? search,
        CancellationToken cancellationToken);
    Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot, string? name,
        int? priority, bool enabled, CancellationToken cancellationToken);
    Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot, string? name,
        int? priority, bool enabled, CancellationToken cancellationToken, Action<string>? progress) =>
        CreatePairAsync(accountId, remotePath, localRoot, name, priority, enabled, cancellationToken);
    // Exclusions at creation are core v2 only: an operations surface without them refuses any --exclude.
    Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot, string? name,
        int? priority, bool enabled, IReadOnlyList<string> excluded, CancellationToken cancellationToken,
        Action<string>? progress) => excluded.Count > 0
        ? throw new SyncCliUsageException("The option --exclude is not supported by 'pairs create' in this " +
            "engine. Nothing was created.")
        : CreatePairAsync(accountId, remotePath, localRoot, name, priority, enabled, cancellationToken, progress);
    Task<SyncCliPairData> EditPairAsync(string pairId, string? name, int? priority, bool? enabled,
        CancellationToken cancellationToken);
    Task<SyncCliPairData> EditPairAsync(string pairId, string? name, int? priority, bool? enabled,
        CancellationToken cancellationToken, Action<string>? progress) =>
        EditPairAsync(pairId, name, priority, enabled, cancellationToken);
    Task<SyncCliSharingListData> LoadSharingAsync(string accountId, CancellationToken cancellationToken);
    Task<SyncCliSharingListData> LoadSharingAsync(string accountId, string? namespaceId, int limit, int offset,
        CancellationToken cancellationToken);
    Task<SyncCliSharedLinkData> ChangeSharedLinkAsync(string accountId, string? namespaceId, string url,
        string changeJson, CancellationToken cancellationToken);
    Task<SyncCliFileRequestMappingData> LoadFileRequestsAsync(string accountId, string? namespaceId, int limit,
        int offset, CancellationToken cancellationToken);
    Task<SyncCliFileRequestData> EditFileRequestAsync(string accountId, string? namespaceId, string id,
        string changeJson, CancellationToken cancellationToken);
    Task<SyncCliSharedLinkData> CreateSharedLinkAsync(string accountId, string path, string audience,
        CancellationToken cancellationToken);
    Task<SyncCliSharedLinkData> RevokeSharedLinkAsync(string accountId, string url,
        CancellationToken cancellationToken);
    Task<SyncCliSharedLinkData> RevokeSharedLinkAsync(string accountId, string? namespaceId, string url,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SharedLinkRevokeResult>> RevokeSharedLinksAsync(string selectionJson, CancellationToken cancellationToken);
    Task<SyncCliFileRequestMappingData> LoadFileRequestsAsync(string accountId,
        CancellationToken cancellationToken);
    Task<SyncCliFileRequestData> CreateFileRequestAsync(string accountId, string title, string destination,
        string? description, CancellationToken cancellationToken);
    Task<SyncCliFileRequestData> CloseFileRequestAsync(string accountId, string id,
        CancellationToken cancellationToken);
    Task<SyncCliFileRequestData> CloseFileRequestAsync(string accountId, string? namespaceId, string id,
        CancellationToken cancellationToken);
    Task<SyncCliFileRequestData> CreateFileRequestAsync(string accountId, string? namespaceId, string title,
        string destination, string? description, CancellationToken cancellationToken);
    Task<SyncCliDiscoveryStatusData> LoadDiscoveryStatusAsync(CancellationToken cancellationToken);
    Task<SyncCliDiscoveryStatusData> CheckDiscoveryAsync(string pairId, CancellationToken cancellationToken);
    Task<SyncCliControlData> ConfigureDiscoveryAsync(string mode, CancellationToken cancellationToken);
    Task<SyncCliExplorerStatusData> LoadExplorerStatusAsync(CancellationToken cancellationToken);
    Task<SyncCliControlData> ShowApplicationAsync(CancellationToken cancellationToken);
    // The one standalone exact-file Dropbox upload: an explicit account, source identity, destination and
    // confirmation, with no sync pair, inventory, plan, settings or engine change.
    Task<SyncCliExactFileUploadData> UploadExactFileAsync(SyncExactFileUploadRequest request,
        Action<string> progress, CancellationToken cancellationToken);
}

public abstract partial class SyncCliOperationsBase : ISyncCliOperations
{
    public virtual Task<object> ExecuteAppActionAsync(SyncCliInvocation invocation, CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException("This action requires the running application's guarded adapter. No action was taken.");
    protected abstract SyncAuthorityRuntimeHost Host { get; }
    protected abstract PilotStateAuthority StateAuthority { get; }
    /// <summary>
    /// Fixture transport seam for the endpoint-authoring checks: the account-bound folder browser for
    /// one exact connection id, or null (every production process) to use the authority host's own
    /// provider browser. A fixture supplies folder inventory only; the provider match, the verified
    /// namespace and the exact path match all stay in production code.
    /// </summary>
    internal static Func<string, CancellationToken, Task<IDropboxFolderBrowserService>>?
        EndpointBrowserFactoryForTest { get; set; }
    protected virtual bool ApplicationOwnsRuntime => true;
    protected virtual bool HostRunActive => Host.HasActiveRun;
    protected virtual bool HostRunPaused => Host.ActiveRun?.Paused == true;
    protected virtual Task ShowApplicationCoreAsync(CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException(
            "No Sync Shark application is running for this user, so there is no window to show.");
    protected virtual Task ExitApplicationCoreAsync(CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException(
            "No Sync Shark application is running for this user, so there is no application to exit.");

    public virtual Task<SyncCliControlData> ConfigureDiscoveryAsync(string mode, CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException("Discovery setup requires the running signed installed app and Windows administrator approval. Nothing was launched.");

    public virtual Task<SyncCliRescanData> RescanPairAsync(string pairId, string? side,
        CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException(
            "Forcing a pair's next run to rescan in full is not supported by this core. Run it with --core v2.");

    public async Task<SyncCliControlData> ExitApplicationAsync(CancellationToken cancellationToken)
    {
        // The running application performs its own orderly shutdown, including its in-flight work
        // checkpoint. The CLI never terminates the process and never bypasses that checkpoint.
        await ExitApplicationCoreAsync(cancellationToken);
        return new SyncCliControlData("app exit", true, "The application's graceful exit checkpoint completed. " +
            "The CLI verifies owner exit after receiving this result; no process is forcibly terminated.");
    }

    private UiPreferencesStore Preferences => new(StateAuthority);
    public async Task<object> VerifyRunAsync(string runId, Action<string> progress, CancellationToken cancellationToken)
    {
        string? verificationRunId = null;
        var batch = await Host.VerifyRunOutcomesAsync(runId, entry =>
        {
            verificationRunId ??= Host.ActiveRun?.Id;
            progress(DescribeRunProgress(entry)); return Task.CompletedTask;
        }, cancellationToken, onReadProgress: progress);
        return CompactBatchReceipt(batch.Items.Select(CompactPairResult).ToArray(), verificationRunId,
            batch.Stopped, "run verify", runId);
    }

    public async Task<object> FollowActivityAsync(SyncCliInvocation invocation, Action<string> progress, CancellationToken cancellationToken)
    {
        var cursor = long.Parse(invocation.Value("--after") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var duration = TimeSpan.FromSeconds(int.Parse(invocation.Value("--seconds")!, System.Globalization.CultureInfo.InvariantCulture));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        do
        {
            var page = await LoadActivityAsync(invocation.AccountId, 200, 0, cancellationToken,
                new SyncActivityQuery(RunId: invocation.Value("--run"), AfterSequence: cursor, DetailedLog: true),
                pairId: invocation.PairId);
            foreach (var item in page.Items)
            {
                progress(JsonSerializer.Serialize(item));
                cursor = item.Sequence!.Value;
                count++;
            }
            if (started.Elapsed >= duration) break;
            if (!page.HasMore) await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        } while (true);
        return new { eventsEmitted = count, nextSequence = cursor, runId = invocation.Value("--run"),
            note = "Detailed event JSON was streamed on stderr. Continue from nextSequence; stdout is this final receipt. This read never starts or retries a sync." };
    }

    public async Task<object> RunRetryAsync(string runId, string? selection, string mode, bool preview,
        Action<string> progress, CancellationToken cancellationToken, string? pairId = null)
    {
        if (preview) return await Host.LoadRunRetryAsync(runId, cancellationToken, pairId, progress);
        var paths = selection is null ? null : JsonSerializer.Deserialize<string[]>(selection)
            ?? throw new SyncCliUsageException("--selection must be an array of exact retained file paths.");
        string? retryRunId = null;
        var result = await Host.RetryRunAsync(runId, paths, mode, entry =>
        {
            retryRunId ??= Host.ActiveRun?.Id;
            progress(DescribeRunProgress(entry)); return Task.CompletedTask;
        }, cancellationToken, pairId, progress);
        return CheckedFirstSyncResult(result, retryRunId);
    }

    public abstract string Mode { get; }
    public string ProductRoot => StateAuthority.ProductRoot;
    public bool AuthorityStateExists => Host.AuthorityStateExists;
    public bool RunActive => Host.HasActiveRun;
    public bool RunPaused => Host.ActiveRun?.Paused == true;

    public virtual async ValueTask DisposeAsync()
    {
        Host.Dispose();
        await Task.CompletedTask;
    }

    public async Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken)
    {
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        var accounts = LoadAccountRecords(pairSet);
        var attention = await LoadAttentionAsync(cancellationToken);
        var pairIds = pairSet.Pairs.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);
        var pending = await Host.LoadPendingQueueCountAsync(cancellationToken);
        DateTimeOffset? lastRun = null;
        if (Host.AuthorityStateExists)
        {
            try { lastRun = await Host.Authority.LoadLastSuccessfulSyncUtcAsync(cancellationToken); }
            catch (InvalidDataException) { lastRun = null; }
        }
        var attentionCount = attention.Items?.Count(item => pairIds.Contains(item.PairId)) ?? attention.PairIds.Count;
        return new SyncCliStatusData("Sync Shark", ProductVersion(), BuildIdentity.SourceRevision,
            ApplicationOwnsRuntime ? "running-application" : "exclusive-product-lock", ProductRoot,
            Host.AuthorityStateExists, accounts.Count, accounts,
            new SyncCliPairTallyData(pairSet.Pairs.Count, pairSet.Pairs.Count(pair => pair.Enabled),
                pairSet.Pairs.Count(pair => !pair.Enabled), attention.PairIds.Count(id => pairIds.Contains(id))),
            pending, lastRun, SyncCli.StateAccessNote, Mode, RunActive, RunPaused, attentionCount);
    }

    public async Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = LoadAccountRecords(await Host.LoadPairsAsync(cancellationToken));
        return new SyncCliAccountListData(accounts.Count, accounts);
    }

    public async Task<SyncCliAccountDetailData> LoadAccountAsync(string accountId,
        CancellationToken cancellationToken)
    {
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        var binding = RequireConnection(accountId);
        var account = LoadAccountRecords(pairSet)
            .Single(item => string.Equals(item.ConnectionId, accountId, StringComparison.Ordinal));
        return new SyncCliAccountDetailData(account, binding.RootNamespaceId ?? "", binding.HomeNamespaceId ?? "",
            binding.Scopes.Count, DropboxProviderConfiguration.HasFileRequestAccess(binding.Scopes),
            binding.ConnectedUtc, binding.LastVerifiedUtc, account.PairCount, account.IsPaused);
    }

    public async Task<SyncCliAccountCheckData> CheckAccountAsync(string? accountId,
        CancellationToken cancellationToken)
    {
        if (accountId is not null) RequireConnection(accountId);
        // The existing live identity check reads the protected credential inside the product boundary and
        // never returns it. It requires provider reachability and is therefore a live-verification item.
        var status = await Host.GetAccountStatusAsync(verifyLive: true, cancellationToken, accountId);
        return new SyncCliAccountCheckData(status.Binding?.ConnectionId ?? accountId ?? "primary",
            status.HasProtectedCredential, status.LiveVerified, status.Health, status.FailureType,
            status.DependentPairCount, status.LastSuccessfulSyncUtc);
    }

    public async Task<SyncCliAccountActionData> ConnectAccountAsync(bool reauthorize, string? accountId,
        Action<string> progress, CancellationToken cancellationToken, string provider = SyncProviders.Dropbox)
    {
        if (reauthorize)
        {
            if (string.IsNullOrWhiteSpace(accountId))
                throw new SyncCliUsageException("Account reauthorization requires one exact account id; no primary account is selected implicitly.");
            provider = RequireConnection(accountId).Provider;
        }
        provider = SyncProviders.Normalize(provider);
        if (provider == SyncProviders.GoogleDrive)
        {
            progress("Opening Google authorization. Existing accounts and pairs retain their identities.");
            var google = await Host.ConnectGoogleDriveAsync(OpenAuthorizationPageAsync, cancellationToken,
                reauthorize ? accountId : null);
            return new SyncCliAccountActionData(google.ConnectionId, reauthorize ? "reauthorize" : "connect",
                "Google account verified and saved in protected Windows custody.", LiveVerificationRequired: false);
        }
        if (provider != SyncProviders.Dropbox) throw new SyncCliUsageException("--provider must be dropbox or google-drive.");
        progress("Opening the registered Dropbox authorization page in the default browser.");
        var binding = reauthorize
            ? await Host.ReauthorizeDropboxAsync(OpenAuthorizationPageAsync, cancellationToken, accountId)
            : await Host.ConnectDropboxAsync(OpenAuthorizationPageAsync, cancellationToken, addAccount: true);
        return new SyncCliAccountActionData(binding.ConnectionId, reauthorize ? "reauthorize" : "connect",
            "The verified connection was saved with its protected credential inside Windows Credential Manager.",
            LiveVerificationRequired: true);
    }

    public async Task<SyncCliAccountActionData> DisconnectAccountAsync(string accountId,
        CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        if (binding.Provider == SyncProviders.GoogleDrive)
            await Host.DisconnectGoogleDriveAsync(binding.ConnectionId, cancellationToken);
        else await Host.DisconnectDropboxAsync(cancellationToken, binding.ConnectionId);
        return new SyncCliAccountActionData(binding.ConnectionId, "disconnect",
            "The protected credential was removed. Files, pair definitions, baselines, recovery records and " +
            "history were retained; disconnect is never deletion evidence.", LiveVerificationRequired: false);
    }

    // GCLI-G3/AUTH1: the readonly pending view and the exact-target cancellation command. Both read the
    // running application's own host state. Neither starts an application, starts an authorization,
    // contacts a provider or reads a credential to answer.
    public Task<SyncCliPendingAuthorizationData> LoadPendingAuthorizationAsync(CancellationToken cancellationToken)
    {
        var pending = Host.PendingAuthorization;
        return Task.FromResult(new SyncCliPendingAuthorizationData(pending is null ? 0 : 1, pending,
            pending is null
                ? ApplicationOwnsRuntime
                    ? "No interactive authorization attempt is waiting in this application."
                    : "No interactive authorization attempt is waiting in this process. A pending authorization " +
                      "belongs to the process that started it, so only the running application can report one."
                : "One interactive authorization attempt is waiting. It is not an account, a sync run or a sync job."));
    }

    public async Task<SyncCliControlData> CancelPendingAuthorizationAsync(string attemptId,
        CancellationToken cancellationToken)
    {
        // The exact attempt lease is captured before any asynchronous work, so a stale, wrong or finished
        // identity can never cancel the attempt that replaced it.
        var lease = Host.CaptureAuthorizationAttempt(attemptId)
            ?? throw new SyncCliTargetException("No pending authorization attempt with exact ID '" + attemptId +
                "' is waiting. No other attempt, account or sync run was touched.");
        lease.RequestCancellation();
        bool committed;
        try
        {
            committed = await lease.Terminal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (TimeoutException)
        {
            // A timeout is a request, never a completed cancellation and never a statement about accounts.
            return new SyncCliControlData("accounts cancel", true,
                "Cancellation was requested for authorization attempt '" + attemptId + "'. That attempt is " +
                "still finishing and its terminal result is not known yet.");
        }
        if (committed)
            return new SyncCliControlData("accounts cancel", false,
                "Authorization attempt '" + attemptId + "' completed before the cancellation took effect. The " +
                "verified connection was kept; no account, credential or pair was removed.");
        return new SyncCliControlData("accounts cancel", true,
            "Authorization attempt '" + attemptId + "' was cancelled. Existing accounts were kept; you can try " +
            "again now.");
    }

    private static Task OpenAuthorizationPageAsync(Uri uri)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Dropbox authorization requires Windows shell activation.");
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The authorization page must use the registered HTTPS endpoint.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken)
    {
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        var pairs = pairSet.Pairs.Select(pair => Project(pair, pairSet.SelectedPairId)).ToArray();
        return new SyncCliPairListData(pairs.Length, pairs);
    }

    public async Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken)
    {
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        return Project(RequirePair(pairSet, pairId), pairSet.SelectedPairId);
    }

    public Task<SyncCliPairData> SetPairEnabledAsync(string pairId, bool enabled,
        CancellationToken cancellationToken) => SetPairEnabledAsync(pairId, enabled, cancellationToken, null);

    public async Task<SyncCliPairData> SetPairEnabledAsync(string pairId, bool enabled,
        CancellationToken cancellationToken, Action<string>? progress)
    {
        var pairSet = await Host.SetPairEnabledAsync(pairId, enabled, cancellationToken, progress);
        return Project(RequirePair(pairSet, pairId), pairSet.SelectedPairId);
    }

    public async Task<SyncCliPairData> SelectPairAsync(string pairId, CancellationToken cancellationToken)
    {
        var pairSet = await Host.SelectPairAsync(pairId, cancellationToken);
        return Project(RequirePair(pairSet, pairId), pairSet.SelectedPairId);
    }

    public async Task<SyncCliPairData> RetirePairAsync(string pairId, CancellationToken cancellationToken)
    {
        var before = await Host.LoadPairsAsync(cancellationToken);
        var pair = before.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        if (pair is null)
        {
            // Retirement retains the definition and history. A repeated exact
            // request succeeds without replay; an unknown identity remains an error.
            pair = (await Host.Authority.LoadAllPairDefinitionsAsync(cancellationToken))
                .SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
            if (pair is null || await Host.Authority.LoadPairStateAsync(pairId, cancellationToken) != "retired")
                throw new SyncCliTargetException("No retained folder pair has that exact identity.");
            return Project(pair, before.SelectedPairId) with { IsRetired = true, IsSelected = false, Enabled = false };
        }
        var pairSet = await Host.RetirePairAsync(pairId, cancellationToken);
        if (pairSet.Pairs.Any(item => string.Equals(item.Id, pairId, StringComparison.Ordinal)))
            throw new InvalidDataException("The pair is still retained after the retirement request.");
        return Project(pair, pairSet.SelectedPairId) with { IsRetired = true, IsSelected = false, Enabled = false };
    }

    /// <summary>
    /// RC639-6: the owner-approved administrative retained-control recovery, exposed through the existing
    /// pairs family. It reads a pinned original authority database, installs ownership metadata only for the
    /// exact retained cloud pair, and then runs the same guarded authoring path as every other save. The pair
    /// is authored with the CURRENT retained draft (disabled, fresh review) and no old database or policy is
    /// imported. It is not a general backup or import wizard.
    /// </summary>
    /// <summary>root723: the shared Host interrupted-publication recovery, exposed on the CLI operation surface.</summary>
    public Task<bool> RecoverInterruptedPublicationAsync(string pairId, CancellationToken cancellationToken) =>
        Host.RecoverInterruptedPublicationAsync(pairId, cancellationToken);

    public async Task<SyncCliPairData> RecoverRetainedControlAsync(string pairId, string sourceDatabase,
        string sourceSha256, string localRoot, string? draftJson, Action<string> progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Retained-control recovery requires Windows.");
        var before = await Host.LoadPairsAsync(cancellationToken);
        var retained = before.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        // LC653-4: an unpublished initial candidate is never in the published-pair projection, so its current
        // intent is supplied as --draft; --draft is omitted only for an existing current retained definition.
        SyncPair pair;
        if (!string.IsNullOrWhiteSpace(draftJson))
        {
            pair = SyncPairStore.NormalizePair(System.Text.Json.JsonSerializer.Deserialize<SyncPair>(draftJson)
                ?? throw new SyncCliUsageException(
                    "'pairs recover-control' --draft does not carry a folder-pair definition."));
            if (!string.Equals(pair.Id, pairId, StringComparison.Ordinal))
                throw new SyncCliUsageException("--draft must carry the exact --pair identity.");
        }
        else
        {
            pair = retained ?? throw new SyncCliTargetException("No retained folder pair has that exact identity.");
        }
        if (!SyncCloudPairConfiguration.IsCloudOnly(pair) && !HasLocalCloudEndpoints(pair))
            throw new NotSupportedException(
                "Retained-control recovery authoring supports the two-endpoint cloud pair family and the " +
                "local/cloud pair family in either placement; a pair with no cloud endpoint is refused.");
        if (string.IsNullOrWhiteSpace(sourceDatabase))
            throw new SyncCliUsageException("'pairs recover-control' requires --source <original-db-path>.");
        if (sourceSha256 is null || sourceSha256.Length != 64 ||
            !sourceSha256.All(character => (character >= '0' && character <= '9') ||
                (character >= 'a' && character <= 'f')))
            throw new SyncCliUsageException(
                "'pairs recover-control' requires --source-sha256 as the exact lowercase 64-hex source hash.");
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new SyncCliUsageException("'pairs recover-control' requires --local-root <held-local-root>.");
        // The CURRENT retained draft only, and only its ownership metadata is recovered; the draft keeps its
        // own direction, deletion, privacy and storage policy and stays disabled for fresh first-sync review.
        var draft = pair with { Enabled = false, InitialReviewApproved = false };
        using var selectedRoot = PilotDirectoryLease.Open(Path.GetFullPath(localRoot));
        selectedRoot.VerifyBinding();
        var target = await Host.ResolveRetainedControlRecoveryTargetAsync(draft, selectedRoot, cancellationToken);
        var source = new SyncAuthorityDatabase.RetainedControlSource(Path.GetFullPath(sourceDatabase),
            sourceSha256, pair.Id, target.Side, target.RootBindingId);
        // LC653-3/4: initial publication of an unpublished candidate must not pass a nonexistent published
        // replacement pair or invent a policy revision; a published edit keeps its exact revision/CAS.
        string? replacingPairId = null;
        long? expectedPolicyRevision = null;
        if (retained is not null)
        {
            if (!string.IsNullOrWhiteSpace(draftJson) && pair.PolicyRevision != retained.PolicyRevision)
                throw new SyncCliUsageException(
                    "The --draft revision does not match the current retained definition; reload it before recovering.");
            replacingPairId = retained.Id;
            expectedPolicyRevision = retained.PolicyRevision;
        }
        else
        {
            var currentRevision = await Host.LoadRetainedPairRevisionAsync(pairId, cancellationToken);
            if (currentRevision < 1)
                throw new SyncCliTargetException("No current retained candidate has that exact identity.");
            if (pair.PolicyRevision != currentRevision)
                throw new SyncCliUsageException(
                    "The --draft revision is stale against the current retained candidate; reload it before recovering.");
            expectedPolicyRevision = currentRevision;
        }
        var result = await Host.RecoverRetainedControlAndAuthorAsync(draft, source, selectedRoot, replacingPairId,
            expectedPolicyRevision, cancellationToken, progress);
        var updated = result.PairSet.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, pair.Id, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The recovered pair is not retained after the recovery request.");
        return Project(updated, result.PairSet.SelectedPairId);
    }

    /// <summary>LC653-1: true when the pair names exactly one local and one cloud endpoint.</summary>
    private static bool HasLocalCloudEndpoints(SyncPair pair)
    {
        var document = pair.EndpointDocument;
        return document?.Left is not null && document.Right is not null &&
               document.Left.IsLocal != document.Right.IsLocal;
    }

    public async Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
        CancellationToken cancellationToken, SyncActivityQuery? query = null, string? search = null,
        string? filter = null, string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null)
    {
        if (accountId is not null) RequireConnection(accountId);
        if (pairId is not null) await LoadPairAsync(pairId, cancellationToken);
        var rows = await Host.LoadActivityProjectionPageAsync(limit + 1, offset, search, filter, itemType, pairId, sinceUtc,
            cancellationToken, accountId, query);
        var historyWindow = await Host.Authority.LoadActivityRetentionWindowAsync(cancellationToken, query?.RunId);
        var items = rows.Take(limit).Select(row => new SyncCliActivityItemData(row.OccurredUtc, row.PairId,
            row.RelativePath, row.Outcome, row.ItemKind, row.FileType, row.SizeBytes, row.Reason, row.LocalRoot,
            row.DropboxRoot, row.EventId, row.Sequence, row.Categories, row.Severity, row.Entity,
            row.ConnectionId, row.RunId, row.OperationId, row.EndpointContext)).ToArray();
        // LIMIT bounds rows, not their escaped nested transport size. Keep the
        // largest exact prefix; continuation advances only past emitted rows.
        return SyncCli.FitResponseItems(items, returned =>
            new SyncCliActivityListData(returned, limit, offset, returned.Length, rows.Count > returned.Length,
                query?.DetailedLog == true ? returned.Select(item => item.Sequence).DefaultIfEmpty().Max() ?? query.AfterSequence ?? 0 : null,
                query?.DetailedLog == true && query.NewestLogFirst ? returned.LastOrDefault()?.Sequence : null,
                historyWindow),
            "activity list");
    }

    public async Task<SyncCliQueueListData> LoadQueueAsync(string? accountId, string? pairId, int limit, int offset,
        CancellationToken cancellationToken)
    {
        if (accountId is not null) RequireConnection(accountId);
        if (pairId is not null) await LoadPairAsync(pairId, cancellationToken);
        var rows = await Host.LoadQueueProjectionPageAsync(limit + 1, offset, null, null, cancellationToken, pairId,
            null, null, accountId);
        var pending = await Host.LoadPendingQueueCountAsync(cancellationToken, accountId);
        var items = rows.Take(limit).Select(row => new SyncCliQueueItemData(row.OperationId, row.PairId,
            row.RelativePath, row.EnqueuedUtc, row.Priority, row.Operation, row.State, row.RecoveryRequired,
            row.ItemKind, row.FileType)).ToArray();
        return new SyncCliQueueListData(items, limit, offset, items.Length, rows.Count > limit, pending);
    }

    public async Task<SyncCliAttentionData> LoadAttentionAsync(CancellationToken cancellationToken)
    {
        var items = await LoadAttentionItemsCoreAsync(cancellationToken);
        var monitors = await Host.LoadMonitorProjectionAsync(cancellationToken);
        var pending = await Host.LoadPendingQueueCountAsync(cancellationToken);
        return new SyncCliAttentionData(items.Select(item => item.PairId).Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            monitors.Select(monitor => new SyncCliMonitorData(monitor.PairId, monitor.Kind, monitor.State,
                monitor.UpdatedUtc, monitor.PendingDirtyItems)).ToArray(), pending, items);
    }

    protected virtual async Task<IReadOnlyList<SyncCliAttentionItemData>> LoadAttentionItemsCoreAsync(
        CancellationToken cancellationToken)
    {
        var pairs = await Host.LoadPairsAsync(cancellationToken);
        var retained = await Host.LoadRetainedFirstSyncProblemsAsync(cancellationToken);
        var commands = await Host.LoadRetainedCommandProblemsAsync(cancellationToken);
        var pending = await Host.LoadPairIdsNeedingAttentionAsync(cancellationToken);
        var items = new List<SyncCliAttentionItemData>();
        foreach (var pair in pairs.Pairs)
        {
            string? missing = null;
            try
            {
                var context = SyncEndpointContext.Capture(pair);
                foreach (var side in new[] { context.Left, context.Right }.Where(side => side.IsLocal))
                    if (SyncLocalRootAvailability.Observe(side.LocalPath ?? "") == false)
                        missing = $"Local folder unavailable on the {side.Side.ToString().ToLowerInvariant()}: {side.LocalPath}. Choose another folder to repair this pair.";
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { }
            foreach (var problem in retained.Where(problem => problem.PairId == pair.Id))
                items.Add(new(pair.Id, "First sync needs attention", problem.Message + " " + problem.NextAction, true, problem.CommandId));
            foreach (var problem in commands.Where(problem => problem.PairId == pair.Id))
                items.Add(new(pair.Id, problem.Title, problem.Description, true, problem.CommandId));
            if (missing is not null) items.Add(new(pair.Id, "Local folder unavailable", missing, true));
            else if (pair.InitialReviewApproved == false)
                items.Add(new(pair.Id, "Review the First Sync", "Review the proposed changes and excluded folders.", false));
            if (pending.Contains(pair.Id) && items.All(item => item.PairId != pair.Id))
                items.Add(new(pair.Id, "Sync needs attention", "Open this pair's retained conflicts or pending operations.", true));
        }
        return items;
    }

    public async Task<SyncCliRunListData> LoadRunsAsync(int limit, CancellationToken cancellationToken)
    {
        // Only retained status fields are projected; stored request/result JSON stays in the authority.
        var runs = await Host.Authority.LoadRunHistoryAsync(limit, 0, cancellationToken);
        return new SyncCliRunListData(runs.Count, runs.Select(run => new SyncCliRunHistoryData(run.Id, run.Kind,
            run.StartedUtc, run.CompletedUtc, run.Status)).ToArray());
    }

    public async Task<SyncCliConflictListData> LoadConflictsAsync(string? pairId, string? accountId,
        string? search, int limit, int offset, CancellationToken cancellationToken)
    {
        return await LoadConflictPageAsync(pairId, accountId, search, null, limit, offset, cancellationToken);
    }

    // Decision 64's needs-decision paths live only in core v2's store; the v1 core refuses both verbs.
    public Task<SyncCliDecisionListData> LoadDecisionsAsync(string pairId, int limit, int offset,
        CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException("Listing needs-decision paths is only in core v2. " +
            "Run it with --core v2.");

    public Task<SyncCliDecisionResolveData> ResolveDecisionAsync(string pairId, string relativePath, string keep,
        string token, Action<string> progress, CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException("Resolving a needs-decision path is only in core v2. " +
            "Run it with --core v2.");

    public async Task<SyncCliConflictDetailData> LoadConflictAsync(string pairId, string relativePath,
        string? accountId, CancellationToken cancellationToken)
    {
        var page = await LoadConflictPageAsync(pairId, accountId, relativePath, null, int.MaxValue, 0, cancellationToken);
        var matches = page.Items.Where(item => string.Equals(item.PairId, pairId, StringComparison.Ordinal) &&
                                         string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            throw new SyncCliTargetException($"No open conflict in pair '{pairId}' matches '{relativePath}'. Run: " +
                "SyncShark.exe --cli conflicts list --pair " + pairId);
        if (matches.Length > 1)
            throw new SyncCliAmbiguousTargetException(
                $"More than one open conflict in pair '{pairId}' matches '{relativePath}'.");
        var content = await Host.LoadConflictContentAsync(pairId, relativePath, 8L * 1024 * 1024,
            cancellationToken);
        return new SyncCliConflictDetailData(matches[0], content.LocalBytes is { Length: > 0 },
            content.DropboxBytes is { Length: > 0 }, SyncCliSanitize.Message(content.Note),
            ReadConflictText(content.LocalBytes), ReadConflictText(content.DropboxBytes));
    }

    private static string? ReadConflictText(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > 1024 * 1024 || bytes.Contains((byte)0)) return null;
        try
        {
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ? null : text;
        }
        catch (System.Text.DecoderFallbackException) { return null; }
    }

    public async Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice,
        string? conflictId, long? generation, string? accountId, CancellationToken cancellationToken)
        => await ResolveConflictAsync(pairId, relativePath, choice, conflictId, generation, accountId,
            cancellationToken, null);

    public async Task<SyncCliRunData> ResolveConflictAsync(string pairId, string relativePath, string choice,
        string? conflictId, long? generation, string? accountId, CancellationToken cancellationToken,
        Action<string>? progress)
    {
        var pair = accountId is null ? RequirePair(await Host.LoadPairsAsync(cancellationToken), pairId)
            : await RequireBoundPairAsync(pairId, accountId, null, cancellationToken);
        if (V2.NoticeChoices.IsRetainedVersionChoice(choice.ToLowerInvariant()))
            throw new SyncCliUsageException("Use the direct core v2 CLI or the core v2 app notice buttons for retained-version actions. This legacy host changed no files.");
        if (!Enum.TryParse<SyncAuthorityConflictResolutionKind>(choice, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            throw new SyncCliUsageException("The --choice value must be one of: " +
                string.Join(", ", Enum.GetNames<SyncAuthorityConflictResolutionKind>()) + ".");
        if (conflictId is null || generation is null)
            throw new SyncCliUsageException("Resolving a conflict requires both --key <conflict-id> and " +
                "--generation <n> from 'conflicts show', so a stale decision can never be applied.");
        var result = await Host.ResolveConflictCommandAsync(pair, relativePath, kind, conflictId, generation,
            cancellationToken, entry => { progress?.Invoke(DescribeRunProgress(entry)); return Task.CompletedTask; }, progress);
        return new SyncCliRunData(pair.Id, pair.Name, result.Uploaded, result.Downloaded, result.Identical,
            result.Conflicts, result.Blocked, result.Stopped);
    }

    private static SyncCliConflictData Project(SyncConflictComparison comparison) =>
        new(comparison.ConflictId, comparison.PairId, comparison.RelativePath, comparison.Generation,
            comparison.LocalSize, comparison.DropboxSize, comparison.LocalHash, comparison.DropboxHash,
            comparison.LocalModifiedUtc, comparison.DropboxModifiedUtc, comparison.Group,
            comparison.Recommendation, comparison.Status) { Options = comparison.Options ?? [] };

    public async Task<SyncCliRecoveryListData> LoadRecoveryAsync(string pairId, string? search, int limit,
        CancellationToken cancellationToken)
    {
        return await LoadRecoveryPageAsync(pairId, search, limit, 0, false, cancellationToken);
    }

    public async Task<SyncCliRecoveryActionData> RestoreRecoveryAsync(string pairId, string key, bool asCopy,
        CancellationToken cancellationToken)
    {
        var item = await RequireRecoveryItemAsync(pairId, key, cancellationToken);
        var result = await Host.RestoreRecoveryAsync(item, asCopy, cancellationToken);
        return new SyncCliRecoveryActionData(pairId, key, asCopy ? "restore-copy" : "restore",
            $"Restored to '{result.TargetRelativePath}': {SyncCliSanitize.Message(result.Detail) ?? "restored"}");
    }

    public async Task<SyncCliRecoveryActionData> CleanupRecoveryAsync(string pairId, string key,
        CancellationToken cancellationToken)
    {
        var item = await RequireRecoveryItemAsync(pairId, key, cancellationToken);
        var result = await Host.RemoveRecoveryCopyAsync(item, cancellationToken);
        return new SyncCliRecoveryActionData(pairId, key, "cleanup",
            SyncCliSanitize.Message(result.Detail) ?? "The retained recovery copy was removed.");
    }

    private async Task<SyncRecoveryCatalogItem> RequireRecoveryItemAsync(string pairId, string key,
        CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        if (key.StartsWith("trash:", StringComparison.Ordinal))
        {
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            for (var pageNumber = 0; pageNumber < 100; pageNumber++)
            {
                var page = await Host.LoadRecoveryCatalogPageAsync(pairId, 0, cursor, true, 100, null, cancellationToken);
                var matches = page.Items.Where(item => item.Source == SyncRecoverySource.CloudTrash && item.Key == key).ToArray();
                if (matches.Length > 1) throw new SyncCliAmbiguousTargetException("Trash key is not unique.");
                if (matches.Length == 1) return matches[0];
                if (!page.HasMoreDeleted) throw new SyncCliTargetException("The exact Google Trash item is no longer available for this pair.");
                cursor = page.DeletedCursor;
                if (cursor is null || !cursors.Add(cursor)) throw new SyncCliOperationException("Trash pagination did not advance; nothing was restored.");
            }
            throw new SyncCliOperationException("Trash lookup reached its 100-page limit; nothing was restored.");
        }
        for (var offset = 0;;)
        {
            // The host adds one lookahead row; the retained database window is bounded to 500.
            var page = await Host.LoadRecoveryCatalogPageAsync(pairId, offset, null, false, 499, null, cancellationToken);
            var matches = page.Items.Where(item => string.Equals(item.Key, key, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 1) throw new SyncCliAmbiguousTargetException("Recovery key is not unique.");
            if (matches.Length == 1) return matches[0];
            if (!page.HasMoreArchives) break;
            offset = page.NextArchiveOffset;
        }
        throw new SyncCliTargetException($"No retained recovery item in pair '{pairId}' matches '{key}'. Use recovery revisions --path for provider revisions.");
    }

    private static SyncCliRecoveryItemData Project(SyncRecoveryCatalogItem item) =>
        new(item.Key, item.PairId, item.PairName, item.Source.ToString(), item.RelativePath, item.Health,
            item.Size ?? 0, item.ContentHash, item.RetainedUtc, item.CanRestore, item.CanRestoreCopy);

    public async Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken,
        int limit = SyncCli.ActivityDefaultLimit, int offset = 0)
    {
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken), pairId);
        // Retained evidence only: this reads the stored first-run inventories and never refreshes an
        // inventory, contacts Dropbox or starts a run.
        var page = await Host.LoadBootstrapReviewPageAsync(pairId, limit, offset,
            cancellationToken: cancellationToken);
        var items = page.Items.Select(item => new SyncCliPlanItemData(item.RelativePath, item.Group, item.ItemKind,
            item.FileType, item.Recommendation, item.Effect, item.LocalSize, item.DropboxSize,
            item.LocalModifiedUtc, item.DropboxModifiedUtc)).ToArray();
        return SyncCli.FitResponseItems(items, returned =>
            new SyncCliPlanData(pair.Id, pair.Name, pair.InitialReviewApproved == false, page.InventoryComplete,
                page.TotalItems, page.LocalOnlyItems, page.DropboxOnlyItems, page.ConflictItems, returned.Length,
                page.HasMore || returned.Length < items.Length, returned)
            {
                PresentationContext = SyncEndpointContext.Capture(pair), Limit = limit, Offset = offset,
                LocalUpdatedItems = page.LocalUpdatedItems, DropboxUpdatedItems = page.DropboxUpdatedItems
            }, "plan");
    }

    public async Task<SyncCliPreparationData> PrepareFirstSyncAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken, string? choicesJson = null)
    {
        // Per-folder one-sided choices and their resolved effects are a core v2 feature; this runtime only
        // prepares the retained comparison, so it refuses the option instead of planning without it.
        if (choicesJson is not null)
            throw new SyncCliUsageException(
                "first-sync prepare --choices needs core v2 (--cli --core v2). No sync action was applied.");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var phases = new List<SyncCliPreparationPhase>();
        var phaseGate = new object();
        var phase = "Database admission and pair lookup";
        var phaseOrder = 0;
        var phaseStarted = TimeSpan.Zero;
        void BeginPhase(string next)
        {
            lock (phaseGate)
            {
                if (next == phase) return;
                var now = elapsed.Elapsed;
                phases.Add(new SyncCliPreparationPhase(phase, (now - phaseStarted).TotalSeconds));
                progress($"Preparation phase completed: {phase}; elapsed {(now - phaseStarted).TotalSeconds:F3}s.");
                phase = next;
                phaseStarted = now;
                progress("Preparation phase started: " + phase + ". No file effects are authorized.");
            }
        }
        static long Length(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (FileNotFoundException) { return 0; }
            catch (DirectoryNotFoundException) { return 0; }
        }
        var database = Host.Authority.DatabasePath;
        var databaseBefore = Length(database);
        var walBefore = Length(database + "-wal");
        progress("Preparation phase started: " + phase + ". No file effects are authorized.");
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), pairId);
        BeginPhase("Endpoint binding and inventory observation");
        phaseOrder = 1;
        var review = await Host.PrepareBootstrapInventoryAsync(pairId, cancellationToken, entry =>
        {
            // Local and remote read/hash callbacks interleave. Their individual
            // events are still logged below, but switching between them is NOT
            // completion of an inventory phase. Time the actual top-level pipeline
            // barriers once, keeping the result bounded even for millions of files.
            var order = entry.Comparison?.Phase switch
            {
                "Saving Folder Inventories" => 3,
                "Updating Folder Index" => 4,
                "Recording First Sync Baselines" or "Retaining First Sync Conflicts" => 5,
                string phaseLabel when phaseLabel.StartsWith("Checking ", StringComparison.Ordinal) => 1,
                not null => 2,
                _ when entry.FailureMessage?.StartsWith("Comparing the complete", StringComparison.Ordinal) == true => 6,
                _ => 0
            };
            lock (phaseGate)
            {
                if (order > phaseOrder)
                {
                    BeginPhase(order switch
                    {
                        2 => "Reading both endpoint inventories",
                        3 => "Saving folder inventories",
                        4 => "Updating folder index",
                        5 => "Recording matching paths and conflicts",
                        _ => "Complete first-sync comparison"
                    });
                    phaseOrder = order;
                }
            }
            var detail = SyncCliSanitize.Message(DescribeRunProgress(entry), maximumLength: 4096);
            if (detail is not null) progress(detail);
            return Task.CompletedTask;
        });
        BeginPhase("Preparation completed");
        long uploadBytes = 0, downloadBytes = 0;
        foreach (var item in review.Items)
        {
            if (item.ItemKind != "file") continue;
            if (string.Equals(item.Recommendation, "Upload", StringComparison.OrdinalIgnoreCase))
                uploadBytes = checked(uploadBytes + (item.LocalSize ?? 0));
            if (string.Equals(item.Recommendation, "Download", StringComparison.OrdinalIgnoreCase))
                downloadBytes = checked(downloadBytes + (item.DropboxSize ?? 0));
        }
        return new SyncCliPreparationData(pair.Id, pair.Name, pair.PolicyRevision,
            review.LocalScanGenerationId!, review.DropboxScanGenerationId!, review.TotalItems,
            review.LocalOnlyItems, review.DropboxOnlyItems, review.ConflictItems, uploadBytes, downloadBytes,
            elapsed.Elapsed.TotalSeconds, phases, databaseBefore, Length(database), walBefore,
            Length(database + "-wal"), FileEffectsAuthorized: false, FirstSyncApproved: false);
    }

    public async Task<SyncCliScanData> ScanAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken)
    {
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), pairId);
        progress($"Preparing to read both endpoint inventories for '{pair.Name}' ({pair.Id}).");
        await Host.RefreshBootstrapInventoryAsync(pairId, cancellationToken, entry =>
        {
            var text = SyncCliSanitize.Message(DescribeRunProgress(entry), maximumLength: 4096);
            if (text is not null) progress(text);
            return Task.CompletedTask;
        });
        var page = await Host.LoadBootstrapReviewPageAsync(pairId, 1, 0, cancellationToken: cancellationToken);
        return new SyncCliScanData(pair.Id, pair.Name, page.LocalOnlyItems, page.DropboxOnlyItems,
            page.LocalScanGenerationId ?? "none", page.DropboxScanGenerationId ?? "none");
    }

    public async Task<SyncCliPairData> ApproveFirstSyncAsync(string pairId, CancellationToken cancellationToken)
    {
        var approved = await Host.ApproveBootstrapMergeAsync(pairId, cancellationToken);
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        return Project(RequirePair(pairSet, approved.Id), pairSet.SelectedPairId);
    }

    public async Task<SyncCompletedCopyMoveRepairResult> RepairCompletedCopyMovesAsync(string pairId,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var result = await Host.RepairCompletedCopyMovesAsync(pairId, progress, cancellationToken);
        if (result.Items.Any(item => item.Status == "preserved"))
            throw new SyncCliException("move_repair_preserved", "Some move predecessors were preserved. Inspect the per-file reasons before retrying.",
                SyncCliExitCode.Failed, data: result);
        return result;
    }

    public async Task<SyncCliFirstSyncResultData> ApplyFirstSyncAsync(string pairId, string? choicesJson,
        bool allowRecommended, long? expectedPolicyRevision, Action<string> progress,
        CancellationToken cancellationToken, string? expectedLocalScanId = null)
    {
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), pairId);
        // Share the host's exact pair/global/account pause contract. A known
        // refusal must precede reading hundreds of thousands of reviewed rows;
        // execution still repeats this check under its own lock before effects.
        if (Host.NewSyncAdmissionReason(pair) is { } admissionReason)
            throw new SyncCliReviewException(admissionReason);
        // The caller binds its choices to the exact retained plan: a different policy revision (or a
        // refreshed inventory) is refused instead of being applied to a plan it never saw.
        if (expectedPolicyRevision is { } expected && expected != pair.PolicyRevision)
            throw new SyncCliReviewException($"Pair '{pair.Name}' ({pair.Id}) is now at policy revision " +
                pair.PolicyRevision + ", not the expected " + expected + ". Read the plan again with 'plan --pair'.");
        Dictionary<string, string> explicitChoices = new(StringComparer.Ordinal);
        if (choicesJson is not null)
        {
            choicesJson = await ReadFirstSyncChoicesAsync(choicesJson, cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(choicesJson, new System.Text.Json.JsonDocumentOptions
            {
                MaxDepth = 4
            });
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new SyncCliUsageException("The --choices value must be a JSON object of path to choice.");
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // The flat path-to-choice map is this runtime's only --choices form; a nested "paths" or
                // "oneSided" document is the structured core-v2 form and is refused as a usage error.
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    throw new SyncCliUsageException("This runtime accepts only the flat path-to-choice " +
                        "--choices document; per-folder remove/copy choices need core v2 (--cli --core v2). " +
                        "No sync action was applied.");
                if (!explicitChoices.TryAdd(PilotPath.NormalizeInventoryPath(property.Name),
                        (property.Value.GetString() ?? string.Empty).Trim().ToLowerInvariant()))
                    throw new SyncCliUsageException("The --choices document contains a duplicate path or inventory binding. Nothing was applied.");
            }
        }
        // Read one complete projection with its counts and identities. A one-row page still
        // runs the full inventory comparison, so separate metadata reads are not cheap.
        // A two-sided binding selects the immutable review; the legacy left-only binding
        // must still match the current review and cannot silently select an older snapshot.
        var hasLocalBinding = explicitChoices.Remove("__plan", out var planBinding);
        var hasRemoteBinding = explicitChoices.Remove("__remote_plan", out var remoteBinding);
        if (expectedLocalScanId is not null)
        {
            if (hasLocalBinding && !string.Equals(planBinding, expectedLocalScanId, StringComparison.Ordinal))
                throw new SyncCliUsageException("--key and __plan must name the same reviewed local inventory. Nothing was applied.");
            hasLocalBinding = true;
            planBinding = expectedLocalScanId;
        }
        if (hasRemoteBinding && !hasLocalBinding)
            throw new SyncCliUsageException("__remote_plan requires the matching __plan left inventory identity.");
        progress($"Preparing first sync for '{pair.Name}': reading the complete plan in one database query, including saved inventory identities, item counts and last successful sync comparisons; no file transfer has started.");
        var page = await Host.LoadCompleteBootstrapReviewAsync(pairId, cancellationToken,
            reviewedLocalScanId: hasRemoteBinding ? planBinding : null,
            reviewedRemoteScanId: hasRemoteBinding ? remoteBinding : null);
        if (!page.InventoryComplete || page.LocalScanGenerationId is null || page.DropboxScanGenerationId is null)
            throw new SyncCliReviewException($"Pair '{pair.Name}' ({pair.Id}) has no complete retained " +
                "inventory. Run: SyncShark.exe --cli scan --pair " + pairId + " --yes");
        if (hasLocalBinding && !hasRemoteBinding &&
            !string.Equals(planBinding, page.LocalScanGenerationId, StringComparison.Ordinal))
            throw new SyncCliReviewException("The retained first-sync inventory changed since the plan you " +
                "reviewed. Bind both __plan (left scan) and __remote_plan (right scan) to apply that retained snapshot, or read the current plan.");
        var reviewedRows = page.Items;
        progress($"Preparing first sync: loaded {reviewedRows.Count:N0} reviewed items; checking the requested choices and preserving conflicts.");
        if (reviewedRows.Count != page.TotalItems)
            throw new SyncCliReviewException("The retained first-sync inventory changed while the plan was being " +
                "built. Read the plan again with 'plan --pair'.");
        var choices = new List<SyncBootstrapActionChoice>(reviewedRows.Count);
        var undecided = new List<string>();
        var conflictsLeftUnchanged = 0;
        var consumedChoices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in reviewedRows)
        {
            var path = PilotPath.NormalizeInventoryPath(item.RelativePath);
            string? action = null;
            // A retained identical item needs no action at all: an explicit no-change is acknowledged as no
            // effect, and upload or download for it is refused. Identical rows never become choices, so no
            // invented Identical action can reach the Host.
            if (string.Equals(item.Group, "Identical", StringComparison.Ordinal))
            {
                if (explicitChoices.TryGetValue(path, out var identicalChoice))
                {
                    consumedChoices.Add(path);
                    if (!string.Equals(identicalChoice, "no-change", StringComparison.Ordinal))
                        throw new SyncCliUsageException($"Choice '{identicalChoice}' for '{path}' is not valid: " +
                            "the retained inventory shows this item as identical on both sides, so it has no effect.");
                }
                continue;
            }
            // The shared policy also admits a one-sided UPDATE proved by a
            // saved synchronization point. Timestamps alone grant no overwrite.
            // Genuine conflicts stay unchanged and never block independent files.
            if (!SyncBootstrapActions.IsActionable(item.Group))
            {
                if (explicitChoices.TryGetValue(path, out var conflictChoice))
                {
                    consumedChoices.Add(path);
                    if (conflictChoice != "no-change")
                        throw new SyncCliUsageException($"Choice '{conflictChoice}' for '{path}' would overwrite a conflict. " +
                            "Leave it unchanged here and use 'conflicts resolve' with its exact current conflict identity.");
                }
                conflictsLeftUnchanged++;
                continue;
            }
            if (explicitChoices.TryGetValue(path, out var explicitChoice))
            {
                consumedChoices.Add(path);
                action = explicitChoice switch
                {
                    "upload" => "Upload",
                    "download" => "Download",
                    "move" when item.Move is not null => item.Move.Action,
                    // R473-5: no-change on a one-sided item is the core Skip for now action.
                    "no-change" => "Skip for now",
                    _ => throw new SyncCliUsageException($"Choice '{explicitChoice}' for '{path}' must be upload, " +
                        "download, move (only for a reviewed move) or no-change.")
                };
                if (string.Equals(action, "Upload", StringComparison.Ordinal) &&
                    SyncBootstrapActions.TransferAction(item.Group) != "Upload")
                    throw new SyncCliUsageException($"Choice 'upload' for '{path}' is not valid: the retained " +
                        $"inventory shows this item as {item.Group}.");
                else if (string.Equals(action, "Download", StringComparison.Ordinal) &&
                         SyncBootstrapActions.TransferAction(item.Group) != "Download")
                    throw new SyncCliUsageException($"Choice 'download' for '{path}' is not valid: the retained " +
                        $"inventory shows this item as {item.Group}.");
            }
            // This existing switch approves safe transfers, not newly discovered moves.
            // A move requires the explicit choice shown by `plan --pair`.
            if (action is null && allowRecommended && item.Move is null)
            {
                action = SyncBootstrapActions.TransferAction(item.Group);
            }
            if (action is null)
            {
                undecided.Add(item.RelativePath + (item.Move is { } move
                    ? " (move from '" + move.FromPath + "'; choose move or no-change)"
                    : " (" + item.Group + ")"));
                continue;
            }
            choices.Add(new SyncBootstrapActionChoice(item.RelativePath, item.Group, action,
                SyncBootstrapActions.IsMove(action) ? item.Move : null));
        }
        if (consumedChoices.Count != explicitChoices.Count)
        {
            var unknown = explicitChoices.Keys.Where(key => !consumedChoices.Contains(key)).Take(5).ToArray();
            throw new SyncCliUsageException("The choices name " + unknown.Length +
                " path(s) this pair's retained first-sync inventory does not contain, for example: " +
                string.Join(", ", unknown) + ". Read the plan again with 'plan --pair'.");
        }
        if (undecided.Count > 0)
            throw new SyncCliReviewException(undecided.Count + " retained item(s) have no explicit decision, for " +
                "example: " + string.Join(", ", undecided.Take(5)) + ". Supply them with '--choices' (path to " +
                "upload/download/no-change), or pass '--allow-recommended' to accept every retained " +
                "recommendation, and resolve conflicts with 'conflicts resolve'.");
        var plan = new SyncBootstrapActionPlan(pairId, page.LocalScanGenerationId ?? string.Empty,
            page.DropboxScanGenerationId ?? string.Empty, choices);
        progress($"Preparing first sync: rechecking pair policy before admitting {choices.Count:N0} decisions; the execution engine will independently validate every choice against these exact reviewed snapshots before any transfer.");
        var afterPair = RequirePair(await Host.LoadPairsAsync(cancellationToken), pairId);
        // RunBootstrapActionPlanAsync performs the authoritative full snapshot/root,
        // coverage and action validation under its run lock. Do not duplicate its
        // full classification here merely to compare counts outside that lock.
        if (afterPair.PolicyRevision != pair.PolicyRevision)
            throw new SyncCliReviewException("The retained first-sync inventory or policy changed while the choice " +
                "plan was being built. Read the plan again with 'plan --pair'.");
        progress($"Applying {choices.Count} retained first-sync decision(s) for '{pair.Name}' ({pair.Id}). " +
            $"{conflictsLeftUnchanged} conflict(s) will remain unchanged for separate resolution.");
        // R473-2: one progress sink only. The host's stream already carries every live action stage, so the same
        // stream keeps the CLI's output and the retained history identical instead of printing each action twice.
        string? appliedRunId = null;
        var result = await Host.RunBootstrapActionPlanAsync(plan, cancellationToken: cancellationToken,
            expectedPolicyRevision: pair.PolicyRevision,
            onPreparationProgress: progress,
            onProgress: entry =>
        {
            appliedRunId ??= Host.ActiveRun?.Id;
            var text = SyncCliSanitize.Message(DescribeRunProgress(entry), maximumLength: 4096);
            if (text is not null) progress(text);
            return Task.CompletedTask;
        });
        return CheckedFirstSyncResult(result, appliedRunId);
    }

    // Preserve inline JSON callers, but honour the documented file input so a large
    // reviewed selection never has to fit in a Windows command line or bridge message.
    // The file contains decisions, not executable input; the same complete-plan and
    // per-item authority validation above applies after reading it.
    internal const int MaximumFirstSyncChoicesBytes = 64 * 1024 * 1024;

    internal static async Task<string> ReadFirstSyncChoicesAsync(string input, CancellationToken cancellationToken)
    {
        var value = input.Trim();
        if (value.StartsWith('{') || value.StartsWith('['))
        {
            if (System.Text.Encoding.UTF8.GetByteCount(input) > MaximumFirstSyncChoicesBytes)
                throw new SyncCliUsageException("The choices document exceeds the 64 MiB input limit. No sync action was applied.");
            return input;
        }
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new SyncCliUsageException("--choices requires an inline JSON object or an absolute local path to a JSON choices file.");
        try
        {
            var attributes = File.GetAttributes(value);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new SyncCliUsageException("The choices file must be a regular, locally available file, not a directory, link or online-only placeholder.");
            await using var stream = new FileStream(value, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFirstSyncChoicesBytes)
                throw new SyncCliUsageException("The choices file exceeds the 64 MiB input limit. No sync action was applied.");
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SyncCliUsageException("The choices file could not be read as JSON text. Check that the absolute local path exists and is readable. No sync action was applied.");
        }
    }

    // A successful large mutation must not turn into a transport-size failure after
    // files changed. Retain every result in the host's log and return bounded samples
    // plus the exact run identity and complete counters, for GUI and direct CLI alike.
    internal static SyncCliFirstSyncResultData CompactFirstSyncResult(SyncBootstrapActionResult result, string? runId)
    {
        // Show actionable failures first; a successful prefix must not hide the
        // only two errors in a thousand-file receipt. Full retained order is unchanged.
        var sample = result.Result.Decisions.OrderByDescending(item => item.NotAttempted ||
            item.Kind is SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or SyncDecisionKind.Stopped or
                SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending)
            .Take(SyncCli.ActivityDefaultLimit).ToArray();
        SyncCliFirstSyncResultData Create(SyncDecision[] files, string name) =>
            new(result.Pair.Id, name, result.AppliedItems, result.SkippedItems,
                result.FirstSyncComplete, result.Result.Uploaded, result.Result.Downloaded, result.Result.Identical,
                result.Result.Conflicts, result.Result.Blocked, files, runId, result.Result.Decisions.Count,
                files.Length, files.Length < result.Result.Decisions.Count)
            { Failed = result.Result.Failed, NotAttempted = result.Result.NotAttempted,
                NeedsVerification = result.Result.NeedsVerification, Stopped = result.Result.Stopped };
        try
        {
            return SyncCli.FitResponseItems(sample, files => Create(files, result.Pair.Name),
                "first-sync apply", allowEmptySample: true);
        }
        catch (SyncCliOperationException)
        {
            // A display name is optional; an already-applied effect's exact run,
            // pair identity and complete counters are not. Never replace that
            // receipt with a transport failure or implicitly repeat the effect.
            return SyncCli.FitResponseItems(sample,
                files => Create(files, "[display name omitted; open the retained run]"),
                "first-sync apply", allowEmptySample: true);
        }
    }

    internal static SyncCliFirstSyncResultData CheckedFirstSyncResult(SyncBootstrapActionResult result, string? runId)
    {
        var receipt = CompactFirstSyncResult(result, runId);
        if (!result.FirstSyncComplete || result.Result.Blocked != 0 || result.Result.Conflicts != 0 ||
            result.Result.NotAttempted != 0 || result.Result.NeedsVerification != 0 || result.Result.Stopped)
            throw new SyncCliReviewException("The selected work finished with unresolved items or first-sync approval still pending. " +
                "Successful files remain completed. Open this retained run's log and retry only its eligible remaining files.", receipt);
        return receipt;
    }

    private static object CompactPairResult(SyncAuthorityBatchItemResult item) => new
    {
        pairId = item.Pair.Id, pairName = item.Pair.Name, failure = SyncCliSanitize.Message(item.FailureMessage),
        result = item.Result is { } result ? new
        {
            result.Uploaded, result.Downloaded, result.Identical, result.Conflicts, result.Blocked,
            result.Failed, result.NotAttempted, result.NeedsVerification, result.Skipped, result.Stopped,
            totalFiles = result.Decisions.Count
        } : null
    };

    internal static object CompactBatchReceipt(object[] results, string? runId, bool stopped,
        string command, string? originalRunId = null) =>
        SyncCli.FitResponseItems(results, returned => (object)new
        {
            runId, originalRunId, count = results.Length, stopped, returned = returned.Length,
            omitted = results.Length - returned.Length, pairs = returned,
            note = "This is a bounded summary, not a replay instruction. Complete per-pair and per-file outcomes remain in activity list --log --run with this runId."
        }, command, allowEmptySample: true, reserveCharacters: 4096);

    // ENG3: the CLI shares the host's one progress stream instead of keeping a second logger, so the same phase,
    // current path and observed count that the First Sync surface receives reach this command's output.
    internal static string DescribeRunProgress(SyncAuthorityBatchProgress entry) =>
        !string.IsNullOrWhiteSpace(entry.FailureMessage) && (entry.FailureType is not null ||
            entry.State is "Blocked" or "Failed" or "Stopped" or "Needs attention")
            ? entry.State + ": " + entry.FailureMessage
            : entry.Action is { } action
                ? action.Stage + " " + action.Action + " " + action.RelativePath + ": " + action.Detail +
                  (action.Batch is { } batch ? "\n" + batch.Describe(maximumPaths: 0) : "")
                : entry.Comparison is { } comparison ? SyncProgressDescription.Describe(comparison)
                : entry.State + (entry.FailureMessage is null ? string.Empty : ": " + entry.FailureMessage);

    public async Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken)
    {
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), pairId);
        // Exactly one already-approved named pair. The CLI never approves a first sync, a conflict, a
        // deletion, an ownership change or a credential decision itself.
        if (pair.InitialReviewApproved == false)
            throw new SyncCliReviewException($"The first sync for pair '{pair.Name}' ({pair.Id}) has not been " +
                "reviewed. Review it with 'plan --pair', then run 'first-sync approve --pair " + pairId + " --yes'.");
        if (!pair.Enabled)
            throw new SyncCliReviewException($"Pair '{pair.Name}' ({pair.Id}) is disabled. Enable it first: " +
                "SyncShark.exe --cli pairs enable --pair " + pairId + " --yes");
        if (Host.IsPairAccountPaused(pair))
            throw new SyncCliReviewException($"The Dropbox account that owns pair '{pair.Name}' ({pair.Id}) is " +
                "paused. Resume it first with 'resume --account'.");

        progress($"Running pair '{pair.Name}' ({pair.Id}) with the existing Sync Shark runtime.");
        try
        {
            string? runId = null;
            var batch = await Host.RunPairsAsync([pair], SyncCliRuntime.Trigger, entry =>
            {
                runId ??= Host.ActiveRun?.Id;
                var text = SyncCliSanitize.Message(entry.Pair.Name + " (" + entry.Pair.Id + "): " + DescribeRunProgress(entry), maximumLength: 4096);
                if (text is not null) progress(text);
                return Task.CompletedTask;
            }, cancellationToken, admissionProgress: progress);
            var item = RequirePairResult(batch, pairId);
            var receipt = item.Result is { } result
                ? new SyncCliRunData(item.Pair.Id, item.Pair.Name, result.Uploaded, result.Downloaded,
                    result.Identical, result.Conflicts, result.Blocked, result.Stopped || batch.Stopped)
                { RunId = runId, Failed = result.Failed, NotAttempted = result.NotAttempted,
                    NeedsVerification = result.NeedsVerification }
                : null;
            if (item.FailureMessage is not null)
                throw new SyncCliOperationException($"The run for pair '{pair.Name}' ({pair.Id}) failed " +
                    $"({item.FailureType}): {SyncCliSanitize.Message(item.FailureMessage) ?? "no retained detail"}.",
                    data: (object?)receipt ?? new { pairId, runId, item.FailureType });
            if (receipt is null)
                throw new SyncCliOperationException(
                    $"The run for pair '{pair.Name}' ({pair.Id}) ended without a retained result.",
                    data: new { pairId, runId });
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AwaitCheckpointAsync();
            throw;
        }
    }

    public async Task<object> RunBatchAsync(string selection, Action<string> progress, CancellationToken cancellationToken)
    {
        string[] ids;
        try { ids = JsonSerializer.Deserialize<string[]>(selection) ?? []; }
        catch (JsonException) { throw new SyncCliUsageException("--selection must be a JSON array of exact pair IDs."); }
        if (ids.Length is < 1 or > 200 || ids.Any(string.IsNullOrWhiteSpace) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new SyncCliUsageException("Select between 1 and 200 distinct exact pair IDs.");
        var retained = await Host.LoadPairsAsync(cancellationToken, progress);
        var pairs = ids.Select(id => RequirePair(retained, id)).ToArray();
        // Validate the entire selection before the first effect: a missing, unreviewed, disabled or
        // account-paused target must not leave an unintentionally partial batch already running.
        foreach (var pair in pairs)
            if (pair.InitialReviewApproved == false || !pair.Enabled || Host.IsPairAccountPaused(pair))
                throw new SyncCliReviewException("Pair '" + pair.Name + "' (" + pair.Id +
                    ") is unreviewed, disabled or its account is paused. No selected pair was started.");
        try
        {
            string? batchRunId = null;
            var batch = await Host.RunPairsAsync(pairs, SyncCliRuntime.Trigger, entry =>
            {
                batchRunId ??= Host.ActiveRun?.Id;
                progress(SyncCliSanitize.Message(entry.Pair.Name + " (" + entry.Pair.Id + "): " + DescribeRunProgress(entry), maximumLength: 4096) ?? "Pair status changed.");
                return Task.CompletedTask;
            }, cancellationToken, admissionProgress: progress);
            var results = batch.Items.Select(CompactPairResult).ToArray();
            var receipt = CompactBatchReceipt(results, batchRunId, batch.Stopped, "run batch");
            if (batch.Stopped || batch.Items.Any(item => item.Result?.Stopped == true))
                throw new SyncCliException("canceled", "The selected batch stopped; completed work remains in this run's operation log.",
                    SyncCliExitCode.Canceled, data: receipt);
            if (batch.Items.Count != pairs.Length ||
                batch.Items.Any(item => item.FailureMessage is not null || item.Result is null))
                throw new SyncCliOperationException("One or more selected pairs failed. Completed work is retained; inspect this run's log and attention list before retrying.", data: receipt);
            if (batch.Items.Any(item => item.Result!.Blocked > 0 || item.Result.Conflicts > 0 ||
                    item.Result.Failed > 0 || item.Result.NotAttempted > 0 || item.Result.NeedsVerification > 0))
                throw new SyncCliReviewException("The selected batch finished with unresolved files. Inspect this run's log and attention list; successful work remains retained.", receipt);
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AwaitCheckpointAsync();
            throw;
        }
    }

    private static SyncAuthorityBatchItemResult RequirePairResult(SyncAuthorityBatchResult batch, string pairId)
    {
        var matches = batch.Items.Where(item => string.Equals(item.Pair.Id, pairId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
            throw new SyncCliAmbiguousTargetException($"More than one run result matches pair '{pairId}'.");
        if (matches.Length == 0)
            // Never substitute an unrelated pair's result for the requested pair.
            throw new SyncCliOperationException(
                $"The run did not return a result for the requested pair '{pairId}'.");
        return matches[0];
    }

    private async Task AwaitCheckpointAsync()
    {
        try { await Host.WaitForActiveRunCheckpointAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch (Exception) { /* The run already recorded its own durable outcome. */ }
    }

    public async Task<SyncCliControlData> SetRunPauseAsync(bool paused, CancellationToken cancellationToken)
    {
        var current = Preferences.Load();
        if (!paused && (PauseStateProjection.GlobalPaused(current, DateTimeOffset.UtcNow) ||
            PauseStateProjection.ActiveAccountPauses(current, DateTimeOffset.UtcNow).Count != 0))
            return new SyncCliControlData("run resume", false,
                "A global or account pause remains active. Resume that exact scope first; no paused scope was bypassed.");
        var nowPaused = await Host.SetActiveRunPausedAsync(paused, cancellationToken);
        if (nowPaused is null)
            return new SyncCliControlData(paused ? "run pause" : "run resume", false,
                "No synchronization run is active, so there is nothing to pause or resume.");
        return new SyncCliControlData(paused ? "run pause" : "run resume", true,
            paused ? "The active run is paused at its next safe checkpoint." : "The active run continues.");
    }

    public async Task<SyncCliControlData> CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        var stopped = await Host.StopActiveRunAsync(runId, TimeSpan.FromSeconds(30), cancellationToken);
        if (!stopped)
            throw new SyncCliTargetException($"No active job with exact ID '{runId}' accepted cancellation. No different job was stopped.");
        return new SyncCliControlData("cancel", true, $"Run '{runId}' was asked to stop through the existing run " +
            "control and reached its durable checkpoint.");
    }

    public async Task<SyncCliPauseData> SetPauseAsync(string scope, string? target, bool paused, int? minutes,
        bool untilResumed, CancellationToken cancellationToken, DateTimeOffset? untilUtc = null,
        bool untilTomorrow = false, string? timeZoneId = null)
    {
        await BeforePauseChangeAsync(cancellationToken);
        try
        {
        scope = PauseScopes.Require(scope);
        if (!paused && (minutes is not null || untilResumed || untilUtc is not null || untilTomorrow))
            throw new SyncCliUsageException("A resume accepts a scope and an exact target only; it carries no deadline.");
        var forms = new[] { minutes is not null, untilResumed, untilUtc is not null, untilTomorrow }.Count(selected => selected);
        if (paused && forms > 1)
            throw new SyncCliUsageException(
                "Choose one deadline form: --minutes, --until <instant>, --until-tomorrow or --until-resumed.");
        // The existing open-ended pause stays the meaning of a bare 'pause'.
        if (paused && forms == 0) untilResumed = true;
        if (scope == PauseScopes.Account)
            RequireConnection(target ?? throw new SyncCliUsageException("An account pause requires an exact account."));
        if (scope == PauseScopes.Account && Host.HasActiveRun)
            // D8: the CLI cannot prove the active run's account scope from this process boundary,
            // so the request is refused rather than widened to every account.
            return new SyncCliPauseData(scope, target ?? string.Empty, false, null,
                Refused: PauseScopeRules.ActiveRunRefusal(PauseScopes.Account));
        var nowUtc = DateTimeOffset.UtcNow;
        var zone = PauseSchedule.TryResolveZone(timeZoneId) ?? TimeZoneInfo.Local;
        PauseResolution? resolution = null;
        if (paused)
        {
            if (untilResumed) resolution = new PauseResolution(true, null, zone.Id, null);
            else if (untilUtc is { } exact)
            {
                // Authoritative validation: an expired deadline is refused here rather than committed.
                if (!PauseSchedule.ValidateDeadline(exact, nowUtc, out var deadlineReason))
                    throw new SyncCliUsageException(deadlineReason);
                resolution = new PauseResolution(false, exact.ToUniversalTime(), zone.Id,
                    TimeZoneInfo.ConvertTime(exact, zone));
            }
            else if (untilTomorrow) resolution = PauseSchedule.Resolve(PauseOption.UntilTomorrow, nowUtc, zone);
            else
            {
                // Any bounded minute value stays supported even when the product menu offers only
                // the shared option set.
                var bounded = minutes ?? 0;
                if (bounded is < 1 or > 43_200)
                    throw new SyncCliUsageException("The option --minutes requires an integer between 1 and 43200.");
                var until = nowUtc.AddMinutes(bounded);
                resolution = new PauseResolution(false, until, zone.Id, TimeZoneInfo.ConvertTime(until, zone));
            }
        }
        var preferences = Preferences.Load();
        switch (scope)
        {
            case PauseScopes.Global:
                preferences = preferences with
                {
                    PauseUntilResumed = paused && resolution!.Indefinitely,
                    PauseUntilUtc = resolution?.UntilUtc,
                    PauseTimeZoneId = resolution?.TimeZoneId
                };
                break;
            case PauseScopes.Account:
                var binding = RequireConnection(target ?? throw new SyncCliUsageException(
                    "An account pause or resume requires --account <exact-connection-id>."));
                var pauses = (preferences.AccountPauses ?? []).Where(item =>
                    !string.Equals(item.ConnectionId, binding.ConnectionId, StringComparison.Ordinal)).ToList();
                if (paused)
                    pauses.Add(new SyncAccountPause(binding.ConnectionId, resolution!.Indefinitely,
                        resolution.UntilUtc, resolution.TimeZoneId));
                preferences = preferences with { AccountPauses = pauses };
                break;
            default:
                throw new SyncCliUsageException(
                    "A pair pause uses 'pairs disable --pair <exact-id> --yes' and is not a pause scope.");
        }
        await Preferences.SaveAsync(preferences, cancellationToken);
        if (scope == PauseScopes.Global && Host.HasActiveRun &&
            (paused || PauseStateProjection.ActiveAccountPauses(preferences, DateTimeOffset.UtcNow).Count == 0))
        {
            try { await Host.SetActiveRunPausedAsync(paused, cancellationToken); }
            catch (Exception error)
            {
                throw new SyncCliOperationException("The global pause preference was saved, but the active run control " +
                    "could not be confirmed. Check status before retrying.", error);
            }
        }
        return new SyncCliPauseData(scope, target ?? string.Empty, paused, resolution?.UntilUtc,
            UntilResumed: paused && resolution!.Indefinitely, Indefinitely: paused && resolution!.Indefinitely,
            TimeZoneId: resolution?.TimeZoneId, LocalResumeUtc: resolution?.LocalResume);
        }
        finally { await CompletePauseChangeAsync(); }
    }

    protected virtual Task BeforePauseChangeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected virtual Task CompletePauseChangeAsync() => Task.CompletedTask;

    public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var automation = Host.LoadAutomation();
        var transfer = Host.LoadTransferSettings();
        var preferences = Preferences.Load();
        return Task.FromResult(new SyncCliSettingsData(
            new SyncCliAutomationData(automation.Enabled, automation.IntervalMinutes, automation.StartWithWindows),
            new SyncCliTransferData(transfer.MaximumConcurrentPairs, transfer.UploadKiBPerSecond,
                transfer.DownloadKiBPerSecond, transfer.ResourceMode, transfer.MaximumConcurrentFiles,
                transfer.UploadBatchSize, transfer.UploadCommitConcurrency, transfer.DeleteBatchSize),
            new SyncCliInterfaceData(preferences.Theme, preferences.KeepRunningWhenClosed, preferences.NotifyProblems,
                preferences.NotifyCompleted, preferences.NotifyTransfers, preferences.ExplorerIntegrationEnabled,
                preferences.AutomaticUpdateChecks, preferences.CompactRows, preferences.NotificationSound, preferences.StatusIconStyle),
            Resources: Host.TransferResourceStatus, RecoveryMaxBytes: transfer.RecoveryMaxBytes,
            DataLocation: ProductRoot));
    }

    protected virtual Task BeforeSaveSettingsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    protected virtual Task CompleteSettingsSaveAsync() => Task.CompletedTask;

    protected virtual Task<bool> ApplySavedSettingsAsync(SyncCliSettingsData previous, SyncCliSettingsData saved,
        CancellationToken cancellationToken) => Task.FromResult(false);

    public virtual async Task<SyncCliSettingsData> SaveSettingsAsync(string settingsJson,
        CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(settingsJson, new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false
            });
            root = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new SyncCliUsageException("The --settings value must be a JSON object: " +
                (SyncCliSanitize.Message(error.Message) ?? "invalid JSON"));
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new SyncCliUsageException("The --settings value must be a JSON object.");
        ValidateMembers(root, "automation", "transfer", "interface", "recoveryMaxBytes", "recovery", "dataLocation");
        if (root.TryGetProperty("dataLocation", out var location))
        {
            if (root.EnumerateObject().Count() != 1 || location.ValueKind != JsonValueKind.String)
                throw new SyncCliUsageException("dataLocation must be a string and changed separately from other settings.");
            if (ApplicationOwnsRuntime || Host.HasActiveRun)
                throw new SyncCliBusyException("Exit the running application before moving data from the CLI, or use its Data location setting. Finish or resolve pending work first.");
            var previous = await LoadSettingsAsync(cancellationToken);
            var moved = await SyncDataLocation.MoveAsync(StateAuthority, location.GetString()!, cancellationToken);
            if (moved.RestartRequired) await DisposeAsync();
            return previous with { DataLocation = moved.DataLocation, RestartRequired = moved.RestartRequired,
                EffectsApplied = true, EffectsNote = SyncDataLocation.CleanupOldCopy(moved) };
        }
        await BeforeSaveSettingsAsync(cancellationToken);
        try
        {
        var previous = await LoadSettingsAsync(cancellationToken);
        var priorAutomation = Host.LoadAutomation();
        var priorTransfer = Host.LoadTransferSettings();
        var priorPreferences = Preferences.Load();
        var automation = priorAutomation;
        var transfer = priorTransfer;
        var preferences = priorPreferences;
        if (root.TryGetProperty("automation", out var automationElement))
        {
            ValidateMembers(automationElement, "enabled", "intervalMinutes", "startWithWindows");
            automation = priorAutomation with {
                Enabled = ReadBool(automationElement, "enabled", priorAutomation.Enabled),
                IntervalMinutes = ReadInt(automationElement, "intervalMinutes", priorAutomation.IntervalMinutes),
                StartWithWindows = ReadBool(automationElement, "startWithWindows", priorAutomation.StartWithWindows) };
            if (automation.IntervalMinutes is < 1 or > 1440)
                throw new SyncCliUsageException("intervalMinutes must be between 1 and 1440.");
            if (!ApplicationOwnsRuntime && automation.StartWithWindows != priorAutomation.StartWithWindows)
                throw new SyncCliUnsupportedException("Changing Windows startup requires the running installed application's startup registration adapter. No settings changed.");
        }
        if (root.TryGetProperty("transfer", out var transferElement))
        {
            ValidateMembers(transferElement, "maximumConcurrentPairs", "uploadKiBPerSecond", "downloadKiBPerSecond",
                "resourceMode", "maximumConcurrentFiles", "uploadBatchSize", "uploadCommitConcurrency", "deleteBatchSize");
            try
            {
                transfer = SyncTransferSettings.Normalize(new SyncTransferSettings(
                    ReadInt(transferElement, "maximumConcurrentPairs", priorTransfer.MaximumConcurrentPairs),
                    ReadInt(transferElement, "uploadKiBPerSecond", priorTransfer.UploadKiBPerSecond),
                    ReadInt(transferElement, "downloadKiBPerSecond", priorTransfer.DownloadKiBPerSecond),
                    ReadString(transferElement, "resourceMode", priorTransfer.ResourceMode),
                    ReadInt(transferElement, "maximumConcurrentFiles", priorTransfer.MaximumConcurrentFiles),
                    priorTransfer.RecoveryMaxBytes, priorTransfer.LocalRecoveryRoot, priorTransfer.RecoveryRetentionDays,
                    priorTransfer.TransferReservationFloorBytes,
                    ReadInt(transferElement, "uploadBatchSize", priorTransfer.UploadBatchSize),
                    ReadInt(transferElement, "uploadCommitConcurrency", priorTransfer.UploadCommitConcurrency),
                    ReadInt(transferElement, "deleteBatchSize", priorTransfer.DeleteBatchSize),
                    priorTransfer.OnlineOnlyReturnInterval));
            }
            catch (InvalidDataException error)
            {
                throw new SyncCliUsageException(error.Message);
            }
        }
        if (root.TryGetProperty("recoveryMaxBytes", out var recoveryMaxBytes))
        {
            if (recoveryMaxBytes.ValueKind != JsonValueKind.Number ||
                !recoveryMaxBytes.TryGetInt64(out var maximum) || maximum < 0)
                throw new SyncCliUsageException("recoveryMaxBytes must be a non-negative 64-bit integer.");
            transfer = transfer with { RecoveryMaxBytes = maximum };
        }
        if (root.TryGetProperty("recovery", out var recoveryElement))
        {
            // Decision 55: the local Recovery location and retention (no default location; UI field is B-130).
            ValidateMembers(recoveryElement, "localRoot", "retentionDays");
            try
            {
                transfer = SyncTransferSettings.Normalize(transfer with
                {
                    LocalRecoveryRoot = ReadString(recoveryElement, "localRoot", transfer.LocalRecoveryRoot ?? ""),
                    RecoveryRetentionDays = ReadInt(recoveryElement, "retentionDays", transfer.RecoveryRetentionDays)
                });
            }
            catch (InvalidDataException error)
            {
                throw new SyncCliUsageException(error.Message);
            }
        }
        if (root.TryGetProperty("interface", out var interfaceElement))
        {
            ValidateMembers(interfaceElement, "theme", "keepRunningWhenClosed", "notifyProblems", "notifyCompleted",
                "notifyTransfers", "explorerIntegrationEnabled", "automaticUpdateChecks", "compactRows", "notificationSound", "statusIconStyle");
            var current = priorPreferences;
            preferences = current with
            {
                Theme = ReadString(interfaceElement, "theme", current.Theme),
                KeepRunningWhenClosed = ReadBool(interfaceElement, "keepRunningWhenClosed",
                    current.KeepRunningWhenClosed),
                NotifyProblems = ReadBool(interfaceElement, "notifyProblems", current.NotifyProblems),
                NotifyCompleted = ReadBool(interfaceElement, "notifyCompleted", current.NotifyCompleted),
                NotifyTransfers = ReadBool(interfaceElement, "notifyTransfers", current.NotifyTransfers),
                ExplorerIntegrationEnabled = ReadBool(interfaceElement, "explorerIntegrationEnabled",
                    current.ExplorerIntegrationEnabled),
                AutomaticUpdateChecks = ReadBool(interfaceElement, "automaticUpdateChecks",
                    current.AutomaticUpdateChecks),
                CompactRows = ReadBool(interfaceElement, "compactRows", current.CompactRows),
                NotificationSound = ReadBool(interfaceElement, "notificationSound", current.NotificationSound),
                StatusIconStyle = ReadString(interfaceElement, "statusIconStyle", current.StatusIconStyle)
            };
            if (preferences.Theme is not ("System" or "Light" or "AMOLED" or "Dark"))
                throw new SyncCliUsageException("theme must be System, Light, AMOLED or Dark.");
            if (!UiPreferencesStore.IsStatusIconStyle(preferences.StatusIconStyle))
                throw new SyncCliUsageException("statusIconStyle must be Colour, Symbol or Combined.");
        }
        SyncCliSettingsData? saved = null;
        var effectsStarted = false;
        try
        {
            if (root.TryGetProperty("automation", out _)) await Host.SaveAutomationAsync(automation, cancellationToken);
            if (root.TryGetProperty("transfer", out _) || root.TryGetProperty("recoveryMaxBytes", out _) ||
                root.TryGetProperty("recovery", out _))
                await Host.SaveTransferSettingsAsync(transfer, cancellationToken);
            if (root.TryGetProperty("interface", out _)) await Preferences.SaveAsync(preferences, cancellationToken);
            saved = await LoadSettingsAsync(cancellationToken);
            effectsStarted = true;
            var applied = await ApplySavedSettingsAsync(previous, saved, cancellationToken);
            if (!applied && automation.StartWithWindows != priorAutomation.StartWithWindows)
                throw new SyncCliUnsupportedException("The application did not apply Windows startup registration.");
            return saved with { EffectsApplied = applied, EffectsNote = applied
                ? "Settings saved and application effects applied."
                : "Preferences saved for the next application launch; running application effects were not applied." };
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<string>();
            async Task Restore(Func<Task> action, string label)
            { try { await action(); } catch (Exception error) { rollbackErrors.Add(label + ": " + SyncCliSanitize.Message(error.Message)); } }
            await Restore(() => Host.SaveAutomationAsync(priorAutomation, CancellationToken.None), "automation");
            await Restore(() => Host.SaveTransferSettingsAsync(priorTransfer, CancellationToken.None), "transfer");
            await Restore(() => Preferences.SaveAsync(priorPreferences, CancellationToken.None), "interface");
            if (effectsStarted && saved is not null)
                await Restore(async () => {
                    if (!await ApplySavedSettingsAsync(saved, previous, CancellationToken.None) && ApplicationOwnsRuntime)
                        throw new InvalidOperationException("The application did not confirm restoration of its previous effects.");
                }, "application effects");
            throw new SyncCliOperationException("Settings were not applied: " + SyncCliSanitize.Message(failure.Message) +
                (rollbackErrors.Count == 0 ? " Previous retained settings were restored." :
                    " Restoration incomplete: " + string.Join("; ", rollbackErrors)), failure);
        }
        }
        finally { await CompleteSettingsSaveAsync(); }
    }

    private static void ValidateMembers(JsonElement value, params string[] names)
    {
        RequireSection(value);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
            if (!names.Contains(member.Name, StringComparer.Ordinal) || !seen.Add(member.Name))
                throw new SyncCliUsageException("Unknown or duplicate member '" + member.Name + "'.");
    }

    private static bool ReadBool(JsonElement element, string name, bool fallback)
    {
        RequireSection(element);
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new SyncCliUsageException($"Settings member '{name}' must be true or false.");
        return value.GetBoolean();
    }

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        RequireSection(element);
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            throw new SyncCliUsageException($"Settings member '{name}' must be an integer.");
        return parsed;
    }

    private static string ReadString(JsonElement element, string name, string fallback)
    {
        RequireSection(element);
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String)
            throw new SyncCliUsageException($"Settings member '{name}' must be a string.");
        return value.GetString() ?? fallback;
    }

    private static void RequireSection(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new SyncCliUsageException("A settings section must be a JSON object.");
    }

    public async Task<SyncCliIgnoreListData> LoadIgnoresAsync(string pairId, CancellationToken cancellationToken)
    {
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken), pairId);
        return ProjectIgnores(pair);
    }

    public async Task<SyncCliIgnoreListData> SaveIgnoresAsync(string pairId, string rules,
        CancellationToken cancellationToken)
    {
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        var pair = RequirePair(pairSet, pairId);
        IReadOnlyList<SyncIgnoreRule> parsed = [];
        var spec = rules.Trim();
        if (!spec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                parsed = SyncIgnorePolicy.Parse(spec);
            }
            catch (InvalidDataException error)
            {
                throw new SyncCliUsageException(error.Message);
            }
        }
        // The retained pair definition is the single authority for ignore rules. The same validator the
        // application uses is applied before the definition is replaced, and the revision is bumped.
        // The retained row's configuration revision must match the definition being replaced, so the
        // rules are validated and retained through the same authority call the application uses. A
        // separate revision bump belongs to the product's own policy-edit path.
        var normalized = SyncIgnorePolicy.Normalize(parsed);
        var updated = pair with { IgnoreRules = normalized };
        await Host.Authority.RetainPairDefinitionAsync(updated,
            string.Equals(pairSet.SelectedPairId, pairId, StringComparison.Ordinal), cancellationToken);
        await Host.LoadPairsAsync(cancellationToken);
        return ProjectIgnores(updated);
    }

    private static SyncCliIgnoreListData ProjectIgnores(SyncPair pair)
    {
        var rules = SyncIgnorePolicy.Normalize(pair.IgnoreRules);
        return new SyncCliIgnoreListData(pair.Id,
            rules.Select(rule => new SyncCliIgnoreRuleData(rule.Kind.ToString(), rule.Value)).ToArray(),
            rules.Count);
    }

    public async Task<SyncCliFolderTreeData> LoadFoldersAsync(string pairId, CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        var tree = await Host.LoadFolderSelectionTreeAsync(pairId, cancellationToken);
        var folders = tree.Folders.Select(folder => new SyncCliFolderData(folder.RelativePath,
            folder.Presence.ToString(), folder.IsChecked == true, folder.IsChecked is null,
            folder.Rollup?.LocalItems ?? 0, folder.Rollup?.OnlineOnlyItems ?? 0,
            (folder.Rollup?.OnlineOnlyItems ?? 0) > 0 && (folder.Rollup?.LocalItems ?? 0) == 0,
            (folder.Rollup?.LocalItems ?? 0) > 0)).ToArray();
        // W2-C: the ordered endpoints and their OWN values, so a reversed or two-cloud pair is never
        // reported as this computer or Dropbox and a missing side is never reported healthy.
        return new SyncCliFolderTreeData(pairId, folders, folders.Length, tree.LocalHierarchyAvailable,
            SyncCliSanitize.Message(tree.LocalHierarchyMessage), tree.DropboxHierarchyAvailable,
            SyncCliSanitize.Message(tree.DropboxHierarchyMessage),
            HierarchySide(tree.LeftEndpoint), HierarchySide(tree.RightEndpoint));
    }

    private static SyncCliHierarchySideData? HierarchySide(SyncEndpointHierarchySide? side) => side is null
        ? null
        : new SyncCliHierarchySideData(
            SyncEndpointPresentation.HierarchyTitle(side.Endpoint),
            SyncEndpointPresentation.HierarchyLocation(side.Endpoint),
            side.Available, side.Definitive, SyncCliSanitize.Message(side.Message), side.RootFiles);

    public async Task<SyncCliSelectionData> SetSelectionAsync(string pairId, string? path, string selection,
        CancellationToken cancellationToken)
    {
        var pairSet = await Host.LoadPairsAsync(cancellationToken);
        RequirePair(pairSet, pairId);
        var included = selection.ToLowerInvariant() switch
        {
            "include" => true,
            "exclude" => false,
            _ => throw new SyncCliUsageException("The --selection value must be include or exclude.")
        };
        var updated = path is null
            ? await Host.SetRootSelectionAsync(pairId, included, cancellationToken)
            : await Host.SetFolderSelectionAsync(pairId, path, included, null, cancellationToken);
        var pair = RequirePair(updated, pairId);
        return new SyncCliSelectionData(pairId, path ?? "<pair root>", included,
            SyncSelectionPolicy.Normalize(pair.SelectionRules).Count);
    }

    public Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path,
        string availability, CancellationToken cancellationToken) =>
        SetAvailabilityAsync(pairId, path, availability, cancellationToken, null);

    public async Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path,
        string availability, CancellationToken cancellationToken, Action<string>? progress)
    {
        RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), pairId);
        var keepOnDevice = availability.ToLowerInvariant() switch
        {
            "offline" or "keep-on-device" => true,
            "online-only" => false,
            _ => throw new SyncCliUsageException("The --availability value must be online-only or offline.")
        };
        var result = await Host.SetPathAvailabilityAsync(pairId, path, keepOnDevice, cancellationToken,
            entry => { progress?.Invoke(DescribeRunProgress(entry)); return Task.CompletedTask; });
        return new SyncCliAvailabilityData(pairId, result.RelativePath, !result.KeepOnDevice,
            $"files changed={result.FilesChanged} directories changed={result.DirectoriesChanged} " +
            $"failures={result.Failures.Count}",
            result.Failures.Select(error => SyncCliSanitize.Message(error) ?? "Storage change pending").ToArray());
    }

    // The adapter supplies transport input only. Discovery, channel validation, bounded download,
    // extraction and preference reconciliation remain the same production decisions in both paths.
    protected virtual SyncPublicUpdates CreatePublicUpdateClient() => new();

    public async Task<SyncCliUpdateData> UpdatesAsync(bool download, CancellationToken cancellationToken)
    {
        using var updates = CreatePublicUpdateClient();
        var update = await updates.FindAsync(BuildIdentity.InformationalVersion, cancellationToken);
        if (update is null)
            return new SyncCliUpdateData(ProductVersion(), false, null, null,
                "No compatible newer public release is offered for this installed identity.", false, null);
        if (!download)
            return new SyncCliUpdateData(ProductVersion(), true, update.Version, update.ArchiveBytes,
                "Checking and downloading never install; the signed installer still performs its own verified " +
                "handoff with its own consent.", false, null);
        var saved = Preferences.Load();
        if (saved.DownloadedUpdateFolder is { } retained)
            throw new SyncCliOperationException("A downloaded update is already retained. Apply or clear it in the app before downloading another package.");
        string? allocatedFolder = null;
        SyncDownloadedUpdate downloaded;
        try
        {
            downloaded = await updates.DownloadAsync(update, BuildIdentity.InformationalVersion, cancellationToken,
                folder =>
                {
                    allocatedFolder = folder;
                    return Preferences.SaveAsync(Preferences.Load() with { DownloadedUpdateFolder = folder }, cancellationToken);
                });
        }
        catch (Exception downloadError)
        {
            try { await ClearAbsentFailedUpdateAsync(Preferences, allocatedFolder); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("The update download failed and its retained pointer could not be reconciled; no installer was started.",
                    downloadError, cleanupError);
            }
            throw;
        }
        // Retain exactly this recorded package for the existing installed verifier. This releases the
        // download handles only; it does NOT launch an installer or establish signature verification.
        downloaded.ReleaseToInstaller();
        return new SyncCliUpdateData(ProductVersion(), true, update.Version, update.ArchiveBytes,
            "The package was downloaded and its published checksums matched. The retained package still needs " +
            "the installed verifier's signature and compatibility checks before installation.", true, null);
    }

    internal static Task ClearAbsentFailedUpdateAsync(UiPreferencesStore preferences, string? allocatedFolder)
    {
        // A failed download owns only this generated allocation. Never clear another download, or a
        // retained directory whose native cleanup was refused. Cancellation must not cancel this readback.
        if (allocatedFolder is null) return Task.CompletedTask;
        try { _ = File.GetAttributes(allocatedFolder); return Task.CompletedTask; }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var current = preferences.Load();
        return string.Equals(current.DownloadedUpdateFolder, allocatedFolder, StringComparison.Ordinal)
            ? preferences.SaveAsync(current with { DownloadedUpdateFolder = null }, CancellationToken.None)
            : Task.CompletedTask;
    }

    public async Task<SyncCliExplorerStatusData> LoadExplorerStatusAsync(CancellationToken cancellationToken)
    {
        var pairs = await Host.LoadPairsAsync(cancellationToken);
        return new SyncCliExplorerStatusData(
            SyncSharkRuntimePaths.IsInstalledProcess,
            Preferences.Load().ExplorerIntegrationEnabled, pairs.Pairs.Count, ProductRoot);
    }

    public async Task<SyncCliControlData> ShowApplicationAsync(CancellationToken cancellationToken)
    {
        await ShowApplicationCoreAsync(cancellationToken);
        return new SyncCliControlData("app show", true, "The running Sync Shark window was activated.");
    }

    public virtual Task<SyncCliControlData> UpdatesApplyAsync(string version, CancellationToken cancellationToken) =>
        throw new SyncCliUnsupportedException("Start the signed installed Sync Shark app before applying an update. " +
            "The CLI must use that app's verified installer handoff; no installer was started.");

    public async Task<SyncCliControlData> RewindCheckpointAsync(string pairId, Action<string> progress,
        CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        progress("Preparing a Rewind checkpoint for " + pairId + " through the existing checkpoint sequence.");
        var handoff = await Host.PrepareRewindAsync(pairId, cancellationToken);
        return new SyncCliControlData("rewind checkpoint", true, "Session " + handoff.Session.SessionId + " is " +
            handoff.Session.Status + ". Apply Rewind in the provider folder, then run 'rewind preview --pair " +
            pairId + " --yes' to read the rollback batch.");
    }

    public async Task<SyncCliControlData> RewindPreviewAsync(string pairId, CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        var session = await Host.PreviewRewindImportAsync(pairId, cancellationToken);
        return new SyncCliControlData("rewind preview", true, "Session " + session.SessionId + " is " +
            session.Status + ": " + (SyncCliSanitize.Message(session.Detail) ?? "the retained rollback batch was read."))
        {
            RewindConfirmation = SyncRewindApplyConfirmation.FromSession(session)
        };
    }

    public async Task<SyncCliControlData> RewindApplyAsync(string pairId, SyncRewindApplyConfirmation confirmation, Action<string> progress,
        CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        progress("Applying the retained Rewind rollback batch through the existing guarded executor.");
        var applied = await Host.ApplyRewindImportAsync(pairId, confirmation, cancellationToken);
        var complete = applied.Session.Status == SyncRewindStatus.AppliedPaused;
        var report = new SyncCliControlData("rewind apply", complete,
            "Session " + applied.Session.SessionId + " is " + applied.Session.Status + ": " +
            (SyncCliSanitize.Message(applied.Session.Detail) ?? "Inspect the retained Rewind path results.") +
            ". The pair stays paused until 'rewind resume --pair " + pairId + " --yes'.")
        {
            RewindConfirmation = confirmation
        };
        if (!complete) throw new SyncCliReviewException(report.Result, report);
        return report;
    }

    public async Task<SyncCliControlData> RewindResumeAsync(string pairId, CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        var session = await Host.ResumeAfterRewindAsync(pairId, cancellationToken);
        return new SyncCliControlData("rewind resume", true, "Session " + session.SessionId + " is " +
            session.Status + "; ordinary synchronization may continue.");
    }

    // ---- pair binding and editing ---------------------------------------------------------------

    public async Task<SyncCliRemoteFolderListData> LoadRemoteFoldersAsync(string accountId, string? parentPath,
        string? search, CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var browser = await Host.CreateFolderBrowserAsync(cancellationToken, binding.ConnectionId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var found = await browser.SearchFoldersAsync(search.Trim(), null, cancellationToken);
            return ProjectFolders(accountId, null, found.Folders, found.ContinueCursor is not null);
        }
        string? parentId = null;
        string? parentDisplay = null;
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            var parent = await ResolveRemoteFolderAsync(browser, parentPath, cancellationToken, binding.RootNamespaceId, binding.Provider);
            parentId = parent.FolderId;
            parentDisplay = parent.Path;
        }
        var page = await browser.ListFoldersAsync(parentId, cancellationToken);
        return ProjectFolders(accountId, parentDisplay, page.Folders, page.ContinueCursor is not null);
    }

    private static SyncCliRemoteFolderListData ProjectFolders(string accountId, string? parentPath,
        IReadOnlyList<DropboxFolderEntry> folders, bool hasMore) =>
        new(accountId, parentPath,
            folders.Select(folder => new SyncCliRemoteFolderData(folder.Name, folder.Path, folder.FolderId)).ToArray(),
            folders.Count, hasMore);

    private static async Task<DropboxFolderEntry> ResolveRemoteFolderAsync(IDropboxFolderBrowserService browser,
        string path, CancellationToken cancellationToken, string? namespaceId = null, string provider = SyncProviders.Dropbox)
    {
        var normalized = PilotPath.NormalizeDropboxRoot(path);
        if (normalized == "/" && provider == SyncProviders.GoogleDrive)
            return new DropboxFolderEntry("My Drive", "/", "root");
        if (normalized == "/" && namespaceId is not null)
            return new DropboxFolderEntry("Dropbox", "/", PilotPath.DropboxNamespaceRootIdentity(namespaceId));
        var name = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                   ?? throw new SyncCliUsageException("--path must name an exact remote folder.");
        var page = await browser.SearchFoldersAsync(name, null, cancellationToken);
        var matches = new Dictionary<string, DropboxFolderEntry>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pages = 0; ; pages++)
        {
            foreach (var folder in page.Folders.Where(folder => string.Equals(folder.Path, normalized, StringComparison.OrdinalIgnoreCase)))
                matches[folder.FolderId] = folder;
            if (page.ContinueCursor is null) break;
            if (pages >= 99 || !cursors.Add(page.ContinueCursor))
                throw new SyncCliReviewException("Folder search could not complete its bounded exact-identity check; nothing was bound.");
            page = await browser.ContinueFolderSearchAsync(page.ContinueCursor, cancellationToken);
        }
        if (matches.Count > 1)
            throw new SyncCliAmbiguousTargetException($"More than one Dropbox folder matches '{normalized}'.");
        if (matches.Count == 0)
            throw new SyncCliTargetException($"No Dropbox folder matches '{normalized}' exactly. Browse with " +
                "'pairs folders' and use the exact returned path; the CLI never chooses a near match.");
        return matches.Values.Single();
    }

    public async Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot,
        string? name, int? priority, bool enabled, CancellationToken cancellationToken)
        => await CreatePairAsync(accountId, remotePath, localRoot, name, priority, enabled, cancellationToken, null);

    public async Task<SyncCliPairData> CreatePairAsync(string accountId, string remotePath, string localRoot,
        string? name, int? priority, bool enabled, CancellationToken cancellationToken, Action<string>? progress)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pair binding requires Windows NTFS folder identities.");
        var binding = RequireConnection(accountId);
        var local = Path.GetFullPath(localRoot);
        // The provider browser and the binding admission are the application's own paths: exact remote
        // folder identity, the recommended recovery folder, and the local root identity are all verified
        // there before anything is retained.
        progress?.Invoke("Pair save: preparing the selected provider's folder browser and resolving the exact selected remote folder.");
        var browser = await Host.CreateFolderBrowserAsync(cancellationToken, binding.ConnectionId);
        var remote = await ResolveRemoteFolderAsync(browser, remotePath, cancellationToken, binding.RootNamespaceId, binding.Provider);
        progress?.Invoke("Pair save: locating or preparing the provider's owned Recovery container.");
        var recovery = await Host.EnsureRecommendedRecoveryFolderAsync(cancellationToken, binding.ConnectionId);
        var request = new SyncPairBindingRequest(null, name ?? remote.Name, local, remote, recovery, enabled,
            priority ?? 0, PropagateDeletions: false, ConnectionId: binding.ConnectionId);
        var result = await Host.BindPairAsync(request, cancellationToken, progress);
        return Project(result.Pair, result.PairSet.SelectedPairId);
    }

    public async Task<SyncCliPairData> EditPairAsync(string pairId, string? name, int? priority, bool? enabled,
        CancellationToken cancellationToken)
        => await EditPairAsync(pairId, name, priority, enabled, cancellationToken, null);

    public async Task<SyncCliPairData> EditPairAsync(string pairId, string? name, int? priority, bool? enabled,
        CancellationToken cancellationToken, Action<string>? progress)
    {
        if (name is null && priority is null && enabled is null)
            throw new SyncCliUsageException(
                "The command 'pairs edit' requires at least one of --name, --priority, --enable or --disable.");
        progress?.Invoke("Pair save: loading the exact retained pair before changing its name, priority or enabled state.");
        var pairSet = await Host.LoadPairsAsync(cancellationToken, progress);
        var pair = RequirePair(pairSet, pairId);
        var updated = SyncPairStore.NormalizePair(pair with
        {
            Name = name ?? pair.Name,
            Priority = priority ?? pair.Priority,
            Enabled = enabled ?? pair.Enabled
        });
        if (enabled is { } requestedEnabled)
        {
            // Editing the enabled flag is the same policy mutation as the dedicated
            // enable/disable commands and the GUI toggle: preserve its execution
            // lock, retained-effect refusal and revision increment. Never write this
            // policy bit through the metadata-only definition path below.
            pairSet = await Host.SetPairEnabledAsync(pairId, requestedEnabled, cancellationToken, progress);
            pair = RequirePair(pairSet, pairId);
            if (name is null && priority is null) return Project(pair, pairSet.SelectedPairId);
            updated = SyncPairStore.NormalizePair(pair with
            {
                Name = name ?? pair.Name,
                Priority = priority ?? pair.Priority
            });
        }
        progress?.Invoke("Pair save: committing the requested pair definition in the retained database.");
        await Host.Authority.RetainPairDefinitionAsync(updated,
            string.Equals(pairSet.SelectedPairId, pairId, StringComparison.Ordinal), cancellationToken);
        var refreshed = await Host.LoadPairsAsync(cancellationToken, progress);
        progress?.Invoke("Pair save: committed pair definition read back successfully; no sync was started by this save.");
        return Project(RequirePair(refreshed, pairId), refreshed.SelectedPairId);
    }

    // ---- shared endpoint authoring: pairs create --settings / pairs edit --settings ------------------

    /// <summary>
    /// Binds the two ordered endpoints one endpoint document names through the one shared
    /// endpoint-authoring boundary the pair editor also uses. Both folders are resolved to their exact
    /// identities here - the local folder through the product's own directory lease, a cloud folder
    /// through the exact account's own provider browser - while every validation, identity, approval and
    /// persistence decision stays in the shared contract. No user folder is created, substituted or
    /// resolved to a near match, and no second endpoint pipeline is introduced.
    /// </summary>
    public async Task<SyncCliPairData> BindEndpointPairAsync(SyncCliInvocation invocation,
        CancellationToken cancellationToken, Action<string>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Pair binding requires Windows NTFS folder identities.");
        var document = SyncCliEndpointAuthoring.Read(invocation.Settings);
        SyncPair? prior = null;
        if (invocation.Command == "pairs edit")
            prior = RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), Need(invocation.PairId, "--pair"));
        if (prior is not null && invocation.Generation is { } expectedRevision &&
            prior.PolicyRevision != expectedRevision)
            throw new SyncCliReviewException(
                "The pair policy revision changed. Read pairs show again; nothing changed.");
        // Both folders on this computer are identity-verified before any provider call, so an unusable
        // local folder is refused without touching the account at all.
        var leftLocal = document.Left.Kind == SyncEndpointKind.Local ? ResolveLocalEndpoint(document.Left) : null;
        var rightLocal = document.Right.Kind == SyncEndpointKind.Local ? ResolveLocalEndpoint(document.Right) : null;
        progress?.Invoke("Pair save: resolving the left endpoint's exact folder identity through its local or provider adapter.");
        var left = leftLocal ?? await ResolveCloudEndpointAsync(document.Left, cancellationToken);
        progress?.Invoke("Pair save: resolving the right endpoint's exact folder identity through its local or provider adapter.");
        var right = rightLocal ?? await ResolveCloudEndpointAsync(document.Right, cancellationToken);
        var name = invocation.Name ?? prior?.Name ?? DefaultEndpointName(right);
        if (string.IsNullOrWhiteSpace(name))
            throw new SyncCliUsageException("The endpoint document needs --name: neither the retained pair nor " +
                "the right endpoint supplies a folder name to retain.");
        SyncPair draft;
        try
        {
            draft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(left, right, document.Direction), name,
                prior);
        }
        catch (SyncEndpointRuntimeRefusedException refusal)
        {
            throw new SyncCliReviewException("This pair cannot be bound: " + refusal.Validation.Describe());
        }
        catch (NotSupportedException unsupported)
        {
            throw new SyncCliUnsupportedException(unsupported.Message);
        }
        // The CLI's own name, priority and enablement policy. A new pair stays disabled until it is
        // enabled explicitly and an edit keeps the retained pair's state unless --enable/--disable says
        // otherwise; every other policy member comes from the retained template only.
        draft = draft with
        {
            Enabled = invocation.Enable ? true : invocation.Disable ? false : prior?.Enabled ?? false,
            Priority = invocation.Priority ?? prior?.Priority ?? 0
        };
        try
        {
            var result = await Host.BindEndpointPairAsync(draft, prior?.Id, invocation.Generation ?? prior?.PolicyRevision,
                cancellationToken, progress: progress);
            return Project(result.Pair, result.PairSet.SelectedPairId);
        }
        catch (SyncEndpointRuntimeRefusedException refusal)
        {
            throw new SyncCliReviewException("This pair cannot be bound: " + refusal.Validation.Describe());
        }
        catch (SyncPairAuthoringReviewException review)
        {
            throw new SyncCliReviewException(review.Message);
        }
        catch (NotSupportedException unsupported)
        {
            throw new SyncCliUnsupportedException(unsupported.Message);
        }
    }

    /// <summary>
    /// One endpoint on this computer: the folder is opened through the product's own directory lease and
    /// must carry its complete NTFS volume and root file identity. A missing, replaced, reparse, unsafe or
    /// product-state folder is refused; the CLI never creates or substitutes a user folder here.
    /// </summary>
    internal SyncEndpoint ResolveLocalEndpoint(SyncCliEndpointRequest endpoint)
    {
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(endpoint.Path);
        }
        catch (ArgumentException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
        catch (IOException error)
        {
            throw new SyncCliTargetException(error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new SyncCliTargetException(error.Message);
        }
        using (lease)
        {
            var identity = SyncLocalEndpointIdentity.From(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
            if (identity is null || !identity.IsComplete)
                throw new SyncCliTargetException("The local folder '" + lease.Path + "' has no complete volume and " +
                    "root file identity, so it cannot be bound as an endpoint.");
            return SyncEndpoint.Local(lease.Path, identity);
        }
    }

    /// <summary>
    /// One cloud endpoint: the exact retained account the document names, checked against the provider its
    /// kind requires and against its verified storage namespace, then the exact provider folder path read
    /// through that same account's own browser. An unknown or ambiguous account, a foreign provider, an
    /// unverified namespace and a near or ambiguous folder match are all refused before anything is
    /// retained. No primary account is ever substituted for the one the document names.
    /// </summary>
    internal async Task<SyncEndpoint> ResolveCloudEndpointAsync(SyncCliEndpointRequest endpoint,
        CancellationToken cancellationToken)
    {
        var record = RequireEndpointAccount(endpoint.Account!, endpoint.Kind);
        var browser = EndpointBrowserFactoryForTest is { } fixture
            ? await fixture(record.ConnectionId, cancellationToken)
            : await Host.CreateFolderBrowserAsync(cancellationToken, record.ConnectionId);
        var folder = await ResolveRemoteFolderAsync(browser, endpoint.Path, cancellationToken,
            record.RootNamespaceId, record.Provider);
        return SyncEndpoint.Cloud(endpoint.Kind, record.ConnectionId, record.RootNamespaceId, folder.FolderId,
            folder.Path);
    }

    /// <summary>
    /// The exact retained active account an endpoint names, checked against the provider and namespace its
    /// kind requires. The provider records are the same ones the shared binding boundary verifies against,
    /// so the CLI cannot accept an account the binding host would later refuse.
    /// </summary>
    private ProviderConnectionRecord RequireEndpointAccount(string accountId, SyncEndpointKind kind)
    {
        var matches = Host.LoadProviderConnections().Where(candidate =>
            string.Equals(candidate.ConnectionId, accountId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
            throw new SyncCliAmbiguousTargetException($"More than one retained account matches '{accountId}'.");
        if (matches.Length == 0 || !matches[0].IsActive)
            throw new SyncCliTargetException($"No retained active account matches '{accountId}'. Run: " +
                $"{SyncCli.ExecutableName} --cli accounts list");
        var account = matches[0];
        if (!string.Equals(account.Provider, SyncEndpoints.ProviderLiteral(kind), StringComparison.OrdinalIgnoreCase))
            throw new SyncCliTargetException("Account '" + accountId + "' is a " +
                SyncProviders.DisplayName(account.Provider) + " account, so it cannot hold the " +
                SyncEndpointProviders.Get(kind).DisplayName + " endpoint this document names.");
        if (string.IsNullOrWhiteSpace(account.RootNamespaceId))
            throw new SyncCliReviewException("Account '" + accountId + "' has no verified storage namespace yet, " +
                "so one of its folders cannot be bound as an endpoint. Reconnect and verify the account first.");
        return account;
    }

    /// <summary>The retained name a right endpoint supplies: its own folder's last path segment, or null.</summary>
    private static string? DefaultEndpointName(SyncEndpoint endpoint)
    {
        var path = (endpoint.IsLocal ? endpoint.LocalPath : endpoint.FolderPath)?.TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(path)) return null;
        var separator = path.LastIndexOfAny(['/', '\\']);
        var name = separator < 0 ? path : path[(separator + 1)..];
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // ---- account-wide sharing and file requests -------------------------------------------------

    public async Task<SyncCliSharingListData> LoadSharingAsync(string accountId, CancellationToken cancellationToken)
    {
        return await LoadSharingAsync(accountId, null, 200, 0, cancellationToken);
    }

    // Exact namespace binding plus bounded user-controlled paging. The provider cursor never leaves this
    // method: the caller states only an offset and a limit, and an unknown namespace is refused.
    public async Task<SyncCliSharingListData> LoadSharingAsync(string accountId, string? namespaceId, int limit,
        int offset, CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        var target = RequireNamespace(client, namespaceId);
        var page = await PageLinksAsync(client, target, offset, limit, cancellationToken);
        return new SyncCliSharingListData(binding.ConnectionId, target, page.Items.Select(Project).ToArray(),
            page.Items.Count, page.HasMore, page.Incomplete, page.Issues);
    }

    private static string RequireNamespace(IAccountSharingClient client, string? namespaceId)
    {
        if (string.IsNullOrWhiteSpace(namespaceId)) return client.Namespaces[0];
        var matches = client.Namespaces.Where(value => string.Equals(value, namespaceId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            throw new SyncCliTargetException("Namespace '" + namespaceId + "' does not belong to this exact " +
                "account. Allowed namespaces for the account are published by 'accounts show'.");
        return matches[0];
    }

    // Bounded internal paging: at most 20 provider pages are read to reach an offset, and the caller never
    // sees a cursor or token.
    private static async Task<(IReadOnlyList<T> Items, bool HasMore, bool Incomplete, IReadOnlyList<AccountSharingIssue> Issues)> PageAsync<T>(
        Func<AccountSharingCursor?, CancellationToken, Task<AccountSharingPage<T>>> read, int offset, int limit,
        CancellationToken cancellationToken)
    {
        AccountSharingCursor? cursor = null;
        var skipped = 0;
        var incomplete = false;
        var issues = new List<AccountSharingIssue>();
        for (var page = 0; page < 20; page++)
        {
            var batch = await read(cursor, cancellationToken);
            incomplete |= batch.Incomplete;
            if (batch.Issues is { Count: > 0 }) { issues.AddRange(batch.Issues); incomplete = true; }
            var remaining = offset - skipped;
            if (remaining >= batch.Items.Count)
            {
                skipped += batch.Items.Count;
                cursor = batch.Next;
                if (cursor is null) return ([], false, incomplete, issues);
                continue;
            }
            var window = batch.Items.Skip(Math.Max(0, remaining)).Take(limit).ToArray();
            var hasMore = batch.Items.Count - Math.Max(0, remaining) > window.Length || batch.Next is not null;
            return (window, hasMore, incomplete, issues);
        }
        throw new SyncCliOperationException("The listing reached its bounded page limit; results are incomplete. Narrow the account namespace or use the application's continuing list.");
    }

    private static Task<(IReadOnlyList<AccountSharedLink> Items, bool HasMore, bool Incomplete, IReadOnlyList<AccountSharingIssue> Issues)> PageLinksAsync(
        IAccountSharingClient client, string namespaceId, int offset, int limit,
        CancellationToken cancellationToken) =>
        PageAsync<AccountSharedLink>((cursor, token) => client.ListLinksAsync(namespaceId, cursor, token), offset,
            limit, cancellationToken);

    public async Task<SyncCliFileRequestMappingData> LoadFileRequestsAsync(string accountId,
        CancellationToken cancellationToken)
    {
        return await LoadFileRequestsAsync(accountId, null, 200, 0, cancellationToken);
    }

    public async Task<SyncCliFileRequestMappingData> LoadFileRequestsAsync(string accountId, string? namespaceId,
        int limit, int offset, CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        var target = RequireNamespace(client, namespaceId);
        var page = await PageAsync<AccountFileRequest>(
            (cursor, token) => client.ListRequestsAsync(target, cursor, token), offset, limit, cancellationToken);
        return new SyncCliFileRequestMappingData(binding.ConnectionId, target, page.Items.Select(Project).ToArray(),
            page.Items.Count, page.HasMore, page.Incomplete, page.Issues);
    }

    // Settings edits. A password VALUE is never accepted from the command line: the provider requires an
    // interactive secret, so the CLI refuses that field with the explicit boundary instead of weakening it.
    public async Task<SyncCliSharedLinkData> ChangeSharedLinkAsync(string accountId, string? namespaceId,
        string url, string changeJson, CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        var target = RequireNamespace(client, namespaceId);
        using var document = System.Text.Json.JsonDocument.Parse(changeJson);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new SyncCliUsageException("The --settings value must be a JSON object of link settings.");
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "password", StringComparison.OrdinalIgnoreCase))
                throw new SyncCliReviewException("Setting or changing a shared-link password requires secure " +
                    "interactive input, which the CLI does not accept in an argument, JSON envelope or log. Use " +
                    "the Sync Shark window to set the password; the CLI can change audience, expiry, the password " +
                    "requirement and the download policy, but never the secret value.");
            if (property.Name is not ("audience" or "expires" or "clearExpiry" or "requirePassword" or
                "allowDownload" or "namespace" or "role" or "permissionId" or "revisionFingerprint"))
                throw new SyncCliUsageException("Unknown shared-link setting '" + property.Name + "'.");
        }
        var link = String(root, "permissionId") is { } permission
            ? await AccountSharingLookup.ExactAsync(client, target, url, permission, cancellationToken)
            : await RequireLinkAsync(client, target, url, cancellationToken);
        if (String(root, "revisionFingerprint") is { } revision && revision != link.RevisionFingerprint)
            throw new SyncCliTargetException("This link changed after it was listed. Refresh before editing it.");
        var change = new SharedLinkChange
        {
            Audience = String(root, "audience"),
            Role = String(root, "role"),
            ChangeExpiry = root.TryGetProperty("clearExpiry", out var clear) && clear.ValueKind ==
                System.Text.Json.JsonValueKind.True || root.TryGetProperty("expires", out _),
            Expires = root.TryGetProperty("expires", out var expiry) && expiry.ValueKind ==
                System.Text.Json.JsonValueKind.String
                ? DateTimeOffset.Parse(expiry.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal)
                : null,
            RequirePassword = Boolean(root, "requirePassword"),
            AllowDownload = Boolean(root, "allowDownload")
        };
        var updated = await client.ChangeLinkAsync(link, change, cancellationToken);
        return Project(updated);
    }

    private static string? String(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Boolean(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is System.Text.Json.JsonValueKind.True or
            System.Text.Json.JsonValueKind.False
            ? value.GetBoolean()
            : null;

    // An exact link is located across bounded pages so a later-page link is reachable without exposing cursors.
    private static async Task<AccountSharedLink> RequireLinkAsync(IAccountSharingClient client,
        string namespaceId, string url, CancellationToken cancellationToken)
    {
        if (client is IAccountSharingExactPermissionLookup)
            return await AccountSharingLookup.ExactAsync(client, namespaceId, url, null, cancellationToken);
        AccountSharingCursor? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var batch = await client.ListLinksAsync(namespaceId, cursor, cancellationToken);
            var matches = batch.Items.Where(link => string.Equals(link.Url, url, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
                throw new SyncCliAmbiguousTargetException("More than one shared link matches that exact URL.");
            if (matches.Length == 1) return matches[0];
            cursor = batch.Next;
            if (cursor is null) break;
        }
        throw new SyncCliTargetException("No shared link in this exact account and namespace matches that URL. " +
            "Run: SyncShark.exe --cli sharing list --account <id> --limit <n> --offset <n>");
    }

    public async Task<SyncCliFileRequestData> EditFileRequestAsync(string accountId, string? namespaceId, string id,
        string changeJson, CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        var target = RequireNamespace(client, namespaceId);
        using var document = System.Text.Json.JsonDocument.Parse(changeJson);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new SyncCliUsageException("The --settings value must be a JSON object of request settings.");
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("title" or "destination" or "description" or "open" or "deadline" or
                "namespace"))
                throw new SyncCliUsageException("Unknown file-request setting '" + property.Name + "'.");
        var expected = await client.GetRequestAsync(target, id, cancellationToken);
        var change = new FileRequestChange(
            String(root, "title") ?? expected.Title,
            String(root, "destination") ?? expected.Destination ?? string.Empty,
            String(root, "description") ?? expected.Description,
            Boolean(root, "open") ?? expected.IsOpen,
            ChangeDeadline: root.TryGetProperty("deadline", out _),
            Deadline: root.TryGetProperty("deadline", out var deadline) &&
                      deadline.ValueKind == System.Text.Json.JsonValueKind.String
                ? DateTimeOffset.Parse(deadline.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal)
                : null);
        var saved = await client.SaveRequestAsync(target, change, expected);
        return Project(saved);
    }

    public async Task<SyncCliSharedLinkData> CreateSharedLinkAsync(string accountId, string path, string audience,
        CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        // The item snapshot carries its own account and namespace identity; a foreign snapshot is refused
        // by the existing provider boundary before any link is created.
        var item = await client.GetItemAsync(client.Namespaces[0], path, cancellationToken);
        var link = await client.CreateLinkAsync(item, audience, cancellationToken);
        return Project(link);
    }

    public Task<SyncCliSharedLinkData> RevokeSharedLinkAsync(string accountId, string url,
        CancellationToken cancellationToken) => RevokeSharedLinkAsync(accountId, null, url, cancellationToken);

    public Task<IReadOnlyList<SharedLinkRevokeResult>> RevokeSharedLinksAsync(string selectionJson,
        CancellationToken cancellationToken)
    {
        var selection = SharedLinkRevokeBatch.Parse(selectionJson);
        var largest = selection.Select(SharedLinkRevokeBatch.MaximumReceipt).ToArray();
        if (!SyncCli.ResponseFits(largest, "sharing revoke-selected"))
            throw new SyncCliReviewException("This selection's complete per-link report exceeds the running-app response limit. " +
                "Select fewer exact links. No provider action was taken, and no identity or outcome was omitted.");
        return Host.RevokeSharedLinksAsync(selection, cancellationToken);
    }

    public async Task<SyncCliSharedLinkData> RevokeSharedLinkAsync(string accountId, string? namespaceId, string url,
        CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        var target = RequireNamespace(client, namespaceId);
        var expected = await RequireLinkAsync(client, target, url, cancellationToken);
        if (!expected.CanRevoke)
            throw new SyncCliReviewException("The provider reports that this link cannot be revoked by this account.");
        await client.RevokeLinkAsync(expected, cancellationToken);
        return Project(expected);
    }

    public Task<SyncCliFileRequestData> CreateFileRequestAsync(string accountId, string title,
        string destination, string? description, CancellationToken cancellationToken) =>
        CreateFileRequestAsync(accountId, null, title, destination, description, cancellationToken);

    public async Task<SyncCliFileRequestData> CreateFileRequestAsync(string accountId, string? namespaceId, string title,
        string destination, string? description, CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        // The provider boundary takes the retained request when editing and null when creating.
        var request = await client.SaveRequestAsync(RequireNamespace(client, namespaceId),
            new FileRequestChange(title, destination, description ?? string.Empty, IsOpen: true), null);
        return Project(request);
    }

    public Task<SyncCliFileRequestData> CloseFileRequestAsync(string accountId, string id,
        CancellationToken cancellationToken) => CloseFileRequestAsync(accountId, null, id, cancellationToken);

    public async Task<SyncCliFileRequestData> CloseFileRequestAsync(string accountId, string? namespaceId, string id,
        CancellationToken cancellationToken)
    {
        var binding = RequireConnection(accountId);
        var client = await Host.CreateAccountSharingClientAsync(binding.ConnectionId, cancellationToken);
        var expected = await client.GetRequestAsync(RequireNamespace(client, namespaceId), id, cancellationToken);
        // The provider boundary takes the expected snapshot and its own cancellation token.
        var closed = await client.SetRequestOpenAsync(expected, false, cancellationToken: cancellationToken);
        return Project(closed);
    }

    private static SyncCliSharedLinkData Project(AccountSharedLink link) =>
        new(link.Url, link.Name, link.Path, link.ItemKind, link.Audience, link.Access, link.Expires, link.CanRevoke,
            link.PasswordRequired, link.AllowDownload, link.AllowedAudiences, link.Provider, link.PermissionId,
            link.RevisionFingerprint, link.CanEdit, link.AllowedRoles, link.DownloadSettingKnown);

    private static SyncCliFileRequestData Project(AccountFileRequest request) =>
        new(request.Id, request.Url, request.Title, request.Destination, request.Description, request.Created,
            request.IsOpen, request.FileCount, request.Deadline);

    public Task<SyncCliDiscoveryStatusData> LoadDiscoveryStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The protected Discovery helper is native, registered and elevation-scoped. Its managed status
        // surface is read-only here; setup, check and configuration stay at that protected boundary.
        var installed = SyncDiscoveryClient.IsInstalled;
        return Task.FromResult(new SyncCliDiscoveryStatusData(installed, SyncDiscoveryClient.Status,
            SyncDiscoveryClient.WorkerPath, ConfigureSupported: false,
            installed
                ? "The protected Discovery helper is installed. Status is reported from its managed surface; " +
                  "changing Discovery configuration stays with its protected setup boundary."
                : "The protected Discovery helper is not installed in this environment; ordinary scanning is " +
                  "active and Discovery configuration requires its protected setup boundary."));
    }

    public async Task<SyncCliDiscoveryStatusData> CheckDiscoveryAsync(string pairId,
        CancellationToken cancellationToken)
    {
        // The existing guarded check: retained pair only, no planning or run, local root identity re-verified
        // around the read. Installation, trust and setup are never touched here.
        var result = await Host.CheckFastDiscoveryAsync(pairId, cancellationToken);
        return new SyncCliDiscoveryStatusData(SyncFastDiscovery.IsInstalled, SyncFastDiscovery.Status,
            SyncDiscoveryClient.WorkerPath, ConfigureSupported: false,
            "pair '" + result.PairName + "' ready=" + result.Ready + " metadataEntries=" +
            result.MetadataEntries + " elapsedMs=" + result.Elapsed.TotalMilliseconds.ToString("F0",
                System.Globalization.CultureInfo.InvariantCulture) + " status=" + result.Status);
    }

    private SyncPair RequirePair(SyncPairSet pairSet, string pairId)
    {
        var matches = pairSet.Pairs.Where(pair => string.Equals(pair.Id, pairId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
            throw new SyncCliAmbiguousTargetException($"More than one retained sync pair matches '{pairId}'.");
        if (matches.Length == 0)
            throw new SyncCliTargetException($"No retained sync pair matches '{pairId}'. Run: " +
                $"{SyncCli.ExecutableName} --cli pairs list");
        return matches[0];
    }

    private SyncAccountBinding RequireConnection(string accountId)
    {
        var matches = Host.LoadProviderConnections().Where(record =>
            string.Equals(record.ConnectionId, accountId, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1)
            throw new SyncCliAmbiguousTargetException($"More than one retained account matches '{accountId}'.");
        if (matches.Length == 0 || !matches[0].IsActive)
            throw new SyncCliTargetException($"No retained active account matches '{accountId}'. Run: " +
                $"{SyncCli.ExecutableName} --cli accounts list");
        return Host.LoadAccountBinding(accountId) ??
               throw new SyncCliTargetException($"The retained account '{accountId}' has no readable connection.");
    }

    private IReadOnlyList<SyncCliAccountData> LoadAccountRecords(SyncPairSet pairSet)
    {
        var primary = Host.LoadDropboxConnection(null);
        var records = Host.LoadProviderConnections();
        var accounts = new List<SyncCliAccountData>(records.Count);
        foreach (var record in records)
        {
            // CredentialReference, tokens and raw provider objects are deliberately never projected.
            var pairCount = pairSet.Pairs.Count(pair =>
                (string.IsNullOrWhiteSpace(pair.ConnectionId) ? primary?.ConnectionId : pair.ConnectionId) ==
                record.ConnectionId);
            accounts.Add(new SyncCliAccountData(record.ConnectionId, record.Provider, record.AccountId,
                record.DisplayName, record.AccountType,
                record.IsActive && string.Equals(record.ConnectionId, primary?.ConnectionId,
                    StringComparison.Ordinal),
                record.IsActive, Host.IsAccountPaused(record.ConnectionId), pairCount));
        }
        return accounts;
    }

    // Additional parity commands use the same host entry points as the corresponding app actions.
    public async Task<object> ExecuteSurfaceAsync(SyncCliInvocation invocation, CancellationToken cancellationToken)
        => await ExecuteSurfaceAsync(invocation, cancellationToken, null);

    public async Task<object> ExecuteSurfaceAsync(SyncCliInvocation invocation, CancellationToken cancellationToken,
        Action<string>? progress)
    {
        switch (invocation.Command)
        {
            case "accounts shared-drives":
                var sharedDriveBinding = await Host.SetGoogleSharedDrivesEnabledAsync(
                    Need(invocation.AccountId, "--account"), invocation.Flag("--enable"), cancellationToken);
                return new SyncCliAccountActionData(sharedDriveBinding.ConnectionId, "shared-drives",
                    sharedDriveBinding.SharedDrivesEnabled ? "Shared drives enabled. No files synced." :
                        "Shared drives disabled. Existing shared-drive pairs cannot sync until re-enabled. No files changed.", false);
            case "pairs create":
                // Reached only for the endpoint-authoring form the parser accepted; the legacy
                // --account/--path/--local form is dispatched directly and never routed here.
                return await BindEndpointPairAsync(invocation, cancellationToken, progress);
            case "pairs edit":
                return SyncCliEndpointAuthoring.IsEndpointDocument(invocation.Settings)
                    ? await BindEndpointPairAsync(invocation, cancellationToken, progress)
                    : await BindEditedPairAsync(invocation, cancellationToken, progress);
            case "pairs duplicate":
                return await BindEditedPairAsync(invocation, cancellationToken, progress);
            case "storage devices":
                return await Task.Run(SyncStorageInventory.ReadDeviceSpace, cancellationToken);
            case "storage inventory":
                return await Host.LoadStorageInventoryAsync(invocation.AccountId, invocation.PairId, invocation.Offset,
                    invocation.Limit, cancellationToken);
            case "storage inspect":
                return await Host.InspectStorageRootAsync(Need(invocation.Value("--local"), "--local"), invocation.PairId,
                    invocation.AccountId, invocation.Limit, cancellationToken);
            case "storage show":
            case "storage files":
            case "storage default":
            case "storage root":
            case "storage folders":
            case "storage file":
            case "storage reset-file":
            case "storage enable":
                return await StorageAsync(invocation, cancellationToken, progress);
            case "conflicts list":
                return await LoadConflictPageAsync(invocation.PairId, invocation.AccountId, invocation.Search,
                    invocation.Value("--group"), invocation.Limit, invocation.Offset, cancellationToken);
            case "conflicts select":
                return await CaptureConflictsAsync(invocation, cancellationToken);
            case "conflicts resolve-selection":
                return await ResolveConflictSelectionAsync(invocation, cancellationToken, progress);
            case "recovery list":
                if (invocation.AccountId is not null)
                    await RequireBoundPairAsync(invocation.PairId!, invocation.AccountId, null, cancellationToken);
                return await LoadRecoveryPageAsync(invocation.PairId!, invocation.Search, invocation.Limit,
                    invocation.Offset, invocation.Flag("--deleted"), cancellationToken);
            case "recovery revisions":
                await RequireBoundPairAsync(invocation.PairId!, Need(invocation.AccountId, "--account"), null, cancellationToken);
                var revisions = await Host.LoadDropboxRevisionCatalogAsync(invocation.PairId!, Need(invocation.Path, "--path"), cancellationToken);
                return new SyncCliRecoveryListData(invocation.PairId!, revisions.Select(Project).ToArray(), revisions.Count,
                    revisions.Count, false, revisions.Count, revisions.Sum(item => item.Size ?? 0), 0, null);
            case "recovery restore":
                await RequireBoundPairAsync(invocation.PairId!, Need(invocation.AccountId, "--account"), null, cancellationToken);
                if (invocation.Path is null)
                    return await RestoreRecoveryAsync(invocation.PairId!, Need(invocation.Key, "--key"), invocation.Copy, cancellationToken);
                var versions = await Host.LoadDropboxRevisionCatalogAsync(invocation.PairId!, invocation.Path, cancellationToken);
                var version = versions.SingleOrDefault(item => item.Key == invocation.Key)
                    ?? throw new SyncCliTargetException("The exact revision is not retained for this pair and path.");
                var restored = await Host.RestoreRecoveryAsync(version, invocation.Copy, cancellationToken);
                return new SyncCliRecoveryActionData(invocation.PairId!, version.Key, invocation.Copy ? "restore-copy" : "restore",
                    "Restored to '" + restored.TargetRelativePath + "': " + SyncCliSanitize.Message(restored.Detail));
            case "jobs status":
                return SyncCliJobData.Project(Host.ActiveRun, Host.HasActiveRun, "jobs status");
            case "jobs watch":
                return await WatchJobAsync(invocation, cancellationToken);
            default: throw new SyncCliUsageException("No surface adapter for " + invocation.Command + ".");
        }
    }

    private static string Need(string? value, string option) => !string.IsNullOrWhiteSpace(value) ? value :
        throw new SyncCliUsageException("This command requires " + option + ".");

    private async Task<SyncPair> RequireBoundPairAsync(string pairId, string accountId, long? generation,
        CancellationToken cancellationToken, Action<string>? progress = null)
    {
        var pair = RequirePair(await Host.LoadPairsAsync(cancellationToken, progress), pairId);
        var account = RequireConnection(accountId);
        var actual = Host.LoadDropboxConnection(pair.ConnectionId)?.ConnectionId ?? pair.ConnectionId;
        if (!string.Equals(actual, account.ConnectionId, StringComparison.Ordinal))
            throw new SyncCliTargetException("This exact pair belongs to a different account. Nothing changed.");
        if (generation is { } revision && pair.PolicyRevision != revision)
            throw new SyncCliReviewException("The pair policy revision changed. Read pairs show again; nothing changed.");
        return pair;
    }

    private async Task<SyncCliPairData> BindEditedPairAsync(SyncCliInvocation invocation, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pair binding requires Windows folder identities.");
        var accountId = Need(invocation.AccountId, "--account");
        var generation = invocation.Generation ?? throw new SyncCliUsageException("Binding requires --generation <policy-revision>.");
        progress?.Invoke("Pair save: loading the exact retained pair and checking its account and policy revision; no files are being synced.");
        var prior = await RequireBoundPairAsync(invocation.PairId!, accountId, generation, cancellationToken, progress);
        var duplicate = invocation.Command == "pairs duplicate";
        using var document = JsonDocument.Parse(invocation.Settings ?? "{}");
        var settings = document.RootElement;
        ValidateMembers(settings, "direction", "propagateDeletions", "recoveryRetentionDays", "recoveryPath",
            "filesOnDemandEnabled", "defaultStorage", "selectionDefaultIncluded", "selectionRules", "ignoreRules", "directoryStorageRules");
        var directionText = ReadString(settings, "direction", prior.Direction.ToString());
        if (!Enum.TryParse<SyncMode>(directionText, true, out var direction) || !Enum.IsDefined(direction))
            throw new SyncCliUsageException("direction must be TwoWay, UploadOnly or DownloadOnly.");
        var retention = prior.RecoveryRetentionDays;
        if (settings.TryGetProperty("recoveryRetentionDays", out var days))
        {
            if (days.ValueKind == JsonValueKind.Null) retention = null;
            else if (days.TryGetInt32(out var count) && count is 30 or 90 or 180) retention = count;
            else throw new SyncCliUsageException("recoveryRetentionDays must be 30, 90, 180 or null (never).");
        }
        var filesOnDemand = ReadBool(settings, "filesOnDemandEnabled", prior.FilesOnDemandEnabled);
        if (!filesOnDemand && prior.FilesOnDemandEnabled && !duplicate)
            throw new SyncCliUsageException("Existing binding keeps managed storage enabled. Choose defaultStorage=offline to keep new files local; use storage disable in the running signed app to make all managed files local and turn it off.");
        var storage = settings.TryGetProperty("defaultStorage", out var storageValue)
            ? ParseStorage(storageValue.GetString(), false)!.Value : SyncDirectoryStoragePolicy.Default(prior);
        var selection = ReadRules<SyncSelectionRule>(settings, "selectionRules", prior.SelectionRules);
        var ignores = ReadRules<SyncIgnoreRule>(settings, "ignoreRules", prior.IgnoreRules);
        var directories = ReadRules<SyncDirectoryStorageRule>(settings, "directoryStorageRules", prior.DirectoryStorageRules);
        // Validate all editable policy data before browser/provider calls or binding side effects.
        selection = SyncSelectionPolicy.Normalize(selection);
        ignores = SyncIgnorePolicy.Normalize(ignores);
        directories = SyncDirectoryStoragePolicy.Normalize(directories);
        var defaultIncluded = ReadBool(settings, "selectionDefaultIncluded", prior.SelectionDefaultIncluded);
        var propagate = ReadBool(settings, "propagateDeletions", prior.PropagateDeletions);
        var name = duplicate ? Need(invocation.Name, "--name") : invocation.Name ?? prior.Name;
        var local = invocation.LocalRoot ?? prior.LocalRoot;
        var remotePath = invocation.Path ?? prior.DropboxRoot;
        var replacingRoots = !Path.GetFullPath(local).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(prior.LocalRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !PilotPath.NormalizeDropboxRoot(remotePath).Equals(prior.DropboxRoot, StringComparison.OrdinalIgnoreCase);
        if (duplicate && !replacingRoots)
            throw new SyncCliUsageException("A duplicate must use a different local folder or Dropbox folder.");
        if (duplicate || replacingRoots)
        {
            selection = selection.Select(rule => rule with { StableFolderIdentity = null }).ToArray();
            directories = directories.Select(rule => rule with { LocalDirectoryId = null, RemoteDirectoryId = null }).ToArray();
        }
        progress?.Invoke("Pair save: preparing the selected provider's folder browser to verify endpoint identities.");
        var browser = await Host.CreateFolderBrowserAsync(cancellationToken, accountId);
        var remote = invocation.Path is null ? new DropboxFolderEntry(Path.GetFileName(prior.DropboxRoot), prior.DropboxRoot, prior.DropboxFolderId)
            : await ResolveRemoteFolderAsync(browser, remotePath, cancellationToken, RequireConnection(accountId).RootNamespaceId, prior.Provider);
        DropboxFolderEntry? recovery;
        if (settings.TryGetProperty("recoveryPath", out var recoveryPath))
            recovery = await ResolveRemoteFolderAsync(browser, Need(recoveryPath.GetString(), "recoveryPath"), cancellationToken,
                provider: prior.Provider);
        else if (prior.RecoveryRoot is { } retained)
            recovery = new DropboxFolderEntry(Path.GetFileName(retained.RootPath), retained.RootPath, retained.RootFolderId);
        else
            recovery = await Host.EnsureRecommendedRecoveryFolderAsync(cancellationToken, accountId);
        if (recovery is not null) SyncDropboxArtifactPaths.RequireRecoveryContainer(recovery.Path);
        // Recheck after browsing. BindPairAsync also checks ExpectedPolicyRevision at its authority boundary.
        progress?.Invoke("Pair save: rechecking the retained policy revision before saving the verified binding.");
        await RequireBoundPairAsync(prior.Id, accountId, generation, cancellationToken, progress);
        var request = new SyncPairBindingRequest(duplicate ? null : prior.Id, name, local, remote, recovery,
            invocation.Enable ? true : invocation.Disable ? false : prior.Enabled, invocation.Priority ?? prior.Priority,
            propagate, filesOnDemand, selection, defaultIncluded, retention, direction, directories,
            accountId, storage, ignores, ExpectedPolicyRevision: duplicate ? null : generation);
        var result = await Host.BindPairAsync(request, cancellationToken, progress);
        if (result.Pair.FilesOnDemandEnabled) await Host.ApplyPendingDirectoryStorageAsync(result.Pair.Id, cancellationToken,
            entry => { progress?.Invoke(DescribeRunProgress(entry)); return Task.CompletedTask; });
        return await LoadPairAsync(result.Pair.Id, cancellationToken);
    }

    private static IReadOnlyList<T> ReadRules<T>(JsonElement root, string property, IReadOnlyList<T>? current)
    {
        if (!root.TryGetProperty(property, out var value)) return current ?? [];
        if (value.ValueKind != JsonValueKind.Array) throw new SyncCliUsageException(property + " must be a JSON array.");
        return JsonSerializer.Deserialize<T[]>(value.GetRawText(), SurfaceJson) ?? [];
    }

    private static readonly JsonSerializerOptions SurfaceJson = new() {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static SyncDirectoryStorage? ParseStorage(string? value, bool inherit) => value?.ToLowerInvariant() switch
    {
        "offline" or "alwaysavailable" => SyncDirectoryStorage.AlwaysAvailable,
        "online-only" or "cloudonly" => SyncDirectoryStorage.CloudOnly,
        "inherit" when inherit => null,
        _ => throw new SyncCliUsageException("Storage must be offline, online-only" + (inherit ? " or inherit." : "."))
    };

    private async Task<object> StorageAsync(SyncCliInvocation invocation, CancellationToken cancellationToken,
        Action<string>? progress)
    {
        var account = Need(invocation.AccountId, "--account");
        var read = invocation.Command is "storage show" or "storage files";
        if (!read && invocation.Generation is null) throw new SyncCliUsageException("Storage changes require --generation <policy-revision>.");
        var pair = await RequireBoundPairAsync(invocation.PairId!, account, invocation.Generation, cancellationToken, progress);
        Task Report(SyncAuthorityBatchProgress entry)
        {
            progress?.Invoke(DescribeRunProgress(entry));
            return Task.CompletedTask;
        }
        if (invocation.Command == "storage show")
        {
            var overview = await Host.LoadStorageOverviewAsync(pair.Id, account, invocation.Offset, invocation.Search,
                invocation.Value("--filter"), cancellationToken);
            return new SyncCliStorageData(Project(overview.Pair, null), overview.Folders, overview.KnownSyncedBytes,
                overview.KnownSyncedFiles, overview.IndexedUtc);
        }
        if (invocation.Command == "storage files")
        {
            var rules = (pair.FileStorageRules ?? []).Where(rule => invocation.Search is null ||
                rule.RelativePath.Contains(invocation.Search, StringComparison.OrdinalIgnoreCase)).ToArray();
            var page = rules.Skip(invocation.Offset).Take(invocation.Limit).ToArray();
            return new SyncCliStorageFilesData(pair.Id, pair.PolicyRevision, page, invocation.Offset, page.Length,
                rules.Length > invocation.Offset + page.Length);
        }
        SyncCloudAvailabilityResult result;
        var revision = invocation.Generation!.Value;
        if (invocation.Command == "storage reset-file")
        {
            var path = PilotPath.NormalizeRelative(Need(invocation.Path, "--path"));
            if (!ulong.TryParse(Need(invocation.Key, "--key"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var identity) || identity == 0)
                throw new SyncCliUsageException("--key must be the exact nonzero localFileId from storage files.");
            var rule = (pair.FileStorageRules ?? []).SingleOrDefault(item => item.RelativePath == path && item.LocalFileId == identity)
                ?? throw new SyncCliTargetException("The exact path and file identity do not match a retained override.");
            result = await Host.ResetManagedFileStorageAsync(pair.Id, account, rule, revision, cancellationToken, Report);
        }
        else if (invocation.Command == "storage enable")
            result = await Host.ChangeManagedStorageAsync(pair.Id, account, null, SyncDirectoryStoragePolicy.Default(pair), revision, cancellationToken, Report);
        else
        {
            var storage = ParseStorage(invocation.Availability, invocation.Command == "storage folders");
            switch (invocation.Command)
            {
                case "storage default":
                    result = await Host.ChangeManagedStorageAsync(pair.Id, account, null, storage, revision, cancellationToken, Report); break;
                case "storage root":
                    result = await Host.ChangeManagedRootStorageAsync(pair.Id, account, storage!.Value, revision, cancellationToken, Report); break;
                case "storage file":
                    result = await Host.ChangeManagedFileStorageAsync(pair.Id, account, Need(invocation.Path, "--path"), storage!.Value, revision, cancellationToken, Report); break;
                default:
                    var folders = JsonSerializer.Deserialize<string[]>(Need(invocation.Settings, "--settings"))
                        ?? throw new SyncCliUsageException("--settings must be an array of exact folder paths.");
                    if (folders.Length is < 1 or > 100 || folders.Any(string.IsNullOrWhiteSpace))
                        throw new SyncCliUsageException("Select 1..100 exact folder paths.");
                    var normalized = folders.Select(PilotPath.NormalizeRelative).ToArray();
                    if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
                        throw new SyncCliUsageException("Each folder must be selected once.");
                    result = await Host.ChangeManagedStorageBatchAsync(pair.Id, account, normalized, storage, revision, cancellationToken, onProgress: Report); break;
            }
        }
        var updated = RequirePair(await Host.LoadPairsAsync(cancellationToken), pair.Id);
        return new SyncCliStorageResultData(pair.Id, updated.PolicyRevision, invocation.Command,
            result.FilesChanged, result.DirectoriesChanged, result.Failures.Select(error => SyncCliSanitize.Message(error) ?? "Storage change pending").ToArray());
    }

    private async Task<SyncCliConflictListData> LoadConflictPageAsync(string? pairId, string? accountId, string? search,
        string? group, int limit, int offset, CancellationToken cancellationToken)
    {
        if (accountId is not null) RequireConnection(accountId);
        if (pairId is not null)
        {
            if (accountId is not null) await RequireBoundPairAsync(pairId, accountId, null, cancellationToken);
            else await LoadPairAsync(pairId, cancellationToken);
        }
        var items = new List<SyncCliConflictData>();
        var total = 0;
        for (var index = 0;;)
        {
            var page = await Host.LoadOpenConflictComparisonsPageAsync(500, index, search, group, cancellationToken, accountId);
            foreach (var comparison in page)
            {
                if (pairId is not null && comparison.PairId != pairId) continue;
                if (total >= offset && items.Count < limit) items.Add(Project(comparison));
                total = checked(total + 1);
            }
            if (page.Count < 500) break;
            index = checked(index + page.Count);
        }
        return new SyncCliConflictListData(items, limit, offset, items.Count, total > offset + items.Count, total);
    }

    private async Task<SyncCliConflictSelectionData> CaptureConflictsAsync(SyncCliInvocation invocation, CancellationToken cancellationToken)
    {
        var account = Need(invocation.AccountId, "--account");
        RequireConnection(account);
        if (invocation.PairId is not null) await RequireBoundPairAsync(invocation.PairId, account, null, cancellationToken);
        var captured = await Host.CaptureOpenConflictSelectionAsync(invocation.Search, invocation.Value("--group"), cancellationToken, account);
        var selected = captured.Where(item => invocation.PairId is null || item.PairId == invocation.PairId).ToArray();
        return new SyncCliConflictSelectionData(selected, selected.Length);
    }

    private async Task<object> ResolveConflictSelectionAsync(SyncCliInvocation invocation, CancellationToken cancellationToken,
        Action<string>? progress)
    {
        var account = Need(invocation.AccountId, "--account");
        RequireConnection(account);
        if (!Enum.TryParse<SyncAuthorityConflictResolutionKind>(invocation.Choice, true, out var choice) || !Enum.IsDefined(choice))
            throw new SyncCliUsageException("Choose KeepLocal, KeepDropbox or PreserveBoth.");
        var path = Need(invocation.Choices, "--choices");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new SyncCliUsageException("The conflict snapshot is larger than 8 MiB.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var envelope)) root = envelope;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var array)) root = array;
        if (root.ValueKind != JsonValueKind.Array) throw new SyncCliUsageException("The snapshot must contain an array of exact conflict selections.");
        var selected = JsonSerializer.Deserialize<SyncConflictSelection[]>(root.GetRawText(), SurfaceJson)
            ?? throw new SyncCliUsageException("The snapshot must contain an array of exact conflict selections.");
        if (selected.Length == 0 || selected.Select(item => item.ConflictId).Distinct().Count() != selected.Length)
            throw new SyncCliUsageException("Select at least one conflict, each exactly once.");
        // The shared host validates the complete exact selection before admission and again after
        // admission. Duplicating that full projection here added seconds without another safeguard.
        var batch = await Host.ResolveConflictSelectionCommandAsync(selected.Select(item =>
            new SyncConflictResolutionRequest(item, choice)).ToArray(), account, cancellationToken,
            entry => { progress?.Invoke(DescribeRunProgress(entry)); return Task.CompletedTask; }, progress);
        var results = batch.Items.Select(item => new SyncCliRunData(item.Pair.Id, item.Pair.Name,
            item.Result?.Uploaded ?? 0, item.Result?.Downloaded ?? 0, item.Result?.Identical ?? 0,
            item.Result?.Conflicts ?? 0, item.Result?.Blocked ?? 0, batch.Stopped || item.Result?.Stopped == true)).ToArray();
        var data = new SyncCliConflictResolutionData(results,
            batch.Items.Sum(item => item.Result?.Recovered ?? 0), batch.Items);
        if (batch.Stopped || data.Count != selected.Length)
            throw new SyncCliException(batch.Stopped ? "canceled" : "needs_review",
                $"Resolved {data.Count} of {selected.Length} exact conflicts; retained per-file results identify completed, uncertain and unattempted targets. No completed resolution is replayed.",
                batch.Stopped ? SyncCliExitCode.Canceled : SyncCliExitCode.NeedsReview, data: data);
        return data;
    }

    private async Task<SyncCliRecoveryListData> LoadRecoveryPageAsync(string pairId, string? search, int limit, int offset,
        bool deleted, CancellationToken cancellationToken)
    {
        await LoadPairAsync(pairId, cancellationToken);
        var items = new List<SyncCliRecoveryItemData>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var archiveOffset = 0; string? cursor = null; var readDeleted = deleted; var seen = 0; var more = false;
        var positions = new HashSet<(int, string?, bool)>();
        while (true)
        {
            if (!positions.Add((archiveOffset, cursor, readDeleted)))
                throw new SyncCliOperationException("Recovery pagination did not advance; no recovery effects were performed.");
            var page = await Host.LoadRecoveryCatalogPageAsync(pairId, archiveOffset, cursor, readDeleted, 499, search, cancellationToken);
            foreach (var warning in page.Warnings ?? []) warnings.Add(warning);
            foreach (var item in page.Items)
            {
                if (seen++ < offset) continue;
                if (items.Count == limit) { more = true; break; }
                items.Add(Project(item));
            }
            if (more || !page.HasMoreArchives && !page.HasMoreDeleted) break;
            archiveOffset = page.NextArchiveOffset; cursor = page.DeletedCursor; readDeleted = page.HasMoreDeleted;
        }
        var usage = await Host.LoadRecoveryUsageAsync(pairId, cancellationToken);
        return new SyncCliRecoveryListData(pairId, items, limit, items.Count, more, usage.ItemCount, usage.Size,
            usage.CleanupEligibleCount, usage.RetentionDays, offset, checked(offset + items.Count), warnings.ToArray());
    }

    private async Task<SyncCliJobData> WatchJobAsync(SyncCliInvocation invocation, CancellationToken cancellationToken)
    {
        var id = Need(invocation.Key, "--key");
        var seconds = invocation.Value("--seconds") is { } value && int.TryParse(value, out var parsed) && parsed is >= 1 and <= 60
            ? parsed : throw new SyncCliUsageException("jobs watch requires --seconds 1..60.");
        var snapshot = Host.ActiveRun;
        if (snapshot is null || snapshot.Id != id) throw new SyncCliTargetException("The exact requested job is not active.");
        var stop = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < stop)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            var current = Host.ActiveRun;
            if (current is null || current.Id != id) return SyncCliJobData.Project(snapshot, false, "jobs watch", true);
            if (current != snapshot) return SyncCliJobData.Project(current, true, "jobs watch");
        }
        var final = Host.ActiveRun;
        return final is not null && final.Id == id ? SyncCliJobData.Project(final, true, "jobs watch")
            : SyncCliJobData.Project(snapshot, false, "jobs watch", true);
    }

    /// <summary>
    /// The CLI reaches exactly the shared local Create Folder action the pair editor uses, so the parent
    /// rules, the name rules and the exclusive creation cannot drift apart between the two surfaces.
    /// Nothing here touches a provider, an account, a pair or sync state.
    /// </summary>
    public Task<object> CreateLocalFolderAsync(string parentPath, string name, CancellationToken cancellationToken)
    {
        string created;
        try
        {
            created = LocalFolderActions.Create(parentPath, name, cancellationToken);
        }
        catch (ArgumentException error)
        {
            throw new SyncCliUsageException(error.Message);
        }
        catch (DirectoryNotFoundException error)
        {
            throw new SyncCliTargetException(error.Message);
        }
        catch (IOException error)
        {
            throw new SyncCliOperationException(error.Message, error);
        }
        return Task.FromResult<object>(new SyncCliLocalFolderCreateData(parentPath, name, created));
    }

    private static SyncCliPairData Project(SyncPair pair, string? selectedPairId) =>
        new(pair.Id, pair.Name, pair.Provider, pair.ConnectionId, pair.LocalRoot, pair.DropboxRoot, pair.Enabled,
            pair.Priority, pair.Direction.ToString(), pair.PolicyRevision, pair.InitialReviewApproved,
            pair.InitialReviewApproved == false, pair.FilesOnDemandEnabled,
            SyncSelectionPolicy.Normalize(pair.SelectionRules).Count,
            SyncIgnorePolicy.Normalize(pair.IgnoreRules).Count,
            string.Equals(pair.Id, selectedPairId, StringComparison.Ordinal), pair.PropagateDeletions,
            pair.RecoveryRetentionDays, pair.RecoveryRoot, SyncDirectoryStoragePolicy.Default(pair).ToString(),
            pair.SelectionDefaultIncluded, pair.SelectionRules ?? [], pair.IgnoreRules ?? [],
            pair.DirectoryStorageRules ?? [], pair.FileStorageRules ?? [], pair.DirectoryStorageApplyPaths ?? [],
            pair.DropboxFolderId, pair.EndpointDocument);

    protected static string ProductVersion()
    {
        var informational = BuildIdentity.InformationalVersion;
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        return separator < 0 ? informational : informational[..separator];
    }
}

/// <summary>
/// The local folder the CLI just created: the exact parent, the exact single name and the exact created
/// path. No pair, account, provider or sync state is implied or reported, because none is touched.
/// </summary>
public sealed record SyncCliLocalFolderCreateData(string ParentPath, string Name, string Path);

/// <summary>
/// One endpoint an endpoint-authoring document names: the closed provider kind, the exact local folder
/// path or the provider folder path inside a named account, and the exact cloud connection id. It carries
/// no stable identity of its own: the local volume and root file id, the verified namespace and the
/// provider folder id are all read from the product's own lease and provider browser when the pair is
/// bound, so a document can never assert an identity the product has not just verified.
/// </summary>
public sealed record SyncCliEndpointRequest(SyncEndpointKind Kind, string Path, string? Account);

/// <summary>
/// The validated endpoint-authoring document: both ordered endpoints together and the direction.
/// Direction is logical left to right, so UploadOnly moves left to right and DownloadOnly moves right to
/// left; it is stored in the pair's existing direction member and never becomes a second source of truth.
/// </summary>
public sealed record SyncCliEndpointDocument(SyncCliEndpointRequest Left, SyncCliEndpointRequest Right,
    SyncMode Direction);

/// <summary>
/// The `--settings` endpoint-authoring document for `pairs create` and `pairs edit`: recognition and
/// exact validation, shared by the registry's parser and the binding operations so the accepted shape
/// cannot drift between them. Unknown or duplicate members, an unknown provider or direction, one side
/// without the other, a relative or empty local path, a missing cloud account and a local account key are
/// all refused here, and the parser calls this before product state is opened, so a malformed document can
/// never reach a provider or a binding.
/// </summary>
public static class SyncCliEndpointAuthoring
{
    private static readonly string[] DocumentMembers = ["left", "right", "direction"];
    private static readonly string[] EndpointMembers = ["provider", "path", "account"];

    /// <summary>
    /// True when the value is a JSON object carrying an endpoint-document member. A non-object or
    /// malformed value is not an endpoint document, so the caller's existing handling stays in charge of
    /// it and no command silently changes shape.
    /// </summary>
    public static bool IsEndpointDocument(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            // Direction is also a valid legacy policy edit. Only an explicit endpoint member
            // selects endpoint rebinding; changing a policy must not demand new root identities.
            return document.RootElement.TryGetProperty("left", out _) ||
                document.RootElement.TryGetProperty("right", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The validated document, or the exact usage refusal for the shape that was supplied.</summary>
    public static SyncCliEndpointDocument Read(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            throw new SyncCliUsageException("An endpoint document is required: " +
                "{\"left\":{\"provider\":\"local\",\"path\":\"C:\\\\Folder\"},\"right\":{\"provider\":\"dropbox\"," +
                "\"path\":\"/Photos\",\"account\":\"<exact-connection-id>\"},\"direction\":\"TwoWay\"}.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException error)
        {
            throw new SyncCliUsageException("The --settings endpoint document is not valid JSON: " + error.Message);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new SyncCliUsageException("The --settings endpoint document must be one JSON object with " +
                    "left, right and direction.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
                if (!DocumentMembers.Contains(member.Name, StringComparer.Ordinal) || !seen.Add(member.Name))
                    throw new SyncCliUsageException("Unknown or duplicate endpoint document member '" +
                        member.Name + "'. Allowed members: left, right, direction.");
            if (!root.TryGetProperty("left", out var left) || !root.TryGetProperty("right", out var right))
                throw new SyncCliUsageException("The endpoint document requires both left and right endpoints " +
                    "together; one endpoint on its own is never bound.");
            if (!root.TryGetProperty("direction", out var direction))
                throw new SyncCliUsageException("The endpoint document requires direction: TwoWay, UploadOnly or " +
                    "DownloadOnly. Upload-only means left to right and download-only means right to left.");
            var directionText = direction.ValueKind == JsonValueKind.String ? direction.GetString() : null;
            var mode = directionText switch
            {
                "TwoWay" => SyncMode.TwoWay,
                "UploadOnly" => SyncMode.UploadOnly,
                "DownloadOnly" => SyncMode.DownloadOnly,
                _ => throw new SyncCliUsageException("direction must be TwoWay, UploadOnly or DownloadOnly.")
            };
            return new SyncCliEndpointDocument(ReadEndpoint(left, "left"), ReadEndpoint(right, "right"), mode);
        }
    }

    private static SyncCliEndpointRequest ReadEndpoint(JsonElement element, string side)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new SyncCliUsageException("The " + side + " endpoint must be a JSON object with provider, path " +
                "and, for a cloud endpoint, account.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
            if (!EndpointMembers.Contains(member.Name, StringComparer.Ordinal) || !seen.Add(member.Name))
                throw new SyncCliUsageException("Unknown or duplicate " + side + " endpoint member '" +
                    member.Name + "'. Allowed members: provider, path, account.");
        var provider = Member(element, "provider", side);
        if (provider is null)
            throw new SyncCliUsageException("The " + side + " endpoint requires provider: local, dropbox or " +
                "google-drive.");
        if (!TryKind(provider, out var kind))
            throw new SyncCliUsageException("The " + side + " endpoint provider '" + provider + "' is not " +
                "supported. Use local, dropbox or google-drive.");
        var path = Member(element, "path", side);
        var account = Member(element, "account", side);
        if (kind == SyncEndpointKind.Local)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new SyncCliUsageException("The " + side + " local endpoint requires its absolute folder " +
                    "path, for example C:\\\\Folder.");
            if (!Path.IsPathFullyQualified(path))
                throw new SyncCliUsageException("The " + side + " local endpoint path must be absolute; a relative " +
                    "path is never resolved against the current directory.");
            if (element.TryGetProperty("account", out _))
                throw new SyncCliUsageException("The " + side + " endpoint is a folder on this computer, so it " +
                    "cannot name an account; remove the account member.");
            return new SyncCliEndpointRequest(kind, path, null);
        }
        if (account is null)
            throw new SyncCliUsageException("The " + side + " " + SyncEndpointProviders.Get(kind).DisplayName +
                " endpoint requires account: the exact retained connection id of its account.");
        if (string.IsNullOrWhiteSpace(path))
            throw new SyncCliUsageException("The " + side + " " + SyncEndpointProviders.Get(kind).DisplayName +
                " endpoint requires the provider folder path to sync inside that account.");
        return new SyncCliEndpointRequest(kind, path, account);
    }

    /// <summary>The member as one exact non-blank string, or null when it is absent or blank.</summary>
    private static string? Member(JsonElement element, string name, string side)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new SyncCliUsageException("The " + side + " endpoint member '" + name + "' must be a string.");
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// The kind one provider literal names, through the shared provider registry. The local kind carries
    /// no provider id in that registry, so it is recognised by its registered literal; every cloud literal
    /// is mapped by the registry itself, and a kind the registry does not register can never be returned.
    /// </summary>
    private static bool TryKind(string provider, out SyncEndpointKind kind)
    {
        if (string.Equals(provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
        {
            kind = SyncEndpointKind.Local;
            return true;
        }
        return SyncEndpoints.TryMapProvider(provider, out kind) &&
            SyncEndpointProviders.Get(kind).ProviderId is not null;
    }
}
