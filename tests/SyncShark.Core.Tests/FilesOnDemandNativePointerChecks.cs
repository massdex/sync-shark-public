using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandNativePointerChecks
{
    internal static async Task VerifyRenameAsync(string root, string path, string pairId, ItemState pointer,
        Func<int> reads, WindowsCloudFilesProvider provider, PilotRemoteEntry remote)
    {
        var before = reads();
        var renamed = Path.Combine(root, "renamed-pointer.bin");
        File.Move(path, renamed);
        try
        {
            using var lease = PilotDirectoryLease.Open(root);
            var scan = await new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true }.ScanAsync(
                lease, new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None,
                expectedPairId: pairId, contentProvider: SyncProviders.Dropbox);
            var observed = LocalItems.FromScan(scan.Entries.Values.Single(item => item.RelativePath == "renamed-pointer.bin"));
            if (!observed.Flags.HasFlag(ItemFlags.OwnedPointer) || observed.ObjectId != pointer.ObjectId ||
                observed.Hash != pointer.Hash || observed.Path != "renamed-pointer.bin" || reads() != before)
                throw new IOException("Renaming a pointer lost owned identity or fetched file contents.");
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(renamed, out var cloud) || cloud is null ||
                !cloud.Identity.IsBoundTo(pairId, SyncProviders.Dropbox))
                throw new IOException("The renamed pointer lost its binding.");
            var previous = remote with { RelativePath = "renamed-pointer.bin" };
            var edited = previous with { Revision = "renamed-edit", Size = remote.Size + 17,
                ContentHash = new string('a', 64), ModifiedUtc = remote.ModifiedUtc!.Value.AddMinutes(1) };
            var (volume, id) = LocalItems.ParseObjectId(pointer.ObjectId);
            var updated = provider.UpdateDehydratedPlaceholder(previous, edited, new(volume, id));
            if (updated.FileId != id || updated.Identity.DropboxRevision != edited.Revision ||
                updated.Identity.LogicalSize != edited.Size || updated.Identity.ModifiedUtc != edited.ModifiedUtc ||
                updated.State.Availability != SyncCloudFileAvailability.OnlineOnly || reads() != before)
                throw new IOException("Remote edit after pointer rename did not converge in place without download.");
            provider.UpdateDehydratedPlaceholder(previous, edited, new(volume, id)); // lost response is idempotent
            provider.UpdateDehydratedPlaceholder(edited, previous, new(volume, id)); // restore fixture receipt
            if (reads() != before) throw new IOException("Pointer metadata updates fetched content.");
        }
        finally
        {
            File.Move(renamed, path);
            var (volume, id) = LocalItems.ParseObjectId(pointer.ObjectId);
            provider.UpdateDehydratedPlaceholder(remote, remote, new(volume, id));
        }
    }
}
