using System.ComponentModel;

namespace Massdex.SyncShark.V2;

public static class QuarantineFailure
{
    internal static string GrantWarning(string path) => "Defender exclusions may still cover these recorded paths: " + path + "; " + ArtifactPaths.DownloadStagePath(path) +
        ". This different entity or folder was not materialized. Review/remove the exact file and sibling-stage exclusions in Windows Security, then explicitly retry verification, or exclude/ignore this item. Sync Shark never removes exclusions automatically.";

    public const string NoticeKey = "antivirus-quarantine";
    public static int? NativeCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var code = current is Win32Exception native ? native.NativeErrorCode :
                ((uint)current.HResult & 0xffff0000u) == 0x80070000u ? current.HResult & 0xffff : 0;
            if (code is 225 or 226) return code;
        }
        return null;
    }
    public static bool IsQuarantine(Exception error) => NativeCode(error) is not null;
    public static string Describe(string path, Exception error) =>
        $"Antivirus blocked or quarantined '{path}'; Win32={NativeCode(error)}, HRESULT=0x{(0x80070000u | (uint)(NativeCode(error) ?? 0)):X8}. " +
        "Held on both sides, never treated as deleted. Retry explicitly, ignore, exclude from sync, " +
        "delete the reviewed Dropbox revision, or request a Defender exclusion (reduces protection).";
    internal static Notice Notice(string pairId, SyncAction action, string detail) =>
        new(pairId, 30, NoticeKind.UserDecision, action.Path, detail, NoticeKey,
            Quarantine: new(action.Target, action.Source, action.ExpectedTarget, PendingActionId: action.ProviderEffect is null ? null : action.ActionId, ObjectKey: action.ObjectKey, HeldSource: action.Source));
}
