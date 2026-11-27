using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the IdentityPaths adapter against observed outcomes.</summary>
public static class IdentityPathsChecks
{
    public static async Task RunAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var root=dir.CreateSubdirectory("root").FullName;
            var path=Path.Combine(root,"source"); var alias=Path.Combine(dir.FullName,"alias"); File.WriteAllText(path,"bytes");
            if(!CreateHardLinkW(alias,path,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var expected=L.DriveIdentity.Read(root).Identity; var id=L.DirectoryListing.One(root,"source")!.Id;
            var number=unchecked((ulong)long.Parse(id,System.Globalization.CultureInfo.InvariantCulture)); var reader=new L.IdentityPaths();
            var (_,links)=L.IdentityPaths.ResolveLinksById(root,expected,number,true,default);
            Check.Require(links is not null && links.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([path,alias]),"Did not resolve all live hard-link names");
            var denied=Path.Combine(root,"aaa-excluded"); var winner=Path.Combine(root,"bbb-eligible");
            if(!CreateHardLinkW(denied,path,IntPtr.Zero) || !CreateHardLinkW(winner,path,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var pair=Scenarios.ItemChecks.Pair() with {LocalRoot=root,Exclusions=[new(Side.Local,"aaa-excluded",true)],Roots=null};
            var scoped=await reader.LocateInScopeAsync(root,expected,id,new PathScope(pair,[pair],DriveType.Fixed),default);
            Check.Require(scoped is { InScope:true } && scoped.Value.Location.NativePath==winner,"Lookup did not select the ordinal-first eligible alias");
            File.Delete(denied); File.Delete(winner);
            File.Delete(path); var outside=await reader.LocateAsync(root,expected,id,default);
            Check.Require(outside?.NativePath==alias && outside.Entry.Id==id,"Outside-only alias became absence");
            File.Delete(alias); Check.Require(await reader.LocateAsync(root,expected,id,default) is null,"Retired identity was still present");
            RequireRetiredError(root, expected.FileId, number);
            var scope = new PathScope(pair,[pair],DriveType.Fixed);
            Check.Require(await reader.LocateInScopeAsync(root,expected,id,scope,default) is null,"Retired child under verified root was treated as a root failure");
            // L1: a file left with one name still reports it; a plain single-link file reports its only name. A shortcut
            // that hides the singleton (force enumeration off) returns null and fails here.
            var solo=Directory.CreateDirectory(Path.Combine(root,"solo"));
            var soloRoot=L.DriveIdentity.Read(solo.FullName).Identity;
            var soloPair=Scenarios.ItemChecks.Pair() with {LocalRoot=solo.FullName,Roots=null};
            var soloScope=new PathScope(soloPair,[soloPair],DriveType.Fixed);
            var kept=Path.Combine(solo.FullName,"kept"); var gone=Path.Combine(solo.FullName,"gone");
            File.WriteAllText(kept,"solo");
            if(!CreateHardLinkW(gone,kept,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var keptId=L.DirectoryListing.One(solo.FullName,"kept")!.Id;
            File.Delete(gone);
            var survivor=L.IdentityPaths.LinkNamesById(solo.FullName,soloRoot,keptId,soloScope,default);
            Check.Require(survivor is {Count:1} && PathNames.Same(survivor[0],"kept"),"A one-name survivor was hidden instead of listed");
            var lone=Path.Combine(solo.FullName,"lone"); File.WriteAllText(lone,"lone");
            var loneId=L.DirectoryListing.One(solo.FullName,"lone")!.Id;
            var loneNames=L.IdentityPaths.LinkNamesById(solo.FullName,soloRoot,loneId,soloScope,default);
            Check.Require(loneNames is {Count:1} && PathNames.Same(loneNames[0],"lone"),"A single-link name was hidden instead of listed");
            Directory.Move(root,Path.Combine(dir.FullName,"moved-root"));
            try { await reader.LocateInScopeAsync(root,expected,id,scope,default); throw new InvalidDataException("Missing configured root supplied item absence"); }
            catch(ChangedUnderneathException e) when(e.Path==root && e.InnerException is System.ComponentModel.Win32Exception {NativeErrorCode:2 or 3}) { }
            catch(System.ComponentModel.Win32Exception e) { throw new InvalidDataException("Missing configured root escaped as by-ID item I/O",e); }

        }
        finally { dir.Delete(true); }
    }
    private static void RequireRetiredError(string root, ulong rootId, ulong retiredId)
    {
        const uint backupSemantics = 0x02000000; // FILE_FLAG_BACKUP_SEMANTICS, WinBase.h
        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        using var hint = CreateFileW(root, FileSystemRights.ReadAttributes, share, IntPtr.Zero, FileMode.Open, backupSemantics, IntPtr.Zero);
        if (hint.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Id = rootId };
        using var control = OpenFileById(hint, ref descriptor, FileSystemRights.ReadAttributes, share, IntPtr.Zero, backupSemantics);
        if (control.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        descriptor.Id = retiredId;
        using var retired = OpenFileById(hint, ref descriptor, FileSystemRights.ReadAttributes, share, IntPtr.Zero, backupSemantics);
        var error = Marshal.GetLastWin32Error();
        const int invalidParameter = 87; // ERROR_INVALID_PARAMETER, WinError.h
        Check.Require(retired.IsInvalid && error == invalidParameter, $"Retired OpenFileById: invalid={retired.IsInvalid}, actual error {error}, expected {invalidParameter}");
    }

    // FILE_ID_DESCRIPTOR (WinBase.h): size and FileIdType followed by the 16-byte identifier union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public ulong Id;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, FileSystemRights access, FileShare share, IntPtr security, FileMode creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id, FileSystemRights access, FileShare share, IntPtr security, uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern bool CreateHardLinkW(string newName,string existing,IntPtr security);
}
