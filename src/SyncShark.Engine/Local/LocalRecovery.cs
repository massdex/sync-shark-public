using System.Runtime.CompilerServices;
using static Massdex.SyncShark.Engine.Local.LocalNative;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Reads a retained local version back by identity, under held handles, for recovery custody.</summary>
internal static class LocalRecovery
{
    // The scope is a supplier: only a folder receipt evaluates it, exactly as before the split.
    internal static async Task<Recovery?> RecoverAsync(IdentityLocation actual,Entry version,Func<PathScope> scope,CancellationToken ct)
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
            var digest=await LocalSide.Digest(bytes,null,ct);
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
}
