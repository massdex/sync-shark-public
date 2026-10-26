namespace Massdex.SyncShark.V2;

/// <summary>
/// The narrow durable evidence one direct download writes after verification and before it archives the original:
/// the verified <see cref="SyncDownloadStage"/>, carrying the stage path, object identity and Recovery locator.
/// The action itself already binds the item path, source and target.
/// </summary>
public static class SyncDirectDownloadCheckpoint
{
    public const string Kind = "local-download";
    internal const string NoticePrefix = "Download recovery: ";
    internal const string DateNoticePrefix = "Download date: ";
    internal static Notice? NoticeFor(string pairId, SyncAction action, ActionResult result) =>
        result.Reason?.StartsWith(DateNoticePrefix, StringComparison.Ordinal) == true
            ? new Notice(pairId, action.Rule, NoticeKind.Info, action.Path, result.Reason, "local-download-date") :
        result.Reason?.StartsWith(NoticePrefix, StringComparison.Ordinal) == true
            ? new Notice(pairId, action.Rule, NoticeKind.Info, action.Path, result.Reason, "local-download-recovery")
            : null;
}
