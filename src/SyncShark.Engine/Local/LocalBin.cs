using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Runs the native IFileOperation recycle protocol on an STA thread and classifies its exact callbacks.</summary>
public static class LocalBin
{
    private const string ShellLibrary = "shell32.dll";
    private const string FileOperationClassId = "3ad05575-8857-4850-9277-11b85bdb8e09";
    private const string FileOperationInterfaceId = "947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8";
    private const string ShellItemInterfaceId = "43826d1e-e718-42ee-bc55-a1e261c37bfe";
    private const string ProgressSinkInterfaceId = "04b0f1a7-9490-44bc-96e1-4296a31252e2";
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x0080;
    private const uint SigdnFileSystemPath = 0x80058000;
    private const int S_OK = 0;
    private const int E_ABORT = unchecked((int)0x80004004);

    /// <summary>Test seam: when set, called instead of the shell; true means a verified expected recycle.</summary>
    internal static Func<string, bool>? SendOverride { get; set; }

    internal static (RemovalResult? Result, string? BinLocator) RunShell(string fullPath,
        PilotDirectoryIdentity? held)
    {
        var sendOverride = SendOverride;
        RemovalResult? result = null;
        string? binLocator = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (sendOverride is null)
                {
                    (result, binLocator) = SendToShell(fullPath, held);
                }
                else
                {
                    result = sendOverride(fullPath)
                        ? new RemovalResult(RemovalOutcome.Removed, null, null, null)
                        : new RemovalResult(RemovalOutcome.NoEffect, null, null, TextKey.RecycleUnavailable);
                }
            }
            catch (Exception error)
                when (error is ChangedUnderneathException or ItemFailedException) { failure = error; }
            catch (Exception error)
            {
                failure = new ItemFailedException(fullPath, LocalErrors.Cause(error, TextKey.RecycleFailed), error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return (result, binLocator);
    }

    private static (RemovalResult? Result, string? BinLocator) SendToShell(string fullPath,
        PilotDirectoryIdentity? held)
    {
        IFileOperation? operation = null;
        var item = IntPtr.Zero;
        var sink = new DeleteSink(fullPath, held);
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid(FileOperationClassId), throwOnError: true)!)!;
            CheckResult(operation.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION |
                FOF_NOERRORUI | FOF_SILENT | FOFX_RECYCLEONDELETE), fullPath);
            var iid = new Guid(ShellItemInterfaceId);
            CheckResult(SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iid, out item), fullPath);
            CheckResult(operation.DeleteItem(item, sink), fullPath);
            int aggregate;
            try { aggregate = operation.PerformOperations(); }
            catch (Exception error)
            {
                return (new RemovalResult(RemovalOutcome.Unknown, null, null,
                    LocalErrors.Cause(error, TextKey.RecycleFailed)), null);
            }
            return Classify(operation, sink, aggregate, held);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }

    // The sink's own veto is the only definite pre-effect outcome. A specific per-item HRESULT outranks the
    // aggregate PerformOperations/FinishOperations result, and an abort never becomes an in-use cause. A named
    // receipt that must be checked against the held identity is left to the caller (null result), because reading
    // its content is asynchronous.
    private static (RemovalResult? Result, string? BinLocator) Classify(IFileOperation operation, DeleteSink sink,
        int aggregate, PilotDirectoryIdentity? held)
    {
        if (sink.VetoCause is { } veto)
            return (new RemovalResult(RemovalOutcome.NoEffect, null, null, veto), null);
        if (sink.ItemResult < 0)
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, CauseOf(sink.ItemResult)), null);
        if (aggregate < 0)
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, CauseOf(aggregate)), null);
        if (sink.FinishResult < 0)
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, CauseOf(sink.FinishResult)), null);
        if (operation.GetAnyOperationsAborted(out var aborted) < 0 || aborted)
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, TextKey.RecycleFailed), null);
        if (!sink.Received) return (new RemovalResult(RemovalOutcome.Unknown, null, null, null), null);
        if (sink.ReceiptPath is null) return (new RemovalResult(RemovalOutcome.Unknown, null, null, null), null);
        if (held is null) return (new RemovalResult(RemovalOutcome.Removed, null, null, null), sink.ReceiptPath);
        return (null, sink.ReceiptPath);
    }

    private static void CheckResult(int result, string path)
    {
        if (result < 0)
            throw new ItemFailedException(path, CauseOf(result), Marshal.GetExceptionForHR(result));
    }

    private static TextKey CauseOf(int result) =>
        LocalErrors.Cause(Marshal.GetExceptionForHR(result) ?? new COMException(string.Empty, result),
            TextKey.RecycleFailed);

    [DllImport(ShellLibrary, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        ref Guid interfaceId, out IntPtr item);

    // COM vtable order includes unused methods; trimming them would call the wrong shell operation.
    [ComImport, Guid(FileOperationInterfaceId), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name,
            IFileOperationProgressSink sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IntPtr item, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IntPtr item, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IntPtr item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr destination, uint attributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template,
            IFileOperationProgressSink sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    // COM vtable order includes unused methods; trimming them would call the wrong shell item operation.
    [ComImport, Guid(ShellItemInterfaceId), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId,
            out IntPtr result);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint displayName, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IntPtr other, uint hint, out int order);
    }

    [ComImport, Guid(ProgressSinkInterfaceId), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item,
            [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination,
            [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template,
            uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeleteSink(string path, PilotDirectoryIdentity? held) : IFileOperationProgressSink
    {
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
            if (held is not null && !MatchesHeld(path, held)) return E_ABORT;
            if ((flags & TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0) return S_OK;
            VetoCause = TextKey.RecycleUnavailable;
            return E_ABORT;
        }

        // The held identity is rechecked on the exact pathname at the last moment before the shell selects its
        // victim; the check and the shell action are not atomic, so a later arrival is detected after the effect.
        private bool MatchesHeld(string target, PilotDirectoryIdentity expected)
        {
            try
            {
                using var fresh = SyncLocalFileAuthority.OpenExistingRecycleGuard(target);
                if (fresh.ReadIdentity() == expected) return true;
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
}
