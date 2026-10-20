namespace Massdex.SyncShark;

public sealed record SyncCliAccountDetailData(SyncCliAccountData Account, string RootNamespaceId,
    string HomeNamespaceId, int ScopeCount, bool HasFileRequestAccess, DateTimeOffset? ConnectedUtc,
    DateTimeOffset? LastVerifiedUtc, int PairCount, bool Paused);

public sealed record SyncCliAccountCheckData(string ConnectionId, bool HasProtectedCredential, bool LiveVerified,
    string Health, string? FailureType, int DependentPairCount, DateTimeOffset? LastSuccessfulRunUtc);

public sealed record SyncCliAccountActionData(string ConnectionId, string Action, string Result,
    bool LiveVerificationRequired);

public sealed record SyncCliQueueItemData(string OperationId, string PairId, string RelativePath,
    DateTimeOffset EnqueuedUtc, int Priority, string Operation, string State, bool RecoveryRequired,
    string ItemKind, string FileType);

public sealed record SyncCliQueueListData(IReadOnlyList<SyncCliQueueItemData> Items, int Limit, int Offset,
    int Returned, bool HasMore, int PendingCount);

public sealed record SyncCliMonitorData(string PairId, string Kind, string State, DateTimeOffset UpdatedUtc,
    int PendingDirtyItems);

public sealed record SyncCliAttentionData(IReadOnlyList<string> PairIds, IReadOnlyList<SyncCliMonitorData> Monitors,
    int PendingCount, IReadOnlyList<SyncCliAttentionItemData>? Items = null);

public sealed record SyncCliAttentionItemData(string PairId, string Title, string Description,
    bool IsProblem, string? RunId = null);

public sealed record SyncCliRunHistoryData(string Id, string Kind, DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc, string Status);

public sealed record SyncCliRunListData(int Count, IReadOnlyList<SyncCliRunHistoryData> Runs);
public sealed record SyncCliRunRetryPlanData(string RunId, string? PairId, string? UnavailableReason,
    IReadOnlyList<SyncRunRetryItem> Items, long? PolicyRevision, int TotalItems, int Limit, int Offset,
    int Returned, bool HasMore, int Failed, int NotAttempted, int NeedsVerification)
{
    public static SyncCliRunRetryPlanData Page(SyncRunRetryPlan plan, int limit, int offset)
    {
        var items = SyncCli.BoundedResultItems(plan.Items.Skip(offset), limit);
        if (items.Length == 0 && offset < plan.Items.Count)
            throw new SyncCliOperationException("This individual retry record exceeds the safe response size. Read or export its retained operation log; no sync was started.");
        return new(plan.RunId, plan.PairId, plan.UnavailableReason, items, plan.PolicyRevision,
            plan.Items.Count, limit, offset, items.Length, offset + items.Length < plan.Items.Count,
            plan.Items.Count(item => item.State == "Failed"), plan.Items.Count(item => item.State == "Not attempted"),
            plan.Items.Count(item => item.State == "Verify first"));
    }
}
public sealed record SyncCliJobResultData(string PairId, int DecisionCount, int Uploaded, int Downloaded,
    int Identical, int Recovered, int Conflicts, int Blocked, int Failed, int NotAttempted,
    int NeedsVerification, int Skipped, int OnlineOnly, bool Stopped);

public sealed record SyncCliJobData(SyncActiveRunSnapshot? Job, bool Active, bool RequestedJobFinished = false)
{
    public int ActiveRowsTotal { get; init; }
    public int ActiveRowsReturned { get; init; }
    public bool ActiveRowsHasMore { get; init; }
    public SyncCliJobResultData? ResultSummary { get; init; }

