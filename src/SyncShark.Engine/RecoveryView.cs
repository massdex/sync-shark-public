namespace Massdex.SyncShark.Engine;

/// <summary>
/// Read-only evidence for one recovery operation. Allowed choices are supplied by the recovery authority,
/// not authorization to act; mutation still requires fresh exact-operation and generation checks.
/// Recorded stage proof is evidence for Local to verify, never a claim about the current occupant.
/// </summary>
public sealed record RecoveryView
{
    public string OperationId { get; }
    public long Generation { get; }
    public string Path { get; }
    public bool IsLegacy { get; }
    public PendingKind? Kind { get; }
    public PendingPhase? Phase { get; }
    public RemovalProof? Losing { get; }
    public SyncedEntry? Saved { get; }
    public Entry? Source { get; }
    public string? StagePath { get; }
    public StageProof? Stage { get; }
    public string? BinLocator { get; }
    public StageProof? Victim { get; }
    public bool? PermanentAllowed { get; }
    public TextKey? Cause { get; }
    public IReadOnlyList<RecoveryChoice> AllowedChoices { get; }
    public FolderRemovalPhase? FolderPhase { get; }
    public FolderRemovalStatus? FolderStatus { get; }
    public FolderRetirementDisposition? RetirementDisposition { get; }

    /// <summary>Reads the existing wire projection; this value does not grant mutation or stage ownership.</summary>
    [System.Text.Json.Serialization.JsonConstructor]
    public RecoveryView(string operationId, long generation, string path, bool isLegacy, PendingKind? kind,
        PendingPhase? phase, RemovalProof? losing, SyncedEntry? saved, Entry? source, string? stagePath,
        StageProof? stage, string? binLocator, StageProof? victim, bool? permanentAllowed, TextKey? cause,
        IReadOnlyList<RecoveryChoice> allowedChoices, FolderRemovalPhase? folderPhase,
        FolderRemovalStatus? folderStatus, FolderRetirementDisposition? retirementDisposition)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(allowedChoices);
        OperationId = operationId;
        Generation = generation;
        Path = path;
        IsLegacy = isLegacy;
        Kind = kind;
        Phase = phase;
        Losing = losing;
        Saved = saved;
        Source = source;
        StagePath = stagePath;
        Stage = stage;
        BinLocator = binLocator;
        Victim = victim;
        PermanentAllowed = permanentAllowed;
        Cause = cause;
        AllowedChoices = Array.AsReadOnly(allowedChoices.ToArray());
        FolderPhase = folderPhase;
        FolderStatus = folderStatus;
        RetirementDisposition = retirementDisposition;
    }

    /// <summary>Projects the recorded current note without inspecting or acquiring its evidence paths.</summary>
    public RecoveryView(PendingEffect pending, IReadOnlyList<RecoveryChoice> allowedChoices)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(allowedChoices);
        OperationId = pending.OperationId;
        Generation = pending.Generation;
        Path = pending.Path;
        Kind = pending.Kind;
        Phase = pending.Phase;
        Losing = pending.Losing;
        Saved = pending.Saved;
        Source = pending.Source;
        Stage = pending.Stage;
        StagePath = pending.Stage?.Path;
        BinLocator = pending.BinLocator;
        Victim = pending.Victim;
        PermanentAllowed = pending.PermanentAllowed;
        Cause = pending.Cause;
        AllowedChoices = Array.AsReadOnly(allowedChoices.ToArray());
    }

    /// <summary>Projects only legacy locators, with the exact raw token and generation zero; no phase or proof exists.</summary>
    public RecoveryView(LegacyPending pending, IReadOnlyList<RecoveryChoice> allowedChoices)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(allowedChoices);
        OperationId = pending.RawToken;
        Generation = 0;
        Path = pending.Path;
        IsLegacy = true;
        Source = pending.Source;
        StagePath = pending.StagePath;
        AllowedChoices = Array.AsReadOnly(allowedChoices.ToArray());
    }

    /// <summary>
    /// Projects a folder-operation summary supplied by its authority. Members and their custody are paged separately;
    /// retirement disposition does not turn an unknown or retained result into successful removal.
    /// </summary>
    public RecoveryView(string operationId, long generation, string rootPath, FolderRemovalPhase phase,
        FolderRemovalStatus status, TextKey? cause, FolderRetirementDisposition? retirementDisposition,
        IReadOnlyList<RecoveryChoice> allowedChoices)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(allowedChoices);
        OperationId = operationId;
        Generation = generation;
        Path = rootPath;
        FolderPhase = phase;
        FolderStatus = status;
        Cause = cause;
        RetirementDisposition = retirementDisposition;
        AllowedChoices = Array.AsReadOnly(allowedChoices.ToArray());
    }
}
