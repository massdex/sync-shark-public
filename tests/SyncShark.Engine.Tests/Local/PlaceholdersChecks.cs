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
            unknown.Attributes |= FileAttributes.Offline;
            Check.Require((File.GetAttributes(unknown.FullName)&FileAttributes.Offline)!=0,"Fixture did not expose the residency flag");
            var pair=Scenarios.ItemChecks.Pair() with {LocalRoot=root.FullName}; var expected=L.DriveIdentity.Read(root.FullName).Identity;
            var scope=new PathScope(pair,[pair],DriveType.Fixed); var seen=new List<Listed>();
            await foreach(var row in L.DirectoryListing.ReadAsync(root.FullName,expected,scope,"",new(true,false),default)) seen.Add(row);
            Check.Require(seen.Count==1 && seen[0].Entry is {IsFolder:true,Skip:not null},"A nonresident directory supplied descendant coverage");
            try { L.DriveIdentity.Read(unknown.FullName); throw new InvalidDataException("A nonresident root was admitted"); } catch(IOException) { }
        }
        finally { root.Delete(true); }
    }
}
