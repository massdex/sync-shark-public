using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandNativePointerChecks
{
    internal static async Task VerifyRenameAsync(string root, string path, string pairId, ItemState pointer,
        Func<int> reads)
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
        }
        finally { File.Move(renamed, path); }
    }
}
