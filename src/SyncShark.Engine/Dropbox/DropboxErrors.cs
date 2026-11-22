using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The 409 mapping of section 5.4, by error_summary prefix. One owner classifies every Dropbox reply:
/// a single response and each member of a batch reply, from its route and the request evidence.</summary>
public sealed class DropboxErrors
{
    /// <summary>What the caller observed about the request that produced a failure. ProvenNoEffect is the caller's
    /// proof that a dispatched write was not applied; Transient is positive transport evidence.</summary>
    public readonly record struct RequestEvidence(bool Read, bool Dispatched, bool ProvenNoEffect, bool Transient);

    /// <summary>One classified failure: the cause, the disposition, whether the reply proves the write had no effect
    /// (an attributable refusal, which a folder split may act on), and whether a read proved its target absent.</summary>
    public readonly record struct Classified(
        TextKey Cause, FailureDisposition Disposition, bool ProvenNoEffect, bool Absent = false);

    /// <summary>Classify one single response. <paramref name="body"/> is the parsed reply; a documented 409 refusal,
    /// an absence, or the request evidence decides the outcome. <paramref name="expectedIsNull"/> is true when a free
    /// target was required (an add or a create), so a conflict is a taken name rather than a changed target.</summary>
    public Classified Classify(HttpStatusCode status, JsonElement body, string route, bool expectedIsNull,
        RequestEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (status != HttpStatusCode.Conflict) return Unclassified(evidence);

        var summary = Summary(body);
        var failure = Failure(body);
        if (route == DropboxProtocol.GetMetadata &&
            (IsStructuredNotFound(failure) || Matches(summary, DropboxProtocol.PathNotFound)))
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true, Absent: true);

        // Write routes report a structured union whose nested WriteError carries more than the summary prefix.
        if (IsWriteRoute(route) && UnionTag(failure) is { } tag)
            return Structured(failure, tag, expectedIsNull);

