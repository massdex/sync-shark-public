using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Dropbox provider facts as named constants and the sole provider-wait applicability decoder
/// (section 5.5; READING 8.5).</summary>
public static class DropboxProtocol
{
    // Routes.
    public const string ListFolder = "files/list_folder";
    public const string ListFolderContinue = "files/list_folder/continue";
    public const string ListFolderGetLatestCursor = "files/list_folder/get_latest_cursor";
    public const string GetMetadata = "files/get_metadata";
    public const string Download = "files/download";
    public const string Upload = "files/upload";
    public const string UploadSessionStart = "files/upload_session/start";
    public const string UploadSessionAppend = "files/upload_session/append_v2";
    public const string UploadSessionFinish = "files/upload_session/finish";
    public const string CreateFolder = "files/create_folder_v2";
    public const string Delete = "files/delete_v2";
    public const string DeleteBatch = "files/delete_batch";
    public const string DeleteBatchCheck = "files/delete_batch/check";
    public const string Move = "files/move_v2";
    public const string ListFolderLongpoll = "files/list_folder/longpoll";
    public const string ListRevisions = "files/list_revisions";
    public const string GetCurrentAccount = "users/get_current_account";
    public const string UploadSessionFinishBatch = "files/upload_session/finish_batch_v2";

    // Requested history depth: the deletion-evidence reader consumes only the newest retained revision.
    internal const int DeletionHistoryHeadLimit = 1;

    // Documented limits.
    public const int ListPageMaximum = 2000;

    // Error summary prefixes.
    /// <summary>Separates the tags inside a 409 error_summary.</summary>
    public const char ErrorSummarySeparator = '/';
    public const string PathNotFound = "path/not_found";
    public const string PathConflict = "path/conflict";
    public const string PathConflictFolder = "path/conflict/folder";
    public const string PathLookupNotFound = "path_lookup/not_found";
    public const string FromLookupNotFound = "from_lookup/not_found";
    public const string ToConflict = "to/conflict";
    public const string PathLookup = "path_lookup";
    public const string PathWrite = "path_write";
    public const string TooManyWriteOperations = "too_many_write_operations";
    public const string TooManyRequests = "too_many_requests";
    public const string TooManyFiles = "too_many_files";
    // async.PollError on files/delete_batch/check: an invalid job ID blocks visibly until a later check.
    public const string InvalidAsyncJobId = "invalid_async_job_id";

    // Write-error summary locations by route: upload, upload_session/finish and create_folder_v2 report under
    // path/, delete_v2 under path_write/, move_v2 under to/ (destination) and from_write/ (source).
    public const string UploadWritePrefix = "path/";
    public const string DeleteWritePrefix = "path_write/";
    public const string MoveToWritePrefix = "to/";
    public const string MoveFromWritePrefix = "from_write/";
    // Documented write-error reasons reported under those locations.
    public const string MalformedPath = "malformed_path";
    public const string DisallowedName = "disallowed_name";
    public const string NoWritePermission = "no_write_permission";
    public const string InsufficientSpace = "insufficient_space";
    public const string Conflict = "conflict";
    public const string FileAncestor = "file_ancestor";

    // Headers and values.
    public const string PathRootHeader = "Dropbox-API-Path-Root";
    public const string ArgHeader = "Dropbox-API-Arg";
    public const string RetryAfterHeader = "Retry-After";
    public const string BearerScheme = "Bearer";
    public const string RevisionPrefix = "rev:";
    public const string ClientModifiedFormat = "yyyy-MM-ddTHH:mm:ssZ";

