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
        // Exercise the parent-listing metadata directly: no foreign reparse provider is needed.
        var skip = typeof(L.Placeholders).GetMethod("Skip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<FileAttributes, uint, TextKey?>>();
        const uint foreignTag = 0x80000004; // IO_REPARSE_TAG_HSM
        foreach (var nonresident in new[]{FileAttributes.Offline, (FileAttributes)0x00040000, recall})
        {
            Check.Require(skip(reparse | nonresident, foreignTag) == TextKey.SkipUnreadable, "Nonresident foreign reparse file was not marked SkipUnreadable");
            Check.Require(skip(reparse | nonresident, 0xa000000c) == TextKey.SkipLink &&
                skip(reparse | nonresident, 0x9000301a) == TextKey.SkipOnlineOnly && skip(nonresident, 0) == TextKey.SkipOnlineOnly,
                "Nonresident link or supported placeholder lost its classification");
        }
        Check.Require(skip(reparse, foreignTag) == TextKey.SkipSpecial && skip(reparse, 0x9000301a) is null, "Resident reparse classification changed");
        var root=Directory.CreateTempSubdirectory();
        try
        {
            var unknown=root.CreateSubdirectory("unknown"); File.WriteAllText(Path.Combine(unknown.FullName,"child"),"local fixture");
            File.WriteAllText(Path.Combine(root.FullName,"healthy"),"sibling");
            unknown.Attributes |= FileAttributes.Offline;
            Check.Require((File.GetAttributes(unknown.FullName)&FileAttributes.Offline)!=0,"Fixture did not expose the residency flag");
            var pair=Scenarios.ItemChecks.Pair() with {LocalRoot=root.FullName}; var expected=L.DriveIdentity.Read(root.FullName).Identity;
            var scope=new PathScope(pair,[pair],DriveType.Fixed); var seen=new List<Listed>();
            await foreach(var row in L.DirectoryListing.ReadAsync(root.FullName,expected,scope,"",new(true,false),default)) seen.Add(row);
            Check.Require(seen.Any(row => row is {Path:"unknown",Entry:{IsFolder:true,Skip:nameof(TextKey.SkipUnreadable)}}),
                "Nonresident directory was not marked SkipUnreadable");
            Check.Require(seen.Count==2 && seen.Any(row => row is {Path:"healthy",Entry:{IsFolder:false,Skip:null}}),
                "The walk missed the healthy sibling or reported a child of the unreadable directory");
            try { L.DriveIdentity.Read(unknown.FullName); throw new InvalidDataException("A nonresident root was admitted"); } catch(IOException) { }
        }
        finally { root.Delete(true); }
    }
}
