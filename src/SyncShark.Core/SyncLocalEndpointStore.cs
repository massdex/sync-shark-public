using System.Globalization;
using System.ComponentModel;
using System.Net;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

public sealed record SyncLocalEndpointBinding(string RootPath, uint VolumeSerialNumber, ulong RootFileId)
{
    public PilotDirectoryIdentity Identity => new(VolumeSerialNumber, RootFileId);
    public string IdentityToken => VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture) + ":" +
        RootFileId.ToString("x16", CultureInfo.InvariantCulture);
}

/// <summary>
/// The existing endpoint transport contract over a real second NTFS folder. This adapter does not
/// plan, decide conflicts, allocate recovery authority or retry effects. Those remain in the shared
/// authority pipeline. Every effect holds the exact object and its ancestors through verification and
/// commit. Staging and retained predecessors live only in the caller's bound private namespace.
/// </summary>
public sealed partial class SyncLocalEndpointStore : ISyncRemoteStore, ISyncUploadSessionPolicySource
{
    internal static Action? PublishedBeforeCloseForTest { get; set; }

    public SyncUploadSessionPolicy UploadSessionPolicy => new(long.MaxValue, long.MaxValue);

    // DeleteAsync holds the directory identity; Windows refuses FileDispositionInfo
    // when a child exists, including a child created after the inventory.
    public bool SupportsConditionalDirectoryDelete => true;

    private readonly SyncLocalEndpointBinding _binding;
    private readonly SyncControlNamespaceBinding _control;
    private readonly string? _contentProvider;

    public SyncLocalEndpointStore(SyncLocalEndpointBinding binding, SyncControlNamespaceBinding control,
        string? contentProvider = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(control);
        _binding = binding with { RootPath = PilotPath.NormalizeLocalRoot(binding.RootPath) };
        _control = control;
        _contentProvider = contentProvider;
        SyncControlNamespace.Validate(control);
        using var root = OpenRoot();
        using var owned = SyncControlNamespace.OpenLocal(_control, root);
    }

    public SyncRemoteRecoveryRootBinding RecoveryBinding => new(_control.Local.RootPath,
        ObjectId(_control.Local.RootVolumeSerialNumber, _control.Local.RootFileId), _control.NamespaceId);

    private PilotDirectoryLease OpenRoot() => PilotDirectoryLease.Open(_binding.RootPath, _binding.Identity);
    internal static string ObjectId(uint volume, ulong file) => "id:local:" +
        volume.ToString("x8", CultureInfo.InvariantCulture) + ":" + file.ToString("x16", CultureInfo.InvariantCulture);
    private static string Revision(string id, long size, string hash) => id + ":" +
        size.ToString(CultureInfo.InvariantCulture) + ":" + hash;
    private static DropboxApiException Stale(string role) => new(HttpStatusCode.Conflict,
        "The exact local endpoint " + role + " changed; the effect was refused.");

    private async Task<PilotRemoteEntry> DescribeFileAsync(SyncLocalFileAuthority file, string relative,
        CancellationToken token)
    {
        var identity = file.ReadIdentity();
        var id = ObjectId(identity.VolumeSerialNumber, identity.FileId);
        var size = file.Stream.Length;
        file.Stream.Position = 0;
        var hash = await SyncContentIdentity.ComputeAsync(_contentProvider, file.Stream, token);
        file.Stream.Position = 0;
        return new(relative, PilotEntryKind.File, id, Revision(id, size, hash), size, hash);
    }

    private static PilotRemoteEntry DescribeDirectory(PilotDirectoryLease directory, string relative) =>
        new(relative, PilotEntryKind.Directory, ObjectId(directory.Identity.VolumeSerialNumber,
            directory.Identity.FileId), "directory", 0, null);

