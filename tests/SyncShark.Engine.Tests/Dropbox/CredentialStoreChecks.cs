using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for CredentialStore: the per-connection target naming, own/legacy target selection,
/// and protected round trips through Windows Credential Manager.</summary>
public static class CredentialStoreChecks
{
    public static Task RunAsync()
    {
        var target = CredentialStore.AccountTarget("connection-" + new string('a', 24));
        Check.Require(target == "massdex/account-access/v1/dropbox/sync-shark-" + new string('a', 24) + "/production/refresh-token",
            "Account target is not the per-connection protected name");
        Check.Require(CredentialStore.AccountTarget("connection-" + new string('a', 24)) !=
                      CredentialStore.AccountTarget("connection-" + new string('b', 24)), "Two connections share a protected target");
        foreach (var invalid in new[] { "connection-" + new string('A', 24), "connection-" + new string('a', 23),
                     "connection-" + new string('a', 25), "nope", "connection-" + new string('g', 24), "" })
            Throws<InvalidDataException>(() => CredentialStore.AccountTarget(invalid));

        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        LegacyFixture.RequireAbsent();
        var first = "connection-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var second = "connection-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var store = new CredentialStore();
        try
        {
            store.Save(first, "refresh-fixture-one");
            Check.Require(store.ReadOwn(first) == "refresh-fixture-one" && store.Read(first) == "refresh-fixture-one",
                "A saved token was not read from its own target");
            store.Save(first, "refresh-fixture-two");
            Check.Require(store.ReadOwn(first) == "refresh-fixture-two", "A re-saved token was not read");

            // The fixed legacy target is distinct from every connection's own target.
            LegacyFixture.Write("legacy-fixture");
            Check.Require(store.ReadLegacy() == "legacy-fixture" && store.ReadOwn(second) is null,
                "The legacy target was not read or leaked into another connection's own target");
            Check.Require(store.Read(second) == "legacy-fixture", "Read did not fall back to the only legacy target");
            store.RemoveLegacy();
            Check.Require(store.ReadLegacy() is null, "RemoveLegacy kept the fixed target");
        }
        finally
        {
            LegacyFixture.Delete();
            store.Remove(first);
            store.Remove(second);
        }
        store.Remove(first);
        Throws<ArgumentException>(() => store.Save(first, " "));
        Throws<ArgumentOutOfRangeException>(() => store.Save(first, new string('x', 3000)));
        Throws<InvalidDataException>(() => store.Read("not-a-connection-id"));
        return Task.CompletedTask;
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (Exception error) when (error is T) { return; }
        throw new InvalidDataException("Expected " + typeof(T).Name);
    }

    /// <summary>Writes the fixed 0.3.95 legacy target for the checks. Refuses to overwrite a credential this fixture
    /// does not own, so a real installation's legacy credential is never clobbered.</summary>
    internal static class LegacyFixture
    {
        private const uint GenericCredential = 1;
        private const uint PersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;
        private const string Marker = "legacy-fixture";

        public static void RequireAbsent()
        {
            var existing = Read();
            if (existing is not null && existing != Marker)
                throw new InvalidDataException("A real legacy credential exists; refusing to overwrite it.");
        }

        public static void Write(string token)
        {
            RequireAbsent();
            var bytes = Encoding.UTF8.GetBytes(token);
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = GenericCredential,
                    TargetName = CredentialStore.RegisteredTarget,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = "Dropbox refresh token"
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
                Marshal.FreeCoTaskMem(blob);
            }
        }

        public static string? Read()
        {
            if (!CredRead(CredentialStore.RegisteredTarget, GenericCredential, 0, out var pointer))
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
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CredFree(pointer); }
        }

        public static void Delete()
        {
            var existing = Read();
            if (existing is not null && existing != Marker) return;
            if (!CredDelete(CredentialStore.RegisteredTarget, GenericCredential, 0) &&
                Marshal.GetLastWin32Error() != ErrorNotFound)
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
}
