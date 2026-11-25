using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Deny-ACE and backup-privilege fixtures for native metadata-open checks.</summary>
internal static class AclFixture
{
    /// <summary>SYNCHRONIZE | FILE_READ_ATTRIBUTES: the access the per-file metadata read requests.</summary>
    internal const uint MetadataAccess = 0x00100000 | 0x80;

    // FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_OPEN_NO_RECALL; backup intent adds FILE_FLAG_BACKUP_SEMANTICS.
    private const uint OpenFlags = 0x00200000 | 0x00100000, BackupFlag = 0x02000000;

    /// <summary>Prepend a World deny ACE for <paramref name="mask"/>; return the original descriptor to restore.</summary>
    internal static byte[] Deny(string path, uint mask)
    {
        var info = new FileInfo(path);
        const AccessControlSections sections = AccessControlSections.Access;
        var security = info.GetAccessControl(sections);
        var original = security.GetSecurityDescriptorBinaryForm();
        var sddl = security.GetSecurityDescriptorSddlForm(sections);
        var ace = "(D;;0x" + mask.ToString("x8", CultureInfo.InvariantCulture) + ";;;WD)";
        var at = sddl.IndexOf('(');
        security.SetSecurityDescriptorSddlForm(at < 0 ? sddl + ace : sddl[..at] + ace + sddl[at..], sections);
        info.SetAccessControl(security);
        return original;
    }

    internal static void Restore(string path, byte[] original)
    {
        var info = new FileInfo(path);
        const AccessControlSections sections = AccessControlSections.Access;
        var security = info.GetAccessControl(sections);
        security.SetSecurityDescriptorBinaryForm(original, sections);
        info.SetAccessControl(security);
    }

    private static SafeFileHandle Open(string path, bool backup) =>
        CreateFileW(path, MetadataAccess, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, OpenFlags | (backup ? BackupFlag : 0u), IntPtr.Zero);

    /// <summary>True when the metadata open is refused with ERROR_ACCESS_DENIED.</summary>
    internal static bool Denied(string path, bool backup)
    {
        using var handle = Open(path, backup);
        return handle.IsInvalid && Marshal.GetLastWin32Error() == 5;
    }

    /// <summary>True when the metadata open succeeds, which proves the deny ACE was removed.</summary>
    internal static bool Opens(string path, bool backup)
    {
        using var handle = Open(path, backup);
        return !handle.IsInvalid;
    }

    /// <summary>Whether <paramref name="privilege"/> is present and enabled in the current process token.</summary>
    internal static bool PrivilegeEnabled(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
        try
        {
            GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out var size);
            if (size <= 0) return false;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, size, out _)) return false;
                if (!LookupPrivilegeValue(null, privilege, out var wanted)) return false;
                var count = Marshal.ReadInt32(buffer);
                var stride = Marshal.SizeOf<LuidAndAttributes>();
                for (var i = 0; i < count; i++)
                {
                    var at = IntPtr.Add(buffer, Marshal.SizeOf<int>() + i * stride);
                    if ((uint)Marshal.ReadInt32(at, 0) != wanted.LowPart || Marshal.ReadInt32(at, 4) != wanted.HighPart) continue;
                    return ((uint)Marshal.ReadInt32(at, 8) & SePrivilegeEnabled) != 0;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Enable or disable <paramref name="privilege"/> on the current process token.</summary>
    internal static bool SetPrivilege(string privilege, bool enable)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjust | TokenQuery, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid)) return false;
            var state = new TokenPrivileges { PrivilegeCount = 1, Privileges = new LuidAndAttributes { Luid = luid, Attributes = enable ? SePrivilegeEnabled : 0 } };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    private const int TokenPrivilegesClass = 3;
    private const uint TokenQuery = 0x8, TokenAdjust = 0x20, SePrivilegeEnabled = 0x2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes { public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint PrivilegeCount; public LuidAndAttributes Privileges; }
}
