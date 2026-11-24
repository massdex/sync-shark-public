namespace Massdex.SyncShark.Engine;

/// <summary>A scan entry whose id matches a synced item at a different path (section 3.4).</summary>
public sealed record RenameCandidate(Side Side, Entry Moved, SyncedEntry Synced);
