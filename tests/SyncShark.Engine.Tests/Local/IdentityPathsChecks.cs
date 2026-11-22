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
            var pair=Scenarios.ItemChecks.Pair() with {LocalRoot=root,Exclusions=[new(Side.Local,"aaa-excluded",true)]};
            var scoped=await reader.LocateInScopeAsync(root,expected,id,new PathScope(pair,[pair],DriveType.Fixed),default);
            Check.Require(scoped is { InScope:true } && scoped.Value.Location.NativePath==winner,"Lookup did not select the ordinal-first eligible alias");
            File.Delete(denied); File.Delete(winner);
            File.Delete(path); var outside=await reader.LocateAsync(root,expected,id,default);
            Check.Require(outside?.NativePath==alias && outside.Entry.Id==id,"Outside-only alias became absence");
            File.Delete(alias); Check.Require(await reader.LocateAsync(root,expected,id,default) is null,"Retired identity was still present");
        }
        finally { dir.Delete(true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern bool CreateHardLinkW(string newName,string existing,IntPtr security);
}
