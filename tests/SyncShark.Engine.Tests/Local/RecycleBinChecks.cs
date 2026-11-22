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
        foreach(var check in new Func<Task>[] {WholePutBackAsync,HydratedSelectionAsync,ManagedChangeAsync,()=>ManagedChangeAsync(false,true),ReadableSelectionAsync})
        {
            try { await check(); } catch(InvalidDataException e) { errors.Add(e.Message); }
        }
        Check.Require(errors.Count==0,string.Join("; ",errors));
        await SubstituteVersionAsync();
        await SelectedCustodyAsync();
        // Non-removable drives must not touch even a similarly named directory.
        await new RecycleBin().TrimAsync("invalid\0root",DriveType.Fixed,DateTimeOffset.UnixEpoch,default);
    }
    private static async Task WholePutBackAsync()
    {
        var home=Directory.CreateTempSubdirectory().FullName;
        var drive=Enumerable.Range('Q',10).Reverse().Select(c=>((char)c)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));
        try
        {
            Subst(drive,home);
            try
            {
                var root=Directory.CreateDirectory(Path.Combine(home,"pair")).FullName;
                var bin=Directory.CreateDirectory(Path.Combine(home,ResourceNames.RootBinFolder)).FullName;
                var src=Directory.CreateDirectory(Path.Combine(bin,"saved")).FullName;
                var child=Path.Combine("nested","child"); Directory.CreateDirectory(Path.Combine(src,"nested")); File.WriteAllBytes(Path.Combine(src,child),[7,9]);
                var dest=Directory.CreateDirectory(Path.Combine(root,"original")).FullName; File.WriteAllText(Path.Combine(dest,"occupant"),"keep");
                var source=Path.Combine(drive+Path.DirectorySeparatorChar,ResourceNames.RootBinFolder,"saved"); var rootAlias=Path.Combine(drive+Path.DirectorySeparatorChar,"pair");
                var identity=Identity(source); var bound=Identity(rootAlias); var culture=System.Globalization.CultureInfo.InvariantCulture;
                var entry=new Entry("original",true,0,default,unchecked((long)identity.FileId).ToString(culture),"","",null);
                var actual=new IdentityLocation(identity.VolumeSerialNumber.ToString(culture),source,entry);
                var restore=typeof(RecycleBin).GetMethod("RestoreRecycledAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
                Task<RemovalResult> Restore()=>(Task<RemovalResult>)restore.Invoke(null,[actual,entry,"original",rootAlias,bound,CancellationToken.None])!;
                var failures=new List<string>();
                try { var occupied=await Restore(); if(occupied.Outcome==RemovalOutcome.Published) failures.Add("occupied folder claimed restored"); }
                catch(PathOccupiedException) { }
                Check.Require(File.ReadAllText(Path.Combine(dest,"occupant"))=="keep" && File.ReadAllBytes(Path.Combine(src,child)).SequenceEqual(new byte[]{7,9}),"occupied restore damaged a version");
                Directory.Move(dest,dest+".away");
                var free=await Restore();
                if(free.Outcome!=RemovalOutcome.Published || free.Final?.Id!=entry.Id || !File.Exists(Path.Combine(dest,child)) || Directory.Exists(src) || Identity(dest)!=identity || !File.ReadAllBytes(Path.Combine(dest,child)).SequenceEqual(new byte[]{7,9})) failures.Add("nonempty binned folder was not moved whole to free name");
                Check.Require(failures.Count==0,string.Join("; ",failures));
            }
            finally {Subst(drive,"/D");}
        }
        finally {Directory.Delete(home,true);}
    }
    private static DirectoryIdentity Identity(string full)
    {
        using var handle=OpenMetadata(full,0x80,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x02300000,IntPtr.Zero);
        var id=new byte[24];Check.Require(!handle.IsInvalid && GetFileInformationByHandleEx(handle,18,id,24),"native fixture identity unavailable");
        return new(BitConverter.ToUInt64(id),BitConverter.ToUInt64(id,8),BitConverter.ToUInt64(id,16));
    }
    // Drive the actual shell veto and receipt boundaries, but never submit a shell delete.
    private static Task HydratedSelectionAsync()=>HydratedSelectionAsync(false);
    private static Task ReadableSelectionAsync()=>HydratedSelectionAsync(false,true);
    internal static async Task HydratedSelectionAsync(bool checkOffline,bool readable=false)
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path,[1,2]); var stamp=File.GetLastWriteTimeUtc(path);
            using var metadata=OpenMetadata(path,0x80,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x00300000,IntPtr.Zero);
            File.SetAttributes(path,readable?FileAttributes.Normal:FileAttributes.Offline);
            Check.Require(!metadata.IsInvalid && (readable || File.GetAttributes(path).HasFlag(FileAttributes.Offline)),"offline metadata fixture was not established");
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
            var selected=typeof(LocalSide).GetMethod("Info",flags)!.Invoke(null,[metadata])!;
            var id=new byte[24]; Check.Require(GetFileInformationByHandleEx(metadata,18,id,24),"fixture identity unavailable");
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            using var originalHash=new ContentHash(); originalHash.Append(new byte[]{1,2});
            var entry=new Entry("stub",false,2,stamp,BitConverter.ToInt64(id,8).ToString(culture),"",readable?originalHash.Finish():"",readable?null:nameof(TextKey.SkipOnlineOnly));
            var proof=new RemovalProof(entry,BitConverter.ToUInt64(id).ToString(culture),readable?entry.ContentHash:null);
            var sinkType=typeof(RecycleBin).GetNestedType("DeleteSink",System.Reflection.BindingFlags.NonPublic)!;
            var sink=Activator.CreateInstance(sinkType,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,[path,selected,entry],null)!;
            var callback=sinkType.GetInterfaces().Single(i=>i.Name=="IFileOperationProgressSink").GetMethod("PreDeleteItem")!;
            var read=typeof(RecycleBin).GetMethod("ObserveReceipt",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            if(checkOffline)
            {
                using var deny=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.Write|FileShare.Delete);
                var approved=(int)callback.Invoke(sink,[0x80u,IntPtr.Zero])!;
                var same=await (Task<RemovalResult>)read.Invoke(new RecycleBin(),[path,selected,proof,CancellationToken.None])!;
                Check.Require(approved==0 && same.Outcome==RemovalOutcome.Removed && same.RecycledVictim?.Entry.Skip==entry.Skip,"unchanged stub was not accepted on metadata alone");
            }
            // Same identity, size and stamp, but now a locally edited readable version.
            File.SetAttributes(path,FileAttributes.Normal); File.WriteAllBytes(path,[3,4]); File.SetLastWriteTimeUtc(path,readable?stamp.AddMinutes(1):stamp);
            int veto;
            using(var deny=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.Write|FileShare.Delete))
                veto=(int)callback.Invoke(sink,[0x80u,IntPtr.Zero])!;
            var receipt=await (Task<RemovalResult>)read.Invoke(new RecycleBin(),[path,selected,proof,CancellationToken.None])!;
            using var digest=new ContentHash(); digest.Append(new byte[]{3,4});
            Check.Require(veto<0 && Equals(sinkType.GetProperty("VetoCause")!.GetValue(sink),TextKey.FailChanged) && receipt.Outcome==RemovalOutcome.Retained &&
                receipt.RecycledVictim is {Entry.Skip:null} actual && actual.NativePath==path && actual.Entry.Id==entry.Id && actual.Entry.ContentHash==digest.Finish(),
                $"hydrated same-ID selection: veto={veto}, outcome={receipt.Outcome}, actual digest={receipt.RecycledVictim?.Entry.ContentHash}");
            var pair=new PairConfig("pair","Pair",Path.GetDirectoryName(path)!,"/root","account",[],[],FirstRunPolicy.NewestWins,true,null);
            var side=LocalSide.Factory()(pair,()=>new PathScope(pair,[pair],DriveType.Fixed),SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
            var found=receipt.RecycledVictim!;
            var recovered=await side.RecoverAsync(found,found.Entry,default) as Recovery.File;
            if(recovered is null) throw new InvalidDataException("identified hydrated version was not recoverable");
            await using var recoveredStream=recovered.Bytes; using var copy=new MemoryStream(); await recoveredStream.CopyToAsync(copy);
            Check.Require(copy.ToArray().SequenceEqual(new byte[]{3,4}),"recovery returned a different version");
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
    internal static async Task ManagedChangeAsync(bool offline,bool attemptRename=false)
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
                bool denied=false;
                Task Arm()
                {
                    if(attemptRename)
                    {
                        try { File.Move(full,full+".later"); File.WriteAllText(full,"other"); }
                        catch(IOException e) when((e.HResult&0xffff)==32) { denied=true; }
                    }
                    else File.SetLastWriteTimeUtc(alias,stamp.AddMinutes(1));
                    armed=true; return Task.CompletedTask;
                }
                var bin=(RecycleBin)Activator.CreateInstance(typeof(RecycleBin),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,
                    null,[(Func<Task>)Arm,null],null)!;
                var result=await bin.RemoveAsync(volume,DriveType.Removable,expected,selection,default);
                if(attemptRename)
                {
                    Check.Require(armed && denied && result.Outcome==RemovalOutcome.Removed && !File.Exists(full) &&
                        File.ReadAllBytes(Path.Combine(root,ResourceNames.RootBinFolder,name)).SequenceEqual(data),"managed recycle took a user-renamed object or refused its unchanged source");
                    return;
                }
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
