using System.Diagnostics;
using System.Text;

namespace Massdex.SyncShark;

/// <summary>Removes an owned setup download and reports retained downloads without hiding installation results.</summary>
public static class SetupDownloadCleanup
{
    public static string LogPath => Path.Combine(Path.GetTempPath(), "SyncShark-Update-cleanup.log");

    public static void Delete(string directory, TextWriter diagnostics)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report(directory, error, diagnostics);
        }
    }

    public static void Start(string directory, int setupPid, TextWriter diagnostics)
    {
        using var resource = typeof(SetupDownloadCleanup).Assembly.GetManifestResourceStream("SyncShark.SetupCleanup.ps1")!;
        using var reader = new StreamReader(resource);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32/WindowsPowerShell/v1.0/powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd())) }) start.ArgumentList.Add(argument);
        start.Environment["SYNCSHARK_SETUP_PID"] = setupPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["SYNCSHARK_SETUP_DIR"] = directory;
        start.Environment["SYNCSHARK_SETUP_CLEANUP_LOG"] = LogPath;
        start.Environment["SYNCSHARK_SETUP_RETAINED_MESSAGE"] = SetupMessages.Text("cleanup.retained", "{0}", "{1}");
        start.Environment["SYNCSHARK_SETUP_LOG_MESSAGE"] = SetupMessages.Text("cleanup.log", "{0}", "{1}");
        start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/Modules");
        try
        {
            using var cleanup = Process.Start(start) ?? throw new IOException(SetupMessages.Text("cleanup.start"));
            diagnostics.WriteLine(SetupMessages.Text("cleanup.scheduled", LogPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Report(directory, error, diagnostics);
        }
    }

    private static void Report(string directory, Exception error, TextWriter diagnostics)
    {
        var message = SetupMessages.Text("cleanup.retained", directory, error.Message);
        diagnostics.WriteLine(message);
        try { File.AppendAllText(LogPath, message + Environment.NewLine); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            diagnostics.WriteLine(SetupMessages.Text("cleanup.log", LogPath, logError.Message));
        }
    }
}