    private static void RequireExact(PilotRemoteEntry expected, PilotRemoteEntry current)
    {
        if (expected.Kind != current.Kind || expected.Id != current.Id ||
            expected.Revision != current.Revision || expected.Size != current.Size ||
            !string.Equals(expected.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw Stale("object or content");
    }

    // Every ancestor is retained, not merely checked and released. A junction replacement or an
    // ancestor rename cannot redirect an otherwise valid leaf path after its root was verified.
    private sealed class HeldPath : IDisposable
    {
        private readonly List<PilotDirectoryLease> _parents = [];
        public string Relative { get; }
        public string Name { get; }
        public string FullPath { get; }
        public PilotDirectoryLease Parent { get; }

        public HeldPath(PilotDirectoryLease root, string relative)
        {
            Relative = PilotPath.NormalizeRelative(relative);
            var segments = Relative.Split('/');
            Name = PilotPath.NormalizeTopLevelName(segments[^1]);
            Parent = root;
            try
            {
                foreach (var segment in segments.Take(segments.Length - 1))
                {
                    var next = PilotDirectoryLease.Open(Path.Combine(Parent.Path, segment));
                    _parents.Add(next);
                    Parent = next;
                }
                FullPath = Path.Combine(Parent.Path, Name);
                root.VerifyBinding();
            }
            catch { Dispose(); throw; }
        }

        public SyncLocalFileAuthority OpenFile(bool exclusive = false)
        {
            Parent.VerifyBinding();
            var file = exclusive ? SyncLocalFileAuthority.OpenExistingExclusive(FullPath) :
                SyncLocalFileAuthority.OpenExistingReadGuard(FullPath);
            try
            {
                file.ReadIdentity();
                if (!string.Equals(file.FinalPath, FullPath, StringComparison.OrdinalIgnoreCase))
                    throw Stale("file path");
                if (exclusive) file.VerifySingleLink();
                return file;
            }
            catch { file.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var i = _parents.Count - 1; i >= 0; i--) _parents[i].Dispose();
        }
    }

    public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var root = OpenRoot();
        root.VerifyBinding();
        return Task.CompletedTask;
    }

    public Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installationId,
        string namespaceId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A local endpoint uses its bound local control namespace.");

    public Task VerifyRecoveryRootAsync(SyncRemoteRecoveryRootBinding binding,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireRecoveryBinding(binding);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        return Task.CompletedTask;
    }

    private void RequireRecoveryBinding(SyncRemoteRecoveryRootBinding binding)
    {
        if (binding != RecoveryBinding) throw Stale("recovery ownership");
    }

    public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
        ListEntriesCoreAsync(null, cancellationToken);

    public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
        Func<SyncInventoryProgress, Task> onProgress, CancellationToken cancellationToken = default) =>
        ListEntriesCoreAsync(onProgress, cancellationToken);

    private async Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesCoreAsync(
        Func<SyncInventoryProgress, Task>? onProgress, CancellationToken cancellationToken)
    {
        using var root = OpenRoot();
        var scan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
            contentProvider: _contentProvider, onInventory: onProgress is null ? null :
                (path, count) => onProgress(new("Reading Local Inventory", path, count,
                    "Inspecting local file metadata and its content identity.")));
        // An unreadable/foreign placeholder is not absence. Do not publish a misleading complete
        // inventory that could authorize deletion on the other endpoint.
        if (scan.Decisions.Count != 0) throw new IOException("This endpoint inventory is incomplete; no complete scan was published.");
        return scan.Entries.Values.Select(entry =>
        {
            if (entry.VolumeSerialNumber is not { } volume || entry.FileId is not { } file)
                throw new IOException("The endpoint inventory lacks a stable object identity.");
            var id = ObjectId(volume, file);
            if (entry.Kind == PilotEntryKind.File && string.IsNullOrEmpty(entry.ContentHash))
                throw new IOException("The endpoint inventory lacks a verified file content identity.");
            return new PilotRemoteEntry(entry.RelativePath, entry.Kind, id,
                entry.Kind == PilotEntryKind.Directory ? "directory" : Revision(id, entry.Size, entry.ContentHash!),
                entry.Size, entry.ContentHash, entry.LastWriteUtc);
        }).OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// One exact target lookup without scanning the endpoint: only the requested leaf and its retained
    /// ancestors are inspected, so an unrelated unreadable file elsewhere can never block a healthy transfer.
    /// </summary>
    public Task<PilotRemoteEntry> ReadEntryAsync(string relativePath, string providerObjectId,
        CancellationToken cancellationToken = default) =>
        ReadEntryHeldAsync(relativePath, providerObjectId, cancellationToken);

