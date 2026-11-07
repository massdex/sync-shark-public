using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class ProtectedRuntimeChecks
{
    private const string Version = "0.3.31";
    private static int checks;
    private sealed class Interrupted : Exception { }
    private static void Check(bool value, string label)
    { if (!value) throw new IOException(label); checks++; }
    private static void Reject(Action action, string label)
    { try { action(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Check(true, label); return; } throw new IOException("Expected rejection: " + label); }
    private static string Allocation => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TestData", "Projects", "sync-shark", "build", "home-activity-tray", "native-release-checks");
    private static string Member(string root, string name) => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
    private static void Seed(string root)
    {
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(Path.Combine(source, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        foreach (var name in SyncSharkProtectedRuntimeTransaction.Members) File.WriteAllText(Member(source, name), "fixture:" + name);
    }
    private static SyncSharkProtectedRuntimeTransaction Transaction(string root, Action<string>? observe = null)
    {
        var source = Path.Combine(root, "source");
        return new(Path.Combine(root, "versions"), Version,
            SyncSharkProtectedRuntimeTransaction.Members.Select(name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Member(source, name)))).ToLowerInvariant()).ToArray(),
            SyncSharkProtectedRuntimeTransaction.Members.Select(name => new FileInfo(Member(source, name)).Length).ToArray(), observe!);
    }
    private static void RemoveFixture(string root)
    {
        if (!Path.GetFullPath(root).StartsWith(Allocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture cleanup escaped its allocation.");
        if (Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Preserve an unexpected redirected fixture.");
        Directory.Delete(root, recursive: true);
    }
    internal static int Child(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        if (!root.StartsWith(Allocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 87;
        var tx = Transaction(root, step => { if (step == args[2]) Environment.Exit(71); });
        tx.Install(Path.Combine(root, "source")); return 0;
    }
    internal static int Run()
    {
        var root = Path.Combine(Allocation, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Check(SyncSharkRuntimePaths.ProductRoot.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase), "Mutable state stays per-user");
            Check(SyncSharkRuntimePaths.InstalledVersionsRoot == @"C:\Program Files\Massdex\SyncShark\versions", "Production code allocation is exact");
            Check(SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(SyncSharkRuntimePaths.InstalledVersionsRoot, Version)), "Exact protected release path admitted");
            Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(SyncSharkRuntimePaths.ProductRoot, "versions", Version)), "Writable runtime path rejected");
            Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(SyncSharkRuntimePaths.InstalledVersionsRoot, Version, "nested")), "Nested code-root substitution rejected");
            Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(SyncSharkRuntimePaths.InstalledVersionsRoot, "active")), "Mutable launch alias is never an external code root");
            var baseline = Path.Combine(root, "baseline"); Seed(baseline);
            var events = new List<string>(); var normal = Transaction(baseline, events.Add); normal.Install(Path.Combine(baseline, "source"));
            var target = Path.Combine(baseline, "versions", Version);
            normal.Verify(target); Check(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 16, "All code and logo members copied by production transaction");
            normal.Install(Path.Combine(baseline, "source")); Check(!Directory.Exists(target + ".installing"), "Exact completed install replay is idempotent");
            normal.Retire(); normal.Retire(); Check(!Directory.Exists(target) && Directory.Exists(target + ".uninstalled"), "Uninstall retains one exact undo tree");
            normal.Install(Path.Combine(baseline, "source")); normal.Verify(target); Check(!Directory.Exists(target + ".uninstalled"), "Undo restores exact runtime");
            foreach (var at in events.Distinct().Where(value => value is not "runtime-retired" and not "runtime-restored").ToArray())
            {
                var sample = Path.Combine(root, Guid.NewGuid().ToString("N")); Seed(sample);
                try
                {
                    var tx = Transaction(sample, step => { if (step == at) throw new Interrupted(); });
                    try { tx.Install(Path.Combine(sample, "source")); } catch (Interrupted) { }
                    Transaction(sample).Install(Path.Combine(sample, "source"));
                    Transaction(sample).Verify(Path.Combine(sample, "versions", Version)); Check(true, "Interrupted install resumes at " + at);
                }
                finally { RemoveFixture(sample); }
            }
            foreach (var at in new[] { "runtime-stage-created", "runtime-copied-7", "runtime-published" })
            {
                var sample = Path.Combine(root, Guid.NewGuid().ToString("N")); Seed(sample);
                try
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    foreach (var value in new[] { "--protected-runtime-child", sample, at }) start.ArgumentList.Add(value);
                    using var child = Process.Start(start) ?? throw new IOException("No fixture child.");
                    var error = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(30000)) { child.Kill(); child.WaitForExit(5000); throw new IOException("Fixture child timeout."); }
                    Check(child.ExitCode == 71, "Real process exit reached " + at + ": " + error.GetAwaiter().GetResult());
                    Transaction(sample).Install(Path.Combine(sample, "source")); Transaction(sample).Verify(Path.Combine(sample, "versions", Version));
                    Check(true, "Durable replay after actual child termination at " + at);
                }
                finally { RemoveFixture(sample); }
            }
            File.WriteAllText(Path.Combine(target, "unrelated.txt"), "keep");
            Reject(() => normal.Retire(), "Unknown member blocks uninstall without removing it");
            Check(File.ReadAllText(Path.Combine(target, "unrelated.txt")) == "keep", "Unknown content preserved"); File.Delete(Path.Combine(target, "unrelated.txt"));
            File.AppendAllText(Path.Combine(target, "SyncShark.dll"), "altered");
            Reject(() => normal.Retire(), "Changed runtime blocks retirement");
            Check(Directory.Exists(target), "Rejected retirement keeps the live target");
            Reject(() => normal.Verify(Path.Combine(root, "unrelated")), "Arbitrary verification/removal destination rejected");
            Console.WriteLine($"PASS {checks} protected-runtime policy and actual filesystem recovery checks. Isolated fixtures only; no protected placement, ACL acceptance, signing, registration or elevation claimed.");
            return 0;
        }
        finally { RemoveFixture(root); }
    }
}
