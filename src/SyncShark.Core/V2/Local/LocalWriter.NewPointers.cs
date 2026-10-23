namespace Massdex.SyncShark.V2;

public sealed partial class LocalWriter
{
    internal async Task<ItemState> CreateNewPointerAsync(ItemState remote, string path,
        WindowsCloudFilesProvider provider, CancellationToken ct)
    {
        await EnsureParentsAsync(path, ct).ConfigureAwait(false);
        using var root = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
        ct.ThrowIfCancellationRequested();
        var result = provider.CreateNewPlaceholder(DropboxItems.ToEntry(remote with { Path = path }), _rootIdentity);
        return PointerState(path, result, _rootIdentity.VolumeSerialNumber);
    }

    private static ItemState PointerState(string path, WindowsCloudPlaceholderInfo pointer, uint volume) =>
        new(path, ItemKind.File, SyncLocalEndpointStore.ObjectId(volume, pointer.FileId), null,
            pointer.Identity.ContentHash, pointer.Identity.LogicalSize,
            pointer.Identity.ModifiedUtc ?? throw new IOException("The pointer has no confirmed modification time."),
            ItemFlags.OwnedPointer);

    private ItemState? ObserveOwnedPointer(string path)
    {
        using var root = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
        var parents = new List<PilotDirectoryLease>();
        try
        {
            var segments = PilotPath.NormalizeRelative(path).Split('/');
            var parent = root;
            foreach (var segment in segments.Take(segments.Length - 1))
            {
                parent = PilotDirectoryLease.Open(Path.Combine(parent.Path, segment));
                parents.Add(parent);
            }
            var full = Path.Combine(parent.Path, segments[^1]);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var info) || info is null ||
                info.IsDirectory || info.State.Availability == SyncCloudFileAvailability.AvailableOffline) return null;
            using var held = SyncLocalFileAuthority.OpenOnlineOnlyStub(full);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(held.Handle, false, out info) || info is null ||
                !info.Identity.IsBoundTo(_pairId, SyncProviders.Dropbox) || info.SyncRootFileId != root.Identity.FileId ||
                !info.State.IsInSync || WindowsCloudFilesProvider.ReadStubState(held.Handle) is not { OwnedFullyDehydrated: true } ||
                !PilotDirectoryLease.GetFileInformationByHandle(held.Handle, out var physical) ||
                physical.VolumeSerialNumber != _rootIdentity.VolumeSerialNumber ||
                info.FileId != (((ulong)physical.FileIndexHigh << 32) | physical.FileIndexLow) ||
                info.Identity.LogicalSize != (((long)physical.FileSizeHigh << 32) | physical.FileSizeLow) ||
                info.Identity.ModifiedUtc?.UtcDateTime != DateTime.FromFileTimeUtc(
                    ((long)physical.LastWriteTime.dwHighDateTime << 32) | (uint)physical.LastWriteTime.dwLowDateTime))
                throw new IOException("The online-only item cannot be verified without recalling bytes. Rescan or retry storage settlement.");
            root.VerifyBinding();
            return PointerState(path, info, physical.VolumeSerialNumber);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        finally { for (var i = parents.Count - 1; i >= 0; i--) parents[i].Dispose(); }
    }
}
