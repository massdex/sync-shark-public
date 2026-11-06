using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

// Embedded-resource setup shell adapted from LiminalInstaller; no UI or elevation.
internal static class SyncSharkSetup
{
    private static int Main(string[] args)
    {
        string root = null;
        try
        {
            if (args.Length == 2 && args[0] == "--wait-pid")
            {
                try { using (Process caller = Process.GetProcessById(int.Parse(args[1]))) caller.WaitForExit(); }
                catch (ArgumentException) { }
            }
            else if (args.Length != 0) throw new ArgumentException("Unsupported setup arguments.");
            root = Path.Combine(Path.GetTempPath(), "SyncShark-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (string name in new[] { "payload.zip", "setup.ps1" })
            {
                using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                using (Stream output = new FileStream(Path.Combine(root, name), FileMode.CreateNew))
                {
                    if (input == null) throw new InvalidDataException("Missing embedded setup payload.");
                    input.CopyTo(output);
                }
            }
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            start.UseShellExecute = false;
            start.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(root, "setup.ps1") + "\" -Payload \"" + Path.Combine(root, "payload.zip") + "\"";
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            if (root != null && Directory.Exists(root))
            {
                try { Directory.Delete(root, true); }
                catch (IOException error) { Console.Error.WriteLine("Remove setup temporary folder " + root + ": " + error.Message); }
                catch (UnauthorizedAccessException error) { Console.Error.WriteLine("Remove setup temporary folder " + root + ": " + error.Message); }
            }
        }
    }
}
