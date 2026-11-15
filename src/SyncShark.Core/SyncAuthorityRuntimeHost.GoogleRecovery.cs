using System.Text.Json;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private async Task<SyncRecoveryCatalogPage> LoadGoogleTrashCatalogAsync(SyncPair pair, string account,
        string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var page = await CreateGoogleDriveClient(pair).ListTrashPageAsync(cursor,
            Math.Min(pageSize, GoogleDrivePilotClient.MaximumTrashPageSize), cancellationToken);
        var items = page.Entries.Select(entry => GoogleTrashCatalogItem(pair, account, entry)).ToArray();
        return new(items, 0, false, page.ContinueCursor, page.ContinueCursor is not null, page.Warnings);
    }

    internal static SyncRecoveryCatalogItem GoogleTrashCatalogItem(SyncPair pair, string account,
        GoogleDriveTrashEntry entry) => new("trash:" + entry.FileId, pair.Id, pair.Name,
        SyncRecoverySource.CloudTrash, entry.RelativePath, account, "Google Drive Trash", entry.Size,
        null, "Subject to Google Drive's Trash retention", entry.CanRestore
            ? "Available; restores the original cloud item only, without replacing an existing destination"
            : "Unavailable: Google permission, item type or a trashed parent prevents restoring this item",
        entry.ContentHash, entry.FileId, entry.Version, JsonSerializer.Serialize(entry), entry.CanRestore,
        entry.Kind == GoogleDriveEntryKind.Folder ? "folder" : "file");

    private static GoogleDriveTrashEntry RequireGoogleTrashEntry(SyncPair pair, SyncRecoveryCatalogItem item)
    {
        var entry = JsonSerializer.Deserialize<GoogleDriveTrashEntry>(item.IdentityJson)
                    ?? throw new InvalidDataException("The reviewed Google Trash identity is absent.");
        if (!IsGooglePair(pair) || item.Source != SyncRecoverySource.CloudTrash || !item.CanRestore ||
            pair.Id != item.PairId || pair.ConnectionId != entry.ConnectionId ||
            pair.DropboxFolderId != entry.RootFolderId || item.Key != "trash:" + entry.FileId ||
            item.Identity != entry.FileId || item.Revision != entry.Version ||
            item.RelativePath != entry.RelativePath || item.Size != entry.Size || item.ContentHash != entry.ContentHash)
            throw new InvalidDataException("The reviewed Trash item does not match this pair and retained identity. Refresh Recovery.");
        return entry;
    }

    private async Task<string> RestoreGoogleTrashAsync(SyncPair pair, SyncRecoveryCatalogItem item,
        bool restoreAsCopy, CancellationToken cancellationToken)
    {
        if (restoreAsCopy) throw new InvalidOperationException("Google Trash restores the original item only; it cannot restore a copy.");
        var entry = RequireGoogleTrashEntry(pair, item);
        await RequirePairAccountAsync(pair, pair.ConnectionId, cancellationToken);
        await CreateGoogleDriveClient(pair).RestoreTrashAsync(entry, cancellationToken);
        return "The exact item was restored from Google Drive Trash to its verified original cloud folder.";
    }

    private async Task<IReadOnlyList<SyncRecoveryCatalogItem>> LoadGoogleRevisionCatalogAsync(SyncPair pair,
        string relativePath, CancellationToken cancellationToken)
    {
        var account = await RecoveryAccountDisplayAsync(pair, cancellationToken);
        var revisions = await CreateGoogleDriveClient(pair).ListRevisionsAsync(relativePath, cancellationToken);
        return revisions.Select(revision => new SyncRecoveryCatalogItem("revision:" + revision.RevisionId,
            pair.Id, pair.Name, SyncRecoverySource.DropboxRevision, revision.RelativePath, account,
            "Google Drive revision " + revision.RevisionId, revision.Size, revision.ModifiedUtc,
            "Subject to Google Drive's retained revision history; its API may omit older revisions",
            revision.KeepForever ? "Available from Google Drive" : "Restore also keeps this revision in Google Drive storage",
            "md5:" + revision.Md5, revision.FileId, revision.RevisionId, JsonSerializer.Serialize(revision), true)).ToArray();
    }

    private async Task<string> RestoreGoogleRevisionAsync(SyncPair pair, SyncRecoveryCatalogItem requested,
        string targetRelativePath, bool restoreAsCopy, string commandId, CancellationToken cancellationToken)
    {
        await RequirePairAccountAsync(pair, pair.ConnectionId, cancellationToken);
        var remote = CreateGoogleDriveClient(pair);
        var exact = (await remote.ListRevisionsAsync(requested.RelativePath, cancellationToken)).SingleOrDefault(row =>
            row.FileId == requested.Identity && row.RevisionId == requested.Revision && row.Size == requested.Size &&
            "md5:" + row.Md5 == requested.ContentHash)
            ?? throw new InvalidOperationException("The selected Google revision no longer matches the reviewed file, size and checksum.");
        string? privateStage = null;
        return await RestoreJournalledRevisionAsync(pair, requested, targetRelativePath, restoreAsCopy,
            commandId, remote, (consume, retain, token) =>
                remote.WithRevisionBytesAsync(exact, consume, token, retain, path => privateStage = path),
            () => privateStage is null || !File.Exists(privateStage), cancellationToken);
    }
}
