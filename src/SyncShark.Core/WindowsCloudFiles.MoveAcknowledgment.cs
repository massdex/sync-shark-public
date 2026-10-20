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
        if (!PilotDirectoryLease.GetFileInformationByHandle(held.Handle, out var objectInfo) ||
            new PilotDirectoryIdentity(objectInfo.VolumeSerialNumber, ((ulong)objectInfo.FileIndexHigh << 32) | objectInfo.FileIndexLow) != expectedLocalIdentity)
            throw new PointerAcknowledgmentChangedException();
        if (!TryReadOwnedPlaceholder(held.Handle, false, out var current) || current is null || current.IsDirectory ||
            current.SyncRootFileId != _pair.LocalRootFileId ||
            !SameIdentity(current.Identity with { RelativePath = expected.RelativePath }, expected) ||
            ReadStubState(held.Handle) is not { OnDiskBytes: 0, ModifiedBytes: 0 } stub ||
            stub.PlaceholderState == uint.MaxValue || (stub.PlaceholderState & 1) == 0 ||
            (stub.PlaceholderState & 0x10) == 0 || current.ModifiedDataSize != 0 ||
            !PilotDirectoryLease.GetFileInformationByHandle(held.Handle, out var physical) || physical.NumberOfLinks != 1 ||
            (((long)physical.FileSizeHigh << 32) | physical.FileSizeLow) != expected.LogicalSize ||
            expected.ModifiedUtc is not { } modified ||
            DateTime.FromFileTimeUtc(((long)physical.LastWriteTime.dwHighDateTime << 32) |
                (uint)physical.LastWriteTime.dwLowDateTime) != modified.UtcDateTime)
            throw new PointerAcknowledgmentChangedException();
        RequireCurrentNativeBinding();
        root.VerifyBinding();
        foreach (var heldParent in parents) heldParent.VerifyBinding();
        revalidate();
        // Same exclusive native handle for the proof and effect; replay is harmless when already in sync.
        Check(CfSetInSyncState(held.Handle.DangerousGetHandle(), 1, 0, IntPtr.Zero), "Acknowledging the committed pointer move");
        if (!TryReadOwnedPlaceholder(held.Handle, false, out var updated) || updated is null ||
            !SameIdentity(updated.Identity, current.Identity) || !updated.State.IsInSync ||
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

    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfSetInSyncState(IntPtr fileHandle, uint inSyncState, uint flags, IntPtr inSyncUsn);
}
