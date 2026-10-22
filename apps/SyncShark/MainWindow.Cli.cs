using System.IO;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private bool _cliSettingsGateHeld;
    private bool _cliExplorerRefreshPending;
    private bool _cliPauseGateHeld;

    internal async Task BeforeCliPauseChangeAsync(CancellationToken cancellationToken)
    {
        await _workspacePreferencesGate.WaitAsync(cancellationToken);
        if (_pauseChangeActive)
        {
            _workspacePreferencesGate.Release();
            throw new InvalidOperationException("A pause change is already being applied.");
        }
        _pauseChangeActive = true;
        _cliPauseGateHeld = true;
    }

    internal Task CompleteCliPauseChangeAsync()
    {
        if (!_cliPauseGateHeld) throw new InvalidOperationException("The CLI does not hold the pause preference gate.");
        try
        {
            _workspacePreferences = _workspacePreferencesStore.Load();
            _workspacePaused = _authorityHost.ActiveRun?.Paused == true;
            RefreshWorkspace();
        }
        finally
        {
            _cliPauseGateHeld = false;
            _pauseChangeActive = false;
            _workspacePreferencesGate.Release();
        }
        return Task.CompletedTask;
    }

    internal async Task BeforeCliSettingsSaveAsync(CancellationToken cancellationToken)
    {
        RequireCleanCliSettingsDrafts();
        await _workspacePreferencesGate.WaitAsync(cancellationToken);
        try
        {
            RequireCleanCliSettingsDrafts();
            _cliSettingsGateHeld = true;
        }
        catch { _workspacePreferencesGate.Release(); throw; }
    }

    internal async Task CompleteCliSettingsSaveAsync()
    {
        if (!_cliSettingsGateHeld) throw new InvalidOperationException("The CLI settings transaction does not own the app preference gate.");
        _cliSettingsGateHeld = false;
        _workspacePreferencesGate.Release();
        // Explorer registration takes its own gate, then briefly takes the preference
        // gate to retain its rollback preimage. Never await it inside that same gate.
        if (_cliExplorerRefreshPending)
        {
            _cliExplorerRefreshPending = false;
            _explorerRegistrationFingerprint = "";
            await RefreshExplorerIntegrationAsync();
        }
    }

    private void RequireCleanCliSettingsDrafts()
    {
        if (_runActive || _settingsSaving || _navigationSaving || _checkingDeliveredUpdates || _discoverySetupActive)
            throw new InvalidOperationException("Finish the active sync, settings save or setup before changing settings.");
        if (Enumerable.Range(0, 3).Any(SettingsCategoryDirty) ||
            PairLocationDraftActive && CurrentPairEditorHasUnsavedChanges())
            throw new InvalidOperationException("Save or discard the app's unsaved changes before changing settings from the CLI.");
    }

    internal async Task<bool> ApplyCliSavedSettingsAsync(SyncCliSettingsData previous, SyncCliSettingsData saved,
        CancellationToken cancellationToken)
    {
        if (!_cliSettingsGateHeld) throw new InvalidOperationException("The app preference gate is not held.");
        RequireCleanCliSettingsDrafts();
        cancellationToken.ThrowIfCancellationRequested();
        var startupChanged = previous.Automation.StartWithWindows != saved.Automation.StartWithWindows;
        try
        {
            if (previous.Interface.ExplorerIntegrationEnabled && !saved.Interface.ExplorerIntegrationEnabled)
                throw new InvalidOperationException("A settings flag cannot safely remove registered Explorer integration. Use the app's guarded removal workflow.");
            if (startupChanged) SetDraftStartupRegistration(saved.Automation.StartWithWindows);
            var automation = _authorityHost.LoadAutomation();
            ConfigureAutomationTimer(automation);
            await ConfigureLocalMonitorsAsync(automation);
            _workspacePreferences = _workspacePreferencesStore.Load();
            _themeManager.PreviewPreference(_workspacePreferences.Theme);
            BrandedItemImages.ApplyStyle(_workspacePreferences.StatusIconStyle);
            _homeFeed.RowHeight = ActivityGrid.RowHeight = _workspacePreferences.CompactRows ? 60 : 76;
            foreach (var row in PairRows) row.UpdateGlobalLiveSync(automation.Enabled);

            // Update controls and their saved baselines together. Drafts were refused before saving;
            // selection handlers must not manufacture new unsaved changes during this refresh.
            _settingsLoading = true;
            try
            {
                _settingsAppearance.SelectedItem = _workspacePreferences.Theme;
                StartWithWindowsBox.IsChecked = automation.StartWithWindows;
                _settingsToggles["running"].IsChecked = _workspacePreferences.KeepRunningWhenClosed;
                _settingsDensity.SelectedIndex = _workspacePreferences.CompactRows ? 1 : 0;
                _settingsIconStyle.SelectedItem = _workspacePreferences.StatusIconStyle;
                AutomationEnabledBox.IsChecked = automation.Enabled;
                AutomationIntervalBox.Text = automation.IntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var transfers = _authorityHost.LoadTransferSettings();
                ConcurrentPairsBox.Text = transfers.MaximumConcurrentPairs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                UploadLimitBox.Text = transfers.UploadKiBPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture);
                DownloadLimitBox.Text = transfers.DownloadKiBPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _settingsResourceMode.SelectedItem = transfers.ResourceMode;
                _settingsConcurrentFiles.Text = transfers.MaximumConcurrentFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _settingsToggles["problems"].IsChecked = _workspacePreferences.NotifyProblems;
                _settingsToggles["completed"].IsChecked = _workspacePreferences.NotifyCompleted;
                _settingsToggles["transfers"].IsChecked = _workspacePreferences.NotifyTransfers;
                _settingsToggles["sound"].IsChecked = _workspacePreferences.NotificationSound;
                for (var category = 0; category < 3; category++)
                    _settingsBaselines[category] = CaptureSettingsCategory(category);
            }
            finally { _settingsLoading = false; }
            RefreshSettingsDraftHeader();
            RefreshUpdateFolderLabel();
            if (!previous.Interface.ExplorerIntegrationEnabled && saved.Interface.ExplorerIntegrationEnabled)
            {
                _cliExplorerRefreshPending = true;
            }
            RefreshWorkspace();
            return true;
        }
        catch
        {
            if (startupChanged) SetDraftStartupRegistration(previous.Automation.StartWithWindows);
            throw; // Core restores the previous saved stores and invokes this adapter for that state.
        }
    }
}
