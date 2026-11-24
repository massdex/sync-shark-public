using System.Globalization;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Distinguishes retained deletion evidence from a live item or an inconclusive lookup.</summary>
public sealed class DeletedHistory
{
    /// <summary>Reads deletion evidence for a file using its exact path and retained revisions.</summary>
    public Task<DeletionEvidence> ReadAsync(DropboxApi api, PairConfig pair, string path, CancellationToken ct) =>
        ReadAsync(api, pair, path, false, ct);

    /// <summary>Read exact file revision evidence or folder ancestor history according to the selected kind. A lookup
    /// failure is never absence; only coherent positive evidence constructs a deletion.</summary>
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
        catch (ItemFailedException error) when (error.Disposition == FailureDisposition.TemporaryNoEffect) { throw; }
        catch (ItemFailedException error) { return new DeletionEvidence.Unknown(path, error.Cause); }
        catch (ChangedUnderneathException) { return new DeletionEvidence.Unknown(path, TextKey.FailChanged); }
        catch (Exception error) when (error is not (OperationCanceledException or NotImplementedException))
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

        try
        {
            var (absent, head) = await DropboxListing.ReadReplyAsync(api, DropboxProtocol.ListRevisions, path, new
            {
                path = DropboxPaths.Remote(pair, path), mode = DropboxProtocol.Path, limit = DropboxProtocol.DeletionHistoryHeadLimit
            }, DropboxReplyShapes.HistoryHead(), ct);
            if (absent || !head.HasHead) return new DeletionEvidence.NoRetainedHistory(path);
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
        catch (ItemFailedException error) when (error.Disposition != FailureDisposition.TemporaryNoEffect)
        {
            return new DeletionEvidence.HistoryUnavailable(path, error.Cause);
        }
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
            break;
        }
        return new DeletionEvidence.NoFolderTombstone(path);
    }

    private static async Task<Listed?> ReadMetadataAsync(DropboxApi api, PairConfig pair, string path,
        CancellationToken ct)
    {
        var (absent, listed) = await DropboxListing.ReadReplyAsync(api, DropboxProtocol.GetMetadata, path,
            new { path = DropboxPaths.Remote(pair, path), include_deleted = true }, DropboxReplyShapes.Listed(pair), ct);
        if (absent) return null;
        if (!PathNames.Same(listed.Path, path)) throw new ChangedUnderneathException(path);
        return listed;
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

    /// <summary>Parses the provider deletion time; a missing or malformed value keeps the coherent remainder usable.</summary>
    private static (DateTimeOffset? Value, TextKey? Reason) DeletionTime(string? value, bool wrongType)
    {
        if (wrongType) return (null, TextKey.DeletionTimeMalformed);
        if (value is null) return (null, TextKey.DeletionTimeMissing);
        return DateTimeOffset.TryParseExact(value, DropboxProtocol.ClientModifiedFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? (parsed, null)
                : (null, TextKey.DeletionTimeMalformed);
    }

    /// <summary>Parses the retained content hash; the provider spelling is normalised to lower case.</summary>
    private static (string? Value, TextKey? Reason) DeletionHash(string? value, bool wrongType)
    {
        if (wrongType) return (null, TextKey.DeletionHashMalformed);
        if (value is null) return (null, TextKey.DeletionHashMissing);
        return ContentHash.IsValid(value) ? (value.ToLowerInvariant(), null) : (null, TextKey.DeletionHashMalformed);
    }
}