    private async Task<PilotRemoteEntry> ReadEntryHeldAsync(string relativePath, string providerObjectId,
        CancellationToken cancellationToken)
    {
        var found = await FindEntryAtPathAsync(relativePath, cancellationToken)
            ?? throw new IOException("The selected local endpoint did not return one exact item at " +
                PilotPath.NormalizeRelative(relativePath) + ".");
        if (!string.Equals(found.Id, providerObjectId, StringComparison.Ordinal)) throw Stale("object");
        return found;
    }

    /// <summary>
    /// One exact-path observation of this endpoint. Null means ONLY an explicit native file/path-not-found on
    /// the requested leaf or an ancestor under the unchanged verified root; a root failure, denied access, a
    /// sharing violation, cancellation, a malformed path, or a foreign/reparse target is always an error.
    /// </summary>
    public Task<PilotRemoteEntry?> FindEntryAtPathAsync(string relativePath,
        CancellationToken cancellationToken = default) => FindEntryAtPathCoreAsync(relativePath, true, cancellationToken);

    // Same held-root/ancestor and native absence contract, without reading file content.
    internal Task<PilotRemoteEntry?> FindMetadataAtPathAsync(string relativePath, CancellationToken cancellationToken) =>
        FindEntryAtPathCoreAsync(relativePath, false, cancellationToken);

    private async Task<PilotRemoteEntry?> FindEntryAtPathCoreAsync(string relativePath, bool readContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = PilotPath.NormalizeRelative(relativePath);
        using var root = OpenRoot();
        root.VerifyBinding();
        HeldPath? held = null;
        try
        {
            try
            {
                held = new HeldPath(root, relative);
            }
            catch (IOException error) when (IsMissingTarget(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.VerifyBinding();
                return null;
            }
            root.VerifyBinding();
            held.Parent.VerifyBinding();
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(held.FullPath);
            }
            catch (IOException error) when (IsMissingTarget(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                root.VerifyBinding();
                held.Parent.VerifyBinding();
                return null;
            }
            if (PilotPath.IsUnsafeAttributes(attributes) && PilotPath.IsUnsafePath(held.FullPath))
                throw new IOException(
                    "The exact local endpoint target is a reparse point, offline or otherwise unsafe placeholder; it is refused rather than reported present or absent.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var directory = PilotDirectoryLease.Open(held.FullPath);
                root.VerifyBinding();
                held.Parent.VerifyBinding();
                directory.VerifyBinding();
                return DescribeDirectory(directory, relative);
            }
            using var file = held.OpenFile();
            PilotRemoteEntry described;
            if (readContent) described = await DescribeFileAsync(file, relative, cancellationToken);
            else
            {
                var identity = file.ReadIdentity();
                described = new PilotRemoteEntry(relative, PilotEntryKind.File,
                    ObjectId(identity.VolumeSerialNumber, identity.FileId), null, file.Stream.Length, null);
            }
            root.VerifyBinding();
            held.Parent.VerifyBinding();
            return described;
        }
        finally
        {
            held?.Dispose();
        }
    }

    // Directory leases wrap native not-found exceptions. Only those exact native classifications
    // denote target absence; a generic IO failure, sharing error or failed bound-root open does not.
    private static bool IsMissingTarget(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException ||
        error is Win32Exception { NativeErrorCode: 2 or 3 } ||
        error is IOException && (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003) ||
            error.InnerException is { } inner && IsMissingTarget(inner));

    public async Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
        CancellationToken cancellationToken = default)
    {
        using var root = OpenRoot();
        using var path = new HeldPath(root, expected.RelativePath);
        using var source = path.OpenFile();
        var current = await DescribeFileAsync(source, path.Relative, cancellationToken);
        RequireExact(expected, current);
        await source.Stream.CopyToAsync(destination, cancellationToken);
        return new(current.Id!, current.Revision!, current.Size, current.ContentHash!);
    }

