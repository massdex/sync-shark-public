using System.Runtime.InteropServices;

namespace Massdex.SyncShark;

public sealed partial class WindowsCloudFilesProvider
{
    /// <summary>Metadata-only acknowledgment of a committed remote move; never hydrates or discards data.</summary>
    internal WindowsCloudPlaceholderInfo AcknowledgeDehydratedMove(PilotRemoteEntry receipt,
        PilotDirectoryIdentity expectedLocalIdentity, Action revalidate)
    {
        ThrowIfDisposed();
        var expected = WindowsCloudFileIdentity.FromRemote(_pair.Id, receipt, _pair.Provider);
        var path = PilotPath.ResolveContainedPath(_pair.LocalRoot, receipt.RelativePath);
        using var root = PilotDirectoryLease.Open(_pair.LocalRoot, new(_pair.LocalVolumeSerialNumber, _pair.LocalRootFileId));
        var parents = new List<PilotDirectoryLease>();
        try
        {
        var parent = root;
        foreach (var segment in PilotPath.NormalizeRelative(receipt.RelativePath).Split('/').SkipLast(1))
        {
            parent = PilotDirectoryLease.Open(Path.Combine(parent.Path, segment));
            parents.Add(parent);
        }
        using var held = OpenAttributes(path, writeDac: true, exclusive: true);
        RequireCurrentNativeBinding();
        // Failure to observe metadata is not evidence of a byte edit. Keep the durable ack
        // retryable instead of converting a transient native read/binding failure into hydration debt.
        if (!PilotDirectoryLease.GetFileInformationByHandle(held.Handle, out var physical))
            throw new IOException("The pointer file identity is temporarily unreadable. Retry metadata acknowledgment.");
        if (new PilotDirectoryIdentity(physical.VolumeSerialNumber, ((ulong)physical.FileIndexHigh << 32) | physical.FileIndexLow) != expectedLocalIdentity)
            throw new PointerAcknowledgmentChangedException();
        if (!TryReadOwnedPlaceholder(held.Handle, false, out var current) || current is null ||
            current.RawPlaceholderState == uint.MaxValue || current.State.OnDiskBytes < 0 || current.ModifiedDataSize < 0)
            throw new IOException("The pointer metadata is temporarily unreadable. Retry metadata acknowledgment.");
        if (expected.ModifiedUtc is not { } modified)
            throw new IOException("The pointer receipt timestamp is unavailable. Rescan before retrying metadata acknowledgment.");
        if (current.IsDirectory || current.SyncRootFileId != _pair.LocalRootFileId ||
            !SameIdentity(current.Identity with { RelativePath = expected.RelativePath, DropboxRevision = expected.DropboxRevision }, expected) ||
            !V2.OwnedPointerProof.IsDehydrated(current) || physical.NumberOfLinks != 1 ||
            (((long)physical.FileSizeHigh << 32) | physical.FileSizeLow) != expected.LogicalSize ||
            DateTime.FromFileTimeUtc(((long)physical.LastWriteTime.dwHighDateTime << 32) |
                (uint)physical.LastWriteTime.dwLowDateTime) != modified.UtcDateTime)
            throw new PointerAcknowledgmentChangedException();
        RequireCurrentNativeBinding();
        root.VerifyBinding();
        foreach (var heldParent in parents) heldParent.VerifyBinding();
        revalidate();
        // One metadata-only effect on the proven exclusive handle. No invalidation ranges or
        // DEHYDRATE flag: update the fetch identity and in-sync bit without touching file data.
        // Repeating the same identity after a crash is harmless.
        var bytes = expected.Serialize();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var metadata = Metadata(expected);
            Check(Native.CfUpdatePlaceholder(held.Handle.DangerousGetHandle(), ref metadata,
                pinned.AddrOfPinnedObject(), checked((uint)bytes.Length), IntPtr.Zero, 0,
                0x00000002u, IntPtr.Zero, IntPtr.Zero), "Acknowledging the committed pointer move");
        }
        finally { pinned.Free(); }
        if (!TryReadOwnedPlaceholder(held.Handle, false, out var updated) || updated is null ||
            !SameIdentity(updated.Identity, expected) || !updated.State.IsInSync ||
            ReadStubState(held.Handle) is not { OwnedFullyDehydrated: true })
            throw new IOException("The move acknowledgment could not be confirmed. Retry metadata verification.");
        return updated;
        }
        finally
        {
            for (var index = parents.Count - 1; index >= 0; index--) parents[index].Dispose();
        }
    }

    internal sealed class PointerAcknowledgmentChangedException()
        : IOException("The pointer changed after the remote move. Normal content verification is required; no in-sync mark was applied.");
}
