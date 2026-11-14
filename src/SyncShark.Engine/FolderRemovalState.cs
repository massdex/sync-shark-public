namespace Massdex.SyncShark.Engine;

/// <summary>
/// Frozen root evidence and aggregate progress of one durable folder removal.
/// SourceEvidence is the opposite-side observation, including observed absence; it is not
/// fresh deletion authority. Member effects and submission receipts remain separately owned.
/// Cursor belongs only to this operation, and retirement never implies successful removal.
/// </summary>
public sealed record FolderRemovalState(
    string OperationId,
    long Generation,
    FolderRemovalPhase Phase,
    Side DeletingSide,
    Entry RootEntry,
    Entry? SourceEvidence,
    SyncedEntry? SavedRoot,
    RowContext RootContext,
    string? Cursor,
    bool Active,
    TextKey? Cause,
    FolderRetirementDisposition? RetirementDisposition,
    FolderRemovalAuthority Authority)
{
    /// <summary>Where each send checks the opposite side against <see cref="SourceEvidence"/>.</summary>
    public string CounterpartPath =>
        SourceEvidence?.Path ?? SavedRoot?.PathOn(PathNames.Other(DeletingSide)) ?? RootEntry.Path;

    /// <summary>The absent-side identity a restored root had before this run restored it; null otherwise.</summary>
    public string? PreRestoreId { get; init; }
}
