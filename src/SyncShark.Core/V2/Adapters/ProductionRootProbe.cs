using System.Net;

namespace Massdex.SyncShark.V2;

/// <summary>The production probe: local ids from the NTFS identity, Dropbox ids from the folder's metadata.</summary>
public sealed class ProductionRootProbe(DropboxRootClients dropbox) : IRootProbe
{
    /// <inheritdoc />
    public async Task<string> ObjectIdAsync(string provider, string? accountId, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            return LocalRootObjectId(path);
        if (!string.Equals(provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase))
            throw new SyncRunRefusedException("A " + provider + " root is not supported in core v2.");
        var client = await dropbox.OpenAsync(accountId, path, null, ct).ConfigureAwait(false);
        try
        {
            return await client.ReadRootFolderIdAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new SyncRunRefusedException("The Dropbox path '" + path + "' is not a folder at exactly that path.");
        }
        catch (DropboxApiException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            throw new SyncRunRefusedException("The Dropbox folder '" + path + "' does not exist.");
        }
        catch (Exception error) when (DropboxPilotClient.IsAccessRefusal(error))
        {
            // 401 or a revoked sign-in (invalid_grant): the account needs the user, the same outcome as a run's.
            throw DropboxItems.MapAccessError(error);
        }
    }

    /// <summary>The object id of a local root folder; a missing folder is refused.</summary>
    public static string LocalRootObjectId(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new SyncRunRefusedException("The local folder '" + path + "' does not exist.");
        PilotDirectoryLease lease;
        try
        {
            // The lease refuses a folder that equals, contains or sits inside the data folder.
            lease = PilotDirectoryLease.Open(Path.GetFullPath(path));
        }
        catch (InvalidDataException error)
        {
            throw new SyncRunRefusedException("The local folder '" + path + "' cannot be paired: " + error.Message);
        }
        using (lease)
            return SyncLocalEndpointStore.ObjectId(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
    }
}
