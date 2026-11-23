using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class RecycleBinChecks
{
    public static async Task RunAsync()
    {
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
        await LinkedBinRefusalAsync();
        await SubstituteVersionAsync(false);
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
                // A file restore must verify the complete selected version, not just the bin name and ID.
                var file=Path.Combine(bin,"saved-file"); File.WriteAllBytes(file,[6,8]);
                var fid=Identity(file); using var digest=new ContentHash(); digest.Append(new byte[]{6,8});
                var selectedFile=new Entry("file",false,2,File.GetLastWriteTimeUtc(file),unchecked((long)fid.FileId).ToString(culture),"",digest.Finish(),null);
                var fileReceipt=new IdentityLocation(actual.Domain,Path.Combine(drive+Path.DirectorySeparatorChar,ResourceNames.RootBinFolder,"saved-file"),selectedFile);
                Task<RemovalResult> RestoreFile(Entry wanted,string target)=>(Task<RemovalResult>)restore.Invoke(null,[fileReceipt,wanted,target,rootAlias,bound,CancellationToken.None])!;
                var oldHash=selectedFile with {ContentHash=new string('0',64)};
                var wrongDest=Path.Combine(root,"wrong");
                try { await RestoreFile(oldHash,"wrong"); failures.Add("wrong digest accepted for same-id size/stamp file"); }
                catch(ChangedUnderneathException e) when(e.Path=="wrong" || e.Path==selectedFile.Path) { }
                if(File.Exists(wrongDest) || !File.Exists(file) || Identity(file)!=fid || !File.ReadAllBytes(file).SequenceEqual(new byte[]{6,8}))
                    failures.Add("wrong-digest refusal did not preserve bin bytes and free target");
                // Independent source for the occupation assertion, even when the digest negative control moved the first source.
                var occupiedPath=Path.Combine(root,"occupied"); File.WriteAllBytes(occupiedPath,[9,7]); var occupantId=Identity(occupiedPath);
                if(!File.Exists(file) && File.Exists(wrongDest)) File.Move(wrongDest,file);
                if(File.Exists(file))
                {
                    try { await RestoreFile(selectedFile,"occupied"); failures.Add("occupied file restore did not refuse the live name"); }
                    catch(PathOccupiedException) { }
                    if(!File.Exists(occupiedPath) || Identity(occupiedPath)!=occupantId || !File.ReadAllBytes(occupiedPath).SequenceEqual(new byte[]{9,7}) ||
                        !File.Exists(file) || Identity(file)!=fid || !File.ReadAllBytes(file).SequenceEqual(new byte[]{6,8})) failures.Add("occupied file refusal did not preserve both exact versions");
                    if(File.Exists(file))
                    {
                        var fileResult=await RestoreFile(selectedFile,"file");
                        if(fileResult.Outcome!=RemovalOutcome.Published || fileResult.Final!=selectedFile || File.Exists(file) ||
                            !File.Exists(Path.Combine(root,"file")) || !File.ReadAllBytes(Path.Combine(root,"file")).SequenceEqual(new byte[]{6,8})) failures.Add("file restore did not preserve the exact version");
                    }
                }
                Check.Require(failures.Count==0,string.Join("; ",failures));
            }
            finally {Subst(drive,"/D");}
        }
        finally {Directory.Delete(home,true);}
    }
    private static async Task LinkedBinRefusalAsync()
    {
        var home=Directory.CreateTempSubdirectory(); var drive=Enumerable.Range('Q',10).Reverse().Select(c=>((char)c)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));
        try
        {
            Subst(drive,home.FullName);
            try
            {
                var root=drive+Path.DirectorySeparatorChar; var path=Path.Combine(home.FullName,"source");
                File.WriteAllBytes(path,[1,3]); var identity=Identity(path); var target=Directory.CreateDirectory(Path.Combine(home.FullName,"redirect")).FullName;
                File.WriteAllText(Path.Combine(target,"kept"),"original"); var link=Path.Combine(home.FullName,ResourceNames.RootBinFolder);
                Directory.CreateSymbolicLink(link,target);
                try
                {
                    var culture=System.Globalization.CultureInfo.InvariantCulture; using var hash=new ContentHash(); hash.Append(new byte[]{1,3}); var digest=hash.Finish();
                    var entry=new Entry("source",false,2,File.GetLastWriteTimeUtc(path),unchecked((long)identity.FileId).ToString(culture),"",digest,null);
                    bool armed=false; var bin=(RecycleBin)Activator.CreateInstance(typeof(RecycleBin),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,[(Func<Task>)(()=>{armed=true;return Task.CompletedTask;}),null],null)!;
                    RemovalResult? result=null;
                    try { result=await bin.RemoveAsync(root,DriveType.Removable,entry,new(new(entry,identity.VolumeSerialNumber.ToString(culture),digest),null),default); }
                    catch(ChangedUnderneathException) { /* A thrown change is not the required recoverable refusal. */ }
                    Check.Require(armed && result is {Outcome:RemovalOutcome.NoEffect,Cause:TextKey.RecycleUnavailable} && File.Exists(path) && Identity(path)==identity &&
                        File.ReadAllBytes(path).SequenceEqual(new byte[]{1,3}) && Directory.GetFileSystemEntries(target).Length==1 && File.ReadAllText(Path.Combine(target,"kept"))=="original",
                        "linked managed bin did not refuse without changing the source or redirect target");
                }
                finally { Directory.Delete(link); }
            }
            finally { Subst(drive,"/D"); }
        }
        finally { home.Delete(true); }
    }
    private static DirectoryIdentity Identity(string full)
    {
        using var handle=OpenMetadata(full,0x80,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x02300000,IntPtr.Zero);
        var id=new byte[24];Check.Require(!handle.IsInvalid && GetFileInformationByHandleEx(handle,18,id,24),"native fixture identity unavailable");
        return new(BitConverter.ToUInt64(id),BitConverter.ToUInt64(id,8),BitConverter.ToUInt64(id,16));
    }
    // Drive the actual shell veto and receipt boundaries, but never submit a shell delete.
    public static Task OfflineSelectionAsync()=>HydratedSelectionAsync(true);
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
            if(checkOffline) Check.Require(Placeholders.IsOnlineOnly(File.GetAttributes(path),0),"offline selection was not classified as metadata-only");
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
            var selected=typeof(LocalSide).GetMethod("Info",flags)!.Invoke(null,[metadata])!;
            var id=new byte[24]; Check.Require(GetFileInformationByHandleEx(metadata,18,id,24),"fixture identity unavailable");
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            using var originalHash=new ContentHash(); originalHash.Append(new byte[]{1,2});
            var entry=new Entry("stub",false,2,stamp,BitConverter.ToInt64(id,8).ToString(culture),"",readable?originalHash.Finish():"",readable?null:nameof(TextKey.SkipOnlineOnly));
            var proof=new RemovalProof(entry,BitConverter.ToUInt64(id).ToString(culture),readable?entry.ContentHash:null);
            var sinkType=typeof(RecycleBin).GetNestedType("DeleteSink",System.Reflection.BindingFlags.NonPublic)!;
            var sink=Activator.CreateInstance(sinkType,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,[path,selected,entry],null)!;
            var callback=sinkType.GetInterfaces().Single(i=>i.Name=="IFileOperationProgressSink").GetMethod("PreDeleteItem")!.CreateDelegate<Func<uint,IntPtr,int>>(sink);
            var read=typeof(RecycleBin).GetMethod("ObserveReceipt",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            if(checkOffline)
            {
                using var deny=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.Write|FileShare.Delete);
                var approved=callback(0x80u,IntPtr.Zero);
                var same=await (Task<RemovalResult>)read.Invoke(new RecycleBin(),[path,selected,proof,CancellationToken.None])!;
                Check.Require(approved==0 && same.Outcome==RemovalOutcome.Removed && same.RecycledVictim?.Entry.Skip==entry.Skip,"unchanged stub was not accepted on metadata alone");
            }
            // Same identity, size and stamp, but now a locally edited readable version.
            File.SetAttributes(path,FileAttributes.Normal); File.WriteAllBytes(path,[3,4]); File.SetLastWriteTimeUtc(path,readable?stamp.AddMinutes(1):stamp);
            int veto;
            using(var deny=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.Write|FileShare.Delete))
                veto=callback(0x80u,IntPtr.Zero);
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
    public static Task OfflineManagedChangeAsync()=>ManagedChangeAsync(true);
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
                var volume=drive+Path.DirectorySeparatorChar; const string name="first.txt";
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
                var binRoot=Path.Combine(root,ResourceNames.RootBinFolder);
                if(attemptRename) { Directory.CreateDirectory(binRoot); File.WriteAllBytes(Path.Combine(binRoot,name),[4]); }
                var result=await bin.RemoveAsync(volume,DriveType.Removable,expected,selection,default);
                if(attemptRename)
                {
                    Check.Require(armed && denied && result.Outcome==RemovalOutcome.Removed && !File.Exists(full) &&
                        File.ReadAllBytes(Path.Combine(binRoot,name)).SequenceEqual(new byte[]{4}) &&
                        File.Exists(Path.Combine(binRoot,"first (2).txt")) && File.ReadAllBytes(Path.Combine(binRoot,"first (2).txt")).SequenceEqual(data),"managed recycle took a user-renamed object or refused its unchanged source");
                    const string group="group.a"; var folder=Directory.CreateDirectory(Path.Combine(root,group)).FullName;
                    File.WriteAllText(Path.Combine(folder,"quiet"),"kept"); var identity=Identity(folder);
                    var entry=new Entry(group,true,0,default,unchecked((long)identity.FileId).ToString(culture),"","",null);
                    Directory.CreateDirectory(Path.Combine(binRoot,group)); Directory.CreateDirectory(Path.Combine(binRoot,group+" (2)"));
                    var mover=(RecycleBin)Activator.CreateInstance(typeof(RecycleBin),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,[(Func<Task>)(()=>Task.CompletedTask),null],null)!;
                    var moved=await mover.RemoveAsync(volume,DriveType.Removable,entry,new(new(entry,identity.VolumeSerialNumber.ToString(culture),null),null),default);
                    var target=Path.Combine(binRoot,group+" (3)");
                    Check.Require(moved.Outcome==RemovalOutcome.Removed && Directory.Exists(target) && Identity(target)==identity && File.ReadAllText(Path.Combine(target,"quiet"))=="kept","managed folder collision did not preserve the dotted folder name");
                    return;
                }
                Check.Require(armed && result.Outcome==RemovalOutcome.NoEffect && result.Cause==TextKey.FailChanged && File.Exists(full) &&
                    !File.Exists(Path.Combine(root,ResourceNames.RootBinFolder,name)),"managed rename moved a selection whose metadata changed at the arm");
            }
            finally {Subst(drive,"/D");}
        }
        finally {Directory.Delete(root,true);}
    }
    private static async Task SubstituteVersionAsync(bool dispatchOnly)
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
            var flags=System.Reflection.BindingFlags.NonPublic;
            var held=typeof(LocalSide).GetMethod("Info",flags|System.Reflection.BindingFlags.Static)!.Invoke(null,[metadata])!;
            var oldEntry=first.Entry with {ContentHash=new string('0',64)};
            var selected=new RemovalProof(oldEntry,first.Domain,oldEntry.ContentHash);
            // Capture the first version *before* changing the bytes. Exercise both the native receipt dispatch
            // and the same private content-read step with that saved observation; no scheduling race is required.
            byte[] newest=[22,33,44]; var after=before.AddDays(1);
            File.WriteAllBytes(path,newest); File.SetAttributes(path,FileAttributes.Normal); File.SetLastWriteTimeUtc(path,after);
            using var digest=new ContentHash(); digest.Append(newest);
            var wanted=first with { Entry=first.Entry with {Size=newest.Length,Modified=after,ContentHash=digest.Finish()} };
            if(!dispatchOnly)
            {
                var prove=typeof(RecycleBin).GetMethod("ReadSubstituteReceiptAsync",flags|System.Reflection.BindingFlags.Static)!;
                var split=await (Task<RemovalResult>)prove.Invoke(null,[first,metadata,CancellationToken.None])!;
                Check.Require(split.Outcome==RemovalOutcome.Retained && split.RecycledVictim==wanted,
                    "receipt retained first metadata alongside second-version bytes");
            }
            var observe=typeof(RecycleBin).GetMethod("ObserveReceipt",flags|System.Reflection.BindingFlags.Instance)!;
            var receipt=await (Task<RemovalResult>)observe.Invoke(new RecycleBin(),[path,held,selected,CancellationToken.None])!;
            Check.Require(receipt.Outcome==RemovalOutcome.Retained && receipt.RecycledVictim==wanted,
                "production receipt dispatch did not return the proved actual version");
        }
        finally { File.Delete(path); }
    }
    // A batch oplock admits the attribute-only metadata open and breaks only on the later data
    // open, so the break handler edits strictly between the two native receipt samples. A
    // substitute receipt may be refused (Unknown, RecycleUnchecked, no victim) or returned as
    // Retained with the exact readable post-edit version (Skip null); a Removed outcome would
    // mean the edit preceded the first sample.
    private const uint RequestBatchOplock=0x00090008; // FSCTL_REQUEST_BATCH_OPLOCK (winioctl.h)
    private const int IoPending=997;                  // ERROR_IO_PENDING
    public static async Task UninterruptedReceiptAsync()
    {
        var home=Directory.CreateTempSubdirectory(); var path=Path.Combine(home.FullName,"receipt");
        Task<RemovalResult>? reading=null;
        try
        {
            File.WriteAllBytes(path,[11]);
            var identity=Identity(path); var culture=System.Globalization.CultureInfo.InvariantCulture;
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
            object selected;
            using(var meta=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                selected=typeof(LocalSide).GetMethod("Info",flags)!.Invoke(null,[meta])!;
            var modified=(DateTimeOffset)selected.GetType().GetProperty("Modified",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(selected)!;
            byte[] newer=[22,33,44]; var after=modified.AddMinutes(1);
            string hash; using(var d=new ContentHash()){ d.Append(newer); hash=d.Finish(); }
            var idText=unchecked((long)identity.FileId).ToString(culture);
            var volumeId=identity.VolumeSerialNumber.ToString(culture);
            var expected=new Entry("victim",false,newer.Length,after,idText,"",hash,null);
            var proof=new RemovalProof(expected,volumeId,hash);
            var observe=typeof(RecycleBin).GetMethod("ObserveReceipt",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            using var signal=new ManualResetEvent(false);
            var edit=File.OpenHandle(path,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete,FileOptions.Asynchronous);
            var overlappedPtr=System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<System.Threading.NativeOverlapped>());
            bool requested=false, done=false;
            try
            {
                var tagged=new IntPtr(signal.SafeWaitHandle.DangerousGetHandle().ToInt64()|1);
                System.Runtime.InteropServices.Marshal.StructureToPtr(new System.Threading.NativeOverlapped{EventHandle=tagged},overlappedPtr,false);
                var issued=DeviceIoControl(edit,RequestBatchOplock,IntPtr.Zero,0,IntPtr.Zero,0,IntPtr.Zero,overlappedPtr);
                var code=System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Check.Require(!issued && code==IoPending,$"NTFS batch oplock fixture unavailable (Win32 {code})");
                requested=true;
                reading=Task.Run(()=>(Task<RemovalResult>)observe.Invoke(new RecycleBin(),[path,selected,proof,CancellationToken.None])!);
                // The receipt must block on the data open until this holder acknowledges the break.
                var which=WaitHandle.WaitAny([signal,((IAsyncResult)reading!).AsyncWaitHandle],TimeSpan.FromSeconds(30));
                if(which==WaitHandle.WaitTimeout) throw new InvalidDataException("native data-open oplock break did not occur");
                if(which!=0||reading!.IsCompleted) throw new InvalidDataException("receipt returned before opening held bytes");
                Check.Require(OplockResult(edit,overlappedPtr,out _,false),"NTFS did not report an actual data-open break");
                done=true; // GetOverlappedResult established terminal completion of the request
                RandomAccess.Write(edit,newer,0);
                File.SetLastWriteTimeUtc(edit,after.UtcDateTime);
            }
            finally
            {
                // Keep the OVERLAPPED and its event alive until the pending request is terminal.
                if(requested && !done)
                {
                    CancelIoEx(edit,overlappedPtr);
                    _=OplockResult(edit,overlappedPtr,out _,true); // drain so neither the storage nor the event is in use
                }
                edit.Dispose(); // release the holder; a still-outstanding break is acknowledged or cancelled
                System.Runtime.InteropServices.Marshal.FreeHGlobal(overlappedPtr);
            }
            var receipt=await reading!.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(File.ReadAllBytes(path).SequenceEqual(newer),"receipt editor did not establish the new version");
            var victim=receipt.RecycledVictim;
            Check.Require(receipt.Outcome!=RemovalOutcome.Removed,"receipt claimed an unbound removal of the postimage");
            if(victim is null)
                Check.Require(receipt.Outcome==RemovalOutcome.Unknown && receipt.Cause==TextKey.RecycleUnchecked,
                    "receipt claimed success without an exact version");
            else
                Check.Require(receipt.Outcome==RemovalOutcome.Retained && receipt.Cause==TextKey.FailChanged &&
                    victim.NativePath==path && victim.Domain==volumeId && victim.Entry==expected,
                    "Uninterrupted receipt mixed two versions");
        }
        finally
        {
            // Join the receipt once before deleting its tree; a watchdog timeout keeps the tree while its task is live.
            bool settled=reading is null;
            if(!settled) try { settled=reading!.Wait(TimeSpan.FromSeconds(30)); } catch { settled=true; }
            if(settled) home.Delete(true);
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="DeviceIoControl",SetLastError=true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file,uint code,IntPtr input,uint inputSize,IntPtr output,uint outputSize,IntPtr returned,IntPtr overlapped);
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="GetOverlappedResult",SetLastError=true)]
    private static extern bool OplockResult(Microsoft.Win32.SafeHandles.SafeFileHandle file,IntPtr overlapped,out uint transferred,bool wait);
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="CancelIoEx",SetLastError=true)]
    private static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle file,IntPtr overlapped);
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
