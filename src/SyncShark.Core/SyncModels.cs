using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

public enum SyncDirection
{
    Upload,
    Download
}

public enum SyncIntentKind
{
    Transfer,
    ArchiveRemoteAfterLocalDeletion,
    ArchiveLocalAfterDropboxDeletion,
    ResolveKeepLocal,
    ResolveKeepDropbox
}

public enum SyncIntentPhase
{
    Prepared,
    SuccessorCreated,
    ArchiveCompleted,
    CommittedCleanupPending,
    CleanupCompleted
}

public enum SyncConflictResolution
{
    KeepLocalArchiveDropbox,
    KeepDropboxArchiveLocal
}

public enum SyncDecisionKind
{
    Baseline,
    Uploaded,
    Downloaded,
    Identical,
    Conflict,
    Recovered,
    Blocked,
    Stopped,
    Archived,
    OnlineOnly,
    ResponseUnknown,
    Reconciling,
    CleanupPending,
    Skipped
}

public enum SyncMode { TwoWay, UploadOnly, DownloadOnly }

public sealed record SyncPair(
    string Id,
    string Name,
    string LocalRoot,
    uint LocalVolumeSerialNumber,
    ulong LocalRootFileId,
    string DropboxRoot,
    string DropboxFolderId,
    bool Enabled = true,
    int Priority = 0,
    bool PropagateDeletions = false,
    long PolicyRevision = 1,
    SyncControlNamespaceBinding? ControlNamespace = null,
    SyncRemoteRecoveryRootBinding? RecoveryRoot = null,
    IReadOnlyList<SyncSelectionRule>? SelectionRules = null,
    bool SelectionDefaultIncluded = true,
    string ConnectionId = "",
    string Provider = "dropbox",
    bool? InitialReviewApproved = null,
    bool FilesOnDemandEnabled = false,
    int? RecoveryRetentionDays = 30,
    SyncMode Direction = SyncMode.TwoWay,
    IReadOnlyList<SyncDirectoryStorageRule>? DirectoryStorageRules = null,
    IReadOnlyList<string>? DirectoryStorageApplyPaths = null,
    SyncDirectoryStorage? DefaultStorage = null,
    IReadOnlyList<SyncIgnoreRule>? IgnoreRules = null,
    IReadOnlyList<SyncFileStorageRule>? FileStorageRules = null,
    [property: JsonPropertyName(SyncEndpointSchema.VersionMember), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? EndpointSchemaVersion = null,
    [property: JsonPropertyName(SyncEndpointSchema.LeftMember), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncEndpointRecord? LeftEndpoint = null,
    [property: JsonPropertyName(SyncEndpointSchema.RightMember), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncEndpointRecord? RightEndpoint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SyncControlNamespaceBinding? RightControlNamespace = null)
{
    [JsonIgnore]
    public SyncEndpointDocument? EndpointDocument => EndpointSchemaVersion is null &&
        LeftEndpoint is null && RightEndpoint is null ? null :
        new(EndpointSchemaVersion ?? 0, LeftEndpoint, RightEndpoint);

    public PilotSettings ToPilotSettings() => new(LocalRoot, LocalVolumeSerialNumber, LocalRootFileId, DropboxRoot, DropboxFolderId);

    public static string DeriveId(PilotSettings settings, string? providerRootNamespaceId = null)
    {
        var identity = string.Join("\n", settings.LocalVolumeSerialNumber, settings.LocalRootFileId,
            settings.DropboxFolderId, providerRootNamespaceId ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..20];
    }

    public static SyncPair FromPilot(PilotSettings settings, string? name = null,
        string? providerRootNamespaceId = null)
    {
        var id = DeriveId(settings, providerRootNamespaceId);
        return new SyncPair(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(settings.LocalRoot) : name.Trim(),
            settings.LocalRoot, settings.LocalVolumeSerialNumber, settings.LocalRootFileId,
            settings.DropboxRoot, settings.DropboxFolderId);
    }
}


public sealed record SyncCloudFileState(
    string RelativePath,
    SyncCloudFileAvailability Availability,
    bool IsPinned,
    bool IsInSync,
    long LogicalSize,
    long OnDiskBytes,
    string? DropboxId,
    string? DropboxRevision,
    string? ContentHash);

public sealed record SyncCloudAvailabilityResult(
    string RelativePath,
    bool KeepOnDevice,
    int FilesChanged,
    int DirectoriesChanged,
    IReadOnlyList<string> Failures);

public sealed record SyncSelectionRule(string FolderPath, bool Included, string? StableFolderIdentity = null);

public static class SyncSelectionPolicy
{
    public static SyncSelectionIndex Compile(SyncPair pair) => new(Normalize(pair.SelectionRules),
        pair.SelectionDefaultIncluded, SyncIgnorePolicy.Normalize(pair.IgnoreRules));

    public static bool IsIncluded(string relativePath, SyncPair pair, bool isDirectory = false) =>
        Compile(pair).IsIncluded(relativePath, isDirectory);

    public static SyncSelectionIndex Compile(IReadOnlyList<SyncSelectionRule>? rules,
        bool defaultIncluded = true) =>
        new(Normalize(rules), defaultIncluded);

    public static IReadOnlyList<SyncSelectionRule> Normalize(
        IReadOnlyList<SyncSelectionRule>? rules)
    {
        if (rules is null || rules.Count == 0) return [];
        var normalized = new Dictionary<string, SyncSelectionRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var path = PilotPath.NormalizeRelative(rule.FolderPath);
            var stableIdentity = string.IsNullOrWhiteSpace(rule.StableFolderIdentity)
                ? null
                : rule.StableFolderIdentity.Trim();
            normalized[path] = new SyncSelectionRule(path, rule.Included, stableIdentity);
        }
        return normalized.Values
            .OrderBy(item => Depth(item.FolderPath))
            .ThenBy(item => item.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsIncluded(string relativePath, IReadOnlyList<SyncSelectionRule>? rules)
        => Compile(rules).IsIncluded(relativePath);

    public static bool IsIncluded(string relativePath, IReadOnlyList<SyncSelectionRule>? rules,
        bool defaultIncluded) => Compile(rules, defaultIncluded).IsIncluded(relativePath);

    public static IReadOnlyList<SyncSelectionRule> SetFolder(
        IReadOnlyList<SyncSelectionRule>? rules, string folderPath, bool included,
        string? stableFolderIdentity = null)
    {
        var folder = PilotPath.NormalizeRelative(folderPath);
        var retained = Normalize(rules)
            .Where(rule => !string.Equals(rule.FolderPath, folder, StringComparison.OrdinalIgnoreCase) &&
                           !rule.FolderPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            .Append(new SyncSelectionRule(folder, included,
                string.IsNullOrWhiteSpace(stableFolderIdentity) ? null : stableFolderIdentity.Trim()));
        return Normalize(retained.ToArray());
    }

    private static int Depth(string path) => path.Count(character => character == '/') + 1;
}

public sealed class SyncSelectionIndex
{
    private readonly IReadOnlyList<SyncSelectionRule> _rules;
    private readonly IReadOnlyList<string> _includedExceptions;
    private readonly bool _defaultIncluded;
    private readonly IReadOnlyList<SyncIgnoreRule> _ignoreRules;

    internal SyncSelectionIndex(IReadOnlyList<SyncSelectionRule> normalizedRules, bool defaultIncluded,
        IReadOnlyList<SyncIgnoreRule>? ignoreRules = null)
    {
        _ignoreRules = ignoreRules ?? [];
        _defaultIncluded = defaultIncluded;
        _rules = normalizedRules.OrderByDescending(rule => rule.FolderPath.Count(character => character == '/'))
            .ToArray();
        _includedExceptions = normalizedRules.Where(rule => rule.Included)
            .Select(rule => rule.FolderPath).ToArray();
    }

    public bool IsIncluded(string relativePath) => IsIncluded(relativePath, false);

    public bool IsIncluded(string relativePath, bool isDirectory)
    {
        // Selection reads inventory metadata, including present-but-untransferable names.
        // Rule creation and every effect still require Windows-safe paths.
        var path = PilotPath.NormalizeInventoryPath(relativePath.Replace('\\', '/'));
        if (SyncIgnorePolicy.IsIgnored(path, isDirectory, _ignoreRules)) return false;
        var match = _rules.FirstOrDefault(rule =>
            string.Equals(path, rule.FolderPath, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(rule.FolderPath + "/", StringComparison.OrdinalIgnoreCase));
        return match?.Included ?? _defaultIncluded;
    }

    public bool ShouldTraverseDirectory(string relativePath)
    {
        var path = PilotPath.NormalizeRelative(relativePath);
        if (SyncIgnorePolicy.IsIgnored(path, true, _ignoreRules)) return false;
        if (IsIncluded(path, true)) return true;
        return _includedExceptions.Any(exception =>
            exception.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
    }
}

public enum SyncFolderPresence
{
    LocalOnly,
    CloudOnly,
    Mixed,
    OnlineOnly,
    LocalUnloaded,
    Excluded,
    DropboxUnloaded
}

/// <summary>
/// W2-C: the verified, side-bound hierarchy facts of ONE ordered endpoint of a pair. Endpoint is the
/// pair's own captured context side, so a consumer never has to assume that the first ordered endpoint is
/// the physical local folder, and a cloud-only item never becomes on-device residency. Available and
/// Verified report only what the read actually established; a side that was not reached is never reported
/// healthy. This is an optional addition: an older producer that does not supply it leaves it null.
/// </summary>
public sealed record SyncEndpointHierarchySide(
    SyncEndpointContextSide Endpoint,
    bool Available,
    bool Verified,
    string? Message,
    int? RootFiles,
    bool Definitive = false);

public sealed record SyncFolderRollup(
    int LocalItems,
    int OnlineOnlyItems,
    int MixedItems,
    int ExcludedItems,
    int PendingItems,
    int ErrorItems);

public sealed record SyncFolderSelectionNode(
    string RelativePath,
    string Name,
    int Depth,
    bool? IsChecked,
    SyncFolderPresence Presence,
    int DescendantFolders,
    string? StableFolderIdentity = null,
    SyncFolderRollup? Rollup = null,
    string? LocalStableFolderIdentity = null,
    string? DropboxStableFolderIdentity = null);

public sealed record SyncPairDraftPreview(
    string LocalBrowseRoot,
    DropboxFolderEntry DropboxBrowseRoot,
    IReadOnlyList<SyncFolderSelectionNode> Folders,
    int LocalRootFiles,
    int DropboxRootFiles,
    bool LocalCatalogComplete);

public sealed record SyncFolderSelectionTree(
    string PairId,
    IReadOnlyList<SyncFolderSelectionNode> Folders,
    int CloudOnlyFolders,
    int LocalOnlyFolders,
    int MixedFolders,
    int OnlineOnlyFolders,
    SyncFolderRollup? Rollup = null,
    bool LocalHierarchyAvailable = true,
    string? LocalHierarchyMessage = null,
    bool DropboxHierarchyAvailable = true,
    string? DropboxHierarchyMessage = null,
    int? LocalRootFiles = null,
    int? DropboxRootFiles = null,
    SyncEndpointContext? EndpointContext = null,
    SyncEndpointHierarchySide? LeftEndpoint = null,
    SyncEndpointHierarchySide? RightEndpoint = null);

public sealed record SyncFolderSelectionPage(
    string PairId,
    IReadOnlyList<SyncFolderSelectionNode> Folders,
    int Offset,
    int TotalMatchingFolders,
    int TotalFolders,
    bool HasMore,
    int CloudOnlyFolders,
    int LocalOnlyFolders,
    int MixedFolders,
    int OnlineOnlyFolders,
    SyncFolderRollup Rollup);

public sealed record SyncPathIndexEntry(
    string RelativePath,
    PilotEntryKind Kind,
    bool LocalObserved,
    bool LocalPresent,
    bool DropboxPresent,
    bool OnlineOnly,
    string? LocalStableIdentity,
    string? DropboxStableIdentity,
    bool Included);

public sealed record SyncPairSet(IReadOnlyList<SyncPair> Pairs, string? SelectedPairId);

public sealed record SyncFileBaseline(
    string RelativePath,
    string? LocalContentHash,
    long? LocalSize,
    string? RemoteContentHash,
    long? RemoteSize,
    string? RemoteId,
    string? RemoteRevision,
    uint? LocalVolumeSerialNumber = null,
    ulong? LocalFileId = null,
    DateTime? LocalLastWriteUtc = null);

public sealed record SyncLocalControlNamespaceBinding(
    string RootPath,
    uint RootVolumeSerialNumber,
    ulong RootFileId,
    string MarkerFileId,
    string MarkerContentHash);

public sealed record SyncRemoteControlNamespaceBinding(
    string RootPath,
    string RootFolderId,
    string MarkerPath,
    string MarkerFileId,
    string MarkerRevision,
    string MarkerContentHash);

public sealed record SyncRemoteRecoveryRootBinding(
    string RootPath,
    string RootFolderId,
    string RootNamespaceId);

public sealed record SyncRemoteChangeEntry(
    string? RelativePath,
    string EventKind,
    string EvidenceJson);

public sealed record SyncRemoteChangePage(
    string Cursor,
    bool HasMore,
    IReadOnlyList<SyncRemoteChangeEntry> Entries);

public sealed record SyncControlNamespaceBinding(
    string InstallationId,
    string NamespaceId,
    SyncLocalControlNamespaceBinding Local,
    SyncRemoteControlNamespaceBinding? Remote = null);

public sealed record SyncTransferIntent(
    string Id,
    string PairId,
    string RelativePath,
    SyncDirection Direction,
    string SourceContentHash,
    string? ExpectedDestinationContentHash,
    string? ExpectedDestinationRevision,
    string? StageFileName,
    string? BackupFileName,
    DateTimeOffset StartedUtc,
    SyncIntentKind Kind = SyncIntentKind.Transfer,
    SyncIntentPhase Phase = SyncIntentPhase.Prepared,
    string? RecoveryPath = null,
    string? RecoveryOriginalPath = null,
    string? ExpectedSourceId = null,
    string? ExpectedSourceRevision = null,
    long? ExpectedSourceSize = null,
    string? RecoveryFileId = null,
    string? RecoveryRevision = null,
    string? RecoveryContentHash = null,
    long? RecoverySize = null,
    long CreationPolicyRevision = 1,
    string? StageArtifactId = null,
    string? BackupArtifactId = null,
    string? TransactionDirectoryName = null,
    string? TransactionDirectoryId = null,
    string? TransactionDirectorySecurityHash = null,
    bool ArtifactsBound = false,
    uint? ExpectedDestinationVolumeSerialNumber = null,
    ulong? ExpectedDestinationFileId = null,
    string? StagingRelativePath = null,
    uint? StagingRootVolumeSerialNumber = null,
    ulong? StagingRootFileId = null,
    uint? ExpectedSourceVolumeSerialNumber = null,
    ulong? ExpectedSourceFileId = null,
    DateTime? ExpectedSourceLastWriteUtc = null,
    string? DestinationRelativePath = null,
    string? SuccessorFileId = null,
    string? SuccessorRevision = null,
    string? SuccessorContentHash = null,
    long? SuccessorSize = null,
    string? ExpectedDestinationRemoteId = null,
    long? ExpectedDestinationSize = null);

public sealed record SyncPairRuntimeState(
    string PairId,
    bool BaselineEstablished,
    IReadOnlyList<SyncFileBaseline> Files,
    SyncTransferIntent? PendingTransfer,
    IReadOnlyList<SyncFileBaseline>? InitialObservation = null,
    int InitialObservationCount = 0);

public sealed record SyncRuntimeState(IReadOnlyList<SyncPairRuntimeState> Pairs);

public sealed record SyncRunLease(string PairId, string Generation, long PolicyRevision, DateTimeOffset StartedUtc);

public sealed record SyncProductState(SyncPairSet PairSet, SyncRuntimeState Runtime, SyncRunLease? ActiveRun = null,
    string? InstallationId = null, SyncTransferSettings? TransferSettings = null,
    IReadOnlyList<SyncRewindSession>? RewindSessions = null,
    SyncRewindStorageReference? RewindStorage = null);

public enum SyncRewindStatus
{
    AwaitingDropbox,
    PreviewReady,
    Applying,
    AppliedPaused,
    Resumed,
    Blocked,
    PartiallyApplied,
    ApplyStopped,
    EffectUnknown,
    ApplyBlocked
}

public enum SyncRewindPathStatus
{
    Pending,
    Planned,
    Completed,
    Conflict,
    Blocked,
    Stopped,
    EffectUnknown
}

public sealed record SyncRewindPreviewItem(
    string RelativePath,
    string Group,
    string ProposedEffect,
    bool DropboxChanged,
    bool LocalChanged,
    string Reason);

public sealed record SyncRewindPathProgress(
    string RelativePath,
    SyncRewindPathStatus Status,
    string? OperationId,
    string Detail,
    DateTimeOffset UpdatedUtc);

public sealed record SyncRewindSession(
    string SessionId,
    string ConnectionId,
    string PairId,
    string PairName,
    string DropboxRoot,
    string LocalCheckpointScanId,
    string DropboxCheckpointScanId,
    string DropboxCheckpointCursor,
    DateTimeOffset CheckpointUtc,
    SyncRewindStatus Status,
    string? LocalPreviewScanId = null,
    string? DropboxPreviewScanId = null,
    IReadOnlyList<SyncRewindPreviewItem>? Preview = null,
    string? Detail = null,
    string? OperationGroupId = null,
    IReadOnlyList<SyncRewindPathProgress>? PathProgress = null);

// Bind destructive consent to the displayed session and immutable preview scans.
public sealed record SyncRewindApplyConfirmation(string SessionId, string LocalPreviewScanId,
    string DropboxPreviewScanId)
{
    public static SyncRewindApplyConfirmation FromSession(SyncRewindSession session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) ||
            string.IsNullOrWhiteSpace(session.LocalPreviewScanId) ||
            string.IsNullOrWhiteSpace(session.DropboxPreviewScanId) || session.Preview is null)
            throw new InvalidOperationException("Detect and review the Rewind preview before confirming it.");
        return new(session.SessionId, session.LocalPreviewScanId, session.DropboxPreviewScanId);
    }

    internal bool Matches(SyncRewindSession session) =>
        !string.IsNullOrWhiteSpace(SessionId) && !string.IsNullOrWhiteSpace(LocalPreviewScanId) &&
        !string.IsNullOrWhiteSpace(DropboxPreviewScanId) &&
        string.Equals(SessionId, session.SessionId, StringComparison.Ordinal) &&
        string.Equals(LocalPreviewScanId, session.LocalPreviewScanId, StringComparison.Ordinal) &&
        string.Equals(DropboxPreviewScanId, session.DropboxPreviewScanId, StringComparison.Ordinal);
}

public sealed record SyncAuthorityReviewedPathCheckpoint(
    string RelativePath,
    SyncRewindPathStatus Status,
    string? OperationId,
    SyncDecision Decision);

public sealed record SyncDecision(string RelativePath, SyncDecisionKind Kind, string Reason)
{
    // A dependency/stop deferral is not a failed transfer. Older records retain
    // their recorded outcome rather than acquiring invented attempt evidence.
    public bool NotAttempted { get; init; }
}

/// <summary>
/// Immutable snapshot of the relative paths that actually completed a transfer in each
/// direction during one coordinated run. It is kept separate from the final most-material
/// per-path decision so a path uploaded and then downloaded counts once in each direction
/// instead of only reporting its last decision.
/// </summary>
public sealed record SyncRunTransferTotals
{
    /// <summary>
    /// The constructor copies both caller collections into frozen case-insensitive sets, so a
    /// published snapshot cannot change afterwards even if the caller keeps mutating the sets it
    /// passed in. Each set is owned by this instance and is never handed back for mutation.
    /// </summary>
    public SyncRunTransferTotals(IEnumerable<string> uploadedPaths, IEnumerable<string> downloadedPaths)
    {
        UploadedPaths = Freeze(uploadedPaths);
        DownloadedPaths = Freeze(downloadedPaths);
    }

    public IReadOnlySet<string> UploadedPaths { get; }
    public IReadOnlySet<string> DownloadedPaths { get; }

    public int Uploaded => UploadedPaths.Count;
    public int Downloaded => DownloadedPaths.Count;

    private static IReadOnlySet<string> Freeze(IEnumerable<string> paths) =>
        paths.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Authoritative aggregate result counts, independent of bounded or absent display rows.</summary>
public sealed record SyncRunCounts(int Applied, int Uploaded, int Downloaded, int Deleted, int Moved,
    int KeptBoth, int Held, int Failed, int Uncertain, int Skipped, int Deferred);

public sealed record SyncRunResult(string PairId, IReadOnlyList<SyncDecision> Decisions)
{
    /// <summary>The v2 run owner's final disposition; null only for legacy results.</summary>
    public string? Outcome { get; init; }
    public int Pending { get; init; }
    public IReadOnlyDictionary<string, long> IndexingRemainingByRoot { get; init; } = new Dictionary<string, long>();
    public int Deferred { get; init; }
    /// <summary>
    /// Optional completed-transfer totals for this run. It is a non-positional init property so
    /// existing construction and deconstruction callers keep working unchanged; when it is
    /// absent the counters fall back to the retained per-path decisions exactly as before.
    /// </summary>
    public SyncRunTransferTotals? TransferTotals { get; init; }

    /// <summary>Present for v2 reports; display observations never replace these counted facts.</summary>
    public SyncRunCounts? Counts { get; init; }

    public int Uploaded => Counts?.Uploaded ?? TransferTotals?.Uploaded ?? Decisions.Count(item => item.Kind == SyncDecisionKind.Uploaded);
    public int Downloaded => Counts?.Downloaded ?? TransferTotals?.Downloaded ?? Decisions.Count(item => item.Kind == SyncDecisionKind.Downloaded);
    // Applied actions include successful metadata-only effects (notably moves),
    // not just transferred bytes. Keep this shared first-sync/retry projection
    // out of serialized run objects; their existing receipt field owns the count.
    [System.Text.Json.Serialization.JsonIgnore]
    public int AppliedItems => Counts?.Applied ?? (Uploaded + Downloaded + Decisions.Count(item => !item.NotAttempted &&
        item.Kind is SyncDecisionKind.Recovered or SyncDecisionKind.Archived or SyncDecisionKind.OnlineOnly));
    public int Identical => Counts is not null ? 0 : Decisions.Count(item => item.Kind == SyncDecisionKind.Identical);
    public int Conflicts => Counts?.Held ?? Decisions.Count(item => item.Kind == SyncDecisionKind.Conflict);
    public int Recovered => Counts is not null ? 0 : Decisions.Count(item => item.Kind == SyncDecisionKind.Recovered);
    public int Blocked => Counts?.Failed ?? Decisions.Count(item => item.Kind == SyncDecisionKind.Blocked);
    public int Failed => Counts?.Failed ?? Decisions.Count(item => item.Kind == SyncDecisionKind.Blocked && !item.NotAttempted);
    public int NotAttempted => Counts?.Deferred ?? Decisions.Count(item => item.NotAttempted);
    public int NeedsVerification => Counts?.Uncertain ?? Decisions.Count(item => item.Kind is SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending);
    public int Skipped => Counts?.Skipped ?? Decisions.Count(item => item.Kind == SyncDecisionKind.Skipped);
    public int OnlineOnly => Counts is not null ? 0 : Decisions.Count(item => item.Kind == SyncDecisionKind.OnlineOnly);
    public bool Stopped => Outcome is not null ? Outcome == V2.RunOutcomes.Stopped : Decisions.Any(item => item.Kind == SyncDecisionKind.Stopped);
}

public sealed record SyncArchiveResult(string FileId, string Revision, string RecoveryPath, long Size, string ContentHash);

public sealed record SyncLocalArchiveResult(string RecoveryPath, uint VolumeSerialNumber, ulong FileId, long Size,
    string ContentHash);

public sealed record SyncHistoryEntry(
    DateTimeOffset OccurredUtc,
    string PairId,
    string PairName,
    string RelativePath,
    SyncDecisionKind Outcome,
    string Reason);

public sealed record SyncHistory(IReadOnlyList<SyncHistoryEntry> Entries);

public sealed record SyncAutomationSettings(
    bool Enabled,
    int IntervalMinutes,
    bool RunOnStartup,
    bool StartWithWindows = false);

public sealed record SyncTransferSettings(
    int MaximumConcurrentPairs = 2,
    int UploadKiBPerSecond = 0,
    int DownloadKiBPerSecond = 0,
    string ResourceMode = "Auto",
    int MaximumConcurrentFiles = 0,
    long RecoveryMaxBytes = 20L * 1024 * 1024 * 1024,
    string? LocalRecoveryRoot = null,
    int RecoveryRetentionDays = 30,
    long TransferReservationFloorBytes = 64L * 1024,
    // The engine batch tunables, adjustable the same way as MaximumConcurrentFiles: 0 means "automatic" and keeps
    // the engine default (V2Settings.Default), so each default has exactly one home. When set, uploadBatchSize and
    // deleteBatchSize are 1..1000 (the provider finish_batch_v2/delete_batch maximum) and uploadCommitConcurrency is 1..4.
    int UploadBatchSize = 0,
    int UploadCommitConcurrency = 0,
    int DeleteBatchSize = 0,
    // Decision 76: how long an opened online-only file stays on this PC before it is returned to the
    // cloud as a pointer. Stored as text so "Never" needs no magic number.
    string OnlineOnlyReturnInterval = "1 day",
    // Process-wide v2 Dropbox payload window; 0 selects V2Settings' default, never a file-size limit.
    long UploadPayloadBudgetBytes = 0)
{
    public const int MaximumRecoveryRetentionDays = 3650;
    public const string DefaultOnlineOnlyReturnInterval = "1 day";
    public const int MaximumBatchEntries = 1000;
    public const int MaximumUploadCommitConcurrency = 4;

    // LocalRecoveryRoot is machine-specific, so it has no default (decision 55); only its form is checked here,
    // its availability is checked when a run needs it.
    public static SyncTransferSettings Normalize(SyncTransferSettings? value)
    {
        value ??= new SyncTransferSettings();
        if (value.RecoveryRetentionDays is < 1 or > MaximumRecoveryRetentionDays)
            throw new InvalidDataException(
                $"Recovery retention must be between 1 and {MaximumRecoveryRetentionDays} days.");
        if (string.IsNullOrWhiteSpace(value.LocalRecoveryRoot))
            value = value with { LocalRecoveryRoot = null };
        else
        {
            var root = value.LocalRecoveryRoot.Trim();
            if (!Path.IsPathFullyQualified(root) || root.Length < 3 || !char.IsAsciiLetter(root[0]) ||
                root[1] != ':' || root[2] is not ('\\' or '/'))
                throw new InvalidDataException("The local Recovery location must be an absolute folder on a local drive.");
            value = value with { LocalRecoveryRoot = Path.GetFullPath(root) };
        }
        if (value.MaximumConcurrentPairs is < 1 or > 8)
            throw new InvalidDataException("Concurrent pair transfers must be between 1 and 8.");
        if (value.UploadKiBPerSecond is < 0 or > 10_000_000 ||
            value.DownloadKiBPerSecond is < 0 or > 10_000_000)
            throw new InvalidDataException("Transfer bandwidth limits are outside the supported range.");
        if (value.ResourceMode is not ("Auto" or "Performance" or "Low impact"))
            throw new InvalidDataException("Resource mode must be Auto, Performance or Low impact.");
        if (value.MaximumConcurrentFiles is < 0 or > 1024)
            throw new InvalidDataException("Concurrent files must be 0 (automatic) or between 1 and 1024.");
        if (value.UploadBatchSize is < 0 or > MaximumBatchEntries)
            throw new InvalidDataException(
                $"The upload batch size must be 0 (automatic) or between 1 and {MaximumBatchEntries}.");
        if (value.DeleteBatchSize is < 0 or > MaximumBatchEntries)
            throw new InvalidDataException(
                $"The delete batch size must be 0 (automatic) or between 1 and {MaximumBatchEntries}.");
        if (value.UploadCommitConcurrency is < 0 or > MaximumUploadCommitConcurrency)
            throw new InvalidDataException(
                $"The upload commit concurrency must be 0 (automatic) or between 1 and {MaximumUploadCommitConcurrency}.");
        if (value.TransferReservationFloorBytes is < 1 or > 16L * 1024 * 1024)
            throw new InvalidDataException("Transfer reservation floor must be positive and at most 16 MiB.");
        if (value.UploadPayloadBudgetBytes < 0)
            throw new InvalidDataException("Upload payload budget must be non-negative; zero uses the engine default.");
        if (value.RecoveryMaxBytes < 0)
            throw new InvalidDataException("Recovery maximum bytes must be non-negative.");
        if (value.OnlineOnlyReturnInterval is not ("1 hour" or "1 day" or "1 week" or "Never"))
            throw new InvalidDataException("The online-only return interval must be 1 hour, 1 day, 1 week or Never.");
        return value;
    }
}

public static class SyncUploadLimits
{
    public const long DropboxSingleRequestBytes = 150L * 1024 * 1024;

    // The legacy default remains byte-compatible. New endpoint adapters publish their
    // actual create/replacement mechanisms; neither logical side nor source provider
    // decides how the destination receives content.
    public static SyncUploadSessionPolicy ForStore(ISyncRemoteStore store) =>
        store is ISyncUploadSessionPolicySource source
            ? source.UploadSessionPolicy.Validate()
            : new(DropboxSingleRequestBytes, DropboxSingleRequestBytes);
}

public sealed record SyncUploadSessionPolicy(long MaximumSimpleCreateBytes, long MaximumSimpleReplacementBytes)
{
    public SyncUploadSessionPolicy Validate()
    {
        if (MaximumSimpleCreateBytes <= 0 || MaximumSimpleReplacementBytes <= 0)
            throw new InvalidDataException("The destination's upload-session policy requires positive limits.");
        return this;
    }

    public bool RequiresSession(long? size, bool replacement) =>
        size > (replacement ? MaximumSimpleReplacementBytes : MaximumSimpleCreateBytes);
}

public interface ISyncUploadSessionPolicySource
{
    SyncUploadSessionPolicy UploadSessionPolicy { get; }
}

public sealed record SyncUploadSessionCheckpoint(string SessionId, long ConfirmedOffset);

public sealed record SyncRangeDownloadResult(
    string FileId,
    string Revision,
    long TotalSize,
    string ContentHash,
    long Offset,
    long Length);

public interface ISyncRemoteRangeStore
{
    Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset, long length,
        Stream destination, CancellationToken cancellationToken = default);
}

public interface ISyncResumableRemoteStore
{
    Task<PilotUploadResult> UploadResumableAsync(string relativePath, Stream source,
        string expectedContentHash, string? expectedRemoteRevision,
        SyncUploadSessionCheckpoint? checkpoint,
        Func<SyncUploadSessionCheckpoint, CancellationToken, Task> persistCheckpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The shared exact-item fallbacks for <see cref="ISyncRemoteStore"/>. Every legacy adapter
/// reaches the listing fallback through here, so an adapter that overrides one lookup for a
/// native metadata endpoint, and an opted-in fixture that models one, still has the honest
/// fallback available and a single place defines what one unambiguous observation is.
/// </summary>
public static class SyncRemoteExactLookup
{
    public static async Task<PilotRemoteEntry> RequireEntryAsync(ISyncRemoteStore store,
        string relativePath, string providerObjectId, CancellationToken cancellationToken = default)
    {
        var path = PilotPath.NormalizeRelative(relativePath);
        var entries = await store.ListEntriesAsync(cancellationToken) ??
            throw new IOException("The endpoint returned no inventory; an unavailable observation is not exact-item or absence evidence.");
        var matches = entries.Where(item =>
            string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !string.Equals(matches[0].Id, providerObjectId, StringComparison.Ordinal))
            throw new IOException("The selected endpoint did not return one exact item at " + path + ".");
        return matches[0];
    }

    // Absence is proven only by one fresh observation that unambiguously finds nothing at
    // the exact path. Two names that collapse onto the same observed path are refused
    // rather than reported as either present or absent.
    public static async Task<PilotRemoteEntry?> FindEntryAsync(ISyncRemoteStore store,
        string relativePath, CancellationToken cancellationToken = default)
    {
        var path = PilotPath.NormalizeRelative(relativePath);
        var entries = await store.ListEntriesAsync(cancellationToken) ??
            throw new IOException("The endpoint returned no inventory; an unavailable observation is not exact-item or absence evidence.");
        var matches = entries.Where(item =>
            string.Equals(PilotPath.CanonicalComparisonKey(item.RelativePath),
                PilotPath.CanonicalComparisonKey(path), StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1)
            throw new IOException("The selected endpoint returned more than one item at " + path +
                "; the exact path is ambiguous and absence is not proven.");
        return matches[0];
    }
}

public interface ISyncRemoteStore : IPilotRemoteStore
{
    // Batch lookup for paths present only locally. Missing remote inventory alone is never
    // deletion evidence. Providers must return their actual deletion timestamp, not a file's
    // modification time. This fallback deliberately does not inspect local recycle history.
    Task<IReadOnlyDictionary<string, SyncRemoteDeletionEvidence>> LookupDeletionEvidenceAsync(
        IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, SyncRemoteDeletionEvidence> result = relativePaths
            .Select(PilotPath.NormalizeRelative)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, _ => new SyncRemoteDeletionEvidence(null,
                "This endpoint does not support remote deletion-time evidence; remote absence alone does not prove deletion."),
                StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }

    // A fresh exact-path lookup, never a cached inventory. Adapters with a native
    // metadata endpoint override this; legacy adapters retain the safe listing
    // fallback and reject ambiguous names instead of choosing an arbitrary item.
    Task<PilotRemoteEntry> ReadEntryAsync(string relativePath, string providerObjectId,
        CancellationToken cancellationToken = default) =>
        SyncRemoteExactLookup.RequireEntryAsync(this, relativePath, providerObjectId, cancellationToken);

    // One fresh nullable exact-path observation under the exact bound root, never a cached
    // inventory and never by itself a creation authority. A native metadata adapter returns
    // null only for its own explicit absent-path classification; authentication, permission,
    // an unavailable or malformed response, transport loss and an ambiguous name all stay
    // errors and are never reported as absence. Legacy adapters keep the listing fallback.
    Task<PilotRemoteEntry?> FindEntryAtPathAsync(string relativePath,
        CancellationToken cancellationToken = default) =>
        SyncRemoteExactLookup.FindEntryAsync(this, relativePath, cancellationToken);

    // One observation contract for every endpoint. Progress never supplies sync authority.
    async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
        Func<SyncInventoryProgress, Task> onProgress, CancellationToken cancellationToken = default)
    {
        await onProgress(new("Reading folder inventory", "", 0,
            "Waiting for the selected endpoint to return its folder inventory."));
        return await ListEntriesAsync(cancellationToken);
    }

    // True only when the effect itself removes the exact directory ONLY IF empty.
    // A metadata read or descendant pre-list is not an atomic emptiness guard.
    bool SupportsConditionalDirectoryDelete => false;

    Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installationId, string namespaceId,
        CancellationToken cancellationToken = default);

    Task<PilotUploadResult> UploadAsync(string relativePath, Stream source, string expectedContentHash,
        string? expectedRemoteRevision, CancellationToken cancellationToken = default);

    Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
        CancellationToken cancellationToken = default);

    Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
        CancellationToken cancellationToken = default);

    Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(PilotRemoteEntry expected,
        CancellationToken cancellationToken = default);

    Task<PilotRemoteEntry> CreateDirectoryAsync(string relativePath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter cannot create directories.");

    Task<PilotRemoteEntry> MoveAsync(PilotRemoteEntry expected, string destinationRelativePath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter cannot perform identity-preserving moves.");

    Task VerifyRecoveryRootAsync(SyncRemoteRecoveryRootBinding binding,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter has no bound recovery-root implementation.");

    Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter has no bound recovery-root implementation.");

    Task<SyncArchiveResult> CopyRevisionToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter cannot retain a historical revision.");

    Task<SyncArchiveResult?> GetRecoveryAsync(SyncRemoteRecoveryRootBinding binding, string deterministicName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter has no bound recovery-root implementation.");

    Task<SyncDownloadResult> DownloadRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        SyncArchiveResult expected, Stream destination, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter cannot read an exact retained recovery copy.");

    Task<PilotUploadResult> CopyCreateOnlyAsync(PilotRemoteEntry expected, string destinationRelativePath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter has no visible create-only copy implementation.");

    Task DeleteRecoveryAsync(SyncRemoteRecoveryRootBinding binding, SyncArchiveResult expected,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter cannot remove a retained recovery copy.");

    Task<PilotRemoteEntry> CopyRecoveryToPathAsync(SyncRemoteRecoveryRootBinding binding,
        SyncArchiveResult expected, string destinationRelativePath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This remote adapter cannot restore a retained recovery copy.");
}

public interface ISyncRemoteChangeFeed
{
    Task<string> GetLatestCursorAsync(CancellationToken cancellationToken = default);

    Task<SyncRemoteChangePage> ContinueChangesAsync(string cursor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional provider capability for deciding whether one exact provider object identity still exists
/// inside the authorized namespace. Existence is measured by identity, never by a relative path: an object
/// that moved elsewhere inside the authorized namespace still exists, and a path disagreement is not
/// absence. Only the adapter's own explicit classified absent-path response may answer false;
/// authentication, permission, rate, transport and malformed responses stay errors. Adapters without this
/// capability keep their existing conservative inventory fallback at the consumer.
/// </summary>
public interface ISyncRemoteObjectIdentityLookup
{
    Task<bool> ObjectExistsAsync(string providerObjectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional provider capability for moving one stable object between two separately bound
/// sync roots without transferring its bytes. Implementations must reject cross-account or
/// cross-namespace destinations and must preserve the provider object identity.
/// </summary>
public interface ISyncCrossRootRemoteMove
{
    Task<PilotRemoteEntry> MoveToAsync(PilotRemoteEntry expected,
        ISyncRemoteStore destinationStore, string destinationRelativePath,
        CancellationToken cancellationToken = default);
}
