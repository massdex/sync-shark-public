using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Massdex.SyncShark;

// Embedded-resource setup shell adapted from LiminalInstaller; no UI or elevation.
internal static class SyncSharkSetup
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new ArgumentException(SetupMessages.Text("shell.arguments"));
            string root = Path.Combine(Path.GetTempPath(), "SyncShark-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            // A temporary pathname is not proof that the shell owns every child at exit.
            Console.Error.WriteLine(SetupMessages.Text("preparation.retained", root));
            foreach (string name in new[] { "payload.zip", "setup.ps1", "SetupFileSystem.cs", "SetupMessages.cs", "setup-strings.xml" })
            {
                using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                using (Stream output = new FileStream(Path.Combine(root, name), FileMode.CreateNew))
                {
                    if (input == null) throw new InvalidDataException(SetupMessages.Text("shell.payload"));
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
    }
}
