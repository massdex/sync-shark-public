using System.Windows;
using Button = System.Windows.Controls.Button;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private bool _noticeOptionBusy;

    private async Task ShowRemainingNoticeItemsAsync(DecisionRow row)
    {
        var offset = 0;
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
                        SyncAuthorityConflictResolutionKind.PreserveBoth, V2.NoticeChoices.ShowRemaining, new(Offset: offset))],
                    connectionId: _workspaceAccountId);
                text.Text = result.Items.Single().Result?.Decisions.Single().Reason ?? "Listing unavailable; retry.";
                next.IsEnabled = text.Text.Contains("More items: next offset", StringComparison.Ordinal);
            }
            catch (Exception error) { text.Text = error.Message; }
        }
        next.Click += async (_, _) => { offset += 50; await LoadPage(); };
        await LoadPage();
        new Window { Title = "Remaining items", Owner = this, Content = panel, Width = 720,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
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
            if (option.Choice == V2.NoticeChoices.Rename && option.RequestsName)
            {
                var name = new System.Windows.Controls.TextBox { Text = V2.BlockerNames.Suggest(row.Path), MinWidth = 320, Margin = new Thickness(12) };
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Choose a new name. Existing files are never overwritten.", Margin = new Thickness(12) });
                panel.Children.Add(name);
                var confirm = new Button { Content = "Rename", IsDefault = true, Margin = new Thickness(12) };
                panel.Children.Add(confirm);
                var dialog = new Window { Title = "Rename blocked item", Owner = this, Content = panel,
                    SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                confirm.Click += (_, _) => dialog.DialogResult = true;
                if (dialog.ShowDialog() != true) return;
                input = new NoticeActionInput(name.Text);
            }
            if (option.Choice == V2.NoticeChoices.ShowRemaining)
            {
                await ShowRemainingNoticeItemsAsync(row);
                return;
            }
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, "Recording your choice…", isError: false);
            var batch = await _authorityHost.ResolveConflictSelectionCommandAsync(
                [new(new(row.ConflictId ?? "", row.PairId, row.Path, row.ConflictGeneration ?? 0),
                    SyncAuthorityConflictResolutionKind.PreserveBoth, option.Choice, input)],
                connectionId: _workspaceAccountId);
            var result = batch.Items.Single();
            var message = result.Result?.Decisions.Single().Reason ?? "The choice was not completed. Refresh and try again.";
            // Reload from the existing read models, rather than hiding a row before its choice was saved.
            await LoadConflictPageAsync(reset: true);
            _pairSet = await _authorityHost.LoadPairsAsync();
            RefreshFullApp();
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, message, isError: result.Result is null);
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
