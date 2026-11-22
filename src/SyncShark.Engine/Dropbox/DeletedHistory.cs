namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DeletedHistory operations for the dropbox module.</summary>
public sealed class DeletedHistory
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<DeletionEvidence> ReadAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct) => throw new NotImplementedException();
}
