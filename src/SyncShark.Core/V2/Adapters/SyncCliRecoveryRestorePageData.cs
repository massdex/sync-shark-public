namespace Massdex.SyncShark.V2;

/// <summary>
/// One bounded restore page: each row is either previewed (dry run, the default) or restored, and a full page
/// carries the exclusive cursor for the next invocation. No invocation ever loads or restores a whole history.
/// </summary>
public sealed record SyncCliRecoveryRestorePageData(string PairId,
    IReadOnlyList<SyncCliRecoveryRestoreItemData> Items, bool DryRun, bool AsCopy, int Returned, bool HasMore,
    bool MayHaveMore, string? NextCursor, string CurrentStateHint, IReadOnlyList<string> Warnings);
