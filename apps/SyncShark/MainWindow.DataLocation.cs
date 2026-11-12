using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private void ChangeDataLocation()
    {
        try
        {
            RequireCleanCliSettingsDrafts();
            if (_authorityHost.HasActiveRun)
                throw new InvalidOperationException("Finish or resolve pending work first.");
            var picker = new OpenFolderDialog { Title = "Choose an empty Sync Shark data folder", Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            var destination = SyncDataLocation.ValidateDirectory(picker.FolderName);
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable."))
                { UseShellExecute = false };
            start.ArgumentList.Add("--move-data"); start.ArgumentList.Add(destination);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppDialogWindow.Inform(this, "Data location", $"Moving your data to {destination}… Sync Shark will restart",
                "The maintenance process verifies the copy before switching locations. If it cannot move the data, Sync Shark restarts on the old location and reports why.");
            // No await between admission, launching maintenance and shutdown: UI work cannot interleave.
            _ = Process.Start(start) ?? throw new InvalidOperationException("The data migration process could not be started.");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            AppDialogWindow.Inform(this, "Data location not changed", "Finish or resolve pending work first, then try again.", error.Message);
        }
    }
}