    // This is a CLI presentation copy, never the scheduler's active set or a
    // transfer limit. All aggregate counters and identities remain exact; the
    // retained log, not a status packet, owns complete per-file result details.
    internal static SyncCliJobData Project(SyncActiveRunSnapshot? snapshot, bool active,
        string command, bool requestedJobFinished = false)
    {
        var result = snapshot?.Progress?.Result;
        var summary = result is null ? null : new SyncCliJobResultData(result.PairId, result.Decisions.Count,
            result.Uploaded, result.Downloaded, result.Identical, result.Recovered, result.Conflicts,
            result.Blocked, result.Failed, result.NotAttempted, result.NeedsVerification,
            result.Skipped, result.OnlineOnly, result.Stopped);
        var projected = result is null ? snapshot : snapshot! with
        { Progress = snapshot.Progress! with { Result = null } };
        var action = projected?.Progress?.Action;
        var batch = action?.Batch;
        var rows = batch?.Active ?? Array.Empty<SyncActiveTransfer>();
        return SyncCli.FitResponseItems(rows, returned =>
        {
            var job = batch is null ? projected : projected! with
            {
                Progress = projected.Progress! with
                { Action = action! with { Batch = batch with { Active = returned } } }
            };
            return new SyncCliJobData(job, active, requestedJobFinished)
            {
                ActiveRowsTotal = rows.Count, ActiveRowsReturned = returned.Length,
                ActiveRowsHasMore = returned.Length < rows.Count, ResultSummary = summary
            };
        }, command, allowEmptySample: true);
    }
}

// GCLI-G3/AUTH1: the safe readonly projection of the host's one pending interactive authorization
// attempt. Count is 0 or 1. A zero count carries no attempt fields at all, and the projection never
// contains an authorization URL, OAuth state, code, verifier, token or any credential value.
public sealed record SyncCliPendingAuthorizationData(int Count, SyncPendingAuthorizationSnapshot? Pending,
    string Detail);

public sealed record SyncCliConflictData(string ConflictId, string PairId, string RelativePath, long Generation,
    long? LocalSize, long? DropboxSize, string? LocalHash, string? DropboxHash,
    DateTimeOffset? LocalModifiedUtc, DateTimeOffset? DropboxModifiedUtc, string Group, string Recommendation,
    string Status)
{
    public V2.FreeSpaceNoticeMetadata? FreeSpace { get; init; }
    public IReadOnlyList<SyncNoticeOption> Options { get; init; } = [];
    public string? RetainedRevision { get; init; }
}

public sealed record SyncCliConflictListData(IReadOnlyList<SyncCliConflictData> Items, int Limit, int Offset,
    int Returned, bool HasMore, int TotalOpen)
{
    public IReadOnlyList<V2.FreeSpaceNoticeSummary> FreeSpacePending { get; init; } = [];
}

public sealed record SyncCliConflictDetailData(SyncCliConflictData Conflict, bool HasLocalContent,
    bool HasDropboxContent, string? Note, string? LocalText = null, string? DropboxText = null);

/// <summary>One open needs-decision path: its notice and both sides' current state from the store (decision 64).</summary>
public sealed record SyncCliDecisionItemData(string PairId, string RelativePath, int Rule, string ReasonKey,
    string Detail, string SideA, string SideB, long? SizeA, long? SizeB, DateTimeOffset? ModifiedA,
    DateTimeOffset? ModifiedB, string Token, bool IsFolder = false);

public sealed record SyncCliDecisionListData(string PairId, IReadOnlyList<SyncCliDecisionItemData> Items, int Limit,
    int Offset, int Returned, bool HasMore, int TotalOpen);

/// <summary>The outcome of resolving one needs-decision path: effects done and whether the notice closed.</summary>
public sealed record SyncCliDecisionResolveData(string PairId, string RelativePath, string Side, int Applied,
    int Failed, int Held, bool NoticeClosed, string? Detail);

public sealed record SyncCliRecoveryItemData(string Key, string PairId, string PairName, string Source,
    string RelativePath, string Status, long Size, string? ContentHash, DateTimeOffset? RetainedUtc,
    bool CanRestore = false, bool CanRestoreCopy = false);

public sealed record SyncCliRecoveryListData(string PairId, IReadOnlyList<SyncCliRecoveryItemData> Items, int Limit,
    int Returned, bool HasMore, int ItemCount, long TotalSize, int CleanupEligibleCount, int? RetentionDays,
    int Offset = 0, int NextOffset = 0, IReadOnlyList<string>? Warnings = null);

