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
    FolderRetirementDisposition? RetirementDisposition);
