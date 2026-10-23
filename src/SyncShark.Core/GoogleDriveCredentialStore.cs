using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// Fixed registered Windows-user custody only. No arbitrary credential target is accepted.
public sealed class WindowsGoogleDriveRefreshTokenStore : IGoogleDriveRefreshTokenStore
{
    public const string ClientTarget = "massdex/account-access/v1/google/sync-shark/production/oauth-client";

    public static string AccountTarget(string connectionId)
    {
        const string prefix = "gdrive-connection-";
        if (!connectionId.StartsWith(prefix, StringComparison.Ordinal) ||
            connectionId.Length != prefix.Length + 24 ||
            connectionId[prefix.Length..].Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("A bound Google Drive account identity is required.");
        return "massdex/account-access/v1/google/sync-shark-" + connectionId[prefix.Length..] +
               "/production/refresh-token";
    }

    public Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(AccountTarget(connectionId))); }

    public Task WriteAsync(string connectionId, string refreshToken, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(AccountTarget(connectionId), refreshToken); return Task.CompletedTask; }

    public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); RequireWindows();
        if (!CredDelete(AccountTarget(connectionId), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Google credential removal failed.");
        return Task.CompletedTask;
    }

    internal static GoogleDriveOAuthEndpoint OpenRegisteredClient()
    {
        var payload = Read(ClientTarget) ?? throw new InvalidOperationException(
            "The Sync Shark Google client is not configured in Windows Credential Manager.");
        try
        {
            using var document = JsonDocument.Parse(payload, new() { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.TryGetProperty("installed", out var installed)) root = installed;
            var clientId = root.GetProperty("client_id").GetString();
            if (clientId != GoogleDriveProviderConfiguration.RegisteredClientId ||
                !GoogleDriveProviderConfiguration.IsLiveProviderBound)
                throw new InvalidDataException("The protected Google client does not match this Sync Shark build.");
            var secret = root.TryGetProperty("client_secret", out var value) ? value.GetString() : null;
            return new GoogleDriveOAuthEndpoint(clientId!, secret);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("The protected Google client configuration is invalid."); }
    }

    private static string? Read(string target)
    {
        RequireWindows();
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Google credential read failed.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.Type != 1 || credential.TargetName != target || credential.CredentialBlobSize > 2560)
                throw new InvalidDataException("The protected Google credential has an invalid binding or size.");
            var bytes = new byte[credential.CredentialBlobSize];
            try { Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static void Write(string target, string token)
    {
        RequireWindows(); ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length > 2560) { CryptographicOperations.ZeroMemory(bytes); throw new InvalidDataException("Google credential exceeds the Windows store limit."); }
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential { Type = 1, TargetName = target,
                CredentialBlob = blob, CredentialBlobSize = (uint)bytes.Length, Persist = 2,
                UserName = "Google Drive refresh token" };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Google credential write failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    private static void RequireWindows()
    { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required."); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
        public uint AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr value);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential value, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr value);
}
