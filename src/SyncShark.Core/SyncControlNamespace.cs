using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

internal sealed record SyncLocalControlNamespaceAllocationPlan(
    string InstallationId,
    string NamespaceId,
    string SelectedRootPath,
    PilotDirectoryIdentity SelectedRootIdentity,
    string ParentPath,
    PilotDirectoryIdentity ParentIdentity,
    string ParentIdentityText,
    string DeterministicName,
    string ControlPath,
    string OwnerMarkerHash);

public static class SyncControlNamespace
{
    public const string RemoteContainerPath = SyncDropboxArtifactPaths.RecoveryContainerPath;
    internal const string LocalPrefix = ".syncshark-control-";
    internal const string MarkerName = ".syncshark-owner.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    internal static Action<string>? AllocationBoundaryForTest { get; set; }

    public static string NormalizeInstallationId(string value) => NormalizeIdentifier(value, "installation");

    public static string NormalizeNamespaceId(string value) => NormalizeIdentifier(value, "namespace");

    public static string LocalRootFor(string selectedRoot, string namespaceId)
    {
        var root = PilotPath.NormalizeLocalRoot(selectedRoot);
        var parent = Directory.GetParent(root)?.FullName ??
                     throw new InvalidDataException("The selected local root has no bounded parent for private staging.");
        return Path.Combine(parent, LocalPrefix + NormalizeNamespaceId(namespaceId));
    }

    [SupportedOSPlatform("windows")]
    internal static SyncLocalControlNamespaceAllocationPlan PlanLocalAllocation(
        PilotDirectoryLease selectedRoot, string installationId, string namespaceId)
    {
        installationId = NormalizeInstallationId(installationId);
        namespaceId = NormalizeNamespaceId(namespaceId);
        selectedRoot.VerifyBinding();
        var parentPath = Directory.GetParent(selectedRoot.Path)?.FullName ??
                         throw new InvalidDataException("The selected local root has no bounded parent for private staging.");
        using var parent = OpenAllocationParent(parentPath);
        parent.VerifyBinding();
        if (parent.Identity.VolumeSerialNumber != selectedRoot.Identity.VolumeSerialNumber)
            throw new IOException("The private staging parent is not on the selected pair volume.");
        selectedRoot.VerifyBinding();
        var deterministicName = LocalPrefix + namespaceId;
        var markerHash = DropboxContentHash.Compute(MarkerBytes(installationId, namespaceId));
        return new SyncLocalControlNamespaceAllocationPlan(installationId, namespaceId, selectedRoot.Path,
            selectedRoot.Identity, parent.Path, parent.Identity, FormatIdentity(parent.Identity), deterministicName,
            Path.Combine(parent.Path, deterministicName), markerHash);
    }

    [SupportedOSPlatform("windows")]
    internal static SyncLocalControlNamespaceBinding BindLocalForTest(PilotDirectoryLease selectedRoot,
        string installationId, string namespaceId) =>
        BindAuthorizedLocalAsync(selectedRoot, PlanLocalAllocation(selectedRoot, installationId, namespaceId),
            expectedExistingUnmarkedIdentity: null, _ => Task.CompletedTask).GetAwaiter().GetResult();

    [SupportedOSPlatform("windows")]
    internal static Task<SyncLocalControlNamespaceBinding?> InspectAuthorizedLocalAsync(
        PilotDirectoryLease selectedRoot, SyncLocalControlNamespaceAllocationPlan plan, bool sharedRead = false) =>
        BindLocalCoreAsync(selectedRoot, plan, createWhenMissing: false,
            expectedExistingUnmarkedIdentity: null, _ => Task.CompletedTask, sharedRead);

    [SupportedOSPlatform("windows")]
    internal static Task<SyncLocalControlNamespaceBinding?> ResumeCreatedLocalAsync(
        PilotDirectoryLease selectedRoot, SyncLocalControlNamespaceAllocationPlan plan,
        PilotDirectoryIdentity expectedCreatedIdentity) =>
        BindLocalCoreAsync(selectedRoot, plan, createWhenMissing: false,
            expectedCreatedIdentity, _ => Task.CompletedTask);

