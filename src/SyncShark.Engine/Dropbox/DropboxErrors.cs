using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The 409 mapping of section 5.4, by error_summary prefix. One owner classifies every Dropbox reply:
/// a single response and each member of a batch reply, from its route and the request evidence. A documented,
/// well-shaped refusal proves no effect; anything else is an unknown outcome for a dispatched write, never a refusal.</summary>
public sealed class DropboxErrors
{
    /// <summary>What the caller observed about the request that produced a failure. ProvenNoEffect is the caller's
    /// proof that a dispatched write was not applied; Transient is positive transport evidence.</summary>
    public readonly record struct RequestEvidence(bool Read, bool Dispatched, bool ProvenNoEffect, bool Transient);

    /// <summary>One classified failure: the cause, the disposition, whether it is an attributable refusal that proves
    /// the write had no effect (a folder split may act on it), and whether a usable observation proved the target
    /// identity absent (a read's documented miss, or a by-id delete member's exact structured path_lookup/not_found;
    /// absence is never the same as our request having deleted it).</summary>
    public readonly record struct Classified(
        TextKey Cause, FailureDisposition Disposition, bool ProvenNoEffect, bool Absent = false);

    /// <summary>Classify one single response. <paramref name="body"/> is the parsed reply. A documented write
    /// contention takes the write rule on any status; a documented refusal proves no effect; absence needs the exact
    /// documented miss. Otherwise the request evidence decides, and an unproved dispatched write stays unknown.
    /// <paramref name="expectedIsNull"/> is true when a free target was required (an add or a create).</summary>
    public Classified Classify(HttpStatusCode status, JsonElement body, string route, bool expectedIsNull,
        RequestEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(route);
        var summary = Summary(body);
        var failure = Failure(body);
        var structured = UnionTag(failure) is not null;

        // Reason-first: documented write contention takes the write rule on any status (product-0518/0546).
        if (IsMutatingRoute(route) && DocumentsWriteThrottle(summary, failure))
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);

        if (status == HttpStatusCode.Conflict)
        {
            // Structured evidence decides absence; the summary fallback must be the exact documented summary.
            if (route == DropboxProtocol.GetMetadata && IsExactAbsence(summary, failure))
                return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true, Absent: true);

            if (IsUploadRoute(route))
            {
                // A structured refusal is authoritative; when it proves nothing we do not fall back to the summary.
                if (structured)
                    return WriteRefusal(failure, expectedIsNull) ?? Unclassified(evidence);
                return SingleSummaryRefusal(route, summary, expectedIsNull) ?? Unclassified(evidence);
            }