    public Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source,
        string expectedContentHash, CancellationToken cancellationToken = default) =>
        UploadAsync(relativePath, source, expectedContentHash, null, cancellationToken);

    public Task<PilotUploadResult> UploadAsync(string relativePath, Stream source, string expectedContentHash,
        string? expectedRemoteRevision, CancellationToken cancellationToken = default) =>
        UploadAsync(relativePath, source, expectedContentHash, expectedRemoteRevision, cancellationToken, null);

    internal async Task<PilotUploadResult> UploadAsync(string relativePath, Stream source, string expectedContentHash,
        string? expectedRemoteRevision, CancellationToken cancellationToken, DateTimeOffset? lastWriteUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        return await UploadHeldAsync(root, control, relativePath, source, expectedContentHash,
            expectedRemoteRevision, cancellationToken, lastWriteUtc);
    }

    private async Task<PilotUploadResult> UploadHeldAsync(PilotDirectoryLease root, PilotDirectoryLease control,
        string relativePath, Stream source, string expectedContentHash, string? expectedRemoteRevision,
        CancellationToken cancellationToken, DateTimeOffset? lastWriteUtc)
    {
        using var path = new HeldPath(root, relativePath);
        var stageName = SyncCreateNewLocalTransfer.StageName(Guid.NewGuid().ToString("N"));
        return await FillAndPublishAsync(control, path, Path.Combine(control.Path, stageName), expectedContentHash,
            expectedRemoteRevision, null, string.Empty, null, async (destination, token) =>
            {
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
                return null;
            }, cancellationToken, lastWriteUtc).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one exact remote revision straight into a temporary file in the destination's own directory on
    /// the target volume, verifies its length and content hash, and publishes it with the same conditional
    /// create-only rename as an upload. There is no product spool copy, so the download needs one target-volume
    /// copy plus the reserve rather than two. A failure or cancellation deletes only this call's own partial.
    /// </summary>
    internal async Task<PilotUploadResult> DownloadToTargetAsync(string relativePath, long expectedSize,
        Func<Stream, CancellationToken, Task<SyncDownloadResult>> download, string expectedContentHash,
        string? expectedRemoteRevision, string? expectedStubId, string recoveryPathForCheckpoint,
        Func<SyncDownloadStage, CancellationToken, Task>? preservedAfterVerify,
        CancellationToken cancellationToken = default, DateTimeOffset? lastWriteUtc = null)
    {
        ArgumentNullException.ThrowIfNull(download);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        using var path = new HeldPath(root, relativePath);
        var stagePath = V2.ProviderEffectContext.Current.Value?.Action?.QuarantineStage == true
            ? AttendedElevatedAction.DownloadStagePath(path.FullPath)
            : Path.Combine(path.Parent.Path, SyncLocalTransfer.ArtifactPrefix + Guid.NewGuid().ToString("N") + ".partial");
        return await FillAndPublishAsync(control, path, stagePath,
            expectedContentHash, expectedRemoteRevision, expectedStubId, recoveryPathForCheckpoint,
            preservedAfterVerify, async (destination, token) =>
            {
                var result = await download(destination, token).ConfigureAwait(false);
                if (result.Size != expectedSize)
                    throw new IOException("The downloaded identity differs from the planned source content.");
                return result;
            }, cancellationToken, lastWriteUtc).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes one already-verified download stage (an owned transfer artifact in the destination's own
    /// directory) after a crash between verification and the swap. The stage must still be the checkpoint's exact
    /// size and content hash, and the destination must still be absent; otherwise it is retained and nothing moves.
    /// </summary>
    internal async Task<PilotUploadResult> PublishVerifiedPartialAsync(string relativePath, string stagePath,
        string expectedStageObjectId, long expectedSize, string expectedContentHash,
        CancellationToken cancellationToken = default, DateTimeOffset? lastWriteUtc = null)
    {
        using var root = OpenRoot();
        using var path = new HeldPath(root, relativePath);
        var stageFull = Path.GetFullPath(stagePath);
        var targetFull = Path.GetFullPath(path.FullPath);
        RequireDownloadStagePath(targetFull, stageFull);
        if (File.Exists(targetFull) || Directory.Exists(targetFull))
            throw new DownloadStageMismatchException("The destination appeared; the verified partial is retained.");
        var (stageVolume, stageFileId) = LocalItems.ParseObjectId(expectedStageObjectId);
        SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stageFull,
            new PilotDirectoryIdentity(stageVolume, stageFileId), stageFull);
        using var stage = SyncLocalFileAuthority.OpenExistingExclusive(stageFull, FileAccess.ReadWrite);
        var written = await DescribeFileAsync(stage, path.Relative, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(written.Id, expectedStageObjectId, StringComparison.Ordinal) ||
            written.Size != expectedSize ||
            !string.Equals(written.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            throw new DownloadStageMismatchException("The verified download stage no longer matches its checkpoint.");
        if (lastWriteUtc is { } modified) File.SetLastWriteTimeUtc(stage.Handle, modified.UtcDateTime);
        stage.RenameTo(path.Parent, path.Name, replaceIfExists: false);
        return new(written.Id!, written.Revision!, written.Size, written.ContentHash!);
    }

    internal sealed class DownloadStageMismatchException(string message) : IOException(message);

    internal static void RequireDownloadStagePath(string targetPath, string stagePath)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(stagePath)),
                Path.GetDirectoryName(Path.GetFullPath(targetPath)), StringComparison.OrdinalIgnoreCase) ||
            !SyncLocalTransfer.IsTransferArtifactName(Path.GetFileName(stagePath)))
            throw new DownloadStageMismatchException("The download stage is not this destination's owned partial.");
    }

    private async Task<PilotUploadResult> FillAndPublishAsync(PilotDirectoryLease control, HeldPath path,
        string stagePath, string expectedContentHash, string? expectedRemoteRevision, string? expectedStubId,
        string recoveryPathForCheckpoint,
        Func<SyncDownloadStage, CancellationToken, Task>? preservedAfterVerify,
        Func<Stream, CancellationToken, Task<SyncDownloadResult?>> fill, CancellationToken cancellationToken,
        DateTimeOffset? lastWriteUtc)
    {
        await using var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath);
        PilotRemoteEntry written;
        try
        {
            if (preservedAfterVerify is not null)
            {
                var identity = stage.ReadIdentity();
                await preservedAfterVerify(new SyncDownloadStage(stagePath,
                    ObjectId(identity.VolumeSerialNumber, identity.FileId), recoveryPathForCheckpoint, Verified: false),
                    cancellationToken).ConfigureAwait(false);
            }
            var downloaded = await fill(stage.CreateGuardedWriteStream(), cancellationToken).ConfigureAwait(false);
            await stage.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stage.Stream.Flush(true);
            written = await DescribeFileAsync(stage, path.Relative, cancellationToken).ConfigureAwait(false);
            if (downloaded is not null && downloaded.Size != written.Size)
                throw new IOException("The downloaded bytes differ from the source content identity.");
            if (!string.Equals(written.ContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged bytes do not match the source content identity.");
            // RQ-036/038: stamp the verified, still-held stage after all writes are flushed and before
            // Recovery or publication. Never reopen the destination by name to stamp a possible replacement.
            // LocalWriter observes the published filesystem time for the baseline (including FS rounding).
            if (lastWriteUtc is { } modified) File.SetLastWriteTimeUtc(stage.Handle, modified.UtcDateTime);
            cancellationToken.ThrowIfCancellationRequested();
            // Owner decision: the overwritten original is archived only after the downloaded bytes are verified, so
            // a failed or cancelled download leaves it in place. A proven Recovery archive makes the publish
            // create-only because the exact planned original has been removed.
            if (preservedAfterVerify is not null && recoveryPathForCheckpoint.Length != 0)
            {
                await preservedAfterVerify(new SyncDownloadStage(stagePath, written.Id!, recoveryPathForCheckpoint),
                    cancellationToken).ConfigureAwait(false);
                if (recoveryPathForCheckpoint.Length != 0) expectedRemoteRevision = null;
            }
        }
        catch
        {
            stage.DeleteOnClose();
            throw;
        }

        SyncLocalFileAuthority? predecessor = null;
        var predecessorMoved = false;
        var published = false;
        try
        {
            if (expectedStubId is not null)
            {
                RequireStub(path, expectedStubId);
                predecessor = SyncLocalFileAuthority.OpenOnlineOnlyStub(path.FullPath);
                if (!PilotDirectoryLease.GetFileInformationByHandle(predecessor.Handle, out var held) ||
                    ObjectId(held.VolumeSerialNumber, ((ulong)held.FileIndexHigh << 32) | held.FileIndexLow) !=
                    expectedStubId ||
                    !string.Equals(predecessor.FinalPath, path.FullPath, StringComparison.OrdinalIgnoreCase))
                    throw Stale("online-only placeholder");
                RequireStub(path, expectedStubId);                 // still the stub now that it is held
                if (WindowsCloudFilesProvider.ReadStubState(predecessor.Handle) is not
                        { FullyDehydrated: true } state ||
                    state.FileId != (((ulong)held.FileIndexHigh << 32) | held.FileIndexLow))
                    throw new IOException(
                        "unreadable: the held cloud placeholder is not proven fully dehydrated and unmodified.");
            }
            else if (expectedRemoteRevision is not null)
            {
                predecessor = path.OpenFile(exclusive: true);
                var current = await DescribeFileAsync(predecessor, path.Relative, cancellationToken)
                    .ConfigureAwait(false);
                if (current.Revision != expectedRemoteRevision) throw Stale("replacement predecessor");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (predecessor is not null)
            {
                // Existing remote executors retain the original first. This adapter also keeps the
                // exact held predecessor across the filesystem's two atomic create-only renames.
                // No check-then-overwrite File.Move/Replace and no unguarded replacement are used.
                predecessor.RenameTo(control, SyncCreateNewLocalTransfer.StageName(Guid.NewGuid().ToString("N")));
                predecessorMoved = true;
            }
            stage.RenameTo(path.Parent, path.Name, replaceIfExists: false);
            published = true;
            PublishedBeforeCloseForTest?.Invoke();
            if (predecessor is not null) predecessor.DeleteOnClose();
            return new(written.Id!, written.Revision!, written.Size, written.ContentHash!);
        }
        catch (Exception error)
        {
            if (!published && predecessorMoved)
            {
                try { predecessor!.RenameTo(path.Parent, path.Name, replaceIfExists: false); }
                catch (IOException) { throw new DropboxEffectUncertainException(
                    expectedStubId is null
                        ? "Replacement was interrupted; the exact predecessor remains in private recovery. Reconcile before retry."
                        : "Stub replacement was interrupted; the placeholder remains in the private namespace. Reconcile before retry.",
                    error); }
            }
            if (published) throw new DropboxEffectUncertainException(
                "The replacement was published but completion needs reconciliation; do not replay it.", error);
            throw;
        }
        finally
        {
            // Only this call's own unpublised partial is removed; a published stage is the destination, and the
            // rolled-back predecessor is never deleted here.
            if (!published) stage.DeleteOnClose();
            predecessor?.Dispose();
        }
    }

    /// <summary>
    /// replaces one online-only cloud-files stub with the given bytes, as an ordinary local file.
    /// The stub has no local content, so no Recovery copy is made; it is never opened for data. The bytes are staged
    /// and verified first; the stub must still be the exact listed online-only placeholder (same NTFS file id) while
    /// it is held; it is moved aside, the stage is published in its place with a create-only rename, and only then is
    /// the held stub deleted. The new object is a fresh regular file: no reparse point or placeholder attribute
    /// survives, and no provider (connected or absent) is needed.
    /// </summary>
    public async Task<PilotUploadResult> ReplaceOnlineOnlyStubAsync(string relativePath, Stream source,
        string expectedContentHash, string expectedStubId, CancellationToken cancellationToken = default,
        DateTimeOffset? lastWriteUtc = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        using var path = new HeldPath(root, relativePath);
        var stageName = SyncCreateNewLocalTransfer.StageName(Guid.NewGuid().ToString("N"));
        return await FillAndPublishAsync(control, path, Path.Combine(control.Path, stageName), expectedContentHash,
            null, expectedStubId, string.Empty, null, async (destination, token) =>
            {
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
                return null;
            }, cancellationToken, lastWriteUtc).ConfigureAwait(false);
    }

    // The exact path is still the planned online-only cloud-files placeholder, read from its parent listing only.
    private static void RequireStub(HeldPath path, string expectedStubId)
    {
        path.Parent.VerifyBinding();
        DirectoryEntryInfo listed;
        try { listed = PilotPath.ListedEntry(path.FullPath); }
        catch (FileNotFoundException) { throw Stale("online-only placeholder (absent)"); }
        if ((listed.Attributes & FileAttributes.Directory) != 0 ||
            !PilotPath.IsOnlineOnlyStub(listed.Attributes, listed.ReparseTag) ||
            ObjectId(path.Parent.Identity.VolumeSerialNumber, (ulong)listed.FileId) != expectedStubId)
            throw Stale("online-only placeholder");
    }

    public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Recovery requires this endpoint's exact bound namespace.");
    public Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Recovery requires this endpoint's exact bound namespace.");

    public async Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName, CancellationToken cancellationToken = default)
    {
        RequireRecoveryBinding(binding);
        var name = PilotPath.NormalizeTopLevelName(deterministicName);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        using var path = new HeldPath(root, expected.RelativePath);
        using var source = path.OpenFile();
        RequireExact(expected, await DescribeFileAsync(source, path.Relative, cancellationToken));
        await using var copy = SyncLocalFileAuthority.CreateNewMovableFile(Path.Combine(control.Path, name));
        try
        {
            await source.Stream.CopyToAsync(copy.CreateGuardedWriteStream(), cancellationToken);
            await copy.Stream.FlushAsync(cancellationToken);
            copy.Stream.Flush(true);
            var stored = await DescribeFileAsync(copy, name, cancellationToken);
            if (stored.Size != expected.Size ||
                !string.Equals(stored.ContentHash, expected.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained bytes do not match the predecessor.");
            return new(stored.Id!, stored.Revision!, name, stored.Size, stored.ContentHash!);
        }
        catch { copy.DeleteOnClose(); throw; }
    }

    public Task<SyncArchiveResult> CopyRevisionToRecoveryAsync(PilotRemoteEntry expected,
        SyncRemoteRecoveryRootBinding binding, string deterministicName, CancellationToken cancellationToken = default) =>
        CopyToRecoveryAsync(expected, binding, deterministicName, cancellationToken);

    public async Task<SyncArchiveResult?> GetRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
        string deterministicName, CancellationToken cancellationToken = default)
    {
        RequireRecoveryBinding(binding);
        var name = PilotPath.NormalizeTopLevelName(deterministicName);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        using var path = new HeldPath(control, name);
        SyncLocalFileAuthority file;
        try { file = path.OpenFile(); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 })
        { return null; }
        using var held = file;
        var stored = await DescribeFileAsync(file, name, cancellationToken);
        return new(stored.Id!, stored.Revision!, name, stored.Size, stored.ContentHash!);
    }

    public async Task DeleteRecoveryAsync(SyncRemoteRecoveryRootBinding binding, SyncArchiveResult expected,
        CancellationToken cancellationToken = default)
    {
        RequireRecoveryBinding(binding);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        using var path = new HeldPath(control, PilotPath.NormalizeTopLevelName(expected.RecoveryPath));
        using var file = path.OpenFile(exclusive: true);
        RequireExact(RecoveryEntry(expected), await DescribeFileAsync(file, path.Relative, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        file.DeleteOnClose();
    }

    public async Task<PilotRemoteEntry> CopyRecoveryToPathAsync(SyncRemoteRecoveryRootBinding binding,
        SyncArchiveResult expected, string destinationRelativePath, CancellationToken cancellationToken = default)
    {
        RequireRecoveryBinding(binding);
        using var root = OpenRoot();
        using var control = SyncControlNamespace.OpenLocal(_control, root);
        using var path = new HeldPath(control, PilotPath.NormalizeTopLevelName(expected.RecoveryPath));
        using var source = path.OpenFile();
        RequireExact(RecoveryEntry(expected), await DescribeFileAsync(source, path.Relative, cancellationToken));
        var stored = await UploadHeldAsync(root, control, destinationRelativePath, source.Stream,
            expected.ContentHash, expectedRemoteRevision: null, cancellationToken, lastWriteUtc: null);
        return new(PilotPath.NormalizeRelative(destinationRelativePath), PilotEntryKind.File,
            stored.FileId, stored.Revision, stored.Size, stored.ContentHash);
    }

    private static PilotRemoteEntry RecoveryEntry(SyncArchiveResult expected) =>
        new(expected.RecoveryPath, PilotEntryKind.File, expected.FileId, expected.Revision, expected.Size, expected.ContentHash);

    public Task<PilotRemoteEntry> CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var root = OpenRoot();
        using var path = new HeldPath(root, relativePath);
        using var created = PilotDirectoryLease.CreateChild(path.Parent, path.Name, hidden: false);
        return Task.FromResult(DescribeDirectory(created, path.Relative));
    }

    public async Task<PilotRemoteEntry> MoveAsync(PilotRemoteEntry expected, string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        using var root = OpenRoot();
        using var sourcePath = new HeldPath(root, expected.RelativePath);
        using var target = new HeldPath(root, destinationRelativePath);
        if (expected.Kind == PilotEntryKind.Directory)
        {
            using var source = PilotDirectoryLease.OpenOwnedDirectory(sourcePath.FullPath);
            RequireExact(expected, DescribeDirectory(source, sourcePath.Relative));
            cancellationToken.ThrowIfCancellationRequested();
            source.RenameTo(target.Parent, target.Name);
            return DescribeDirectory(source, target.Relative);
        }
        using var file = sourcePath.OpenFile(exclusive: true);
        var current = await DescribeFileAsync(file, sourcePath.Relative, cancellationToken);
        RequireExact(expected, current);
        cancellationToken.ThrowIfCancellationRequested();
        file.RenameTo(target.Parent, target.Name, replaceIfExists: false);
        return current with { RelativePath = target.Relative };
    }

    public async Task<PilotUploadResult> CopyCreateOnlyAsync(PilotRemoteEntry expected, string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        using var root = OpenRoot();
        using var path = new HeldPath(root, expected.RelativePath);
        using var source = path.OpenFile();
        var current = await DescribeFileAsync(source, path.Relative, cancellationToken);
        RequireExact(expected, current);
        return await UploadCreateOnlyAsync(destinationRelativePath, source.Stream, current.ContentHash!, cancellationToken);
    }

    public async Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default)
    {
        using var root = OpenRoot();
        using var path = new HeldPath(root, expected.RelativePath);
        if (expected.Kind == PilotEntryKind.Directory)
        {
            using var directory = PilotDirectoryLease.OpenOwnedDirectory(path.FullPath);
            RequireExact(expected, DescribeDirectory(directory, path.Relative));
            cancellationToken.ThrowIfCancellationRequested();
            directory.DeleteOnClose(); // Windows atomically refuses a nonempty directory.
            return;
        }
        using var file = path.OpenFile(exclusive: true);
        RequireExact(expected, await DescribeFileAsync(file, path.Relative, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        file.DeleteOnClose();
    }
}
