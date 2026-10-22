using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Contract checks for the v2 local inventory and writer; run with <c>--v2-local-checks &lt;root&gt;</c>.</summary>
internal static class LocalChecks
{
    internal const string InstallationId = "localv2install0001";
    internal const string PairId = "pair00001";

    private static string _base = DefaultTestBase();

    private static string DefaultTestBase()
    {
        var configured = Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT");
        var root = string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : configured;
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("SYNCSHARK_TEST_ROOT must be an absolute test directory.");
        return RejectDropbox(Path.GetFullPath(root));
    }

    private static string SafeTestPath(string path) => RejectDropbox(Path.GetFullPath(path, DefaultTestBase()));

    private static string RejectDropbox(string path)
    {
        var dropbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox")
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Equals(dropbox, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(dropbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test fixtures must never be created under the user's Dropbox folder.");
        return path;
    }

    /// <summary>Runs the local inventory, delta and writer checks against fresh temp folders.</summary>
    public static async Task RunAsync(string root, bool realCloudFiles = true)
    {
        _base = SafeTestPath(root);
        Directory.CreateDirectory(_base);
        await FreeSpaceAdmissionChecks.RunAsync().ConfigureAwait(false);
        await LocalInventoryChecks.RunAsync().ConfigureAwait(false);
        await LocalDeltaChecks.RunAsync().ConfigureAwait(false);
        await LocalJournalChecks.RunAsync(Path.Combine(_base, "journal-regressions"), scale: false).ConfigureAwait(false);
        await LocalWriterChecks.RunAsync().ConfigureAwait(false);
        await DirectDownloadChecks.RunAsync().ConfigureAwait(false);
        await RecoveryFirstRestartChecks.RunAsync().ConfigureAwait(false);
        await StubReplacementChecks.RunAsync().ConfigureAwait(false);
        if (realCloudFiles) await LocalCloudFilesChecks.RunAsync().ConfigureAwait(false);
        Console.WriteLine("v2 local checks passed.");
    }

    internal static Task RunConstituentAsync(string root, string constituent)
    {
        _base = SafeTestPath(root);
        Directory.CreateDirectory(_base);
        return constituent switch
        {
            "FreeSpaceAdmission" => FreeSpaceAdmissionChecks.RunAsync(),
            "LocalInventory" => LocalInventoryChecks.RunAsync(),
            "LocalDelta" => LocalDeltaChecks.RunAsync(),
            "LocalWriter" => LocalWriterChecks.RunAsync(),
            "LocalWriterRead" => LocalWriterReadChecks.RunAsync(),
            "DirectDownload" => DirectDownloadChecks.RunAsync(),
            "RecoveryFirstRestart" => RecoveryFirstRestartChecks.RunAsync(),
            "StubReplacement" => StubReplacementChecks.RunAsync(),
            _ => throw new ArgumentException("Unknown local constituent: " + constituent)
        };
    }

    internal static async Task MappedViewCheckAsync(string root)
    {
        _base = Path.GetFullPath(root);
        Directory.CreateDirectory(_base);
        await LocalWriterChecks.WritableMappedViewBlocksExclusiveExecutorAsync();
    }

    /// <summary>A fresh per-check root whose parent holds its private control namespace, so both are cleaned together.</summary>
    internal static string FreshRoot(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "." || Path.IsPathRooted(name) ||
            name.Split('/', '\\').Any(part => part == ".."))
            throw new ArgumentException("A fixture name must stay inside its explicit test base.", nameof(name));
        var parent = RejectDropbox(Path.GetFullPath(name, _base));
        if (Directory.Exists(parent)) Directory.Delete(parent, true);
        var root = Path.Combine(parent, "root");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void Seed(string root, string relative, string text) =>
        Seed(root, relative, Encoding.UTF8.GetBytes(text));

    internal static void Seed(string root, string relative, byte[] bytes)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    internal static string ObjectIdOf(string root)
    {
        using var lease = PilotDirectoryLease.Open(root);
        return SyncLocalEndpointStore.ObjectId(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
    }

    internal static RootDef RootOf(string root, string rootId) =>
        new(rootId, SyncProviders.Local, null, root, ObjectIdOf(root));

    internal static LocalInventory Inventory(string root, IStateStore known, TimeProvider clock,
        IReadOnlySet<string>? productOwned = null, V2Settings? settings = null) =>
        Inventory(root, ObjectIdOf(root), known, clock, productOwned, settings);

    internal static LocalInventory Inventory(string root, string rootObjectId, IStateStore known, TimeProvider clock,
        IReadOnlySet<string>? productOwned = null, V2Settings? settings = null) =>
        new(new RootDef("root-local", SyncProviders.Local, null, root, rootObjectId), Side.A, known,
            productOwned ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), settings ?? V2Settings.Default, clock);

    internal static Task<LocalWriter> OpenWriter(string root, string recoveryRoot, CancellationToken ct) =>
        LocalWriter.OpenAsync(RootOf(root, "root-local"), Side.A, PairId, InstallationId, recoveryRoot, ct);

    internal static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    internal static string BlockHash(byte[] bytes) => DropboxContentHash.Compute(bytes);

    internal static ItemState Item(string path, ItemKind kind) =>
        new(path, kind, "id:local:00000001:0000000000000001", null, null, 0, DateTimeOffset.UnixEpoch, ItemFlags.None);

    internal static string OperationId(string actionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionId))).ToLowerInvariant()[..32];

    internal static void CreateJunction(string link, string target) =>
        RunCmd("mklink", "/J", link, target);

    internal static void CreateHardLink(string link, string target) =>
        RunCmd("mklink", "/H", link, target);

    internal static void RemoveJunction(string link)
    {
        if (Directory.Exists(link)) RunCmd("rmdir", null, link, null);
    }

    private static void RunCmd(string verb, string? flag, string arg1, string? arg2)
    {
        var arguments = "/c " + verb + (flag is null ? "" : " " + flag) + " \"" + arg1 + "\"" +
            (arg2 is null ? "" : " \"" + arg2 + "\"");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new IOException("The junction fixture command could not be started.");
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("The junction fixture command failed: " + error.Trim());
    }

    internal static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    internal static string[] Ordinal(params string[] values) =>
        values.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    /// <summary>A clock that never advances, so a local deletion time is exactly the scan time.</summary>
    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A clock that advances one second on every read, so the order of clock reads is observable.</summary>
    internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private long _reads;

        public override DateTimeOffset GetUtcNow() => start.AddSeconds(Interlocked.Increment(ref _reads));
    }
}
