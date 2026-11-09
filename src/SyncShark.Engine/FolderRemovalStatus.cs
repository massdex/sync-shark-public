namespace Massdex.SyncShark.Engine;

/// <summary>Aggregate outcome of a selected folder removal operation.</summary>
public enum FolderRemovalStatus
{
    Complete,
    Partial,
    Retained,
    Unknown
}
