using System.Windows.Controls;
using Control = System.Windows.Controls.Control;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private int _activityPageLoads, _queuePageLoads;
    private bool _queueLoadFailed;
    private bool _loadingConflicts, _capturingConflictSelection;
    private string? _conflictLoadFailure;
    private string? _pairEditorBusyReason;

    private void BindActionExplanations()
    {
        IEnumerable<Control> ControlsUnder(System.Windows.DependencyObject parent)
        {
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
                if (child is System.Windows.DependencyObject element)
                {
                    if (element is Control control) yield return control;
                    foreach (var nested in ControlsUnder(element)) yield return nested;
                }
        }
        var pairControls = ControlsUnder(PairEditorPanel).ToHashSet();
        string PairBusy() => _runActive ? Busy() : _pairEditorBusyReason ?? "The current folder-pair operation is still running.";
        foreach (var control in pairControls)
            if (control.ReadLocalValue(System.Windows.UIElement.IsEnabledProperty) == System.Windows.DependencyProperty.UnsetValue)
                ActionAvailability.Bind(control, PairBusy);
        void Bind(string name, Func<string> reason)
        { if (FindName(name) is Control control) ActionAvailability.Bind(control,
            () => pairControls.Contains(control) && !PairEditorPanel.IsEnabled ? PairBusy() : reason()); }
        string Busy() => "A scan or sync is running. Wait for it to finish or stop it before starting another action.";
        foreach (var name in new[] { "RunButton", "SyncAllNowButton", "AddPairButton", "BrowseRecoveryButton",
                     "KeepLocalConflictButton", "KeepDropboxConflictButton", "PreserveBothConflictButton",
                     "PairEnabledBox", "PairDeletionBox", "PairPriorityChoice", "RecoveryRetentionBox", "WholeRootSelectionBox" })
            Bind(name, Busy);
        Bind("PairStorageBox", () => _runActive ? Busy() : StorageSupportText.Text);
        Bind("PairFilesOnDemandBox", () => _runActive ? Busy()
            : "Online-only storage is already enabled for this pair. Use Disable Files On Demand to change it safely.");
        Bind("LocalPathEntryBox", () => "The previous folder path is still opening. Wait for its result before entering another path.");
        Bind("SelectAllMatchingConflictsButton", () => _runActive ? Busy()
            : "The exact conflict generations matching these filters are still being selected. Wait for the selection result.");
        Bind("ApproveBootstrapButton", () => FirstSyncUnavailableReason);
        Bind("RefreshBootstrapButton", () => UiOperationBusy ? Busy() : "Select a folder pair to scan first.");
        Bind("RepairBootstrapMappingsButton", () => UiOperationBusy ? Busy()
            : "Select the folder pair whose retained copied-file identities require recovery.");
        Bind("GoToLocalPathButton", () => !LocalPathEntryBox.IsEnabled ? "The previous folder path is still opening."
            : "Enter a full local folder path first.");
        Bind("SavePairButton", () => _runActive ? Busy() : _pairValidationReason ??
            EndpointDraft()?.Validation.Describe() ?? "No changes are ready to save. Complete the folder-pair fields first.");
        Bind("EditorSyncNowButton", () => UiOperationBusy ? Busy() :
            PairList.SelectedItem is not PairRow row ? "Save or select a folder pair first."
            : PairHealthBlockingReason(row.Id) ?? "Save or discard this pair's unsaved changes before syncing.");
        foreach (var name in new[] { "PauseButton", "StopButton" })
            Bind(name, () => _readingRunOutcomes ? "The operation has ended; the app is reading its recorded outcomes. There is no active transfer to pause or stop."
                : name == "PauseButton" ? "There are no folder pairs in the selected account scope to pause."
                : "No scan or sync is currently running.");
        foreach (var name in new[] { "LoadMoreActivityButton", "LoadMoreBootstrapButton", "LoadMoreConflictsButton",
                     "LoadMoreQueueButton", "LoadMoreRecoveryButton" })
            Bind(name, () => name == "LoadMoreBootstrapButton" && _loadingBootstrap ||
                name == "LoadMoreRecoveryButton" && _loadingRecovery ||
                name == "LoadMoreActivityButton" && _activityPageLoads > 0 ||
                name == "LoadMoreConflictsButton" && _loadingConflicts ||
                name == "LoadMoreQueueButton" && _queuePageLoads > 0
                    ? "The requested page is still loading. Wait for its rows or an error before loading another page."
                    : name == "LoadMoreConflictsButton" && _conflictLoadFailure is not null
                    ? _conflictLoadFailure + " Refresh or change the filters to try again. Existing rows were kept."
                    : !_authorityHost.AuthorityStateExists ? "There is no saved activity yet."
                    : "All currently available matching rows are loaded. Refresh to check for new results.");
        Bind("LoadMoreDropboxRootButton", () => (_selectedDropboxRootNode ?? DropboxRootNodes.FirstOrDefault()) is not { } node
            ? "Select a provider folder first." : node.IsLoading ? "This folder's contents are still loading."
            : node.PaginationBlockedReason ?? "All available child folders are loaded.");
        foreach (var name in new[] { "ConnectButton", "ConnectionConnectButton", "ConnectionReauthorizeButton", "ConnectionDisconnectButton" })
            Bind(name, () => UiOperationBusy ? Busy() : AuthorizationAttemptWaiting ? "Finish or cancel the current authorization first."
                : _connectionStatus?.Binding is null ? "Select or connect an account first."
                : name.Contains("Disconnect", StringComparison.Ordinal) ? "This account has no saved connection to disconnect."
                : "This account is already connected. Use Reconnect to authorize it again.");
        foreach (var name in new[] { "RestoreOriginalButton", "RestoreCopyButton", "RemoveRecoveryButton" })
            Bind(name, () => _runActive ? Busy() : _loadingRecovery ? "Recovery copies are still loading."
                : RecoveryGrid.SelectedItem is not RecoveryRow row ? "Select a retained recovery copy first."
                : "This copy does not support that action. " + row.DetailText());
        foreach (var name in new[] { "PrepareRewindButton", "DetectRewindButton", "ApplyRewindButton", "ResumeRewindButton" })
            Bind(name, () => _runActive ? Busy() : RewindPairBox.SelectedItem is not PairRow ? "Select a folder pair first."
                : RewindStatusText.Text);
        if (_diagnosticRun is not null) ActionAvailability.Bind(_diagnosticRun, () => "The read-only diagnostic checks are running.");
        if (_storageRefresh is not null) ActionAvailability.Bind(_storageRefresh, () => "Storage information is being loaded. Wait or cancel the current inspection.");
        Bind("ConnectionManageButton", () => "Connect this account before opening its provider account page.");
        Bind("DisableFilesOnDemandButton", () => "Online-only files are not enabled for this saved pair.");
        Bind("RecoveryPairBox", () => "Recovery information for the current pair is still loading.");
        void BindSettings(System.Windows.DependencyObject parent)
        {
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
                if (child is System.Windows.DependencyObject element)
                {
                    if (element is Control control) ActionAvailability.Bind(control, Busy);
                    BindSettings(element);
                }
        }
        BindSettings(TransferSettingsPanel);
        foreach (var name in new[] { "CreateLocalFolderConfirmButton", "CreateLocalFolderCancelButton" })
            Bind(name, () => "The local folder is being created. Wait for Windows to confirm the result.");
        foreach (var name in new[] { "PairDuplicateMenuItem", "PairDeleteMenuItem", "PairSyncNowMenuItem" }) Bind(name, Busy);
        Bind("ActivityOpenPairMenuItem", () => "This event is not linked to an existing folder pair.");
        Bind("ActivityOpenRecoveryMenuItem", () => "This event has no recoverable copy in an existing pair.");
        Bind("ActivityCopyPathMenuItem", () => "Select a file or folder event with a recorded path.");
        Bind("ActivityCopyDetailsMenuItem", () => "Select an activity event first.");
        if (_activityRefresh is not null) ActionAvailability.Bind(_activityRefresh, () => "The activity list is being refreshed.");
        if (_runLoadMore is not null) ActionAvailability.Bind(_runLoadMore, () => "The next page of run history is being loaded.");
        if (_workspacePairAction is not null) ActionAvailability.Bind(_workspacePairAction, () => "This event is not linked to an existing folder pair.");
        if (_workspaceRecoveryAction is not null) ActionAvailability.Bind(_workspaceRecoveryAction, () => "This event has no recoverable copy in an existing pair.");
        if (_inspectorLocal is not null) ActionAvailability.Bind(_inspectorLocal, () => "This event has no accessible local file or folder path.");
        if (_inspectorCloud is not null) ActionAvailability.Bind(_inspectorCloud, () => "This event does not identify a current cloud item that can be opened.");
        if (_inspectorLink is not null) ActionAvailability.Bind(_inspectorLink, () => "This event does not identify a current cloud item with a location link.");
        if (_addAccountButton is not null) ActionAvailability.Bind(_addAccountButton, () => "Finish or cancel the current authorization before adding another account.");
        if (_accountPauseButton is not null) ActionAvailability.Bind(_accountPauseButton, () => "Select a connected account first.");
        if (_pairAccountPicker is not null) ActionAvailability.Bind(_pairAccountPicker, () => _runActive ? Busy()
            : "An existing pair keeps its bound account. Select accounts in the endpoint editor when creating a new pair.");
        if (_sharedDrivesToggle is not null) ActionAvailability.Bind(_sharedDrivesToggle, () => _savingSharedDrives
            ? "The Shared drives preference is being saved." : "Select a connected Google Drive account to change Shared drives access.");
        if (_accountPicker is not null) ActionAvailability.Bind(_accountPicker,
            () => "Finish or cancel the current authorization before changing the selected connection.");
        if (_authorizationCancelButton is not null) ActionAvailability.Bind(_authorizationCancelButton,
            () => "Cancellation has been requested. Waiting for the authorization attempt to stop safely.");
        if (_operationLogRunLink is not null) ActionAvailability.Bind(_operationLogRunLink,
            OperationRunLinkUnavailableReason);
        foreach (var button in new[] { _updateCheck, _updateInstall, _updateDownload })
            if (button is not null) ActionAvailability.Bind(button, () => "An update check, download or installation is already in progress. " + _updateStatus.Text);
        foreach (var button in new[] { _explorerSetupButton, _explorerRepairButton })
            if (button is not null) ActionAvailability.Bind(button, () => "Explorer integration is being checked or updated. Wait for that operation to finish.");
    }
}
