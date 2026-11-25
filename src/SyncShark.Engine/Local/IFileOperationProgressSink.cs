using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

[ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
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
