using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Massdex.SyncShark.Engine.Local.LocalNative;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Observes one bound PC root and changes only the selected object, without replacing unselected names.</summary>
public sealed class LocalSide : ISide
{
    // The pair shares one bin-then-rename turn; victim verification stays parallel.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim> Turns=new();
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
        catch(IOException e) when(LocalErrors.Missing(e.HResult)) { return null; }
        catch(ChangedUnderneathException) when((DriveIdentity.Basic(pins.Last).FileAttributes & (FileAttributes.Directory|FileAttributes.ReparsePoint))==0) { return null; }
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
    /// <inheritdoc/>
    public Task<IReadOnlyList<string>?> NamesByIdAsync(string id,CancellationToken ct) =>
        Task.FromResult(IdentityPaths.LinkNamesById(pair.LocalRoot,Bound,id,scope(),ct));
    // Stream support is a property of the volume; the first stamp records it so a stream-less volume is never probed again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> KeepStreamSupport=new();
    /// <inheritdoc/>
    public Task<(string? Token,string? MarkedPath)> KeepMarkAsync(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var domain=Domain(Bound);
        if(KeepStreamSupport.TryGetValue(domain,out var supported) && !supported) return Task.FromResult<(string?,string?)>((null,null));
        try
        {
            // Item first, then nearest ancestor folder; the pair root can never carry a relative-path rule.
            for(var candidate=path;candidate.Length>0;candidate=PathNames.Parent(candidate))
            {
                var token=LocalNative.ReadKeepStream(Full(pair.LocalRoot,candidate));
                if(token is {Length:>0}) return Task.FromResult<(string?,string?)>((token,candidate));
            }
            return Task.FromResult<(string?,string?)>((null,null));
        }
        catch(Exception error) when(LocalErrors.IsFileFailure(error))
        { throw new ItemFailedException(path,LocalErrors.Cause(error,TextKey.ReadLocalFailed),error); }
    }
    /// <inheritdoc/>
    public Task<bool> StampKeepAsync(string path,string token,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var domain=Domain(Bound);
        if(KeepStreamSupport.TryGetValue(domain,out var known) && !known) return Task.FromResult(false);
        try
        {
            var written=LocalNative.WriteKeepStream(Full(pair.LocalRoot,path),token);
            KeepStreamSupport[domain]=written;
            return Task.FromResult(written);
        }
        catch(Exception error) when(LocalErrors.IsFileFailure(error))
        { throw new ItemFailedException(path,LocalErrors.Cause(error,TextKey.ReadLocalFailed),error); }
    }
    /// <inheritdoc/>
    public Task ClearKeepAsync(string path,string token,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(KeepStreamSupport.TryGetValue(Domain(Bound),out var known) && !known) return Task.CompletedTask;
        try
        {
            var full=Full(pair.LocalRoot,path);
            if(LocalNative.ReadKeepStream(full)==token) LocalNative.ClearKeepStream(full);
        }
        catch(Exception error) when(LocalErrors.IsFileFailure(error)) { /* a vanished object took its marker with it */ }
        return Task.CompletedTask;
    }
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
        using var hash=new ContentHash();
        using var sink=new DigestSink(hash,output ?? Stream.Null);
        await input.CopyToAsync(sink,ct);
        return hash.Finish();
    }
    // Hash the bytes forwarded to the stage without owning its stream.
    private sealed class DigestSink(ContentHash hash,Stream output) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer,int offset,int count)
        {
            hash.Append(buffer.AsSpan(offset,count));
            output.Write(buffer,offset,count);
        }
        public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset,count),ct).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct=default)
        {
            hash.Append(bytes.Span);
            return output.WriteAsync(bytes,ct);
        }
    }
    public async Task<IStaged> StageAsync(string path,Stream content,Entry source,Entry? selectedTarget,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,path);
        SafeFileHandle h; string name;
        using(Parents(full))
        {
            if(source.Size<0) throw new ItemFailedException(path,TextKey.FailDownloadSizeInvalid,null);
            long available;
            try { available=VolumeSpace.Available(Path.GetDirectoryName(full)!); }
            catch(Exception error) when(LocalErrors.IsFileFailure(error)) { throw new ItemFailedException(path,TextKey.FailLocalSpaceUnknown,error); }
            if(available<source.Size) throw new ItemFailedException(path,TextKey.FailLocalNoSpace,null);
            name=StageNames.For(full); h=Open(name,ReadData|WriteData|DeleteAccess,FileShare.Delete,create:true);
        }
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
        var file=new FileStream(Open(expected.Path,ReadData|WriteData|DeleteAccess,FileShare.Delete),FileAccess.ReadWrite);
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
        var turn=expected==null?null:Turns.GetOrAdd(pair.PairId,_=>new(1)); bool held=false;
        try
        {
            RemovalResult? removal=null;
            if(expected is not null)
            {
                if(expected.Path!=local.Path || selection?.Proof.Expected!=expected) throw new ChangedUnderneathException(local.Path);
                removal=await RemovePinned(expected,selection!,local.Proof,driveType,ct,async()=>{await turn!.WaitAsync(ct);held=true;});
                if(removal.Outcome!=RemovalOutcome.Removed) return removal;
            }
            else if(selection is not null) throw new ChangedUnderneathException(local.Path);
            ct.ThrowIfCancellationRequested(); Rename(file.SafeFileHandle,parents.Last,Path.GetFileName(full)); local.Published=true;
            try
            {
                var final=Info(file.SafeFileHandle);
                if(final.Domain!=local.Proof.VolumeId || final.Id!=local.Proof.FileId || final.Size!=local.Proof.Size)
                    return new(RemovalOutcome.Unknown,null,TextKey.PublishFailed);
                var published=Publication(final.Entry(local.Path,local.Proof.ContentHash),usn.Published(file.SafeFileHandle)) with
                    {Recycled=removal?.Recycled, RecycledVictim=removal?.RecycledVictim};
                if(selection!=null) await store.DisarmAsync(pair.PairId,selection,local.Proof,ct);
                return published;
            }
            finally { file.Dispose(); }
        }
        finally { if(held) turn!.Release(); }
    }
    public async Task<RemovalResult> DeleteAsync(Entry expected,RemovalSelection? selection,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(selection?.Proof.Expected!=expected) throw new ChangedUnderneathException(expected.Path);
        using var parents=Parents(Full(pair.LocalRoot,expected.Path),out var driveType);
        var turn=Turns.GetOrAdd(pair.PairId,_=>new(1)); bool held=false;
        try
        {
            var result=await RemovePinned(expected,selection!,null,driveType,ct,async()=>{await turn.WaitAsync(ct);held=true;});
            if(result.Outcome==RemovalOutcome.Removed && !expected.IsFolder) await store.DisarmAsync(pair.PairId,selection!,null,ct);
            return result;
        }
        finally { if(held) turn.Release(); }
    }
    private Task<RemovalResult> RemovePinned(Entry expected,RemovalSelection selection,StageProof? stage,DriveType driveType,CancellationToken ct,Func<Task> enter)
    {
        if(selection.Proof.VolumeId!=Domain(Bound)) throw new ChangedUnderneathException(expected.Path);
        return new RecycleBin(async()=>{await enter();return await store.ArmAsync(pair.PairId,selection,stage,ct);},scope())
            .RemoveAsync(pair.LocalRoot,driveType,expected,selection,ct);
    }
    public Task<RemovalResult> EnsureFolderAsync(string path,CancellationToken ct)=>CreateFolder(path,false,ct);
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path,CancellationToken ct)=>CreateFolder(path,true,ct);
    private async Task<RemovalResult> CreateFolder(string path,bool exclusive,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var full=Full(pair.LocalRoot,path); using var parents=Parents(full);
        if(!CreateDirectoryW(DriveIdentity.ToExtendedWindowsPath(full),IntPtr.Zero))
        {
            int code=Marshal.GetLastWin32Error(); if(code!=LocalErrors.ErrorAlreadyExists) throw Native(code); if(exclusive) throw new PathOccupiedException(path,Side.Local);
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
    public Task<Recovery?> RecoverAsync(IdentityLocation actual,Entry version,CancellationToken ct)=>LocalRecovery.RecoverAsync(actual,version,scope,ct);
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
}