public sealed record SyncCliConflictSelectionData(IReadOnlyList<SyncConflictSelection> Items, int Count);
public sealed record SyncCliConflictResolutionData(IReadOnlyList<SyncCliRunData> Items, int Count,
    IReadOnlyList<SyncAuthorityBatchItemResult>? Results = null);
public sealed record SyncCliStorageData(SyncCliPairData Pair, SyncFolderSelectionPage? Folders,
    long KnownSyncedBytes, long KnownSyncedFiles, DateTimeOffset? IndexedUtc);
public sealed record SyncCliStorageResultData(string PairId, long PolicyRevision, string Action,
    int FilesChanged, int DirectoriesChanged, IReadOnlyList<string> Failures);
public sealed record SyncCliStorageFilesData(string PairId, long PolicyRevision,
    IReadOnlyList<SyncFileStorageRule> Items, int Offset, int Returned, bool HasMore);

public sealed record SyncCliRecoveryActionData(string PairId, string Key, string Action, string Result);

public sealed record SyncCliRecoveryUsageData(string PairId, int ItemCount, long Size,
    DateTimeOffset? OldestRetainedUtc, int CleanupEligibleCount, int? RetentionDays);

public sealed record SyncCliScanData(string PairId, string PairName, int LocalEntries, int DropboxEntries,
    string LocalScanGenerationId, string DropboxScanGenerationId);

public sealed record SyncCliPreparationPhase(string Phase, double ElapsedSeconds);

/// <summary>One per-folder first-sync choice's resolved effect (first-sync-choices.md): what it removes or copies.</summary>
public sealed record SyncCliChoiceEffectData(string Prefix, string Side, string Action, int Files, int Folders,
    long Bytes);

/// <summary>
/// One rebuildable-folder recommendation on the first-sync report (docs/design/rebuildables.md): the category id and
/// explanation, how many folders, files and bytes it covers, whether the user chose to stop syncing it, and up to
/// twenty example folder paths.
/// </summary>
public sealed record SyncCliRebuildableData(string Id, string Explanation, int Folders, int Files, long Bytes,
    bool Chosen, IReadOnlyList<string> Examples, IReadOnlyList<SyncCliRebuildableFolderData>? Candidates = null);

/// <summary>Every candidate folder and whether it lies within a chosen category's scope.</summary>
public sealed record SyncCliRebuildableFolderData(string Path, bool Chosen);

/// <summary>One example of a held deletion's exact path, local pair side and conditional rule.</summary>
public sealed record SyncCliHeldDeletionData(string Path, string Local, string Rule);

/// <summary>Ignored descendants removed along with a planned (or conditionally held) folder delete.</summary>
public sealed record SyncCliCarriedIgnoredData(string Path, string Side, int Files, int Folders, long Bytes);

