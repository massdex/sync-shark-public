namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox listing and single-item read</summary>
public sealed class DropboxListing
{
    /// <summary>Read operation on the supplied facts.</summary>
    public IAsyncEnumerable<Listed> ReadAsync(DropboxApi api, PairConfig pair, string under, ListOptions options, CancellationToken ct) => throw new NotImplementedException();
}
