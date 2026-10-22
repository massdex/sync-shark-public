using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

// Only the existing elevated RuntimeInstall boundary calls this. The unelevated installer
// supplies bounded DATA, not code or a path to execute. It cannot choose the resize size.
public static class SyncSharkJournalSetup
{
    public static void Install(string transactionDirectory)
    {
        IntPtr medium = IntPtr.Zero;
        try
        {
            medium = SyncSharkDiscoverySetupNative.MediumToken();
            using (var files = new SyncSharkUpdaterFiles(medium))
            {
                string text = files.SmallText(Path.Combine(transactionDirectory, "journal-roots.txt"), 65536, false);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Never interpret UNC/device/environment paths or commands from mutable data.
                    if (root.Length < 3 || root.Length > 32760 || root[1] != ':' || root[2] != '\\' ||
                        char.ToUpperInvariant(root[0]) < 'A' || char.ToUpperInvariant(root[0]) > 'Z') continue;
                    try
                    {
                        var mount = new StringBuilder(32768);
                        if (!GetVolumePathName(root, mount, mount.Capacity)) continue;
                        // Mount-point volumes without a drive letter are deliberately skipped, never resize their host.
                        string volume = mount.ToString();
                        if (volume.Length != 3 || volume[1] != ':' || !seen.Add(volume)) continue;
                        volume = volume.Substring(0, 2).ToUpperInvariant();
                        var current = SyncSharkJournalNative.Query(volume);
                        if (current != null) SyncSharkJournalNative.Grow(volume, current[7], SyncSharkJournalNative.GiB, true);
                    }
                    catch (Exception) { /* Optional optimization; runtime will offer a notice. Never abort installation. */ }
                }
            }
        }
        catch (Exception) { /* No configured roots or unavailable handoff: leave all journals unchanged. */ }
        finally { if (medium != IntPtr.Zero) CloseHandle(medium); }
    }
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
}
