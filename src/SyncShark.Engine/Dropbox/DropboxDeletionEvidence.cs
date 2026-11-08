using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Reads targeted deletion evidence through the shared reply and current-identity owners.</summary>
public sealed partial class DropboxDeletionEvidence(DropboxApi api, PairConfig pair, DropboxListing listing)
{
    /// <summary>Returns evidence for the exact mapped target; the candidate kind selects file or folder history.</summary>
    public partial Task<DeletionEvidence> ReadDeletionEvidenceAsync(
        string path, bool candidateIsFolder, CancellationToken ct);

    public async partial Task<DeletionEvidence> ReadDeletionEvidenceAsync(
        string path, bool candidateIsFolder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pair.Roots is null || string.IsNullOrWhiteSpace(pair.Roots.Dropbox.RootNamespaceId))
            return new DeletionEvidence.Unknown(path, TextKey.RootUnbound);
        if (path.Length == 0 || !PathNames.IsTransferable(path))
            return new DeletionEvidence.Unknown(path, TextKey.SkipReservedName);
        try
        {
            return candidateIsFolder
                ? await ReadFolderAsync(path, ct).ConfigureAwait(false)
                : await ReadFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (ItemFailedException error)
        {
            return new DeletionEvidence.Unknown(path, error.Cause);
        }
        catch (ChangedUnderneathException)
        {
            return new DeletionEvidence.Unknown(path, TextKey.FailChanged);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InventoryIncompleteException)
        {
            return new DeletionEvidence.Unknown(path, TextKey.ReadDropboxFailed);
        }
    }

    // One ordered evidence decision: each read decides whether the next is permitted, so a split could conclude a deletion without the preceding identity proof.
    private async Task<DeletionEvidence> ReadFileAsync(string path, CancellationToken ct)
    {
        var target = await ReadMetadataAsync(path, ct).ConfigureAwait(false);
        if (target?.Entry is { } occupant)
            return await ReadLiveAsync(path, occupant, ct).ConfigureAwait(false);

        (bool IsDeleted, bool HasHead, string? ServerDeleted, bool TimeWrongType,
            string? Id, string? Revision, string? NativePath, string? ContentHash, bool HashWrongType) head;
        try
        {
            head = await api.RpcAsync(DropboxProtocol.ListRevisions,
                new { path = DropboxPaths.Remote(pair, path), mode = DropboxProtocol.Path,
                    limit = DropboxProtocol.DeletionHistoryHeadLimit },
                DropboxReplyShapes.HistoryHead(), new RequestAttempt(new object(), AttemptOwner.Request), ct)
                .ConfigureAwait(false);
        }
        catch (DropboxApiException error) when (DropboxErrors.IsStructuredPathNotFound(error))
        {
            return new DeletionEvidence.NoRetainedHistory(path);
        }
        catch (DropboxApiException error) when (DropboxErrors.IsHistoryOnlyFailure(DropboxProtocol.ListRevisions, error))
        {
            return new DeletionEvidence.HistoryUnavailable(path, TextKey.ReadDropboxFailed);
        }
        catch (JsonException)
        {
            return new DeletionEvidence.HistoryUnavailable(path, TextKey.ReadDropboxFailed);
        }

        // No retained revision is positive evidence that the name never existed: an empty history list uploads
        // without a notice. Only a malformed head with entries but no identity is a failed read.
        if (!head.HasHead)
            return new DeletionEvidence.NoRetainedHistory(path);
        if (string.IsNullOrWhiteSpace(head.Id))
            return new DeletionEvidence.HistoryUnavailable(path, TextKey.ReadDropboxFailed);
        var current = await listing.ReadByIdAsync(head.Id,
            new RequestAttempt(new object(), AttemptOwner.Request), ct).ConfigureAwait(false);
        if (current is not null) return Live(path, current);
        if (!head.IsDeleted || !PathNames.Same(head.NativePath, DropboxPaths.Remote(pair, path)))
            return new DeletionEvidence.Unknown(path, TextKey.FailChanged);

        var time = DeletionTime(head.ServerDeleted, head.TimeWrongType);
        var hash = DeletionHash(head.ContentHash, head.HashWrongType);
        return new DeletionEvidence.DeletedFile(path, pair.Roots!.Dropbox.RootNamespaceId, head.Id,
            string.IsNullOrWhiteSpace(head.Revision) ? null : head.Revision,
            time.Value, time.Reason, hash.Value, hash.Reason);
    }

