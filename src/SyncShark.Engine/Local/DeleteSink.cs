using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class DeleteSink(string path, HeldInfo held, Entry expected) : IFileOperationProgressSink
{
    private const uint SigdnFileSystemPath = 0x80058000;
    private const int S_OK = 0;
    private const int E_ABORT = unchecked((int)0x80004004);
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x0080;

    /// <summary>The definite pre-effect refusal cause, or null when the shell was allowed to act.</summary>
    public TextKey? VetoCause { get; private set; }
    /// <summary>The first specific per-item failing HRESULT the sink saw, or zero.</summary>
    public int ItemResult { get; private set; }
    /// <summary>The generic FinishOperations failure, or zero; never replaces <see cref="ItemResult"/>.</summary>
    public int FinishResult { get; private set; }
    /// <summary>Whether the shell reported a recycled item.</summary>
    public bool Received { get; private set; }
    /// <summary>The recycled item's exact path when the shell named it.</summary>
    public string? ReceiptPath { get; private set; }

    int IFileOperationProgressSink.PreDeleteItem(uint flags, IntPtr item)
    {
        if (!MatchesHeld(path, held, expected)) return E_ABORT;
        if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0) return S_OK;
        VetoCause = TextKey.RecycleUnavailable;
        return E_ABORT;
    }

    // The held identity is rechecked on the exact pathname at the last moment before the shell selects its
    // victim; the check and the shell action are not atomic, so a later arrival is detected after the effect.
    private bool MatchesHeld(string target, HeldInfo original, Entry selection)
    {
        try
        {
            using var fresh = LocalNative.Open(target, LocalNative.ReadAttributes, FileShare.ReadWrite | FileShare.Delete);
            var info=LocalNative.Info(fresh);
            if(info.Identity==original.Identity && LocalNative.Matches(info,selection,original.Domain,LocalNative.Classify(fresh,info.Attributes),true)) return true;
            VetoCause = TextKey.FailChanged;
            return false;
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            VetoCause = LocalErrors.Cause(error, TextKey.FailChanged);
            return false;
        }
    }

    int IFileOperationProgressSink.PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
    {
        if (result < 0 && ItemResult == 0) ItemResult = result;
        if (created != IntPtr.Zero)
        {
            Received = true;
            ReceiptPath = DisplayName(created);
        }
        return S_OK;
    }

    int IFileOperationProgressSink.FinishOperations(int result)
    {
        if (result < 0) FinishResult = result;
        return S_OK;
    }

    private static string? DisplayName(IntPtr shellItem)
    {
        IShellItem? item = null;
        IntPtr name = IntPtr.Zero;
        try
        {
            item = (IShellItem)Marshal.GetObjectForIUnknown(shellItem);
            if (item.GetDisplayName(SigdnFileSystemPath, out name) < 0 || name == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(name);
        }
        catch (Exception) { return null; }
        finally
        {
            if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    int IFileOperationProgressSink.StartOperations() => S_OK;
    int IFileOperationProgressSink.PreRenameItem(uint flags, IntPtr item, string name) => S_OK;
    int IFileOperationProgressSink.PostRenameItem(uint flags, IntPtr item, string name,
        int result, IntPtr created) => S_OK;
    int IFileOperationProgressSink.PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => S_OK;
    int IFileOperationProgressSink.PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name,
        int result, IntPtr created) => S_OK;
    int IFileOperationProgressSink.PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => S_OK;
    int IFileOperationProgressSink.PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name,
        int result, IntPtr created) => S_OK;
    int IFileOperationProgressSink.PreNewItem(uint flags, IntPtr destination, string name) => S_OK;
    int IFileOperationProgressSink.PostNewItem(uint flags, IntPtr destination, string name, string template,
        uint attributes, int result, IntPtr created) => S_OK;
    int IFileOperationProgressSink.UpdateProgress(uint total, uint completed) => S_OK;
    int IFileOperationProgressSink.ResetTimer() => S_OK;
    int IFileOperationProgressSink.PauseTimer() => S_OK;
    int IFileOperationProgressSink.ResumeTimer() => S_OK;
}
