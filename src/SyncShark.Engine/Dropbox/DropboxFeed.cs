namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DropboxFeed operations for the dropbox module.</summary>
public sealed class DropboxFeed
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<ChangePage> ReadAsync(DropboxApi api, PairConfig pair, string? cursor, CancellationToken ct) => throw new NotImplementedException();
}