public sealed record SyncCliPreparationData(string PairId, string PairName, long PolicyRevision,
    string LocalScanGenerationId, string DropboxScanGenerationId, int TotalItems, int LocalOnlyItems,
    int DropboxOnlyItems, int ConflictItems, long RecommendedUploadBytes, long RecommendedDownloadBytes,
    double ElapsedSeconds, IReadOnlyList<SyncCliPreparationPhase> Phases, long DatabaseBytesBefore,
    long DatabaseBytesAfter, long WalBytesBefore, long WalBytesAfter, bool FileEffectsAuthorized,
    bool FirstSyncApproved, IReadOnlyList<SyncCliChoiceEffectData>? ChoiceEffects = null,
    string? ChoicesDigest = null, IReadOnlyList<SyncCliRebuildableData>? Rebuildables = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Per-folder ignored content carried by deletes, including zero ceilings for empty folders.</summary>
    public IReadOnlyList<SyncCliCarriedIgnoredData> CarriedIgnoredDeletes { get; init; } = [];

    /// <summary>Paths waiting for deletion evidence, whose conditional rules this prepare reviews.</summary>
    public int HeldDeletionCount { get; init; }

    /// <summary>Paths whose deletion evidence was already resolved while preparing.</summary>
    public int DeletionEvidenceResolved { get; init; }

    /// <summary>At most fifty held paths and their conditional rules; the retained approval keeps the full list.</summary>
    public IReadOnlyList<SyncCliHeldDeletionData> HeldDeletionExamples { get; init; } = [];

    /// <summary>Online-only cloud-files stubs the plan replaces by downloading the other side's file (rule 35).</summary>
    public int StubDownloads { get; init; }

    /// <summary>The bytes those stub downloads transfer (also part of RecommendedDownloadBytes).</summary>
    public long StubDownloadBytes { get; init; }

    /// <summary>Online-only stubs with no copy on the other side: reported, kept, never deleted.</summary>
    public int StubsWithoutCounterpart { get; init; }

    /// <summary>The links summary (decision 59): count and top folders of links excluded on both sides, or null.</summary>
    public string? LinksExcluded { get; init; }

    /// <summary>The online-only placeholders left alone while stub replacement is off (decision 57), or null.</summary>
    public string? StubsLeftAlone { get; init; }
}

public sealed record SyncCliFirstSyncResultData(string PairId, string PairName, int AppliedItems, int SkippedItems,
    bool FirstSyncComplete, int Uploaded, int Downloaded, int Unchanged, int Conflicts, int Blocked,
    IReadOnlyList<SyncDecision>? Files = null, string? RunId = null, int TotalFiles = 0,
    int Returned = 0, bool HasMore = false, IReadOnlyList<SyncCliChoiceEffectData>? ChoiceEffects = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Failed { get; init; }
    public int NotAttempted { get; init; }
    public int NeedsVerification { get; init; }
    public bool Stopped { get; init; }
    public string? Outcome { get; init; }

    /// <summary>Core v2 per-run throughput measurements; absent when collection is disabled.</summary>
    public V2.RunDiagnostics? Diagnostics { get; init; }

    /// <summary>Core v2: reviewed effects not completed, so the first sync stays open; 0 when complete.</summary>
    public int Deferred { get; init; }

    /// <summary>Core v2: exact held paths whose deletion evidence remains unresolved after apply.</summary>
    public int DeletionEvidencePendingCount { get; init; }

    /// <summary>Reviewed evidence paths still pending; rerunning the saved apply may resolve them.</summary>
    public int ReviewedDeletionEvidencePendingCount { get; init; }

    /// <summary>New held paths not in the frozen approval; require a follow-up review, never automatic authorization.</summary>
    public int UnreviewedPending { get; init; }

    /// <summary>At most fifty pending deletion-evidence paths, not an authorization for their descendants.</summary>
    public IReadOnlyList<string> DeletionEvidencePendingExamples { get; init; } = [];

    /// <summary>Distinct paths whose content is not confirmed, keeping the first sync incomplete.</summary>
    public int ContentUnconfirmedCount { get; init; }

    /// <summary>At most fifty paths whose content is not confirmed.</summary>
    public IReadOnlyList<string> ContentUnconfirmedExamples { get; init; } = [];

    /// <summary>Core v2: the catalog's "first sync not complete" line when <see cref="Deferred"/> is not 0.</summary>
    public string? Incomplete { get; init; }

    /// <summary>
    /// Core v2: rebuildable paths whose cloud copy could not be removed (the delete was blocked), so the first sync
    /// stays incomplete and each path is left to ordinary planning.
    /// </summary>
    public IReadOnlyList<string> BlockedRebuildables { get; init; } = [];
}

public sealed record SyncCliControlData(string Action, bool Accepted, string Result, int? PendingCount = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SyncRewindApplyConfirmation? RewindConfirmation { get; init; }
}

public sealed record SyncCliPauseData(string Scope, string Target, bool Paused, DateTimeOffset? UntilUtc,
    bool UntilResumed = false, bool Indefinitely = false, string? TimeZoneId = null,
    DateTimeOffset? LocalResumeUtc = null, string? Refused = null);

