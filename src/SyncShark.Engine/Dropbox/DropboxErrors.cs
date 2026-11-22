using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The 409 mapping of section 5.4. Structured provider evidence is authoritative; a documented summary-only
/// refusal counts only with a complete tag and its required subtype. An inconclusive reply (a malformed or
/// contradictory token, an unknown tag, a missing subtype) is an unknown outcome for a dispatched write, never proof of
/// no effect. One owner classifies a single response and each member of a batch reply from its route and request
/// evidence.</summary>
public sealed class DropboxErrors
{
    /// <summary>What the caller observed about the request that produced a failure. ProvenNoEffect is the caller's
    /// proof that a dispatched write was not applied; Transient is positive transport evidence.</summary>
    public readonly record struct RequestEvidence(bool Read, bool Dispatched, bool ProvenNoEffect, bool Transient);

    /// <summary>One classified failure: the cause, the disposition, whether it is an attributable refusal that proves
    /// the write had no effect (a folder split may act on it), and whether a usable observation proved the target
    /// identity absent. Absence means a get_metadata's exact structured path/not_found, or a by-id delete's exact
    /// structured path_lookup/not_found; it is never the same as our request having deleted it (its effect stays
    /// Unknown), and a caller carries a delete absence only when its own request addressed the selected id.</summary>
    public readonly record struct Classified(
        TextKey Cause, FailureDisposition Disposition, bool ProvenNoEffect, bool Absent = false)
    {
        /// <summary>Whether this continuation cursor needs to be replaced by a fresh listing.</summary>
        public bool CursorReset { get; init; }
    }

    /// <summary>Classify one single response. <paramref name="body"/> is the parsed reply. A documented write
    /// contention takes the write rule on any status; a well-shaped structured refusal proves no effect; a documented
    /// summary-only refusal counts only when no error payload is present; absence needs the exact structured miss.
    /// Otherwise the request evidence decides, and an unproved dispatched write stays unknown.</summary>
    public Classified Classify(HttpStatusCode status, JsonElement body, string route, bool expectedIsNull,
        RequestEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(route);
        var fallback = Unclassified(evidence);
        JsonElement failure = default;
        var hasError = body.ValueKind == JsonValueKind.Object && body.TryGetProperty(DropboxProtocol.Error, out failure);
        var summary = !hasError && body.ValueKind == JsonValueKind.Object && !body.TryGetProperty(DropboxProtocol.Tag, out _)
            ? Summary(body) : string.Empty;
        // RateLimitError is an untagged struct. An unrelated tagged error may carry its own reason.
        if (IsMutatingRoute(route) && (Prefixed(summary, DropboxProtocol.TooManyWriteOperations) || IsRateLimitContention(failure)))
            return Contention();

        Classified? decoded = hasError ? route switch
        {
            DropboxProtocol.GetMetadata when IsStructuredNotFound(failure) => Refused(TextKey.DropboxRefused) with { Absent = true },
            DropboxProtocol.Download when IsStructuredNotFound(failure) => Refused(TextKey.FailChanged),
            _ when IsJobCheckRoute(route) && UnionTag(failure) == DropboxProtocol.InvalidAsyncJobId =>
                new Classified(TextKey.FailDropboxDeleteJobInvalid, FailureDisposition.Permanent, false),
            DropboxProtocol.Delete => DeleteMember(failure),
            DropboxProtocol.Move => MoveMember(failure, batch: false),
            DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionAppend or
                DropboxProtocol.UploadSessionStart or DropboxProtocol.CreateFolder => WriteRefusal(route, failure, expectedIsNull),
            _ => null // Batch launch errors are not individual member results.
        } : SingleSummaryRefusal(route, summary, expectedIsNull);
        if (decoded is not { } result) return fallback;
        if (result.Cause == TextKey.WaitTooManyWrites) return result;
        if (status != HttpStatusCode.Conflict) return fallback;
        return !result.Absent && result.Disposition == FailureDisposition.UnknownEffect
            ? fallback with { Cause = result.Cause } : result;
    }

