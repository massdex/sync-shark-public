using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public static class PilotPath
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Extended-length form for native Windows opens; stored paths remain unchanged.</summary>
    internal static string ToExtendedWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

    public static string NormalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized)) throw new ArgumentException("A relative path is required.", nameof(value));
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                              IsUnsupportedWindowsSegment(part)))
            throw new ArgumentException("The relative path contains an unsafe segment.", nameof(value));
        return string.Join('/', parts);
    }

    private static bool IsUnsupportedWindowsSegment(string value)
    {
        if (value.EndsWith(' ') || value.EndsWith('.')) return true;
        var stem = value.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    public static string CanonicalComparisonKey(string value) =>
        NormalizeRelative(value).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    // Metadata only. A provider may retain a name Windows cannot materialize. Such an
    // item must remain visible as blocked evidence, not abort its healthy siblings or
    // become proof of absence. NEVER use this method to authorize a filesystem effect.
    internal static string NormalizeInventoryPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return string.Empty; // Inventory/provider root sentinel; never filesystem authority.
        if (value.StartsWith('/') || value.Contains('\\') ||
            value.Any(char.IsControl) || value.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("Inventory metadata requires a contained relative provider path.", nameof(value));
        return value;
    }

    // Provider-relative paths are not Windows paths: a colon is a filename character, not a drive.
    // This validator never authorizes a local filesystem operation.
    internal static string NormalizeDropboxRelative(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.StartsWith('/') || value.Contains('\\') || value.Any(char.IsControl) ||
            value.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("A contained relative Dropbox path is required.", nameof(value));
        return value;
    }

    internal static string DropboxComparisonKey(string value) =>
        NormalizeDropboxRelative(value).Normalize(NormalizationForm.FormC).ToUpperInvariant();

    internal static bool IsWindowsTransferPath(string value)
    {
        try { return string.Equals(NormalizeRelative(value), value, StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    public static string ResolveContainedPath(string root, string relativePath)
    {
        var normalizedRoot = NormalizeLocalRoot(root);
        var normalizedRelative = NormalizeRelative(relativePath);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot,
            normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The relative path escapes the selected local root.");
        return combined;
    }

    public static bool IsPathInside(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasExactRelativePath(string root, string candidate, string expectedRelativePath)
    {
        if (!IsPathInside(root, candidate)) return false;
        var observed = NormalizeRelative(Path.GetRelativePath(Path.GetFullPath(root),
            Path.GetFullPath(candidate)));
        return string.Equals(observed, NormalizeRelative(expectedRelativePath), StringComparison.Ordinal);
    }

    public static string NormalizeTopLevelName(string value)
    {
        var normalized = NormalizeRelative(value);
        if (normalized.Contains('/')) throw new ArgumentException("The legacy upload-only workflow accepts top-level files only.", nameof(value));
        return normalized;
    }

    public static string NormalizeLocalRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(full) || string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A bounded local folder below the volume root is required.", nameof(value));
        return full;
    }

    public static void RequireSeparatedFromProductState(string localRoot) =>
        RequireSeparatedFromProductState(localRoot, SyncSharkRuntimePaths.ProductRoot);

    internal static void RequireSeparatedFromProductState(string localRoot, string productRoot)
    {
        var full = NormalizeLocalRoot(localRoot);
        foreach (var protectedRoot in new[] { productRoot, Path.Combine(productRoot, "state"),
                     System.IO.Path.GetDirectoryName(SyncSharkRuntimePaths.InstalledVersionsRoot)! })
        {
            var protectedFull = Path.GetFullPath(protectedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsSameOrAncestor(full, protectedFull) || IsSameOrAncestor(protectedFull, full))
                throw new InvalidDataException("The Sync Shark local folder must not equal, contain, or be contained by Sync Shark product or state storage.");
        }
    }

    private static bool IsSameOrAncestor(string candidateAncestor, string candidateChild) =>
        string.Equals(candidateAncestor, candidateChild, StringComparison.OrdinalIgnoreCase) ||
        candidateChild.StartsWith(candidateAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeDropboxRoot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = "/" + string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new ArgumentException("The Dropbox path contains an unsafe segment.", nameof(value));
        return normalized;
    }

    public static string DropboxNamespaceRootIdentity(string rootNamespaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootNamespaceId);
        return "ns:" + rootNamespaceId.Trim();
    }

    public static bool IsDropboxNamespaceRootIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith("ns:", StringComparison.Ordinal) && value.Length > 3;

    public static bool IsDropboxFolderIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        ((value.StartsWith("id:", StringComparison.Ordinal) && value.Length > 3) ||
         IsDropboxNamespaceRootIdentity(value));

    public static bool IsUnsafeAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    public static bool IsOnlineOnlyAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;

    /// <summary>IO_REPARSE_TAG_CLOUD (0x9000001A) and its sixteen IO_REPARSE_TAG_CLOUD_1..F variants (0x9000x01A).</summary>
    public static bool IsCloudFilesReparseTag(uint tag) => (tag & 0xFFFF0FFFu) == 0x9000001Au;

    /// <summary>
    /// A Windows cloud-files placeholder (any provider, connected or not) whose content is fully on this device: a
    /// cloud reparse tag and none of OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS. Its bytes and children are
    /// ordinary local data, so it is inventoried, read and replaced like a normal file or folder. Symlinks,
    /// junctions, mount points and every other tag stay unsafe.
    /// </summary>
    public static bool IsLocalCloudFilesObject(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsCloudFilesReparseTag(reparseTag) &&
        !IsOnlineOnlyAttributes(attributes);

    /// <summary>
    /// An online-only stub candidate OFFLINE / RECALL bits and a cloud tag or hidden reparse
    /// point. This is only a conservative no-data-open filter, NOT replacement/deletion authority. B-169 requires
    /// positive native placeholder state and full dehydration on the held file before replacement; OFFLINE alone
    /// becomes unreadable. Another tag (HSM etc.) is not a candidate.
    /// </summary>
    public static bool IsOnlineOnlyStub(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.Directory) == 0 && IsOnlineOnlyAttributes(attributes) &&
        ((attributes & FileAttributes.ReparsePoint) == 0 || IsCloudFilesReparseTag(reparseTag));

    /// <summary>A link that is never followed (decision 59): a junction or mount point, a symlink, or a WSL symlink.</summary>
    public static bool IsLinkReparseTag(uint tag) => tag is 0xA0000003u or 0xA000000Cu or 0xA000001Du;

    /// <summary>A reparse point carrying a link tag (decision 59), whatever other attribute bits it has.</summary>
    public static bool IsLink(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsLinkReparseTag(reparseTag);

    /// <summary>
    /// The one classification order for an unsafe-looking listed item (core v2): a link first (a file symlink can carry
    /// OFFLINE/RECALL bits), then a fully local cloud placeholder, then an online-only file; anything else is None.
    /// </summary>
    public static ForeignObjectKind ClassifyForeign(FileAttributes attributes, uint reparseTag) =>
        IsLink(attributes, reparseTag) ? ForeignObjectKind.Link
        : IsLocalCloudFilesObject(attributes, reparseTag) ? ForeignObjectKind.LocalCloud
        : (attributes & FileAttributes.Directory) == 0 && IsOnlineOnlyAttributes(attributes)
            ? ForeignObjectKind.OnlineOnlyFile
            : ForeignObjectKind.None;

    /// <summary><see cref="IsUnsafeAttributes"/> except a fully local cloud-files placeholder, which is safe.</summary>
    public static bool IsUnsafeObject(FileAttributes attributes, uint reparseTag) =>
        IsUnsafeAttributes(attributes) && !IsLocalCloudFilesObject(attributes, reparseTag);

    /// <summary>Unsafe attributes on a held object, except a fully local cloud-files placeholder .</summary>
    internal static bool IsUnsafeHandle(SafeFileHandle handle, FileAttributes attributes)
    {
        if (!IsUnsafeAttributes(attributes)) return false;
        var (held, tag) = AttributeTag(handle);
        return IsUnsafeObject(held, tag);
    }

    /// <summary>The attributes and reparse tag of an already open handle (FileAttributeTagInfo); no data access.</summary>
    internal static (FileAttributes Attributes, uint ReparseTag) AttributeTag(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new IOException("The local item's attributes and reparse tag could not be read.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return ((FileAttributes)info.FileAttributes,
            (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? info.ReparseTag : 0);
    }

    /// <summary>
    /// The attributes and reparse tag of a path, read from its parent's directory listing so the item itself is
    /// never opened (an online-only placeholder must not be). Throws FileNotFoundException when the name is absent.
    /// </summary>
    public static (FileAttributes Attributes, uint ReparseTag) ListedAttributeTag(string fullPath)
    {
        var entry = ListedEntry(fullPath);
        return (entry.Attributes, entry.ReparseTag);
    }

    /// <summary>The parent listing's entry for a path (identity, size, attributes, tag); the item is never opened.</summary>
    public static DirectoryEntryInfo ListedEntry(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var parent = Path.GetDirectoryName(full) ?? throw new IOException("A volume root has no listing entry.");
        var name = Path.GetFileName(full);
        foreach (var entry in WindowsDirectoryListing.List(parent))
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return entry;
        throw new FileNotFoundException("The local item is not listed by its parent folder.", full);
    }

    /// <summary>
    /// <see cref="IsUnsafeAttributes"/> for a path, accepting a fully local cloud-files placeholder. The reparse tag
    /// comes from the parent listing, so an online-only or foreign item is classified without being opened.
    /// </summary>
    public static bool IsUnsafePath(string fullPath)
    {
        var attributes = File.GetAttributes(fullPath);
        if (!IsUnsafeAttributes(attributes)) return false;
        if ((attributes & FileAttributes.ReparsePoint) == 0) return true;
        var (listed, tag) = ListedAttributeTag(fullPath);
        return IsUnsafeObject(listed, tag);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, uint bufferSize);
}

/// <summary>How core v2 treats an unsafe-looking listed item (see PilotPath.ClassifyForeign).</summary>
public enum ForeignObjectKind { None, Link, LocalCloud, OnlineOnlyFile }

public sealed record PilotDirectoryIdentity(uint VolumeSerialNumber, ulong FileId);

public sealed class PilotDirectoryLease : IDisposable
{
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint Synchronize = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    internal const uint FileReadAttributes = 0x80;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const int FileDispositionInfo = 4;
    private const int FileRenameInformation = 10;
    private readonly SafeFileHandle _handle;
    private readonly bool _canDelete;

    private PilotDirectoryLease(string path, SafeFileHandle handle, PilotDirectoryIdentity identity, bool canDelete = false)
    {
        Path = path;
        _handle = handle;
        Identity = identity;
        _canDelete = canDelete;
    }

    public string Path { get; private set; }
    public PilotDirectoryIdentity Identity { get; }
    internal SafeFileHandle Handle => _handle;

    public static PilotDirectoryLease Open(string path, PilotDirectoryIdentity? expected = null)
        => OpenCore(path, expected, allowProductState: false);

    internal static PilotDirectoryLease OpenVolumeRootForAllocation(string path,
        PilotDirectoryIdentity? expected = null)
        => OpenCore(path, expected, allowProductState: false, allowVolumeRoot: true);

    internal static PilotDirectoryLease OpenProductDirectory(string path, PilotDirectoryIdentity? expected = null)
        => OpenCore(path, expected, allowProductState: true);

    internal static PilotDirectoryLease OpenProductDirectoryReadGuard(string path)
        => OpenCore(path, expected: null, allowProductState: true, directoryReadAccess: true);

    public static PilotDirectoryLease OpenInstalledRuntime(string directory)
    {
        var full = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(full))
            throw new IOException("Only an exact versioned Sync Shark installation can be held by this operation.");
        var lease = OpenProductDirectory(full);
        try
        {
            if (!SyncSharkRuntimePaths.HasTestProductRoot) RequireProtectedRuntime(full);
            lease.VerifyBinding();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static void RequireProtectedRuntime(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected runtime checks require Windows.");
        if (!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(directory))
            throw new IOException("The protected application location changed.");
        // Deny unsafe grants, including parent delete-child and owner/DACL changes.
        // This proves filesystem protection, not resistance to per-user package re-registration.
        string[] owners = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if ((current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new IOException("Protected code cannot use redirected or offline directories.");
            var acl = current.GetAccessControl();
            if (!owners.Contains(acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? "", StringComparer.Ordinal))
                throw new UnauthorizedAccessException("The application directory has an untrusted owner.");
            long unsafeRights = current.Parent is null ? 0x500d0040L : 0x500d0156L;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow &&
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                    ((long)rule.FileSystemRights & unsafeRights) != 0 &&
                    !owners.Contains(rule.IdentityReference.Value, StringComparer.Ordinal))
                    throw new UnauthorizedAccessException("The application directory is writable by an ordinary user.");
        }
    }

    internal static PilotDirectoryLease OpenOwnedDirectory(string path, PilotDirectoryIdentity expected)
        => OpenCore(path, expected, allowProductState: false, deleteAccess: true);

    internal static PilotDirectoryLease OpenOwnedDirectory(string path)
        => OpenCore(path, expected: null, allowProductState: false, deleteAccess: true);

    internal static PilotDirectoryLease OpenDirectoryReadGuard(string path, PilotDirectoryIdentity expected)
        => OpenCore(path, expected, allowProductState: false, directoryReadAccess: true);

    private static PilotDirectoryLease OpenCore(string path, PilotDirectoryIdentity? expected, bool allowProductState,
        bool deleteAccess = false, bool allowVolumeRoot = false, bool directoryReadAccess = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sync Shark requires Windows NTFS directory identities.");
        var normalized = allowVolumeRoot
            ? System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.AltDirectorySeparatorChar)
            : PilotPath.NormalizeLocalRoot(path);
        var normalizedVolumeRoot = System.IO.Path.GetPathRoot(normalized)?.TrimEnd(System.IO.Path.AltDirectorySeparatorChar);
        var isVolumeRoot = string.Equals(normalized, normalizedVolumeRoot, StringComparison.OrdinalIgnoreCase);
        if (isVolumeRoot != allowVolumeRoot)
            throw new ArgumentException(allowVolumeRoot
                ? "The allocation parent must be the exact volume root."
                : "A bounded local folder below the volume root is required.", nameof(path));
        if (!allowVolumeRoot)
        {
            if (!allowProductState) PilotPath.RequireSeparatedFromProductState(normalized);
        }
        // Deny DELETE sharing as an additional provider guard. Correctness does not rely on that denial:
        // every effect revalidates this directory handle and local commits rename held objects relative to it.
        // Attribute-only directory handles do not establish a Windows read-share
        // reservation. A shared immutable anchor requests LIST_DIRECTORY so its
        // denial of delete sharing participates in real sharing checks.
        var desiredAccess = FileReadAttributes | (directoryReadAccess ? FileListDirectory : 0) |
                            (deleteAccess ? DeleteAccess | WriteDac : 0);
        // Only the native open uses the extended-length form: a private transaction or stage name can push the
        // full path past the Win32 limit, and an unextended path returns ERROR_PATH_NOT_FOUND. The stored and
        // compared path, the final-path check and every access/share/reparse/NTFS/DACL/identity check below
        // continue to use the exact normalized path unchanged.
        var handle = CreateFile(normalized, desiredAccess,
            FileShare.Read | FileShare.Write, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var native = Marshal.GetLastWin32Error();
            handle.Dispose();
            var detail = $"The selected local folder '{normalized}' could not be opened for identity verification. Win32={native}, HRESULT=0x{(0x80070000u | (uint)native):X8}.";
            var error = new IOException(detail, new System.ComponentModel.Win32Exception(native));
            NativeIoDiagnostic.LogOnce(normalized, error);
            throw error;
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("The selected local folder identity could not be read.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            var supportedCloudRoot = PilotPath.IsUnsafeAttributes(info.FileAttributes) &&
                                     (!IsUnsafeHeld(handle, info.FileAttributes) ||
                                      WindowsCloudFilesProvider.IsOwnedSyncRoot(normalized) ||
                                      WindowsCloudFilesProvider.IsCloudFilesPlaceholder(normalized));
            if ((info.FileAttributes & FileAttributes.Directory) == 0 ||
                (PilotPath.IsUnsafeAttributes(info.FileAttributes) && !supportedCloudRoot))
                throw new IOException("The selected local root is not a regular on-disk directory.");
            var observedPath = FinalPath(handle);
            if (!string.Equals(observedPath, normalized, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected local folder path does not match its opened directory object.");
            var fileSystem = new char[32];
            if (!GetVolumeInformation(System.IO.Path.GetPathRoot(normalized)!, null, 0, out var serial, out _, out _, fileSystem, (uint)fileSystem.Length))
                throw new IOException("The local volume identity could not be read.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            if (!new string(fileSystem).TrimEnd('\0').Equals("NTFS", StringComparison.OrdinalIgnoreCase) || serial != info.VolumeSerialNumber)
                throw new IOException("Sync Shark requires a stable NTFS local folder identity.");
            var identity = new PilotDirectoryIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            if (expected is not null && identity != expected) throw new IOException("The selected local folder identity differs from the saved pair.");
            return new PilotDirectoryLease(normalized, handle, identity, deleteAccess);
        }
        catch (Exception error)
        {
            handle.Dispose();
            NativeIoDiagnostic.LogOnce(normalized, error);
            throw;
        }
    }

    public void VerifyBinding()
    {
        try
        {
            if (!GetFileInformationByHandle(_handle, out var info))
                throw new IOException("The selected local folder identity is no longer readable.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            var current = new PilotDirectoryIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            var supportedCloudRoot = PilotPath.IsUnsafeAttributes(info.FileAttributes) &&
                                     (!IsUnsafeHeld(_handle, info.FileAttributes) ||
                                      WindowsCloudFilesProvider.IsOwnedSyncRoot(Path) ||
                                      WindowsCloudFilesProvider.IsCloudFilesPlaceholder(Path));
            if (current != Identity)
                throw new IOException("The selected local folder changed physical identity.");
            if (PilotPath.IsUnsafeAttributes(info.FileAttributes) && !supportedCloudRoot)
                throw new IOException("The selected local folder became a reparse point or unsafe object.");
            if (!string.Equals(FinalPath(_handle), Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected local folder moved outside its retained exact path.");
        }
        catch (Exception error)
        {
            NativeIoDiagnostic.LogOnce(Path, error);
            throw;
        }
    }

    internal static PilotDirectoryLease CreateChild(PilotDirectoryLease parent, string childName,
        bool hidden = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private control directories require Windows NTFS handles.");
        parent.VerifyBinding();
        var safeName = PilotPath.NormalizeTopLevelName(childName);
        var fullPath = System.IO.Path.Combine(parent.Path, safeName);
        var nameBuffer = Marshal.StringToHGlobalUni(safeName);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var parentAdded = false;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(safeName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((safeName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            parent.Handle.DangerousAddRef(ref parentAdded);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.Handle.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = 0,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = NtCreateFile(out var handle,
                DeleteAccess | WriteDac | Synchronize | FileListDirectory | FileReadAttributes,
                ref attributes, out _, IntPtr.Zero,
                hidden ? (uint)FileAttributes.Hidden : (uint)FileAttributes.Normal,
                FileShare.Read | FileShare.Write, FileCreate,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint,
                IntPtr.Zero, 0);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new IOException($"The private control directory could not be created atomically (Windows error {error}).",
                    unchecked((int)(0x80070000u | (uint)error)));
            }
            try
            {
                if (!GetFileInformationByHandle(handle, out var info) ||
                    (info.FileAttributes & FileAttributes.Directory) == 0 || PilotPath.IsUnsafeAttributes(info.FileAttributes) ||
                    !string.Equals(FinalPath(handle), fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The new private control directory identity or final path is not accepted.");
                var identity = new PilotDirectoryIdentity(info.VolumeSerialNumber,
                    ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                parent.VerifyBinding();
                return new PilotDirectoryLease(fullPath, handle, identity, canDelete: true);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            if (parentAdded) parent.Handle.DangerousRelease();
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    internal static PilotDirectoryLease OpenOrCreateDescendant(PilotDirectoryLease root,
        string relativeDirectory)
    {
        root.VerifyBinding();
        var normalized = PilotPath.NormalizeRelative(relativeDirectory);
        PilotDirectoryLease? owned = null;
        var current = root;
        try
        {
            foreach (var segment in normalized.Split('/'))
            {
                var next = OpenOrCreateChild(current, segment);
                owned?.Dispose();
                owned = next;
                current = next;
            }
            root.VerifyBinding();
            var result = owned ?? throw new InvalidOperationException("A descendant directory is required.");
            owned = null;
            return result;
        }
        finally { owned?.Dispose(); }
    }

    internal static PilotDirectoryLease CreateDescendant(PilotDirectoryLease root, string relativeDirectory)
    {
        root.VerifyBinding();
        var normalized = PilotPath.NormalizeRelative(relativeDirectory);
        var separator = normalized.LastIndexOf('/');
        if (separator < 0) return CreateChild(root, normalized, hidden: false);
        using var parent = OpenOrCreateDescendant(root, normalized[..separator]);
        var created = CreateChild(parent, normalized[(separator + 1)..], hidden: false);
        root.VerifyBinding();
        return created;
    }

    internal static PilotDirectoryLease OpenOrCreateChild(PilotDirectoryLease parent, string childName,
        bool hidden = false, bool pin = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Verified descendant directories require Windows NTFS handles.");
        parent.VerifyBinding();
        var safeName = PilotPath.NormalizeTopLevelName(childName);
        var fullPath = System.IO.Path.Combine(parent.Path, safeName);
        var nameBuffer = Marshal.StringToHGlobalUni(safeName);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var parentAdded = false;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(safeName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((safeName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            parent.Handle.DangerousAddRef(ref parentAdded);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.Handle.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = 0,
                SecurityDescriptor = IntPtr.Zero,
                SecurityQualityOfService = IntPtr.Zero
            };
            var status = NtCreateFile(out var handle,
                Synchronize | FileListDirectory | FileReadAttributes,
                ref attributes, out _, IntPtr.Zero, (uint)(hidden ? FileAttributes.Hidden : FileAttributes.Normal),
                FileShare.Read | FileShare.Write | (pin ? 0 : FileShare.Delete), FileOpenIf,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileFlagOpenReparsePoint,
                IntPtr.Zero, 0);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new IOException($"The descendant directory could not be opened or created atomically (Windows error {error}).");
            }
            try
            {
                if (!GetFileInformationByHandle(handle, out var info) ||
                    (info.FileAttributes & FileAttributes.Directory) == 0 ||
                    (pin ? PilotPath.IsUnsafeAttributes(info.FileAttributes) : IsUnsafeHeld(handle, info.FileAttributes)) ||
                    !string.Equals(FinalPath(handle), fullPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A descendant directory is a reparse point or changed identity.");
                var identity = new PilotDirectoryIdentity(info.VolumeSerialNumber,
                    ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                parent.VerifyBinding();
                return new PilotDirectoryLease(fullPath, handle, identity);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            if (parentAdded) parent.Handle.DangerousRelease();
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    internal void DeleteOnClose()
    {
        if (!_canDelete) throw new InvalidOperationException("This directory lease has no delete authority.");
        VerifyBinding();
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(_handle, FileDispositionInfo, buffer, 1))
                throw new IOException("The exact empty private directory could not be marked for deletion.",
                    Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal void RenameTo(PilotDirectoryLease destinationRoot, string destinationRelativePath)
    {
        if (!_canDelete)
            throw new InvalidOperationException("This directory lease has no rename authority.");
        VerifyBinding();
        destinationRoot.VerifyBinding();
        var relative = PilotPath.NormalizeRelative(destinationRelativePath);
        var destination = PilotPath.ResolveContainedPath(destinationRoot.Path, relative);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The directory move destination is occupied.");
        var separator = relative.LastIndexOf('/');
        PilotDirectoryLease? parent = null;
        try
        {
            if (separator >= 0)
                parent = OpenOrCreateDescendant(destinationRoot, relative[..separator]);
            var targetDirectory = parent ?? destinationRoot;
            var safeName = separator >= 0
                ? PilotPath.NormalizeTopLevelName(relative[(separator + 1)..])
                : PilotPath.NormalizeTopLevelName(relative);
            var destinationBytes = Encoding.Unicode.GetBytes(safeName);
            var rootOffset = IntPtr.Size == 8 ? 8 : 4;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(int);
            // FILE_RENAME_INFO ends in WCHAR FileName[1]. Its native structure size therefore
            // includes one WCHAR beyond the variable-name offset (and rounds to pointer
            // alignment on x64). Retain a zero WCHAR after FileName and pass that complete
            // buffer size; otherwise the directory name can acquire an uninitialized trailing
            // code unit on some Windows builds even though FileNameLength is exact.
            var bufferLength = checked(nameOffset + destinationBytes.Length + sizeof(char));
            var buffer = Marshal.AllocHGlobal(bufferLength);
            var destinationHandleAdded = false;
            try
            {
                for (var index = 0; index < bufferLength; index++) Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteByte(buffer, 0, 0); // ReplaceIfExists = false.
                targetDirectory.Handle.DangerousAddRef(ref destinationHandleAdded);
                Marshal.WriteIntPtr(buffer, rootOffset, targetDirectory.Handle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, destinationBytes.Length);
                Marshal.Copy(destinationBytes, 0, buffer + nameOffset, destinationBytes.Length);
                var status = NtSetInformationFile(_handle, out _, buffer, checked((uint)bufferLength),
                    FileRenameInformation);
                if (status < 0)
                {
                    var error = unchecked((int)RtlNtStatusToDosError(status));
                    throw new IOException($"The exact held directory could not be renamed atomically (Windows error {error}).",
                        new System.ComponentModel.Win32Exception(error));
                }
            }
            finally
            {
                if (destinationHandleAdded) targetDirectory.Handle.DangerousRelease();
                Marshal.FreeHGlobal(buffer);
            }
            Path = destination;
            destinationRoot.VerifyBinding();
            parent?.VerifyBinding();
            if (!GetFileInformationByHandle(_handle, out var movedInformation))
                throw new IOException("The renamed directory identity became unreadable.");
            var movedIdentity = new PilotDirectoryIdentity(movedInformation.VolumeSerialNumber,
                ((ulong)movedInformation.FileIndexHigh << 32) | movedInformation.FileIndexLow);
            if (movedIdentity != Identity || IsUnsafeHeld(_handle, movedInformation.FileAttributes))
                throw new IOException("The renamed directory changed identity or became unsafe.");
            var observedPath = FinalPath(_handle);
            if (!string.Equals(observedPath, Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The held directory rename path differs (expected '{System.IO.Path.GetFileName(Path)}', observed '{System.IO.Path.GetFileName(observedPath)}').");
            if (!PilotPath.HasExactRelativePath(destinationRoot.Path, observedPath, relative))
                throw new IOException("The exact held directory did not reach the intended case-sensitive membership.");
        }
        finally { parent?.Dispose(); }
    }

    public void Dispose() => _handle.Dispose();

    private static bool IsUnsafeHeld(SafeFileHandle handle, FileAttributes attributes) =>
        PilotPath.IsUnsafeHandle(handle, attributes);

    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException("The final local path could not be verified.");
        var value = new string(buffer, 0, (int)length);
        if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + value[8..];
        return value.StartsWith("\\\\?\\", StringComparison.Ordinal) ? value[4..] : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    internal static SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile)
        => CreateFileNative(PilotPath.ToExtendedWindowsPath(fileName), desiredAccess, shareMode,
            securityAttributes, creationDisposition, flagsAndAttributes, templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, char[]? volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char[] fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass,
        IntPtr fileInformation, uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess,
        ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize,
        uint fileAttributes, FileShare shareAccess, uint createDisposition, uint createOptions,
        IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}

public sealed record PilotStateFileIdentity(uint VolumeSerialNumber, ulong FileId);

public sealed class PilotStateAuthority : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileFlagWriteThrough = 0x80000000;
    private readonly SafeFileHandle _productRootHandle;
    private readonly SafeFileHandle _stateRootHandle;
    private readonly FileStream _lockStream;
    private readonly PilotStateFileIdentity _productRootIdentity;
    private readonly PilotStateFileIdentity _stateRootIdentity;
    private readonly PilotStateFileIdentity _lockIdentity;
    private readonly Dictionary<string, PilotStateFileIdentity> _stateFileBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _stateStoreGates = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private PilotStateAuthority(
        string productRoot,
        string stateRoot,
        string lockPath,
        SafeFileHandle productRootHandle,
        SafeFileHandle stateRootHandle,
        FileStream lockStream)
    {
        ProductRoot = productRoot;
        StateRoot = stateRoot;
        LockPath = lockPath;
        _productRootHandle = productRootHandle;
        _stateRootHandle = stateRootHandle;
        _lockStream = lockStream;
        _productRootIdentity = IdentityOf(productRootHandle);
        _stateRootIdentity = IdentityOf(stateRootHandle);
        _lockIdentity = IdentityOf(lockStream.SafeFileHandle);
        VerifyAuthority();
    }

    public string ProductRoot { get; }
    public string StateRoot { get; }
    public string LockPath { get; }

    internal static PilotStateAuthority Acquire(string lockPath, bool recordProcessId = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sync Shark state authority requires Windows NTFS identities.");
        var fullLock = Path.GetFullPath(lockPath);
        var stateRoot = Path.GetDirectoryName(fullLock) ?? throw new ArgumentException("The lock path has no state directory.", nameof(lockPath));
        var productRoot = Path.GetDirectoryName(stateRoot) ?? throw new ArgumentException("The state path has no product directory.", nameof(lockPath));
        Directory.CreateDirectory(productRoot);
        // Maintenance remains the existing explicit recovery route. Ordinary GUI
        // and stateful CLI startup must not open a mixed/unfinished release state.
        if (recordProcessId) PilotReleaseRecovery.RequireCurrentBuildReady(
            SyncSharkRuntimePaths.HasTestProductRoot ? productRoot : SyncSharkRuntimePaths.InstallationUserRoot);
        Directory.CreateDirectory(stateRoot);

        SafeFileHandle? productHandle = null;
        SafeFileHandle? stateHandle = null;
        FileStream? lockStream = null;
        try
        {
            productHandle = OpenDirectory(productRoot);
            stateHandle = OpenDirectory(stateRoot);
            var lockHandle = PilotDirectoryLease.CreateFile(fullLock, GenericRead | GenericWrite, FileShare.None, IntPtr.Zero,
                OpenAlways, FileAttributeNormal | PilotDirectoryLease.FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (lockHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                lockHandle.Dispose();
                if (error is 32 or 33)
                    throw new IOException("Sync Shark is already open in this Windows user account.", new System.ComponentModel.Win32Exception(error));
                throw new IOException("The verified Sync Shark state lock could not be opened.", new System.ComponentModel.Win32Exception(error));
            }
            lockStream = new FileStream(lockHandle, FileAccess.ReadWrite, 4096, false);
            VerifyFileHandle(lockStream.SafeFileHandle, fullLock, null);
            var authority = new PilotStateAuthority(productRoot, stateRoot, fullLock, productHandle, stateHandle, lockStream);
            productHandle = null;
            stateHandle = null;
            lockStream = null;
            authority.VerifyAuthority();
            if (recordProcessId)
            {
                authority._lockStream.SetLength(0);
                using (var writer = new StreamWriter(authority._lockStream, leaveOpen: true))
                {
                    writer.Write(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    writer.Flush();
                }
                authority._lockStream.Flush(true);
            }
            authority._lockStream.Position = 0;
            authority.VerifyAuthority();
            return authority;
        }
        catch
        {
            lockStream?.Dispose();
            stateHandle?.Dispose();
            productHandle?.Dispose();
            throw;
        }
    }

    internal bool StateFileExists(string fileName)
    {
        using var stream = OpenStateFile(fileName, create: false, write: false);
        return stream is not null;
    }

    // Instances of a logical state store share the owner authority's transaction gate.
    // Native sharing restrictions remain unchanged: unrelated/external holders still fail closed.
    internal object StateStoreGate(string baseName)
    {
        lock (_stateStoreGates)
        {
            if (!_stateStoreGates.TryGetValue(baseName, out var gate))
                _stateStoreGates.Add(baseName, gate = new object());
            return gate;
        }
    }

    internal FileStream? OpenStateFile(string fileName, bool create, bool write)
    {
        VerifyAuthority();
        var safeName = RequireDirectFileName(fileName);
        var full = Path.Combine(StateRoot, safeName);
        var handle = PilotDirectoryLease.CreateFile(full, write ? GenericRead | GenericWrite : GenericRead,
            write ? FileShare.None : FileShare.Read, IntPtr.Zero, create ? OpenAlways : PilotDirectoryLease.OpenExisting,
            FileAttributeNormal | PilotDirectoryLease.FileFlagOpenReparsePoint | (write ? FileFlagWriteThrough : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (!create && error is 2 or 3) return null;
            throw new IOException($"Sync Shark state file '{safeName}' could not be opened through the verified state root (Windows error {error}).", new System.ComponentModel.Win32Exception(error));
        }
        try
        {
            var identity = VerifyFileHandle(handle, full, null);
            BindStateFile(safeName, identity);
            return new FileStream(handle, write ? FileAccess.ReadWrite : FileAccess.Read, 4096, false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void VerifyStateFile(FileStream stream, string fileName)
    {
        VerifyAuthority();
        var safeName = RequireDirectFileName(fileName);
        PilotStateFileIdentity expected;
        lock (_stateFileBindings)
        {
            if (!_stateFileBindings.TryGetValue(safeName, out expected!)) throw new IOException("The Sync Shark state file has no bound identity.");
        }
        VerifyFileHandle(stream.SafeFileHandle, Path.Combine(StateRoot, safeName), expected);
    }

    internal string PersistRecoveryEvidence(string sourceFileName, string originalSha256, ReadOnlySpan<byte> evidenceBytes)
    {
        VerifyAuthority();
        if (originalSha256.Length != 64 || originalSha256.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidDataException("State recovery evidence hash is invalid.");
        var evidenceName = RequireDirectFileName(sourceFileName) + ".recovery-" + originalSha256.ToLowerInvariant() + ".json";
        var full = Path.Combine(StateRoot, evidenceName);
        var handle = PilotDirectoryLease.CreateFile(full, GenericRead | GenericWrite, FileShare.None, IntPtr.Zero,
            CreateNew, FileAttributeNormal | PilotDirectoryLease.FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (80 or 183))
                throw new IOException("State recovery evidence could not be created.", new System.ComponentModel.Win32Exception(error));
            using var existing = OpenStateFile(evidenceName, create: false, write: true) ?? throw new IOException("State recovery evidence disappeared.");
            VerifyStateFile(existing, evidenceName);
            var observed = new byte[existing.Length <= int.MaxValue ? (int)existing.Length : 0];
            existing.ReadExactly(observed);
            if (!CryptographicOperations.FixedTimeEquals(observed, evidenceBytes))
            {
                VerifyStateFile(existing, evidenceName);
                existing.Position = 0;
                existing.SetLength(0);
                existing.Write(evidenceBytes);
                existing.Flush(true);
                existing.Position = 0;
                observed = new byte[evidenceBytes.Length];
                existing.ReadExactly(observed);
                if (!CryptographicOperations.FixedTimeEquals(observed, evidenceBytes))
                    throw new IOException("State recovery evidence could not converge to the required bytes.");
            }
            VerifyStateFile(existing, evidenceName);
            return evidenceName;
        }
        try
        {
            var identity = VerifyFileHandle(handle, full, null);
            BindStateFile(evidenceName, identity);
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, false);
            VerifyStateFile(stream, evidenceName);
            stream.Write(evidenceBytes);
            stream.Flush(true);
            stream.Position = 0;
            var observed = new byte[evidenceBytes.Length];
            stream.ReadExactly(observed);
            if (!CryptographicOperations.FixedTimeEquals(observed, evidenceBytes)) throw new IOException("State recovery evidence readback differs from the committed bytes.");
            VerifyStateFile(stream, evidenceName);
            return evidenceName;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void VerifyAuthority()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VerifyDirectoryHandle(_productRootHandle, ProductRoot, _productRootIdentity);
        VerifyDirectoryHandle(_stateRootHandle, StateRoot, _stateRootIdentity);
        VerifyFileHandle(_lockStream.SafeFileHandle, LockPath, _lockIdentity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lockStream.Dispose();
        _stateRootHandle.Dispose();
        _productRootHandle.Dispose();
    }

    private void BindStateFile(string fileName, PilotStateFileIdentity observed)
    {
        lock (_stateFileBindings)
        {
            if (_stateFileBindings.TryGetValue(fileName, out var expected) && expected != observed)
                throw new IOException("A Sync Shark state file path now refers to a different NTFS file object.");
            _stateFileBindings[fileName] = observed;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = PilotDirectoryLease.CreateFile(path, PilotDirectoryLease.FileReadAttributes, FileShare.Read | FileShare.Write,
            IntPtr.Zero, PilotDirectoryLease.OpenExisting,
            PilotDirectoryLease.FileFlagBackupSemantics | PilotDirectoryLease.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("A Sync Shark state directory could not be opened without following reparse points.", new System.ComponentModel.Win32Exception(error));
        }
        VerifyDirectoryHandle(handle, Path.GetFullPath(path), null);
        return handle;
    }

    private static PilotStateFileIdentity VerifyDirectoryHandle(SafeFileHandle handle, string expectedPath, PilotStateFileIdentity? expectedIdentity)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
            throw new IOException("A Sync Shark state directory identity could not be read.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if ((info.FileAttributes & FileAttributes.Directory) == 0 || PilotPath.IsUnsafeAttributes(info.FileAttributes))
            throw new IOException("A Sync Shark state directory is not a regular on-disk directory.");
        if (!string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A Sync Shark state directory final path differs from its registered path.");
        var identity = IdentityOf(info);
        if (expectedIdentity is not null && identity != expectedIdentity) throw new IOException("A Sync Shark state directory identity changed.");
        return identity;
    }

    private static PilotStateFileIdentity VerifyFileHandle(SafeFileHandle handle, string expectedPath, PilotStateFileIdentity? expectedIdentity)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
            throw new IOException("A Sync Shark state file identity could not be read.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if ((info.FileAttributes & FileAttributes.Directory) != 0 || PilotPath.IsUnsafeAttributes(info.FileAttributes))
            throw new IOException("A Sync Shark state file is not a regular on-disk file.");
        if (info.NumberOfLinks != 1)
            throw new IOException("A Sync Shark mutable state file must have exactly one NTFS link.");
        if (!string.Equals(PilotDirectoryLease.FinalPath(handle), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A Sync Shark state file final path differs from its registered path.");
        var identity = IdentityOf(info);
        if (expectedIdentity is not null && identity != expectedIdentity) throw new IOException("A Sync Shark state file identity changed.");
        return identity;
    }

    private static PilotStateFileIdentity IdentityOf(SafeFileHandle handle)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var info))
            throw new IOException("A Sync Shark state object identity could not be read.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return IdentityOf(info);
    }

    private static PilotStateFileIdentity IdentityOf(PilotDirectoryLease.ByHandleFileInformation info)
    {
        var identity = new PilotStateFileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        if (identity.VolumeSerialNumber == 0 || identity.FileId == 0)
            throw new IOException("A Sync Shark state object has no stable Windows file identity.");
        return identity;
    }

    private static string RequireDirectFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) ||
            value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A direct state-root file name is required.", nameof(value));
        return value;
    }
}

public sealed class PilotInstanceLock : IDisposable
{
    private readonly PilotStateAuthority _stateAuthority;
    private PilotInstanceLock(PilotStateAuthority stateAuthority) => _stateAuthority = stateAuthority;

    public PilotStateAuthority StateAuthority => _stateAuthority;

    public static PilotInstanceLock Acquire(string path)
    {
        var authority = PilotStateAuthority.Acquire(path);
        try
        {
            if (!SyncSharkRuntimePaths.HasTestProductRoot)
            {
                SyncDataLocation.CheckMoveMarker(authority.ProductRoot);
            }
            return new(authority);
        }
        catch { authority.Dispose(); throw; }
    }

    // Maintenance still owns the same exclusive handle and verified directory/file
    // identities. Its informational PID must not invalidate the installer's exact
    // pre-launch state backup; it does not launch the normal product runtime.
    public static PilotInstanceLock AcquireForMaintenance(string path)
        => new(PilotStateAuthority.Acquire(path, recordProcessId: false));

    public void Dispose() => _stateAuthority.Dispose();
}

public sealed class PilotRunControl
{
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _paused;

    public bool IsPaused => _paused;
    public bool IsStopRequested => _stop.IsCancellationRequested;
    public CancellationToken StopToken => _stop.Token;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    public void Stop() { _paused = false; _stop.Cancel(); }

    public async Task WaitBeforeNextEffectAsync(CancellationToken cancellationToken)
    {
        while (_paused && !IsStopRequested) await Task.Delay(50, cancellationToken);
        _stop.Token.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
