using System.Runtime.InteropServices;

namespace Massdex.SyncShark;

public sealed partial class WindowsCloudFilesProvider
{
    /// <summary>Create-only, metadata-only publication. Never converts, updates, hydrates or dehydrates a present file.</summary>
    internal WindowsCloudPlaceholderInfo CreateNewPlaceholder(PilotRemoteEntry remote,
        PilotDirectoryIdentity expectedRoot)
    {
        ThrowIfDisposed();
        if (!IsConnected) throw new IOException("The Cloud Files provider disconnected. Retry or choose available offline.");
        if (remote.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(remote.Id) ||
            string.IsNullOrWhiteSpace(remote.Revision) || remote.ContentHash is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit) || remote.Size < 0 || remote.ModifiedUtc is null)
            throw new IOException("Creating an online-only file requires exact confirmed remote metadata.");
        var identity = WindowsCloudFileIdentity.FromRemote(_pair.Id, remote, _pair.Provider);
        using var root = PilotDirectoryLease.Open(_pair.LocalRoot, expectedRoot);
        var parents = new List<PilotDirectoryLease>();
        try
        {
            var segments = identity.RelativePath.Split('/');
            var parent = root;
            foreach (var segment in segments.Take(segments.Length - 1))
            {
                parent = PilotDirectoryLease.Open(Path.Combine(parent.Path, segment));
                parents.Add(parent);
            }
            var bytes = identity.Serialize();
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var create = new Native.PlaceholderCreateInfo
                {
                    RelativeFileName = segments[^1], FsMetadata = Metadata(identity),
                    FileIdentity = pinned.AddrOfPinnedObject(), FileIdentityLength = checked((uint)bytes.Length),
                    Flags = 0x00000002u // MARK_IN_SYNC only. In particular, never SUPERSEDE (0x4).
                };
                root.VerifyBinding();
                parent.VerifyBinding();
                Check(Native.CfCreatePlaceholders(parent.Path, ref create, 1, 0x00000001u, out var processed),
                    "Creating a new online-only file without replacing an existing item");
                if (processed != 1 || create.Result < 0)
                    Check(create.Result < 0 ? create.Result : StatusUnsuccessful, "Verifying new pointer creation");
            }
            finally { pinned.Free(); }
            // Creation itself makes an empty-data pointer. Do NOT dehydrate here: a competing application
            // may already have hydrated/edited it. Observe under an exclusive no-recall metadata handle only.
            using var held = OpenAttributes(Path.Combine(parent.Path, segments[^1]), writeDac: true, exclusive: true);
            if (!TryReadOwnedPlaceholder(held.Handle, false, out var result) || result is null ||
                !SameIdentity(result.Identity, identity) || !result.State.IsInSync ||
                result.SyncRootFileId != expectedRoot.FileId ||
                !PilotDirectoryLease.GetFileInformationByHandle(held.Handle, out var physical) ||
                physical.VolumeSerialNumber != expectedRoot.VolumeSerialNumber || physical.NumberOfLinks != 1 ||
                result.FileId != (((ulong)physical.FileIndexHigh << 32) | physical.FileIndexLow) ||
                remote.Size != (((long)physical.FileSizeHigh << 32) | physical.FileSizeLow) ||
                remote.ModifiedUtc.Value.UtcDateTime != DateTime.FromFileTimeUtc(
                    ((long)physical.LastWriteTime.dwHighDateTime << 32) | (uint)physical.LastWriteTime.dwLowDateTime) ||
                ReadStubState(held.Handle) is not { OwnedFullyDehydrated: true })
                throw new IOException("The new pointer changed before verification. Leave it intact and rescan; no bytes were discarded.");
            Check(Native.CfSetPinState(held.Handle.DangerousGetHandle(), 2, 0, IntPtr.Zero),
                "Keeping the verified new pointer online-only");
            root.VerifyBinding();
            return result;
        }
        finally { for (var i = parents.Count - 1; i >= 0; i--) parents[i].Dispose(); }
    }
}
