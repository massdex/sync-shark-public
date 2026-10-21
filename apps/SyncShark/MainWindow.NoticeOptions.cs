using System.Windows;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private bool _noticeOptionBusy;

    private string? NoticeDestination(string title)
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Unused relative path inside this pair:" });
        var input = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(input);
        var save = new Button { Content = "Continue", IsDefault = true };
        panel.Children.Add(save);
        var dialog = new Window { Owner = this, Title = title, Width = 480, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel,
            Background = Background, Foreground = Foreground };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private async Task ShowRemainingNoticeItemsAsync(DecisionRow row)
    {
        var offset = 0;
        string? cursor = null;
        string? pageToken = null;
        var text = new System.Windows.Controls.TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Height = 380, Margin = new Thickness(12) };
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(text);
        var next = new Button { Content = "Next 50 per side", Margin = new Thickness(12) };
        panel.Children.Add(next);
        async Task LoadPage()
        {
            next.IsEnabled = false;
            try
            {
                var result = await _authorityHost.ResolveConflictSelectionCommandAsync(
                    [new(new(row.ConflictId ?? "", row.PairId, row.Path, row.ConflictGeneration ?? 0),
                        SyncAuthorityConflictResolutionKind.PreserveBoth, V2.NoticeChoices.ShowRemaining, NoticeInput: new(Name: cursor, Offset: cursor is null ? offset : 0, PageToken: pageToken))],
                    connectionId: _workspaceAccountId);
                text.Text = result.Items.Single().Result?.Decisions.Single().Reason ?? "Listing unavailable; retry.";
                const string cursorPrefix = "Next cursor (--name): ";
                cursor = text.Text.Split('\n').FirstOrDefault(line => line.StartsWith(cursorPrefix, StringComparison.Ordinal))?[cursorPrefix.Length..].Trim();
                const string marker = "Next page token: ";
                var markerAt = text.Text.IndexOf(marker, StringComparison.Ordinal);
                pageToken = markerAt < 0 ? null : text.Text[(markerAt + marker.Length)..].Trim();
                if (markerAt >= 0) text.Text = text.Text[..markerAt].TrimEnd();
                next.IsEnabled = cursor is not null || pageToken is not null || text.Text.Contains("More items: next offset", StringComparison.Ordinal);
            }
            catch (Exception error) { text.Text = error.Message; }
        }
        next.Click += async (_, _) => { if (pageToken is null) offset += 50; await LoadPage(); };
        await LoadPage();
        new Window { Title = "Remaining items", Owner = this, Content = panel, Width = 720,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        // Online-only paging publishes bounded per-item choices through the same authority.
        await LoadConflictPageAsync(reset: true);
    }

    private async void NoticeOption_Click(object sender, RoutedEventArgs e)
    {
        using var feedback = ActionFeedback.Begin(sender);
        if (!feedback.Entered || _noticeOptionBusy) return;
        if (sender is not Button { DataContext: SyncNoticeOption option, Tag: DecisionRow row }) return;
        _noticeOptionBusy = true;
        try
        {
            if (option.Choice == V2.NoticeChoices.OpenRecoverySettings)
            {
                NavigateTo("Settings");
                ShowStatus("Choose an available Recovery location. Deletions and replacements remain paused until Recovery is available.");
                return;
            }
            if (option.Choice == V2.NoticeChoices.OpenFolderSettings)
            {
                var pair = PairRows.FirstOrDefault(item => item.Id == row.PairId)
                    ?? throw new InvalidOperationException("This folder pair is no longer listed. Refresh Folder Pairs.");
                NavigateTo("Pairs");
                PairList.SelectedItem = pair;
                ExpandPairRow(pair);
                ShowPairEditorStatus("Review selection and ignore rules for: " + row.Path);
                ShowStatus("Folder settings opened. The notice stays open; no files changed.");
                return;
            }
            NoticeActionInput? input = null;
            if (option.RequestsName && option.Choice != V2.NoticeChoices.ShowRemaining)
            {
                var rename = option.Choice == V2.NoticeChoices.Rename;
                var name = new System.Windows.Controls.TextBox { Text = rename ? V2.BlockerNames.Suggest(row.Path) : "", MinWidth = 320, Margin = new Thickness(12) };
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = rename
                    ? "Choose a new name. Existing files are never overwritten."
                    : "Paste the exact path from Show target to confirm reading it, including excluded or nested-pair content. Copy again may leave this blank for an included target.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 540, Margin = new Thickness(12) });
                panel.Children.Add(name);
                var confirm = new Button { Content = rename ? "Rename" : "Confirm copy", IsDefault = true, Margin = new Thickness(12) };
                panel.Children.Add(confirm);
                var dialog = new Window { Title = rename ? "Rename blocked item" : "Confirm link target access", Owner = this, Content = panel,
                    SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                confirm.Click += (_, _) => dialog.DialogResult = true;
                if (dialog.ShowDialog() != true) return;
                input = new NoticeActionInput(string.IsNullOrWhiteSpace(name.Text) ? null : name.Text);
            }
            if (option.Choice == V2.NoticeChoices.ShowRemaining)
            {
                await ShowRemainingNoticeItemsAsync(row);
                return;
            }
            if (option.Choice is V2.NoticeChoices.DefenderKeep or V2.NoticeChoices.DefenderProceed &&
                System.Windows.MessageBox.Show(this, option.Choice == V2.NoticeChoices.DefenderKeep
                    ? "Keep the existing Defender exclusion for this reviewed current file? Antivirus protection remains reduced. No Defender setting will change and no UAC will run. " + row.Reason
                    : "Proceed with deletions for only this reviewed UNKNOWN-history batch? Positive Defender evidence remains held. " + row.Reason,
                    "Confirm Defender decision", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
            var requestPath = row.Path;
            if (option.Choice is V2.NoticeChoices.DefenderReleaseItem or V2.NoticeChoices.DefenderExcludeHeld)
            {
                var release = option.Choice == V2.NoticeChoices.DefenderReleaseItem;
                var held = await _authorityHost.LoadHeldItemsAsync(row.PairId, row.ConflictId ?? "", 1000);
                var picked = HeldItemPicker.Pick(this, release ? "Release one held item" : "Stop syncing one held item",
                    release ? "Release" : "Stop syncing", held);
                if (picked is null) return;
                requestPath = picked;
            }
            if (V2.NoticeChoices.IsHeldChoice(option.Choice) &&
                System.Windows.MessageBox.Show(this, option.Choice switch
                {
                    V2.NoticeChoices.DefenderReleaseHeld => "Release every item held by your choice? The next sync rechecks Defender evidence, then deletes them from Dropbox under ordinary rules (recoverable in Dropbox history).",
                    V2.NoticeChoices.DefenderReleaseItem => "Release " + requestPath + "? The next sync rechecks Defender evidence, then deletes it from Dropbox under ordinary rules (recoverable in Dropbox history).",
                    _ => "Stop syncing " + requestPath + "? It is excluded from sync; neither copy is deleted."
                }, "Confirm held-item choice", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
            var exclusionPaths = "";
            if (option.Choice == V2.NoticeChoices.DefenderExclude)
            {
                var pair = _pairSet.Pairs.Single(p => p.Id == row.PairId);
                var exact = AttendedElevatedAction.ValidatePath(pair.LocalRoot, row.Path);
                exclusionPaths = "Two exact Defender exclusions (not a folder/wildcard):" + Environment.NewLine +
                    exact + Environment.NewLine + AttendedElevatedAction.DownloadStagePath(exact) + Environment.NewLine;
            }
            if (option.Choice == V2.NoticeChoices.DefenderVerify &&
                System.Windows.MessageBox.Show(this, "Read-only administrator verification (Windows UAC). No Defender settings or files will be changed. " +
                    "Only the recorded exclusion paths will be checked." + Environment.NewLine + row.Reason + Environment.NewLine + "Continue?",
                    "Verify exclusion removal", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information) != System.Windows.MessageBoxResult.Yes) return;
            if ((option.Choice is V2.NoticeChoices.QuarantineDeleteDropbox or V2.NoticeChoices.DefenderExclude) &&
                System.Windows.MessageBox.Show(this, "Requested action: " + option.Choice + Environment.NewLine +
                    "Exact item: " + row.Path + Environment.NewLine + exclusionPaths +
                    "Defender exclusions reduce antivirus protection. Dropbox deletion retains version history. Continue?",
                    "Confirm quarantine action", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
            var choice = option.Choice;
            string? destination = null;
            if (choice is V2.NoticeChoices.Compare or V2.NoticeChoices.Merge)
            {
                var content = await _authorityHost.LoadConflictContentAsync(row.PairId, requestPath, conflictId: row.ConflictId);
                var comparison = new ConflictComparisonWindow(content, allowChoices: false) { Owner = this };
                comparison.ShowDialog();
                if (choice == V2.NoticeChoices.Compare) return;
                if (content.MergedText is null)
                {
                    SharedPresentation.SetInlineFeedback(ConflictActionFeedback, content.Note ?? "A clean merge is unavailable; both originals are unchanged.", isError: false);
                    return;
                }
            }
            if (choice is V2.NoticeChoices.Merge or V2.NoticeChoices.RenameIncoming)
            {
                destination = NoticeDestination(choice == V2.NoticeChoices.Merge ? "Save merged file" : "Rename incoming item");
                if (destination is null) return;
            }
            if (choice is V2.NoticeChoices.KeepA or V2.NoticeChoices.KeepB or V2.NoticeChoices.ReplaceExisting)
                if (MessageBox.Show(this, "This choice removes or replaces the unchosen item(s), including the contents of a folder. Removed content is preserved in configured Recovery or provider version history. Items Sync Shark has seen change or appear since are kept. Ignored items inside a removed folder follow the existing rule: preserved, except small ignored file-name matches (64 KiB or less), and Dropbox keeps its version history. Continue?",
                    "Confirm recoverable removal", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            if (option.Choice == V2.NoticeChoices.ConfirmRemoval && System.Windows.MessageBox.Show(this,
                row.Recommendation.Split('\n')[0] + "\n\nConfirm removal of only this frozen review? All removed files remain recoverable. Changed folders are held for another review.",
                "Confirm reviewed removal", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, "Recording your choice…", isError: false);
            var batch = await _authorityHost.ResolveConflictSelectionCommandAsync(
                [new(new(row.ConflictId ?? "", row.PairId, requestPath, row.ConflictGeneration ?? 0),
                    SyncAuthorityConflictResolutionKind.PreserveBoth, choice, destination, input)],
                connectionId: _workspaceAccountId);
            var result = batch.Items.Single();
            var message = result.Result?.Decisions.Single().Reason ?? "The choice was not completed. Refresh and try again.";
            // Reload from the existing read models, rather than hiding a row before its choice was saved.
            await LoadConflictPageAsync(reset: true);
            _pairSet = await _authorityHost.LoadPairsAsync();
            RefreshFullApp();
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, message, isError: result.Result is null);
            if (option.Choice == V2.NoticeChoices.OpenStorage && result.Result is not null && message == "ms-settings:storagesense")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(message) { UseShellExecute = true });
            ShowStatus(message);
        }
        catch (Exception error)
        {
            // Pause may have succeeded before persistence or refresh failed. Show the current state on retry.
            try
            {
                await LoadConflictPageAsync(reset: true);
                _pairSet = await _authorityHost.LoadPairsAsync();
                RefreshFullApp();
            }
            catch { /* Keep the original failure visible if the read model is also unavailable. */ }
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback,
                "Could not finish this choice. Refresh notices and retry: " + error.Message, isError: true);
        }
        finally { _noticeOptionBusy = false; }
    }
}
