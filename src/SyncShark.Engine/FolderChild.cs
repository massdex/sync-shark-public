namespace Massdex.SyncShark.Engine;

/// <summary>One entry under a folder being deleted, with its verdict (section 6.5).</summary>
public sealed record FolderChild(Entry? OnDeletingSide, SyncedEntry? Synced, FolderVerdict Verdict);
