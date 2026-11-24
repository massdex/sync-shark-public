using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Observes one bound PC root and changes only the selected object, without replacing unselected names.</summary>
public sealed class LocalSide : ISide
{
    private readonly PairConfig pair;
    private readonly Func<PathScope> scope;
    private readonly EngineSettings settings;
    private readonly IStore store;
    private readonly JournalReader journal;
    private readonly UsnJournal usn = new();
    private DirectoryIdentity Bound => pair.Roots?.Local ?? throw new ItemFailedException(pair.Name, TextKey.RootUnbound, null);
    public Side Side => Side.Local;
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => null;
    public static Func<PairConfig,Func<PathScope>,EngineSettings,IStore,LocalSide> Factory() => Factory(out _);
    public static Func<PairConfig,Func<PathScope>,EngineSettings,IStore,LocalSide> Factory(out Action<PairConfig> reserve)
    {
        var reader = new JournalReader();
        reserve = reader.Attach;
        return (p,s,e,db) => new LocalSide(p,s,e,db,reader);
    }
    private LocalSide(PairConfig pair, Func<PathScope> scope, EngineSettings settings, IStore store, JournalReader journal)
    {
        this.pair=pair; this.scope=scope; this.settings=settings; this.store=store; this.journal=journal;
        if (pair.Roots is { } roots)
            journal.Attach(pair,store,scope,(id,force,ct)=>IdentityPaths.ResolveLinksById(pair.LocalRoot,roots.Local,id,force,ct),
                path=>BoundEntry(pair.LocalRoot,roots.Local,path));
    }
    /// <summary>Publishes host membership changes to the same volume owner used for acquisition.</summary>
    public Task SetMembershipAsync(PairConfig member,bool paused,bool removed,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return journal.Membership(member,paused,removed); }
    internal static RemovalResult Publication(Entry final, JournalPosition? heldPosition) =>
        new(RemovalOutcome.Published,final,null) { LocalPublished=heldPosition };
    public IAsyncEnumerable<Listed> ReadAllAsync(string under,ListOptions options,CancellationToken ct)
    {
        return DirectoryListing.ReadAsync(pair.LocalRoot,Bound,scope(),under,options,ct);
    }
    public Task<Entry?> ReadOneAsync(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var entry=BoundEntry(pair.LocalRoot,Bound,path);
            return Task.FromResult(entry is {IsFolder:false} && StageNames.IsStage(PathNames.Name(entry.Path)) ? null : entry);
        }
        catch(Exception error) when(LocalErrors.IsFileFailure(error)) { throw new ItemFailedException(path,LocalErrors.Cause(error,TextKey.ReadLocalFailed),error); }
    }
    // Keep the approved root and every traversed parent pinned until the exact long name has been read.
    // A missing parent is absence only after the original root is held and authenticated.
    private static Entry? BoundEntry(string root,DirectoryIdentity bound,string path)
    {
        var full=Full(root,path);
        using var pins=PinRoot(root,bound);
        try
        {
            pins.PinDescendants(root,Path.GetDirectoryName(full)!);
            return DirectoryListing.One(root,path);
        }
        catch(IOException e) when((e.HResult&0xffff) is 2 or 3) { return null; }
    }
    public Task<(DirectoryIdentity? Local,DropboxRootIdentity? Dropbox,DriveEvidence? Drive,DriveType? LocalDriveType)> RootAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var (id,type)=DriveIdentity.Read(pair.LocalRoot);
            if(type==DriveType.Network) throw new ItemFailedException(pair.Name,TextKey.RootLocalNetworkUnsupported,null);
            JournalPosition? p=null;
            try { p=usn.Current(pair.LocalRoot); } catch(Exception error) when(LocalErrors.IsFileFailure(error)) { /* Identity is still available. */ }
            var drive=new DriveEvidence(unchecked((uint)id.VolumeSerialNumber),p?.JournalId,
                p is { NextUsn: <= long.MaxValue } ? (long)p.NextUsn : null);
            return Task.FromResult<(DirectoryIdentity?,DropboxRootIdentity?,DriveEvidence?,DriveType?)>((id,null,drive,type));
        }
        catch(Exception e) when(LocalErrors.RootReadCause(e) is { } cause)
        { throw new ItemFailedException(pair.Name,cause,e); }
    }
    public Task<(string? Cursor,JournalPosition? Position)> BoundaryAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<(string?,JournalPosition?)>((null,usn.Current(pair.LocalRoot))); }
    public Task<JournalPosition?> SettledJournalAsync(CancellationToken ct)=>journal.SettledJournalAsync(pair,ct);
    public Task SettleJournalAsync(JournalPosition position,CancellationToken ct)=>journal.SettleAsync(pair,position,store,ct);
    public Task<ChangePage> ReadChangesAsync(string? cursor,JournalPosition? position,CancellationToken ct)=>journal.ReadPageAsync(pair,position,null,settings,ct);
    public Task<ChangePage> ReadChangesAsync(string? cursor,JournalPosition? position,(string? Cursor,JournalPosition? Position) until,CancellationToken ct)=>journal.ReadPageAsync(pair,position,until.Position,settings,ct);
    public Task<DeletionEvidence> DeletedAsync(string path,CancellationToken ct)=>DeletedAsync(path,false,ct);
    public Task<DeletionEvidence> DeletedAsync(string path,bool folder,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<DeletionEvidence>(new DeletionEvidence.Unknown(path,TextKey.RecoveryHistoryLimits)); }
    public Task<JobReply> ReadJobAsync(AcceptedJob job,CancellationToken ct)=>Task.FromResult<JobReply>(new JobReply.Invalid());
    public Task<(IdentityLocation Location,bool InScope)?> ReadByIdAsync(string id,CancellationToken ct) =>
        new IdentityPaths().LocateInScopeAsync(pair.LocalRoot,Bound,id,scope(),ct);
    internal static string Full(string root,string path)=>Path.Combine(root,PathNames.NormalizeRelative(path));
    public Task<Stream> OpenReadAsync(Entry entry,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(entry.Skip is not null || entry.IsFolder) throw new ChangedUnderneathException(entry.Path);
        var full=Full(pair.LocalRoot,entry.Path);
        using var parents=Parents(full);
        try
        {
            const FileShare share=FileShare.ReadWrite|FileShare.Delete;
            using var metadata=Open(full,ReadAttributes,share);
            Verify(metadata,entry,Domain(Bound));
            var file=ReadContent(metadata,full,share,out var observed);
            if(observed.Size==entry.Size && observed.Modified==entry.Modified) return Task.FromResult<Stream>(new LocalReadStream(file,entry.Path));
            file.Dispose(); throw new ChangedUnderneathException(entry.Path);
        }
        catch(Exception error) when(LocalErrors.IsFileFailure(error))
        {
            var cause=LocalErrors.Cause(error,TextKey.ReadLocalFailed);
            throw new ItemFailedException(entry.Path,cause,error){Holder=cause==TextKey.FailLocalInUse?InUse.Processes(full).FirstOrDefault():null};
        }
    }
    public async Task<string> ContentHashAsync(Entry entry,CancellationToken ct)
    { await using var stream=await OpenReadAsync(entry,ct); return await Digest(stream,null,ct); }
    internal static async Task<string> Digest(Stream input,Stream? output,CancellationToken ct)
    {
        // Retain the 0.3.95 streaming chunk used by SyncContentIdentity.DefaultReadBufferBytes.
        const int readBufferBytes=128*1024;
        using var hash=new ContentHash(); var buffer=ArrayPool<byte>.Shared.Rent(readBufferBytes);
        try
        {
            int n; while((n=await input.ReadAsync(buffer.AsMemory(0,readBufferBytes),ct))!=0)
            { hash.Append(buffer.AsSpan(0,n)); if(output is not null) await output.WriteAsync(buffer.AsMemory(0,n),ct); }
            return hash.Finish();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    public async Task<IStaged> StageAsync(string path,Stream content,Entry source,Entry? selectedTarget,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,path); using var parents=Parents(full);
        if(source.Size<0) throw new ItemFailedException(path,TextKey.FailDownloadSizeInvalid,null);
        long available;
        try { available=VolumeSpace.Available(Path.GetDirectoryName(full)!); }
        catch(Exception error) when(LocalErrors.IsFileFailure(error)) { throw new ItemFailedException(path,TextKey.FailLocalSpaceUnknown,error); }
        if(available<source.Size) throw new ItemFailedException(path,TextKey.FailLocalNoSpace,null);
        var name=StageNames.For(full); var h=Open(name,ReadData|WriteData|DeleteAccess,FileShare.None,create:true);
        var file=new FileStream(h,FileAccess.ReadWrite);
        try
        {
            var hash=await Digest(content,file,ct); file.Flush(true);
            if(file.Length!=source.Size || !StringComparer.OrdinalIgnoreCase.Equals(hash,source.ContentHash))
                throw new ItemFailedException(path,file.Length!=source.Size?TextKey.DownloadIncomplete:TextKey.DownloadHashMismatch,null);
            File.SetLastWriteTimeUtc(h,source.Modified.UtcDateTime); var info=Info(h);
            return new LocalStaged(path,new StageProof(name,info.Domain,info.Id,file.Length,hash),file,Owns);
        }
        catch { try { Discard(h); } finally { file.Dispose(); } throw; }
    }
    private async Task<bool> Owns(StageProof proof)=>(await store.ArmedAsync(pair.PairId,CancellationToken.None))?.Stage==proof;
    public async Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim,StageProof expected,CancellationToken ct)
    {
        var note=await store.ArmedAsync(pair.PairId,ct); var path=armedVictim.Proof.Expected.Path; var target=Full(pair.LocalRoot,path);
        if(note?.Victim!=armedVictim || note?.Stage!=expected || !StageNames.IsStage(Path.GetFileName(expected.Path)) ||
            !PathNames.Same(Path.GetDirectoryName(target),Path.GetDirectoryName(expected.Path))) throw new ChangedUnderneathException(path);
        using var parents=Parents(target);
        // The exclusive data open must follow a no-recall metadata check. Repeat on that same data handle
        // because a formerly local stage can be dehydrated or replaced between the two opens.
        using(var metadata=Open(expected.Path,ReadAttributes,FileShare.ReadWrite|FileShare.Delete))
            RequireStage(metadata,expected,path);
        var file=new FileStream(Open(expected.Path,ReadData|WriteData|DeleteAccess,FileShare.None),FileAccess.ReadWrite);
        try
        {
            RequireStage(file.SafeFileHandle,expected,path);
            if(!StringComparer.OrdinalIgnoreCase.Equals(await Digest(file,null,ct),expected.ContentHash)) throw new ChangedUnderneathException(path);
            return new LocalStaged(path,expected,file,Owns);
        }
        catch { file.Dispose(); throw; }
    }
    private static void RequireStage(SafeFileHandle handle,StageProof expected,string path)
    {
        var info=Info(handle);
        if(info.Domain!=expected.VolumeId || info.Id!=expected.FileId || info.Size!=expected.Size || info.Folder || Classify(handle,info.Attributes) is not null)
            throw new ChangedUnderneathException(path);
    }
    public async Task<RemovalResult> PublishAsync(IStaged staged,Entry? expected,RemovalSelection? selection,CancellationToken ct)
    {
        var local=(LocalStaged)staged; var file=local.Held; var full=Full(pair.LocalRoot,local.Path);
        using var parents=Parents(full,out var driveType);
        var heldInfo=Info(file.SafeFileHandle);
        if(local.Published || heldInfo.Domain!=local.Proof.VolumeId || heldInfo.Size!=local.Proof.Size || !PathNames.Same(Path.GetDirectoryName(full),Path.GetDirectoryName(local.Proof.Path)) ||
            heldInfo.Id!=local.Proof.FileId) throw new ChangedUnderneathException(local.Path);
        RemovalResult? removal=null;
        if(expected is not null)
        {
            if(expected.Path!=local.Path || selection?.Proof.Expected!=expected) throw new ChangedUnderneathException(local.Path);
            removal=await RemovePinned(expected,selection!,local.Proof,driveType,ct);
            if(removal.Outcome!=RemovalOutcome.Removed) return removal;
        }
        else if(selection is not null) throw new ChangedUnderneathException(local.Path);
        ct.ThrowIfCancellationRequested(); Rename(file.SafeFileHandle,parents.Last,Path.GetFileName(full)); local.Published=true;
        try
        {
            var final=Info(file.SafeFileHandle);
            if(final.Domain!=local.Proof.VolumeId || final.Id!=local.Proof.FileId || final.Size!=local.Proof.Size)
                return new(RemovalOutcome.Unknown,null,TextKey.PublishFailed);
            return Publication(final.Entry(local.Path,local.Proof.ContentHash),usn.Published(file.SafeFileHandle)) with
                {Recycled=removal?.Recycled, RecycledVictim=removal?.RecycledVictim};
        }
        finally { file.Dispose(); }
    }
    public async Task<RemovalResult> DeleteAsync(Entry expected,RemovalSelection? selection,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(selection?.Proof.Expected!=expected) throw new ChangedUnderneathException(expected.Path);
        using var parents=Parents(Full(pair.LocalRoot,expected.Path),out var driveType);
        return await RemovePinned(expected,selection!,null,driveType,ct);
    }
    private Task<RemovalResult> RemovePinned(Entry expected,RemovalSelection selection,StageProof? stage,DriveType driveType,CancellationToken ct)
    {
        if(selection.Proof.VolumeId!=Domain(Bound)) throw new ChangedUnderneathException(expected.Path);
        return new RecycleBin(()=>store.ArmAsync(pair.PairId,selection,stage,ct),scope())
            .RemoveAsync(pair.LocalRoot,driveType,expected,selection,ct);
    }
    public Task<RemovalResult> EnsureFolderAsync(string path,CancellationToken ct)=>CreateFolder(path,false,ct);
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path,CancellationToken ct)=>CreateFolder(path,true,ct);
    private async Task<RemovalResult> CreateFolder(string path,bool exclusive,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,path); using var parents=Parents(full);
        if(!CreateDirectoryW(DriveIdentity.ToExtendedWindowsPath(full),IntPtr.Zero))
        {
            int code=Marshal.GetLastWin32Error(); if(code!=183) throw Native(code); if(exclusive) throw new PathOccupiedException(path,Side.Local);
        }
        var final=await ReadOneAsync(path,ct);
        if(final is not { IsFolder:true, Skip:null }) throw new PathOccupiedException(path,Side.Local);
        return Publication(final,null);
    }
    public Task<RemovalResult> MoveAsync(Entry expected,string newPath,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var src=Full(pair.LocalRoot,expected.Path); var dest=Full(pair.LocalRoot,newPath);
        using var sourceParents=Parents(src); using var destinationParents=Parents(dest);
        using var held=Open(src,DeleteAccess|ReadAttributes,FileShare.Read);
        Verify(held,expected,Domain(Bound)); Rename(held,destinationParents.Last,Path.GetFileName(dest));
        return Task.FromResult(Publication(Info(held).Entry(newPath,expected.ContentHash),usn.Published(held)));
    }
    public Task<RemovalResult> RestoreRecycledAsync(IdentityLocation actual,Entry version,string originalPath,CancellationToken ct) =>
        RecycleBin.RestoreRecycledAsync(actual,version,originalPath,pair.LocalRoot,Bound,ct);
    public async Task<Recovery?> RecoverAsync(IdentityLocation actual,Entry version,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(version.Skip is not null || actual.Entry.Skip is not null || actual.Entry.Id!=version.Id || !Path.IsPathFullyQualified(actual.NativePath)) return null;
        using var parents=new ParentHandles(Path.GetDirectoryName(actual.NativePath)!);
        using var held=Open(actual.NativePath,ReadAttributes,FileShare.Read);
        var info=Verify(held,version,actual.Domain);
        if(version.IsFolder) return new Recovery.Folder(version,ReceiptContents(actual.NativePath,info,version.Path,scope(),ct));
        var bytes=ReadContent(held,actual.NativePath,FileShare.Read,out var observed);
        try
        {
            if(observed.Size!=version.Size || observed.Modified!=version.Modified || actual.Entry.IsFolder ||
                (version.ContentHash.Length==0 && (actual.Entry.Size!=version.Size || actual.Entry.Modified!=version.Modified))) { bytes.Dispose(); return null; }
            var digest=await Digest(bytes,null,ct);
            if(version.ContentHash.Length!=0 && !StringComparer.OrdinalIgnoreCase.Equals(digest,version.ContentHash)) { bytes.Dispose(); return null; }
            bytes.Position=0; return new Recovery.File(version with { ContentHash=digest },bytes);
        }
        catch { bytes.Dispose(); throw; }
    }
    internal static async IAsyncEnumerable<Listed> ReceiptContents(string native,HeldInfo info,string original,PathScope scope,[EnumeratorCancellation] CancellationToken ct)
    {
        await foreach(var child in DirectoryListing.ReadAsync(native,info.Identity,scope,"",new(true,true){RequireListedIdentity=true},ct))
        {
            var path=PathNames.Child(original,child.Path);
            if(child.Entry is not { } item || item.Skip==nameof(TextKey.SkipUnreadable)) throw new ItemFailedException(path,TextKey.RecycleUnchecked,null);
            yield return new Listed(path,item with {Path=path});
        }
    }
    // Native handles stay open across verification and mutation. OPEN_REPARSE_POINT never follows the leaf.
    internal const uint ReadData=0x80000000,WriteData=0x40000000,DeleteAccess=0x10000,ReadAttributes=0x80,WriteAttributes=0x100;
    internal static bool IsLink(FileAttributes a)=>(a&FileAttributes.ReparsePoint)!=0;
    internal static string Domain(DirectoryIdentity id)=>id.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
    private const uint NoRecallAndOpenReparsePoint=0x00300000, BackupSemantics=0x02000000;
    internal static SafeFileHandle Open(string path,uint access,FileShare share,bool create=false)
    {
        var handle=CreateFileW(DriveIdentity.ToExtendedWindowsPath(path),access,(uint)share,IntPtr.Zero,create?1u:3u,NoRecallAndOpenReparsePoint|BackupSemantics,IntPtr.Zero);
        if(!handle.IsInvalid) return handle;
        var code=Marshal.GetLastWin32Error(); handle.Dispose(); throw Native(code);
    }
    internal static IOException Native(int code)=>new(new Win32Exception(code).Message,unchecked((int)(0x80070000u|(uint)code)));
    internal sealed record HeldInfo(DirectoryIdentity Identity,FileAttributes Attributes,long Size,DateTimeOffset Modified)
    {
        internal string Id=>LocalIdentity.FileIdText(Identity.FileId);
        internal string Domain=>LocalSide.Domain(Identity);
        internal bool Folder=>(Attributes&FileAttributes.Directory)!=0;
        internal Entry Entry(string path,string hash)=>new(path,Folder,Folder?0:Size,Folder?default:Modified,Id,"",hash,null);
    }
    internal static HeldInfo Info(SafeFileHandle handle)
    {
        if(!GetFileInformationByHandle(handle,out var i)) throw Native(Marshal.GetLastWin32Error());
        var identity=new DirectoryIdentity(i.Volume,((ulong)i.IndexHigh<<32)|i.IndexLow,null);
        if(GetFileInformationByHandleEx(handle,18,out WideId wide,24)) identity=new(wide.Volume,wide.Low,wide.High);
        else if(Marshal.GetLastWin32Error() is not (1 or 50 or 87 or 120 or 124)) throw Native(Marshal.GetLastWin32Error());
        return new(identity,(FileAttributes)i.Attributes,
            checked((long)(((ulong)i.SizeHigh<<32)|i.SizeLow)),DateTimeOffset.FromFileTime(((long)i.WriteHigh<<32)|i.WriteLow).ToUniversalTime());
    }
    internal static HeldInfo Verify(SafeFileHandle handle,Entry entry,string domain,bool allowPlaceholder=false)
    {
        var info=Info(handle);
        var classification=Classify(handle,info.Attributes);
        if(!Matches(info,entry,domain,classification,allowPlaceholder)) throw new ChangedUnderneathException(entry.Path);
        return info;
    }
    internal static bool Matches(HeldInfo info,Entry entry,string domain,TextKey? classification,bool allowPlaceholder) =>
        info.Id==entry.Id && info.Domain==domain && info.Folder==entry.IsFolder && (info.Folder || (info.Size==entry.Size && info.Modified==entry.Modified)) &&
        classification?.ToString()==entry.Skip && (classification is null || (allowPlaceholder && classification==TextKey.SkipOnlineOnly));
    // The metadata handle selected the object, not just a reusable pathname. Reclassify on the data handle
    // before reading even one byte. A receipt takes its metadata and digest under this one writer-denying open.
    internal static FileStream ReadContent(SafeFileHandle metadata,string path,FileShare share,out HeldInfo observed)
    {
        var before=Info(metadata);
        if(before.Folder || Classify(metadata,before.Attributes) is not null) throw new ChangedUnderneathException(path);
        var bytes=CreateFileW(DriveIdentity.ToExtendedWindowsPath(path),ReadData,(uint)share,IntPtr.Zero,3,NoRecallAndOpenReparsePoint,IntPtr.Zero);
        try
        {
            if(bytes.IsInvalid) throw Native(Marshal.GetLastWin32Error());
            observed=Info(bytes);
            if(observed.Identity!=before.Identity || observed.Folder || Classify(bytes,observed.Attributes) is not null)
                throw new ChangedUnderneathException(path);
            return new FileStream(bytes,FileAccess.Read);
        }
        catch { bytes.Dispose(); throw; }
    }
    internal static TextKey? Classify(SafeFileHandle handle,FileAttributes attributes)
    {
        // Plain files have no reparse tag or recall attributes to classify.
        if((attributes & ~(FileAttributes.Normal|FileAttributes.Archive|FileAttributes.Directory))==0) return null;
        if(!GetFileInformationByHandleEx(handle,9,out AttributeTag tag,8)) throw Native(Marshal.GetLastWin32Error());
        return Placeholders.Skip(attributes,tag.Tag);
    }
    // Authenticate the root before the descendant operation. Only failures in this boundary belong to Host re-observation.
    private static ParentHandles PinRoot(string root,DirectoryIdentity bound)
    {
        ParentHandles? held=null;
        try
        {
            held=new ParentHandles(root);
            if(Info(held.Last).Identity!=bound) throw new ChangedUnderneathException(root);
            return held;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or ChangedUnderneathException)
        { held?.Dispose(); throw new ChangedUnderneathException(root,e); }
    }
    private ParentHandles Parents(string full)=>Parents(pair.LocalRoot,Bound,full);
    internal static ParentHandles Parents(string root,DirectoryIdentity bound,string full)
    {
        var held=PinRoot(root,bound);
        try { held.PinDescendants(root,Path.GetDirectoryName(full)!); return held; }
        catch { held.Dispose(); throw; }
    }
    private ParentHandles Parents(string full,out DriveType driveType)
    {
        var held=Parents(full);
        try { driveType=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.LocalRoot))!).DriveType; }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception)
        { held.Dispose(); throw new ChangedUnderneathException(pair.LocalRoot,e); }
        if(driveType is DriveType.Unknown or DriveType.NoRootDirectory) { held.Dispose(); throw new ChangedUnderneathException(pair.LocalRoot); }
        return held;
    }
    internal sealed class ParentHandles : IDisposable
    {
        private readonly List<SafeFileHandle> handles=[];
        internal SafeFileHandle Last=>handles[^1];
        internal ParentHandles(string directory)
        {
            var chain=new Stack<string>();
            for(var d=new DirectoryInfo(directory);d is not null;d=d.Parent) chain.Push(d.FullName);
            try
            {
                foreach(var name in chain) Pin(name);
            }
            catch { Dispose(); throw; }
        }
        internal void PinDescendants(string root,string parent)
        {
            var relative=Path.GetRelativePath(root,parent);
            var current=root;
            if(relative!=".") foreach(var name in relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))
            { current=Path.Combine(current,name); Pin(current); }
        }
        internal void Pin(string name)
        {
            var h=Open(name,ReadAttributes|0x20,FileShare.ReadWrite); handles.Add(h);
            var info=Info(h);
            if(!info.Folder || (IsLink(info.Attributes) && Classify(h,info.Attributes) is not null)) throw new ChangedUnderneathException(name);
        }
        public void Dispose() { for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose(); }
    }
    internal static void Rename(SafeFileHandle source,SafeFileHandle parent,string name)
    {
        var text=System.Text.Encoding.Unicode.GetBytes(name); int length=IntPtr.Size*2,offset=length+sizeof(int);
        var buffer=Marshal.AllocHGlobal(offset+text.Length+IntPtr.Size);
        bool added=false;
        try
        {
            Marshal.WriteIntPtr(buffer,IntPtr.Zero); parent.DangerousAddRef(ref added);
            Marshal.WriteIntPtr(buffer,IntPtr.Size,parent.DangerousGetHandle()); Marshal.WriteInt32(buffer,length,text.Length);
            Marshal.Copy(text,0,buffer+offset,text.Length);
            var status=NtSetInformationFile(source,out _,buffer,(uint)(offset+text.Length+IntPtr.Size),10);
            if(status<0)
            { var code=(int)RtlNtStatusToDosError(status); if(code is 80 or 183) throw new PathOccupiedException(name,Side.Local); throw Native(code); }
        }
        finally { if(added) parent.DangerousRelease(); Marshal.FreeHGlobal(buffer); }
    }
    internal static void Discard(SafeFileHandle handle)
    {
        byte delete=1; if(!SetFileInformationByHandle(handle,4,ref delete,1)) throw Native(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    { public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes,Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct WideId { public ulong Volume,Low,High; }
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out WideId id,uint length);
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status,Information; }
    [DllImport("kernel32",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] internal static extern bool CreateDirectoryW(string path,IntPtr security);
    [DllImport("kernel32",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfoNative info);
    [DllImport("kernel32",SetLastError=true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out AttributeTag tag,uint length);
    [DllImport("kernel32",SetLastError=true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref byte value,uint length);
    [DllImport("ntdll")] private static extern int NtSetInformationFile(SafeFileHandle handle,out IoStatus status,IntPtr info,uint size,int kind);
    [DllImport("ntdll")] private static extern uint RtlNtStatusToDosError(int status);
}
