using System.IO;
using System.Windows;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private ExplorerRequest? _pendingExplorerRequest;
    private bool _explorerLocalReady;
    private bool _explorerStartupReady;
    private bool _explorerStartupFailed;
    private bool _explorerDispatching;
    private string _explorerRegistrationFingerprint = "";
    private readonly SemaphoreSlim _explorerRegistrationGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (SyncPair Pair, string Path, string Status, string Account)> _explorerStatusQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _publishedExplorerStatus = new();
    private int _explorerStatusWorker;
    private readonly Dictionary<string, string> _explorerPairLastState = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _queuedExplorerRootState = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _explorerStatusRetryAfter = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (SyncPair Pair, string Account, string State, long Generation)> _explorerStatusSweeps = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _explorerCurrentPairStatus = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _explorerRecentItemStatus = new();
    private long _explorerSweepGeneration;

    internal void QueueExplorerRequest(ExplorerRequest request)
    {
        ShowFromBackground();
        if (_explorerDispatching || _pendingExplorerRequest is not null)
        { ShowStatus("Finish the current Explorer action before opening another."); return; }
        _pendingExplorerRequest = request;
        if (_explorerLocalReady || _explorerStartupFailed) _ = DispatchExplorerRequestAsync();
    }

    private async Task DispatchExplorerRequestAsync()
    {
        if (_pendingExplorerRequest is not { } request || _explorerDispatching) return;
        if (!_explorerLocalReady && !_explorerStartupFailed) return;
        if (request.RequiresCompletedStartup && !_explorerStartupReady && !_explorerStartupFailed) return;
        _pendingExplorerRequest = null; _explorerDispatching = true;
        try
        {
            if (!_explorerLocalReady || request.RequiresCompletedStartup && !_explorerStartupReady)
                throw new InvalidOperationException("Startup did not finish. Resolve the startup issue before changing files; you can still browse loaded folder pairs.");
            if (!ExplorerIntegration.IsInstalledProcess) throw new InvalidOperationException("Explorer actions require the installed Sync Shark application.");
            if (Enumerable.Range(0, 3).Any(SettingsCategoryDirty))
            { ShowStatus("Save or discard the Settings changes before opening an Explorer action."); return; }
            if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
            await LoadPairsAsync();
            request = ExplorerRequest.Validate(request);
            var pairs = _pairSet.Pairs.Where(pair => string.Equals(pair.LocalRoot.TrimEnd('\\'), request.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                request.Path.StartsWith(pair.LocalRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (pairs.Length != 1) throw new InvalidOperationException("This item does not identify one registered Sync Shark folder pair.");
            var pair = pairs[0];
            using var root = PilotDirectoryLease.Open(pair.LocalRoot, new PilotDirectoryIdentity(pair.LocalVolumeSerialNumber, pair.LocalRootFileId));
            var relative = Path.GetRelativePath(root.Path, request.Path);
            if (relative == ".") relative = ""; else relative = PilotPath.NormalizeRelative(relative);
            VerifyExplorerPath(root.Path, request.Path, pair.Id); root.VerifyBinding();
            var folder = Directory.Exists(request.Path);
            if (!folder && !File.Exists(request.Path)) throw new FileNotFoundException("The selected item moved or is unavailable.");
            var row = new ActivityRow(DateTimeOffset.Now.ToString("O"), relative, "Selected in Explorer", "",
                LabelForPair(pair), pair.Id, pair.Name, ItemKind: folder ? "Folder" : "File", LocalRoot: pair.LocalRoot,
                DropboxRoot: pair.DropboxRoot, ConnectionId: pair.ConnectionId);
            switch (request.Operation)
            {
                case "open":
                    NavigateTo("Pairs"); PairList.SelectedItem = PairRows.Single(item => item.Id == pair.Id);
                    ExpandPairRow((PairRow)PairList.SelectedItem);
                    var selectionMessage = relative.Length == 0 ? "Folder Pair Opened" : "Selected Item: " + relative;
                    ShowPairEditorStatus(selectionMessage); ShowStatus(selectionMessage); break;
                case "web":
                    if (!CanResolveCloudItem(row))
                        throw new InvalidOperationException("No " + ActivityCloudName(row) + " location is available for this item.");
                    OpenCloudItem(row); break;
                case "share": OpenSharing(pair.Id, relative); break;
                case "offline":
                case "online-only":
                    if (_runActive) throw new InvalidOperationException("Wait for the current sync before changing availability.");
                    EnsureStorageOwnershipAvailable(pair);
                    var storage = request.Operation == "offline" ? SyncDirectoryStorage.AlwaysAvailable : SyncDirectoryStorage.CloudOnly;
                    var connection = _authorityHost.LoadAccountBinding(pair.ConnectionId) ?? throw new InvalidOperationException("Reconnect this account first.");
                    if (!AppDialogWindow.Confirm(this, "Change Availability", request.Operation == "offline" ? "Make Available Offline?" : "Make Online Only?",
                        LabelForPair(pair) + "\nFolder Pair: " + pair.Name + "\nSelected Item: " + (relative.Length == 0 ? pair.Name : relative) +
                        "\nUnsynced or changed local files are never freed. Folder choices apply to future files too; explicit child choices stay in effect.", "Apply")) break;
                    root.VerifyBinding();
                    ShowStatus("Applying Explorer availability choice; verifying the selected pair, item and policy.");
                    var result = await Task.Run(async () => folder
                        ? relative.Length == 0
                            ? await _authorityHost.ChangeManagedRootStorageAsync(pair.Id, connection.ConnectionId, storage, pair.PolicyRevision,
                                onProgress: ReportPairStorageProgressAsync)
                            : await _authorityHost.ChangeManagedStorageAsync(pair.Id, connection.ConnectionId, relative, storage, pair.PolicyRevision,
                                onProgress: ReportPairStorageProgressAsync)
                        : await _authorityHost.ChangeManagedFileStorageAsync(pair.Id, connection.ConnectionId, relative, storage, pair.PolicyRevision,
                            onProgress: ReportPairStorageProgressAsync));
                    await LoadPairsAsync(); await RefreshExplorerIntegrationAsync();
                    ShowStatus(result.Failures.Count == 0 ? $"Availability Updated: {result.FilesChanged:N0} files and {result.DirectoriesChanged:N0} folders." :
                        $"Preference Saved: {result.Failures.Count:N0} items remain local or unchanged. " +
                        string.Join(Environment.NewLine, result.Failures.Select(SyncActivityText.Safe))); break;
                case "sync":
                    if (!await AdmitManualSyncWithDraftAsync()) return;
                    await RunPairsAsync([pair], "Explorer"); break;
            }
        }
        catch (Exception error) { ShowStatus("Explorer Action Not Completed: " + error.Message); }
        finally { _explorerDispatching = false; }
    }

    internal static void VerifyExplorerPath(string root, string item, string? pairId = null)
    {
        var path = Path.GetFullPath(item);
        if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The item is outside the bound folder.");
        while (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            var attributes = File.GetAttributes(path);
            if (PilotPath.IsUnsafeAttributes(attributes) && (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var placeholder) ||
                    placeholder is null || pairId is not null && placeholder.Identity.PairId != pairId))
                throw new IOException("Explorer actions cannot cross a link or another cloud provider.");
            path = Path.GetDirectoryName(path) ?? throw new IOException("The path lost its bound root.");
        }
    }

    private async Task RefreshExplorerIntegrationAsync(bool propagateErrors = false)
    {
        if (!ExplorerIntegration.IsInstalledProcess || !_workspacePreferencesStore.Load().ExplorerIntegrationEnabled) return;
        await _explorerRegistrationGate.WaitAsync();
        try
        {
            var pairs = _pairSet.Pairs.ToArray();
            var fingerprint = string.Join("\n", pairs.Select(pair => pair.Id + "|" + pair.LocalRoot + "|" + pair.FilesOnDemandEnabled + "|" + pair.Name + "|" + LabelForPair(pair)));
            if (fingerprint == _explorerRegistrationFingerprint) return;
            await _workspacePreferencesGate.WaitAsync();
            try
            {
                var preferences = _workspacePreferencesStore.Load();
                if (preferences.ExplorerRegistrationBeforeJson is null)
                    await _workspacePreferencesStore.SaveAsync(preferences with {
                        ExplorerRegistrationBeforeJson = await Task.Run(() => ExplorerIntegration.CaptureBeforeState(pairs)) });
            }
            finally { _workspacePreferencesGate.Release(); }
            await Task.Run(async () =>
            {
                ExplorerIntegration.RegisterCommands(pairs);
                var failures = new List<string>();
                foreach (var pair in pairs)
                {
                    try { await ExplorerIntegration.RegisterRootAsync(pair, LabelForPair(pair)); }
                    catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                    { failures.Add(pair.Name + ": " + error.Message); }
                }
                if (failures.Count > 0) throw new IOException(string.Join("\n", failures));
            });
            foreach (var accountId in pairs.Select(pair => _authorityHost.LoadAccountBinding(pair.ConnectionId)?.ConnectionId).OfType<string>().Distinct())
                RememberAccountStatus(await _authorityHost.GetAccountStatusAsync(false, connectionId: accountId));
            _explorerRegistrationFingerprint = fingerprint;
            RefreshExplorerRootStates();
        }
        catch (Exception error)
        {
            RefreshExplorerRootStates(); ShowStatus("Explorer Integration Needs Attention: " + error.Message);
            if (propagateErrors) throw;
        }
        finally { _explorerRegistrationGate.Release(); }
    }

    private void QueueExplorerStatus(SyncAuthorityBatchProgress progress)
    {
        if (!ExplorerIntegration.IsInstalledProcess || !_workspacePreferences.ExplorerIntegrationEnabled) return;
        var account = LabelForPair(progress.Pair);
        if (progress.Action is { } action && action.RelativePath.Length > 0)
            Queue(action.RelativePath, action.Stage == "Starting" ? "Syncing" :
                action.Stage is "Failed" or "Blocked" or "Needs attention" ? "Needs Attention" : "Up to Date");
        if (progress.Result is { } result)
        {
            foreach (var item in result.Decisions.Where(item => item.RelativePath.Length > 0))
                Queue(item.RelativePath, item.Kind switch {
                    SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending => "Needs Attention",
                    SyncDecisionKind.Stopped => "Paused",
                    SyncDecisionKind.Reconciling => "Checking",
                    SyncDecisionKind.Skipped => "Not Selected or Ignored",
                    _ => "Up to Date"
                });
            _explorerPairLastState[progress.Pair.Id] = result.Stopped ? "Paused" :
                result.Decisions.Any(item => item.Kind is SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or SyncDecisionKind.ResponseUnknown or SyncDecisionKind.CleanupPending) ? "Needs Attention" : "Up to Date";
            ScheduleExplorerStatusSweep(progress.Pair, account);
        }
        else _explorerPairLastState[progress.Pair.Id] = "Syncing";
        RefreshExplorerRootStates();
        void Queue(string path, string status)
        {
            if (path.StartsWith('<')) return;
            var key = progress.Pair.Id + "|" + path;
            if (_explorerRecentItemStatus.Count >= 4096)
                foreach (var retired in _explorerRecentItemStatus.Keys.Take(1024)) _explorerRecentItemStatus.TryRemove(retired, out _);
            _explorerRecentItemStatus[key] = status;
            if (_explorerStatusQueue.Count >= 1024 && !_explorerStatusQueue.ContainsKey(key))
            { ScheduleExplorerStatusSweep(progress.Pair, account); return; }
            _explorerStatusQueue[key] = (progress.Pair, path, status, account);
        }
        if (Interlocked.CompareExchange(ref _explorerStatusWorker, 1, 0) == 0) _ = Task.Run(PublishExplorerStatusesAsync);
    }

    private void RefreshExplorerRootStates()
    {
        if (!ExplorerIntegration.IsInstalledProcess || !_workspacePreferences.ExplorerIntegrationEnabled) return;
        // SS-D12: one presentation scope for this whole per-pair pass. Every pair's account, known health and
        // account pause is resolved from the one snapshot it established, so the pass reads a fixed number of
        // validated stores instead of re-reading them once per pair.
        using var presentation = OpenPresentationScope();
        var retries = _explorerStatusRetryAfter.Where(item => item.Value <= DateTimeOffset.UtcNow)
            .Select(item => item.Key.Split('|')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in _pairSet.Pairs)
        {
            // The pair's ordered nonlocal endpoint accounts; a legacy blank saved id resolves through the
            // primary Dropbox account exactly as the existing pair rule already does.
            var connections = PresentationPairConnectionIds(pair);
            var binding = connections.Select(id => PresentationAccountBinding(id)).FirstOrDefault(item => item is not null);
            var known = binding is null ? null : _knownAccountStatus.GetValueOrDefault(binding.ConnectionId);
            var disconnected = binding is null || known is { HasProtectedCredential: false } ||
                connections.Any(id => _knownAccountStatus.GetValueOrDefault(id) is { HasProtectedCredential: false });
            var state = disconnected ? "Disconnected" :
                known?.FailureType is not null || IsPairKnownUnhealthy(pair.Id) || _attentionPairIds.Contains(pair.Id) || _monitorAttentionReasonByPair.ContainsKey(pair.Id) || _runAttentionReasonByPair.ContainsKey(pair.Id) ? "Needs Attention" :
                GlobalPauseActive || PresentationPairAccountPaused(pair) || !pair.Enabled ? "Paused" :
                _runActive && _workspaceRunningPairId == pair.Id ? _workspacePaused ? "Paused" : "Syncing" :
                !AutomaticSyncEnabled ? "Paused" : _explorerPairLastState.GetValueOrDefault(pair.Id, "Ready to Check");
            var account = LabelForPair(pair); var fingerprint = state + "|" + account;
            var previous = _explorerCurrentPairStatus.GetValueOrDefault(pair.Id);
            _explorerCurrentPairStatus[pair.Id] = state;
            if (previous != state) ScheduleExplorerStatusSweep(pair, account);
            if (retries.Contains(pair.Id))
            {
                ScheduleExplorerStatusSweep(pair, account);
                foreach (var retryKey in _explorerStatusRetryAfter.Keys.Where(key => key.StartsWith(pair.Id + "|", StringComparison.Ordinal)))
                    _explorerStatusRetryAfter[retryKey] = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            var key = pair.Id + "|";
            if (_queuedExplorerRootState.GetValueOrDefault(pair.Id) == fingerprint &&
                (_publishedExplorerStatus.GetValueOrDefault(key) == fingerprint || _explorerStatusQueue.ContainsKey(key) ||
                 _explorerStatusRetryAfter.TryGetValue(key, out var retry) && DateTimeOffset.UtcNow < retry)) continue;
            _queuedExplorerRootState[pair.Id] = fingerprint;
            _explorerStatusQueue[pair.Id + "|"] = (pair, "", state, account);
        }
        if ((!_explorerStatusQueue.IsEmpty || !_explorerStatusSweeps.IsEmpty) && Interlocked.CompareExchange(ref _explorerStatusWorker, 1, 0) == 0) _ = Task.Run(PublishExplorerStatusesAsync);
    }

    private void ScheduleExplorerStatusSweep(SyncPair pair, string account)
    {
        _explorerStatusSweeps[pair.Id] = (pair, account, _explorerCurrentPairStatus.GetValueOrDefault(pair.Id, "Ready to Check"),
            Interlocked.Increment(ref _explorerSweepGeneration));
    }

    private async Task PublishExplorerStatusesAsync()
    {
        try
        {
            while (!_explorerStatusQueue.IsEmpty || !_explorerStatusSweeps.IsEmpty)
            {
                foreach (var key in _explorerStatusQueue.Keys)
                {
                    if (!_explorerStatusQueue.TryRemove(key, out var value)) continue;
                    await PublishItemAsync(value.Pair, value.Path, value.Status, value.Account);
                }
                foreach (var pairId in _explorerStatusSweeps.Keys)
                {
                    if (!_explorerStatusSweeps.TryRemove(pairId, out var sweep)) continue;
                    if (!WindowsCloudFilesProvider.IsOwnedSyncRoot(sweep.Pair.LocalRoot, pairId)) continue;
                    string? after = null;
                    while (!_updateLifetime.IsCancellationRequested && _workspacePreferences.ExplorerIntegrationEnabled)
                    {
                        if (_explorerStatusSweeps.ContainsKey(pairId)) break; // A newer pair state starts a fresh bounded sweep.
                        var page = await _authorityHost.LoadExplorerStatusPageAsync(pairId, after, _updateLifetime.Token);
                        if (page.Count == 0) break;
                        foreach (var item in page)
                        {
                            var key = pairId + "|" + item.RelativePath;
                            var status = SyncExplorerStatus.Combine(item.Status, _explorerRecentItemStatus.GetValueOrDefault(key));
                            await PublishItemAsync(sweep.Pair, item.RelativePath, status, sweep.Account);
                        }
                        after = page[^1].RelativePath;
                        // Foreground actions and new transfers take precedence over the next metadata page.
                        foreach (var key in _explorerStatusQueue.Keys)
                            if (_explorerStatusQueue.TryRemove(key, out var next))
                                await PublishItemAsync(next.Pair, next.Path, next.Status, next.Account);
                        await Task.Delay(25, _updateLifetime.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error) { await Dispatcher.InvokeAsync(() => ShowStatus("Explorer Status Needs Attention: " + error.Message)); }
        finally
        {
            Interlocked.Exchange(ref _explorerStatusWorker, 0);
            if (!_updateLifetime.IsCancellationRequested && (!_explorerStatusQueue.IsEmpty || !_explorerStatusSweeps.IsEmpty) && Interlocked.CompareExchange(ref _explorerStatusWorker, 1, 0) == 0)
                _ = Task.Run(PublishExplorerStatusesAsync);
        }

        async Task PublishItemAsync(SyncPair pair, string path, string status, string account)
        {
            if (!_workspacePreferences.ExplorerIntegrationEnabled || _updateLifetime.IsCancellationRequested) return;
            var key = pair.Id + "|" + path;
            status = SyncExplorerStatus.Display(status, _explorerCurrentPairStatus.GetValueOrDefault(pair.Id, "Ready to Check"));
            var fingerprint = status + "|" + account;
            if (_publishedExplorerStatus.GetValueOrDefault(key) == fingerprint) return;
            try
            {
                if (!await ExplorerIntegration.SetItemStatusAsync(pair, path, status, account))
                { MarkRetry(); return; }
                if (_publishedExplorerStatus.Count >= 4096)
                    foreach (var retired in _publishedExplorerStatus.Keys.Take(1024)) _publishedExplorerStatus.TryRemove(retired, out _);
                _publishedExplorerStatus[key] = fingerprint; _explorerStatusRetryAfter.TryRemove(key, out _);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { MarkRetry(); }
            void MarkRetry()
            {
                // A single pair retry covers every missed item without retaining an unbounded queue.
                _explorerStatusRetryAfter[pair.Id + "|"] = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }
    }
}
