using Microsoft.Win32;
using System.IO;

namespace Massdex.SyncShark;

internal static class WindowsStartupRegistration
{
    internal const string ValueName = "Sync Shark";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows startup registration requires Windows.");
        var executable = ExplorerIntegration.IsInstalledProcess ? ExplorerIntegration.InstalledExecutable : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new InvalidOperationException("The running Sync Shark executable path is unavailable.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new IOException("The current-user Windows startup key could not be opened.");
        var current = key.GetValue(ValueName) as string;
        if (current is not null && !IsOwnedCommand(current))
            throw new InvalidOperationException("The Windows startup entry was changed outside Sync Shark and was left untouched.");
        if (enabled)
            key.SetValue(ValueName, BuildCommand(executable), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static bool IsEnabledForCurrentExecutable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        var executable = ExplorerIntegration.IsInstalledProcess ? ExplorerIntegration.InstalledExecutable : Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(executable) &&
               string.Equals(value, BuildCommand(executable), StringComparison.OrdinalIgnoreCase);
    }

    internal static void RebindEnabledInstalledCommand()
    {
        if (!ExplorerIntegration.IsInstalledProcess) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var current = key?.GetValue(ValueName) as string;
        // Absence stays disabled. An unrelated user-owned value is not ours to change.
        if (current is null || !IsOwnedCommand(current)) return;
        var command = BuildCommand(ExplorerIntegration.InstalledExecutable);
        if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
            key!.SetValue(ValueName, command, RegistryValueKind.String);
    }

    internal static bool IsOwnedCommand(string command)
    {
        if (!command.StartsWith('"') || !command.EndsWith("\" --background", StringComparison.Ordinal)) return false;
        var path = command[1..^14];
        try
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "SyncShark.exe") return false;
            var root = Path.GetFullPath(SyncSharkRuntimePaths.ProductRoot).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(path);
            if (SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.GetDirectoryName(full)!)) return true;
            return string.Equals(full, Path.Combine(root, "active", "SyncShark.exe"), StringComparison.OrdinalIgnoreCase) ||
                   System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Path.GetDirectoryName(full)!), @"^[0-9]+\.[0-9]+\.[0-9]+$") &&
                   full.StartsWith(Path.Combine(root, "versions") + "\\", StringComparison.OrdinalIgnoreCase) &&
                   Path.GetDirectoryName(Path.GetDirectoryName(full))?.Equals(Path.Combine(root, "versions"), StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception error) when (error is ArgumentException or IOException) { return false; }
    }

    internal static string BuildCommand(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("A fully qualified Sync Shark executable path is required.",
                nameof(executable));
        return $"\"{Path.GetFullPath(executable)}\" --background";
    }
}