        return SingleSummary(route, summary, expectedIsNull);
    }

    /// <summary>Classify one member of a batch reply body (HTTP 200 with a per-member failure union). An unknown tag
    /// or malformed member proves no outcome, so only its own position is Unknown; a documented refusal is attributable.</summary>
    public Classified ClassifyMember(JsonElement failure, string route, bool expectedIsNull)
    {
        ArgumentNullException.ThrowIfNull(route);
        var tag = UnionTag(failure);
        if (route is DropboxProtocol.DeleteBatch or DropboxProtocol.DeleteBatchCheck) return DeleteMember(failure);
        if (route is DropboxProtocol.MoveBatch or DropboxProtocol.MoveBatchCheck) return MoveMember(failure, tag);
        return UploadMember(failure, tag, expectedIsNull);
    }

    /// <summary>The frozen two-argument surface: no route or request evidence, so an unclassified refusal.</summary>
    public FailureDisposition Classify(HttpStatusCode status, JsonElement error) =>
        Classify(status, error, string.Empty, false, default).Disposition;

    // A failure outside a 409: only a dispatched write without no-effect proof is Unknown; reads and unsent requests
    // are Permanent, and a proven no-effect write with positive transport evidence is temporary.
    private static Classified Unclassified(RequestEvidence evidence)
    {
        if (evidence.Read || !evidence.Dispatched)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        if (evidence.ProvenNoEffect && evidence.Transient)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.TemporaryNoEffect, true);
        return new Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
    }

    private static Classified SingleSummary(string route, string summary, bool expectedIsNull)
    {
        if (Matches(summary, DropboxProtocol.TooManyWriteOperations))
            return new Classified(TextKey.DropboxRefused, FailureDisposition.TemporaryNoEffect, true);
        var cause = route switch
        {
            DropboxProtocol.Download when Matches(summary, DropboxProtocol.PathNotFound) => TextKey.FailChanged,
            DropboxProtocol.Delete when Matches(summary, DropboxProtocol.PathLookupNotFound) => TextKey.FailChanged,
            DropboxProtocol.Move when Matches(summary, DropboxProtocol.FromLookupNotFound) => TextKey.FailChanged,
            DropboxProtocol.Move when Matches(summary, DropboxProtocol.TooManyFiles) =>
                TextKey.FailDropboxTooManyFiles,
            DropboxProtocol.Upload or DropboxProtocol.UploadSessionFinish
                when Matches(summary, DropboxProtocol.PathConflict) =>
                expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged,
            DropboxProtocol.CreateFolder when Matches(summary, DropboxProtocol.PathConflict) =>
                TextKey.ClashNameTaken,
            DropboxProtocol.Move when Matches(summary, DropboxProtocol.ToConflict) => TextKey.ClashNameTaken,
            _ => WriteCause(route, summary)
        };
        return new Classified(cause, FailureDisposition.Permanent, true);
    }

    private static Classified Structured(JsonElement failure, string tag, bool expectedIsNull)
    {
        if (tag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.TemporaryNoEffect, true);
        if (tag != DropboxProtocol.Path)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        var writeTag = WriteError(failure) is { } write ? UnionTag(write) : null;
        if (writeTag is null)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        if (writeTag == DropboxProtocol.Conflict)
            return new Classified(expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged,
                FailureDisposition.Permanent, true);
        if (writeTag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.TemporaryNoEffect, true);
        return new Classified(CauseOf(writeTag), FailureDisposition.Permanent, true);
    }

    // No DeleteError tag proves the effect was not applied, so every delete member is Unknown; its cause is the
    // documented lookup or write reason when the shape carries one.
    private static Classified DeleteMember(JsonElement failure)
    {
        var tag = UnionTag(failure);
        var cause = TextKey.DropboxRefused;
        if (tag == DropboxProtocol.PathLookup &&
            failure.TryGetProperty(DropboxProtocol.PathLookup, out var lookup) &&
            UnionTag(lookup) is { } lookupTag)
        {
            if (IsNotFoundTag(lookupTag)) cause = TextKey.FailChanged;
        }
        else if (tag == DropboxProtocol.PathWrite &&
                 failure.TryGetProperty(DropboxProtocol.PathWrite, out var write) &&
                 UnionTag(write) is { } writeTag)
        {
            cause = CauseOf(writeTag);
        }
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
        return new Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
    }

    private static Classified UploadMember(JsonElement failure, string? tag, bool expectedIsNull)
    {
        if (tag is null) return new Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
        if (tag == DropboxProtocol.TooManyWriteOperations)
            return new Classified(TextKey.DropboxRefused, FailureDisposition.TemporaryNoEffect, true);
        if (tag == DropboxProtocol.Path)
        {
            var writeTag = WriteError(failure) is { } write ? UnionTag(write) : null;
            if (writeTag is null)
                return new Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
            if (writeTag == DropboxProtocol.Conflict)
                return new Classified(expectedIsNull ? TextKey.ClashNameTaken : TextKey.FailChanged,
                    FailureDisposition.Permanent, true);
            if (writeTag == DropboxProtocol.TooManyWriteOperations)
                return new Classified(TextKey.DropboxRefused, FailureDisposition.TemporaryNoEffect, true);
            return new Classified(CauseOf(writeTag), FailureDisposition.Permanent, true);
        }
        if (IsDocumentedTopLevel(tag))
            return new Classified(TextKey.DropboxRefused, FailureDisposition.Permanent, true);
        return new Classified(TextKey.DropboxRefused, FailureDisposition.UnknownEffect, false);
    }

    private static bool IsDocumentedTopLevel(string tag) => tag is
        DropboxProtocol.TagOther or DropboxProtocol.PropertiesError or DropboxProtocol.PayloadTooLarge or
        DropboxProtocol.ContentHashMismatch or DropboxProtocol.LookupFailed;

    private static bool IsWriteRoute(string route) => route is
        DropboxProtocol.Upload or DropboxProtocol.UploadSessionStart or DropboxProtocol.UploadSessionAppend or
        DropboxProtocol.UploadSessionFinish or DropboxProtocol.UploadSessionFinishBatch or DropboxProtocol.CreateFolder;

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

    private static bool Matches(string summary, string prefix) => summary.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>True when a lookup tag is the provider's bare not_found token, the documented suffix of path/not_found.</summary>
    private static bool IsNotFoundTag(string? tag) =>
        tag is not null &&
        tag.AsSpan().SequenceEqual(DropboxProtocol.PathNotFound.AsSpan(DropboxProtocol.Path.Length + 1));

    /// <summary>True only for the exact structured 409 path/not_found union; a summary prefix match cannot prove it.</summary>
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
}
