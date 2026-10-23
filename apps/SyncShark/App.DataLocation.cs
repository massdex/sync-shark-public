using System.Diagnostics;

namespace Massdex.SyncShark;

public partial class App
{
    private async void MoveDataAndRestart(string destination, string parentId)
    {
        string message;
        try
        {
            if (!int.TryParse(parentId, out var id) || id == Environment.ProcessId)
                throw new InvalidOperationException("The data move has no valid originating process.");
            try
            {
                using var parent = Process.GetProcessById(id);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { /* The originating process already exited. */ }
            SyncDataLocation.MoveResult result;
            using (var owner = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile))
                result = await Task.Run(() => SyncDataLocation.MoveAsync(owner.StateAuthority, destination));
            message = SyncDataLocation.CleanupOldCopy(result);
        }
        catch (Exception error)
        {
            message = "Data location was not changed. " + error.Message;
            message += " Sync Shark will reopen its existing data.";
        }
        AppDialogWindow.Inform(null, "Data location", message, "Sync Shark will now restart.");
        try
        {
            if (Environment.ProcessPath is { } executable)
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
        }
        catch (Exception error)
        {
            AppDialogWindow.Inform(null, "Restart Sync Shark", "Open Sync Shark again to continue.", error.Message);
        }
        Shutdown();
    }
}
