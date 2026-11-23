namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox provider facts as named constants and the sole provider-wait applicability decoder</summary>
public static class DropboxProtocol
{
    public const string Path = "path";
    public const string ClientModifiedFormat = "yyyy-MM-ddTHH:mm:ssZ";
    public const string Upload = "files/upload";
    public const string UploadSessionStart = "files/upload_session/start";
    public const string UploadSessionAppend = "files/upload_session/append_v2";
    public const string UploadSessionFinish = "files/upload_session/finish";
    public const string CreateFolder = "files/create_folder_v2";
    public const string CreateFolderBatch = "files/create_folder_batch";
    public const string Delete = "files/delete_v2";
    public const string DeleteBatch = "files/delete_batch";
    public const string Move = "files/move_v2";
    public const string MoveBatch = "files/move_batch_v2";
    public const string UploadSessionFinishBatch = "files/upload_session/finish_batch_v2";
    public const int DeletionHistoryHeadLimit = 1;
    public const string ListFolder = "files/list_folder";
    public const string ListFolderContinue = "files/list_folder/continue";
    public const string ListFolderGetLatestCursor = "files/list_folder/get_latest_cursor";
    public const string GetMetadata = "files/get_metadata";
    public const string ListRevisions = "files/list_revisions";
    public const int ListPageMaximum = 2000;
    internal const string AuthorizationPath = "oauth2/authorize";
    internal const string TokenPath = "oauth2/token";
    internal const string AuthorizationHost = "www.dropbox.com";
    internal const string TokenHost = "api.dropboxapi.com";

    /// <summary>Parse operation on the supplied facts.</summary>
    public static System.Text.Json.JsonDocument Parse(Stream response) => throw new NotImplementedException();

    /// <summary>Compare the header wait with error.retry_after from the whole reply envelope; use default for a header-only reply.</summary>
    public static TimeSpan? ProviderWait(TimeSpan? retryAfter, System.Text.Json.JsonElement envelope) => throw new NotImplementedException();
    /// <summary>ProviderConstraintFor for the selected DropboxProtocol operation.</summary>
    internal static ProviderConstraint? ProviderConstraintFor( string accountId, TimeSpan? wait, long startedTimestamp) => throw new NotImplementedException();
    /// <summary>LongpollConstraintFor for the selected DropboxProtocol operation.</summary>
    internal static ProviderConstraint? LongpollConstraintFor( string accountId, string sourceIdentity, TimeSpan? backoff, long startedTimestamp) => throw new NotImplementedException();
    /// <summary>Provider wait scoped to the account, route or change source.</summary>
    internal readonly record struct ProviderConstraint(
        (string AccountId, string? RouteName, string? SourceIdentity) Scope,
        long StartedTimestamp, TimeSpan Duration);
}
