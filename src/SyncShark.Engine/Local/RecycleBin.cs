using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Runs the native IFileOperation recycle protocol on an STA thread and classifies its exact callbacks.</summary>
public sealed class RecycleBin
{
    private const string ShellLibrary = "shell32.dll";
    private const string FileOperationClassId = "3ad05575-8857-4850-9277-11b85bdb8e09";
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;

    private static (RemovalResult? Result, string? BinLocator) RunShell(string fullPath, LocalSide.HeldInfo held, Entry expected)
    {
        (RemovalResult?, string?) outcome = default;
        Exception? failure = null;
        var thread = new Thread(() => {
            try { outcome = SendToShell(fullPath, held, expected); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new ItemFailedException(fullPath, TextKey.RecycleFailed, failure);
        return outcome;
    }

    private static (RemovalResult? Result, string? BinLocator) SendToShell(string fullPath,
        LocalSide.HeldInfo held, Entry expected)
    {
        IFileOperation? operation = null;
        var item = IntPtr.Zero;
        var sink = new DeleteSink(fullPath, held, expected);
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid(FileOperationClassId), throwOnError: true)!)!;
            CheckResult(operation.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION |
                FOF_NOERRORUI | FOF_SILENT | FOFX_RECYCLEONDELETE), fullPath);
            var iid = new Guid(IShellItem.InterfaceId);
            CheckResult(SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iid, out item), fullPath);
            CheckResult(operation.DeleteItem(item, sink), fullPath);
            int aggregate;
            try { aggregate = operation.PerformOperations(); }
            catch (Exception error)
            {
                return (new RemovalResult(RemovalOutcome.Unknown, null,
                    LocalErrors.Cause(error, TextKey.RecycleFailed)), null);
            }
            return Classify(operation, sink, aggregate);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }

    // The sink's own veto is the only definite pre-effect outcome. A specific per-item HRESULT outranks the
    // aggregate PerformOperations/FinishOperations result, and an abort never becomes an in-use cause. A named
    // receipt that must be checked against the held identity is left to the caller (null result), because reading
    // its content is asynchronous.
    private static (RemovalResult? Result, string? BinLocator) Classify(IFileOperation operation, DeleteSink sink,
        int aggregate)
    {
        if (sink.VetoCause is { } veto)
            return (new RemovalResult(RemovalOutcome.NoEffect, null, veto), null);
        if (sink.ItemResult < 0)
            return (new RemovalResult(RemovalOutcome.Unknown, null, CauseOf(sink.ItemResult)), null);
        if (aggregate < 0)
            return (new RemovalResult(RemovalOutcome.Unknown, null, CauseOf(aggregate)), null);
        if (sink.FinishResult < 0)
            return (new RemovalResult(RemovalOutcome.Unknown, null, CauseOf(sink.FinishResult)), null);
        if (operation.GetAnyOperationsAborted(out var aborted) < 0 || aborted)
            return (new RemovalResult(RemovalOutcome.Unknown, null, TextKey.RecycleFailed), null);
        if (!sink.Received) return (new RemovalResult(RemovalOutcome.Unknown, null, null), null);
        if (sink.ReceiptPath is null) return (new RemovalResult(RemovalOutcome.Unknown, null, null), null);
        return (null, sink.ReceiptPath);
    }

    private static void CheckResult(int result, string path)
    {
        if (result < 0)
            throw new ItemFailedException(path, CauseOf(result), Marshal.GetExceptionForHR(result));
    }

    private static TextKey CauseOf(int result) =>
        LocalErrors.Cause(Marshal.GetExceptionForHR(result) ?? new COMException(string.Empty, result),
            TextKey.RecycleFailed);

    [DllImport(ShellLibrary, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        ref Guid interfaceId, out IntPtr item);

    private readonly Func<Task<bool>>? arm;
    private readonly PathScope? scope;
    public RecycleBin() { }
    internal RecycleBin(Func<Task<bool>> arm,PathScope scope) { this.arm=arm; this.scope=scope; }

    /// <summary>Remove only the selected version; the caller with a store supplies the arm callback.</summary>
    public async Task<RemovalResult> RemoveAsync(string root,DriveType driveType,Entry expected,RemovalSelection? selection,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(arm is null || selection?.Proof.Expected!=expected) throw new ChangedUnderneathException(expected.Path);
        var full=LocalSide.Full(root,expected.Path);
        bool managed=driveType==DriveType.Removable;
        bool metadata=expected.IsFolder || expected.Skip==nameof(TextKey.SkipOnlineOnly);
        uint access=LocalSide.ReadAttributes | (managed? LocalSide.WriteAttributes|LocalSide.DeleteAccess:0);
        using var held=LocalSide.Open(full,access,managed?FileShare.Read:FileShare.Read|FileShare.Delete);
        var selected=LocalSide.Verify(held,expected,selection!.Proof.VolumeId,allowPlaceholder:metadata);
        // Retain the writer-denying data handle through arm, shell effect and receipt validation.
        LocalSide.HeldInfo verified=selected;
        using var bytesGuard=metadata?null:LocalSide.ReadContent(held,full,FileShare.Read|FileShare.Delete,out verified);
        if(bytesGuard is not null)
        {
            if(verified.Size!=expected.Size || verified.Modified!=expected.Modified) throw new ChangedUnderneathException(expected.Path);
            string hash=await LocalSide.Digest(bytesGuard,null,ct);
            if(!StringComparer.OrdinalIgnoreCase.Equals(hash,selection.Proof.ContentHash))
                return RemovalResult.Refused(new ChangedUnderneathException(expected.Path){ObservedLocal=expected with {ContentHash=hash}});
        }
        ct.ThrowIfCancellationRequested(); if(!await arm()) return new(RemovalOutcome.Pending,null,null);
        string? receipt;
        if(managed)
        {
            var volume=Path.GetPathRoot(full)!; string bin=Path.Combine(volume,ResourceNames.RootBinFolder);
            using var parent=new LocalSide.ParentHandles(volume);
            Directory.CreateDirectory(bin);
            using var target=LocalSide.Open(bin,LocalSide.ReadAttributes|0x20,FileShare.ReadWrite);
            var directory=LocalSide.Info(target);
            if(LocalSide.IsLink(directory.Attributes)) return new(RemovalOutcome.NoEffect,null,TextKey.RecycleUnavailable);
            if(!directory.Folder) throw new ChangedUnderneathException(bin);
            for(int ordinal=1;;ordinal=checked(ordinal+1))
            {
                var current=LocalSide.Info(held);
                if(!LocalSide.Matches(current,expected,selection.Proof.VolumeId,LocalSide.Classify(held,current.Attributes),metadata))
                    return new(RemovalOutcome.NoEffect,null,TextKey.FailChanged);
                if(ordinal==1) File.SetCreationTimeUtc(held,DateTime.UtcNow);
                var name=BinName(Path.GetFileName(full),expected.IsFolder,ordinal);
                try { LocalSide.Rename(held,target,name); receipt=Path.Combine(bin,name); break; }
                catch(PathOccupiedException) { ct.ThrowIfCancellationRequested(); }
            }
        }
        else
        {
            var shell=RunShell(full,selected,expected);
            if(shell.Result is not null) return shell.Result;
            receipt=shell.BinLocator;
        }
        return await ObserveReceipt(receipt,selected,selection.Proof,ct);
    }

    private async Task<RemovalResult> ObserveReceipt(string? receipt,LocalSide.HeldInfo selected,RemovalProof proof,CancellationToken ct)
    {
        var expected=proof.Expected;
        if(receipt is null) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
        try
        {
            using var actualParents=new LocalSide.ParentHandles(Path.GetDirectoryName(receipt)!);
            using var observed=LocalSide.Open(receipt,LocalSide.ReadAttributes,FileShare.ReadWrite|FileShare.Delete);
            var info=LocalSide.Info(observed);
            var classification=LocalSide.Classify(observed,info.Attributes);
            bool same=info.Identity==selected.Identity && LocalSide.Matches(info,expected,proof.VolumeId,classification,true);
            if(!same && classification is not null) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
            string hash=same?proof.ContentHash??"":"";
            string? skip=classification?.ToString();
            if(!same && !info.Folder && skip is null)
                return await ReadSubstituteReceiptAsync(new(info.Domain,receipt,info.Entry(expected.Path,"")),observed,ct);
            var actual=info.Entry(expected.Path,hash) with {Skip=skip};
            return new(same?RemovalOutcome.Removed:RemovalOutcome.Retained,null,same?null:TextKey.FailChanged)
            {
                RecycledVictim=new(info.Domain,receipt,actual),
                Recycled=info.Folder && scope is not null ? LocalSide.ReceiptContents(receipt,info,expected.Path,scope,ct) : null
            };
        }
        catch(Exception e) when(LocalErrors.IsFileFailure(e) || e is OperationCanceledException or ChangedUnderneathException)
        { return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked); }
    }
    /// <summary>Verify the substitute named by a shell receipt and return the recoverable file version, never a selected removal.</summary>
    private static async Task<RemovalResult> ReadSubstituteReceiptAsync(IdentityLocation first,SafeFileHandle metadata,CancellationToken ct)
    {
        using var bytes=LocalSide.ReadContent(metadata,first.NativePath,FileShare.Read|FileShare.Delete,out var held);
        if(held.Domain!=first.Domain || held.Id!=first.Entry.Id) return new(RemovalOutcome.Unknown,null,TextKey.RecycleUnchecked);
        var hash=await LocalSide.Digest(bytes,null,ct);
        return new(RemovalOutcome.Retained,null,TextKey.FailChanged) { RecycledVictim=first with { Entry=held.Entry(first.Entry.Path,hash) } };
    }
    /// <summary>Put the identified bin object back under its original name, without replacing a live binding.</summary>
    internal static async Task<RemovalResult> RestoreRecycledAsync(IdentityLocation actual,Entry version,string originalPath,string root,DirectoryIdentity bound,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var src=actual.NativePath; var dest=LocalSide.Full(root,originalPath);
        if(!Path.IsPathFullyQualified(src) || actual.Domain!=LocalSide.Domain(bound) || actual.Entry.Id!=version.Id || actual.Entry.IsFolder!=version.IsFolder ||
            version.Skip is not null || actual.Entry.Skip is not null || !version.IsFolder && !ContentHash.IsValid(version.ContentHash)) throw new ChangedUnderneathException(originalPath);
        var volume=Path.GetPathRoot(src)!;
        if(PathNames.RelativeUnderLocalRoot(Path.Combine(volume,ResourceNames.RootBinFolder),src) is not {Length:>0} &&
            PathNames.RelativeUnderLocalRoot(Path.Combine(volume,"$Recycle.Bin"),src) is not {Length:>0}) throw new ChangedUnderneathException(originalPath);
        using var target=LocalSide.Parents(root,bound,dest); using var source=new LocalSide.ParentHandles(Path.GetDirectoryName(src)!);
        using var held=LocalSide.Open(src,LocalSide.DeleteAccess|LocalSide.ReadAttributes,FileShare.Read);
        var info=LocalSide.Verify(held,version,actual.Domain);
        using var bytes=info.Folder?null:LocalSide.ReadContent(held,src,FileShare.Read|FileShare.Delete,out info);
        if(bytes is not null && (info.Size!=version.Size || info.Modified!=version.Modified ||
            !StringComparer.OrdinalIgnoreCase.Equals(await LocalSide.Digest(bytes,null,ct),version.ContentHash))) throw new ChangedUnderneathException(originalPath);
        ct.ThrowIfCancellationRequested(); LocalSide.Rename(held,target.Last,Path.GetFileName(dest));
        return LocalSide.Publication(info.Entry(originalPath,version.ContentHash),null);
    }
    /// <summary>A taken name is suffixed before its file extension; a folder keeps its whole name.</summary>
    private static string BinName(string name,bool folder,int ordinal)=>ordinal==1?name:
        (folder?name:Path.GetFileNameWithoutExtension(name))+" ("+ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)+")"+(folder?"":Path.GetExtension(name));

    /// <summary>Remove only expired top-level items in a participating removable volume's plain bin.</summary>
    public Task TrimAsync(string root,DriveType driveType,DateTimeOffset now,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(driveType!=DriveType.Removable) return Task.CompletedTask;
        var bin=Path.Combine(Path.GetPathRoot(Path.GetFullPath(root))!,ResourceNames.RootBinFolder);
        ItemFailedException? failure=null;
        try
        {
            using var parent=new LocalSide.ParentHandles(bin);
            if(LocalSide.IsLink(LocalSide.Info(parent.Last).Attributes)) return Task.CompletedTask;
            foreach(var path in Directory.EnumerateFileSystemEntries(bin)) Trim(path,now.UtcDateTime.AddDays(-30),ct,ref failure);
        }
        catch(IOException e) when((e.HResult&0xffff) is 2 or 3) { }
        catch(ChangedUnderneathException) { /* A linked or non-directory bin is never entered. */ }
        if(failure is not null) throw failure;
        return Task.CompletedTask;
    }
    private static void Trim(string path,DateTime? cutoff,CancellationToken ct,ref ItemFailedException? failure)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var held=LocalSide.Open(path,LocalSide.ReadAttributes|LocalSide.DeleteAccess,FileShare.ReadWrite);
            // Denying delete sharing pins this exact name while a directory is enumerated; leaves are deleted by handle.
            if(cutoff is {} threshold && File.GetCreationTimeUtc(held)>=threshold) return;
            var info=LocalSide.Info(held);
            if(info.Folder && !LocalSide.IsLink(info.Attributes))
                foreach(var child in Directory.EnumerateFileSystemEntries(path)) Trim(child,null,ct,ref failure);
            LocalSide.Discard(held);
        }
        catch(IOException error) when((error.HResult&0xffff) is 2 or 3) { /* Already gone. */ }
        catch(Exception error) when(error is IOException or Win32Exception or UnauthorizedAccessException or ChangedUnderneathException)
        { failure ??= new ItemFailedException(path,LocalErrors.Cause(error,TextKey.RecycleFailed),error); }
    }
}
