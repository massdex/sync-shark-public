namespace Massdex.SyncShark.Engine.Run;

/// <summary>External credentials, independently owned Dropbox clients and optional caller-owned recovery.</summary>
internal sealed record EngineExternals(IDropboxRefreshTokenStore Credentials,
    Func<TimeSpan, HttpClient> CreateDropboxClient, ILocalRecovery? Recovery = null);
