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
            var deniedRoot = dir.CreateSubdirectory("deny-list");
            var bound = global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(deniedRoot.FullName).Identity;
            File.WriteAllText(Path.Combine(deniedRoot.FullName,"included"),"fixture");
            var deniedPair = pair with {LocalRoot=deniedRoot.FullName};
            var deniedScope = new PathScope(deniedPair,[deniedPair],DriveType.Fixed);
            var acl = deniedRoot.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            var original = acl.GetSecurityDescriptorBinaryForm();
            using var principal = System.Security.Principal.WindowsIdentity.GetCurrent();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(principal.User!,System.Security.AccessControl.FileSystemRights.ListDirectory,System.Security.AccessControl.AccessControlType.Deny));
            try
            {
                deniedRoot.SetAccessControl(acl);
                Check.Require(global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(deniedRoot.FullName).Identity==bound,"Deny-list fixture also blocked metadata-only authentication");
                try
                {
                    await foreach(var _ in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(deniedRoot.FullName,bound,deniedScope,"",new(true,false),default))
                        throw new InvalidDataException("Deny-list fixture unexpectedly enumerated its first row");
                    throw new InvalidDataException("Deny-list fixture returned empty coverage");
                }
                catch(ChangedUnderneathException e) when(e.Path==deniedRoot.FullName && e.InnerException is System.ComponentModel.Win32Exception {NativeErrorCode:5} cause)
                {
                    // Distinguish this fault from the already-covered open failure: a wrong-phase fixture cannot pass.
                    Check.Require(cause.StackTrace?.Contains(".Enumerate",StringComparison.Ordinal)==true || cause.StackTrace?.Contains("<Enumerate>",StringComparison.Ordinal)==true,
                        "Deny-list fixture failed during root open, before native enumeration: "+cause.StackTrace);
                }
                catch(InventoryIncompleteException e) when(e.InnerException is System.ComponentModel.Win32Exception {NativeErrorCode:5})
                { throw new InvalidDataException("Native configured-root enumeration was classified as an unread range",e); }
            }
            finally
            {
                acl.SetSecurityDescriptorBinaryForm(original,System.Security.AccessControl.AccessControlSections.Access);
                deniedRoot.SetAccessControl(acl);
            }
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
            var times = Directory.CreateDirectory(Path.Combine(root,"times"));
            foreach (var name in new[]{"bad-time","healthy"}) File.WriteAllText(Path.Combine(times.FullName,name),name);
            // NTFS FILETIME can represent instants beyond DateTime.MaxValue. Do not corrupt a directory record.
            var invalidTime = DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc).ToFileTimeUtc() + 1;
            var badFile = Path.Combine(times.FullName,"bad-time");
            using (var handle = File.OpenHandle(badFile,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
                if (!SetFileTime(handle,IntPtr.Zero,IntPtr.Zero,ref invalidTime)) throw new System.ComponentModel.Win32Exception();
            var linkPath = Path.Combine(times.FullName,"file-link");
            File.CreateSymbolicLink(linkPath,Path.Combine(times.FullName,"healthy"));
            using (var handle = CreateFileW(linkPath,0x100,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x00200000,IntPtr.Zero)) // WRITE_ATTRIBUTES, OPEN_REPARSE_POINT
                if (handle.IsInvalid || !SetFileTime(handle,IntPtr.Zero,IntPtr.Zero,ref invalidTime)) throw new System.ComponentModel.Win32Exception();
            var observations = new List<Listed>();
            await foreach (var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"times",new(true,false),default)) observations.Add(row);
            Check.Require(observations.Any(r=>r is {Path:"times/file-link",Entry:{IsFolder:false,Skip:nameof(TextKey.SkipLink)}}) &&
                global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"times/file-link")?.Skip==nameof(TextKey.SkipLink),"Invalid timestamp displaced known file link protection");
            Check.Require(observations.Count==3 && observations.Count(r=>r is {Path:"times/bad-time", Entry:{IsFolder:false,Skip:nameof(TextKey.SkipUnreadable)}})==1 &&
                observations.Any(r=>r is {Path:"times/healthy",Entry:{IsFolder:false,Skip:null}}),"Invalid timestamp did not yield one unreadable file and its healthy sibling");
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
            catch(ChangedUnderneathException e) when(e.Path==other) { return; }
            catch(InventoryIncompleteException e) { throw new InvalidDataException("Replaced configured root was reported as an unread child range",e); }
            throw new InvalidDataException("Replaced root supplied empty coverage");
        }
        finally { dir.Delete(true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling=true, SetLastError=true)]
    private static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr created, IntPtr accessed, ref long modified);
}
