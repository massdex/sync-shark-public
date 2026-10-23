namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    public event Action? AuthorizationReturnRequested;
    private DropboxLoopbackReceiver CreateAuthorizationReceiver() =>
        AuthorizationReturnRequested is null ? new() : new(() => AuthorizationReturnRequested?.Invoke());
}
