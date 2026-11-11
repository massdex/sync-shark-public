using Massdex.SyncShark;

internal static class DirectoryLeaseChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "directory-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
        using var parent = PilotDirectoryLease.Open(root);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            using var child = PilotDirectoryLease.OpenOrCreateChild(parent, "shared", pin: true);
            child.VerifyBinding();
            return child.Identity;
        })).ToArray();
        start.Set();
        var identities = await Task.WhenAll(tasks);
        Require(identities.Distinct().Count() == 1, "concurrent creators share one verified directory");
        var rejected = false;
        try { using var duplicate = PilotDirectoryLease.CreateChild(parent, "shared"); }
        catch (IOException) { rejected = true; }
        Require(rejected, "exclusive create-new remains exclusive");
        File.WriteAllText(Path.Combine(root, "file"), "must remain a file");
        rejected = false;
        try { using var file = PilotDirectoryLease.OpenOrCreateChild(parent, "file", pin: true); }
        catch (IOException) { rejected = true; }
        Require(rejected && File.ReadAllText(Path.Combine(root, "file")) == "must remain a file",
            "existing file is rejected without modification");
        // Junction creation does not require developer mode/elevation on Windows.
        var target = Path.Combine(root, "target");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/c mklink /J \"" + junction + "\" \"" + target + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, "junction fixture created");
        rejected = false;
        try { using var link = PilotDirectoryLease.OpenOrCreateChild(parent, "junction", pin: true); }
        catch (IOException) { rejected = true; }
        Require(rejected, "existing junction cannot be adopted as a shared directory");
        var selectedPath = Path.Combine(root, "selected");
        Directory.CreateDirectory(selectedPath);
        using (var selected = PilotDirectoryLease.Open(selectedPath))
        {
            var plan = SyncControlNamespace.PlanLocalAllocation(selected, "fixture-installation", "fixture-namespace");
            Directory.CreateDirectory(plan.ControlPath);
            var boundaries = new List<string>();
            SyncControlNamespace.AllocationBoundaryForTest = boundary =>
            {
                boundaries.Add(boundary);
                if (boundary == "before-control-directory-recheck") Directory.Delete(plan.ControlPath);
            };
            try
            {
                var binding = await SyncControlNamespace.InspectAuthorizedLocalAsync(selected, plan, sharedRead: true);
                Require(binding is null, "vanished read-only namespace returns the same missing outcome as the initial guard");
                Require(!Directory.Exists(plan.ControlPath) &&
                    !File.Exists(Path.Combine(plan.ControlPath, SyncControlNamespace.MarkerName)) &&
                    boundaries.SequenceEqual(new[] { "before-control-directory-recheck" }),
                    "read-only race never creates, secures, marks or cleans up a control namespace");
            }
            finally { SyncControlNamespace.AllocationBoundaryForTest = null; }
        }
        Console.WriteLine("PASS directory lease concurrent create-or-open, exclusive create, file and junction rejection, read-only namespace disappearance");
        }
        finally
        {
            var junction = Path.Combine(root, "junction");
            if (Directory.Exists(junction)) Directory.Delete(junction, recursive: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
