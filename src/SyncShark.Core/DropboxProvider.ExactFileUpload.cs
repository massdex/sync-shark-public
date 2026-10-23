using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Massdex.SyncShark;

// The provider seam for the one exact-file Dropbox upload. It adds no engine, client, token, scope or
// per-pair switch: it is the existing DropboxPilotClient, bound to one verified account, namespace and
// existing destination parent.

/// <summary>The exact result class of one create-only exact-file commit.</summary>
public enum SyncExactFileUploadDisposition
{
    Success,
    KnownNoEffect,
    Unknown
}

/// <summary>
/// One exact-file commit result. KnownNoEffect means the commit was transmitted and the provider
/// definitively did not create the object; Unknown means the result was not proven.
/// </summary>
public sealed record SyncExactFileUploadOutcome(SyncExactFileUploadDisposition Disposition,
    PilotUploadResult? Result, string Detail);

/// <summary>
/// One classified, definite absence of the exact destination parent. Every other failure keeps its own
/// error, so absence is never inferred from a status code, a transport loss or an unparsable body.
/// </summary>
public sealed class DropboxExactParentAbsentException(string message) : IOException(message);

public sealed partial class DropboxPilotClient
{
    /// <summary>
    /// One fresh exact-path folder lookup that returns the parent's stable identity. Only Dropbox's own
    /// exact path/not_found classification means absence; permission, authentication, transport, an
    /// unavailability, a malformed or an ambiguous response stays an error.
    /// </summary>
    internal async Task<string> RequireExactExistingFolderIdAsync(string exactParentPath,
        CancellationToken cancellationToken)
    {
        var normalized = PilotPath.NormalizeDropboxRoot(exactParentPath);
        using var document = await PostJsonAllowingMissingPathAsync(
            "https://api.dropboxapi.com/2/files/get_metadata",
            new { path = normalized, include_deleted = false, include_media_info = false }, cancellationToken);
        if (document is null)
            throw new DropboxExactParentAbsentException(
                "The exact Dropbox destination parent does not exist. This command never creates a parent folder.");
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty(".tag").GetString(), "folder", StringComparison.Ordinal))
            throw new InvalidDataException("The exact Dropbox destination parent is not a folder.");
        var display = root.TryGetProperty("path_display", out var displayValue)
            ? displayValue.GetString()
            : root.TryGetProperty("path_lower", out var lowerValue) ? lowerValue.GetString() : null;
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        if (!IsExactDropboxObjectIdentity(id) || string.IsNullOrWhiteSpace(display) ||
            !string.Equals(PilotPath.NormalizeDropboxRoot(display), normalized, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "Dropbox returned a different destination parent identity or path; the exact parent is refused.");
        return id!;
    }

    /// <summary>
    /// ONE create-only exact-file commit below the verified parent identity this client is bound to. The
    /// commit is mode add with strict_conflict and no autorename, so an occupied destination is refused by
    /// the provider without replacing anything. A definitive create-only conflict is a known no-effect;
    /// every other failure after transmission is an unproven effect that is never retried here.
    /// </summary>
    internal async Task<SyncExactFileUploadOutcome> CommitCreateOnlyExactFileAsync(string leafName, Stream source,
        string expectedContentHash, Action effectDispatched, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effectDispatched);
        if (!source.CanSeek) throw new IOException("The exact-file upload source must be seekable.");
        var leaf = PilotPath.NormalizeDropboxRelative(leafName);
        if (leaf.Contains('/')) throw new InvalidDataException("Exactly one leaf file name is required.");
        if (!PilotPath.IsDropboxFolderIdentity(_rootId))
            throw new InvalidDataException("The exact-file upload requires a bound destination parent identity.");
        if (source.Length > _singleRequestUploadLimit)
        {
            effectDispatched();
            var session = await UploadSessionAsync(leaf, source, expectedContentHash, null, null,
                static (_, _) => Task.CompletedTask, cancellationToken);
            return new SyncExactFileUploadOutcome(SyncExactFileUploadDisposition.Success, session,
                "Committed one exact file with a create-only upload-session finish.");
        }
        source.Position = 0;
        using var request = await AuthorizedAsync(HttpMethod.Post,
            "https://content.dropboxapi.com/2/files/upload", cancellationToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(CommitInfo(leaf, null), JsonOptions));
        request.Content = new UploadPayloadContent(new ThrottledLeaveOpenReadStream(source, _transferLimiter));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        Interlocked.Increment(ref _providerCallCount);
        effectDispatched();
        HttpResponseMessage response;
        try
        {
            response = await SendResourceRequestAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new DropboxEffectUncertainException(
                "The create-only upload response was interrupted after request transmission.", exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var rejection = await ClassifyCreateOnlyRejectionAsync(response, cancellationToken);
                if (rejection.CreateOnlyConflict)
                    return new SyncExactFileUploadOutcome(SyncExactFileUploadDisposition.KnownNoEffect, null,
                        "Dropbox refused the create-only commit with the reason code '" +
                        rejection.SafeReasonCode + "'" + rejection.Correlation +
                        ", so nothing was created, replaced, renamed or deleted.");
                throw new DropboxEffectUncertainException(
                    $"Dropbox returned HTTP {(int)response.StatusCode} with the reason code '" +
                    rejection.SafeReasonCode + "'" + rejection.Correlation +
                    " after the create-only upload was transmitted, so its exact effect is not proven.");
            }
            using var document = await ReadEffectJsonAsync(response,
                "The create-only upload success body was unavailable or malformed after request transmission.",
                cancellationToken);
            return new SyncExactFileUploadOutcome(SyncExactFileUploadDisposition.Success,
                ParseUploadResult(document.RootElement, expectedContentHash, source.Length),
                "Committed one exact file with a create-only conditional commit.");
        }
    }

    // One bounded, sanitized classification of a transmitted create-only commit rejection. Only
    // Dropbox's structured upload-error taxonomy is read: the single-request UploadError.path member
    // carries UploadWriteFailed as an ordinary struct, so Stone serializes its fields inline and the
    // create-only conflict discriminator is the WriteError at error.reason..tag, never a nested
    // error.path..tag. A typed create-only conflict additionally requires the original HTTP 409 and the
    // complete UploadWriteFailed payload (a recognised WriteConflictError subtype and a string
    // upload_session_id). The human-readable error_summary never proves an outcome, no required field
    // value is read, logged or reused, and no raw body, session id, authentication header or arbitrary
    // reflected text is retained.
    private async Task<UploadRejection> ClassifyCreateOnlyRejectionAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var requestId = BoundedUploadRequestId(response);
        try
        {
            using var error = await ReadBoundedJsonAsync(response, cancellationToken);
            var root = error.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var detail) ||
                detail.ValueKind != JsonValueKind.Object)
                return new UploadRejection(false, "unrecognized", requestId);
            var errorTag = ReadUnionTag(detail);
            string? reasonTag = null;
            string? conflictTag = null;
            string? propertiesTag = null;
            var uploadWriteFailedComplete = false;
            if (string.Equals(errorTag, "path", StringComparison.Ordinal) &&
                detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.Object)
            {
                reasonTag = ReadUnionTag(reason);
                if (string.Equals(reasonTag, "conflict", StringComparison.Ordinal) &&
                    reason.TryGetProperty("conflict", out var conflict) &&
                    conflict.ValueKind == JsonValueKind.Object)
                    conflictTag = ReadUnionTag(conflict);
                // UploadWriteFailed requires both reason and a string upload_session_id; the value is
                // type-checked only and is never read, logged or reused.
                uploadWriteFailedComplete = detail.TryGetProperty("upload_session_id", out var sessionId) &&
                                            sessionId.ValueKind == JsonValueKind.String;
            }
            else if (string.Equals(errorTag, "properties_error", StringComparison.Ordinal) &&
                     detail.TryGetProperty("properties_error", out var properties) &&
                     properties.ValueKind == JsonValueKind.Object)
            {
                propertiesTag = ReadUnionTag(properties);
            }
            var code = errorTag switch
            {
                "path" => SafeWriteErrorCode(reasonTag, conflictTag),
                "properties_error" => propertiesTag is not null && RecognisedPropertyErrorTags.Contains(propertiesTag)
                    ? "properties_error/" + propertiesTag
                    : "properties_error",
                "payload_too_large" or "content_hash_mismatch" or "encryption_not_supported" => errorTag,
                null => "unrecognized",
                _ => "other"
            };
            return new UploadRejection(ProvesCreateOnlyConflict(response, errorTag, reasonTag, conflictTag,
                uploadWriteFailedComplete), code, requestId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or
                                              HttpRequestException or TimeoutException)
        {
            // A rejected body whose read stalls or whose transport fails is never read, never reflected and
            // never retried; cancellation still propagates on the existing safe budget and caller-cancellation
            // paths. The stall surfaces here as TimeoutException, so the effect is classified unproven and the
            // caller marks it uncertain.
            return new UploadRejection(false, "unreadable", requestId);
        }
    }

    // A typed create-only conflict needs the original 409, the recognised UploadError.path reason union,
    // a recognised WriteConflictError subtype and the complete UploadWriteFailed struct. Any contradictory
    // status or incomplete or malformed shape leaves the effect unproven.
    private static bool ProvesCreateOnlyConflict(HttpResponseMessage response, string? errorTag, string? reasonTag,
        string? conflictTag, bool uploadWriteFailedComplete) =>
        response.StatusCode == HttpStatusCode.Conflict &&
        string.Equals(errorTag, "path", StringComparison.Ordinal) &&
        string.Equals(reasonTag, "conflict", StringComparison.Ordinal) &&
        conflictTag is not null && RecognisedWriteConflictTags.Contains(conflictTag) &&
        uploadWriteFailedComplete;

    // The classified rejection keeps only a finite allowlisted reason code and the bounded validated
    // provider correlation value; the raw provider body is never retained or returned.
    private readonly record struct UploadRejection(bool CreateOnlyConflict, string SafeReasonCode, string? RequestId)
    {
        internal string Correlation => RequestId is { } id ? ", request id " + id : string.Empty;
    }

    private static readonly HashSet<string> RecognisedWriteErrorTags = new(StringComparer.Ordinal)
    {
        "malformed_path", "conflict", "no_write_permission", "insufficient_space", "disallowed_name",
        "team_folder", "operation_suppressed", "too_many_write_operations", "access_restricted",
        "team_folder_insufficient_space", "member_folder_insufficient_space", "upload_traffic_limit_reached"
    };

    private static readonly HashSet<string> RecognisedWriteConflictTags = new(StringComparer.Ordinal)
    {
        "file", "folder", "file_ancestor"
    };

    private static readonly HashSet<string> RecognisedPropertyErrorTags = new(StringComparer.Ordinal)
    {
        "property_field_too_large", "does_not_fit_template", "duplicate_property_groups"
    };

    // Every unrecognised or absent tag collapses to one fixed code so a provider-controlled string is
    // never reflected into CLI output.
    private static string SafeWriteErrorCode(string? reasonTag, string? conflictTag)
    {
        if (reasonTag is null) return "path";
        if (!RecognisedWriteErrorTags.Contains(reasonTag)) return "path/other";
        if (!string.Equals(reasonTag, "conflict", StringComparison.Ordinal)) return "path/" + reasonTag;
        return conflictTag is not null && RecognisedWriteConflictTags.Contains(conflictTag)
            ? "path/conflict/" + conflictTag
            : "path/conflict";
    }

    private static string? ReadUnionTag(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(".tag", out var tag) &&
        tag.ValueKind == JsonValueKind.String
            ? tag.GetString()
            : null;

    // Dropbox's correlation header is retained only as a bounded conservative ASCII token; anything
    // outside that shape (control characters, whitespace, an oversized value) is dropped entirely.
    private static string? BoundedUploadRequestId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Dropbox-Request-Id", out var values)) return null;
        var candidate = values.FirstOrDefault();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 64) return null;
        foreach (var character in candidate)
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '_' or '-'))
                return null;
        return candidate;
    }
}
