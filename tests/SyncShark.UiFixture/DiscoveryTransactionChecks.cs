using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SyncShark.UiFixture;

internal static class DiscoveryTransactionChecks
{
    private const string User = "S-1-5-21-123-456-789-1001";
    private static readonly string[] Names = ["SyncSharkDiscovery.exe", "discovery-manifest.json", "SyncSharkDiscovery.exe.previous", "discovery-manifest.json.previous"];
    private sealed class Interrupted : Exception { }
    private static int checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new IOException(name); checks++; }
    private static string[] Snapshot(string root) => Names.Select(name => File.Exists(Path.Combine(root, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))) : "absent").ToArray();
    private static void Seed(string root)
    {
        Directory.CreateDirectory(root);
        for (var i = 0; i < Names.Length; i++) File.WriteAllText(Path.Combine(root, Names[i]), "original-" + i);
    }
    private static void Execute(string root, string mode, Action<string>? observer)
    {
        using var tx = new SyncSharkDiscoveryTransaction(root, User, observer!);
        tx.Recover();
        var candidate = Path.Combine(Path.GetDirectoryName(root)!, "candidate.exe");
        tx.Prepare(mode, candidate, Encoding.UTF8.GetBytes("candidate manifest"));
        tx.Apply(); tx.Complete();
    }
    private static void Recover(string root)
    {
        using var tx = new SyncSharkDiscoveryTransaction(root, User, null!);
        tx.Recover();
        Check(!tx.HasPending && !File.Exists(Path.Combine(root, "discovery-setup.json")) &&
            !Directory.EnumerateDirectories(root).Any(), "Recovery closes only its claimed stage and durable journal");
    }
    internal static int Child(string[] args)
    {
        var allocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestData", "Projects", "sync-shark", "build") + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(args[1]);
        if (!root.StartsWith(allocation, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)) return 87;
        if (args[2] == "claim")
        {
            try { using var tx = new SyncSharkDiscoveryTransaction(root, User, null!); return 1; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { return 73; }
        }
        Execute(root, args[2], step => { if (step == args[3]) Environment.Exit(71); });
        return 0;
    }
    private static int RunChild(string root, string mode, string step)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--discovery-transaction-child", root, mode, step }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Fixture child did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(); process.WaitForExit(5000);
            throw new IOException("Fixture child exceeded its bound at " + step + ": " + (errors.IsCompletedSuccessfully ? errors.Result : "No completed error output"));
        }
        if (process.ExitCode is not (0 or 71 or 73))
            throw new IOException("Fixture child failed at " + step + ": " + errors.GetAwaiter().GetResult());
        return process.ExitCode;
    }
    internal static int Run()
    {
        var allocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestData", "Projects", "sync-shark", "build", "home-activity-tray");
        var suite = Path.Combine(allocation, "discovery-transaction-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suite);
        File.WriteAllText(Path.Combine(suite, "candidate.exe"), "new worker");
        try
        {
            foreach (var mode in new[] { "Configure", "Restore", "Remove" })
            {
                var reference = Path.Combine(suite, "reference-" + mode); Seed(reference);
                var before = Snapshot(reference); var steps = new List<string>(); Execute(reference, mode, steps.Add);
                var after = Snapshot(reference);
                Check(!before.SequenceEqual(after), mode + " publishes its actual fixed-member generation");
                foreach (var (step, number) in steps.Distinct().Select((step, number) => (step, number)))
                {
                    var root = Path.Combine(suite, mode + "-" + number); Seed(root);
                    try { Execute(root, mode, at => { if (at == step) throw new Interrupted(); }); throw new IOException("Checkpoint not reached: " + step); }
                    catch (Interrupted) { }
                    Recover(root); var recovered = Snapshot(root);
                    var expected = steps.IndexOf(step) >= steps.IndexOf("journal-published-Committed") ? after : before;
                    Check(recovered.SequenceEqual(expected), mode + " interrupted at " + step + " obeys the durable commit boundary");
                    Recover(root); Check(Snapshot(root).SequenceEqual(recovered), "Recovery replay is idempotent");
                }
                Console.WriteLine("PASS " + mode + ": real filesystem interruption/reopen at " + steps.Distinct().Count() + " checkpoints");
            }
            foreach (var (point, i) in new[] { "journal-written-Preparing", "member-published-0", "member-published-1", "journal-written-Committed", "cleanup-old-0" }.Select((point, i) => (point, i)))
            {
                var root = Path.Combine(suite, "process-crash-" + i); Seed(root); var before = Snapshot(root);
                Check(RunChild(root, "Configure", point) == 71, "OS process exit reached " + point);
                Recover(root);
                Check(point == "cleanup-old-0" ? File.ReadAllText(Path.Combine(root, Names[0])) == "new worker" : Snapshot(root).SequenceEqual(before), "Killed setup process obeys the durable commit boundary");
            }
            var locked = Path.Combine(suite, "concurrent"); Seed(locked);
            using (var owner = new SyncSharkDiscoveryTransaction(locked, User, null!))
                Check(RunChild(locked, "claim", "unused") == 73, "A separate process cannot take the protected transaction's file claim");
            var unknown = Path.Combine(suite, "unknown"); Seed(unknown); File.WriteAllText(Path.Combine(unknown, "foreign.bin"), "preserve");
            try { using var tx = new SyncSharkDiscoveryTransaction(unknown, User, null!); throw new InvalidOperationException("Unclaimed content accepted"); }
            catch (IOException) { Check(File.ReadAllText(Path.Combine(unknown, "foreign.bin")) == "preserve", "Unclaimed content is preserved and rejected"); }
            Console.WriteLine("PASS " + checks + " Discovery transaction checks. Actual fixed-member I/O, atomic replacement, process interruption and process contention; protected ACL/UAC/task effects remain separate and unverified.");
            return 0;
        }
        finally
        {
            // Exact generated fixture, never a product/runtime directory or user file.
            if (!Path.GetFullPath(suite).StartsWith(allocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup containment changed");
            var directories = new Queue<string>(); directories.Enqueue(suite);
            while (directories.TryDequeue(out var directory))
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup preserves unexpected reparse content");
                    if ((attributes & FileAttributes.Directory) != 0) directories.Enqueue(path);
                }
            Directory.Delete(suite, true);
        }
    }
}
