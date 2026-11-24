using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The documented-refusal cause decoder: maps a structured relocation or a summary-only write refusal to the
/// specific TextKey cause (section 5.4). Kept apart from DropboxErrors, which decides what a cause proves about a
/// dispatched request; these are pure functions over the reply evidence alone.</summary>
internal static class DropboxErrorCauses
{
    internal static TextKey? MoveCause(JsonElement relocation)
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
    internal static TextKey? WriteCause(string route, string summary) => route switch
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
    internal static TextKey CauseOf(ReadOnlySpan<char> tag)
    {
        if (tag.SequenceEqual(DropboxProtocol.MalformedPath) || tag.SequenceEqual(DropboxProtocol.DisallowedName))
            return TextKey.FailDropboxNameRejected;
        if (tag.SequenceEqual(DropboxProtocol.NoWritePermission)) return TextKey.FailDropboxNoPermission;
        if (tag.SequenceEqual(DropboxProtocol.InsufficientSpace)) return TextKey.FailDropboxNoSpace;
        return TextKey.DropboxRefused;
    }

    internal static bool IsWellFormedConflict(JsonElement write) =>
        write.TryGetProperty(DropboxProtocol.Conflict, out var conflict) &&
        UnionTag(conflict) is DropboxProtocol.TagFile or DropboxProtocol.TagFolder or DropboxProtocol.FileAncestor;

    /// <summary>The union's string .tag, or null when the shape carries none.</summary>
    internal static string? UnionTag(JsonElement union) =>
        union.ValueKind == JsonValueKind.Object &&
        union.TryGetProperty(DropboxProtocol.Tag, out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()
            : null;

    /// <summary>True when a lookup tag is the provider's bare not_found token, the documented suffix of path/not_found.</summary>
    internal static bool IsNotFoundTag(string? tag) =>
        tag is not null &&
        tag.AsSpan().SequenceEqual(DropboxProtocol.PathNotFound.AsSpan(DropboxProtocol.Path.Length + 1));
}
