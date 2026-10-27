namespace Massdex.SyncShark;

// The one account-bound seam for the exact-file upload command. It reaches the existing Dropbox provider
// through the existing protected account selection and creates no pair, inventory, plan, settings, plan
// revision, token, scope or per-pair switch.
public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>
    /// Uploads exactly one file to an exact absent Dropbox destination under the retained namespace. The
    /// verified existing destination parent is bound as the provider root and one validated leaf is added
    /// below it, so no parent folder is ever created and no existing object is replaced.
    /// </summary>
    public async Task<SyncExactFileUploadOutcome> UploadExactFileAsync(string connectionId,
        string exactDestinationPath, Stream source, string expectedDropboxContentHash, Action effectDispatched,
        Action<string> progress, CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDropboxContentHash);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(effectDispatched);
        ArgumentNullException.ThrowIfNull(progress);
        if (!source.CanSeek) throw new IOException("The exact-file upload source must be seekable.");
        var account = await DropboxAccounts.OpenAsync(connectionId, null, cancellationToken)
                      ?? throw new InvalidOperationException("Connect Dropbox first.");
        var connection = account.Connection;
        var tokenSource = AccountTokenSource(connection);
        var namespaceRoot = PilotPath.DropboxNamespaceRootIdentity(connection.RootNamespaceId);
        SyncExactFileUploadRules.SplitExactDestination(exactDestinationPath, out var parentPath, out var leaf);

        // Pass 1: the retained account and namespace, then the exact existing parent folder identity.
        var resolver = account.Client;
        await resolver.VerifyRootIdentityAsync(cancellationToken);
        var parentId = string.Equals(parentPath, "/", StringComparison.Ordinal)
            ? namespaceRoot
            : await resolver.RequireExactExistingFolderIdAsync(parentPath, cancellationToken);

        // Pass 2: the verified existing parent becomes the provider root and the one validated leaf is
        // added below it. There is deliberately no parent-folder creation on this path.
        var client = new DropboxPilotClient(httpClient, tokenSource, parentPath, connection, parentId, _transferLimiter);
        await client.VerifyRootIdentityAsync(cancellationToken);
        progress("Checking whether the exact destination is already present");
        if (await client.FindEntryAtPathAsync(leaf, cancellationToken) is not null)
            return new SyncExactFileUploadOutcome(SyncExactFileUploadDisposition.KnownNoEffect, null,
                "The exact Dropbox destination already exists, so nothing was created, replaced, renamed or deleted.");
        progress("Uploading one file with a create-only conditional commit under the verified parent");
        return await client.CommitCreateOnlyExactFileAsync(leaf, source, expectedDropboxContentHash,
            effectDispatched, cancellationToken);
    }
}
