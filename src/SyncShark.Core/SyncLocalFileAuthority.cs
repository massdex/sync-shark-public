using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

internal sealed class SyncLocalFileAuthority : IDisposable, IAsyncDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint ReadControl = 0x00020000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExistingDisposition = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileRenameInformation = 10;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint CryptProtectUiForbidden = 0x1;
    private const int MaximumRecoveryEnvelopeBytes = 128 * 1024;
    internal const string AccessRecoverySuffix = ".syncshark-acl";
    private static readonly byte[] RecoveryPayloadMagic = "SSACL001"u8.ToArray();
    private static readonly byte[] RecoveryEntropy = Encoding.UTF8.GetBytes("Massdex.SyncShark.WritableAcl.v1");
    internal sealed class RecoveryEnvelopeException(string message, Exception? inner = null) : IOException(message, inner);
    internal static Action<string>? InterruptedStageRemovedForTest { get; set; }
    private byte[]? _originalSecurityDescriptor;
    private string? _originalParentPath;
    private FileStream? _accessRecoveryStream;

    private const int FileBufferBytes = 128 * 1024;

    private SyncLocalFileAuthority(SafeFileHandle handle, FileAccess access)
    {
        Handle = handle;
        Stream = new FileStream(handle, access, FileBufferBytes, false);
    }

    public SafeFileHandle Handle { get; }
    public FileStream Stream { get; }
    public string FinalPath => PilotDirectoryLease.FinalPath(Handle);

    public static SyncLocalFileAuthority OpenExisting(string path, FileAccess access = FileAccess.Read)
    {
        var desired = DeleteAccess | GenericRead | (access == FileAccess.ReadWrite ? GenericWrite : 0);
        return Open(path, desired, ShareRead | ShareWrite | ShareDelete, OpenExistingDisposition, access);
    }

    internal static SyncLocalFileAuthority OpenExistingSharedRead(string path) =>
        Open(path, GenericRead, ShareRead | ShareWrite | ShareDelete, OpenExistingDisposition, FileAccess.Read);

    public static SyncLocalFileAuthority OpenExistingExclusive(string path, FileAccess access = FileAccess.Read)
    {
        var desired = DeleteAccess | GenericRead | (access == FileAccess.ReadWrite ? GenericWrite : 0);
        return Open(path, desired, 0, OpenExistingDisposition, access);
    }

    internal static SyncLocalFileAuthority OpenExistingWriteGuard(string path)
    {
        var desired = GenericRead | GenericWrite;
        return Open(path, desired, ShareRead | ShareWrite | ShareDelete, OpenExistingDisposition,
            FileAccess.ReadWrite);
    }

    internal static SyncLocalFileAuthority OpenExistingReadGuard(string path)
    {
        var desired = GenericRead;
        return Open(path, desired, ShareRead, OpenExistingDisposition, FileAccess.Read);
    }

    // holds an online-only cloud-files stub so it can be moved aside and deleted. The handle has
    // DELETE and attribute access only (no data access), opens the reparse object itself with no-recall, and denies
    // write and delete sharing before validation; no data is opened or recalled.
    internal static SyncLocalFileAuthority OpenOnlineOnlyStub(string path)
    {
        const uint noRecall = 0x00100000;
        var handle = CreateFileW(ExtendedPath(path), DeleteAccess | PilotDirectoryLease.FileReadAttributes,
            0, IntPtr.Zero, OpenExistingDisposition,
            FileAttributeNormal | FileFlagOpenReparsePoint | noRecall, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"The online-only placeholder could not be held (Windows error {error}).",
                new Win32Exception(error));
        }
        return new SyncLocalFileAuthority(handle, FileAccess.Read);
    }

    // Pins a mutable database identity, not its contents: SQLite may still write,
    // but replacing/deleting the file while pooled connections exist is refused.
    internal static SyncLocalFileAuthority OpenExistingMutableReadGuard(string path) =>
        Open(path, GenericRead, ShareRead | ShareWrite, OpenExistingDisposition, FileAccess.Read);

    internal static SyncLocalFileAuthority CreateNewUnprotectedFile(string path) =>
        Open(path, DeleteAccess | GenericRead | GenericWrite, 0, CreateNew, FileAccess.ReadWrite);

    public static SyncLocalFileAuthority CreateNewFile(string path)
    {
        var authority = Open(path, DeleteAccess | WriteDac | GenericRead | GenericWrite, 0, CreateNew,
            FileAccess.ReadWrite);
        try
        {
            authority.ProtectWritableObject();
            return authority;
        }
        catch
        {
            try { authority.DeleteOnClose(); }
            catch { }
            authority.Dispose();
            throw;
        }
    }

    public static SyncLocalFileAuthority CreateNewMovableFile(string path)
    {
        var authority = Open(path, DeleteAccess | WriteDac | GenericRead | GenericWrite,
            ShareRead | ShareDelete, CreateNew, FileAccess.ReadWrite);
        try
        {
            authority.ProtectWritableObject();
            return authority;
        }
        catch
        {
            try { authority.DeleteOnClose(); }
            catch { }
            authority.Dispose();
            throw;
        }
    }

    public static PilotDirectoryIdentity? RestoreInterruptedWritableObjectAccess(string protectedObjectPath,
        params string[] candidatePaths) =>
        RestoreInterruptedWritableObjectAccessCore(protectedObjectPath, null, candidatePaths);

    internal static void RemoveInterruptedWritableObject(string path) =>
        RestoreInterruptedWritableObjectAccessCore(path, null, [path], remove: true);

    // Manual command recovery has a separate durable native stage identity. It must agree with
    // the encrypted envelope BEFORE any candidate ACL changes, not after returning a repaired ID.
    internal static PilotDirectoryIdentity? RestoreInterruptedWritableObjectAccess(string protectedObjectPath,
        PilotDirectoryIdentity expectedIdentity, params string[] candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        return RestoreInterruptedWritableObjectAccessCore(protectedObjectPath, expectedIdentity, candidatePaths);
    }

    private static PilotDirectoryIdentity? RestoreInterruptedWritableObjectAccessCore(string protectedObjectPath,
        PilotDirectoryIdentity? expectedIdentity, string[] candidatePaths, bool remove = false)
    {
        var ownerPath = Path.GetFullPath(protectedObjectPath);
        var sidecarPath = RecoverySidecarPath(ownerPath);
        if (!File.Exists(sidecarPath)) return null;

        using var sidecar = OpenRaw(sidecarPath, DeleteAccess | GenericRead | GenericWrite, 0, FileAccess.ReadWrite);
        VerifySafeFileIdentity(sidecar.SafeFileHandle, requireSingleLink: false);
        if (sidecar.Length is <= 0 or > MaximumRecoveryEnvelopeBytes)
            throw new RecoveryEnvelopeException("The writable-object recovery envelope length is not accepted.");
        var protectedPayload = new byte[sidecar.Length];
        sidecar.Position = 0;
        // Opening and reading the file remain ordinary I/O failures, not ownership verdicts.
        sidecar.ReadExactly(protectedPayload);
        AccessRecoveryRecord recovery;
        try { recovery = ParseRecoveryPayload(UnprotectForCurrentUser(protectedPayload)); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException ||
            error is IOException { InnerException: Win32Exception { NativeErrorCode: 13 or 87 } })
        {
            // DPAPI returns ERROR_INVALID_PARAMETER (87) for a truncated blob, or ERROR_INVALID_DATA
            // (13) for invalid authenticated data. Here all other API arguments are fixed by this helper;
            // neither code describes filesystem sharing/access. Other OS failures remain ordinary errors.
            throw new RecoveryEnvelopeException("The writable-object recovery envelope is corrupt or cannot be authenticated.", error);
        }
        if (expectedIdentity is not null && recovery.Identity != expectedIdentity)
            throw new RecoveryEnvelopeException("The interrupted access envelope does not match the original command-bound native identity.");

        SafeFileHandle? matchedHandle = null;
        string? matchedPath = null;
        try
        {
            foreach (var candidate in candidatePaths.Prepend(ownerPath)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var handle = TryOpenForAccessRecovery(candidate);
                if (handle is null) continue;
                var identity = VerifySafeFileIdentity(handle, requireSingleLink: false);
                if (identity != recovery.Identity)
                {
                    handle.Dispose();
                    continue;
                }
                if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information) ||
                    information.NumberOfLinks != 1)
                {
                    handle.Dispose();
                    throw new IOException("The interrupted writable object is no longer a single-link file.");
                }
                matchedHandle = handle;
                matchedPath = candidate;
                break;
            }

            if (matchedHandle is null || matchedPath is null)
            {
                if (remove && !File.Exists(ownerPath) && !Directory.Exists(ownerPath))
                {
                    // A second interruption can follow deletion of the disposable stage but precede
                    // consuming its encrypted envelope. There is no file to delete or ACL to change.
                    MarkDeleteOnClose(sidecar.SafeFileHandle, "The consumed staging envelope could not be removed");
                    return null;
                }
                throw new RecoveryEnvelopeException("The interrupted writable-object identity could not be recovered from its prebound paths.");
            }

            var recoveredDescriptor = DestinationAccessDescriptor(matchedHandle,
                Path.GetDirectoryName(ownerPath)!, recovery.SecurityDescriptor);
            ApplySecurityDescriptor(matchedHandle, recoveredDescriptor,
                "The interrupted writable-object DACL could not be restored");

            using (var verified = OpenExistingExclusive(matchedPath))
            {
                if (verified.VerifySingleLink() != recovery.Identity)
                    throw new IOException("The recovered writable-object identity changed during access restoration.");
                if (remove) verified.DeleteOnClose();
            }

            if (remove)
            {
                // Release the access-repair handle too so the stage is really absent at this boundary.
                matchedHandle.Dispose();
                matchedHandle = null;
                InterruptedStageRemovedForTest?.Invoke(ownerPath);
            }
            MarkDeleteOnClose(sidecar.SafeFileHandle,
                "The consumed writable-object recovery envelope could not be removed");
            return recovery.Identity;
        }
        finally { matchedHandle?.Dispose(); }
    }

    internal static string RecoverySidecarPath(string protectedObjectPath) =>
        Path.GetFullPath(protectedObjectPath) + AccessRecoverySuffix;

    private static SyncLocalFileAuthority Open(string path, uint desiredAccess, uint shareMode, uint creationDisposition, FileAccess access)
    {
        var handle = CreateFileW(ExtendedPath(path), desiredAccess, shareMode, IntPtr.Zero,
            creationDisposition, FileAttributeNormal | FileFlagWriteThrough | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"The exact local file object could not be opened (Windows error {error}).",
                new Win32Exception(error));
        }
        return new SyncLocalFileAuthority(handle, access);
    }

    private static FileStream OpenRaw(string path, uint desiredAccess, uint shareMode, FileAccess access)
    {
        var handle = CreateFileW(ExtendedPath(path), desiredAccess, shareMode, IntPtr.Zero,
            OpenExistingDisposition, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"The writable-object recovery envelope could not be opened (Windows error {error}).",
                new Win32Exception(error));
        }
        return new FileStream(handle, access, 4096, false);
    }

    private static string ExtendedPath(string path)
        // One shared converter; the file helper keeps its exact previous behaviour.
        => PilotPath.ToExtendedWindowsPath(path);

    public PilotDirectoryIdentity VerifySingleLink()
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(Handle, out var information))
            throw new IOException("The exact local file identity could not be read.");
        if (information.NumberOfLinks != 1 || PilotPath.IsUnsafeHandle(Handle, information.FileAttributes))
            throw new IOException("The writable local object is not an exclusive single-link file.");
        return new PilotDirectoryIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    public PilotDirectoryIdentity VerifySameSingleLink(PilotDirectoryIdentity expected)
    {
        var current = VerifySingleLink();
        if (current != expected)
            throw new IOException("The writable local object identity changed during the held interval.");
        return current;
    }

    public Stream CreateGuardedWriteStream(Action<string>? duringFirstWrite = null)
    {
        var identity = VerifySingleLink();
        return new GuardedWriteStream(this, identity, duringFirstWrite);
    }

    public PilotDirectoryIdentity ReadIdentity()
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(Handle, out var information) ||
            PilotPath.IsUnsafeHandle(Handle, information.FileAttributes))
            throw new IOException("The exact local file identity could not be read safely.");
        return new PilotDirectoryIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    public void RenameTo(PilotDirectoryLease destinationDirectory, string destinationName, bool replaceIfExists = false)
    {
        destinationDirectory.VerifyBinding();
        var relative = PilotPath.NormalizeRelative(destinationName);
        var separatorIndex = relative.LastIndexOf('/');
        PilotDirectoryLease? descendant = null;
        try
        {
            var targetDirectory = destinationDirectory;
            var safeName = relative;
            if (separatorIndex >= 0)
            {
                descendant = PilotDirectoryLease.OpenOrCreateDescendant(destinationDirectory,
                    relative[..separatorIndex]);
                targetDirectory = descendant;
                safeName = PilotPath.NormalizeTopLevelName(relative[(separatorIndex + 1)..]);
            }
            var fullDestination = PilotPath.ResolveContainedPath(destinationDirectory.Path, relative);
            var nameBytes = MemoryMarshal.AsBytes(safeName.AsSpan()).ToArray();
            var rootOffset = IntPtr.Size == 8 ? 8 : 4;
            var lengthOffset = rootOffset + IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(uint);
            // Include the aligned native FILE_RENAME_INFORMATION header as
            // well as the variable payload. Field offsets alone under-allocate
            // one-character names (22 bytes instead of a 24-byte x64 header).
            var minimumBufferSize = checked(((nameOffset + sizeof(char) + IntPtr.Size - 1) /
                IntPtr.Size) * IntPtr.Size);
            var bufferSize = checked(minimumBufferSize + nameBytes.Length);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            var destinationHandleAdded = false;
            try
            {
                for (var index = 0; index < bufferSize; index++) Marshal.WriteByte(buffer, index, 0);
                Marshal.WriteByte(buffer, 0, replaceIfExists ? (byte)1 : (byte)0);
                targetDirectory.Handle.DangerousAddRef(ref destinationHandleAdded);
                Marshal.WriteIntPtr(buffer, rootOffset, targetDirectory.Handle.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, nameOffset), nameBytes.Length);
                var status = NtSetInformationFile(Handle, out _, buffer, (uint)bufferSize, FileRenameInformation);
                if (status < 0)
                {
                    var error = unchecked((int)RtlNtStatusToDosError(status));
                    throw new IOException($"The exact local file object could not be renamed (Windows error {error}).",
                        new Win32Exception(error));
                }
            }
            finally
            {
                if (destinationHandleAdded) targetDirectory.Handle.DangerousRelease();
                Marshal.FreeHGlobal(buffer);
            }
            destinationDirectory.VerifyBinding();
            targetDirectory.VerifyBinding();
            if (!PilotPath.HasExactRelativePath(destinationDirectory.Path, FinalPath, relative))
                throw new IOException("The exact local file object did not reach the intended destination.");
        }
        finally { descendant?.Dispose(); }
    }

    public void DeleteOnClose()
    {
        MarkDeleteOnClose(Handle, "The exact local artifact could not be marked for deletion");
    }

    private static void MarkDeleteOnClose(SafeFileHandle handle, string message)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, buffer, 1))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"{message} (Windows error {error}).",
                    new Win32Exception(error));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        try { RestoreWritableObjectAccess(); }
        finally
        {
            _accessRecoveryStream?.Dispose();
            Stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { RestoreWritableObjectAccess(); }
        finally
        {
            if (_accessRecoveryStream is not null) await _accessRecoveryStream.DisposeAsync();
            await Stream.DisposeAsync();
        }
    }

    private void ProtectWritableObject()
    {
        _originalParentPath = Path.GetDirectoryName(FinalPath)!;
        GetKernelObjectSecurity(Handle, DaclSecurityInformation, IntPtr.Zero, 0, out var required);
        var firstError = Marshal.GetLastWin32Error();
        if (required == 0 || firstError != ErrorInsufficientBuffer)
            throw new IOException($"The writable object security descriptor length could not be read (Windows error {firstError}).");
        var original = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetKernelObjectSecurity(Handle, DaclSecurityInformation, original, required, out _))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"The writable object security descriptor could not be read (Windows error {error}).");
            }
            _originalSecurityDescriptor = new byte[required];
            Marshal.Copy(original, _originalSecurityDescriptor, 0, checked((int)required));
        }
        finally { Marshal.FreeHGlobal(original); }

        PersistAccessRecoveryEnvelope(VerifySingleLink(), _originalSecurityDescriptor);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Writable-object DACL isolation requires Windows.");
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentSid = currentIdentity.User?.Value ??
                         throw new IOException("The current Windows identity has no security identifier.");
        // CreateFile opens synchronous handles, so Windows also evaluates SYNCHRONIZE even when the
        // caller requests only WRITE_DAC. No data, attribute, delete, or link-forming access is granted.
        var isolationDescriptor = $"D:P(A;;0x00160000;;;{currentSid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(isolationDescriptor, 1, out var protectedDescriptor, out _))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"The temporary writable-object DACL could not be prepared (Windows error {error}).");
        }
        try
        {
            if (!SetKernelObjectSecurity(Handle, DaclSecurityInformation, protectedDescriptor))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"The writable object could not be isolated for its held interval (Windows error {error}).");
            }
        }
        finally { LocalFree(protectedDescriptor); }
    }

    private void RestoreWritableObjectAccess()
    {
        if (_originalSecurityDescriptor is null) return;
        try
        {
            var descriptor = DestinationAccessDescriptor(Handle, _originalParentPath!, _originalSecurityDescriptor);
            ApplySecurityDescriptor(Handle, descriptor, "The writable object DACL could not be restored");
            _originalSecurityDescriptor = null;
            if (_accessRecoveryStream is not null)
                MarkDeleteOnClose(_accessRecoveryStream.SafeFileHandle,
                    "The writable-object recovery envelope could not be removed");
        }
        finally
        {
            _accessRecoveryStream?.Dispose();
            _accessRecoveryStream = null;
        }
    }

    private void PersistAccessRecoveryEnvelope(PilotDirectoryIdentity identity, byte[] securityDescriptor)
    {
        var sidecarPath = RecoverySidecarPath(FinalPath);
        var handle = CreateFileW(ExtendedPath(sidecarPath), DeleteAccess | GenericRead | GenericWrite, 0,
            IntPtr.Zero, CreateNew, FileAttributeNormal | FileFlagWriteThrough, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"The writable-object recovery envelope could not be created (Windows error {error}).",
                new Win32Exception(error));
        }

        _accessRecoveryStream = new FileStream(handle, FileAccess.ReadWrite, 4096, false);
        try
        {
            VerifySafeFileIdentity(handle, requireSingleLink: true);
            var protectedPayload = ProtectForCurrentUser(BuildRecoveryPayload(identity, securityDescriptor));
            if (protectedPayload.Length > MaximumRecoveryEnvelopeBytes)
                throw new InvalidDataException("The writable-object recovery envelope is too large.");
            _accessRecoveryStream.Write(protectedPayload);
            _accessRecoveryStream.Flush(true);
            VerifySafeFileIdentity(handle, requireSingleLink: true);
        }
        catch
        {
            try { MarkDeleteOnClose(handle, "The incomplete writable-object recovery envelope could not be removed"); }
            catch { }
            _accessRecoveryStream.Dispose();
            _accessRecoveryStream = null;
            throw;
        }
    }

    private static byte[] BuildRecoveryPayload(PilotDirectoryIdentity identity, byte[] securityDescriptor)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(RecoveryPayloadMagic);
            writer.Write(identity.VolumeSerialNumber);
            writer.Write(identity.FileId);
            writer.Write(securityDescriptor.Length);
            writer.Write(securityDescriptor);
        }
        return buffer.ToArray();
    }

    private static AccessRecoveryRecord ParseRecoveryPayload(byte[] payload)
    {
        if (payload.Length > MaximumRecoveryEnvelopeBytes)
            throw new InvalidDataException("The writable-object recovery payload is too large.");
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(RecoveryPayloadMagic.Length);
        if (!magic.SequenceEqual(RecoveryPayloadMagic))
            throw new InvalidDataException("The writable-object recovery payload version is not accepted.");
        var identity = new PilotDirectoryIdentity(reader.ReadUInt32(), reader.ReadUInt64());
        var descriptorLength = reader.ReadInt32();
        if (identity.VolumeSerialNumber == 0 || identity.FileId == 0 || descriptorLength is <= 0 or > 64 * 1024 ||
            descriptorLength != buffer.Length - buffer.Position)
            throw new InvalidDataException("The writable-object recovery payload is malformed.");
        return new AccessRecoveryRecord(identity, reader.ReadBytes(descriptorLength));
    }

    private static byte[] ProtectForCurrentUser(byte[] payload) => TransformProtectedData(payload, protect: true);
    private static byte[] UnprotectForCurrentUser(byte[] payload) => TransformProtectedData(payload, protect: false);

    private static byte[] TransformProtectedData(byte[] payload, bool protect)
    {
        var inputMemory = Marshal.AllocHGlobal(payload.Length);
        var entropyMemory = Marshal.AllocHGlobal(RecoveryEntropy.Length);
        try
        {
            Marshal.Copy(payload, 0, inputMemory, payload.Length);
            Marshal.Copy(RecoveryEntropy, 0, entropyMemory, RecoveryEntropy.Length);
            var input = new DataBlob(payload.Length, inputMemory);
            var entropy = new DataBlob(RecoveryEntropy.Length, entropyMemory);
            DataBlob output;
            IntPtr description = IntPtr.Zero;
            var succeeded = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, out description, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            try
            {
                if (!succeeded)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"The writable-object recovery payload could not be {(protect ? "protected" : "verified")} (Windows error {error}).",
                        new Win32Exception(error));
                }
                if (output.Length is <= 0 or > MaximumRecoveryEnvelopeBytes)
                    throw new InvalidDataException("The protected writable-object recovery payload length is not accepted.");
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputMemory);
            Marshal.FreeHGlobal(entropyMemory);
        }
    }

    private static SafeFileHandle? TryOpenForAccessRecovery(string path)
    {
        var handle = CreateFileW(ExtendedPath(path), WriteDac,
            ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExistingDisposition, FileAttributeNormal, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        throw new IOException($"The prebound interrupted writable-object path '{Path.GetFileName(path)}' could not be opened (Windows error {error}).",
            new Win32Exception(error));
    }

    private static PilotDirectoryIdentity VerifySafeFileIdentity(SafeFileHandle handle, bool requireSingleLink)
    {
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information) ||
            PilotPath.IsUnsafeHandle(handle, information.FileAttributes) ||
            requireSingleLink && information.NumberOfLinks != 1)
            throw new IOException("The writable-object recovery file identity is not safe.");
        return new PilotDirectoryIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static void ApplySecurityDescriptor(SafeFileHandle handle, byte[] securityDescriptor, string message)
    {
        var descriptor = Marshal.AllocHGlobal(securityDescriptor.Length);
        try
        {
            Marshal.Copy(securityDescriptor, 0, descriptor, securityDescriptor.Length);
            if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, descriptor))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"{message} (Windows error {error}).", new Win32Exception(error));
            }
        }
        finally { Marshal.FreeHGlobal(descriptor); }
    }

    // New files are isolated while held, but their saved ACL belongs to the
    // staging directory. Recompute inherited ACEs from the actual destination
    // only after promotion; explicit creator ACEs remain intact. Interrupted
    // recovery uses the same derivation after matching the exact native file ID,
    // so the existing protected sidecar need not change format or be rewritten.
    private static byte[] DestinationAccessDescriptor(SafeFileHandle file, string originalParent,
        byte[] creatorDescriptor)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var parentPath = Path.GetDirectoryName(PilotDirectoryLease.FinalPath(file))!;
        if (string.Equals(parentPath, originalParent, StringComparison.OrdinalIgnoreCase))
            return creatorDescriptor;
        using var parent = PilotDirectoryLease.Open(parentPath);
        using var securityHandle = CreateFileW(ExtendedPath(parentPath), ReadControl | PilotDirectoryLease.FileReadAttributes,
            ShareRead | ShareWrite, IntPtr.Zero, OpenExistingDisposition,
            PilotDirectoryLease.FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (securityHandle.IsInvalid ||
            !PilotDirectoryLease.GetFileInformationByHandle(securityHandle, out var information) ||
            PilotPath.IsUnsafeHandle(securityHandle, information.FileAttributes) ||
            (information.FileAttributes & FileAttributes.Directory) == 0 ||
            new PilotDirectoryIdentity(information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow) != parent.Identity ||
            !string.Equals(PilotDirectoryLease.FinalPath(securityHandle), parent.Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The promoted file's exact destination parent ACL cannot be read safely.");

        GetKernelObjectSecurity(securityHandle, DaclSecurityInformation, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required is 0 or > 64 * 1024 || error != ErrorInsufficientBuffer)
            throw new IOException("The promoted file's destination parent descriptor length is not accepted.");
        var parentDescriptor = Marshal.AllocHGlobal(checked((int)required));
        var creator = Marshal.AllocHGlobal(creatorDescriptor.Length);
        var inherited = IntPtr.Zero;
        try
        {
            if (!GetKernelObjectSecurity(securityHandle, DaclSecurityInformation, parentDescriptor, required, out _))
                throw new IOException("The promoted file's destination parent descriptor could not be read.");
            Marshal.Copy(creatorDescriptor, 0, creator, creatorDescriptor.Length);
            using var identity = WindowsIdentity.GetCurrent();
            var mapping = new FileGenericMapping(0x00120089, 0x00120116, 0x001200a0, 0x001f01ff);
            const uint daclAutoInherit = 0x1;
            if (!CreatePrivateObjectSecurityEx(parentDescriptor, creator, out inherited, IntPtr.Zero,
                    false, daclAutoInherit, identity.AccessToken, ref mapping))
                throw new IOException("The promoted file's inherited destination ACL could not be derived.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            var length = GetSecurityDescriptorLength(inherited);
            if (length is 0 or > 64 * 1024)
                throw new IOException("The promoted file's inherited descriptor length is not accepted.");
            var result = new byte[length];
            Marshal.Copy(inherited, result, 0, checked((int)length));
            parent.VerifyBinding();
            return result;
        }
        finally
        {
            if (inherited != IntPtr.Zero) DestroyPrivateObjectSecurity(ref inherited);
            Marshal.FreeHGlobal(creator);
            Marshal.FreeHGlobal(parentDescriptor);
        }
    }

    private sealed class GuardedWriteStream(
        SyncLocalFileAuthority authority,
        PilotDirectoryIdentity identity,
        Action<string>? duringFirstWrite) : Stream
    {
        private bool _firstWrite = true;

        public override bool CanRead => authority.Stream.CanRead;
        public override bool CanSeek => authority.Stream.CanSeek;
        public override bool CanWrite => authority.Stream.CanWrite;
        public override long Length => authority.Stream.Length;
        public override long Position { get => authority.Stream.Position; set => authority.Stream.Position = value; }

        public override void Flush()
        {
            Verify();
            authority.Stream.Flush();
            Verify();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            Verify();
            await authority.Stream.FlushAsync(cancellationToken);
            Verify();
        }

        public override int Read(byte[] buffer, int offset, int count) => authority.Stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => authority.Stream.Seek(offset, origin);
        public override void SetLength(long value)
        {
            BeforeWrite();
            authority.Stream.SetLength(value);
            Verify();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            BeforeWrite();
            authority.Stream.Write(buffer, offset, count);
            Verify();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteArrayAsync(buffer, offset, count, cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            BeforeWrite();
            await authority.Stream.WriteAsync(buffer, cancellationToken);
            Verify();
        }

        private async Task WriteArrayAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            BeforeWrite();
            await authority.Stream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            Verify();
        }

        private void BeforeWrite()
        {
            Verify();
            if (_firstWrite)
            {
                _firstWrite = false;
                duringFirstWrite?.Invoke(authority.FinalPath);
                Verify();
            }
        }

        private void Verify() => authority.VerifySameSingleLink(identity);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle fileHandle, int fileInformationClass,
        IntPtr fileInformation, uint bufferSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint requestedInformation,
        IntPtr securityDescriptor, uint length, out uint lengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation,
        IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePrivateObjectSecurityEx(IntPtr parentDescriptor, IntPtr creatorDescriptor,
        out IntPtr newDescriptor, IntPtr objectType, [MarshalAs(UnmanagedType.Bool)] bool isContainer,
        uint autoInheritFlags, SafeAccessTokenHandle token, ref FileGenericMapping genericMapping);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPrivateObjectSecurity(ref IntPtr securityDescriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor,
        uint stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, out IntPtr description,
        ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileGenericMapping(uint read, uint write, uint execute, uint all)
    {
        public uint GenericRead = read;
        public uint GenericWrite = write;
        public uint GenericExecute = execute;
        public uint GenericAll = all;
    }

    private sealed record AccessRecoveryRecord(PilotDirectoryIdentity Identity, byte[] SecurityDescriptor);
}
