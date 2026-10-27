using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>
/// The tray's one host send: the production client's method, or a controlled transport in the fixture.
/// A null reply means the connection deadline expired before any bytes were sent.
/// </summary>
internal delegate Task<AutoSyncHostReply?> TrayHostSend(AutoSyncHostRequest request, TimeSpan connectionTimeout,
    CancellationToken cancellationToken);
