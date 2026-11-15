namespace Massdex.SyncShark.V2;

public sealed partial class LocalWriter
{
    // A pointer is a reference, not an archive/upload source. Hold the reparse object
    // with DELETE + attribute access only, no recall and no write/delete sharing.
    private async Task<ItemState> MoveOwnedPointerAsync(SyncAction action, CancellationToken ct)
    {
        await EnsureParentsAsync(action.Path, ct).ConfigureAwait(false);
        MutateOwnedPointer(action.ExpectedTarget!, action.FromPath!, action.Path, ct);
        return action.ExpectedTarget! with { Path = action.Path };
    }

    private void DeleteOwnedPointer(SyncAction action, CancellationToken ct) =>
        MutateOwnedPointer(action.ExpectedTarget!, action.Path, null, ct);

    private void MutateOwnedPointer(ItemState expected, string path, string? destination, CancellationToken ct)
    {
        using var root = PilotDirectoryLease.Open(_rootPath, _rootIdentity);
        var parents = new List<PilotDirectoryLease>();
        try
        {
            PilotDirectoryLease Parent(string relative)
            {
                var segments = PilotPath.NormalizeRelative(relative).Split('/');
                var parent = root;
                foreach (var segment in segments.Take(segments.Length - 1))
                {
                    parent = PilotDirectoryLease.Open(Path.Combine(parent.Path, segment));
                    parents.Add(parent);
                }
                return parent;
            }
            var sourceParent = Parent(path);
            var targetParent = destination is null ? null : Parent(destination);
            var full = Path.Combine(sourceParent.Path, PilotPath.NormalizeRelative(path).Split('/')[^1]);
            using var held = SyncLocalFileAuthority.OpenOnlineOnlyStub(full);
            if (!PilotDirectoryLease.GetFileInformationByHandle(held.Handle, out var physical) ||
                !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(held.Handle, false, out var pointer) || pointer is null ||
                !pointer.Identity.IsBoundTo(_pairId, SyncProviders.Dropbox) || pointer.Identity.ObjectKind != "file" ||
                pointer.SyncRootFileId != root.Identity.FileId ||
                WindowsCloudFilesProvider.ReadStubState(held.Handle) is not { OwnedFullyDehydrated: true } ||
                !pointer.State.IsInSync || physical.NumberOfLinks != 1 ||
                SyncLocalEndpointStore.ObjectId(physical.VolumeSerialNumber,
                    ((ulong)physical.FileIndexHigh << 32) | physical.FileIndexLow) != expected.ObjectId ||
                pointer.FileId != (((ulong)physical.FileIndexHigh << 32) | physical.FileIndexLow) ||
                Content.SameHash(pointer.Identity.ContentHash, expected.Hash) != true ||
                pointer.Identity.LogicalSize != expected.Size ||
                (((long)physical.FileSizeHigh << 32) | physical.FileSizeLow) != expected.Size ||
                DateTime.FromFileTimeUtc(((long)physical.LastWriteTime.dwHighDateTime << 32) |
                    (uint)physical.LastWriteTime.dwLowDateTime) != expected.Mtime.UtcDateTime ||
                !string.Equals(held.FinalPath, full, StringComparison.OrdinalIgnoreCase))
                throw new ExpectedTargetMismatchException("The online-only item changed, was hydrated, or is not owned by this root; rescan and retry.");
            root.VerifyBinding();
            sourceParent.VerifyBinding();
            ct.ThrowIfCancellationRequested();
            if (destination is null) held.DeleteOnClose();
            else held.RenameTo(targetParent!, PilotPath.NormalizeRelative(destination).Split('/')[^1], replaceIfExists: false);
        }
        finally
        {
            for (var index = parents.Count - 1; index >= 0; index--) parents[index].Dispose();
        }
    }
}
