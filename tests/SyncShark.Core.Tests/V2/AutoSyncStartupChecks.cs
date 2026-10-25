using Microsoft.Win32;

namespace Massdex.SyncShark.V2;

/// <summary>Exercises the production registration edge only under an isolated test registry key.</summary>
internal static class AutoSyncStartupChecks
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = AutoSyncProcess.StartupKeyPathForTest;
        var path = @"Software\SyncShark.Tests\Startup-" + Guid.NewGuid().ToString("N");
        AutoSyncProcess.StartupKeyPathForTest = path;
        var root = Path.Combine(Path.GetTempPath(), "startup-" + Guid.NewGuid().ToString("N"));
        var oldExecutable = Path.Combine(root, "previous", "SyncShark.exe");
        var executable = Path.Combine(root, "candidate", "SyncShark.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(Environment.ProcessPath!, oldExecutable);
        File.Copy(Environment.ProcessPath!, executable);
        var messages = new List<string>();
        try
        {
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(!AutoSyncProcess.ReadStartup(), "disabled remains disabled on launch");
            using (var missing = Registry.CurrentUser.OpenSubKey(path))
                Require(missing is null, "read and launch never create a missing key");
            AutoSyncProcess.RegisterStartup(executable, false);
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(AutoSyncProcess.LegacyValueName, $"\"{oldExecutable}\" {AutoSyncProcess.LegacyArguments}");
            AutoSyncProcess.RegisterStartup(oldExecutable, true);
            Require(AutoSyncProcess.ReadStartup() &&
                (string?)key.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(oldExecutable),
                "enabling registers the supervisor, not the legacy GUI");
            Require(key.GetValue(AutoSyncProcess.LegacyValueName) is null, "successful enable retires the legacy value");
            key.SetValue(AutoSyncProcess.LegacyValueName, $"\"{oldExecutable}\" {AutoSyncProcess.LegacyArguments}");
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(key.GetValue(AutoSyncProcess.LegacyValueName) is null, "an existing working supervisor can retire a leftover owned legacy launcher");
            Require((string?)key.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(oldExecutable) && messages.Count == 0,
                "launching a candidate or worktree build preserves an existing working registration");
            File.Delete(oldExecutable);
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require((string?)key.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(
                AutoSyncProcess.SelectStartupExecutable(executable)), "only a missing target is repaired using the shared selection");
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(messages.Count == 1, "unchanged launch does not repeat the repair diagnostic");
            AutoSyncProcess.RegisterStartup(executable, false);
            Require(!AutoSyncProcess.ReadStartup() && key.GetValue(AutoSyncProcess.ValueName) is null,
                "disabling removes the registration");
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(!AutoSyncProcess.ReadStartup(), "a later launch cannot re-enable startup");
            foreach (var foreign in new object[] { $"\"{oldExecutable}\" {AutoSyncProcess.LegacyArguments}",
                AutoSyncProcess.StartupCommand(oldExecutable) + " --other", "other.exe", 7 })
            {
                key.SetValue(AutoSyncProcess.ValueName, foreign);
                AutoSyncProcess.RepairStartup(executable, messages.Add);
                Require(!AutoSyncProcess.ReadStartup(), "foreign command is not reported enabled");
                var refused = false;
                try { AutoSyncProcess.RegisterStartup(executable, true); }
                catch (InvalidOperationException) { refused = true; }
                Require(refused && Equals(key.GetValue(AutoSyncProcess.ValueName), foreign), "enable preserves foreign values");
                key.SetValue(AutoSyncProcess.LegacyValueName, $"\"{oldExecutable}\" {AutoSyncProcess.LegacyArguments}");
                var diagnostic = AutoSyncProcess.RegisterStartup(executable, false);
                Require(Equals(key.GetValue(AutoSyncProcess.ValueName), foreign) &&
                    key.GetValue(AutoSyncProcess.LegacyValueName) is null && diagnostic == EdgeMessages.Text("startup-foreign"),
                    "disable preserves foreign values without throwing, reports not ours and retires owned legacy startup");
            }
            key.DeleteValue(AutoSyncProcess.ValueName);
            CheckMigrationOrder(key, oldExecutable, executable, messages);
            File.Copy(executable, oldExecutable);
            CheckMigrationOrder(key, oldExecutable, executable, messages);
            CheckStoredStartup(root, executable);
            CheckVersionWarning(executable);
            var rejected = false;
            try { AutoSyncProcess.RegisterStartup(Path.Combine(root, "Renamed.exe"), true); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && !AutoSyncProcess.ReadStartup(), "writer rejects names the ownership predicate would reject");
            Require(AutoSyncProcess.IsProductStartupCommand(AutoSyncProcess.StartupCommand(executable)),
                "every accepted executable name is recognized as owned");
            var json = System.Text.Json.JsonSerializer.Serialize(new SyncAutomationSettings(false, 5, false, true));
            Require(!json.Contains("StartWithWindows", StringComparison.OrdinalIgnoreCase), "the unread startup flag is not persisted");
            key.SetValue(AutoSyncProcess.LegacyValueName, "foreign.exe --background");
            AutoSyncProcess.RegisterStartup(executable, true);
            Require((string?)key.GetValue(AutoSyncProcess.LegacyValueName) == "foreign.exe --background",
                "migration preserves a foreign legacy-name value");
            Console.WriteLine("PASS startup working-build preservation, missing-target repair, foreign disable, migration ordering, old stored settings and detailed version warnings");
        }
        finally
        {
            AutoSyncProcess.BeforeStartupWriteForTest = null;
            AutoSyncProcess.StartupKeyPathForTest = previous;
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckMigrationOrder(RegistryKey key, string previous, string executable, List<string> messages)
    {
        var legacy = $"\"{previous}\" {AutoSyncProcess.LegacyArguments}";
        key.SetValue(AutoSyncProcess.LegacyValueName, legacy);
        AutoSyncProcess.BeforeStartupWriteForTest = () => throw new IOException("Injected registry write failure");
        try
        {
            var refused = false;
            try { AutoSyncProcess.RegisterStartup(executable, true); }
            catch (IOException) { refused = true; }
            Require(refused && (string?)key.GetValue(AutoSyncProcess.LegacyValueName) == legacy && !AutoSyncProcess.ReadStartup(),
                "failed explicit registration preserves the legacy launcher");
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require((string?)key.GetValue(AutoSyncProcess.LegacyValueName) == legacy && !AutoSyncProcess.ReadStartup() &&
                messages.Last().Contains("Retry with autosync install-startup", StringComparison.Ordinal),
                "failed launch migration preserves the launcher and reports an actionable retry");
        }
        finally { AutoSyncProcess.BeforeStartupWriteForTest = null; }
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(AutoSyncProcess.ReadStartup() && key.GetValue(AutoSyncProcess.LegacyValueName) is null &&
            (string?)key.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(
                File.Exists(previous) ? previous : AutoSyncProcess.SelectStartupExecutable(executable)),
            "successful migration preserves a working legacy target and retires it only after the supervisor write");
        AutoSyncProcess.RegisterStartup(executable, false);
    }

    private static void CheckStoredStartup(string root, string executable)
    {
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        // Write the old on-disk shape through the same envelope/checksum owner as production.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            "{\"Enabled\":true,\"IntervalMinutes\":7,\"RunOnStartup\":false,\"StartWithWindows\":true}");
        new DualSlotJsonStore<System.Text.Json.JsonElement>(instance.StateAuthority, SyncSharkRuntimePaths.AutomationStateName)
            .SaveAsync(legacy).GetAwaiter().GetResult();
        var store = new SyncAutomationStore(instance.StateAuthority);
        var loaded = store.Load();
        Require(loaded.Enabled && loaded.IntervalMinutes == 7 && !loaded.StartWithWindows && !AutoSyncProcess.ReadStartup(),
            "old stored startup true loads but cannot enable absent registry startup");
        AutoSyncProcess.RegisterStartup(executable, true);
        Require(!store.Load().StartWithWindows && AutoSyncProcess.ReadStartup(),
            "registry remains the startup authority independently of the old stored flag");
        AutoSyncProcess.RegisterStartup(executable, false);
    }

    private static void CheckVersionWarning(string executable)
    {
        Require(SyncSharkRuntimePaths.HasTestProductRoot, "version warning uses the CLI suite isolated product root");
        var installed = SyncSharkRuntimePaths.InstalledExecutablePath;
        Require(!File.Exists(installed), "version fixture does not replace an existing executable");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "fixture without version metadata");
        try
        {
            var messages = new List<string>();
            AutoSyncProcess.SelectStartupExecutable(executable, warning: messages.Add);
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            var installedVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(installed).ProductVersion;
            Require(!string.Equals(version, installedVersion, StringComparison.Ordinal) &&
                messages.Contains(EdgeMessages.Text("startup-versions-differ", $"{executable} ({version}) / {installed} ({installedVersion})")),
                "version warning includes both executable paths and versions");
        }
        finally { File.Delete(installed); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
