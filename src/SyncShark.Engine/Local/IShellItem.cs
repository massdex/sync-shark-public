using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

// COM vtable order includes unused methods; trimming them would call the wrong shell item operation.
[ComImport, Guid(InterfaceId), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    // Single source of the shell item IID: this interface's [Guid] and RecycleBin's SHCreateItemFromParsingName riid.
    public const string InterfaceId = "43826d1e-e718-42ee-bc55-a1e261c37bfe";
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId,
        out IntPtr result);
    [PreserveSig] int GetParent(out IntPtr parent);
    [PreserveSig] int GetDisplayName(uint displayName, out IntPtr name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IntPtr other, uint hint, out int order);
}
