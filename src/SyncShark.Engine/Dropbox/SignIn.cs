namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>SignIn operations for the dropbox module.</summary>
public sealed class SignIn
{
    /// <summary>Receives the browser return on loopback; token exchange and account requests go through DropboxApi.</summary>
    public Task<string> ConnectAsync(DropboxApi api, CancellationToken ct) => throw new NotImplementedException();
}
