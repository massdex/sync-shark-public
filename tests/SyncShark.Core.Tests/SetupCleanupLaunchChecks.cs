using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Massdex.SyncShark;
using Microsoft.Win32.SafeHandles;

internal static class SetupCleanupLaunchChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "setup-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "owned-download");
        File.WriteAllText(sentinel, "preserve");
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SetupCleanupLaunchChecks).Assembly.Location);
            start.ArgumentList.Add("--setup-cleanup-launch-child");
            start.ArgumentList.Add(root);
            using var child = Process.Start(start) ?? throw new Exception("Cleanup launch fixture did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            child.WaitForExit();
            var evidence = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new Exception("Cleanup launch fixture failed: " + evidence);
            if (File.ReadAllText(sentinel) != "preserve") throw new Exception("Failed cleanup launch changed the download.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static int Child(string root)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new Exception("The process-limit fixture runs only on disposable CI runners.");
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // One slot is occupied by this isolated child; Windows rejects its cleanup subprocess.
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information
        var limits = new BasicLimits { LimitFlags = 0x8, ActiveProcessLimit = 1 };
        const int jobObjectBasicLimitInformation = 2;
        if (!SetInformationJobObject(job, jobObjectBasicLimitInformation, ref limits, (uint)Marshal.SizeOf<BasicLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var current = Process.GetCurrentProcess();
        if (!AssignProcessToJobObject(job, current.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var diagnostics = new StringWriter();
        SetupDownloadCleanup.Start(root, Environment.ProcessId, diagnostics);
        var output = diagnostics.ToString();
        if (!output.Contains(root) || !output.Contains("Remove that folder after setup has exited.") ||
            !File.Exists(SetupDownloadCleanup.LogPath) || !File.ReadAllText(SetupDownloadCleanup.LogPath).Contains(root))
            throw new Exception("Cleanup launch failure was not reported in output and log.");
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref BasicLimits information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
