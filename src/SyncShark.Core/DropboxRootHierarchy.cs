namespace Massdex.SyncShark;

// CC2D: the Dropbox implementation of the one provider-neutral cloud root hierarchy capability.
//
// The retained account and root namespace are verified through the existing VerifyAccountAsync, the exact
// bound folder is resolved through the existing GetFolderIdentityAsync, and every ancestor is resolved by
// its actual provider path inside that verified namespace until the retained namespace root, which is the
// existing synthetic namespace identity with the provider root path "/" only. No display-path cache, no
// account label and no e-mail participates in any identity decision.
public sealed partial class DropboxPilotClient : ISyncAuthorityCloudRootHierarchySource
{
    /// <summary>The bounded depth of one provider root hierarchy walk.</summary>
    public const int MaximumRootHierarchyDepth = 128;

    public async Task<SyncAuthorityCloudRootHierarchyEvidence> ReadRootHierarchyAsync(
        SyncAuthorityCloudRootHierarchyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DropboxProviderConfiguration.ValidateConnection(expectedConnection);
        if (string.IsNullOrWhiteSpace(_rootId))
            throw new SyncAuthorityCloudRootHierarchyException(
                "This Dropbox adapter has no bound stable folder identity, so no root hierarchy is read from a display path alone.");
        DropboxConnectionBinding retained;
        try
        {
            retained = expectedConnection.Normalize();
        }
        catch (ArgumentException error)
        {
            throw new SyncAuthorityCloudRootHierarchyException(
                "This Dropbox connection has no complete retained account identity, so no root hierarchy is read: " +
                error.Message);
        }
        request.RequireBoundTo(SyncProviders.Dropbox, retained.ConnectionId,
            retained.RootNamespaceId, _rootId!);
        string expectedProviderAccountId;
        try
        {
            expectedProviderAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox,
                expectedConnection.AccountId, expectedConnection.RootNamespaceId,
                expectedConnection.HomeNamespaceId);
        }
        catch (ArgumentException error)
        {
            throw new SyncAuthorityCloudRootHierarchyException(
                "This Dropbox connection has no complete retained account identity, so no root hierarchy is read: " +
                error.Message);
        }
        if (!string.Equals(request.ProviderAccountId, expectedProviderAccountId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The requested cloud root hierarchy read names a different retained provider account than this Dropbox connection; it is refused without a provider read.");
        await VerifyAccountAsync(cancellationToken);
        var first = await ReadNamespaceChainAsync(request, cancellationToken);
        var second = await ReadNamespaceChainAsync(request, cancellationToken);
        if (!SameChain(first, second))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The Dropbox root hierarchy drifted between two fresh readbacks; the ancestry is refused rather than used.");
        var ordered = first.Reverse().ToArray();
        var ids = ordered.Select(item => item.FolderId).ToArray();
        // Every actual chain node carries the provider-issued keys that node was read with, including the
        // shared-folder mount identity at the location it is mounted at. The coverage declaration is what lets
        // a comparison distinguish "this provider issues no further key here" from "it was never read".
        var nodeKeys = ordered.Select(item => new SyncAuthorityCloudHierarchyNodeKeys(item.FolderId,
            SyncCloudObjectKeys.Combine(SyncProviders.Dropbox, item.FolderId, item.ObjectAliases))).ToArray();
        return new SyncAuthorityCloudRootHierarchyEvidence(SyncProviders.Dropbox, retained.ConnectionId,
            expectedProviderAccountId, retained.RootNamespaceId, request.FolderPath, ids[^1], ids[0], ids,
            DateTimeOffset.UtcNow, 2)
        {
            ChainNodeKeys = nodeKeys,
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.MountAliasesComplete
        };
    }

    // One pass: the retained root read back from the provider, then each actual ancestor folder identity in
    // this verified namespace until the retained namespace root terminal.
    private async Task<IReadOnlyList<PilotRemoteRootBinding>> ReadNamespaceChainAsync(
        SyncAuthorityCloudRootHierarchyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var namespaceIdentity = PilotPath.DropboxNamespaceRootIdentity(expectedConnection.RootNamespaceId);
        if (PilotPath.IsDropboxNamespaceRootIdentity(request.FolderId))
        {
            // The namespace root is the existing synthetic identity and the provider root path only.
            if (!string.Equals(request.FolderId, namespaceIdentity, StringComparison.Ordinal) ||
                !string.Equals(_root, "/", StringComparison.Ordinal))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "The requested namespace-root identity does not match the retained Dropbox namespace root; the ancestry is refused.");
            return new[] { new PilotRemoteRootBinding("/", namespaceIdentity) };
        }
        var current = await GetFolderIdentityAsync(request.FolderId, cancellationToken);
        if (!string.Equals(current.FolderId, request.FolderId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "Dropbox returned a different folder identity than the one that was read; the ancestry is refused.");
        var currentPath = PilotPath.NormalizeDropboxRoot(current.Path);
        if (!string.Equals(currentPath, _root, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The retained Dropbox root no longer resolves to its bound path; the root moved, so its ancestry is refused.");
        if (!string.IsNullOrWhiteSpace(request.FolderPath) &&
            !string.Equals(request.FolderPath, "/", StringComparison.Ordinal) &&
            !string.Equals(PilotPath.NormalizeDropboxRoot(request.FolderPath), currentPath,
                StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The supplied display path is stale against the current provider readback of this root; the ancestry is refused rather than taken from the stale path.");
        var chain = new List<PilotRemoteRootBinding> { new(currentPath, current.FolderId, current.ObjectAliases) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { current.FolderId };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chain.Count >= MaximumRootHierarchyDepth)
                throw new SyncAuthorityCloudRootHierarchyException(
                    "The Dropbox root hierarchy exceeds the bounded depth of " + MaximumRootHierarchyDepth + ".");
            var parentPath = ParentDropboxPath(chain[^1].Path);
            if (string.Equals(parentPath, "/", StringComparison.Ordinal))
            {
                chain.Add(new PilotRemoteRootBinding("/", namespaceIdentity));
                break;
            }
            var parent = await GetFolderIdentityAsync(parentPath, cancellationToken);
            var readbackPath = PilotPath.NormalizeDropboxRoot(parent.Path);
            if (!string.Equals(readbackPath, parentPath, StringComparison.OrdinalIgnoreCase))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A Dropbox ancestor folder readback does not match the path it was read from; the ancestry is refused.");
            if (string.IsNullOrWhiteSpace(parent.FolderId))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A Dropbox ancestor folder has no stable identity; the ancestry is refused.");
            if (!seen.Add(parent.FolderId))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "The Dropbox parent chain repeats a folder identity; a cyclic ancestry is refused.");
            chain.Add(new PilotRemoteRootBinding(readbackPath, parent.FolderId, parent.ObjectAliases));
        }
        return chain;
    }

    private static string ParentDropboxPath(string path)
    {
        var trimmed = PilotPath.NormalizeDropboxRoot(path).TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static bool SameChain(IReadOnlyList<PilotRemoteRootBinding> left,
        IReadOnlyList<PilotRemoteRootBinding> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].FolderId, right[index].FolderId, StringComparison.Ordinal)) return false;
            if (!string.Equals(left[index].Path, right[index].Path, StringComparison.OrdinalIgnoreCase)) return false;
            // A changed shared-folder mount identity between the two fresh passes is drift: the ancestry is
            // refused rather than used with one pass's mount alias applied to the other pass's native ids.
            if (!SameAliases(left[index].ObjectAliases, right[index].ObjectAliases)) return false;
        }
        return true;
    }

    private static bool SameAliases(IReadOnlyList<SyncCloudObjectKey>? left,
        IReadOnlyList<SyncCloudObjectKey>? right)
    {
        var first = left ?? Array.Empty<SyncCloudObjectKey>();
        var second = right ?? Array.Empty<SyncCloudObjectKey>();
        if (first.Count != second.Count) return false;
        foreach (var key in first)
            if (!second.Any(candidate => SyncCloudObjectKeys.SameObject(key, candidate))) return false;
        return true;
    }
}
