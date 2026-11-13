namespace Massdex.SyncShark.Engine;

/// <summary>Preserves classified descendant evidence for durable folder removal without reclassification.</summary>
public sealed record FrozenFolderEntry(
    long Ordinal,
    string Key,
    string NativeKey,
    Entry? Entry,
    SyncedEntry? Saved,
    FolderVerdict Verdict,
    bool FollowUp,
    bool SelectedBranch);
