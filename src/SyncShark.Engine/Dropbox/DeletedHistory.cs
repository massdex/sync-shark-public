using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DeletedHistory operations for the dropbox module.</summary>
public sealed class DeletedHistory
{
    private const string GetMetadata = "files/get_metadata";
    private const string ListRevisions = "files/list_revisions";
    private const string PathMode = "path";
    private const string IsDeletedName = "is_deleted";
    private const string EntriesName = "entries";
    private const string ServerDeletedName = "server_deleted";
    private const string ContentHashName = "content_hash";
    private const string IdName = "id";
    private const string RevisionName = "rev";
    private const string PathDisplayName = "path_display";
    private const string ClientModifiedFormat = "yyyy-MM-ddTHH:mm:ssZ";
    private const int DeletionHistoryHeadLimit = 1;

    /// <summary>Read operation on the supplied facts.</summary>
    public Task<DeletionEvidence> ReadAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct) =>
        ReadAsync(api, pair, path, false, ct);

    /// <summary>Read exact file revision evidence or folder ancestor history according to the selected kind.</summary>
    public async Task<DeletionEvidence> ReadAsync(DropboxApi api, PairConfig pair, string path, bool folder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        ct.ThrowIfCancellationRequested();
        if (pair.Roots is null || string.IsNullOrWhiteSpace(pair.Roots.Dropbox.RootNamespaceId))
            return new DeletionEvidence.Unknown(path, TextKey.RootUnbound);
        if (path.Length == 0 || !PathNames.IsTransferable(path))
            return new DeletionEvidence.Unknown(path, TextKey.SkipReservedName);
        try
        {
            return folder
                ? await ReadFolderAsync(api, pair, path, ct).ConfigureAwait(false)
                : await ReadFileAsync(api, pair, path, ct).ConfigureAwait(false);
        }
        catch (ItemFailedException error) { return new DeletionEvidence.Unknown(path, error.Cause); }
        catch (ChangedUnderneathException) { return new DeletionEvidence.Unknown(path, TextKey.FailChanged); }
        catch (Exception error) when (error is HttpRequestException or JsonException or
            InventoryIncompleteException or DropboxListing.RefusedException)
        {
            return new DeletionEvidence.Unknown(path, TextKey.ReadDropboxFailed);
        }
    }

    // One ordered evidence decision: each read decides whether the next is permitted, so a split could conclude a
    // deletion without the preceding identity proof.
    private async Task<DeletionEvidence> ReadFileAsync(DropboxApi api, PairConfig pair, string path,
        CancellationToken ct)
    {
        var target = await ReadMetadataAsync(api, pair, path, ct).ConfigureAwait(false);
        if (target?.Entry is { } occupant)
            return await ReadLiveAsync(api, pair, path, occupant, ct).ConfigureAwait(false);

        Head head;
        try { head = await ReadHeadAsync(api, pair, path, ct).ConfigureAwait(false); }
        catch (DropboxListing.RefusedException refused) when (refused.IsNotFound)
        {
            return new DeletionEvidence.NoRetainedHistory(path);
        }
        catch (Exception error) when (error is DropboxListing.RefusedException or JsonException or
            InventoryIncompleteException or HttpRequestException)
        {
            return new DeletionEvidence.HistoryUnavailable(path, TextKey.ReadDropboxFailed);
        }

        // No retained revision is positive evidence that the name never existed: an empty history list uploads
        // without a notice. Only a malformed head with entries but no identity is a failed read.
        if (!head.HasHead) return new DeletionEvidence.NoRetainedHistory(path);
        if (string.IsNullOrWhiteSpace(head.Id))
            return new DeletionEvidence.HistoryUnavailable(path, TextKey.ReadDropboxFailed);

        var current = await new DropboxListing().ReadByIdAsync(api, pair, head.Id!, ct).ConfigureAwait(false);
        if (current is not null) return Live(pair, path, current);
        if (!head.IsDeleted || !PathNames.Same(head.NativePath, DropboxPaths.Remote(pair, path)))
            return new DeletionEvidence.Unknown(path, TextKey.FailChanged);

        var time = DeletionTime(head.ServerDeleted, head.TimeWrongType);
        var hash = DeletionHash(head.ContentHash, head.HashWrongType);
        return new DeletionEvidence.DeletedFile(path, pair.Roots!.Dropbox.RootNamespaceId, head.Id!,
            string.IsNullOrWhiteSpace(head.Revision) ? null : head.Revision,
            time.Value, time.Reason, hash.Value, hash.Reason);
    }

    private async Task<DeletionEvidence> ReadFolderAsync(DropboxApi api, PairConfig pair, string path,
        CancellationToken ct)
    {
        for (var probe = path; probe.Length != 0; probe = PathNames.Parent(probe))
        {
            var metadata = await ReadMetadataAsync(api, pair, probe, ct).ConfigureAwait(false);
            if (metadata is null) continue;
            if (metadata.Entry is null) return new DeletionEvidence.DeletedPath(path, probe);
            if (PathNames.Same(probe, path))
                return await ReadLiveAsync(api, pair, path, metadata.Entry, ct).ConfigureAwait(false);
        }
        return new DeletionEvidence.NoFolderTombstone(path);
    }

    private static async Task<Listed?> ReadMetadataAsync(DropboxApi api, PairConfig pair, string path,
        CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await DropboxListing.ReadReplyAsync(api, GetMetadata,
                new { path = DropboxPaths.Remote(pair, path), include_deleted = true }, ct).ConfigureAwait(false);
        }
        catch (DropboxListing.RefusedException refused) when (refused.IsNotFound) { return null; }
        using (document)
        {
            var listed = new DropboxEntries().Decode(document.RootElement, pair);
            if (!PathNames.Same(listed.Path, path)) throw new ChangedUnderneathException(path);
            return listed;
        }
    }

    private async Task<DeletionEvidence> ReadLiveAsync(DropboxApi api, PairConfig pair, string path, Entry entry,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            return new DeletionEvidence.Unknown(path, TextKey.ReadDropboxFailed);
        var current = await new DropboxListing().ReadByIdAsync(api, pair, entry.Id, ct).ConfigureAwait(false);
        return current is null ? new DeletionEvidence.Unknown(path, TextKey.FailChanged) : Live(pair, path, current);
    }

    private static DeletionEvidence Live(PairConfig pair, string path, IdentityLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.Domain) || !PathNames.IsDropboxRoot(location.NativePath) ||
            string.IsNullOrWhiteSpace(location.Entry.Id))
            return new DeletionEvidence.Unknown(path, TextKey.ReadDropboxFailed);
        return string.Equals(location.Domain, pair.Roots!.Dropbox.RootNamespaceId, StringComparison.Ordinal) &&
            PathNames.Same(location.NativePath, DropboxPaths.Remote(pair, path))
                ? new DeletionEvidence.LiveAtTarget(path, location)
                : new DeletionEvidence.LiveElsewhere(path, location);
    }

    private static async Task<Head> ReadHeadAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct)
    {
        using var document = await DropboxListing.ReadReplyAsync(api, ListRevisions, new
        {
            path = DropboxPaths.Remote(pair, path), mode = PathMode, limit = DeletionHistoryHeadLimit
        }, ct).ConfigureAwait(false);
        return ParseHead(document.RootElement);
    }

    private static Head ParseHead(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var isDeleted = Bool(root, IsDeletedName);
        if (!root.TryGetProperty(EntriesName, out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new JsonException();
        var hasHead = entries.GetArrayLength() != 0;
        var head = hasHead ? entries[0] : default(JsonElement);
        if (hasHead && head.ValueKind != JsonValueKind.Object) throw new JsonException();
        // The route reports server_deleted beside entries; a value on the head element itself wins.
        var (deleted, timeWrongType) = OptionalText(head, root, ServerDeletedName);
        var (hash, hashWrongType) = OptionalText(head, default, ContentHashName);
        return new Head(isDeleted, hasHead, deleted, timeWrongType, OptionalString(head, IdName),
            OptionalString(head, RevisionName), OptionalString(head, PathDisplayName), hash, hashWrongType);
    }

    /// <summary>Parses the provider deletion time; a missing or malformed value keeps the coherent remainder usable.</summary>
    public static (DateTimeOffset? Value, TextKey? Reason) DeletionTime(string? value, bool wrongType)
    {
        if (wrongType) return (null, TextKey.DeletionTimeMalformed);
        if (value is null) return (null, TextKey.DeletionTimeMissing);
        return DateTimeOffset.TryParseExact(value, ClientModifiedFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? (parsed, null)
                : (null, TextKey.DeletionTimeMalformed);
    }

    /// <summary>Parses the retained content hash; the provider spelling is normalised to lower case.</summary>
    public static (string? Value, TextKey? Reason) DeletionHash(string? value, bool wrongType)
    {
        if (wrongType) return (null, TextKey.DeletionHashMalformed);
        if (value is null) return (null, TextKey.DeletionHashMissing);
        return ContentHash.IsValid(value) ? (value.ToLowerInvariant(), null) : (null, TextKey.DeletionHashMalformed);
    }

    private static (string? Value, bool WrongType) OptionalText(JsonElement primary, JsonElement fallback,
        string name)
    {
        var element = Field(primary, name);
        if (element.ValueKind == JsonValueKind.Undefined) element = Field(fallback, name);
        return element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => (null, false),
            JsonValueKind.String => (element.GetString(), false),
            _ => (null, true)
        };
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException()
        };
    }

    private static bool Bool(JsonElement element, string name)
    {
        var value = Field(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            _ => throw new JsonException()
        };
    }

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private readonly record struct Head(bool IsDeleted, bool HasHead, string? ServerDeleted, bool TimeWrongType,
        string? Id, string? Revision, string? NativePath, string? ContentHash, bool HashWrongType);
}
