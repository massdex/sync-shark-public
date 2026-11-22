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
            catch(ChangedUnderneathException e) when(e.Path==other) { await OfflineRootAsync(); return; }
            catch(InventoryIncompleteException e) { throw new InvalidDataException("Replaced configured root was reported as an unread child range",e); }
            throw new InvalidDataException("Replaced root supplied empty coverage");
        }
        finally { dir.Delete(true); }
    }
    // The first row proves authentication and a successful native read. Removing only this image makes a later page fail.
    private static async Task OfflineRootAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new NotImplementedException("Disposable virtual disk requires a GitHub-hosted Windows runner");
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
                Get-Disk | Out-Null
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

        async Task<string> DiskScript(string body,bool capabilityCheck=false)
        {
            var shell = Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
            if(capabilityCheck && !File.Exists(shell)) throw Unavailable("Windows PowerShell unavailable");
            var start=new System.Diagnostics.ProcessStartInfo(shell) {UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true};
            start.Environment["FIXTURE_IMAGE"]=image; start.Environment["FIXTURE_DEFINITION"]=definition;
            // Only documented capability failures become a skip; operational and cleanup errors stay failures.
            var onError = capabilityCheck ? "if ($_.Exception.StatusCode -in 'InvalidNamespace','InvalidClass','NotSupported') { $_.Exception.Message; exit 77 }; " : "";
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-Command","$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; try { "+body+" } catch { "+onError+"if ($_.CategoryInfo.Category -eq 'PermissionDenied' -or $_.Exception.StatusCode -eq 'AccessDenied') { $_.Exception.Message; exit 77 }; throw }"}) start.ArgumentList.Add(arg);
            using var process=System.Diagnostics.Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            var output=(await stdout).Trim(); var error=(await stderr).Trim();
            if(process.ExitCode==77) throw Unavailable(output);
            if(process.ExitCode!=0) throw new InvalidDataException(error);
            return output;
        }
        static NotImplementedException Unavailable(string reason)
        {
            Console.WriteLine("NOT IMPLEMENTED: " + reason);
            return new NotImplementedException(reason);
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling=true, SetLastError=true)]
    private static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr created, IntPtr accessed, ref long modified);
}
