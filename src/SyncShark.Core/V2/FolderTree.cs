namespace Massdex.SyncShark.V2;

/// <summary>One whole-folder delete the folder and the live descendants the plan froze.</summary>
public sealed record FolderTree(ItemState Folder, IReadOnlyList<ItemState> Victims)
{
    // Transient Executor-owned source admission; never part of the recovery checkpoint.
    internal Func<Task<bool>>? RevalidateDelete { get; init; }
}
