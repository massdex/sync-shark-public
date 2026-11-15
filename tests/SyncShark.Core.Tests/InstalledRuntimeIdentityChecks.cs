using System.Diagnostics;
using Massdex.SyncShark;

// I1-I4: the installed-process admission is one canonical Core decision. These checks drive it
// with real filesystem paths and real leases under the isolated test product root, and they
// exercise the pure handle-final-path normalization directly, so no network or live system path
// is ever opened. The live Program Files runtime, product state and every provider are untouched.
internal static class InstalledRuntimeIdentityChecks
{
    private const string BoundRevision = "0e6725b6ad697d1510bb8575dbb53d50cb2c768f";
    private const string ActiveVersion = "9.9.9";
    private const string InactiveVersion = "9.9.8";

    internal static void Run()
    {
        // Refuse to touch anything unless the harness has already redirected the product root to
        // an isolated temporary allocation.
        Check(SyncSharkRuntimePaths.HasTestProductRoot,
            "The installed-identity checks refuse to run without an isolated test product root");

        var productRoot = SyncSharkRuntimePaths.ProductRoot;
        var versions = SyncSharkRuntimePaths.InstalledVersionsRoot;
        var runtime = Path.Combine(versions, ActiveVersion);
        var inactiveRuntime = Path.Combine(versions, InactiveVersion);
        var active = Path.Combine(productRoot, "active");
        var activeExecutable = Path.Combine(active, "SyncShark.exe");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(inactiveRuntime);
        var runtimeExecutable = Path.Combine(runtime, "SyncShark.exe");
        var inactiveExecutable = Path.Combine(inactiveRuntime, "SyncShark.exe");
        File.WriteAllBytes(runtimeExecutable, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        File.WriteAllBytes(inactiveExecutable, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        Check(Path.GetFullPath(SyncSharkRuntimePaths.InstalledExecutablePath) == Path.GetFullPath(activeExecutable),
            "The canonical installed launcher path is the product-root active SyncShark.exe");
        Check(!SyncSharkRuntimePaths.IsInstalledProcessFor(runtimeExecutable, BoundRevision),
            "Absent active launcher is not an installed process even from the exact versioned path");

        // Mutable and substituted locations are never an installed runtime directory.
        Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(active),
            "The mutable active alias is never an installed runtime directory");
        Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(versions, "not-a-version")),
            "A non-version directory name is never an installed runtime directory");
        Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(runtime, "nested")),
            "A nested directory below the version root is never an installed runtime directory");
        Check(!SyncSharkRuntimePaths.IsInstalledRuntimeDirectory(Path.Combine(productRoot, "versions")),
            "The versions parent itself is never an installed runtime directory");

        // Direct versioned launch and active-junction launch must report the same true.
        CreateDirectoryJunction(active, runtime);
        try
        {
            Check(SyncSharkRuntimePaths.IsInstalledProcessFor(runtimeExecutable, BoundRevision),
                "Direct versioned executable is admitted while the active launcher resolves to it");
            Check(SyncSharkRuntimePaths.IsInstalledProcessFor(activeExecutable, BoundRevision),
                "Active-junction launch reports the same true as the exact versioned path");
            Check(SyncSharkRuntimePaths.ResolveExecutablePath(activeExecutable).Equals(
                    SyncSharkRuntimePaths.ResolveExecutablePath(runtimeExecutable), StringComparison.OrdinalIgnoreCase),
                "The shared resolver returns one handle-final path for both launch forms");

            // Safe rejection: an inactive but otherwise valid version, and an executable outside
            // the version tree entirely.
            Check(!SyncSharkRuntimePaths.IsInstalledProcessFor(inactiveExecutable, BoundRevision),
                "A valid but inactive version runtime is not the installed process");
            var other = Path.Combine(productRoot, "other");
            Directory.CreateDirectory(other);
            var otherExecutable = Path.Combine(other, "SyncShark.exe");
            File.WriteAllBytes(otherExecutable, new byte[] { 0x4D, 0x5A });
            Check(!SyncSharkRuntimePaths.IsInstalledProcessFor(otherExecutable, BoundRevision),
                "An executable outside the active version path is not admitted");

            // Safe rejection: unbound revision and an absent running process path.
            Check(!SyncSharkRuntimePaths.IsInstalledProcessFor(activeExecutable, "UNBOUND"),
                "An unbound source revision fails closed even on the active launcher");
            Check(!SyncSharkRuntimePaths.IsInstalledProcessFor(null, BoundRevision),
                "An absent running process path fails closed");

            // Real local-handle rejections, all inside the isolated fixture.
            RequireRejected(() => SyncSharkRuntimePaths.ResolveExecutablePath(Path.Combine(productRoot, "absent.exe")),
                "An unresolvable process path is rejected");
            RequireRejected(() => SyncSharkRuntimePaths.ResolveExecutablePath(runtime),
                "A directory input is rejected by the executable resolver");

            // Safe rejection: protection admission itself fails closed for an unprotected runtime.
            RequireRejected(() => PilotDirectoryLease.RequireProtectedRuntime(runtime),
                "Protected-runtime admission rejects an unprotected runtime directory");
        }
        finally
        {
            if (Directory.Exists(active)) Directory.Delete(active);
        }

        // The exact handle-final decision, exercised as a pure function with no file or network
        // access: only the extended-path local DOS form is admitted.
        Check(SyncSharkRuntimePaths.NormalizeHandleFinalPath(@"\\?\C:\dir\app.exe") == @"C:\dir\app.exe",
            "A local extended-path DOS form normalizes to its ordinary absolute path");
        RequireRejected(() => SyncSharkRuntimePaths.NormalizeHandleFinalPath(@"\\?\UNC\server\share\app.exe"),
            "A UNC extended-path form is not a local DOS path");
        RequireRejected(() => SyncSharkRuntimePaths.NormalizeHandleFinalPath(
                @"\\?\Volume{00000000-0000-0000-0000-000000000000}\app.exe"),
            "A device extended-path form is not a local DOS path");
        RequireRejected(() => SyncSharkRuntimePaths.NormalizeHandleFinalPath(@"\\?\C:app.exe"),
            "A malformed extended-path form without its separator is rejected");
        RequireRejected(() => SyncSharkRuntimePaths.NormalizeHandleFinalPath(@"C:\dir\app.exe"),
            "An unprefixed ordinary path is rejected because Windows always returns the prefix");
        RequireRejected(() => SyncSharkRuntimePaths.NormalizeHandleFinalPath(@"\\?\"),
            "A truncated extended-path form is rejected");
    }

    private static void RequireRejected(Action action, string message)
    {
        Exception? observed = null;
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { observed = error; }
        if (observed is null)
            throw new InvalidDataException("Expected rejection but the call succeeded: " + message);
        Console.WriteLine("PASS " + message + " (" + observed.GetType().Name + ")");
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(Path.GetFullPath(junctionPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(targetPath));
        using var process = Process.Start(startInfo) ??
                            throw new IOException("The directory-junction probe could not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
            throw new IOException("The directory-junction probe failed: " + output + error);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); Console.WriteLine("PASS " + message); }
}
