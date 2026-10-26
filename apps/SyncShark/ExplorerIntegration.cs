using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Massdex.SyncShark;

internal static class ExplorerIntegration
{
    internal sealed record CompetingRoot(string Id, string Path, string Provider, bool HasRestorableShellMetadata);

    internal static IReadOnlyList<CompetingRoot> CompetingRoots(string localRoot)
    {
        var result = new Dictionary<string, CompetingRoot>(StringComparer.Ordinal);
        var path = System.IO.Path.GetFullPath(localRoot).TrimEnd('\\');
        bool Overlaps(string other) => string.Equals(path, other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
            other.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase);
        foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
        {
            if (root.Id.StartsWith(Owner + "!", StringComparison.Ordinal)) continue;
            var location = root.Path?.Path;
            if (location is not null && Overlaps(location)) result[root.Id] = new(root.Id, location,
                root.Id.Contains("dropbox", StringComparison.OrdinalIgnoreCase) ? "Dropbox" : root.Id.Split('!')[0], true);
        }
        // A stale Explorer registration may be absent from WinRT enumeration but still owns a
        // namespace in the shell. Absence from one API is not proof that the path is free.
        using var registry = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
        foreach (var id in registry?.GetSubKeyNames() ?? [])
        {
            if (id.StartsWith(Owner + "!", StringComparison.Ordinal) || result.ContainsKey(id)) continue;
            using var root = registry!.OpenSubKey(id); using var paths = root?.OpenSubKey("UserSyncRoots");
            var value = paths?.GetValue(WindowsIdentity.GetCurrent().User!.Value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (value is not null && System.IO.Path.IsPathFullyQualified(value) && Overlaps(value))
                result[id] = new(id, value, id.Contains("dropbox", StringComparison.OrdinalIgnoreCase) ? "Dropbox" : id.Split('!')[0], false);
        }
        return result.Values.ToArray();
    }
    private const string Owner = "Massdex.SyncShark";
    private static readonly string[] Keys = [@"Software\Classes\*\shell\Massdex.SyncShark", @"Software\Classes\Directory\shell\Massdex.SyncShark"];
    // Both members forward to the one canonical admission in SyncShark.Core, so the Explorer
    // gate and the CLI status surface can never report different installed identities.
    internal static string InstalledExecutable => SyncSharkRuntimePaths.InstalledExecutablePath;

    internal static bool IsInstalledProcess => SyncSharkRuntimePaths.IsInstalledProcess;

    internal static string RootId(SyncPair pair)
    {
        if (string.IsNullOrWhiteSpace(pair.Id) || pair.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("The saved pair ID is not a supported Explorer root identity.");
        var id = Owner + "!" + WindowsIdentity.GetCurrent().User!.Value + "!" + pair.Id;
        if (id.Length > 174) throw new InvalidDataException("The exact Explorer root identity exceeds Windows' supported length.");
        return id;
    }

    internal static void RegisterCommands(IReadOnlyList<SyncPair> pairs)
    {
        if (!IsInstalledProcess) return;
        var roots = pairs.Select(pair => PilotPath.NormalizeLocalRoot(pair.LocalRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) { UnregisterCommands(); return; }
        foreach (var root in roots)
        {
            if (root.Contains('"') || root.Length < 4 || root.StartsWith("\\\\", StringComparison.Ordinal))
                throw new InvalidDataException("Explorer commands require ordinary registered local folders.");
        }
        var appliesTo = string.Join(" OR ", roots.Select(root =>
            "(System.ItemPathDisplay:=\"" + root + "\" OR System.ItemPathDisplay:~<\"" + root.TrimEnd('\\') + "\\\")"));
        var before = CaptureCommands();
        try
        {
        foreach (var name in Keys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(name);
            key.SetValue("SyncSharkOwner", Owner); key.SetValue("MUIVerb", "Sync Shark");
            key.SetValue("Icon", InstalledExecutable + ",0"); key.SetValue("SubCommands", "");
            key.SetValue("MultiSelectModel", "Single"); key.SetValue("AppliesTo", appliesTo);
            foreach (var (verb, label) in new[] { ("open", "Open in Sync Shark"), ("web", "Open online"),
                         ("share", "Share & Manage Links"), ("offline", "Make Available Offline"),
                         ("online-only", "Make Online Only"), ("sync", "Sync This Folder Pair") })
            {
                using var child = key.CreateSubKey("shell\\" + verb);
                child.SetValue("MUIVerb", label); child.SetValue("Icon", InstalledExecutable + ",0");
                child.SetValue("MultiSelectModel", "Single");
                using var command = child.CreateSubKey("command");
                command.SetValue("", "\"" + InstalledExecutable + "\" --explorer " + verb + " \"%1\"");
            }
        }
        ReconcileCommandVisibility();
        }
        catch (Exception original)
        {
            try { RestoreCommands(before); }
            catch (Exception restoration) { throw new AggregateException("Explorer command registration failed; its previous state could not be fully restored.", original, restoration); }
            throw;
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    internal static void ReconcileCommandVisibility()
    {
        if (!IsInstalledProcess) return;
        // Keep the existing owned keys as the native command's bounded root
        // metadata and as a fallback. Suppress only their duplicate visible verb
        // when this exact app's packaged command is registered and verified.
        var before = CaptureCommands();
        if (before.Values.All(value => value is null)) return;
        var nativeMenu = false;
        try
        {
            ExplorerPackage.VerifyRegistered(Path.GetDirectoryName(ExistingWindowActivation.ResolveExecutable(Environment.ProcessPath!))!,
                typeof(App).Assembly.GetName().Version!);
            nativeMenu = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Xml.XmlException)
        { /* Leave the classic fallback visible when the current package is unavailable. */ }
        try
        {
            foreach (var name in Keys)
            {
                if (before[name] is null) continue;
                using var key = Registry.CurrentUser.OpenSubKey(name, writable: true);
                if (key?.GetValue("SyncSharkOwner") as string != Owner)
                    throw new InvalidOperationException("Explorer registration ownership changed during menu reconciliation.");
                if (nativeMenu) key.SetValue("LegacyDisable", "", RegistryValueKind.String);
                else key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
            }
        }
        catch (Exception original)
        {
            try { RestoreCommands(before); }
            catch (Exception restoration) { throw new AggregateException("Explorer menu reconciliation failed; its prior state could not be fully restored.", original, restoration); }
            throw;
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    internal static void UnregisterCommands()
    {
        if (!IsInstalledProcess) return;
        _ = CaptureCommands();
        foreach (var name in Keys)
        {
            using var key = Registry.CurrentUser.OpenSubKey(name);
            if (key?.GetValue("SyncSharkOwner") as string == Owner)
                Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    internal static string CaptureBeforeState(IReadOnlyList<SyncPair> pairs)
    {
        var commands = CaptureCommands();
        var ids = pairs.Select(RootId).ToHashSet(StringComparer.Ordinal);
        var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots().Where(root => ids.Contains(root.Id)).Select(root => new {
            root.Id, Path = root.Path.Path, root.DisplayNameResource, root.IconResource, root.Version,
            root.AllowPinning, root.ShowSiblingsAsGroup, Hydration = (int)root.HydrationPolicy,
            HydrationModifier = (int)root.HydrationPolicyModifier, Population = (int)root.PopulationPolicy,
            InSync = (int)root.InSyncPolicy,
            Properties = root.StorageProviderItemPropertyDefinitions.Select(property => new { property.Id, property.DisplayNameResource }).ToArray()
        }).ToArray();
        return System.Text.Json.JsonSerializer.Serialize(new { CapturedUtc = DateTimeOffset.UtcNow, Commands = commands, Roots = roots });
    }

    private static Dictionary<string, Dictionary<string, string>?> CaptureCommands()
    {
        var commands = new Dictionary<string, Dictionary<string, string>?>(StringComparer.Ordinal);
        foreach (var name in Keys)
        {
            using var key = Registry.CurrentUser.OpenSubKey(name);
            if (key is null) { commands[name] = null; continue; }
            if (key.GetValue("SyncSharkOwner") as string != Owner)
                throw new InvalidOperationException("Explorer registration has an unknown owner; no change was made.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal); Read(key, "", values); commands[name] = values;
        }
        return commands;

        static void Read(RegistryKey key, string prefix, Dictionary<string, string> result)
        {
            var parts = prefix.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var verb = parts.Length >= 2 && parts[0] == "shell" && parts[1] is "open" or "web" or "share" or "sync" or "offline" or "online-only";
            string[] allowedValues = parts.Length == 0 ? ["SyncSharkOwner", "MUIVerb", "Icon", "SubCommands", "MultiSelectModel", "AppliesTo", "LegacyDisable"] :
                parts.Length == 2 && verb ? ["MUIVerb", "Icon", "MultiSelectModel"] :
                parts.Length == 3 && verb && parts[2] == "command" ? [""] : [];
            string[] allowedChildren = parts.Length == 0 ? ["shell"] :
                parts.Length == 1 && parts[0] == "shell" ? ["open", "web", "share", "sync", "offline", "online-only"] :
                parts.Length == 2 && verb ? ["command"] : [];
            foreach (var name in key.GetValueNames())
            {
                if (!allowedValues.Contains(name, StringComparer.Ordinal) || key.GetValueKind(name) != RegistryValueKind.String ||
                    key.GetValue(name) is not string value || value.Length > 32768)
                    throw new InvalidDataException("Unexpected Explorer registration value; no change was made.");
                result[prefix + "@" + name] = value;
            }
            foreach (var name in key.GetSubKeyNames())
            {
                if (!allowedChildren.Contains(name, StringComparer.Ordinal))
                    throw new InvalidDataException("Unexpected Explorer registration structure; no change was made.");
                using var child = key.OpenSubKey(name)!; Read(child, prefix + name + "\\", result);
            }
        }
    }

    private static void RestoreCommands(Dictionary<string, Dictionary<string, string>?> before)
    {
        foreach (var name in Keys)
        {
            using (var current = Registry.CurrentUser.OpenSubKey(name))
            {
                if (current is not null && current.GetValue("SyncSharkOwner") as string != Owner)
                    throw new InvalidOperationException("Explorer registration ownership changed during restoration.");
            }
            Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            if (before[name] is not { } values) continue;
            using var root = Registry.CurrentUser.CreateSubKey(name);
            foreach (var entry in values)
            {
                var split = entry.Key.LastIndexOf('@');
                var child = entry.Key[..split].TrimEnd('\\');
                if (child.Length == 0) root.SetValue(entry.Key[(split + 1)..], entry.Value, RegistryValueKind.String);
                else { using var key = root.CreateSubKey(child); key.SetValue(entry.Key[(split + 1)..], entry.Value, RegistryValueKind.String); }
            }
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    internal static async Task RegisterRootAsync(SyncPair pair, string accountName)
    {
        if (!IsInstalledProcess) return;
        await RegisterRootCoreAsync(pair, accountName);
    }

    private static async Task RegisterRootCoreAsync(SyncPair pair, string accountName)
    {
        if (CompetingRoots(pair.LocalRoot).Count != 0)
            throw new InvalidOperationException("Another provider owns this folder or its parent. Resolve ownership in Folder Pairs before enabling Explorer status.");
        if (!pair.FilesOnDemandEnabled) WindowsCloudFilesProvider.RegisterStatusRoot(pair);
        if (!WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id))
            throw new InvalidOperationException("The managed folder provider is not registered yet. Reconnect it before enabling Explorer status.");
        using var lease = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var folder = await StorageFolder.GetFolderFromPathAsync(pair.LocalRoot);
        var id = RootId(pair);
        var existing = StorageProviderSyncRootManager.GetCurrentSyncRoots().SingleOrDefault(item => item.Id == id);
        if (existing is not null && !string.Equals(existing.Path.Path, pair.LocalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Explorer's root registration no longer matches the saved folder identity.");
        // Context is byte-identical to the existing CFAPI root. Do not convert, adopt or replace a foreign provider.
        var info = existing ?? new StorageProviderSyncRootInfo { Id = id, Path = folder };
        info.Context = CryptographicBuffer.CreateFromByteArray(WindowsCloudFileIdentity.Root(pair.Id).Serialize());
        info.DisplayNameResource = "Sync Shark — " + pair.Name + " (" + accountName + ")";
        info.IconResource = InstalledExecutable + ",0";
        info.Version = typeof(App).Assembly.GetName().Version?.ToString() ?? "";
        info.AllowPinning = pair.FilesOnDemandEnabled; info.ShowSiblingsAsGroup = true;
        // Match the already-owned CFAPI policies exactly; Explorer metadata must not change them.
        info.HydrationPolicy = pair.FilesOnDemandEnabled ? StorageProviderHydrationPolicy.Full : StorageProviderHydrationPolicy.AlwaysFull;
        info.HydrationPolicyModifier = pair.FilesOnDemandEnabled ? StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed : StorageProviderHydrationPolicyModifier.None;
        // WinRT AlwaysFull is 2; the corresponding CFAPI policy is 3. The enums are not interchangeable.
        info.PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull;
        info.InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime;
        info.HardlinkPolicy = StorageProviderHardlinkPolicy.None;
        if (!info.StorageProviderItemPropertyDefinitions.Any(item => item.Id == 1))
            info.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition { Id = 1, DisplayNameResource = "Sync Shark Status" });
        if (!info.StorageProviderItemPropertyDefinitions.Any(item => item.Id == 2))
            info.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition { Id = 2, DisplayNameResource = "Sync Account" });
        lease.VerifyBinding(); StorageProviderSyncRootManager.Register(info); lease.VerifyBinding();
        // The shell invalidates its sync-root cache asynchronously (also accounted for by
        // Microsoft's CloudMirror sample). Bound the readback, never report an unverified root.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            StorageProviderSyncRootInfo? registered = null;
            try { registered = StorageProviderSyncRootManager.GetSyncRootInformationForId(id); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070490)) { }
            if (registered is not null && string.Equals(registered.Path.Path, pair.LocalRoot, StringComparison.OrdinalIgnoreCase) &&
                registered.DisplayNameResource == info.DisplayNameResource && WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id)) break;
            if (deadline.Elapsed >= TimeSpan.FromSeconds(5))
                throw new IOException("Explorer has not confirmed the saved folder registration. " +
                    $"Entry present: {registered is not null}; path matched: {registered?.Path?.Path == pair.LocalRoot}; " +
                    $"name matched: {registered?.DisplayNameResource == info.DisplayNameResource}; " +
                    $"cloud identity matched: {WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id)}. The cloud folder remains managed safely.");
            await Task.Delay(100);
            lease.VerifyBinding();
        }
    }

    internal static async Task<bool> SetItemStatusAsync(SyncPair pair, string relativePath, string status, string account)
    {
        if (!IsInstalledProcess) return false;
        return await SetItemStatusCoreAsync(pair, relativePath, status, account);
    }

    private static async Task<bool> SetItemStatusCoreAsync(SyncPair pair, string relativePath, string status, string account)
    {
        if (!WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id)) return false;
        using var root = PilotDirectoryLease.Open(pair.LocalRoot, new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        var path = relativePath.Length == 0 ? root.Path : System.IO.Path.Combine(root.Path, PilotPath.NormalizeRelative(relativePath).Replace('/', '\\'));
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        MainWindow.VerifyExplorerPath(root.Path, path, pair.Id);
        IStorageItem item;
        if (Directory.Exists(path)) item = await StorageFolder.GetFolderFromPathAsync(path);
        else if (File.Exists(path)) item = await StorageFile.GetFileFromPathAsync(path);
        else return false;
        root.VerifyBinding();
        await StorageProviderItemProperties.SetAsync(item, [new StorageProviderItemProperty { Id = 1, Value = status, IconResource = StatusIconResource(status) },
            new StorageProviderItemProperty { Id = 2, Value = account }]);
        return true;
    }

    internal static string StatusIconResource(string status)
    {
        var id = status switch {
            "Up to Date" => 101,
            "Syncing" or "Checking" => 102,
            "Paused" => 103,
            "Needs Attention" => 104,
            _ => 105
        };
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(InstalledExecutable)!, "SyncShark.Explorer.dll") + ", -" + id;
    }

    internal static void UnregisterRoot(SyncPair pair)
    {
        if (!IsInstalledProcess) return;
        UnregisterRootCore(pair);
    }

    private static void UnregisterRootCore(SyncPair pair)
    {
        var id = RootId(pair);
        var existing = StorageProviderSyncRootManager.GetCurrentSyncRoots().SingleOrDefault(item => item.Id == id);
        // A failed Shell registration can exist in the registry without being returned
        // by WinRT enumeration. Never silently leave that exact owned entry behind.
        var registeredPath = ReadRegisteredRootPath(id);
        if (existing is null && registeredPath is null)
        { if (!pair.FilesOnDemandEnabled) WindowsCloudFilesProvider.UnregisterStatusRoot(pair); return; }
        if ((existing is not null && !string.Equals(existing.Path.Path, pair.LocalRoot, StringComparison.OrdinalIgnoreCase)) ||
            (registeredPath is not null && !string.Equals(registeredPath, pair.LocalRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Explorer root identity changed; registration retained for safety.");
        using var lease = PilotDirectoryLease.Open(pair.LocalRoot,
            new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
        if (!pair.FilesOnDemandEnabled) WindowsCloudFilesProvider.UnregisterStatusRoot(pair);
        // Call only after Core has hydrated/detached every owned placeholder and unregistered CFAPI.
        if (WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot, pair.Id))
            throw new InvalidOperationException("Make the folder fully local before removing its Explorer registration.");
        lease.VerifyBinding(); StorageProviderSyncRootManager.Unregister(id); lease.VerifyBinding();
        if (ReadRegisteredRootPath(id) is not null)
            throw new IOException("Windows retained the Explorer registration; its removal is not confirmed.");
    }

    private static string? ReadRegisteredRootPath(string id)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + id);
        if (root is null) return null;
        using var paths = root.OpenSubKey("UserSyncRoots");
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        if (paths?.GetValue(sid, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string path ||
            string.IsNullOrWhiteSpace(path) || paths.GetValueKind(sid) != RegistryValueKind.String)
            throw new InvalidOperationException("Explorer registration has no verifiable current-user path; it was retained for safety.");
        return path;
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint change, uint flags, IntPtr first, IntPtr second);
}
