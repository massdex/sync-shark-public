namespace Massdex.SyncShark.V2;

/// <summary>How a whole-folder delete ended.</summary>
public enum FolderTreeStatus
{
    /// <summary>Nothing was sent; the executor deletes this folder per item instead.</summary>
    NotSubmitted,

    /// <summary>The delete ran and the change-feed post-check finished.</summary>
    Completed,

    /// <summary>The delete may have run but its post-check (feed read or restore) did not finish.</summary>
    Uncertain,
}
