namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The protected Dropbox credential for a retained connection could not be read; its pairs pause and keep their files.</summary>
public sealed class DropboxCredentialUnavailableException(string connectionId)
    : Exception(Text.Get(TextKey.AccountMissing, (TextArg.Account, connectionId)))
{
    /// <summary>The retained connection whose credential is missing.</summary>
    public string ConnectionId { get; } = connectionId;
}