    [SupportedOSPlatform("windows")]
    internal static async Task<SyncLocalControlNamespaceBinding> BindAuthorizedLocalAsync(
        PilotDirectoryLease selectedRoot, SyncLocalControlNamespaceAllocationPlan plan,
        PilotDirectoryIdentity? expectedExistingUnmarkedIdentity,
        Func<PilotDirectoryIdentity, Task> onDirectoryCreated)
    {
        ArgumentNullException.ThrowIfNull(onDirectoryCreated);
        return await BindLocalCoreAsync(selectedRoot, plan, createWhenMissing: true,
                   expectedExistingUnmarkedIdentity, onDirectoryCreated) ??
               throw new IOException("The authorized private control namespace was not created.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<SyncLocalControlNamespaceBinding?> BindLocalCoreAsync(
        PilotDirectoryLease selectedRoot, SyncLocalControlNamespaceAllocationPlan plan, bool createWhenMissing,
        PilotDirectoryIdentity? expectedExistingUnmarkedIdentity,
        Func<PilotDirectoryIdentity, Task> onDirectoryCreated, bool sharedRead = false)
    {
        ValidateAllocationPlan(selectedRoot, plan);
        using var parent = OpenAllocationParent(plan.ParentPath, plan.ParentIdentity);
        parent.VerifyBinding();
        selectedRoot.VerifyBinding();
        if (!Directory.Exists(plan.ControlPath) && !File.Exists(plan.ControlPath) && !createWhenMissing)
            return null;
        if (File.Exists(plan.ControlPath))
            throw new IOException("The authorized private staging path is occupied by a non-directory object.");

        PilotDirectoryLease control;
        var created = false;
        if (!Directory.Exists(plan.ControlPath))
        {
            try
            {
                control = PilotDirectoryLease.CreateChild(parent, plan.DeterministicName);
                created = true;
            }
            catch (IOException error) when (error.HResult == unchecked((int)0x800700B7))
            {
                // A concurrent creator won. Open verifies directory/reparse identity;
                // the existing marker checks below still own adoption authority.
                // Never mark this as ours or grant cleanup rights over its contents.
                control = PilotDirectoryLease.Open(plan.ControlPath);
            }
        }
        else
        {
            control = PilotDirectoryLease.Open(plan.ControlPath);
        }

        using (control)
        {
            try
            {
                selectedRoot.VerifyBinding();
                parent.VerifyBinding();
                if (control.Identity.VolumeSerialNumber != selectedRoot.Identity.VolumeSerialNumber)
                    throw new IOException("The private staging root is not on the selected pair volume.");
                if (created)
                {
                    ApplyRestrictiveSecurity(plan.ControlPath);
                    AllocationBoundaryForTest?.Invoke("directory-created-before-authority-record");
                    await onDirectoryCreated(control.Identity);
                    AllocationBoundaryForTest?.Invoke("directory-authority-recorded-before-marker");
                }
                control.VerifyBinding();

                var markerBytes = MarkerBytes(plan.InstallationId, plan.NamespaceId);
                if (!string.Equals(DropboxContentHash.Compute(markerBytes), plan.OwnerMarkerHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The authorized private staging owner-marker hash changed.");
                var markerPath = Path.Combine(plan.ControlPath, MarkerName);
                PilotDirectoryIdentity markerIdentity;
                if (!File.Exists(markerPath))
                {
                    if (!created && (expectedExistingUnmarkedIdentity is null ||
                                     control.Identity != expectedExistingUnmarkedIdentity))
                        throw new IOException(
                            "A pre-existing private staging root has no exact retained creation identity.");
                    using var marker = SyncLocalFileAuthority.CreateNewFile(markerPath);
                    using (var writer = marker.CreateGuardedWriteStream())
                    {
                        writer.Write(markerBytes);
                        writer.Flush();
                        marker.Stream.Flush(true);
                    }
                    markerIdentity = marker.VerifySingleLink();
                    AllocationBoundaryForTest?.Invoke("marker-written-before-binding-return");
                }
                else
                {
                    if (expectedExistingUnmarkedIdentity is not null)
                        SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(markerPath, markerPath);
                    using var marker = sharedRead
                        ? SyncLocalFileAuthority.OpenExistingSharedRead(markerPath)
                        : SyncLocalFileAuthority.OpenExistingExclusive(markerPath);
                    markerIdentity = marker.VerifySingleLink();
                    var observed = ReadBounded(marker.Stream);
                    if (!observed.AsSpan().SequenceEqual(markerBytes))
                        throw new IOException("The private staging ownership record belongs to another installation or namespace.");
                }

                control.VerifyBinding();
                selectedRoot.VerifyBinding();
                return new SyncLocalControlNamespaceBinding(control.Path, control.Identity.VolumeSerialNumber,
                    control.Identity.FileId, FormatIdentity(markerIdentity), plan.OwnerMarkerHash);
            }
            catch
            {
                if (created)
                {
                    try
                    {
                        var markerPath = Path.Combine(plan.ControlPath, MarkerName);
                        if (File.Exists(markerPath))
                        {
                            using var marker = SyncLocalFileAuthority.OpenExistingExclusive(markerPath);
                            marker.DeleteOnClose();
                        }
                        if (!Directory.EnumerateFileSystemEntries(plan.ControlPath).Any()) control.DeleteOnClose();
                    }
                    catch { }
                }
                throw;
            }
        }
    }

    private static void ValidateAllocationPlan(PilotDirectoryLease selectedRoot,
        SyncLocalControlNamespaceAllocationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        selectedRoot.VerifyBinding();
        if (!string.Equals(selectedRoot.Path, plan.SelectedRootPath, StringComparison.OrdinalIgnoreCase) ||
            selectedRoot.Identity != plan.SelectedRootIdentity ||
            !string.Equals(LocalPrefix + NormalizeNamespaceId(plan.NamespaceId), plan.DeterministicName,
                StringComparison.Ordinal) ||
            !string.Equals(LocalRootFor(selectedRoot.Path, plan.NamespaceId), plan.ControlPath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(DropboxContentHash.Compute(MarkerBytes(plan.InstallationId, plan.NamespaceId)),
                plan.OwnerMarkerHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(FormatIdentity(plan.ParentIdentity), plan.ParentIdentityText, StringComparison.Ordinal))
            throw new IOException("The private staging allocation plan no longer matches the selected root authority.");
    }

    private static PilotDirectoryLease OpenAllocationParent(string parentPath,
        PilotDirectoryIdentity? expected = null)
    {
        var parentIsVolumeRoot = string.Equals(
            Path.GetFullPath(parentPath).TrimEnd(Path.AltDirectorySeparatorChar),
            Path.GetPathRoot(parentPath)?.TrimEnd(Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        return parentIsVolumeRoot
            ? PilotDirectoryLease.OpenVolumeRootForAllocation(parentPath, expected)
            : PilotDirectoryLease.Open(parentPath, expected);
    }

    public static PilotDirectoryLease OpenLocal(SyncControlNamespaceBinding binding, PilotDirectoryLease selectedRoot)
    {
        Validate(binding);
        selectedRoot.VerifyBinding();
        var local = binding.Local;
        if (!string.Equals(Path.GetFullPath(local.RootPath), LocalRootFor(selectedRoot.Path, binding.NamespaceId),
                StringComparison.OrdinalIgnoreCase) || local.RootVolumeSerialNumber != selectedRoot.Identity.VolumeSerialNumber)
            throw new IOException("The private staging root is not the exact same-volume pair namespace.");
        // The shared namespace is an identity anchor, not this operation's
        // deletion target. Read/attribute leases can coexist while still denying
        // DELETE sharing. Each private transaction retains its own delete lease.
        var lease = PilotDirectoryLease.OpenDirectoryReadGuard(local.RootPath,
            new PilotDirectoryIdentity(local.RootVolumeSerialNumber, local.RootFileId));
        try
        {
            VerifyLocalMarker(binding, lease);
            selectedRoot.VerifyBinding();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static void VerifyLocalMarker(SyncControlNamespaceBinding binding, PilotDirectoryLease control)
    {
        Validate(binding);
        control.VerifyBinding();
        var markerPath = Path.Combine(control.Path, MarkerName);
        // Several operations may verify the immutable ownership marker together.
        // ReadGuard allows only other readers, still excluding writes and deletion.
        using var marker = SyncLocalFileAuthority.OpenExistingReadGuard(markerPath);
        VerifyLocalMarkerCore(binding, control, marker);
    }

    /// <summary>
    /// RC639-2.AUTH-2: verify the same immutable ownership marker through an ALREADY HELD marker read
    /// guard, so a caller can keep the exact single-link marker pinned (excluding writers, deleters and
    /// hardlink substitution) across a decisive transaction. This is the one existing validator used with a
    /// held guard; no second marker-validation implementation exists.
    /// </summary>
    internal static void VerifyLocalMarker(SyncControlNamespaceBinding binding, PilotDirectoryLease control,
        SyncLocalFileAuthority heldMarker)
    {
        ArgumentNullException.ThrowIfNull(heldMarker);
        Validate(binding);
        control.VerifyBinding();
        VerifyLocalMarkerCore(binding, control, heldMarker);
    }

    private static void VerifyLocalMarkerCore(SyncControlNamespaceBinding binding, PilotDirectoryLease control,
        SyncLocalFileAuthority marker)
    {
        var expectedIdentity = SyncLocalRecovery.ParseSourceIdentity(binding.Local.MarkerFileId);
        marker.VerifySameSingleLink(expectedIdentity);
        var bytes = ReadBounded(marker.Stream);
        if (!bytes.AsSpan().SequenceEqual(MarkerBytes(binding.InstallationId, binding.NamespaceId)) ||
            !string.Equals(DropboxContentHash.Compute(bytes), binding.Local.MarkerContentHash,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The private staging ownership record changed.");
        control.VerifyBinding();
    }

    public static void Validate(SyncControlNamespaceBinding binding)
    {
        NormalizeInstallationId(binding.InstallationId);
        NormalizeNamespaceId(binding.NamespaceId);
        var local = binding.Local ?? throw new InvalidDataException("The local control namespace binding is absent.");
        if (local.RootVolumeSerialNumber == 0 || local.RootFileId == 0 ||
            string.IsNullOrWhiteSpace(local.MarkerContentHash) || local.MarkerContentHash.Length != 64)
            throw new InvalidDataException("The local control namespace binding is incomplete.");
        SyncLocalRecovery.ParseSourceIdentity(local.MarkerFileId);
        PilotPath.NormalizeLocalRoot(local.RootPath);
        if (binding.Remote is { } remote &&
            (!remote.RootPath.StartsWith(RemoteContainerPath + "/", StringComparison.Ordinal) ||
             !remote.RootFolderId.StartsWith("id:", StringComparison.Ordinal) ||
             !remote.MarkerFileId.StartsWith("id:", StringComparison.Ordinal) ||
             string.IsNullOrWhiteSpace(remote.MarkerRevision) || remote.MarkerContentHash.Length != 64 ||
             !string.Equals(remote.MarkerPath, remote.RootPath + "/" + MarkerName, StringComparison.Ordinal)))
            throw new InvalidDataException("The legacy Dropbox control namespace binding is incomplete.");
    }

    public static byte[] MarkerBytes(string installationId, string namespaceId) =>
        JsonSerializer.SerializeToUtf8Bytes(new OwnershipRecord("Massdex.SyncShark.ControlNamespace.v1",
            NormalizeInstallationId(installationId), NormalizeNamespaceId(namespaceId)), JsonOptions);

    public static void RequireRemotePairRootAllowed(string dropboxRoot)
    {
        var root = PilotPath.NormalizeDropboxRoot(dropboxRoot);
        if (string.Equals(root, "/", StringComparison.Ordinal)) return;
        if (string.Equals(root, RemoteContainerPath, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(RemoteContainerPath + "/", StringComparison.OrdinalIgnoreCase) ||
            RemoteContainerPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A Dropbox pair root cannot equal, contain, or be contained by the Sync Shark recovery namespace.");
    }

    public static void RequireLocalPairRootAllowed(string localRoot)
    {
        var root = PilotPath.NormalizeLocalRoot(localRoot);
        if (root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A local pair root cannot equal or be inside a Sync Shark private control namespace.");
    }

    private static byte[] ReadBounded(Stream stream)
    {
        if (!stream.CanSeek || stream.Length is <= 0 or > 4096)
            throw new InvalidDataException("The control namespace ownership record length is not accepted.");
        stream.Position = 0;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        stream.Position = 0;
        return bytes;
    }

    private static string NormalizeIdentifier(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length is < 16 or > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException($"The Sync Shark {role} identifier is not accepted.");
        return value;
    }

    private static string FormatIdentity(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";

    [SupportedOSPlatform("windows")]
    private static void ApplyRestrictiveSecurity(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current Windows identity has no SID.");
        var security = new DirectorySecurity();
        // The restrictive DACL is mandatory; setting the owner is not. Persist them separately
        // so a filtered token's denied WRITE_OWNER cannot prevent private staging allocation.
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.SetAccessControl(security);
        var ownership = new DirectorySecurity();
        ownership.SetOwner(sid);
        try { directory.SetAccessControl(ownership); }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(
                "Private staging DACL applied; owner change was not permitted and the existing owner was retained.");
        }
    }

    private sealed record OwnershipRecord(string Schema, string InstallationId, string NamespaceId);
}
