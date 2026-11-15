using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

public interface IDropboxRefreshTokenStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
    IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.CredentialReference) ||
            binding.CredentialReference == WindowsDropboxRefreshTokenStore.RegisteredTarget) return this;
        throw new InvalidOperationException("This credential store cannot route another Dropbox account.");
    }
}

public sealed class WindowsDropboxRefreshTokenStore : IDropboxRefreshTokenStore
{
    public const string RegisteredTarget = "massdex/account-access/v1/dropbox/sync-shark/production/refresh-token";
    private readonly string _target;
    public WindowsDropboxRefreshTokenStore() => _target = RegisteredTarget;
    private WindowsDropboxRefreshTokenStore(string target) => _target = target;
    public static string AccountTarget(string connectionId)
    {
        const string prefix = "connection-";
        if (!connectionId.StartsWith(prefix, StringComparison.Ordinal) || connectionId.Length != prefix.Length + 24 ||
            connectionId[prefix.Length..].Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("A stable registered Dropbox connection key is required.");
        return "massdex/account-access/v1/dropbox/sync-shark-" + connectionId[prefix.Length..] + "/production/refresh-token";
    }
    public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding)
    {
        var reference = binding.Normalize().CredentialReference;
        if (reference == RegisteredTarget) return new WindowsDropboxRefreshTokenStore();
        if (!string.Equals(binding.ConnectionId, DropboxConnectionBinding.CreateConnectionId(binding.AccountId), StringComparison.Ordinal) ||
            !string.Equals(reference, AccountTarget(binding.ConnectionId), StringComparison.Ordinal))
            throw new InvalidDataException("The credential target does not belong to this Dropbox connection.");
        return new WindowsDropboxRefreshTokenStore(reference);
    }
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        if (!CredRead(_target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return Task.FromResult<string?>(null);
            throw new Win32Exception(error, "Credential Manager read failed.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var bytes = Encoding.UTF8.GetBytes(refreshToken);
        if (bytes.Length > 2560) throw new ArgumentException("Refresh token exceeds the Credential Manager limit.", nameof(refreshToken));
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = _target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = "Dropbox refresh token"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Credential Manager write failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
            Marshal.FreeCoTaskMem(blob);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        if (!CredDelete(_target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error, "Credential Manager delete failed.");
        }
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
