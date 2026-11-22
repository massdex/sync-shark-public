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
    public static Func<PairConfig,Func<PathScope>,EngineSettings,IStore,LocalSide> Factory()
    {
        var reader = new JournalReader();
        return (p,s,e,db) => new LocalSide(p,s,e,db,reader);
    }
    private LocalSide(PairConfig pair, Func<PathScope> scope, EngineSettings settings, IStore store, JournalReader journal)
    {
        this.pair=pair; this.scope=scope; this.settings=settings; this.store=store; this.journal=journal;
        if (pair.Roots is { } roots)
            journal.Attach(pair,store,scope,(id,force,ct)=>IdentityPaths.ResolveLinksById(pair.LocalRoot,roots.Local,id,force,ct),
                path=>DirectoryListing.One(pair.LocalRoot,path));
    }
    /// <summary>Publishes host membership changes to the same volume owner used for acquisition.</summary>
    public Task SetMembershipAsync(PairConfig member,bool paused,bool removed,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); journal.Membership(member,paused,removed); return Task.CompletedTask; }
    public static RemovalResult Publication(Entry final, JournalPosition? heldPosition) =>
        new(RemovalOutcome.Published,final,null) { LocalPublished=heldPosition };
    public static void RequireRoot(DirectoryIdentity bound,DirectoryIdentity observed)
    {
        if(bound!=observed) throw new InventoryIncompleteException(Text.Get(TextKey.RootLocalIdentityChanged),null);
    }
    public IAsyncEnumerable<Listed> ReadAllAsync(string under,ListOptions options,CancellationToken ct)
    {
        RequireRoot(Bound,DriveIdentity.Read(pair.LocalRoot).Identity);
        return DirectoryListing.ReadAsync(pair.LocalRoot,Bound,scope(),under,options,ct);
    }
    public Task<Entry?> ReadOneAsync(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireRoot(Bound,DriveIdentity.Read(pair.LocalRoot).Identity);
        return Task.FromResult(DirectoryListing.One(pair.LocalRoot,path));
    }
    public Task<(DirectoryIdentity? Local,DropboxRootIdentity? Dropbox,DriveEvidence? Drive,DriveType? LocalDriveType)> RootAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var (id,type)=DriveIdentity.Read(pair.LocalRoot);
            if(type==DriveType.Network) throw new ItemFailedException(pair.Name,TextKey.RootLocalNetworkUnsupported,null);
            JournalPosition? p=null;
            try { p=usn.Current(pair.LocalRoot); } catch(Exception error) when(IsIo(error)) { /* Identity is still available. */ }
            var drive=new DriveEvidence(id.VolumeSerialNumber<=uint.MaxValue?(uint)id.VolumeSerialNumber:null,p?.JournalId,
                p is { NextUsn: <= long.MaxValue } ? (long)p.NextUsn : null);
            return Task.FromResult<(DirectoryIdentity?,DropboxRootIdentity?,DriveEvidence?,DriveType?)>((id,null,drive,type));
        }
        catch(Exception e) when(IsIo(e) || e is NotSupportedException)
        { throw new ItemFailedException(pair.Name,e is UnauthorizedAccessException or Win32Exception {NativeErrorCode:5} ? TextKey.RootLocalAccessDenied : e is NotSupportedException ? TextKey.RootLocalFormatUnsupported : TextKey.RootLocalIdentityUnavailable,e); }
    }
    public Task<(string? Cursor,JournalPosition? Position)> BoundaryAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<(string?,JournalPosition?)>((null,usn.Current(pair.LocalRoot))); }
    public Task SettleJournalAsync(JournalPosition position,CancellationToken ct)=>journal.SettleAsync(pair,position,store,ct);
    public Task<ChangePage> ReadChangesAsync(string? cursor,JournalPosition? position,CancellationToken ct)=>journal.ReadPageAsync(pair,position,null,settings,ct);
    public Task<ChangePage> ReadChangesAsync(string? cursor,JournalPosition? position,(string? Cursor,JournalPosition? Position) until,CancellationToken ct)=>journal.ReadPageAsync(pair,position,until.Position,settings,ct);
    public Task<DeletionEvidence> DeletedAsync(string path,CancellationToken ct)=>DeletedAsync(path,false,ct);
    public Task<DeletionEvidence> DeletedAsync(string path,bool folder,CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<DeletionEvidence>(new DeletionEvidence.Unknown(path,TextKey.RecoveryHistoryLimits)); }
    public Task<JobReply> ReadJobAsync(AcceptedJob job,CancellationToken ct)=>Task.FromResult<JobReply>(new JobReply.Invalid());
    public async Task<(IdentityLocation Location,bool InScope)?> ReadByIdAsync(string id,CancellationToken ct)
    {
        var location=await new IdentityPaths().LocateAsync(pair.LocalRoot,Bound,id,ct);
        if(location is null) return null;
        var relative=PathNames.RelativeUnderLocalRoot(pair.LocalRoot,location.NativePath);
        return (location,relative is not null && !scope().Excludes(relative,null,location.Entry.IsFolder));
    }
    internal static string Full(string root,string path)=>Path.Combine(root,PathNames.NormalizeRelative(path));
    public Task<Stream> OpenReadAsync(Entry entry,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,entry.Path);
        using var parents=Parents(full);
        try
        {
            var h=Open(full,ReadData,FileShare.ReadWrite|FileShare.Delete);
            try { Verify(h,entry,Domain(Bound)); return Task.FromResult<Stream>(new LocalReadStream(new FileStream(h,FileAccess.Read),entry.Path)); }
            catch { h.Dispose(); throw; }
        }
        catch(Exception error) when(IsIo(error))
        {
            var cause=LocalErrors.Cause(error,TextKey.ReadLocalFailed);
            throw new ItemFailedException(entry.Path,cause,error){Holder=cause==TextKey.FailLocalInUse?InUse.Processes(full).FirstOrDefault():null};
        }
    }
    public async Task<string> ContentHashAsync(Entry entry,CancellationToken ct)
    { await using var stream=await OpenReadAsync(entry,ct); return await Digest(stream,null,ct); }
    internal static async Task<string> Digest(Stream input,Stream? output,CancellationToken ct)
    {
        using var hash=new ContentHash(); var buffer=ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int n; while((n=await input.ReadAsync(buffer.AsMemory(),ct))!=0)
            { hash.Append(buffer.AsSpan(0,n)); if(output is not null) await output.WriteAsync(buffer.AsMemory(0,n),ct); }
            return hash.Finish();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    public async Task<IStaged> StageAsync(string path,Stream content,Entry source,Entry? selectedTarget,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,path); using var parents=Parents(full);
        if(source.Size<0 || VolumeSpace.Available(Path.GetDirectoryName(full)!)<source.Size) throw new ItemFailedException(path,TextKey.FailLocalNoSpace,null);
        var name=StageNames.For(full); var h=Open(name,ReadData|WriteData|DeleteAccess,FileShare.Read,create:true);
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
        using var parents=Parents(target); var file=new FileStream(Open(expected.Path,ReadData|DeleteAccess,FileShare.Read),FileAccess.Read);
        try
        {
            var info=Info(file.SafeFileHandle);
            if(info.Domain!=expected.VolumeId || info.Id!=expected.FileId || info.Size!=expected.Size || IsLink(info.Attributes) ||
                !StringComparer.OrdinalIgnoreCase.Equals(await Digest(file,null,ct),expected.ContentHash)) throw new ChangedUnderneathException(path);
            return new LocalStaged(path,expected,file,Owns);
        }
        catch { file.Dispose(); throw; }
    }
    public async Task<RemovalResult> PublishAsync(IStaged staged,Entry? expected,RemovalSelection? selection,CancellationToken ct)
    {
        var local=(LocalStaged)staged; var file=(FileStream)local.Held; var full=Full(pair.LocalRoot,local.Path);
        using var parents=Parents(full);
        var heldInfo=Info(file.SafeFileHandle);
        if(!local.AdapterOwned || local.Published || heldInfo.Domain!=local.Proof.VolumeId || heldInfo.Size!=local.Proof.Size || !PathNames.Same(Path.GetDirectoryName(full),Path.GetDirectoryName(local.Proof.Path)) ||
            heldInfo.Id!=local.Proof.FileId) throw new ChangedUnderneathException(local.Path);
        RemovalResult? removal=null;
        if(expected is not null)
        {
            if(expected.Path!=local.Path || selection?.Proof.Expected!=expected) throw new ChangedUnderneathException(local.Path);
            removal=await Remove(expected,selection!,local.Proof,ct);
            if(removal.Outcome!=RemovalOutcome.Removed) return removal;
        }
        else if(selection is not null) throw new ChangedUnderneathException(local.Path);
        ct.ThrowIfCancellationRequested(); Rename(file.SafeFileHandle,parents.Last,Path.GetFileName(full)); local.Published=true;
        var proof=usn.Published(file.SafeFileHandle);
        return Publication(Info(file.SafeFileHandle).Entry(local.Path,local.Proof.ContentHash),proof) with {Recycled=removal?.Recycled, RecycledVictim=removal?.RecycledVictim};
    }
    public Task<RemovalResult> DeleteAsync(Entry expected,RemovalSelection? selection,CancellationToken ct)=>
        selection?.Proof.Expected==expected?Remove(expected,selection!,null,ct):throw new ChangedUnderneathException(expected.Path);
    private async Task<RemovalResult> Remove(Entry expected,RemovalSelection selection,StageProof? stage,CancellationToken ct)
    {
        using var parents=Parents(Full(pair.LocalRoot,expected.Path)); var drive=DriveIdentity.Read(pair.LocalRoot); RequireRoot(Bound,drive.Identity);
        if(selection.Proof.VolumeId!=Domain(Bound)) throw new ChangedUnderneathException(expected.Path);
        return await new RecycleBin(()=>store.ArmAsync(pair.PairId,selection,stage,ct),scope())
            .RemoveAsync(pair.LocalRoot,drive.Type,expected,selection,ct);
    }
    public Task<RemovalResult> EnsureFolderAsync(string path,CancellationToken ct)=>CreateFolder(path,false,ct);
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path,CancellationToken ct)=>CreateFolder(path,true,ct);
    private async Task<RemovalResult> CreateFolder(string path,bool exclusive,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,path); using var parents=Parents(full);
        if(!CreateDirectoryW(full,IntPtr.Zero))
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
    public async Task<Recovery?> RecoverAsync(IdentityLocation actual,Entry version,CancellationToken ct)
    {
        if(actual.Entry.Id!=version.Id || !Path.IsPathFullyQualified(actual.NativePath)) return null;
        using var parents=new ParentHandles(Path.GetDirectoryName(actual.NativePath)!);
        var file=Open(actual.NativePath,version.IsFolder?ReadAttributes:ReadData,FileShare.Read);
        try
        {
            var info=Verify(file,version,actual.Domain);
            if(version.IsFolder)
            { file.Dispose(); return new Recovery.Folder(version,ReceiptContents(actual.NativePath,info,version.Path,scope(),ct)); }
            var bytes=new FileStream(file,FileAccess.Read);
            try
            {
                if(!StringComparer.OrdinalIgnoreCase.Equals(await Digest(bytes,null,ct),version.ContentHash)){ bytes.Dispose(); return null; }
                bytes.Position=0; return new Recovery.File(version,bytes);
            }
            catch { bytes.Dispose(); throw; }
        }
        catch { file.Dispose(); throw; }
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
    internal static bool IsIo(Exception e)=>LocalErrors.IsFileFailure(e);
    internal static bool IsLink(FileAttributes a)=>(a&FileAttributes.ReparsePoint)!=0;
    internal static string Domain(DirectoryIdentity id)=>id.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
    internal static SafeFileHandle Open(string path,uint access,FileShare share,bool create=false)
    {
        var handle=CreateFileW(path,access,(uint)share,IntPtr.Zero,create?1u:3u,0x02200000,IntPtr.Zero);
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
    internal static HeldInfo Verify(SafeFileHandle handle,Entry entry,string domain)
    {
        var info=Info(handle);
        var placeholder=entry.Skip==nameof(TextKey.SkipOnlineOnly);
        if(info.Id!=entry.Id || info.Domain!=domain || info.Folder!=entry.IsFolder || (!info.Folder && (info.Size!=entry.Size || info.Modified!=entry.Modified)) ||
            (placeholder ? !Dehydrated(handle,info.Attributes) : IsLink(info.Attributes))) throw new ChangedUnderneathException(entry.Path);
        return info;
    }
    internal static bool Dehydrated(SafeFileHandle handle,FileAttributes attributes)
    {
        if(!GetFileInformationByHandleEx(handle,9,out AttributeTag tag,8)) throw Native(Marshal.GetLastWin32Error());
        return Placeholders.IsOnlineOnly(attributes,tag.Tag);
    }
    private ParentHandles Parents(string full)
    {
        var held=new ParentHandles(Path.GetDirectoryName(full)!);
        try { RequireRoot(Bound,DriveIdentity.Read(pair.LocalRoot).Identity); return held; }
        catch { held.Dispose(); throw; }
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
                foreach(var name in chain)
                {
                    var h=Open(name,ReadAttributes|0x20,FileShare.ReadWrite); handles.Add(h);
                    var info=Info(h); if(!info.Folder || IsLink(info.Attributes)) throw new ChangedUnderneathException(name);
                }
            }
            catch { Dispose(); throw; }
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
