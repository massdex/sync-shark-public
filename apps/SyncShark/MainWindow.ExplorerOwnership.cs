using System.IO;
using System.Windows;
using System.Windows.Controls;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private int _pairOwnershipRefresh;
    internal Func<string, Task<IReadOnlyList<ExplorerIntegration.CompetingRoot>>>? PairOwnershipLoaderForTest { get; set; }

    private async Task RefreshPairOwnershipAsync(SyncPair pair)
    {
        PairPauseButton.Visibility = _authorityHost is V2.V2SyncHost ? Visibility.Visible : Visibility.Collapsed;
        var refresh = ++_pairOwnershipRefresh;
        PairOwnershipActions.Children.Clear();
        try
        {
            if (_authorityHost is V2.V2SyncHost pauseHost)
            {
                var paused = await Task.Run(() => pauseHost.IsPairPaused(pair.Id));
                if (!IsCurrent()) return;
                PairPauseButton.Content = paused ? "Resume Pair" : "Pause Pair";
            }
            var conflicts = PairOwnershipLoaderForTest is { } loader
                ? await loader(pair.LocalRoot)
                : await Task.Run(() => ExplorerIntegration.CompetingRoots(pair.LocalRoot));
            if (!IsCurrent()) return;
            PairOwnershipActions.Children.Clear();
            PairOwnershipText.Text = conflicts.Count == 0
                ? pair.FilesOnDemandEnabled ? "Managed by Sync Shark" : "Local Files; Explorer Status Not Enabled"
                : string.Join("\n", conflicts.Select(item => item.Provider + "\nParent Location: " + item.Path));
            foreach (var conflict in conflicts)
                PairOwnershipActions.Children.Add(Button("Resolve Ownership", (_, _) => OpenOwnershipHandover(pair, conflict), true));
            if (conflicts.Count == 0 && !pair.FilesOnDemandEnabled)
                PairOwnershipActions.Children.Add(AsyncButton("Enable Explorer Status", async (_, _) => await EnablePairExplorerStatusAsync(pair), true));
        }
        catch (Exception error)
        {
            if (!IsCurrent()) return;
            PairOwnershipActions.Children.Clear();
            PairOwnershipText.Text = "Ownership Could Not Be Verified: " + error.Message;
        }

        bool IsCurrent() => refresh == _pairOwnershipRefresh && !_editingNewPair &&
            PairList.SelectedItem is PairRow selected && selected.Id == pair.Id;
    }

    private async void PairPause_Click(object sender, RoutedEventArgs e)
    {
        if (_authorityHost is not V2.V2SyncHost host || PairList.SelectedItem is not PairRow selected) return;
        try
        {
            var wasPaused = await Task.Run(() => host.IsPairPaused(selected.Id));
            var paused = await host.SetPairPausedAsync(selected.Id, !wasPaused);
            if (PairList.SelectedItem is not PairRow current || current.Id != selected.Id) return;
            PairPauseButton.Content = paused ? "Resume Pair" : "Pause Pair";
            ShowStatus(paused ? "Pause requested. This pair will stop at its next checkpoint." :
                "Pair resumed. Its next run continues from checkpoints.");
        }
        catch (Exception error) { ShowStatus("Pair pause could not be changed: " + error.Message); }
    }

    private async Task EnablePairExplorerStatusAsync(SyncPair pair)
    {
        if (!ExplorerIntegration.IsInstalledProcess) { ShowStatus("Explorer registration is available in the signed installed app."); return; }
        if (_runActive || !await PrepareToLeaveCurrentPairDraftAsync()) return;
        try
        {
            EnsureStorageOwnershipAvailable(pair);
            var account = _authorityHost.LoadDropboxConnection(pair.ConnectionId) ?? throw new InvalidOperationException("Reconnect this account first.");
            await _authorityHost.ChangeManagedStorageAsync(pair.Id, account.ConnectionId, null,
                SyncDirectoryStoragePolicy.Default(pair), pair.PolicyRevision);
            await LoadPairsAsync(); _explorerRegistrationFingerprint = ""; await RefreshExplorerIntegrationAsync();
            await RefreshPairSyncStatusAsync();
            ShowStatus("Explorer Status Enabled. Your storage default and existing file contents are unchanged.");
        }
        catch (Exception error) { ShowStatus("Explorer Status Could Not Be Enabled: " + error.Message); }
    }

    private void OpenOwnershipHandover(SyncPair pair, ExplorerIntegration.CompetingRoot target)
    {
        var window = SharingDialog("Folder Ownership", out var body, out var feedback, out var actions);
        var affected = _pairSet.Pairs.Where(item => item.LocalRoot.Equals(target.Path, StringComparison.OrdinalIgnoreCase) ||
            item.LocalRoot.StartsWith(target.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
        body.Children.Add(Field("Current Provider", Text(target.Provider, 15, true)));
        body.Children.Add(Field("Entire Registered Root", Text(target.Path, 13)));
        body.Children.Add(Field("Affected Sync Shark Pairs", Text(string.Join("\n", affected.Select(item => item.Name + " (" + LabelForPair(item) + ")")), 13)));
        body.Children.Add(Text("This applies to the entire parent root, not just this pair. It removes only Dropbox's Explorer registration; it does not uninstall Dropbox, delete files or revoke cloud access. Dropbox may register the folder again when restarted.", 13, margin: new Thickness(0, 12, 0, 12)));
        body.Children.Add(Text("Quit Dropbox first. Inspection reads folder metadata without downloading content. Native cloud-files ownership, online-only placeholders, links, unreadable folders or changes during inspection stop the handover.", 12, brush: "MutedInk"));
        ExplorerOwnership.Inspection? inspection = null; var busy = false;
        usingLifetime();
        void usingLifetime()
        {
            var lifetime = new CancellationTokenSource();
            CancellationTokenSource? inspectionCancellation = null;
            var remove = Button("Remove Dropbox Registration", (_, _) => { }); remove.IsEnabled = false;
            ActionAvailability.Bind(remove, () => busy ? "The folder ownership inspection or change is still running."
                : inspection is null ? "Inspect the parent folder successfully before removing its registration."
                : inspection.RemovalUncertain ? "Windows did not confirm the last result. Refresh ownership status first."
                : "The registration has already been removed.");
            var restore = Button("Undo Registration Removal", (_, _) => { }, true); restore.Visibility = Visibility.Collapsed;
            var inspect = AsyncButton("Inspect Parent Folder", async (_, _) =>
            {
                if (busy) return;
                if (!ExplorerIntegration.IsInstalledProcess) { feedback.Text = "Use the signed installed app for ownership changes."; return; }
                if (_runActive) { feedback.Text = "Wait for the current Sync Shark operation to finish."; return; }
                busy = true; remove.IsEnabled = false; inspection?.Dispose(); inspection = null;
                inspectionCancellation?.Dispose();
                inspectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                feedback.Text = "Inspecting metadata only. No file content is being opened…";
                try
                {
                    var next = new ExplorerOwnership.Inspection(target);
                    try { await Task.Run(() => next.Inspect(inspectionCancellation.Token), inspectionCancellation.Token); inspection = next; }
                    catch { next.Dispose(); throw; }
                    feedback.Text = $"Inspection Passed\nFiles: {inspection.Files:N0}\nFolders: {inspection.Folders:N0}\nNo native cloud root, placeholders or links found.";
                    remove.IsEnabled = true;
                }
                catch (Exception error) { feedback.Text = error is OperationCanceledException ? "Inspection Cancelled" : error.Message; }
                finally { busy = false; }
            }, true);
            remove.Click += async (sender, _) =>
            {
                using var actionFeedback = ActionFeedback.Begin(sender);
                if (!actionFeedback.Entered) return;
                if (busy || inspection is null || _runActive || !await PrepareToLeaveCurrentPairDraftAsync()) return;
                if (!AppDialogWindow.Confirm(window, "Remove Dropbox Registration", "Hand over this entire Explorer root?",
                    target.Path + "\nAffected Folder Pairs: " + affected.Length + "\nNo files are deleted. This affects Explorer integration for every item under the parent root, including items outside your Sync Shark pairs.", "Remove Registration")) return;
                busy = true;
                try
                {
                    inspection.Remove(); remove.IsEnabled = inspect.IsEnabled = false; restore.Visibility = Visibility.Visible;
                    feedback.Text = "Dropbox Registration Removed. No file content changed. Close this window, then enable Explorer Status for the pairs you want Sync Shark to manage. Undo is available here before another provider registers these folders.";
                    await RefreshPairOwnershipAsync(pair);
                }
                catch (Exception error) { feedback.Text = error.Message + (inspection.RemovalUncertain ? "\nWindows did not confirm the outcome. Refresh ownership status before taking another action. No automatic retry was made." : ""); restore.Visibility = inspection.Removed && !inspection.RemovalUncertain ? Visibility.Visible : Visibility.Collapsed; remove.IsEnabled = false; inspect.IsEnabled = !inspection.RemovalUncertain; }
                finally { busy = false; }
            };
            restore.Click += async (sender, _) =>
            {
                using var actionFeedback = ActionFeedback.Begin(sender);
                if (!actionFeedback.Entered) return;
                if (busy || inspection is null) return;
                try { inspection.Restore(); feedback.Text = "Dropbox Registration Restored."; restore.Visibility = Visibility.Collapsed; await RefreshPairOwnershipAsync(pair); }
                catch (Exception error) { feedback.Text = error.Message; }
            };
            actions.Children.Add(inspect); actions.Children.Add(remove); actions.Children.Add(restore);
            actions.Children.Add(Button("Cancel Inspection", (_, _) => inspectionCancellation?.Cancel(), true));
            actions.Children.Add(Button("Close", (_, _) => window.Close(), true));
            window.Closing += (_, e) => { if (busy) { inspectionCancellation?.Cancel(); e.Cancel = true; feedback.Text = "Stopping inspection…"; } };
            window.Closed += (_, _) => { lifetime.Cancel(); inspectionCancellation?.Dispose(); lifetime.Dispose(); inspection?.Dispose(); };
        }
        window.Show();
    }
}
