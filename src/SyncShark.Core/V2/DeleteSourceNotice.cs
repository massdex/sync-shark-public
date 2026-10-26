namespace Massdex.SyncShark.V2;

internal static class DeleteSourceNotice
{
    internal const string ReasonKey = NoticeVocabulary.DeleteSourceReasonKey;
    internal static string NoticeId(string pairId, string path) => ShortId.Make("n", pairId, "0", PathKey.Of(path), ReasonKey);
}