    /// <summary>Classify one member of a batch reply body (HTTP 200 with a per-member failure union). Delete, move,
    /// upload-finish and create-folder members use the route's own decode; an unknown tag, malformed member or
    /// unrecognized route stays Unknown so only its own position is affected.</summary>
    public Classified ClassifyMember(JsonElement failure, string route, bool expectedIsNull)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route switch
        {
            DropboxProtocol.DeleteBatch or DropboxProtocol.DeleteBatchCheck => DeleteMember(failure),
            DropboxProtocol.MoveBatch or DropboxProtocol.MoveBatchCheck => MoveMember(failure, batch: true),
            DropboxProtocol.CreateFolderBatch or DropboxProtocol.CreateFolderBatchCheck =>
                WriteRefusal(DropboxProtocol.CreateFolder, failure, true) ?? Unproven(),
            DropboxProtocol.UploadSessionFinishBatch => WriteRefusal(DropboxProtocol.UploadSessionFinish, failure, expectedIsNull) ?? Unproven(),
            _ => Unproven()
        };
    }

    /// <summary>The frozen two-argument surface: no route or request evidence, so an unclassified refusal.</summary>
    public FailureDisposition Classify(HttpStatusCode status, JsonElement error) =>
        Classify(status, error, string.Empty, false, default).Disposition;

    // A failure outside a 409, or an inconclusive one: a read follows its transient evidence (log-only retry when
    // positive, so a decided 500 stays a failure); an unsent write is permanent; a dispatched write the caller did not
    // prove had no effect is unknown; a proved no-effect write is temporary only when transient.
    private static Classified Unclassified(RequestEvidence evidence)
    {
        if (evidence.Read)
            return new Classified(TextKey.DropboxRefused,
                evidence.Transient ? FailureDisposition.TemporaryNoEffect : FailureDisposition.Permanent, true);
        if (!evidence.Dispatched)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        if (!evidence.ProvenNoEffect)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
        return new Classified(TextKey.DropboxRefused,
            evidence.Transient ? FailureDisposition.TemporaryNoEffect : FailureDisposition.Permanent, true);
    }

    private static Classified Unproven() => new(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);

    private static Classified Contention() => new(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);

    private static Classified Refused(TextKey cause) => new(cause, FailureDisposition.Permanent, true);

    private static Classified? SingleSummaryRefusal(string route, string summary, bool expectedIsNull)
    {
        if (route == DropboxProtocol.Download && Prefixed(summary, DropboxProtocol.PathNotFound))
            return Refused(TextKey.FailChanged);
        if (route == DropboxProtocol.Move && Prefixed(summary, DropboxProtocol.FromLookupNotFound))
            return Refused(TextKey.FailChanged);
        if (route == DropboxProtocol.Move &&
            (Prefixed(summary, DropboxProtocol.ToConflictFile) ||
             Prefixed(summary, DropboxProtocol.ToConflictFolder) ||
             Prefixed(summary, DropboxProtocol.ToConflictFileAncestor)))
            return Refused(TextKey.ClashNameTaken);
        if (route == DropboxProtocol.Move && Prefixed(summary, DropboxProtocol.TooManyFiles))
            return Refused(TextKey.FailDropboxTooManyFiles);
        // A conflict needs its documented subtype segment; a bare or unknown subtype proves nothing.
        if ((route is DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish) &&
            (Prefixed(summary, DropboxProtocol.PathConflictFile) ||
             Prefixed(summary, DropboxProtocol.PathConflictFolder)))
            return Refused(expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged);
        if (route == DropboxProtocol.CreateFolder &&
            (Prefixed(summary, DropboxProtocol.PathConflictFolder) ||
             Prefixed(summary, DropboxProtocol.PathConflictFile)))
            return Refused(TextKey.ClashNameTaken);
        return WriteCause(route, summary) switch
        {
            null => null,
            TextKey.WaitTooManyWrites => Contention(),
            { } cause when route == DropboxProtocol.Delete => Unproven() with { Cause = cause },
            { } cause => Refused(cause)
        };
    }

    // Decode one structured write union (an upload, upload-finish or create member, single or batch). A refusal counts
    // only when it is well shaped: a conflict carries its required subtype, and malformed_path carries its string.
    private static Classified? WriteRefusal(string route, JsonElement failure, bool expectedIsNull)
    {
        var tag = UnionTag(failure);
        if (route != DropboxProtocol.CreateFolder && tag is DropboxProtocol.PayloadTooLarge or DropboxProtocol.ContentHashMismatch)
            return Refused(TextKey.DropboxRefused);
        if (route == DropboxProtocol.UploadSessionFinish)
        {
            if (tag == DropboxProtocol.TooManyWriteOperations) return Contention();
            if (tag == DropboxProtocol.LookupFailed && failure.TryGetProperty(tag, out var lookup) &&
                ProvenLookupRefusal(lookup)) return Refused(TextKey.DropboxRefused);
        }
        if (route is DropboxProtocol.UploadSessionAppend or DropboxProtocol.UploadSessionStart || tag != DropboxProtocol.Path)
            return null;
        // UploadWriteFailed has inline struct fields; finish and create carry the WriteError union under path.
        var direct = route == DropboxProtocol.Upload;
        if (direct && (!failure.TryGetProperty(DropboxProtocol.UploadSessionId, out var session) ||
            session.ValueKind != JsonValueKind.String)) return null;
        if (!failure.TryGetProperty(direct ? DropboxProtocol.Reason : DropboxProtocol.Path, out var write)) return null;
        var writeTag = UnionTag(write);
        if (writeTag is null) return null;
        if (writeTag == DropboxProtocol.Conflict)
            return IsWellFormedConflict(write)
                ? Refused(route == DropboxProtocol.CreateFolder || expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged)
                : null;
        if (writeTag == DropboxProtocol.TooManyWriteOperations)
            return Contention();
        if (writeTag == DropboxProtocol.MalformedPath)
            return write.TryGetProperty(DropboxProtocol.MalformedPath, out var malformed) &&
                malformed.ValueKind is JsonValueKind.String or JsonValueKind.Null
                    ? Refused(TextKey.FailDropboxNameRejected) : null;
        if (writeTag == DropboxProtocol.DisallowedName) return Refused(TextKey.FailDropboxNameRejected);
        if (writeTag == DropboxProtocol.NoWritePermission) return Refused(TextKey.FailDropboxNoPermission);
        if (writeTag == DropboxProtocol.InsufficientSpace) return Refused(TextKey.FailDropboxNoSpace);
        if (writeTag is DropboxProtocol.TeamFolder or DropboxProtocol.OperationSuppressed)
            return Refused(TextKey.DropboxRefused);
        return null;
    }

    // Except for explicit contention, a delete refusal names a cause, not an effect. A selected-id miss is absence only.
    private static Classified DeleteMember(JsonElement failure)
    {
        var tag = UnionTag(failure);
        if (tag == DropboxProtocol.PathLookup &&
            failure.TryGetProperty(DropboxProtocol.PathLookup, out var lookup) &&
            UnionTag(lookup) is { } lookupTag && IsNotFoundTag(lookupTag))
            return new Classified(TextKey.FailChanged, FailureDisposition.UnknownEffect, false, Absent: true);
        if (tag == DropboxProtocol.TooManyWriteOperations) return Contention();
        var cause = TextKey.DropboxRefused;
        if (tag == DropboxProtocol.PathWrite &&
            failure.TryGetProperty(DropboxProtocol.PathWrite, out var write) &&
            UnionTag(write) is { } writeTag)
        {
            if (writeTag == DropboxProtocol.TooManyWriteOperations) return Contention();
            cause = CauseOf(writeTag);
        }
        return new Classified(cause, FailureDisposition.UnknownEffect, false);
    }

    private static Classified MoveMember(JsonElement failure, bool batch)
    {
        var tag = UnionTag(failure);
        if (batch && tag == DropboxProtocol.TooManyWriteOperations) return Contention();
        // A move batch member wraps the RelocationError under relocation_error; move_v2's own error is the union itself.
        var relocation = failure;
        if (batch && (tag != DropboxProtocol.RelocationError ||
                      !failure.TryGetProperty(DropboxProtocol.RelocationError, out relocation)))
            return Unproven();
        return MoveCause(relocation) switch
        {
            TextKey.WaitTooManyWrites => Contention(),
            { } known => Refused(known),
            null => Unproven()
        };
    }

    private static bool IsWellFormedConflict(JsonElement write) =>
        write.TryGetProperty(DropboxProtocol.Conflict, out var conflict) &&
        UnionTag(conflict) is DropboxProtocol.TagFile or DropboxProtocol.TagFolder or DropboxProtocol.FileAncestor;

    // The poll-check routes; an exact structured invalid_async_job_id on these is the invalid-job cause (BC21).
    private static bool IsJobCheckRoute(string route) => route is
        DropboxProtocol.DeleteBatchCheck or DropboxProtocol.MoveBatchCheck or DropboxProtocol.CreateFolderBatchCheck;

    // Every route that can change the provider's state, so the reason-first write-contention rule applies.
    private static bool IsMutatingRoute(string route) => route is
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend or
        DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch or DropboxProtocol.CreateFolder or
        DropboxProtocol.CreateFolderBatch or DropboxProtocol.Delete or DropboxProtocol.DeleteBatch or
        DropboxProtocol.Move or DropboxProtocol.MoveBatch;

    private static bool IsRateLimitContention(JsonElement failure) =>
        failure.ValueKind == JsonValueKind.Object && !failure.TryGetProperty(DropboxProtocol.Tag, out _) &&
        failure.TryGetProperty(DropboxProtocol.Reason, out var reason) && UnionTag(reason) == DropboxProtocol.TooManyWriteOperations &&
        (!failure.TryGetProperty(DropboxProtocol.RetryAfter, out var wait) || wait.ValueKind == JsonValueKind.Number && wait.TryGetUInt64(out _));

    /// <summary>The error_summary for an object reply that carries a string, else empty; never throws on shape.</summary>
    private static string Summary(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(DropboxProtocol.ErrorSummary, out var summary) &&
        summary.ValueKind == JsonValueKind.String
            ? summary.GetString()!
            : string.Empty;

    /// <summary>The union's string .tag, or null when the shape carries none.</summary>
    private static string? UnionTag(JsonElement union) =>
        union.ValueKind == JsonValueKind.Object &&
        union.TryGetProperty(DropboxProtocol.Tag, out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()
            : null;

    // A complete tag segment: equal to the prefix, or the prefix followed by the summary separator. So
    // "path_lookup/not_found_extra" is not "path_lookup/not_found".
    private static bool Prefixed(string summary, string prefix) =>
        summary.Equals(prefix, StringComparison.Ordinal) ||
        summary.Length > prefix.Length && summary.StartsWith(prefix, StringComparison.Ordinal) &&
            summary[prefix.Length] == DropboxProtocol.ErrorSummarySeparator;

    /// <summary>True when a lookup tag is the provider's bare not_found token, the documented suffix of path/not_found.</summary>
    private static bool IsNotFoundTag(string? tag) =>
        tag is not null &&
        tag.AsSpan().SequenceEqual(DropboxProtocol.PathNotFound.AsSpan(DropboxProtocol.Path.Length + 1));

    /// <summary>True only for the exact structured 409 path/not_found union; a summary prefix or a malformed payload is
    /// never absence (M14/BC21).</summary>
    private static bool IsStructuredNotFound(JsonElement failure) =>
        UnionTag(failure) == DropboxProtocol.Path &&
        failure.TryGetProperty(DropboxProtocol.Path, out var lookup) && lookup.ValueKind == JsonValueKind.Object &&
        lookup.TryGetProperty(DropboxProtocol.Tag, out var reason) && reason.ValueKind == JsonValueKind.String &&
        IsNotFoundTag(reason.GetString());

    // UploadSessionOffsetError is a struct: correct_offset is inline in the incorrect_offset union.
    private static bool ProvenLookupRefusal(JsonElement lookup) => UnionTag(lookup) switch
    {
        DropboxProtocol.IncorrectOffset => lookup.TryGetProperty(DropboxProtocol.CorrectOffset, out var offset) &&
            offset.ValueKind == JsonValueKind.Number && offset.TryGetUInt64(out _),
        DropboxProtocol.SessionClosed or DropboxProtocol.SessionNotClosed or DropboxProtocol.SessionTooLarge or
            DropboxProtocol.PayloadTooLarge => true,
        var tag => IsNotFoundTag(tag)
    };

    private static TextKey? MoveCause(JsonElement relocation)
    {
        var tag = UnionTag(relocation);
        if (tag is DropboxProtocol.FromLookup or DropboxProtocol.FromWrite or DropboxProtocol.To)
        {
            if (!relocation.TryGetProperty(tag ?? string.Empty, out var path)) return null;
            var reason = UnionTag(path);
            // Payloads are evidence too: a recognized outer tag alone proves nothing.
            if (reason == DropboxProtocol.MalformedPath)
                return path.TryGetProperty(DropboxProtocol.MalformedPath, out var malformed) &&
                    malformed.ValueKind is JsonValueKind.String or JsonValueKind.Null
                        ? TextKey.FailDropboxNameRejected : null;
            if (tag == DropboxProtocol.FromLookup)
                return IsNotFoundTag(reason) ? TextKey.FailChanged : null;
            if (reason == DropboxProtocol.Conflict)
                return IsWellFormedConflict(path)
                    ? tag == DropboxProtocol.To ? TextKey.ClashNameTaken : CauseOf(reason)
                    : null;
            return reason switch
            {
                DropboxProtocol.TooManyWriteOperations => TextKey.WaitTooManyWrites,
                DropboxProtocol.DisallowedName or DropboxProtocol.NoWritePermission or
                    DropboxProtocol.InsufficientSpace or DropboxProtocol.TeamFolder or
                    DropboxProtocol.OperationSuppressed => CauseOf(reason),
                _ => null
            };
        }
        return tag switch
        {
            DropboxProtocol.TooManyFiles => TextKey.FailDropboxTooManyFiles,
            DropboxProtocol.CantCopySharedFolder or DropboxProtocol.CantNestSharedFolder or
                DropboxProtocol.CantMoveFolderIntoItself or DropboxProtocol.DuplicatedOrNestedPaths or
                DropboxProtocol.CantTransferOwnership or DropboxProtocol.InsufficientQuota or
                DropboxProtocol.CantMoveSharedFolder => TextKey.DropboxRefused,
            _ => null
        };
    }

    /// <summary>The specific cause of a documented summary write refusal for route, else null (not a proven refusal).</summary>
    private static TextKey? WriteCause(string route, string summary) => route switch
    {
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish or DropboxProtocol.CreateFolder =>
            CauseUnder(summary, DropboxProtocol.UploadWritePrefix),
        DropboxProtocol.Delete => CauseUnder(summary, DropboxProtocol.DeleteWritePrefix),
        DropboxProtocol.Move =>
            CauseUnder(summary, summary.StartsWith(DropboxProtocol.MoveToWritePrefix, StringComparison.Ordinal)
                ? DropboxProtocol.MoveToWritePrefix
                : DropboxProtocol.MoveFromWritePrefix),
        _ => null
    };

    private static TextKey? CauseUnder(string summary, string location)
    {
        if (!summary.StartsWith(location, StringComparison.Ordinal)) return null;
        var rest = summary.AsSpan(location.Length);
        var end = rest.IndexOf(DropboxProtocol.ErrorSummarySeparator);
        // Complete segment only: "no_write_permission_extra" is a different reason.
        var reason = end < 0 ? rest : rest[..end];
        return reason.SequenceEqual(DropboxProtocol.TooManyWriteOperations) ? TextKey.WaitTooManyWrites :
            IsDocumentedSummaryReason(reason) ? CauseOf(reason) : null;
    }

    private static bool IsDocumentedSummaryReason(ReadOnlySpan<char> tag) =>
        tag.SequenceEqual(DropboxProtocol.MalformedPath) || tag.SequenceEqual(DropboxProtocol.DisallowedName) ||
        tag.SequenceEqual(DropboxProtocol.NoWritePermission) || tag.SequenceEqual(DropboxProtocol.InsufficientSpace) ||
        tag.SequenceEqual(DropboxProtocol.TeamFolder) || tag.SequenceEqual(DropboxProtocol.OperationSuppressed);

    /// <summary>The cause of a documented write-refusal tag; team_folder and operation_suppressed are ordinary refusals.</summary>
    private static TextKey CauseOf(ReadOnlySpan<char> tag)
    {
        if (tag.SequenceEqual(DropboxProtocol.MalformedPath) || tag.SequenceEqual(DropboxProtocol.DisallowedName))
            return TextKey.FailDropboxNameRejected;
        if (tag.SequenceEqual(DropboxProtocol.NoWritePermission)) return TextKey.FailDropboxNoPermission;
        if (tag.SequenceEqual(DropboxProtocol.InsufficientSpace)) return TextKey.FailDropboxNoSpace;
        return TextKey.DropboxRefused;
    }
}