    private async Task<DeletionEvidence> ReadFolderAsync(string path, CancellationToken ct)
    {
        for (var probe = path; probe.Length != 0; probe = PathNames.Parent(probe))
        {
            var metadata = await ReadMetadataAsync(probe, ct).ConfigureAwait(false);
            if (metadata is null) continue;
            if (metadata.Entry is null) return new DeletionEvidence.DeletedPath(path, probe);
            if (PathNames.Same(probe, path))
                return await ReadLiveAsync(path, metadata.Entry, ct).ConfigureAwait(false);
        }
        return new DeletionEvidence.NoFolderTombstone(path);
    }

    private async Task<Listed?> ReadMetadataAsync(string path, CancellationToken ct)
    {
        try
        {
            var metadata = await api.RpcAsync(DropboxProtocol.GetMetadata,
                new { path = DropboxPaths.Remote(pair, path), include_deleted = true },
                DropboxReplyShapes.Listed(pair), new RequestAttempt(new object(), AttemptOwner.Request), ct)
                .ConfigureAwait(false);
            if (!PathNames.Same(metadata.Path, path)) throw new ChangedUnderneathException(path);
            return metadata;
        }
        catch (DropboxApiException error) when (DropboxErrors.IsStructuredPathNotFound(error))
        {
            return null;
        }
    }

    private async Task<DeletionEvidence> ReadLiveAsync(string path, Entry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            return new DeletionEvidence.Unknown(path, TextKey.ReadDropboxFailed);
        var current = await listing.ReadByIdAsync(entry.Id,
            new RequestAttempt(new object(), AttemptOwner.Request), ct).ConfigureAwait(false);
        return current is null ? new DeletionEvidence.Unknown(path, TextKey.FailChanged) : Live(path, current);
    }

    private DeletionEvidence Live(string path, IdentityLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.Domain) || !PathNames.IsDropboxRoot(location.NativePath) ||
            string.IsNullOrWhiteSpace(location.Entry.Id))
            return new DeletionEvidence.Unknown(path, TextKey.ReadDropboxFailed);
        return string.Equals(location.Domain, pair.Roots!.Dropbox.RootNamespaceId, StringComparison.Ordinal) &&
            PathNames.Same(location.NativePath, DropboxPaths.Remote(pair, path))
                ? new DeletionEvidence.LiveAtTarget(path, location)
                : new DeletionEvidence.LiveElsewhere(path, location);
    }

    private static (DateTimeOffset? Value, TextKey? Reason) DeletionTime(string? value, bool wrongType)
    {
        if (wrongType) return (null, TextKey.DeletionTimeMalformed);
        if (value is null) return (null, TextKey.DeletionTimeMissing);
        return DateTimeOffset.TryParseExact(value, DropboxProtocol.ClientModifiedFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? (parsed, null) : (null, TextKey.DeletionTimeMalformed);
    }

    private static (string? Value, TextKey? Reason) DeletionHash(string? value, bool wrongType)
    {
        if (wrongType) return (null, TextKey.DeletionHashMalformed);
        if (value is null) return (null, TextKey.DeletionHashMissing);
        return SyncContentIdentity.IdentityMatchesAlgorithm(SyncContentIdentity.DropboxBlockSha256, value)
            ? (value.ToLowerInvariant(), null) : (null, TextKey.DeletionHashMalformed);
    }
}
