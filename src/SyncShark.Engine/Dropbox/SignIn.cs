namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>SignIn operations for the dropbox module.</summary>
public sealed class SignIn
{
    /// <summary>Receives the browser return on loopback; token exchange and account requests go through DropboxApi. Use the catalog acknowledgement on receipt, not a success claim; do not echo callback values.</summary>
    public Task<string> ConnectAsync(DropboxApi api, CancellationToken ct) => throw new NotImplementedException();
}
