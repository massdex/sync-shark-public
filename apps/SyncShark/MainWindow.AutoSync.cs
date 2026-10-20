using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

// Core v2 continuous sync at the window edge: the tray colour and the automatic-run balloon follow
// AutoSync.Changed, and the pause, power and network events kick it. None of this runs under core v1, where the
// host is not a V2SyncHost, so the v1 path is unchanged.
public partial class MainWindow
{
    // The newest report already announced per pair, so a later state change of one pair never re-announces it.
    private readonly Dictionary<string, RunReport> _announcedAutoReportByPair = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoRunningPairs = new(StringComparer.Ordinal);
    private TrayLatestRun? _latestAutoRunForTray;

    /// <summary>Re-evaluates continuous sync now for one pair (or every pair when null); a no-op under core v1.</summary>
    private void KickV2Monitors(string? pairId)
    {
        if (_authorityHost is V2SyncHost v2) v2.KickAutoSync(pairId);
    }

    /// <summary>Re-evaluates every pair on one account now; a no-op under core v1.</summary>
    private void KickV2MonitorsForAccount(string connectionId)
    {
        if (_authorityHost is V2SyncHost v2) v2.KickAutoSyncForAccount(connectionId);
    }

    /// <summary>Applies one continuous-sync state change to the pair rows, the tray colour and the run balloon.</summary>
    private void OnAutoSyncChanged(PairAutoState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnAutoSyncChanged(state)));
            return;
        }
        if (_closing) return;
        _monitorStateByPair[state.PairId] = MonitorMapping.StateText(state);
        // The tray shows automatic work through its own state; _runActive stays the manual run's ownership.
        if (state.Status == AutoStatus.Running) _autoRunningPairs.Add(state.PairId);
        else _autoRunningPairs.Remove(state.PairId);
        if (state.Status == AutoStatus.Held && state.Reason is { } reason)
            _monitorAttentionReasonByPair[state.PairId] = reason.Detail;
        else
            _monitorAttentionReasonByPair.Remove(state.PairId);
        RecordAutomaticRun(state);
        RefreshPairRows(_pairSet.SelectedPairId);
        WorkspacePresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    // The tray balloon follows each pair's newest completed automatic run: a report is announced once per pair, so
    // a later state change of another pair can never repeat it. The report carries its own completion instant, so
    // the balloon's time and the tray's ordering are the run's real time, never a fresh timestamp.
    private void RecordAutomaticRun(PairAutoState state)
    {
        if (state.LastReport is not { } report) return;
        if (_announcedAutoReportByPair.TryGetValue(state.PairId, out var announced) &&
            ReferenceEquals(announced, report)) return;
        _announcedAutoReportByPair[state.PairId] = report;
        var completed = report.CompletedUtc;
        if (_latestAutoRunForTray is { } newer && completed <= newer.CompletedUtc) return;
        _latestAutoRunForTray = new TrayLatestRun(state.PairId + "@" + completed.ToUnixTimeMilliseconds(), completed,
            PairNameFor(state.PairId), SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput
            {
                Stopped = report.Outcome == RunOutcomes.Stopped,
                Uploaded = report.Uploaded,
                Downloaded = report.Downloaded,
                Applied = report.Done,
                Failed = report.FailureCount,
                Blocked = report.Held
            }));
    }

    private string PairNameFor(string pairId) =>
        _pairSet.Pairs.FirstOrDefault(pair => pair.Id == pairId)?.Name ?? pairId;
}