            return SingleSummaryRefusal(route, summary, expectedIsNull) ?? Unclassified(evidence);
        }

        return Unclassified(evidence);
    }

    /// <summary>Classify one member of a batch reply body (HTTP 200 with a per-member failure union). Only the
    /// upload-finish and create routes use the write-union decoding; a delete member proves no outcome; an unknown tag,
    /// malformed member or unrecognized route stays Unknown so only its own position is affected.</summary>
    public Classified ClassifyMember(JsonElement failure, string route, bool expectedIsNull)
    {
        ArgumentNullException.ThrowIfNull(route);
        var tag = UnionTag(failure);
        if (route is DropboxProtocol.DeleteBatch or DropboxProtocol.DeleteBatchCheck) return DeleteMember(failure);
        if (route is DropboxProtocol.MoveBatch or DropboxProtocol.MoveBatchCheck) return MoveMember(failure, tag);
        if (route == DropboxProtocol.UploadSessionFinishBatch)
            return WriteRefusal(failure, expectedIsNull) ?? Unproven();
        return Unproven();
    }

    /// <summary>The frozen two-argument surface: no route or request evidence, so an unclassified refusal.</summary>
    public FailureDisposition Classify(HttpStatusCode status, JsonElement error) =>
        Classify(status, error, string.Empty, false, default).Disposition;

    // A write outside a 409, or an unprovable 409, follows the request evidence: an unsent request is permanent; a read
    // is temporary only on positive transient evidence (so a decided 500 stays a failure); a dispatched write that the
    // caller did not prove had no effect is unknown, and a proved no-effect write is temporary only when transient.
    private static Classified Unclassified(RequestEvidence evidence)
    {
        if (!evidence.Dispatched)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        if (evidence.Read)
            return new Classified(TextKey.DropboxRefused,
                evidence.Transient ? FailureDisposition.TemporaryNoEffect : FailureDisposition.Permanent, true);
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
        if (route == DropboxProtocol.Delete && Prefixed(summary, DropboxProtocol.PathLookupNotFound))
            return Refused(TextKey.FailChanged);
        if (route == DropboxProtocol.Move && Prefixed(summary, DropboxProtocol.FromLookupNotFound))
            return Refused(TextKey.FailChanged);
        if (route == DropboxProtocol.Move && Prefixed(summary, DropboxProtocol.ToConflict))
            return Refused(TextKey.ClashNameTaken);
        if (route == DropboxProtocol.Move && Prefixed(summary, DropboxProtocol.TooManyFiles))
            return Refused(TextKey.FailDropboxTooManyFiles);
        if ((route is DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish) &&
            Prefixed(summary, DropboxProtocol.PathConflict))
            return Refused(expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged);
        if (route == DropboxProtocol.CreateFolder && Prefixed(summary, DropboxProtocol.PathConflict))
            return Refused(TextKey.ClashNameTaken);
        return WriteCause(route, summary) is { } cause ? Refused(cause) : null;
    }

    // Decode one structured write union (an upload or create member, single or batch). A refusal counts only when it
    // is route-appropriate and well shaped; an unknown tag, a missing conflict subtype or an unknown reason is null.
    private static Classified? WriteRefusal(JsonElement failure, bool expectedIsNull)
    {
        var tag = UnionTag(failure);
        if (tag is null) return null;
        if (tag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
        if (tag != DropboxProtocol.Path)
            return IsDocumentedTopLevel(tag) ? Refused(TextKey.DropboxRefused) : null;
        if (WriteError(failure) is not { } write) return null;
        var writeTag = UnionTag(write);
        if (writeTag is null) return null;
        if (writeTag == DropboxProtocol.Conflict)
            return IsWellFormedConflict(write)
                ? Refused(expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged)
                : null;
        if (writeTag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
        return IsDocumentedWriteReason(writeTag) ? Refused(CauseOf(writeTag)) : null;
    }

    // No DeleteError tag proves the effect was not applied, so every delete member's effect stays Unknown. The one
    // exception is the exact structured by-id path_lookup/not_found: it proves the selected identity is gone (Absent),
    // which is not the same as our request having deleted it, so the effect remains Unknown.
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

    private static Classified MoveMember(JsonElement failure, string? tag)
    {
        if (tag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.WaitTooManyWrites, FailureDisposition.TemporaryNoEffect, true);
        if (tag == DropboxProtocol.RelocationError &&
            failure.TryGetProperty(DropboxProtocol.RelocationError, out var relocation) &&
            MoveCause(relocation) is { } known)
            return new Classified(known,
                known == TextKey.WaitTooManyWrites
                    ? FailureDisposition.TemporaryNoEffect : FailureDisposition.Permanent, true);
        return Unproven();
    }

    private static bool IsDocumentedTopLevel(string tag) => tag is
        DropboxProtocol.TagOther or DropboxProtocol.PropertiesError or DropboxProtocol.PayloadTooLarge or
        DropboxProtocol.ContentHashMismatch or DropboxProtocol.LookupFailed;

    private static bool IsDocumentedWriteReason(ReadOnlySpan<char> tag) =>
        tag.SequenceEqual(DropboxProtocol.MalformedPath) || tag.SequenceEqual(DropboxProtocol.DisallowedName) ||
        tag.SequenceEqual(DropboxProtocol.NoWritePermission) || tag.SequenceEqual(DropboxProtocol.InsufficientSpace) ||
        tag.SequenceEqual(DropboxProtocol.TeamFolder) || tag.SequenceEqual(DropboxProtocol.OperationSuppressed);

    private static bool IsWellFormedConflict(JsonElement write) =>
        write.TryGetProperty(DropboxProtocol.Conflict, out var conflict) &&
        UnionTag(conflict) is DropboxProtocol.TagFile or DropboxProtocol.TagFolder or DropboxProtocol.FileAncestor;

    private static bool IsUploadRoute(string route) => route is
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend or
        DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch or DropboxProtocol.CreateFolder;

    // Every route that can change the provider's state, so the reason-first write-contention rule applies.
    private static bool IsMutatingRoute(string route) => route is
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend or
        DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch or DropboxProtocol.CreateFolder or
        DropboxProtocol.Delete or DropboxProtocol.DeleteBatch or DropboxProtocol.DeleteBatchCheck or
        DropboxProtocol.Move or DropboxProtocol.MoveBatch or DropboxProtocol.MoveBatchCheck;

    // A documented write contention, by the structured union (top level or nested WriteError) or a complete summary tag.
    private static bool DocumentsWriteThrottle(string summary, JsonElement failure)
    {
        if (UnionTag(failure) == DropboxProtocol.TooManyWriteOperations) return true;
        if (WriteError(failure) is { } write && UnionTag(write) == DropboxProtocol.TooManyWriteOperations) return true;
        return HasSegment(summary, DropboxProtocol.TooManyWriteOperations);
    }

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
        if (body.TryGetProperty(DropboxProtocol.Error, out var error) && error.ValueKind == JsonValueKind.Object)
            return error;
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

    // Absence needs a usable observation: the exact structured 409 path/not_found when a structured error is present,
    // else the exact summary. A prefix match or a contradictory structured refusal is not absence (M14/BC21).
    private static bool IsExactAbsence(string summary, JsonElement failure)
    {
        if (failure.ValueKind == JsonValueKind.Object) return IsStructuredNotFound(failure);
        return summary.AsSpan().SequenceEqual(DropboxProtocol.PathNotFound);
    }

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

    /// <summary>The specific cause of a documented write refusal for route, else null (not a proven refusal).</summary>
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
        return IsDocumentedWriteReason(reason) ? CauseOf(reason) : null;
    }

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
