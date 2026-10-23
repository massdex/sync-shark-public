namespace Massdex.SyncShark;

/// <summary>The protected Dropbox credential for a retained connection could not be read (design §1.3).</summary>
public sealed class DropboxCredentialUnavailableException(string connectionId)
    : Exception($"The protected Dropbox credential for connection {connectionId} is unavailable.")
{
    /// <summary>The retained connection whose credential is missing.</summary>
    public string ConnectionId { get; } = connectionId;
}
