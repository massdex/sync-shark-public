namespace Massdex.SyncShark.Engine;

/// <summary>Current aggregate outcome for one folder removal operation and root, not an individual effect receipt.</summary>
public sealed record FolderRemovalResult(
    string OperationId,
    string RootPath,
    FolderRemovalStatus Status);
