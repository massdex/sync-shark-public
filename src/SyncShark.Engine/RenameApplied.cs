namespace Massdex.SyncShark.Engine;

/// <summary>A committed move. Before == null: a folder-spelling move with no saved record, so only After is recorded.
/// CounterpartSide == null: no counterpart was moved (Adopt); the path fields are then null.</summary>
public sealed record RenameApplied(SyncedEntry? Before, SyncedEntry After, Side? CounterpartSide,
    string? CounterpartOldPath, string? CounterpartNewPath);
