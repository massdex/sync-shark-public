namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox listing and single-item read</summary>
public sealed class DropboxListing
{
    /// <summary>Read operation on the supplied facts.</summary>
    public IAsyncEnumerable<Listed> ReadAsync(DropboxApi api, PairConfig pair, Func<PathScope> resolvedScope, string under, ListOptions options, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>ReadOneAsync for the selected DropboxListing operation.</summary>
    public Task<Entry?> ReadOneAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadByIdAsync for the selected DropboxListing operation.</summary>
    public Task<IdentityLocation?> ReadByIdAsync(DropboxApi api, PairConfig pair, string id, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>CaptureCursorAsync for the selected DropboxListing operation.</summary>
    public Task<string> CaptureCursorAsync(DropboxApi api, PairConfig pair, ListOptions options, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadChangePageAsync for the selected DropboxListing operation.</summary>
    public Task<ChangePage> ReadChangePageAsync(DropboxApi api, PairConfig pair, string cursor, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Respelled for the selected DropboxListing operation.</summary>
    public static Entry Respelled(Entry entry, string requestedPath) => throw new NotImplementedException();
    /// <summary>VerifiedById for the selected DropboxListing operation.</summary>
    public static IdentityLocation VerifiedById(Entry entry, string id, PairConfig pair) => throw new NotImplementedException();
    /// <summary>List descendants of the selected folder identity.</summary>
    public IAsyncEnumerable<Listed> ReadFolderAsync(DropboxApi api, PairConfig pair, string folderId, ListOptions options, CancellationToken ct) => throw new NotImplementedException();
}
