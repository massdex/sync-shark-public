using System.Runtime.InteropServices;

namespace Massdex.SyncShark;

public static class SyncSharkRuntimePaths
{
    private static string? _productRootForTest;
    public const string SettingsFileName = "pilot-settings.jsonl";
    public const string ConnectionBindingFileName = "dropbox-connection.jsonl";
    public const string GoogleDriveConnectionFileName = "google-drive-connection.jsonl";
    public const string UiPreferencesFileName = "ui-preferences.jsonl";
    public const string PairSetStateName = "sync-pairs";
    public const string RuntimeStateName = "sync-runtime";
    public const string ProductStateName = "sync-product-state";
    public const string HistoryStateName = "sync-history";
    public const string AutomationStateName = "sync-automation";
    public const string InstanceLockFileName = "instance.lock";
    public const string ReplacementStagingDirectoryName = "replacement-staging";
    public static string InstallationUserRoot => Volatile.Read(ref _productRootForTest) ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");
    public static string ProductRoot => Volatile.Read(ref _productRootForTest) ?? SyncDataLocation.ProductRoot;
    public static string StateRoot => Path.Combine(ProductRoot, "state");
    // Code and mutable per-user state deliberately have different roots.
    public static string InstalledVersionsRoot => Volatile.Read(ref _productRootForTest) is { } testRoot
        ? Path.Combine(testRoot, "versions") : @"C:\Program Files\Massdex\SyncShark\versions";
    internal static bool HasTestProductRoot => Volatile.Read(ref _productRootForTest) is not null;
    public static bool IsInstalledRuntimeDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var version = Path.GetFileName(full);
        return System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$",
                   System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
               string.Equals(full, Path.Combine(InstalledVersionsRoot, version), StringComparison.OrdinalIgnoreCase);
    }
    public static string ReplacementStagingRoot => Path.Combine(ProductRoot, ReplacementStagingDirectoryName);
    public static string SettingsFile => Path.Combine(StateRoot, SettingsFileName);
    public static string ConnectionBindingFile => Path.Combine(StateRoot, ConnectionBindingFileName);
    public static string GoogleDriveConnectionFile => Path.Combine(StateRoot, GoogleDriveConnectionFileName);
    public static string InstanceLockFile => Path.Combine(StateRoot, InstanceLockFileName);

    public static string InstalledExecutablePath => Path.Combine(
        Volatile.Read(ref _productRootForTest) ?? InstallationUserRoot, "active", "SyncShark.exe");

    // One canonical installed-process decision. The GUI Explorer gate and the CLI status
    // surface both read this, so they cannot drift apart. The admission itself is unchanged:
    // the running executable must resolve to the same handle-final path as the active
    // launcher, that launcher must exist, the build must be bound to a frozen revision and
    // the resolved versioned runtime must pass the protected-runtime lease.
    public static bool IsInstalledProcess => IsInstalledProcessFor(Environment.ProcessPath, BuildIdentity.SourceRevision);

    internal static bool IsInstalledProcessFor(string? processPath, string? sourceRevision)
    {
        try
        {
            if (processPath is not { } path || !File.Exists(InstalledExecutablePath) ||
                sourceRevision == "UNBOUND") return false;
            var resolved = ResolveExecutablePath(path);
            if (!string.Equals(resolved, ResolveExecutablePath(InstalledExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
            using var runtime = PilotDirectoryLease.OpenInstalledRuntime(Path.GetDirectoryName(resolved)!);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // The single handle-based resolver for this process family. Windows returns the final
    // local DOS path for the opened handle, which is what installed-runtime admission compares.
    public static string ResolveExecutablePath(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) throw new IOException("Executable path could not be resolved.");
        var resolved = new string(buffer, 0, (int)length);
        return NormalizeHandleFinalPath(resolved);
    }

    // Pure normalization of a handle-resolved final path, shared by the real resolver and its
    // tests so the exact same decision is exercised without opening any file or network path.
    // Windows returns a handle-resolved DOS path with the extended-path prefix; every admission
    // comparison uses the same ordinary absolute local form, keeps the resolved target rather
    // than the caller's junction path, and rejects non-local forms.
    internal static string NormalizeHandleFinalPath(string resolved)
    {
        if (resolved.Length < 7 || !resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !char.IsAsciiLetter(resolved[4]) || resolved[5] != ':' || resolved[6] != '\\')
            throw new IOException("The executable did not resolve to a local drive path.");
        return resolved[4..];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, char[] path, uint length, uint flags);

    internal static IDisposable OverrideProductRootForTest(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The test product root must remain under the operating-system temporary directory.");
        Directory.CreateDirectory(fullRoot);
        if (Interlocked.CompareExchange(ref _productRootForTest, fullRoot, null) is not null)
            throw new InvalidOperationException("The test product root is already overridden in this process.");
        return new ProductRootOverride(fullRoot);
    }

    internal static IDisposable OverrideProductRootForOwnedAllocationTest(string root, string ownedAllocation, string ownershipToken)
    {
        var allocation = Path.GetFullPath(ownedAllocation);
        var fullRoot = Path.GetFullPath(root);
        // One bounded allocation folder on the operating-system temporary volume, one GUID name, one strict descendant root.
        var allocationRoot = Path.Combine(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!, "SyncSharkTest");
        var name = Path.GetFileName(allocation);
        if (!string.Equals(Path.GetDirectoryName(allocation), allocationRoot, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("installer55-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["installer55-".Length..], "N", out _) ||
            !PilotPath.IsPathInside(allocation, fullRoot) || string.Equals(allocation, fullRoot, StringComparison.OrdinalIgnoreCase) || ownershipToken.Length == 0)
            throw new InvalidDataException(SyncDataLocation.MoveText("data-move-allocation-invalid"));
        var chain = new PilotDirectoryLease.MigrationChain(allocation);
        SyncLocalFileAuthority? receipt = null;
        try
        {
            var receiptPath = Path.Combine(allocation, ".installer55-owner");
            receipt = SyncLocalFileAuthority.OpenExistingReadGuard(receiptPath);
            receipt.VerifyMigrationFile(receiptPath);
            using var reader = new StreamReader(receipt.Stream, leaveOpen: true);
            if (reader.ReadToEnd() != ownershipToken) throw new InvalidDataException(SyncDataLocation.MoveText("data-move-allocation-invalid"));
            using var rootChain = new PilotDirectoryLease.MigrationChain(fullRoot);
            if (Interlocked.CompareExchange(ref _productRootForTest, fullRoot, null) is not null)
                throw new InvalidOperationException(SyncDataLocation.MoveText("data-move-allocation-invalid"));
            return new ProductRootOverride(fullRoot, receipt, chain);
        }
        catch { receipt?.Dispose(); chain.Dispose(); throw; }
    }

    private sealed class ProductRootOverride(string expectedRoot, IDisposable? receipt = null, IDisposable? chain = null) : IDisposable
    {
        private string? _expectedRoot = expectedRoot;

        public void Dispose()
        {
            var expected = Interlocked.Exchange(ref _expectedRoot, null);
            if (expected is null) return;
            try
            {
                if (!string.Equals(Interlocked.CompareExchange(ref _productRootForTest, null, expected), expected,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The test product-root override changed unexpectedly.");
            }
            finally { receipt?.Dispose(); chain?.Dispose(); }
        }
    }
}