    // JSON field names and tags.
    public const string ErrorSummary = "error_summary";
    public const string Error = "error";
    public const string Reason = "reason";
    public const string Tag = ".tag";
    public const string TagFile = "file";
    public const string TagFolder = "folder";
    public const string TagDeleted = "deleted";
    // https://docs.dropboxapi.com/dropbox-api/docs/detecting-changes: expired cursors return HTTP 409 reset.
    public const string TagReset = "reset";
    public const string TagSuccess = "success";
    public const string TagFailure = "failure";
    public const string TagComplete = "complete";
    public const string TagFailed = "failed";
    public const string TagInProgress = "in_progress";
    public const string TagOther = "other";
    public const string Entries = "entries";
    public const string Cursor = "cursor";
    public const string HasMore = "has_more";
    public const string Metadata = "metadata";
    public const string Id = "id";
    public const string Rev = "rev";
    public const string Size = "size";
    public const string ClientModified = "client_modified";
    public const string ContentHash = "content_hash";
    public const string PathDisplay = "path_display";
    public const string IsDownloadable = "is_downloadable";
    public const string SessionId = "session_id";
    public const string AsyncJobId = "async_job_id";
    public const string Path = "path";
    public const string IncludeDeleted = "include_deleted";
    public const string Changes = "changes";
    public const string Backoff = "backoff";
    public const string IsDeleted = "is_deleted";
    public const string ServerDeleted = "server_deleted";
    public const string AccountId = "account_id";
    public const string RootInfo = "root_info";
    public const string RootNamespaceId = "root_namespace_id";
    public const string HomeNamespaceId = "home_namespace_id";
    public const string RetryAfter = "retry_after";

    // Tags and fields written in arguments and headers ({".tag":"root","root":nsid}; mode add / {".tag":"update","update":rev}).
    public const string Root = "root";
    public const string ModeAdd = "add";
    public const string ModeUpdate = "update";
    public const string OctetStream = "application/octet-stream";

    /// <summary>The provider wait encoded by a reply, or null when it supplied none: the larger of a
    /// nonnegative Retry-After header and a nonnegative RateLimitError.retry_after applies.
    /// A missing or malformed value selects no wait, not a default.</summary>
    public static TimeSpan? ProviderWait(TimeSpan? retryAfter, JsonElement error)
    {
        var header = retryAfter is { } supplied && supplied >= TimeSpan.Zero ? supplied : (TimeSpan?)null;
        if (error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty(RetryAfter, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var seconds) ||
            seconds < 0 ||
            seconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
            return header;
        var body = TimeSpan.FromSeconds(seconds);
        return header is { } first && first > body ? first : body;
    }

    /// <summary>A provider-wait floor and the widest scope its source can prove (applicability table rows B and C).</summary>
    internal readonly record struct ProviderConstraint(
        (string AccountId, string? RouteName, string? SourceIdentity) Scope,
        long StartedTimestamp, TimeSpan Duration);

    /// <summary>The account-wide floor for any supplied wait (applicability table row B).</summary>
    internal static ProviderConstraint? ProviderConstraintFor(
        string accountId, TimeSpan? wait, long startedTimestamp) =>
        wait is { } supplied ? new((accountId, null, null), startedTimestamp, supplied) : null;

    /// <summary>The longpoll cursor floor for an accepted result.backoff (applicability table row C).</summary>
    internal static ProviderConstraint? LongpollConstraintFor(
        string accountId, string sourceIdentity, TimeSpan? backoff, long startedTimestamp) =>
        backoff is { } supplied
            ? new((accountId, ListFolderLongpoll, sourceIdentity), startedTimestamp, supplied) : null;

    /// <summary>The stable source key a longpoll floor is bound to (applicability table row C).</summary>
    internal static string SourceIdentity(SourceProvenance provenance) =>
        JsonSerializer.Serialize(new object[]
        {
            provenance.AccountId, provenance.NamespaceId, provenance.RootIdentity, provenance.ConfiguredRoot,
            provenance.Recursive, provenance.IncludeDeleted, provenance.IncludeMountedFolders,
            provenance.IncludeNonDownloadableFiles
        });
    // PairGeneration and SourceEpoch are deliberately excluded.

    /// <summary>The provider's tagged-union argument shape: {".tag": kind, kind: value}, in that order.</summary>
    internal static Dictionary<string, string> TaggedUnion(string kind, string value) => new()
    {
        [Tag] = kind,
        [kind] = value
    };
}