public sealed record SyncCliIgnoreRuleData(string Kind, string Value);

public sealed record SyncCliIgnoreListData(string PairId, IReadOnlyList<SyncCliIgnoreRuleData> Rules, int Count)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record SyncCliFolderData(string RelativePath, string State, bool Included, bool Mixed,
    int LocalItems, int OnlineOnlyItems, bool OnlineOnly, bool LocalPresent);

public sealed record SyncCliFolderTreeData(string PairId, IReadOnlyList<SyncCliFolderData> Folders, int Count,
    bool LocalHierarchyAvailable, string? LocalHierarchyMessage, bool DropboxHierarchyAvailable,
    string? DropboxHierarchyMessage,
    SyncCliHierarchySideData? LeftEndpoint = null, SyncCliHierarchySideData? RightEndpoint = null);

/// <summary>
/// W2-C: one ORDERED endpoint of a folder tree as the CLI renders it. The title and location are the
/// endpoint's own, so a reversed or two-cloud pair is never described as this computer or Dropbox, and
/// Available/Definitive report only what the read established.
/// </summary>
public sealed record SyncCliHierarchySideData(string Title, string Location, bool Available, bool Definitive,
    string? Message, int? RootFiles);

public sealed record SyncCliSelectionData(string PairId, string RelativePath, bool Included, int SelectionRuleCount)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record SyncCliAvailabilityData(string PairId, string RelativePath, bool OnlineOnly, string Result,
    IReadOnlyList<string>? Failures = null);

public sealed record SyncCliUpdateData(string CurrentVersion, bool UpdateAvailable, string? Version,
    long? ArchiveBytes, string? Detail, bool Downloaded, string? DownloadDirectory);

public sealed record SyncCliExplorerStatusData(bool InstalledProcess, bool IntegrationEnabled, int PairCount,
    string ProductRoot);

public sealed record SyncCliCapabilityRow(string Command, string Usage, string Kind, string Effect,
    string Requires, string Mode);

public sealed record SyncCliRemoteFolderData(string Name, string Path, string FolderId);

public sealed record SyncCliRemoteFolderListData(string AccountId, string? ParentPath,
    IReadOnlyList<SyncCliRemoteFolderData> Folders, int Count, bool HasMore);

public sealed record SyncCliSharedLinkData(string Url, string Name, string? Path, string ItemKind, string Audience,
    string Access, DateTimeOffset? Expires, bool CanRevoke, bool PasswordRequired, bool AllowDownload,
    IReadOnlyList<string> AllowedAudiences, string Provider = SyncProviders.Dropbox, string? PermissionId = null,
    string? RevisionFingerprint = null, bool CanEdit = true, IReadOnlyList<string>? AllowedRoles = null,
    bool DownloadSettingKnown = true);

public sealed record SyncCliSharingListData(string AccountId, string NamespaceId,
    IReadOnlyList<SyncCliSharedLinkData> Links, int Count, bool HasMore,
    bool Incomplete = false, IReadOnlyList<AccountSharingIssue>? Issues = null);

public sealed record SyncCliFileRequestData(string Id, string Url, string Title, string? Destination,
    string Description, DateTimeOffset Created, bool IsOpen, long FileCount, DateTimeOffset? Deadline);

public sealed record SyncCliFileRequestMappingData(string AccountId, string NamespaceId,
    IReadOnlyList<SyncCliFileRequestData> Requests, int Count, bool HasMore,
    bool Incomplete = false, IReadOnlyList<AccountSharingIssue>? Issues = null);

public sealed record SyncCliDiscoveryStatusData(bool Installed, string Status, string WorkerPath,
    bool ConfigureSupported, string Detail);

public sealed record SyncCliCapabilityData(string Product, string Version, string SourceRevision,
    IReadOnlyList<SyncCliCapabilityRow> Commands, IReadOnlyList<string> NotImplemented,
    IReadOnlyList<string> LiveVerificationRequired);
