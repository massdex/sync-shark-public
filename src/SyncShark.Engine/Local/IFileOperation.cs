using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

// COM vtable order includes unused methods; trimming them would call the wrong shell operation.
[ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
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
