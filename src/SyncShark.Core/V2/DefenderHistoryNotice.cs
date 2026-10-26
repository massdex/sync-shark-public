namespace Massdex.SyncShark.V2;

public sealed record DefenderHistoryNotice(string RootId, string Batch, long Count, string Cause, RootDef? ReviewedRoot = null,
    DefenderSharedRelease? SharedRelease = null);
