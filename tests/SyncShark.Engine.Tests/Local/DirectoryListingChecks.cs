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
            await CheckDiscardedRowAsync(pair,expected);
            try { await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(other,expected,paths,"",new(true,false),default)) throw new InvalidDataException("Read from a different root"); }
            catch(ChangedUnderneathException e) when(e.Path==other) { return; }
            catch(InventoryIncompleteException e) { throw new InvalidDataException("Replaced configured root was reported as an unread child range",e); }
            throw new InvalidDataException("Replaced root supplied empty coverage");
        }
        finally { dir.Delete(true); }
    }
    // The base listing fixture the independent guards share: a selected root, a replacement root holding an occupant, and the pair scope.
    private static DirectoryInfo PairFixture(out string root,out string other,out DirectoryIdentity expected,out PairConfig pair,out PathScope paths)
    {
        var dir=Directory.CreateTempSubdirectory();
        root=dir.CreateSubdirectory("selected").FullName;
        other=dir.CreateSubdirectory("replacement").FullName;
        File.WriteAllText(Path.Combine(other,"occupant"),"other");
        expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;
        pair=Scenarios.ItemChecks.Pair() with { LocalRoot=root, Exclusions=[new(Side.Local,"excluded",true)] };
        paths=new PathScope(pair,[pair],DriveType.Fixed);
        return dir;
    }
    // A folder lost during the full walk is reported unreadable, not empty.
    public static async Task LostFolderAsync()
    {
        var dir=PairFixture(out var root,out _,out var expected,out _,out var paths);
        try
        {
            var vanishing=Directory.CreateDirectory(Path.Combine(root,"vanishing")); var unreadable=false;
            await foreach (var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default))
            {
                if (row.Path!="vanishing") continue;
                if (row.Entry?.Skip==TextKey.SkipUnreadable.ToString()) unreadable=true; else vanishing.Delete();
            }
            Check.Require(unreadable,"A folder lost during the full walk was treated as empty");
        }
        finally { dir.Delete(true); }
    }
    // A descendant replaced after its page was read is not reported as an empty (or substituted) subtree.
    public static async Task SubstituteAsync()
    {
        var dir=PairFixture(out var root,out var other,out var expected,out _,out var paths);
        try
        {
            var scope=Directory.CreateDirectory(Path.Combine(root,"ordinary"));
            var selected=scope.CreateSubdirectory("child"); var sibling=scope.CreateSubdirectory("healthy");
            File.WriteAllText(Path.Combine(sibling.FullName,"kept"),"healthy"); bool withheld=false, healthy=false;
            await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,scope.Name,new(true,false),default))
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
        finally { dir.Delete(true); }
    }
    // A scoped listing must not follow an intermediate link.
    public static async Task IntermediateLinkAsync()
    {
        var dir=PairFixture(out var root,out var other,out var expected,out _,out var paths);
        try
        {
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
        }
        finally { dir.Delete(true); }
    }
    // The first row proves authentication and a successful native read. Removing only this image makes a later page fail.
    public static async Task OfflineRootAsync()
    {
        var hosted = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted";
        if (!hosted || !OperatingSystem.IsWindows())
            throw Unavailable("Disposable virtual disk requires a GitHub-hosted Windows runner");
        var temp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (string.IsNullOrEmpty(temp) || !Path.IsPathFullyQualified(temp)) throw new InvalidDataException("Hosted temporary directory unavailable");
        var work = Directory.CreateDirectory(Path.Combine(temp, Guid.NewGuid().ToString("N")));
        var image = Path.Combine(work.FullName,"fixture.vhdx");
        var definition = Path.Combine(work.FullName,"create.txt");
        Exception? primary = null;
        try
        {
            await DiskScript("""
                $admin = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
                if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { 'Runner is not a virtual-disk administrator'; exit 77 }
                foreach ($cmd in 'diskpart.exe','Mount-DiskImage','Get-DiskImage','Dismount-DiskImage','Get-Disk','Initialize-Disk','New-Partition','Format-Volume') {
                    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { "Missing virtual-disk command: $cmd"; exit 77 }
                }
                Get-CimClass -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_DiskImage | Out-Null
                """, capabilityCheck:true);
            File.WriteAllText(definition,$"create vdisk file=\"{image}\" maximum=64 type=expandable");
            var letter = await DiskScript("""
                $trace = & diskpart.exe /s $env:FIXTURE_DEFINITION
                if ($LASTEXITCODE) { throw ($trace -join ' ') }
                $img = Mount-DiskImage -ImagePath $env:FIXTURE_IMAGE -NoDriveLetter -PassThru
                $disk = @($img | Get-Disk)
                if ($disk.Count -ne 1 -or $disk[0].IsBoot -or $disk[0].IsSystem -or $disk[0].PartitionStyle -ne 'RAW' -or $disk[0].Size -ne 64MB) { throw 'Unexpected fixture disk' }
                $p = $disk[0] | Initialize-Disk -PartitionStyle MBR -PassThru | New-Partition -UseMaximumSize -AssignDriveLetter
                $p | Format-Volume -FileSystem NTFS -Confirm:$false -Force | Out-Null
                $p.DriveLetter
                """);
            if (letter.Length!=1 || !char.IsAsciiLetter(letter[0])) throw new InvalidDataException("Fixture volume did not receive a drive letter");
            var root=Directory.CreateDirectory(Path.Combine(letter+Path.VolumeSeparatorChar+Path.DirectorySeparatorChar,"selected")).FullName;
            // More than one 64 KiB native page (each name alone exceeds 300 bytes).
            for (var n=0;n<256;n++) File.WriteAllBytes(Path.Combine(root,new string('x',160)+n),[]);
            var expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;
            var pair=Scenarios.ItemChecks.Pair() with {LocalRoot=root}; var paths=new PathScope(pair,[pair],DriveType.Fixed);
            await using var rows=global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default).GetAsyncEnumerator();
            Check.Require(await rows.MoveNextAsync() && rows.Current.Entry is {IsFolder:false},"Root must yield a real file before detach");
            // The image's volume root is distinct from the selected pair root; the deny rule must not inherit.
            var volume = new DirectoryInfo(Path.GetPathRoot(root)!);
            var access = volume.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            var original = access.GetSecurityDescriptorBinaryForm();
            using var principal = System.Security.Principal.WindowsIdentity.GetCurrent();
            var fileId = unchecked((ulong)long.Parse(rows.Current.Entry!.Id,System.Globalization.CultureInfo.InvariantCulture));
            var before = global::Massdex.SyncShark.Engine.Local.IdentityPaths.ResolveLinksById(root,expected,fileId,false,default);
            Check.Require(before.Path!=null,"Selected file ID must resolve before the volume fault");
            access.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(principal.User!,System.Security.AccessControl.FileSystemRights.ReadAttributes,System.Security.AccessControl.AccessControlType.Deny));
            try
            {
                volume.SetAccessControl(access);
                Check.Require(global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity==expected,"Volume-only deny rule affected the pair root");
                try
                {
                    global::Massdex.SyncShark.Engine.Local.IdentityPaths.ResolveLinksById(root,expected,fileId,false,default);
                    throw new InvalidDataException("By-ID read passed without volume-domain evidence");
                }
                catch(ChangedUnderneathException e) when(e.Path==root && e.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode:5 }) { }
                catch(System.ComponentModel.Win32Exception e) when(e.NativeErrorCode==5)
                { throw new InvalidDataException("Volume-domain access denied escaped as item I/O after pair-root authentication",e); }
            }
            finally
            {
                access.SetSecurityDescriptorBinaryForm(original,System.Security.AccessControl.AccessControlSections.Access);
                volume.SetAccessControl(access);
            }
            await DiskScript("Dismount-DiskImage -ImagePath $env:FIXTURE_IMAGE -ErrorAction Stop");
            try
            {
                while (await rows.MoveNextAsync()) { }
                throw new InvalidDataException("Detached root reported complete coverage");
            }
            catch(ChangedUnderneathException e) when(e.Path==root && e.InnerException is System.ComponentModel.Win32Exception cause)
            { Check.Require(cause.NativeErrorCode==21,$"Detached root must preserve Win32 error 21, got {cause.NativeErrorCode}"); }
            catch(InventoryIncompleteException e) when(e.InnerException is System.ComponentModel.Win32Exception)
            { throw new InvalidDataException("Native configured-root enumeration after detach was reported as an unread range",e); }
        }
        catch (Exception error) { primary=error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            try
            {
                if (File.Exists(image))
                {
                    await DiskScript("""
                        $img = @(Get-DiskImage -ImagePath $env:FIXTURE_IMAGE)
                        if ($img.Count -eq 1 -and $img[0].Attached -eq $true) {
                            Dismount-DiskImage -ImagePath $env:FIXTURE_IMAGE
                            $img = @(Get-DiskImage -ImagePath $env:FIXTURE_IMAGE)
                        }
                        if ($img.Count -ne 1 -or $img[0].Attached -ne $false) { throw 'Fixture image detachment not confirmed' }
                        """);
                    File.Delete(image);
                }
            }
            catch (Exception error) { cleanup.Add(error); }
            try { File.Delete(definition); } catch (Exception error) { cleanup.Add(error); }
            try { if (!Directory.EnumerateFileSystemEntries(work.FullName).Any()) work.Delete(); }
            catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
            {
                if (primary != null) cleanup.Insert(0,primary);
                throw new AggregateException("Hosted fixture cleanup was incomplete",cleanup);
            }
        }

        Console.WriteLine("PASS Local/DirectoryListingChecks/HostedDetach");

        async Task<string> DiskScript(string body,bool capabilityCheck=false)
        {
            var shell = Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
            if(capabilityCheck && !File.Exists(shell)) throw Unavailable("Windows PowerShell unavailable");
            var start=new System.Diagnostics.ProcessStartInfo(shell) {UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true};
            start.Environment["FIXTURE_IMAGE"]=image; start.Environment["FIXTURE_DEFINITION"]=definition;
            // Only documented capability failures become a skip; operational and cleanup errors stay failures.
            if (capabilityCheck) body = "try { " + body + " } catch { if ($_.CategoryInfo.Category -eq 'PermissionDenied' -or $_.Exception.StatusCode -in 'AccessDenied','InvalidNamespace','InvalidClass','NotSupported') { $_.Exception.Message; exit 77 }; throw }";
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; "+body}) start.ArgumentList.Add(arg);
            using var process=System.Diagnostics.Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            var output=(await stdout).Trim(); var error=(await stderr).Trim();
            if(capabilityCheck && process.ExitCode==77) throw Unavailable(output);
            if(process.ExitCode!=0) throw new InvalidDataException(error);
            return output;
        }
        Exception Unavailable(string reason)
        {
            Console.WriteLine("NOT IMPLEMENTED: " + reason);
            return hosted ? new InvalidDataException("Hosted detach proof missing: " + reason) : new NotImplementedException(reason);
        }
    }
    private static async Task CheckDiscardedRowAsync(PairConfig pair, DirectoryIdentity expected)
    {
        var filtered = pair with { Exclusions = [new(Side.Local,"times/bad-time",true)] };
        var paths = new PathScope(filtered,[filtered],DriveType.Fixed);
        var conversions = 0;
        var thread = Environment.CurrentManagedThreadId;
        void CountBadTime(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId==thread && args.Exception is ArgumentOutOfRangeException { ParamName:"fileTime" }) ++conversions;
        }
        AppDomain.CurrentDomain.FirstChanceException += CountBadTime;
        try
        {
            foreach (var include in new[]{true,false})
            {
                conversions=0;
                bool sawBad=false, sawHealthy=false;
                await foreach (var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(pair.LocalRoot,expected,paths,"times",new(true,include),default))
                {
                    sawBad |= row is {Path:"times/bad-time",Entry:{Skip:nameof(TextKey.SkipUnreadable)}};
                    sawHealthy |= row is {Path:"times/healthy",Entry:{Skip:null}};
                }
                Check.Require(sawHealthy && sawBad==include,"Filtering lost the healthy row or leaked the excluded file");
                // An included invalid-time row converts exactly twice (the file's own time, then the listing fallback);
                // a discarded row is never classified, so it converts zero times.
                Check.Require(include ? conversions==2 : conversions==0,include ? "Included invalid time did not convert exactly twice" : "Classified a discarded row before scope filtering");
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= CountBadTime; }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling=true, SetLastError=true)]
    private static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr created, IntPtr accessed, ref long modified);

    // An invalid cached row time must not veto the file's valid own time (the walk classifies the file time first).
    public static async Task InvalidCachedTimeAsync()
    {
        var first=Directory.CreateTempSubdirectory();
        try
        {
            var root=first.FullName; var a=Path.Combine(root,"a"); var b=Path.Combine(root,"b");
            File.WriteAllText(a,"v1");
            if(!CreateHardLinkW(b,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var invalid=DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc).ToFileTimeUtc()+1;
            var t=new DateTime(2021,6,15,1,2,3,DateTimeKind.Utc).ToFileTimeUtc();
            WriteTime(b,invalid);
            WriteTime(a,t);
            // Qualify the fixture through the writer alias without opening b: b's cached row time must be unrepresentable (arming the
            // row-time veto) and the file's own time must be the written valid value (a's refreshed row).
            bool rawInvalid;
            try { _=new DirectoryInfo(root).EnumerateFiles("b").Single().LastWriteTimeUtc; rawInvalid=false; }
            catch(ArgumentOutOfRangeException) { rawInvalid=true; }
            if(!rawInvalid) throw new NotImplementedException("b's cached row time is representable; the row-time veto cannot be armed");
            DateTime held;
            try { held=new DirectoryInfo(root).EnumerateFiles("a").Single().LastWriteTimeUtc; }
            catch(ArgumentOutOfRangeException) { throw new NotImplementedException("the file's written time is unrepresentable; the valid-file-time case cannot be armed"); }
            if(held!=DateTime.FromFileTimeUtc(t)) throw new NotImplementedException("the file's own time is not the written valid value; the case cannot be discriminated");
            var walk=await WalkOneAsync(root,"b");
            Check.Require(walk is {IsFolder:false,Skip:null} && walk.Modified.UtcDateTime==DateTime.FromFileTimeUtc(t),
                "an invalid cached row time vetoed the file's valid own time");
        }
        finally { first.Delete(true); }
    }
    // An unusable file time falls back to the listing's valid row time (the exact-name read classifies the file time first).
    public static async Task UnusableFileTimeAsync()
    {
        var second=Directory.CreateTempSubdirectory();
        try
        {
            var root=second.FullName; var a=Path.Combine(root,"a"); var b=Path.Combine(root,"b");
            File.WriteAllText(a,"v1");
            if(!CreateHardLinkW(b,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var invalid=DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc).ToFileTimeUtc()+1;
            WriteTime(a,invalid);
            // Qualify the fixture through the writer alias without opening b: the file's own time must be the written unrepresentable
            // value (a's refreshed row) while b's cached row must still be a representable time to fall back to.
            bool heldInvalid;
            try { _=new DirectoryInfo(root).EnumerateFiles("a").Single().LastWriteTimeUtc; heldInvalid=false; }
            catch(ArgumentOutOfRangeException) { heldInvalid=true; }
            if(!heldInvalid) throw new NotImplementedException("the file's written time is representable; the unusable-held-time case cannot be armed");
            DateTime r;
            try { r=new DirectoryInfo(root).EnumerateFiles("b").Single().LastWriteTimeUtc; }
            catch(ArgumentOutOfRangeException) { throw new NotImplementedException("b's cached row time is unrepresentable; not the listing-values case"); }
            var one=global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"b");
            Check.Require(one is {Skip:null} && one.Modified.UtcDateTime==r,
                "an unusable file time did not fall back to the valid listing values");
        }
        finally { second.Delete(true); }
    }
    private static void WriteTime(string path,long fileTime)
    {
        using var handle=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
        var value=fileTime;
        if(!SetFileTime(handle,IntPtr.Zero,IntPtr.Zero,ref value)) throw new System.ComponentModel.Win32Exception();
    }
    // Folders, links and other ineligible entries are never opened; only eligible regular files are.
    public static async Task IneligibleNoOpenAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var root=dir.FullName;
            var sub=Directory.CreateDirectory(Path.Combine(root,"sub"));
            File.WriteAllText(Path.Combine(sub.FullName,"inside"),"x");
            File.WriteAllText(Path.Combine(root,"plain"),"p");
            File.CreateSymbolicLink(Path.Combine(root,"file-link"),Path.Combine(root,"plain"));
            var expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;
            var pair=Scenarios.ItemChecks.Pair() with { LocalRoot=root };
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            int changes=0; var thread=Environment.CurrentManagedThreadId;
            void Saw(object? sender,System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
            { if(Environment.CurrentManagedThreadId==thread && e.Exception is ChangedUnderneathException) ++changes; }
            var rows=new List<Listed>();
            AppDomain.CurrentDomain.FirstChanceException+=Saw;
            try { await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default)) rows.Add(row); }
            finally { AppDomain.CurrentDomain.FirstChanceException-=Saw; }
            Check.Require(changes==0,"an ineligible folder, link or special entry was opened");
            Check.Require(rows.Any(r=>r is {Path:"sub",Entry:{IsFolder:true,Skip:null}}) && rows.Any(r=>r is {Path:"sub/inside",Entry:{Skip:null}}) &&
                rows.Any(r=>r is {Path:"file-link",Entry:{Skip:nameof(TextKey.SkipLink)}}) && rows.Any(r=>r is {Path:"plain",Entry:{IsFolder:false,Skip:null}}),
                "eligible or ineligible rows were misclassified after the per-file read");
        }
        finally { dir.Delete(true); }
    }
    // A name reported from an already-read page keeps its listed id and size when the object at that name changes after the yield.
    public static async Task PauseIdentityAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        var kept=Path.Combine(dir.Parent!.FullName,"kept-"+Guid.NewGuid().ToString("N"));
        try
        {
            var root=dir.FullName; var a=Path.Combine(root,"a"); var b=Path.Combine(root,"b");
            File.WriteAllText(a,"A"); File.WriteAllText(b,"BBBB");
            var old=global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"b") ?? throw new InvalidDataException("missing fixture b");
            var expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;
            var pair=Scenarios.ItemChecks.Pair() with { LocalRoot=root };
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            bool paused=false, pausedBeforeB=false; Entry? seen=null;
            await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default))
            {
                if(row.Path=="a")
                {
                    paused=true; File.Move(b,kept);
                    using var replacement=new FileStream(b,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
                    replacement.Write(new byte[(int)old.Size+50]);
                }
                else if(row.Path=="b") { seen=row.Entry; pausedBeforeB=paused; }
            }
            if(seen is null || !pausedBeforeB || seen.Id!=old.Id) throw new NotImplementedException("b was not decoded from the pre-pause page after a's yield");
            Check.Require(seen.Size==old.Size,$"a paused-name entry took the replacement size {seen.Size} instead of the listed {old.Size}");
        }
        finally { try { if(File.Exists(kept)) File.Delete(kept); } catch { } dir.Delete(true); }
    }
    // With the backup privilege enabled, the file open honours its backup intent and reads a file denied to ordinary metadata access.
    public static async Task BackupIntentAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        var root=dir.FullName;
        var a=Path.Combine(root,"a"); var b=Path.Combine(root,"b"); var control=Path.Combine(root,"control");
        try
        {
            File.WriteAllText(a,"v1");
            if(!CreateHardLinkW(b,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            if(!CreateHardLinkW(control,a,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            using(var stream=new FileStream(a,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)) stream.Write("abc"u8);
            var current=global::Massdex.SyncShark.Engine.Local.DirectoryListing.One(root,"a") ?? throw new InvalidDataException("missing fixture a");
            Exception? primary=null;
            byte[]? originalAcl=null;
            uint previousAttributes=0; var privilegeChanged=false;
            try
            {
                // The deny ACE lives on the shared file; apply and restore it through the control alias so b's cached row stays stale.
                originalAcl=TestDoubles.AclFixture.Deny(control,TestDoubles.AclFixture.MetadataAccess);
                if(!TestDoubles.AclFixture.EnablePrivilege("SeBackupPrivilege",out previousAttributes))
                    throw new NotImplementedException("SeBackupPrivilege could not be enabled; backup-intent evidence unavailable");
                privilegeChanged=true;
                // The deny must block an ordinary metadata open but yield to a backup-intent open of the control alias.
                if(!TestDoubles.AclFixture.Denied(control,backup:false)) throw new NotImplementedException("the deny ACE did not block an ordinary metadata open");
                if(!TestDoubles.AclFixture.Opens(control,backup:true)) throw new NotImplementedException("backup intent did not open the denied control alias");
                // Re-read b's cached size after the controls, by enumeration only, immediately before the walk observes it.
                if(new DirectoryInfo(root).EnumerateFiles("b").Single().Length==current.Size) throw new NotImplementedException("b's cached row already equals the current size; backup intent cannot be discriminated");
                var walk=await WalkOneAsync(root,"b");
                Check.Require(walk is {Skip:null} && walk.Size==current.Size && walk.Modified.UtcDateTime==current.Modified.UtcDateTime,
                    "backup intent did not read the denied file's current stamps");
            }
            catch(Exception error) { primary=error; }
            finally
            {
                // Restore what this check changed on every exit, including after a MISSING or a failed assertion. A failed restore is
                // reported (with the primary result) rather than swallowed, so a process left privileged cannot pass quietly.
                var cleanup=new List<Exception>();
                if(originalAcl!=null)
                {
                    try
                    {
                        TestDoubles.AclFixture.Restore(control,originalAcl);
                        if(!TestDoubles.AclFixture.Opens(control,backup:false)) throw new InvalidDataException("the deny ACE was not removed by restore");
                    }
                    catch(Exception error) { cleanup.Add(error); }
                }
                if(privilegeChanged)
                {
                    try { if(!TestDoubles.AclFixture.RestorePrivilege("SeBackupPrivilege",previousAttributes)) throw new InvalidDataException("the backup privilege was not restored"); }
                    catch(Exception error) { cleanup.Add(error); }
                }
                if(cleanup.Count!=0)
                {
                    if(primary!=null) cleanup.Insert(0,primary);
                    throw new AggregateException("backup-intent fixture cleanup was incomplete",cleanup);
                }
            }
            if(primary!=null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(primary);
        }
        finally { try { File.Delete(b); } catch { } try { File.Delete(control); } catch { } try { File.Delete(a); } catch { } dir.Delete(true); }
    }
    internal static async Task<Entry?> WalkOneAsync(string root,string name)
    {
        var expected=global::Massdex.SyncShark.Engine.Local.DriveIdentity.Read(root).Identity;
        var pair=Scenarios.ItemChecks.Pair() with { LocalRoot=root };
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        Entry? found=null;
        await foreach(var row in global::Massdex.SyncShark.Engine.Local.DirectoryListing.ReadAsync(root,expected,paths,"",new(true,false),default))
            if(row.Path==name) found=row.Entry;
        return found;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern bool CreateHardLinkW(string newName,string existing,IntPtr security);
}
