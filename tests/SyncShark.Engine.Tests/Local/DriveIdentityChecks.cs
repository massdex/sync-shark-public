using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the DriveIdentity adapter against observed outcomes.</summary>
public static class DriveIdentityChecks
{
    public static Task RunAsync()
    {
        try { L.DriveIdentity.Read(@"\\example.invalid\share\folder"); throw new InvalidDataException("UNC root accepted"); }
        catch(ItemFailedException e) when(L.LocalErrors.RootReadCause(e)==TextKey.RootLocalNetworkUnsupported) { }
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var first=dir.CreateSubdirectory("first").FullName;
            var second=dir.CreateSubdirectory("second").FullName;
            var (id,type)=L.DriveIdentity.Read(first);
            var moved=Path.Combine(dir.FullName,"moved"); Directory.Move(first,moved);
            Check.Require(L.DriveIdentity.Read(moved).Identity==id && L.DriveIdentity.Read(second).Identity!=id,"Identity did not track the selected directory");
            Check.Require(type==new DriveInfo(Path.GetPathRoot(first)!).DriveType,"Drive type came from another drive");
        }
        finally { dir.Delete(true); }
        return Task.CompletedTask;
    }
}
