namespace Massdex.SyncShark.V2;

/// <summary>Account-bound ranged reads; never falls back to a whole-file download.</summary>
public sealed class DropboxRangeSource : IWindowsCloudFilesRangeSource
{
    private readonly DropboxRootClients _clients;
    private readonly RootDef _root;

    public DropboxRangeSource(DropboxRootClients clients, RootDef root, WindowsCloudFilesBinding binding)
    {
        if (root.Provider != SyncProviders.Dropbox || binding.Provider != root.Provider ||
            binding.AccountId != root.AccountId || binding.RemoteRootId != root.RootObjectId ||
            binding.RemoteRoot != root.RootPath)
            throw new InvalidDataException("The range source does not match the provider/account/root binding.");
        _clients = clients;
        _root = root;
    }

    public async Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default)
    {
        var client = await _clients.OpenAsync(_root, cancellationToken).ConfigureAwait(false);
        await client.VerifyRootIdentityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncRangeDownloadResult> DownloadRangeAsync(PilotRemoteEntry expected, long offset,
        long length, Stream destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = await _clients.OpenAsync(_root, cancellationToken).ConfigureAwait(false);
        return await client.DownloadRangeAsync(expected, offset, length, destination, cancellationToken)
            .ConfigureAwait(false);
    }
}
