namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Contract checks for DirectoryListing.</summary>
public static class DirectoryListingChecks
{
    /// <summary>Verifies the DirectoryListing contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var root=dir.CreateSubdirectory("selected").FullName;var other=dir.CreateSubdirectory("replacement").FullName;File.WriteAllText(Path.Combine(other,"occupant"),"other");
            var expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;var pair=Scenarios.ItemChecks.Pair() with { LocalRoot=root };var paths=new PathScope(pair,[pair],DriveType.Fixed);
            File.WriteAllText(Path.Combine(root,"selected-file"),"selected");
            var listed=new List<Listed>();await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default)) listed.Add(row);
            Check.Require(listed.Count==1 && listed[0] is {Path:"selected-file",Entry:{IsFolder:false}},"Expected root did not list its selected file");
            try { await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(other,expected,paths,"",new(true,false),default)) throw new InvalidDataException("Read from a different root"); }
            catch(ChangedUnderneathException) { return; } catch(InventoryIncompleteException) { return; }
            throw new InvalidDataException("Replaced root supplied empty coverage");
        }
        finally { dir.Delete(true); }
    }
}
