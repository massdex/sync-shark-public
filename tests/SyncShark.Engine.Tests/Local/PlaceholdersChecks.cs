using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the Placeholders adapter against observed outcomes.</summary>
public static class PlaceholdersChecks
{
    public static async Task RunAsync()
    {
        const FileAttributes reparse=FileAttributes.ReparsePoint, recall=(FileAttributes)0x00400000;
        Check.Require(L.Placeholders.IsOnlineOnly(reparse|recall,0x9000301a) && L.Placeholders.IsOnlineOnly(recall,0),"Recall-only files were treated as resident");
        Check.Require(!L.Placeholders.IsOnlineOnly(reparse,0x9000001a) && !L.Placeholders.IsOnlineOnly(reparse|recall,0xa000000c) && !L.Placeholders.IsOnlineOnly(FileAttributes.Directory|recall,0),"Resident, linked or directory entries were treated as online-only files");
        var root=Directory.CreateTempSubdirectory();
        try
        {
            var unknown=root.CreateSubdirectory("unknown"); File.WriteAllText(Path.Combine(unknown.FullName,"child"),"local fixture");
            File.WriteAllText(Path.Combine(root.FullName,"healthy"),"sibling");
            unknown.Attributes |= FileAttributes.Offline;
            Check.Require((File.GetAttributes(unknown.FullName)&FileAttributes.Offline)!=0,"Fixture did not expose the residency flag");
            foreach (var name in new[]{"foreign-resident","foreign-offline"})
            {
                var file=Path.Combine(root.FullName,name); File.WriteAllText(file,"fixture");
                if(name=="foreign-offline") File.SetAttributes(file,FileAttributes.Offline);
                using var handle=File.OpenHandle(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                // A private non-Microsoft tag carries a GUID and no provider payload (REPARSE_GUID_DATA_BUFFER).
                var data=new byte[24]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data,0x00000042);
                Guid.NewGuid().TryWriteBytes(data.AsSpan(8));
                if(!DeviceIoControl(handle,0x000900a4,data,data.Length,IntPtr.Zero,0,out _,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            }
            var pair=Scenarios.ItemChecks.Pair() with {LocalRoot=root.FullName,Roots=null}; var expected=L.DriveIdentity.Read(root.FullName).Identity;
            var scope=new PathScope(pair,[pair],DriveType.Fixed); var seen=new List<Listed>();
            await foreach(var row in L.DirectoryListing.ReadAsync(root.FullName,expected,scope,"",new(true,false),default)) seen.Add(row);
            Check.Require(seen.Any(row => row is {Path:"unknown",Entry:{IsFolder:true,Skip:nameof(TextKey.SkipUnreadable)}}),
                "Nonresident directory was not marked SkipUnreadable");
            foreach(var (name,expectedSkip) in new[]{("foreign-offline",TextKey.SkipUnreadable),("foreign-resident",TextKey.SkipSpecial)})
            {
                var listed=seen.Single(row=>row.Path==name).Entry;
                Check.Require(listed is {IsFolder:false} && listed.Skip==expectedSkip.ToString(),"Foreign reparse file had the wrong listing outcome: "+name);
                Check.Require(L.DirectoryListing.One(root.FullName,name)==listed,"Exact-name foreign reparse observation differed from listing: "+name);
            }
            Check.Require(seen.Count==4 && seen.Any(row => row is {Path:"healthy",Entry:{IsFolder:false,Skip:null}}),
                "The walk missed the healthy sibling or reported a child of the unreadable directory");
            try { L.DriveIdentity.Read(unknown.FullName); throw new InvalidDataException("A nonresident root was admitted"); } catch(IOException) { }
        }
        finally { root.Delete(true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file,uint code,byte[] input,int size,IntPtr output,int capacity,out int returned,IntPtr overlapped);
}
