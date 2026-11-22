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
        TextKey Cause, FailureDisposition Disposition, bool ProvenNoEffect, bool Absent = false);

    /// <summary>Classify one single response. <paramref name="body"/> is the parsed reply. A documented write
    /// contention takes the write rule on any status; a well-shaped structured refusal proves no effect; a documented
    /// summary-only refusal counts only when no error payload is present; absence needs the exact structured miss.
    /// Otherwise the request evidence decides, and an unproved dispatched write stays unknown.</summary>
    public Classified Classify(HttpStatusCode status, JsonElement body, string route, bool expectedIsNull,
        RequestEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(route);
        var summary = Summary(body);
        var failure = Failure(body);
        var structured = UnionTag(failure) is not null;
        var payloadPresent = HasErrorPayload(body);

        // Reason-first: documented write contention takes the write rule on any status (product-0518/0546).
        if (IsMutatingRoute(route) && DocumentsWriteThrottle(summary, failure, payloadPresent))
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);

        if (status == HttpStatusCode.Conflict)
        {
            // Absence only from the exact structured miss; a summary-only or malformed lookup is not absence.
            if (route == DropboxProtocol.GetMetadata && IsStructuredNotFound(failure))
                return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true, Absent: true);

            // An exact structured invalid_async_job_id on a poll-check route is the invalid-job cause (BC21/product-0099).
            if (IsJobCheckRoute(route) && UnionTag(failure) == DropboxProtocol.InvalidAsyncJobId)
                return new Classified(TextKey.FailDropboxDeleteJobInvalid, FailureDisposition.Permanent, false);

            if (IsMutatingRoute(route))
            {
                if (structured)
                {
                    // A well-shaped refusal counts; an inconclusive one falls back to the caller's proof.
                    if (StructuredRefusal(route, failure, expectedIsNull) is { } refusal &&
                        (refusal.Absent || refusal.Disposition != FailureDisposition.UnknownEffect))
                        return refusal;
                    return Unclassified(evidence);
                }
                // A present but unusable error payload is never replaced by the summary.
                if (payloadPresent) return Unclassified(evidence);
                return SingleSummaryRefusal(route, summary, expectedIsNull) ?? Unclassified(evidence);
            }

            return SingleSummaryRefusal(route, summary, expectedIsNull) ?? Unclassified(evidence);
        }

        return Unclassified(evidence);
    }

    /// <summary>Classify one member of a batch reply body (HTTP 200 with a per-member failure union). Delete, move,
    /// upload-finish and create-folder members use the route's own decode; an unknown tag, malformed member or
    /// unrecognized route stays Unknown so only its own position is affected.</summary>
    public Classified ClassifyMember(JsonElement failure, string route, bool expectedIsNull)
    {
        ArgumentNullException.ThrowIfNull(route);
        return StructuredRefusal(route, failure, expectedIsNull) ?? Unproven();
    }

    /// <summary>The frozen two-argument surface: no route or request evidence, so an unclassified refusal.</summary>
    public FailureDisposition Classify(HttpStatusCode status, JsonElement error) =>
        Classify(status, error, string.Empty, false, default).Disposition;

    // The route's own structured refusal decode, shared by the single and member paths. Null only when the route's
    // write union proves nothing (the caller then uses its evidence or treats the member as Unknown).
    private static Classified? StructuredRefusal(string route, JsonElement failure, bool expectedIsNull)
    {
        if (route is DropboxProtocol.Delete or DropboxProtocol.DeleteBatch or DropboxProtocol.DeleteBatchCheck)
            return DeleteMember(failure);
        if (route is DropboxProtocol.Move or DropboxProtocol.MoveBatch or DropboxProtocol.MoveBatchCheck)
            return MoveMember(failure, UnionTag(failure),
                batch: route is DropboxProtocol.MoveBatch or DropboxProtocol.MoveBatchCheck);
        if (IsUploadRoute(route))
            return WriteRefusal(failure, expectedIsNull,
                createFolder: route is DropboxProtocol.CreateFolder or DropboxProtocol.CreateFolderBatch,
                finish: route is DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch);
        return null;
    }

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
        if ((route is DropboxProtocol.CreateFolder or DropboxProtocol.CreateFolderBatch or
                DropboxProtocol.CreateFolderBatchCheck) &&
            (Prefixed(summary, DropboxProtocol.PathConflictFolder) ||
             Prefixed(summary, DropboxProtocol.PathConflictFile)))
            return Refused(TextKey.ClashNameTaken);
        return WriteCause(route, summary) is { } cause ? Refused(cause) : null;
    }

    // Decode one structured write union (an upload, upload-finish or create member, single or batch). A refusal counts
    // only when it is well shaped: a conflict carries its required subtype, and malformed_path carries its string.
    private static Classified? WriteRefusal(JsonElement failure, bool expectedIsNull, bool createFolder, bool finish)
    {
        var tag = UnionTag(failure);
        if (tag is null) return null;
        if (tag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
        if (tag != DropboxProtocol.Path)
        {
            // other/payload_too_large/content_hash_mismatch are documented top-level refusals. properties_error may leave
            // the file uploaded without properties (not proof of no effect); lookup_failed belongs to finish and needs
            // its nested error. Everything else proves nothing.
            if (tag is DropboxProtocol.TagOther or DropboxProtocol.PayloadTooLarge or DropboxProtocol.ContentHashMismatch)
                return Refused(TextKey.DropboxRefused);
            if (tag == DropboxProtocol.LookupFailed && finish &&
                failure.TryGetProperty(DropboxProtocol.LookupFailed, out var lookup) &&
                lookup.ValueKind == JsonValueKind.Object && UnionTag(lookup) is not null)
                return Refused(TextKey.DropboxRefused);
            return null;
        }
        if (WriteError(failure) is not { } write) return null;
        var writeTag = UnionTag(write);
        if (writeTag is null) return null;
        if (writeTag == DropboxProtocol.Conflict)
            return IsWellFormedConflict(write)
                ? Refused(createFolder || expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged)
                : null;
        if (writeTag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
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

    // No DeleteError tag proves our effect, so a delete member's effect stays Unknown. The one exception, the exact
    // structured by-id path_lookup/not_found, proves the selected identity is gone (Absent) without proving our delete.
    private static Classified DeleteMember(JsonElement failure)
    {
        var tag = UnionTag(failure);
        if (tag == DropboxProtocol.PathLookup &&
            failure.TryGetProperty(DropboxProtocol.PathLookup, out var lookup) &&
            UnionTag(lookup) is { } lookupTag && IsNotFoundTag(lookupTag))
            return new Classified(TextKey.FailChanged, FailureDisposition.UnknownEffect, false, Absent: true);
        var cause = TextKey.DropboxRefused;
        if (tag == DropboxProtocol.PathWrite &&
            failure.TryGetProperty(DropboxProtocol.PathWrite, out var write) &&
            UnionTag(write) is { } writeTag)
            cause = CauseOf(writeTag);
        return new Classified(cause, FailureDisposition.UnknownEffect, false);
    }

    private static Classified MoveMember(JsonElement failure, string? tag, bool batch)
    {
        if (tag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
        // A move batch member wraps the RelocationError under relocation_error; move_v2's own error is the union itself.
        var relocation = failure;
        if (batch && (tag != DropboxProtocol.RelocationError ||
                      !failure.TryGetProperty(DropboxProtocol.RelocationError, out relocation)))
            return Unproven();
        return MoveCause(relocation) is { } known
            ? new Classified(known, known == TextKey.WaitTooManyWrites
                ? FailureDisposition.TemporaryNoEffect : FailureDisposition.Permanent, true)
            : Unproven();
    }

    private static bool IsWellFormedConflict(JsonElement write) =>
        write.TryGetProperty(DropboxProtocol.Conflict, out var conflict) &&
        UnionTag(conflict) is DropboxProtocol.TagFile or DropboxProtocol.TagFolder or DropboxProtocol.FileAncestor;

    private static bool IsUploadRoute(string route) => route is
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend or
        DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch or DropboxProtocol.CreateFolder or
        DropboxProtocol.CreateFolderBatch;

    // The poll-check routes; an exact structured invalid_async_job_id on these is the invalid-job cause (BC21).
    private static bool IsJobCheckRoute(string route) => route is
        DropboxProtocol.DeleteBatchCheck or DropboxProtocol.MoveBatchCheck or DropboxProtocol.CreateFolderBatchCheck;

    // Every route that can change the provider's state, so the reason-first write-contention rule applies.
    private static bool IsMutatingRoute(string route) => route is
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend or
        DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch or DropboxProtocol.CreateFolder or
        DropboxProtocol.CreateFolderBatch or DropboxProtocol.Delete or DropboxProtocol.DeleteBatch or
        DropboxProtocol.Move or DropboxProtocol.MoveBatch;

    // A documented write contention, from the structured union (top level, or the WriteError under path/reason) or a
    // complete summary tag only when no error payload is present (a malformed payload never trusts the summary).
    private static bool DocumentsWriteThrottle(string summary, JsonElement failure, bool payloadPresent)
    {
        if (UnionTag(failure) == DropboxProtocol.TooManyWriteOperations) return true;
        if (WriteError(failure) is { } write && UnionTag(write) == DropboxProtocol.TooManyWriteOperations) return true;
        if (payloadPresent) return false;
        return HasSegment(summary, DropboxProtocol.TooManyWriteOperations);
    }

    private static bool HasErrorPayload(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(DropboxProtocol.Error, out _);

    /// <summary>The error_summary for an object reply that carries a string, else empty; never throws on shape.</summary>
    private static string Summary(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(DropboxProtocol.ErrorSummary, out var summary) &&
        summary.ValueKind == JsonValueKind.String
            ? summary.GetString()!
            : string.Empty;

    // The structured failure union: the reply's error member, else the element itself when it already is a union.
    private static JsonElement Failure(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return default;
        // A present error payload is authoritative: an unusable one is rejected, never replaced by the outer object.
        if (body.TryGetProperty(DropboxProtocol.Error, out var error))
            return error.ValueKind == JsonValueKind.Object ? error : default;
        // Only a bare union (no error member) is itself the failure input from a batch/member caller.
        return UnionTag(body) is not null ? body : default;
    }

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

    // True when the optional summary carries tag as a complete separator-delimited segment (empty edges included).
    private static bool HasSegment(string summary, string tag)
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

    // The WriteError inside a path failure: nested under path for UploadSessionFinishError, or under the reason the
    // direct UploadError's UploadWriteFailed wraps, else the object reason member.
    private static JsonElement? WriteError(JsonElement failure)
    {
        if (failure.ValueKind != JsonValueKind.Object) return null;
        if (failure.TryGetProperty(DropboxProtocol.Path, out var path) && path.ValueKind == JsonValueKind.Object)
        {
            if (UnionTag(path) is not null) return path;
            return path.TryGetProperty(DropboxProtocol.Reason, out var wrapped) &&
                wrapped.ValueKind == JsonValueKind.Object ? wrapped : path;
        }
        return failure.TryGetProperty(DropboxProtocol.Reason, out var reason) &&
            reason.ValueKind == JsonValueKind.Object ? reason : null;
    }

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
                return path.TryGetProperty(DropboxProtocol.Conflict, out var conflict) &&
                    UnionTag(conflict) is DropboxProtocol.TagFile or DropboxProtocol.TagFolder or
                        DropboxProtocol.FileAncestor
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
        return IsDocumentedSummaryReason(reason) ? CauseOf(reason) : null;
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
