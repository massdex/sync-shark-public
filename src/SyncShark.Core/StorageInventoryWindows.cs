using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark;

public sealed record StorageRootRegistration(string Path, string Provider, string Evidence);
public sealed record StorageLocalObservation(string Path, bool Directory, string Availability,
    long? LogicalBytes, long? ResidentBytes, bool MayEnumerate, string Evidence);

// Deliberately separate from sync discovery. Never opens a content stream or falls back to ordinary
// enumeration. Unsupported no-recall enumeration is an incomplete observation, not a retry without flags.
public static class StorageInventoryWindows
{
    public static IReadOnlyList<StorageRootRegistration> RegisteredRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<StorageRootRegistration>();
        using var roots = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) return result;
        foreach (var id in roots?.GetSubKeyNames() ?? [])
        {
            using var root = roots!.OpenSubKey(id); using var paths = root?.OpenSubKey("UserSyncRoots");
            if (paths?.GetValue(sid, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string path ||
                !Path.IsPathFullyQualified(path)) continue;
            result.Add(new(Path.GetFullPath(path).TrimEnd('\\'), id.Contains("dropbox", StringComparison.OrdinalIgnoreCase)
                ? "Dropbox" : id.StartsWith("Massdex.SyncShark!", StringComparison.Ordinal) ? "Sync Shark" : id.Split('!')[0],
                "Windows SyncRootManager registration; may remain after the provider stops"));
        }
        return result;
    }

    public static string ValidateRoot(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local storage inspection requires Windows.");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("Choose an exact local folder, not a network, device or relative path.");
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Length < 4 || string.Equals(full, Path.GetPathRoot(full)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a folder, not an entire drive.");
        return full;
    }

    public static StorageLocalObservation Inspect(string path)
    {
        using var chain = OpenChain(path, list: false);
        return Observe(chain.Last, Path.GetFullPath(path));
    }

    public static IReadOnlyList<string> OnDiskChildren(string path, int limit, CancellationToken cancellationToken, out bool complete)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var chain = OpenChain(path, list: true);
        var observed = Observe(chain.Last, path);
        if (!observed.Directory || !observed.MayEnumerate) throw new IOException("This directory cannot be inspected without following an unsupported reparse point.");
        var names = new List<string>(); complete = false;
        const int capacity = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var restart = true;
            while (names.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntquerydirectoryfileex
                // SL_RETURN_ON_DISK_ENTRIES_ONLY prevents provider just-in-time directory expansion.
                var status = NtQueryDirectoryFileEx(chain.Last, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out var io, buffer, capacity, 2, 8u | (restart ? 1u : 0u), IntPtr.Zero);
                restart = false;
                if (status == unchecked((int)0x80000006)) { complete = true; break; } // STATUS_NO_MORE_FILES
                if (status < 0) throw new IOException($"No-recall directory inspection is unavailable (0x{status:x8}).");
                var bytes = checked((int)io.Information.ToUInt64());
                if (bytes is < 68 or > capacity) throw new IOException("Windows returned incomplete directory metadata.");
                var offset = 0;
                while (offset + 68 <= bytes)
                {
                    var item = IntPtr.Add(buffer, offset);
                    var next = Marshal.ReadInt32(item); var length = Marshal.ReadInt32(item, 60);
                    if (length < 0 || length % 2 != 0 || length > bytes - offset - 68)
                        throw new IOException("Windows returned invalid directory metadata.");
                    var name = Marshal.PtrToStringUni(IntPtr.Add(item, 68), length / 2) ?? "";
                    if (name is not ("." or "..") && name.Length > 0)
                    {
                        if (name.IndexOfAny(['\\', '/', ':', '\0']) >= 0) throw new IOException("Invalid child name in directory metadata.");
                        names.Add(Path.Combine(path, name));
                        if (names.Count == limit) return names;
                    }
                    if (next == 0) break;
                    if (next < 68 || next > bytes - offset) throw new IOException("Invalid directory metadata offset.");
                    offset += next;
                }
            }
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static StorageLocalObservation Observe(SafeFileHandle handle, string path)
    {
        if (!GetTag(handle, 9, out var tag, 8)) throw new IOException("File attributes are unavailable.");
        var directory = (tag.Attributes & 16) != 0; var reparse = (tag.Attributes & 0x400) != 0;
        var state = CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
        var cloud = state != uint.MaxValue && (state & 1) != 0;
        if (reparse && !cloud)
            return new(path, directory, "Unknown / not inspected", null, null, false,
                $"Unrecognised or redirecting reparse tag 0x{tag.Tag:x8}; not followed");
        if (!GetStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()))
            throw new IOException("File size metadata is unavailable.");
        if (!cloud)
            return new(path, directory, directory ? "Folder present; contents not inspected" : "Available offline",
                directory ? null : standard.EndOfFile, directory ? null : Math.Max(0, standard.AllocationSize), true,
                "Ordinary filesystem metadata; no provider ownership established");
        const int capacity = 16 * 1024; var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            if (CfGetPlaceholderInfo(handle, 1, buffer, capacity, out var returned) < 0 || returned < 64 || returned > capacity)
                return new(path, directory, "Cloud placeholder; availability unknown", directory ? null : standard.EndOfFile,
                    null, true, "Windows Cloud Files tag; resident metadata unavailable");
            var resident = Math.Max(0, Marshal.ReadInt64(buffer));
            return new(path, directory, ClassifyCloud(directory, state, standard.EndOfFile, resident),
                directory ? null : standard.EndOfFile, directory ? null : resident, true,
                "Windows Cloud Files metadata; a tag alone does not identify the provider");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string ClassifyCloud(bool directory, uint state, long logical, long resident) => directory
        ? "Cloud folder; contents may be mixed"
        : (state & 0x20) != 0 && resident == 0 && logical > 0 ? "Online only"
        : (state & 0x30) != 0 ? "Partly available / awaiting validation" : "Available offline";

    private sealed class HandleChain : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        internal SafeFileHandle Last => Handles[^1];
        public void Dispose() { for (var index = Handles.Count - 1; index >= 0; index--) Handles[index].Dispose(); }
    }

    private static HandleChain OpenChain(string path, bool list)
    {
        var full = ValidateRoot(path); var volume = Path.GetPathRoot(full)!;
        var components = full[volume.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var chain = new HandleChain(); var current = volume;
        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                current = Path.Combine(current, components[index]);
                // Attributes/list-directory only; OPEN_REPARSE_POINT | OPEN_NO_RECALL | BACKUP_SEMANTICS.
                // No FILE_SHARE_DELETE: retain each ancestor until the exact final read has finished.
                var handle = CreateFile(PilotPath.ToExtendedWindowsPath(current),
                    0x80u | (list && index == components.Length - 1 ? 1u : 0u),
                    FileShare.ReadWrite, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
                if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException($"Metadata open failed (Windows {code})."); }
                chain.Handles.Add(handle);
                if (!GetTag(handle, 9, out var tag, 8)) throw new IOException("Ancestor attributes unavailable.");
                if ((tag.Tag & 0x20000000) != 0 && (list || index < components.Length - 1))
                    throw new IOException("Redirecting links/junctions are not followed.");
                if (index < components.Length - 1 && (tag.Attributes & 0x400) != 0)
                {
                    var state = CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag);
                    if (state == uint.MaxValue || (state & 1) == 0) throw new IOException("An ancestor has an unsupported reparse format.");
                }
            }
            return chain;
        }
        catch { chain.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { internal IntPtr Status; internal UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] private struct TagInfo { internal uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo
    { internal long AllocationSize, EndOfFile; internal uint Links; internal byte DeletePending, Directory; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTag(SafeFileHandle handle, int info, out TagInfo value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandard(SafeFileHandle handle, int info, out StandardInfo value, uint size);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryDirectoryFileEx(SafeFileHandle handle, IntPtr evt, IntPtr apc, IntPtr context, out IoStatus io, IntPtr buffer, int length, int info, uint flags, IntPtr pattern);
    [DllImport("CldApi.dll", ExactSpelling = true)] private static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
    [DllImport("CldApi.dll", ExactSpelling = true)] private static extern int CfGetPlaceholderInfo(SafeFileHandle handle, int info, IntPtr buffer, int length, out int returned);
}
