namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DeletedHistory operations for the dropbox module.</summary>
public sealed class DeletedHistory
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<DeletionEvidence> ReadAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read exact file revision evidence or folder ancestor history according to the selected kind.</summary>
    public Task<DeletionEvidence> ReadAsync(DropboxApi api, PairConfig pair, string path, bool folder, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>DeletionTime for the selected DeletedHistory operation.</summary>
    public static (DateTimeOffset? Value, TextKey? Reason) DeletionTime(string? value, bool wrongType) => throw new NotImplementedException();
    /// <summary>DeletionHash for the selected DeletedHistory operation.</summary>
    public static (string? Value, TextKey? Reason) DeletionHash(string? value, bool wrongType) => throw new NotImplementedException();
}
