using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The protected Dropbox credential for a retained connection could not be read; its pairs pause and keep their files.</summary>
public sealed class DropboxCredentialUnavailableException(string connectionId)
    : Exception(Text.Get(TextKey.AccountMissing, (TextArg.Account, connectionId)))
{
    /// <summary>The retained connection whose credential is missing.</summary>
    public string ConnectionId { get; } = connectionId;
}

/// <summary>Stores each Dropbox connection's refresh token in Windows Credential Manager, keyed by its connection id.
/// <see cref="Save"/> writes the connection's own <c>AccountTarget</c>; <see cref="Read"/> tries that target first, then
/// the fixed target 0.3.95 used for its first connection (the only legacy target that can exist). Callers that open an
/// account must prove the verified account's derived connection id matches the requested one before trusting either.</summary>
public sealed class CredentialStore
{
    /// <summary>The fixed target 0.3.95 used for the first connection's refresh token.</summary>
    public const string RegisteredTarget = "massdex/account-access/v1/dropbox/sync-shark/production/refresh-token";
    private static readonly object MutationGate = new();
    private const string AccountPrefix = "massdex/account-access/v1/dropbox/sync-shark-";
    private const string AccountSuffix = "/production/refresh-token";
    private const string ConnectionPrefix = "connection-";
    private const int ConnectionHashLength = 24;
    private const int MaximumTokenBytes = 2560;
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <summary>The protected target for one connection id: a <c>connection-</c> id with its 24 lowercase hex digits.</summary>
    public static string AccountTarget(string connectionId)
    {
        if (connectionId is null || !connectionId.StartsWith(ConnectionPrefix, StringComparison.Ordinal) ||
            connectionId.Length != ConnectionPrefix.Length + ConnectionHashLength ||
            connectionId[ConnectionPrefix.Length..].Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException(Text.Get(TextKey.AccountMissing, (TextArg.Account, connectionId ?? string.Empty)));
        return AccountPrefix + connectionId[ConnectionPrefix.Length..] + AccountSuffix;
    }

    /// <summary>Writes the connection's refresh token to its own protected target.</summary>
    public void Save(string connectionId, string token)
    {
        var target = AccountTarget(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token, nameof(token));
        var bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length > MaximumTokenBytes) throw new ArgumentOutOfRangeException(nameof(token));
        try
        {
            RequireWindows();
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = GenericCredential,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = "Dropbox refresh token"
                };
                lock (MutationGate)
                    if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
                Marshal.FreeCoTaskMem(blob);
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Reads the connection's own protected target, or the fixed legacy target when only that exists.</summary>
    public string? Read(string connectionId)
    {
        var target = AccountTarget(connectionId);
        return ReadTarget(target) ?? ReadTarget(RegisteredTarget);
    }

    /// <summary>Reads only the connection's own protected target; never the fixed legacy target.</summary>
    public string? ReadOwn(string connectionId) => ReadTarget(AccountTarget(connectionId));

    /// <summary>Reads only the fixed legacy target 0.3.95 used for its first connection.</summary>
    public string? ReadLegacy() => ReadTarget(RegisteredTarget);

    /// <summary>Removes the connection's own protected target; a missing credential is not an error.</summary>
    public void Remove(string connectionId) => DeleteTarget(AccountTarget(connectionId));

    /// <summary>Removes the fixed legacy target; only after its owner has been proven.</summary>
    public void RemoveLegacy() => DeleteTarget(RegisteredTarget);

    internal static (string Value, long Stamp)? ObserveOwn(string connectionId) => ReadEntry(AccountTarget(connectionId));

    // All host store instances share this boundary with Save; no lock is held during account verification.
    internal void RemoveObserved(string connectionId, (string Value, long Stamp) observed)
    {
        lock (MutationGate)
            if (ObserveOwn(connectionId) == observed) Remove(connectionId);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    }

    private static string? ReadTarget(string target) => ReadEntry(target)?.Value;

    private static (string Value, long Stamp)? ReadEntry(string target)
    {
        RequireWindows();
        if (!CredRead(target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var written = credential.LastWritten;
            try { return (Encoding.UTF8.GetString(bytes), ((long)written.dwHighDateTime << 32) | (uint)written.dwLowDateTime); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static void DeleteTarget(string target)
    {
        RequireWindows();
        if (!CredDelete(target, GenericCredential, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
