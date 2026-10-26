using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task CaseSensitiveFolderOccupancyAsync(string local, LocalWriter writer)
    {
        var parent = Path.Combine(local, "Sensitive");
        Directory.CreateDirectory(parent);
        using (var handle = PilotDirectoryLease.CreateFile(parent, 0x100,
            FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
        {
            uint flags = 1; // FILE_CS_FLAG_CASE_SENSITIVE_DIR, FILE_CASE_SENSITIVE_INFO.
            Require(!handle.IsInvalid && SetCaseSensitiveDirectory(handle, 23, ref flags, sizeof(uint)),
                $"case-sensitive directory fixture enabled: Win32={Marshal.GetLastWin32Error()}");
        }
        Directory.CreateDirectory(Path.Combine(parent, "Folder"));
        Directory.CreateDirectory(Path.Combine(parent, "folder"));
        await File.WriteAllTextAsync(Path.Combine(parent, "Folder", "source.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(parent, "folder", "target.txt"), "target");
        var source = (await writer.ObserveAsync("Sensitive/Folder", CancellationToken.None))!;
        var target = (await writer.ObserveAsync("Sensitive/folder", CancellationToken.None))!;
        Require(source.ObjectId != target.ObjectId, "case-equivalent fixture folders have distinct NTFS identities");
        var action = new SyncAction("case-occupied", 25, ActionKind.Move, Side.A,
            "Sensitive/folder", "Sensitive/Folder", target, source, false, "case-occupied-key");
        IOException? refusal = null;
        try { await writer.MoveAsync(action, CancellationToken.None); }
        catch (IOException error) { refusal = error; }
        Require(refusal is not null, "distinct case-equivalent folder occupant refuses rename");
        Require((await writer.ObserveAsync("Sensitive/Folder", CancellationToken.None))!.ObjectId == source.ObjectId &&
            (await writer.ObserveAsync("Sensitive/folder", CancellationToken.None))!.ObjectId == target.ObjectId &&
            await File.ReadAllTextAsync(Path.Combine(parent, "Folder", "source.txt")) == "source" &&
            await File.ReadAllTextAsync(Path.Combine(parent, "folder", "target.txt")) == "target",
            "case-equivalent occupied rename preserves both exact identities and both trees");
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCaseSensitiveDirectory(SafeFileHandle handle, int informationClass,
        ref uint information, uint size);
}
