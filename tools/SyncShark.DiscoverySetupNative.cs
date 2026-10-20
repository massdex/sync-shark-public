#if NET8_0_OR_GREATER
#nullable disable
#endif
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Compiled at build time and embedded inside signed SyncSharkDiscovery.exe.
// Elevated setup loads only these resource bytes, never a writable product DLL
// or compiler output. No runtime compiler or unclaimed temporary tree exists.
public static class SyncSharkDiscoverySetupNative
{
    [StructLayout(LayoutKind.Sequential)] struct FileInformation {
        public uint Attributes,CreatedLow,CreatedHigh,AccessedLow,AccessedHigh,WrittenLow,WrittenHigh;
        public uint Volume,SizeHigh,SizeLow,Links,IdHigh,IdLow;
    }
    [StructLayout(LayoutKind.Sequential)] struct Mapping { public uint Read, Write, Execute, All; }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct TrustFile {
        public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, Subject;
    }
    [StructLayout(LayoutKind.Sequential)] struct TrustData {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice;
        public IntPtr File; public uint Action; public IntPtr State, Url; public uint Flags, Context; public IntPtr Settings;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(
        string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileProtected(
        string path,uint access,uint share,ref SecurityAttributes security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="CreateDirectoryW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateDirectoryProtected(string path,ref SecurityAttributes security);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInformation info);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr value,uint length,out uint required);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr token,uint access,IntPtr security,int level,int type,out IntPtr copy);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool AccessCheck(IntPtr descriptor,IntPtr token,uint access,ref Mapping mapping,byte[] privileges,ref uint length,out uint granted,out bool allowed);
    [DllImport("advapi32.dll",SetLastError=true)] static extern uint GetSecurityInfo(SafeFileHandle file,int kind,uint information,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetSecurityDescriptorOwner(IntPtr descriptor,out IntPtr owner,out bool defaulted);
    [DllImport("wintrust.dll",ExactSpelling=true)] static extern uint WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);

    private static SecurityAttributes ProductSecurity(bool directory) {
        IntPtr descriptor;uint size;
        string inherit=directory?"OICI":"";
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor("O:BAG:BAD:P(A;"+inherit+";FA;;;SY)(A;"+inherit+";FA;;;BA)(A;"+inherit+";FRFX;;;BU)",1,out descriptor,out size))throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SecurityAttributes{Size=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=descriptor,Inherit=0};
    }
    public static FileStream CreateProtectedFile(string path,bool fresh) {return OpenProtectedFile(path,fresh?1u:4u);}
    // Routine updater claims must never create a control file during refusal.
    public static FileStream OpenProtectedFile(string path) {return OpenProtectedFile(path,3u);}
    private static FileStream OpenProtectedFile(string path,uint disposition) {
        SecurityAttributes security=ProductSecurity(false);SafeFileHandle handle=null;IntPtr medium=IntPtr.Zero;
        try {
            handle=CreateFileProtected(path,0xc0020080,0,ref security,disposition,0x00200000,IntPtr.Zero);
            if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            FileInformation info;
            if(!GetFileInformationByHandle(handle,out info) || (info.Attributes&0x1410)!=0 || info.Links!=1)throw new IOException("Unexpected transaction file type or alias.");
            medium=MediumToken();RequireProtected(handle,medium,false);
            var stream=new FileStream(handle,FileAccess.ReadWrite);handle=null;return stream;
        } finally {if(handle!=null)handle.Dispose();CloseToken(medium);LocalFree(security.Descriptor);}
    }
    public static void CreateProtectedDirectory(string path) {
        SecurityAttributes security=ProductSecurity(true);
        try {if(!CreateDirectoryProtected(path,ref security))throw new Win32Exception(Marshal.GetLastWin32Error());}
        finally {LocalFree(security.Descriptor);}
    }

    public static SafeFileHandle Hold(string path,bool directory) {
        FileAttributes attributes=File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint|FileAttributes.Offline))!=0 || ((attributes&FileAttributes.Directory)!=0)!=directory)
            throw new IOException("Setup cannot cross a reparse point or offline item.");
        SafeFileHandle handle=CreateFile(path,0x00020080u|(directory?0u:1u),1,IntPtr.Zero,3,0x00200000u|(directory?0x02000000u:0u),IntPtr.Zero);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        FileInformation info;
        if(!GetFileInformationByHandle(handle,out info) || (info.Attributes&0x1400)!=0 || ((info.Attributes&0x10)!=0)!=directory || (!directory&&info.Links!=1)) {
            handle.Dispose();throw new IOException("The held setup object is a reparse point, offline, aliased or changed type.");
        }
        return handle;
    }
    public static IntPtr MediumToken() {
        IntPtr high=IntPtr.Zero,linked=IntPtr.Zero,medium=IntPtr.Zero,buffer=Marshal.AllocHGlobal(IntPtr.Size);
        try {
            if(!OpenProcessToken(GetCurrentProcess(),8,out high))throw new Win32Exception(Marshal.GetLastWin32Error());
            uint required;
            if(!GetTokenInformation(high,19,buffer,(uint)IntPtr.Size,out required))throw new Win32Exception(Marshal.GetLastWin32Error());
            linked=Marshal.ReadIntPtr(buffer);
            // Only AccessCheck consumes this linked identity. Windows can return
            // an identification-only token; do not request an impersonation upgrade
            // (1346) or TOKEN_IMPERSONATE rights that the setup never uses.
            if(!DuplicateTokenEx(linked,8,IntPtr.Zero,1,2,out medium))throw new Win32Exception(Marshal.GetLastWin32Error());
            return medium;
        } finally { if(high!=IntPtr.Zero)CloseHandle(high);if(linked!=IntPtr.Zero)CloseHandle(linked);Marshal.FreeHGlobal(buffer); }
    }
    public static void CloseToken(IntPtr token) { if(token!=IntPtr.Zero)CloseHandle(token); }
    public static void RequireProtected(SafeFileHandle handle,IntPtr medium,bool directory) {
        IntPtr descriptor; uint error=GetSecurityInfo(handle,1,7,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,out descriptor);
        if(error!=0)throw new Win32Exception((int)error);
        try {
            IntPtr owner;bool defaulted;
            if(!GetSecurityDescriptorOwner(descriptor,out owner,out defaulted))throw new Win32Exception(Marshal.GetLastWin32Error());
            string sid=new System.Security.Principal.SecurityIdentifier(owner).Value;
            if(sid!="S-1-5-18" && sid!="S-1-5-32-544" && sid!="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
                throw new UnauthorizedAccessException("Protected product code has an ordinary-user owner.");
            Mapping mapping=new Mapping{Read=0x00120089,Write=0x00120116,Execute=0x001200a0,All=0x001f01ff};
            byte[] privileges=new byte[4096];uint length=4096,granted;bool allowed;
            if(!AccessCheck(descriptor,medium,0x02000000,ref mapping,privileges,ref length,out granted,out allowed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            uint denied=0x000d0116u|(directory?0x40u:0u);
            if(!allowed || (granted&denied)!=0)throw new UnauthorizedAccessException("The Discovery installation is writable by the ordinary user.");
        } finally {LocalFree(descriptor);}
    }
    public static void RequireProductSignature(string path) {
        TrustFile file=new TrustFile{Size=(uint)Marshal.SizeOf(typeof(TrustFile)),Path=path};
        IntPtr pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));Marshal.StructureToPtr(file,pointer,false);
        TrustData data=new TrustData{Size=(uint)Marshal.SizeOf(typeof(TrustData)),Ui=2,Choice=1,File=pointer,Action=1,Flags=0x1000};
        Guid action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try {
            uint status=WinVerifyTrust(new IntPtr(-1),ref action,ref data);
            if(status!=0 && status!=0x800b0109)throw new IOException("The product signature or file integrity is invalid.");
        } finally {data.Action=2;WinVerifyTrust(new IntPtr(-1),ref action,ref data);Marshal.DestroyStructure(pointer,typeof(TrustFile));Marshal.FreeHGlobal(pointer);}
    }
}
