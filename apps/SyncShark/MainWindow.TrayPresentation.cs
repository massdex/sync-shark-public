using System.Windows;

namespace Massdex.SyncShark;

internal sealed record TrayScopePresentation(string Status, string Detail, bool Paused,
    bool OwnPause, bool Running, string Pair, string Label);
internal sealed record TrayAccountSnapshot(IReadOnlyList<ActivityRow> Rows, int Pending);

public partial class MainWindow
{
    internal bool TrayReady => _workspaceReady;

    internal TrayScopePresentation TrayScope(string? accountId, int? pending = null)
    {
        // SS-D12: one presentation scope for this tray scope, so the account list, the pause projection and
        // the attention decision all read the one snapshot and the one captured instant of this pass.
        using var presentation = OpenPresentationScope();
        if (accountId is null)
            return new(TrayStatus, TrayDetail, TrayPaused, TrayPaused, _runActive, TrayPair, "All Accounts");
        var label = TrayAccounts.FirstOrDefault(account => account.Id == accountId)?.Label ?? "Account Unavailable";
        var ownPause = PauseStateProjection.AccountPause(PresentationPreferences, accountId, PresentationNowUtc);
        var running = _runActive && TrayMatchesAccount(_workspaceRunningPairId, accountId);
        var paused = ownPause is not null || GlobalPauseActive || running && _workspacePaused;
        var pairs = _pairSet.Pairs.Where(pair => TrayMatchesAccount(pair.Id, accountId)).ToArray();
        var attention = PresentationAttentionCases.Any(item => TrayMatchesAccount(item.Pair.Id, accountId));
        var status = !_workspaceReady ? "Starting Sync Shark" : paused ? "Sync Paused" : running ? _workspaceStage
            : attention ? "Needs Attention" : pending > 0 ? "Work Waiting to Finish"
            : pairs.Length == 0 ? "Choose Folders to Sync" : pairs.All(pair => !pair.Enabled) ? "Folder Pairs Paused"
            : !AutomaticSyncEnabled ? "Automatic Sync Off" : "Ready to Check";
        var detail = GlobalPauseActive ? "All accounts are paused. Select All Accounts to resume the global pause."
            : ownPause is not null ? PauseSchedule.DescribeDetail(ownPause.UntilResumed, ownPause.UntilUtc,
                ownPause.TimeZoneId, PresentationNowUtc)
            : running ? _workspacePhaseDetail : attention ? "Review this account's items in Needs Attention."
            : pairs.Length == 0 ? "Add a folder pair for this account."
            : pairs.All(pair => !pair.Enabled) ? "Resume individual folder pairs from Folder Pairs."
            : !AutomaticSyncEnabled ? "Automatic monitoring is off; manual sync remains available."
            : pending is null ? "Loading this account's activity…"
            : pending > 0 ? $"{pending:N0} pending operation(s) for this account."
            : "No pending operations recorded for this account.";
        return new(status, detail, paused, ownPause is not null, running,
            running ? _workspaceRunningPair ?? string.Empty : string.Empty, label);
    }

    internal async Task<TrayAccountSnapshot> LoadTrayAccountSnapshotAsync(string accountId)
    {
        if (!TrayAccounts.Any(account => account.Id == accountId))
            throw new InvalidOperationException("This account is no longer connected.");
        var page = await LoadSummaryHistoryAsync(accountId);
        var pending = await _authorityHost.LoadPendingQueueCountAsync(connectionId: accountId);
        return new(page.Select(RetainedActivityRow).ToArray(), pending);
    }

    internal async Task HandleTrayPauseAsync(UIElement target, string? accountId)
    {
        var state = TrayScope(accountId);
        if (state.OwnPause) await ResumePausedAsync(accountId);
        else if (state.Paused)
            ShowActionStatus("This account is held by the global pause. Select All Accounts to resume it.");
        else OpenTrayPauseMenu(target, accountId);
    }

    internal void OpenTrayPauseMenu(UIElement target, string? accountId)
    {
        if (accountId is null) OpenPauseMenu(target);
        else OpenAccountPauseMenu(target, accountId);
    }

    internal void OpenTrayScope(string? accountId, bool attention)
    {
        ApplyAccountScopeTransition(accountId);
        RefreshWorkspaceAccountPickers(); RefreshAttentionPairChoices();
        _attentionFingerprint = string.Empty;
        if (attention) OpenWorkspaceAttention();
        else OpenScopedActivity(0);
    }
}
