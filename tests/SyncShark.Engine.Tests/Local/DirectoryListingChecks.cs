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
            var expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;var pair=Scenarios.ItemChecks.Pair() with { LocalRoot=root, Exclusions=[new(Side.Local,"excluded",true)] };var paths=new PathScope(pair,[pair],DriveType.Fixed);
            File.WriteAllText(Path.Combine(root,"selected-file"),"selected");
            var excluded=Directory.CreateDirectory(Path.Combine(root,"excluded")); File.WriteAllText(Path.Combine(excluded.FullName,"hidden"),"excluded");
            // Deny list access: filtering after opening would fail even if neither rows nor callbacks escaped.
            using (var locked=CreateFileW(excluded.FullName,1,FileShare.None,IntPtr.Zero,3,0x02000000,IntPtr.Zero))
            {
                if(locked.IsInvalid) throw new System.ComponentModel.Win32Exception();
                var controlFailed=false;
                try { await foreach(var _ in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"excluded",new(true,true),default)) { } }
                catch(InventoryIncompleteException e) when(e.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 }) { controlFailed=true; }
                Check.Require(controlFailed,"Excluded fixture did not refuse a real directory open");
                try { await foreach(var _ in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"excluded",new(true,false),default)) throw new InvalidDataException("Excluded range yielded a row"); }
                catch(InventoryIncompleteException e) { throw new InvalidDataException("Opened an excluded directory before pruning",e); }
                var shareFailures = 0;
                var thread = Environment.CurrentManagedThreadId;
                void SawDeniedOpen(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
                {
                    if (Environment.CurrentManagedThreadId == thread && e.Exception is System.ComponentModel.Win32Exception { NativeErrorCode: 32 }) ++shareFailures;
                }
                AppDomain.CurrentDomain.FirstChanceException += SawDeniedOpen;
                try
                {
                    // Observe the denied native open even if the listing later swallows it and filters every row.
                    await foreach (var _ in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,true),default)) { }
                    Check.Require(shareFailures!=0,"Recursive include control did not reach the locked child");
                    shareFailures=0;
                    var sawSibling=false;
                    await foreach (var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default))
                        sawSibling |= row.Path=="selected-file";
                    Check.Require(sawSibling && shareFailures==0,"Recursive listing opened an excluded child before filtering");
                }
                finally { AppDomain.CurrentDomain.FirstChanceException -= SawDeniedOpen; }
            }
            var visited=new List<string>();
            var listed=new List<Listed>();await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false){ DirectoryAcquired=(path,_)=>visited.Add(path) },default)) listed.Add(row);
            Check.Require(listed.Count==1 && listed[0] is {Path:"selected-file",Entry:{IsFolder:false}},"Expected root did not list its selected file");
            Check.Require(visited.Count==0 && global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"SELECTED-FILE")==listed[0].Entry && global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"absent") is null,"Excluded subtree opened or exact-name metadata differed from listing");
            var vanishing=Directory.CreateDirectory(Path.Combine(root,"vanishing")); var unreadable=false;
            await foreach (var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default))
            {
                if (row.Path!="vanishing") continue;
                if (row.Entry?.Skip==TextKey.SkipUnreadable.ToString()) unreadable=true; else vanishing.Delete();
            }
            Check.Require(unreadable,"A folder lost during the full walk was treated as empty");
            foreach (var strict in new[]{false,true})
            {
                var scope=Directory.CreateDirectory(Path.Combine(root,strict ? "strict" : "ordinary"));
                var selected=scope.CreateSubdirectory("child"); var sibling=scope.CreateSubdirectory("healthy");
                File.WriteAllText(Path.Combine(sibling.FullName,"kept"),"healthy"); bool withheld=false, healthy=false;
                await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,scope.Name,new(true,false){RequireListedIdentity=strict},default))
                {
                    if(row.Path==scope.Name+"/child" && row.Entry?.Skip is null)
                    {
                        Directory.Move(selected.FullName,Path.Combine(other,scope.Name));
                        Directory.CreateDirectory(selected.FullName); File.WriteAllText(Path.Combine(selected.FullName,"substitute"),"new occupant");
                    }
                    else if(row.Path==scope.Name+"/child" && row.Entry?.Skip==TextKey.SkipUnreadable.ToString()) withheld=true;
                    else if(row.Path==scope.Name+"/healthy/kept") healthy=true;
                    else Check.Require(!row.Path.EndsWith("/substitute",StringComparison.Ordinal),"Substitute supplied descendant coverage");
                }
                Check.Require(withheld && healthy,"Lost descendant supplied empty coverage or stopped healthy siblings");
            }
            Directory.CreateDirectory(Path.Combine(other,"sub")); File.WriteAllText(Path.Combine(other,"sub","outside"),"outside");
            var link=Path.Combine(root,"link"); Directory.CreateSymbolicLink(link,other);
            try
            {
                await foreach(var _ in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"link/sub",new(true,false),default))
                    throw new InvalidDataException("A scoped listing followed an intermediate link");
                throw new InvalidDataException("Linked range reported empty coverage");
            }
            catch(InventoryIncompleteException) { }
            finally { Directory.Delete(link); }
            try { await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(other,expected,paths,"",new(true,false),default)) throw new InvalidDataException("Read from a different root"); }
            catch(ChangedUnderneathException) { return; } catch(InventoryIncompleteException) { return; }
            throw new InvalidDataException("Replaced root supplied empty coverage");
        }
        finally { dir.Delete(true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
}
