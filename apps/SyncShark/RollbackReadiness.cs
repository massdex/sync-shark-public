using System.IO;
using Microsoft.Win32;
using System.Security.Principal;

namespace Massdex.SyncShark;

internal static class RollbackReadiness
{
    // Called under the existing single-instance/state lock, before any UI, provider or monitor starts.
    internal static async Task<bool> CheckAsync(PilotStateAuthority authority)
    {
        authority.VerifyAuthority();
        var databasePath = Path.Combine(Directory.GetParent(authority.StateRoot)!.FullName, "vNext", "state", "syncshark.db");
        var retained = File.Exists(databasePath)
            ? await new SyncAuthorityDatabase(databasePath).LoadPairSetAsync(readOnly: true)
            : new SyncPairSet([], null);
        var ids = retained.Pairs.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);
        var pairs = retained.Pairs.Concat(new SyncPairStore(authority).Load().Pairs.Where(pair => !ids.Contains(pair.Id))).ToArray();
        if (pairs.Any(pair => pair.FilesOnDemandEnabled || WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id))) return false;
        foreach (var path in new[] { @"Software\Classes\*\shell\Massdex.SyncShark", @"Software\Classes\Directory\shell\Massdex.SyncShark" })
        {
            using var command = Registry.CurrentUser.OpenSubKey(path);
            if (command is not null) return false;
        }
        // WinRT enumeration can omit a partially written registration; check the exact current-user product prefix too.
        using var roots = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
        var prefix = "Massdex.SyncShark!" + WindowsIdentity.GetCurrent().User!.Value + "!";
        if (roots?.GetSubKeyNames().Any(name => name.StartsWith(prefix, StringComparison.Ordinal)) == true) return false;
        authority.VerifyAuthority();
        return true;
    }
}
