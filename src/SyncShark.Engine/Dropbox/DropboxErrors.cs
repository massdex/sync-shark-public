using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

// One provider-error owner: every member translates a Dropbox failure (refusal, transport, decode or structured upload union) into the project's exception or text-key vocabulary, so no caller interprets raw provider errors.
/// <summary>The 409 mapping of section 5.4, by error_summary prefix. A transport error with no status is an item
/// failure (DropboxRefused).</summary>
public static partial class DropboxErrors
{
    // The failure union's field names, readable only through this owner (F25).
    internal static string ErrorName => DropboxProtocol.Error;
    internal static string ReasonName => DropboxProtocol.Reason;

    /// <summary>Whether the provider requires a fresh listing instead of continuing this cursor.</summary>
    internal static bool IsCursorReset(Exception error) =>
        error is DropboxApiException { StatusCode: HttpStatusCode.Conflict } refusal &&
        UnionTag(DropboxApi.Failure(refusal)) == DropboxProtocol.TagReset;

    /// <summary>A mapped provider refusal: the route's documented absence, or the exception the caller throws.</summary>
    public readonly struct MapResult
    {
        private readonly Exception? _error;
        private readonly bool _absent;

        private MapResult(Exception? error, bool absent) => (_error, _absent) = (error, absent);

        /// <summary>The route's documented reply proves the target is absent; nothing is thrown.</summary>
        public static MapResult Absent => new(null, true);

        /// <summary>The refusal to throw.</summary>
        public static MapResult Failure(Exception error) => new(error, false);

        /// <summary>True only for the route's documented absence; an uninitialised result is never absence.</summary>
        public bool IsAbsent => _absent;

        /// <summary>The refusal to throw; an absence or uninitialised result carries none and throws here.</summary>
        public Exception Error => _error ?? throw new InvalidOperationException(nameof(MapResult));
    }

