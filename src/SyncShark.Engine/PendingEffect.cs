namespace Massdex.SyncShark.Engine;

/// <summary>The pair's single durable local removal note (C-10), held until exact settlement. Version is the encoding
/// discriminator, not a tunable. Saved is copied unchanged from the selection: null means no saved binding was observed,
/// not that none was supplied. Victim proves a wrong-restore receipt; it is never an owned disposable stage.</summary>
public sealed record PendingEffect(int Version, string OperationId, long Generation, PendingKind Kind,
    PendingPhase Phase, string Path, RemovalProof Losing, SyncedEntry? Saved,
    Entry? Source, StageProof? Stage, string? BinLocator, StageProof? Victim,
    bool PermanentAllowed, TextKey? Cause);
