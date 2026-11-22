using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class RecycleBinChecks
{
    public static async Task RunAsync()
    {
        Check.Require(RecycleBin.BinName("first.txt",false,1)=="first.txt" && RecycleBin.BinName("first.txt",false,2)=="first (2).txt" &&
            RecycleBin.BinName("group.a",true,3)=="group.a (3)","bin collisions preserve both items");
        var untouched = new Entry("file",false,0,DateTimeOffset.UnixEpoch,"1","","",null);
        try { await new RecycleBin().RemoveAsync("invalid\0root",DriveType.Removable,untouched,null,default);
            throw new InvalidDataException("removal accepted no exact victim"); }
        catch (ChangedUnderneathException) { }
        var errors=new List<string>();
        foreach(var check in new Func<Task>[] {HydratedSelectionAsync,ManagedChangeAsync})
        {
            try { await check(); } catch(InvalidDataException e) { errors.Add(e.Message); }
        }
        Check.Require(errors.Count==0,string.Join("; ",errors));
        await SubstituteVersionAsync();
        await SelectedCustodyAsync();
        // Non-removable drives must not touch even a similarly named directory.
        await new RecycleBin().TrimAsync("invalid\0root",DriveType.Fixed,DateTimeOffset.UnixEpoch,default);
    }
    // Drive the actual shell veto and receipt boundaries, but never submit a shell delete.
    private static async Task HydratedSelectionAsync()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path,[1,2]); var stamp=File.GetLastWriteTimeUtc(path);
            using var metadata=OpenMetadata(path,0x80,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x00300000,IntPtr.Zero);
            File.SetAttributes(path,FileAttributes.Offline);
            Check.Require(!metadata.IsInvalid && File.GetAttributes(path).HasFlag(FileAttributes.Offline),"offline metadata fixture was not established");
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
            var selected=typeof(LocalSide).GetMethod("Info",flags)!.Invoke(null,[metadata])!;
            var id=new byte[24]; Check.Require(GetFileInformationByHandleEx(metadata,18,id,24),"fixture identity unavailable");
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            var entry=new Entry("stub",false,2,stamp,BitConverter.ToInt64(id,8).ToString(culture),"","",nameof(TextKey.SkipOnlineOnly));
            var proof=new RemovalProof(entry,BitConverter.ToUInt64(id).ToString(culture),null);
            // Same identity, size and stamp, but it is now a locally edited readable version.
            File.SetAttributes(path,FileAttributes.Normal); File.WriteAllBytes(path,[3,4]); File.SetLastWriteTimeUtc(path,stamp);
            // Permit attributes-only opens but deny data readers during both checks.
            using var denyDataRead=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.Write|FileShare.Delete);
            var sinkType=typeof(RecycleBin).GetNestedType("DeleteSink",System.Reflection.BindingFlags.NonPublic)!;
            var sink=Activator.CreateInstance(sinkType,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,[path,selected,entry],null)!;
            var callback=sinkType.GetInterfaces().Single(i=>i.Name=="IFileOperationProgressSink").GetMethod("PreDeleteItem")!;
            var veto=(int)callback.Invoke(sink,[0x80u,IntPtr.Zero])!;
            denyDataRead.Dispose(); // A readable changed receipt must now prove the actual recoverable bytes.
            var read=typeof(RecycleBin).GetMethod("ObserveReceipt",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            var receipt=await (Task<RemovalResult>)read.Invoke(new RecycleBin(),[path,selected,proof,CancellationToken.None])!;
            using var digest=new ContentHash(); digest.Append(new byte[]{3,4});
            Check.Require(veto<0 && Equals(sinkType.GetProperty("VetoCause")!.GetValue(sink),TextKey.FailChanged) && receipt.Outcome==RemovalOutcome.Retained &&
                receipt.RecycledVictim is {Entry.Skip:null} actual && actual.NativePath==path && actual.Entry.Id==entry.Id && actual.Entry.ContentHash==digest.Finish(),
                $"hydrated same-ID selection: veto={veto}, outcome={receipt.Outcome}, actual digest={receipt.RecycledVictim?.Entry.ContentHash}");
        }
        finally {File.Delete(path);}
    }
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="CreateFileW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenMetadata(string path,uint access,FileShare share,IntPtr security,uint disposition,uint flags,IntPtr template);
    private sealed class ArmStop : Exception { }
    // The callback is the real arm boundary. Always stop there: this test must never ask the shell to recycle.
    private static async Task SelectedCustodyAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        var full=Path.Combine(root,"selected");
        try
        {
            byte[] data=[4,8]; File.WriteAllBytes(full,data); File.SetAttributes(full,FileAttributes.Normal);
            var stamp=File.GetLastWriteTimeUtc(full); var id=new byte[24];
            using(var named=File.OpenHandle(full,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                Check.Require(GetFileInformationByHandleEx(named,18,id,24),"missing fixture identity");
            var culture=System.Globalization.CultureInfo.InvariantCulture; using var digest=new ContentHash(); digest.Append(data);
            var item=new Entry("selected",false,data.Length,stamp,BitConverter.ToInt64(id,8).ToString(culture),"",digest.Finish(),null);
            var victim=new RemovalSelection(new(item,BitConverter.ToUInt64(id).ToString(culture),item.ContentHash),null);
            bool denied=false;
            Task Arm()
            {
                try { using var writer=File.OpenHandle(full,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete); }
                catch(IOException e) when((e.HResult&0xffff)==32) { denied=true; }
                throw new ArmStop();
            }
            // Construct the production removal owner with its existing arm delegate; no product test hook.
            var bin=(RecycleBin)Activator.CreateInstance(typeof(RecycleBin),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,
                null,[(Func<Task>)Arm,null],null)!;
            try { await bin.RemoveAsync(root,DriveType.Fixed,item,victim,default); throw new InvalidDataException("arm was bypassed"); }
            catch(ArmStop) { }
            Check.Require(denied && File.ReadAllBytes(full).SequenceEqual(data),"selected file admitted a writer after verifying bytes at the pre-shell arm");
        }
        finally {Directory.Delete(root,true);}
    }
    private static Task ManagedChangeAsync()=>ManagedChangeAsync(false);
    internal static async Task ManagedChangeAsync(bool offline)
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        var drive=Enumerable.Range('Q',10).Reverse().Select(c=>((char)c)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));
        try
        {
            Subst(drive,root);
            try
            {
                var volume=drive+Path.DirectorySeparatorChar; var name=Guid.NewGuid().ToString("N");
                var full=Path.Combine(root,name); var alias=Path.Combine(volume,name);
                byte[] data=[5,8]; File.WriteAllBytes(full,data); Check.Require(File.ReadAllBytes(alias).SequenceEqual(data),"volume alias does not name the fixture");
                File.SetAttributes(full,offline?FileAttributes.Offline:FileAttributes.Normal); var stamp=File.GetLastWriteTimeUtc(full);
                var id=new byte[24]; using(var handle=OpenMetadata(full,0x80,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x00300000,IntPtr.Zero))
                    Check.Require(GetFileInformationByHandleEx(handle,18,id,24),"fixture identity unavailable");
                var culture=System.Globalization.CultureInfo.InvariantCulture; using var hash=new ContentHash(); hash.Append(data);
                var expected=new Entry(name,false,data.Length,stamp,BitConverter.ToInt64(id,8).ToString(culture),"",offline?"":hash.Finish(),offline?nameof(TextKey.SkipOnlineOnly):null);
                var selection=new RemovalSelection(new(expected,BitConverter.ToUInt64(id).ToString(culture),offline?null:expected.ContentHash),null);
                bool armed=false;
                Task Arm(){File.SetLastWriteTimeUtc(alias,stamp.AddMinutes(1)); armed=true; return Task.CompletedTask;}
                var bin=(RecycleBin)Activator.CreateInstance(typeof(RecycleBin),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,
                    null,[(Func<Task>)Arm,null],null)!;
                var result=await bin.RemoveAsync(volume,DriveType.Removable,expected,selection,default);
                Check.Require(armed && result.Outcome==RemovalOutcome.NoEffect && result.Cause==TextKey.FailChanged && File.Exists(full) &&
                    !File.Exists(Path.Combine(root,ResourceNames.RootBinFolder,name)),"managed rename moved a selection whose metadata changed at the arm");
            }
            finally {Subst(drive,"/D");}
        }
        finally {Directory.Delete(root,true);}
    }
    private static async Task SubstituteVersionAsync()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path,[11]);
            var before=DateTime.UnixEpoch.AddYears(35); File.SetLastWriteTimeUtc(path,before);
            using var metadata=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var id=new byte[24];
            Check.Require(GetFileInformationByHandleEx(metadata,18,id,(uint)id.Length),"missing NTFS fixture identity");
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            var first=new IdentityLocation(BitConverter.ToUInt64(id).ToString(culture),path,
                new Entry("victim",false,1,before,BitConverter.ToInt64(id,8).ToString(culture),"","",null));
            // The shell has identified the object, but another open can still edit it before custody of its bytes.
            byte[] newest=[22,33,44]; var after=before.AddDays(1);
            File.WriteAllBytes(path,newest); File.SetAttributes(path,FileAttributes.Normal); File.SetLastWriteTimeUtc(path,after);
            var receipt=await RecycleBin.ReadSubstituteReceiptAsync(first,metadata,default);
            using var digest=new ContentHash(); digest.Append(newest);
            Check.Require(receipt.Outcome==RemovalOutcome.Retained && receipt.RecycledVictim==
                (first with {Entry=first.Entry with {Size=newest.Length,Modified=after,ContentHash=digest.Finish()}}),
                "substitute receipt mixed earlier metadata with the later content digest");
        }
        finally { File.Delete(path); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h,int kind,
        [System.Runtime.InteropServices.Out] byte[] data,uint bytes);
    public static async Task TrimContinuesAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        var drive=Enumerable.Range('Q',10).Reverse().Select(c=>((char)c)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));
        try
        {
            Subst(drive,root);
            try
            {
                var bin=Directory.CreateDirectory(Path.Combine(root,ResourceNames.RootBinFolder)).FullName;
                // Never run a removable-bin operation until its synthetic volume root is proven to be ours.
                var marker=Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(root,marker),marker);
                Check.Require(File.ReadAllText(Path.Combine(drive+Path.DirectorySeparatorChar,marker))==marker,"drive is not the fixture");
                var first=Path.Combine(bin,"a"); var later=Path.Combine(bin,"z");
                File.WriteAllText(first,"held"); File.WriteAllText(later,"expired");
                File.SetCreationTimeUtc(first,DateTime.UtcNow.AddDays(-40)); File.SetCreationTimeUtc(later,DateTime.UtcNow.AddDays(-40));
                using var handle=File.Open(first,FileMode.Open,FileAccess.Read,FileShare.None);
                bool reported=false;
                try { await new RecycleBin().TrimAsync(drive+Path.DirectorySeparatorChar,DriveType.Removable,DateTimeOffset.UtcNow,default); }
                catch(ItemFailedException error) { reported=PathNames.Same(error.Path,Path.Combine(drive+Path.DirectorySeparatorChar,ResourceNames.RootBinFolder,"a")) && error.Cause==TextKey.FailLocalInUse; }
                catch(IOException) { /* Old outcome discarded the notice path and typed cause. */ }
                Check.Require(File.Exists(first) && !File.Exists(later) && reported,"failed trim item prevented sibling cleanup or failure reporting");
            }
            finally { Subst(drive,"/D"); }
        }
        finally { Directory.Delete(root,true); }
    }
    private static void Subst(string drive,string argument)
    {
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"subst.exe")){UseShellExecute=false};
        start.ArgumentList.Add(drive); start.ArgumentList.Add(argument);
        using var command=System.Diagnostics.Process.Start(start)!; command.WaitForExit();
        Check.Require(command.ExitCode==0,"could not prepare or release synthetic volume");
    }
}
