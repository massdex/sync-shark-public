using System.Windows;
using Button = System.Windows.Controls.Button;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private bool _noticeOptionBusy;

    private async void NoticeOption_Click(object sender, RoutedEventArgs e)
    {
        using var feedback = ActionFeedback.Begin(sender);
        if (!feedback.Entered || _noticeOptionBusy) return;
        if (sender is not Button { DataContext: SyncNoticeOption option, Tag: DecisionRow row }) return;
        _noticeOptionBusy = true;
        try
        {
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, "Recording your choice…", isError: false);
            var batch = await _authorityHost.ResolveConflictSelectionCommandAsync(
                [new(new(row.ConflictId ?? "", row.PairId, row.Path, row.ConflictGeneration ?? 0),
                    SyncAuthorityConflictResolutionKind.PreserveBoth, option.Choice)],
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
