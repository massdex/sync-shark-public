using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandNativePointerChecks
{
    internal static async Task VerifyRenameAsync(string root, string path, string pairId, ItemState pointer,
        Func<int> reads, WindowsCloudFilesProvider provider, PilotRemoteEntry remote,
        SqliteStateStore store, PairDef pair, string registrationId)
    {
        var before = reads();
        var renamed = Path.Combine(root, "renamed-pointer.bin");
        var (volume, id) = LocalItems.ParseObjectId(pointer.ObjectId);
        File.Move(path, renamed);
        var succeeded = false;
        try
        {
            using var lease = PilotDirectoryLease.Open(root);
            var scan = await new PilotLocalScanner
            {
                MetadataOnly = true, CloudFilesAware = true,
                ClassifyOwnedPointer = (_, native, size) => OwnedPointerProof.Classify(native,
                    registrationId, WindowsCloudFilesProvider.ReadRootRegistration(root), pairId,
                    SyncProviders.Dropbox, volume, store.ByObject(pair.A.RootId, pointer.ObjectId),
                    store.StoragePointerFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId)?.Remote,
                    size, allowDirtyRename: true)
            }.ScanAsync(lease, new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None,
                expectedPairId: pairId, contentProvider: SyncProviders.Dropbox);
            var entry = scan.Entries.Values.SingleOrDefault(item => item.RelativePath == "renamed-pointer.bin");
            if (entry is null)
                throw new IOException("Renamed pointer omitted: " + string.Join("; ", scan.Decisions));
            var observed = LocalItems.FromScan(entry);
            if (!observed.Flags.HasFlag(ItemFlags.OwnedPointer) || observed.ObjectId != pointer.ObjectId ||
                observed.Hash != pointer.Hash || reads() != before)
                throw new IOException("Renaming a pointer lost owned identity or fetched file contents.");
            var previous = remote with { RelativePath = "renamed-pointer.bin" };
            // The durable enqueue/replay is covered by the managed Move-commit checks; this verifies
            // the actual native acknowledgment boundary without a real remote account.
            provider.AcknowledgeDehydratedMove(previous, new(volume, id), () => { });
            provider.AcknowledgeDehydratedMove(previous, new(volume, id), () => { });
            var edited = previous with { Revision = "renamed-edit", Size = remote.Size + 17,
                ContentHash = new string('a', 64), ModifiedUtc = remote.ModifiedUtc!.Value.AddMinutes(1) };
            var updated = provider.UpdateDehydratedPlaceholder(previous, edited, new(volume, id));
            if (updated.FileId != id || updated.Identity.DropboxRevision != edited.Revision ||
                updated.Identity.LogicalSize != edited.Size || updated.Identity.ModifiedUtc != edited.ModifiedUtc ||
                updated.State.Availability != SyncCloudFileAvailability.OnlineOnly || reads() != before)
                throw new IOException("Remote edit after pointer rename did not converge in place without download.");
            provider.UpdateDehydratedPlaceholder(previous, edited, new(volume, id));
            provider.UpdateDehydratedPlaceholder(edited, previous, new(volume, id));
            if (reads() != before) throw new IOException("Pointer metadata updates fetched content.");
            succeeded = true;
        }
        finally
        {
            File.Move(renamed, path);
            if (succeeded)
            {
                provider.AcknowledgeDehydratedMove(remote, new(volume, id), () => { });
                provider.UpdateDehydratedPlaceholder(remote, remote, new(volume, id));
                if (reads() != before) throw new IOException("Renaming the pointer back fetched content.");
            }
            else
            {
                // Only these synthetic bytes are restored; failure cleanup must not leave a dirty stub.
                await File.ReadAllBytesAsync(path);
                await provider.ConvertVerifiedSyncedFileAsync(remote, dehydrateOverride: true);
            }
        }
    }
}