    /// <summary>The root-read cause for a classified provider read failure, or null when the error is not one.</summary>
    public static TextKey? RootReadCause(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is not (ChangedUnderneathException or HttpRequestException or IOException or JsonException
            or InventoryIncompleteException or ProviderTransportTimeoutException)) return null;
        return error switch
        {
            ChangedUnderneathException => TextKey.RootDropboxFolderChanged,
            JsonException or InventoryIncompleteException => TextKey.RootDropboxIdentityInvalid,
            DropboxApiException => TextKey.RootDropboxAccessUnavailable,
            ProviderTransportTimeoutException or HttpRequestException or IOException =>
                TextKey.RootDropboxTransportUnavailable,
            _ => TextKey.RootUnavailable
        };
    }

    /// <summary>Maps a 409 or transport failure for route to the refusal the caller throws, or the route's
    /// documented absence. Only the two routes below may answer absence.</summary>
    public static MapResult Map(string route, HttpRequestException error, bool expectedIsNull, string path)
    {
        if (error is not DropboxApiException { StatusCode: HttpStatusCode.Conflict } api)
            return MapResult.Failure(Refused(path, error));
        bool Matches(string prefix) => api.SafeReason.StartsWith(prefix, StringComparison.Ordinal);
        return route switch
        {
            DropboxProtocol.GetMetadata when Matches(DropboxProtocol.PathNotFound) => MapResult.Absent,
            DropboxProtocol.CreateFolder when Matches(DropboxProtocol.PathConflictFolder) => MapResult.Absent,
            DropboxProtocol.Download when Matches(DropboxProtocol.PathNotFound) =>
                MapResult.Failure(new ChangedUnderneathException(path, error)),
            DropboxProtocol.Delete when Matches(DropboxProtocol.PathLookupNotFound) =>
                MapResult.Failure(new ChangedUnderneathException(path, error)),
            DropboxProtocol.Move when Matches(DropboxProtocol.FromLookupNotFound) =>
                MapResult.Failure(new ChangedUnderneathException(path, error)),
            DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish when Matches(DropboxProtocol.PathConflict) =>
                MapResult.Failure(expectedIsNull ? new PathOccupiedException(path, inner: error) : new ChangedUnderneathException(path, error)),
            DropboxProtocol.CreateFolder when Matches(DropboxProtocol.PathConflict) =>
                MapResult.Failure(new PathOccupiedException(path, inner: error)),
            DropboxProtocol.Move when Matches(DropboxProtocol.ToConflict) =>
                MapResult.Failure(new PathOccupiedException(path, inner: error)),
            _ => MapResult.Failure(new ItemFailedException(path, WriteCause(route, api.SafeReason), error))
        };
    }

    /// <summary>Maps one structured upload failure — the UploadError or UploadSessionFinishError union — to
    /// the exception the caller settles and the write-throttle fact reported once per response. The path
    /// member nests a WriteError on session finish but carries the UploadWriteFailed reason inline on direct
    /// upload; malformed shape is a decode failure while an unknown well-shaped tag is an ordinary refusal.</summary>
    internal static (Exception Error, bool WriteThrottled) MapUploadFailure(
        JsonElement failure, bool expectedIsNull, string path, DropboxApiException? response = null)
    {
        var tag = RequiredTag(failure);
        Exception Evidence(string value) => new ProviderFailureException(
            response?.Route ?? DropboxProtocol.UploadSessionFinishBatch,
            (int?)response?.StatusCode, value);
        if (!tag.Equals(DropboxProtocol.Path, StringComparison.Ordinal))
            return (new ItemFailedException(path, TextKey.DropboxRefused, Evidence(tag)),
                tag.Equals(DropboxProtocol.TooManyWriteOperations, StringComparison.Ordinal));

        var writeTag = RequiredTag(WriteError(failure) ?? throw new JsonException());
        if (writeTag.Equals(DropboxProtocol.Conflict, StringComparison.Ordinal))
            return (expectedIsNull ? new PathOccupiedException(path, inner: Evidence(writeTag)) :
                new ChangedUnderneathException(path, Evidence(writeTag)), false);
        if (writeTag.Equals(DropboxProtocol.TooManyWriteOperations, StringComparison.Ordinal))
            return (new ItemFailedException(path, TextKey.DropboxRefused, Evidence(writeTag)), true);
        return (new ItemFailedException(path, CauseOf(writeTag), Evidence(writeTag)), false);
    }

    /// <summary>True only for the structured refusals MapUploadFailure itself produces, which may reset the commit
    /// streak once; a write throttle takes precedence over reset eligibility. Generic DropboxRefused, unknown tags,
    /// malformed receipts, summary fallbacks, transport failures and cancellations are not classified and never reset.</summary>
    internal static bool IsClassifiedUploadFailure(Exception error) => error switch
    {
        PathOccupiedException or ChangedUnderneathException => true,
        ItemFailedException
        {
            Cause: TextKey.FailDropboxNameRejected or TextKey.FailDropboxNoPermission or TextKey.FailDropboxNoSpace
        } => true,
        _ => false
    };

    /// <summary>Maps one folder submission member's failure at its original wire ordinal. Without specifically proved
    /// no-effect evidence the member is Unknown, which grants neither success nor replay.</summary>
    internal static FolderMemberResult MapFolderFailure(JsonElement failure, int ordinal) =>
        new FolderMemberResult.Unknown(ordinal, FolderCause(failure));

    /// <summary>True only for the exact structured invalid_async_job_id refusal, which blocks visibly and is checked
    /// again at the next Repair or AutoSync opportunity; no expired tag or time-based release.</summary>
    internal static bool IsInvalidFolderJob(DropboxApiException error) =>
        DropboxApi.Failure(error) is { ValueKind: JsonValueKind.Object } failure &&
        failure.TryGetProperty(DropboxProtocol.Tag, out var tag) &&
        tag.ValueKind == JsonValueKind.String &&
        tag.ValueEquals(DropboxProtocol.InvalidAsyncJobId);

    /// <summary>True only for the documented structured 409 path/not_found union; a summary-prefix match cannot
    /// prove it, so a caller that needs the exact miss uses this. Callers decide what a miss means for their
    /// endpoint.</summary>
    internal static bool IsStructuredPathNotFound(DropboxApiException error)
    {
        var failure = DropboxApi.Failure(error);
        return error.StatusCode == HttpStatusCode.Conflict && failure.ValueKind == JsonValueKind.Object &&
            failure.TryGetProperty(DropboxProtocol.Tag, out var tag) && tag.ValueKind == JsonValueKind.String &&
            tag.ValueEquals(DropboxProtocol.Path) &&
            failure.TryGetProperty(DropboxProtocol.Path, out var lookup) && lookup.ValueKind == JsonValueKind.Object &&
            lookup.TryGetProperty(DropboxProtocol.Tag, out var reason) && reason.ValueKind == JsonValueKind.String &&
            IsNotFoundTag(reason.GetString());
    }

    /// <summary>True only for a files/list_revisions refusal that is history-only once the ordinary retry owner
    /// has finished: an exhausted transient provider failure. Auth, permission and path-root refusals and the
    /// documented path/not_found (NoRetainedHistory) never qualify, and no other route is history.</summary>
    internal static bool IsHistoryOnlyFailure(string route, DropboxApiException error) =>
        route == DropboxProtocol.ListRevisions && !IsStructuredPathNotFound(error) &&
        ProviderTransientFailure.IsTransient(error);

    /// <summary>True when a lookup tag is the provider's bare not_found token; the bare tag has no named
    /// constant, so it is the documented summary suffix of the existing path/not_found prefix.</summary>
    private static bool IsNotFoundTag(string? tag) =>
        tag is not null &&
        tag.AsSpan().SequenceEqual(DropboxProtocol.PathNotFound.AsSpan(DropboxProtocol.Path.Length + 1));

    /// <summary>The WriteError inside a path failure: nested under path for UploadSessionFinishError, or under the
    /// reason the direct UploadError's UploadWriteFailed wraps, else the object reason member. Null when the shape
    /// carries none; MapUploadFailure treats a missing one as malformed.</summary>
    private static JsonElement? WriteError(JsonElement failure)
    {
        if (failure.ValueKind != JsonValueKind.Object) return null;
        if (failure.TryGetProperty(DropboxProtocol.Path, out var path) && path.ValueKind == JsonValueKind.Object)
        {
            if (UnionTag(path) is not null) return path;
            return path.TryGetProperty(DropboxProtocol.Reason, out var wrapped) &&
                wrapped.ValueKind == JsonValueKind.Object
                    ? wrapped
                    : path;
        }
        return failure.TryGetProperty(DropboxProtocol.Reason, out var reason) && reason.ValueKind == JsonValueKind.Object
            ? reason
            : null;
    }

    /// <summary>The union's string .tag, or null when the shape carries none.</summary>
    internal static string? UnionTag(JsonElement union) =>
        union.ValueKind == JsonValueKind.Object &&
        union.TryGetProperty(DropboxProtocol.Tag, out var tag) &&
        tag.ValueKind == JsonValueKind.String
            ? tag.GetString()
            : null;

    /// <summary>The union's string .tag; a non-object or non-string tag is a decode failure.</summary>
    private static string RequiredTag(JsonElement union) => UnionTag(union) ?? throw new JsonException();

    /// <summary>An unclassified provider failure: the item failed at path, preserving the cause.</summary>
    internal static ItemFailedException Refused(string path, Exception error) =>
        new(path, TextKey.DropboxRefused, error);

    /// <summary>The specific cause of a documented write refusal for route, else DropboxRefused.</summary>
    private static TextKey WriteCause(string route, string summary) => route switch
    {
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish or DropboxProtocol.CreateFolder =>
            CauseUnder(summary, DropboxProtocol.UploadWritePrefix),
        DropboxProtocol.Delete => CauseUnder(summary, DropboxProtocol.DeleteWritePrefix),
        DropboxProtocol.Move =>
            CauseUnder(summary, summary.StartsWith(DropboxProtocol.MoveToWritePrefix, StringComparison.Ordinal)
                ? DropboxProtocol.MoveToWritePrefix
                : DropboxProtocol.MoveFromWritePrefix),
        _ => TextKey.DropboxRefused
    };

    private static TextKey CauseUnder(string summary, string location)
    {
        if (!summary.StartsWith(location, StringComparison.Ordinal)) return TextKey.DropboxRefused;
        var rest = summary.AsSpan(location.Length);
        var end = rest.IndexOf(DropboxProtocol.ErrorSummarySeparator);
        // Complete segment only: "no_write_permission_extra" is a different reason.
        return CauseOf(end < 0 ? rest : rest[..end]);
    }

    /// <summary>The cause of a documented write-refusal tag, else DropboxRefused.</summary>
    private static TextKey CauseOf(ReadOnlySpan<char> tag)
    {
        if (tag.SequenceEqual(DropboxProtocol.MalformedPath) || tag.SequenceEqual(DropboxProtocol.DisallowedName))
            return TextKey.FailDropboxNameRejected;
        if (tag.SequenceEqual(DropboxProtocol.NoWritePermission)) return TextKey.FailDropboxNoPermission;
        if (tag.SequenceEqual(DropboxProtocol.InsufficientSpace)) return TextKey.FailDropboxNoSpace;
        return TextKey.DropboxRefused;
    }

    /// <summary>The useful cause of a well-shaped DeleteError; a malformed member carries none. The path_lookup
    /// branch's not_found is the changed-underneath condition Map already proves on the summary, and the path_write
    /// branch nests the same WriteError vocabulary as an upload; no DeleteError tag proves the effect was not
    /// applied, so MapFolderFailure records Unknown in every branch.</summary>
    private static TextKey? FolderCause(JsonElement failure)
    {
        if (failure.ValueKind != JsonValueKind.Object ||
            !failure.TryGetProperty(DropboxProtocol.Tag, out var tag) || tag.ValueKind != JsonValueKind.String)
            return null;
        if (tag.ValueEquals(DropboxProtocol.PathLookup))
        {
            if (!failure.TryGetProperty(DropboxProtocol.PathLookup, out var lookup) ||
                lookup.ValueKind != JsonValueKind.Object ||
                !lookup.TryGetProperty(DropboxProtocol.Tag, out var lookupTag) ||
                lookupTag.ValueKind != JsonValueKind.String)
                return null;
            return IsNotFoundTag(lookupTag.GetString())
                ? TextKey.FailChanged
                : TextKey.DropboxRefused;
        }
        if (!tag.ValueEquals(DropboxProtocol.PathWrite)) return TextKey.DropboxRefused;
        return failure.TryGetProperty(DropboxProtocol.PathWrite, out var write) &&
            write.ValueKind == JsonValueKind.Object &&
            write.TryGetProperty(DropboxProtocol.Tag, out var writeTag) &&
            writeTag.ValueKind == JsonValueKind.String
                ? CauseOf(writeTag.GetString())
                : null;
    }

    /// <summary>The error_summary for an object reply that carries a string, else ""; never throws on shape.</summary>
    internal static string Summary(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(DropboxProtocol.ErrorSummary, out var summary) &&
        summary.ValueKind == JsonValueKind.String ? summary.GetString()! : "";

    /// <summary>The canonical provider cause for one classified failure: the structured union's tag path when the
    /// reply carries one, else the error summary; both pass the safe-tag filter so no body text reaches the log.</summary>
    internal static string ProviderCause(DropboxApiException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var failure = DropboxApi.Failure(error);
        if (failure.ValueKind == JsonValueKind.Object && UnionTag(failure) is { } tag)
        {
            foreach (var nested in new[] { DropboxProtocol.Path, DropboxProtocol.PathLookup, DropboxProtocol.Reason })
                if (failure.TryGetProperty(nested, out var child) && child.ValueKind == JsonValueKind.Object &&
                    UnionTag(child) is { } childTag)
                    return SafeProviderTag(tag + DropboxProtocol.ErrorSummarySeparator + childTag);
            return SafeProviderTag(tag);
        }
        return SafeProviderTag(error.SafeReason);
    }

    /// <summary>The one safe-tag filter: lower-case letters, digits, `_`, `.` and `/` only; empty or unsafe text
    /// becomes the data-owned unavailable reason.</summary>
    internal static string SafeProviderTag(string value)
    {
        foreach (var character in value)
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or
                DropboxProtocol.ErrorSummarySeparator or '.'))
                return Text.Get(TextKey.ProviderReasonUnavailable);
        return value.Length == 0 ? Text.Get(TextKey.ProviderReasonUnavailable) : value;
    }

    /// <summary>True when the optional summary carries tag as a complete separator-delimited segment.
    /// Split-equivalent: leading, trailing and empty segments count; no trimming or case folding.</summary>
    private static bool HasSummarySegment(string summary, string tag)
    {
        var rest = summary.AsSpan();
        while (true)
        {
            var end = rest.IndexOf(DropboxProtocol.ErrorSummarySeparator);
            if ((end < 0 ? rest : rest[..end]).SequenceEqual(tag)) return true;
            if (end < 0) return false;
            rest = rest[(end + 1)..];
        }
    }

    /// <summary>Write throttling: the structured union's own tag or the WriteError it nests under path or reason,
    /// else the summary's segments. A malformed structured branch falls through to the summary rather than throwing.</summary>
    internal static bool IsWriteThrottle(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty(DropboxProtocol.Error, out var error) && IsStructuredWriteThrottle(error)) return true;
        return HasSummarySegment(Summary(root), DropboxProtocol.TooManyWriteOperations);
    }

    /// <summary>True when the same tag reader MapUploadFailure uses finds the write-throttle tag at the union or
    /// the WriteError it nests.</summary>
    private static bool IsStructuredWriteThrottle(JsonElement failure)
    {
        if (IsWriteThrottleTag(UnionTag(failure))) return true;
        var writeError = WriteError(failure);
        return writeError is not null && IsWriteThrottleTag(UnionTag(writeError.Value));
    }

    private static bool IsWriteThrottleTag(string? tag) =>
        tag is { } value && value.Equals(DropboxProtocol.TooManyWriteOperations, StringComparison.Ordinal);
}
