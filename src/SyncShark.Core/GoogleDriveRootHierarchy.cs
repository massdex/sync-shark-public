using System.Text.Json;

namespace Massdex.SyncShark;

// CC2D: the Google Drive implementation of the one provider-neutral cloud root hierarchy capability.
//
// The selected root and every ancestor are read from fresh provider metadata (files.get) on every pass. No
// browser display path, cached inventory, connection label or shortcut target is used, and the terminal
// corpus root is positively identified instead of being assumed from a missing or empty parent list. The
// whole bound chain is read twice inside one call; the two passes must agree on every id, parent, kind,
// trash state, drive corpus and provider version or the ancestry is refused.
public sealed partial class GoogleDrivePilotClient : ISyncAuthorityCloudRootHierarchySource
{
    /// <summary>The bounded depth of one provider root hierarchy walk.</summary>
    public const int MaximumRootHierarchyDepth = 128;

    /// <summary>The My Drive alias. It is an alias, never the opaque id files/root returns.</summary>
    public const string MyDriveRootAlias = "root";

    public async Task<SyncAuthorityCloudRootHierarchyEvidence> ReadRootHierarchyAsync(
        SyncAuthorityCloudRootHierarchyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GoogleDriveProviderConfiguration.ValidateConnection(_connection);
        request.RequireBoundTo(SyncProviders.GoogleDrive, _connection.ConnectionId, _connection.PermissionId,
            _rootFolderId);
        // The retained provider-account id is a derivation over this connection's own native account tuple;
        // a request that names another retained account is refused before any provider read.
        string expectedProviderAccountId;
        try
        {
            expectedProviderAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive,
                _connection.PermissionId, _connection.AccountKey, _connection.AccountKey);
        }
        catch (ArgumentException error)
        {
            throw new SyncAuthorityCloudRootHierarchyException(
                "This Google Drive connection has no complete retained account identity, so no root hierarchy is read: " +
                error.Message);
        }
        if (!string.Equals(request.ProviderAccountId, expectedProviderAccountId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The requested cloud root hierarchy read names a different retained provider account than this Google Drive connection; it is refused without a provider read.");
        if (string.Equals(_rootFolderId, MyDriveRootAlias, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The My Drive root alias is not a stable provider folder identity; bind the opaque id returned by files/root before reading this root's ancestry.");
        await VerifyAccountAsync(cancellationToken);
        var first = await ReadCorpusChainAsync(request, cancellationToken);
        var second = await ReadCorpusChainAsync(request, cancellationToken);
        if (!SameChain(first, second))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Google Drive root hierarchy drifted between two fresh readbacks; the ancestry is refused rather than used.");
        var ordered = first.Reverse().ToArray();
        var ids = ordered.Select(item => item.FileId).ToArray();
        // Opaque Google ancestry needs provider-observed NAMES to derive a relative exclusion subtree. Names
        // are accepted only when both agreeing readback passes observed one safe single name per chain node;
        // otherwise the evidence stays identity-only and cannot prove an opaque nested exclusion.
        var names = ordered.Select(item => item.Name).ToArray();
        var secondNames = second.Reverse().Select(item => item.Name).ToArray();
        var observedNames = names.All(name => SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent(name)) &&
                            names.SequenceEqual(secondNames, StringComparer.Ordinal)
            ? names.Select(name => name!).ToArray()
            : null;
        return new SyncAuthorityCloudRootHierarchyEvidence(SyncProviders.GoogleDrive, _connection.ConnectionId,
            expectedProviderAccountId, _connection.PermissionId, request.FolderPath, ids[^1], ids[0], ids,
            DateTimeOffset.UtcNow, 2)
        {
            ChainFolderNames = observedNames
        };
    }

    // One pass: the selected root read from fresh metadata, then its actual parents upward to the positively
    // identified corpus root.
    private async Task<IReadOnlyList<GoogleDriveInventoryEntry>> ReadCorpusChainAsync(
        SyncAuthorityCloudRootHierarchyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = await ReadHierarchyFolderAsync(request.FolderId, cancellationToken, "selected root");
        var boundSharedDriveId = _sharedDriveId;
        string terminalId;
        if (!string.IsNullOrWhiteSpace(selected.DriveId))
        {
            var corpus = selected.DriveId!;
            if (!_allowSharedDrives || !_connection.SharedDrivesEnabled)
                throw new GoogleDriveBlockedException(
                    "The selected Google root lives in shared drive " + corpus + ", but shared drives are not enabled for this connection. Shared drives are an explicit capability; the hierarchy read is refused.");
            if (boundSharedDriveId is null || !string.Equals(boundSharedDriveId, corpus, StringComparison.Ordinal))
                throw new GoogleDriveBlockedException(
                    "The selected Google root does not belong to the shared drive this pair is bound to. Rebind explicitly before its ancestry is read.");
            // The shared-drive terminal is the exact drive id read from the selected root: the drive root
            // folder carries that id, and it is only accepted after it is read back as an accessible folder.
            terminalId = corpus;
        }
        else
        {
            if (boundSharedDriveId is not null)
                throw new GoogleDriveBlockedException(
                    "This pair is bound to shared drive " + boundSharedDriveId +
                    " but the selected root is in My Drive. The hierarchy read is refused.");
            terminalId = await ReadMyDriveRootIdAsync(cancellationToken);
        }
        var chain = new List<GoogleDriveInventoryEntry> { selected };
        var seen = new HashSet<string>(StringComparer.Ordinal) { selected.FileId };
        while (!string.Equals(chain[^1].FileId, terminalId, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = chain[^1];
            if (current.Parents.Count == 0)
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A Google Drive item on this chain has no parent and is not the positively identified corpus root; a parentless item is never assumed to be a root.");
            if (current.Parents.Count > 1)
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A Google Drive item on this chain has more than one parent; the ancestry is ambiguous and is refused.");
            if (chain.Count >= MaximumRootHierarchyDepth)
                throw new SyncAuthorityCloudRootHierarchyException(
                    "The Google Drive root hierarchy exceeds the bounded depth of " + MaximumRootHierarchyDepth + ".");
            var parentId = current.Parents[0];
            if (string.IsNullOrWhiteSpace(parentId))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A Google Drive parent identity on this chain is empty; the ancestry is refused.");
            if (!seen.Add(parentId))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "The Google Drive parent chain repeats an identity; a cyclic ancestry is refused.");
            var parent = await ReadHierarchyFolderAsync(parentId, cancellationToken, "ancestor");
            if (!string.Equals(parent.DriveId, current.DriveId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A Google Drive ancestor belongs to a different drive corpus than its child; the ancestry is refused.");
            chain.Add(parent);
        }
        return chain;
    }

    // The My Drive terminal is the actual id files/root returns. The alias is never accepted as the id.
    private async Task<string> ReadMyDriveRootIdAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("files/root?fields=id,mimeType", cancellationToken);
        var id = document.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
        var mimeType = document.RootElement.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, MyDriveRootAlias, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRootHierarchyException(
                "Google Drive did not return an opaque My Drive root identity from files/root; the alias is never used as a corpus root.");
        if (!string.Equals(mimeType, FolderMimeType, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Google Drive My Drive root returned by files/root is not a folder.");
        return id!;
    }

    // One node of the chain. Every node must be a folder, must not be trashed, must not be a shortcut and must
    // carry its provider version, so a partial or unverifiable node never becomes ancestry evidence.
    private async Task<GoogleDriveInventoryEntry> ReadHierarchyFolderAsync(string fileId,
        CancellationToken cancellationToken, string role)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(fileId))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Google Drive " + role + " has no stable folder identity; the ancestry is refused.");
        var entry = await GetFileAsync(fileId, cancellationToken);
        if (!string.Equals(entry.FileId, fileId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "Google Drive returned a different identity for the " + role + " than the one that was read; the ancestry is refused.");
        if (entry.Kind != GoogleDriveEntryKind.Folder)
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Google Drive " + role + " is not a folder, or is a shortcut; shortcuts are never followed and the ancestry is refused.");
        if (entry.Trashed)
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Google Drive " + role + " is in the trash; the ancestry is refused.");
        if (string.IsNullOrWhiteSpace(entry.Version))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Google Drive " + role + " has no verifiable provider version; the ancestry is refused.");
        return entry;
    }

    private static bool SameChain(IReadOnlyList<GoogleDriveInventoryEntry> left,
        IReadOnlyList<GoogleDriveInventoryEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (!string.Equals(first.FileId, second.FileId, StringComparison.Ordinal)) return false;
            if (!string.Equals(first.Version, second.Version, StringComparison.Ordinal)) return false;
            if (!string.Equals(first.DriveId, second.DriveId, StringComparison.Ordinal)) return false;
            if (first.Kind != second.Kind || first.Trashed != second.Trashed) return false;
            if (first.Parents.Count != second.Parents.Count) return false;
            if (!first.Parents.SequenceEqual(second.Parents, StringComparer.Ordinal)) return false;
        }
        return true;
    }
}

