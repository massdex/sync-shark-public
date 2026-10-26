using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private TabControl _settingsTabs = null!;
    private ComboBox _settingsAppearance = null!, _settingsDensity = null!, _settingsIconStyle = null!;
    private ComboBox _settingsResourceMode = null!;
    private ComboBox _settingsOnlineOnlyReturn = null!;
    private TextBox _settingsConcurrentFiles = null!;
    private TextBlock _settingsResourceStatus = null!;
    private Button _settingsSave = null!, _settingsDiscard = null!;
    private TextBlock _settingsFeedback = null!;
    private TabItem _settingsExplorerTab = null!;
    private readonly Dictionary<string, CheckBox> _settingsToggles = new();
    private readonly Dictionary<int, object> _settingsBaselines = new();
    private bool _settingsReady, _settingsLoading, _settingsSaving, _navigationSaving, _restoringNavigation;
    internal Action<bool>? StartupRegistrationForTest { get; set; }
    internal Func<AppDialogChoice>? UnsavedNavigationChoiceForTest { get; set; }
    private sealed record GeneralDraft(string Theme, bool Startup, bool KeepRunning, bool Compact, string IconStyle);
    private sealed record TransfersDraft(bool Automatic, string Interval, string Concurrent, string Upload, string Download,
        string ResourceMode, string ConcurrentFiles, string OnlineOnlyReturn);
    private sealed record NotificationsDraft(bool Problems, bool Completed, bool Transfers, bool Sound);

    private CheckBox DraftToggle(string key, string label, bool value)
    {
        var toggle = new CheckBox { IsChecked = value, Style = (Style)FindResource("PairToggle"), ToolTip = label };
        System.Windows.Automation.AutomationProperties.SetName(toggle, label);
        toggle.Click += (_, _) => SettingsDraftChanged();
        _settingsToggles.Add(key, toggle);
        return toggle;
    }

    // The Settings information architecture is fixed to six coherent tabs, one concern each.
    // General, Sync & Performance and Notifications keep their existing draft, save and discard
    // behaviour (categories 0-2); Explorer, About & Updates and Diagnostics act immediately and own
    // no draft because they carry status, maintenance actions and read-only checks.
    private void BuildSettingsDraftPage()
    {
        _settingsTabs = new TabControl { ItemContainerStyle = WorkspaceTabStyle(), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        var general = new StackPanel();
        _settingsAppearance = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark", "AMOLED" }, SelectedItem = _themeManager.Preference, MinWidth = 150 };
        System.Windows.Automation.AutomationProperties.SetName(_settingsAppearance, "Appearance");
        _settingsAppearance.SelectionChanged += (_, _) => SettingsDraftChanged();
        general.Children.Add(SettingRow("Appearance", "Preview a theme. Save Changes keeps your choice.", _settingsAppearance));
        // The one status icon preference: this draft control edits the persisted
        // UiPreferences.StatusIconStyle value and previews the real shared marks through
        // BrandedItemImages, inside this category's existing save and discard baseline.
        _settingsIconStyle = new ComboBox { ItemsSource = new[] { "Colour", "Symbol", "Combined" },
            SelectedItem = _workspacePreferences.StatusIconStyle, MinWidth = 150 };
        System.Windows.Automation.AutomationProperties.SetName(_settingsIconStyle, "Status icon style");
        _settingsIconStyle.SelectionChanged += (_, _) => SettingsDraftChanged();
        var iconPreview = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var state in new[] { "Failed", "Completed", "Started" })
            iconPreview.Children.Add(new ShellTypeImage { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0),
                ToolTip = state, DataContext = new ActivityRow("", "<preview>", state, "", Entity: "Run") });
        iconPreview.Children.Add(_settingsIconStyle);
        general.Children.Add(SettingRow("Status Icons", "Colour, symbols, or both. Preview updates now; Save Changes keeps your choice.", iconPreview));
        StartWithWindowsBox.Content = null; StartWithWindowsBox.Style = (Style)FindResource("PairToggle");
        StartWithWindowsBox.Click += (_, _) => SettingsDraftChanged();
        general.Children.Add(SettingRow("Start With Windows", "Start in the notification area after you sign in.", Detach(StartWithWindowsBox)));
        general.Children.Add(SettingRow("Keep Running When Closed", "Closing the window leaves Sync Shark in the tray.", DraftToggle("running", "Keep Running When Closed", _workspacePreferences.KeepRunningWhenClosed)));
        _settingsDensity = new ComboBox { ItemsSource = new[] { "Comfortable", "Compact" }, SelectedIndex = _workspacePreferences.CompactRows ? 1 : 0, MinWidth = 150 };
        _settingsDensity.SelectionChanged += (_, _) => SettingsDraftChanged();
        general.Children.Add(SettingRow("Row Spacing", "Choose how much space appears between activity items.", _settingsDensity));
        general.Children.Add(SettingRow("Data location", SyncSharkRuntimePaths.ProductRoot,
            Button("Move data…", (_, _) => ChangeDataLocation(), true,
                explanation: "Choose an empty folder on a local fixed drive. Sync Shark closes, verifies the move, and restarts. Finish or resolve pending work first.")));
        _settingsTabs.Items.Add(new TabItem { Header = "General", Content = FormPage(general) });

        var sync = new StackPanel();
        AutomationEnabledBox.Content = null; AutomationEnabledBox.Style = (Style)FindResource("PairToggle");
        AutomationEnabledBox.Click += (_, _) => SettingsDraftChanged();
        foreach (var box in new[] { AutomationIntervalBox, ConcurrentPairsBox, UploadLimitBox, DownloadLimitBox })
            box.TextChanged += (_, _) => SettingsDraftChanged();
        sync.Children.Add(SettingsGroupHeading("Transfers"));
        sync.Children.Add(SettingRow("Automatic Sync", "Monitor the folder pairs enabled in Folder Pairs. Pausing live sync does not change folder pairs or files.", Detach(AutomationEnabledBox)));
        sync.Children.Add(SettingRow("Safety Check Interval", "Minutes between full checks for missed change notifications.", Detach(AutomationIntervalBox)));
        sync.Children.Add(SettingRow("Concurrent Pairs", "Maximum pairs running together (1–8). Independent files share the resource limits below; dependent changes stay ordered.", Detach(ConcurrentPairsBox)));
        var transferPreferences = _authorityHost.LoadTransferSettings();
        _settingsResourceMode = new ComboBox { ItemsSource = new[] { "Auto", "Performance", "Low impact" },
            SelectedItem = transferPreferences.ResourceMode, MinWidth = 160,
            ToolTip = "Auto balances sync speed with computer responsiveness. Performance tunes parallel transfers by measured throughput, not total computer CPU usage, while reserving memory for bounded transfer buffers. Low impact prioritises other apps. All modes respect provider cooldowns, memory safeguards and your bandwidth limits." };
        System.Windows.Automation.AutomationProperties.SetName(_settingsResourceMode, "Transfer resource mode");
        _settingsResourceMode.SelectionChanged += (_, _) => SettingsDraftChanged();
        sync.Children.Add(SettingRow("Resource Mode", "Auto (recommended) balances throughput and computer responsiveness. Performance probes higher; Low impact prioritises other apps.", _settingsResourceMode));
        _settingsConcurrentFiles = new TextBox { Text = transferPreferences.MaximumConcurrentFiles.ToString(), MinWidth = 110,
            ToolTip = "0 uses the mode's automatic ceiling. Enter 1–1024 to lower or raise it. This is a ceiling, not a promised number of active files; provider, dependency and computer limits still apply." };
        System.Windows.Automation.AutomationProperties.SetName(_settingsConcurrentFiles, "Maximum concurrent files");
        _settingsConcurrentFiles.TextChanged += (_, _) => SettingsDraftChanged();
        sync.Children.Add(SettingRow("Maximum Concurrent Files", "Across all pairs. 0 = automatic; 1–1024 = your ceiling. Raising it helps only while useful throughput improves.", _settingsConcurrentFiles));
        _settingsResourceStatus = Text(_authorityHost.TransferResourceStatus.Description, 12, brush: "MutedInk");
        _settingsResourceStatus.TextWrapping = TextWrapping.Wrap;
        sync.Children.Add(SettingRow("Current Recommendation", "Start with Auto and automatic concurrency. The current session learns from completed files; no background speed test runs. Live decisions also appear in each run's operation log.", _settingsResourceStatus));
        sync.Children.Add(SettingRow("Upload Limit", "KiB/s across transfers; 0 means unlimited.", Detach(UploadLimitBox)));
        sync.Children.Add(SettingRow("Download Limit", "KiB/s across transfers; 0 means unlimited.", Detach(DownloadLimitBox)));
        sync.Children.Add(SettingRow("Recovery Retention", "Managed separately for each folder pair.",
            Button("Manage Pairs", (_, _) => NavigateTo("Pairs"), true,
                explanation: "Open Folder Pairs to choose how long each pair keeps recovery copies. No retention setting changes until you save that pair.")));
        // Decision 76: the interval is kept in the existing transfer settings and this draft; returning
        // an opened online-only file to a pointer is separate engine work.
        sync.Children.Add(SettingsGroupHeading("Online-only"));
        _settingsOnlineOnlyReturn = new ComboBox { ItemsSource = new[] { "1 day", "1 hour", "1 week", "Never" },
            SelectedItem = transferPreferences.OnlineOnlyReturnInterval, MinWidth = 150,
            ToolTip = "Saved preference only for now. Automatic return of opened online-only files arrives with cloud-files slice 6 (B-192)." };
        System.Windows.Automation.AutomationProperties.SetName(_settingsOnlineOnlyReturn,
            "Return opened online-only files to the cloud after");
        _settingsOnlineOnlyReturn.SelectionChanged += (_, _) => SettingsDraftChanged();
        sync.Children.Add(SettingRow("Return opened online-only files to the cloud after:",
            "Default: 1 day. Saved now; automatic return is pending cloud-files slice 6 (B-192). Never keeps opened files on this PC.",
            _settingsOnlineOnlyReturn));
        sync.Children.Add(SettingsGroupHeading("Fast File Discovery"));
        sync.Children.Add(BuildDiscoverySettings());
        _settingsTabs.Items.Add(new TabItem { Header = "Sync & Performance", Content = FormPage(sync) });

        var notifications = new StackPanel();
        notifications.Children.Add(SettingRow("Needs Attention", "Conflicts and confirmed folder or connection problems.", DraftToggle("problems", "Notify About Problems", _workspacePreferences.NotifyProblems)));
        notifications.Children.Add(SettingRow("Sync Completed", "A summary when a run finishes.", DraftToggle("completed", "Notify About Completed Runs", _workspacePreferences.NotifyCompleted)));
        notifications.Children.Add(SettingRow("Transfer Problems", "An operation cannot continue safely.", DraftToggle("transfers", "Notify About Transfer Problems", _workspacePreferences.NotifyTransfers)));
        notifications.Children.Add(SettingRow("Additional Alert Sound", "Windows controls notification sounds separately.", DraftToggle("sound", "Additional Alert Sound", _workspacePreferences.NotificationSound)));
        // Notifications carries preferences only. Opening the tray is navigation, which the sidebar
        // already provides, so it is not repeated here as a preference row.
        _settingsTabs.Items.Add(new TabItem { Header = "Notifications", Content = FormPage(notifications) });

        // Explorer registration reports its live state, and both of its actions report their own
        // busy, success and failure result on this page instead of on the dashboard.
        _settingsExplorerTab = new TabItem { Header = "Explorer", Content = BuildExplorerSettings() };
        _settingsTabs.Items.Add(_settingsExplorerTab);
        _settingsTabs.Items.Add(new TabItem { Header = "About & Updates", Content = BuildAboutUpdatesSettings() });
        _settingsTabs.Items.Add(new TabItem { Header = "Diagnostics", Content = BuildDiagnosticsSettings() });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _settingsDiscard = Button("Discard Changes", (_, _) => DiscardSettingsCategory(_settingsTabs.SelectedIndex), true);
        CommandIcons.SetUnsavedAction(_settingsDiscard, "Discard Changes", UnsavedActionIntent.Discard);
        _settingsSave = AsyncButton("Save Changes", async (_, _) => await SaveSettingsCategoryAsync(_settingsTabs.SelectedIndex));
        _settingsSave.Style = (Style)FindResource("PrimaryButton");
        ActionAvailability.Bind(_settingsSave, () => _settingsSaving ? "Your settings changes are being saved."
            : "There are no valid unsaved changes in this settings section. Check the displayed validation message.");
        ActionAvailability.Bind(_settingsDiscard, () => _settingsSaving ? "Your settings changes are being saved; wait before discarding edits."
            : "There are no unsaved changes in this settings section.");
        actions.Children.Add(_settingsDiscard); actions.Children.Add(_settingsSave);
        _settingsFeedback = Text("", 12, brush: "MutedInk"); _settingsFeedback.MinHeight = 17; _settingsFeedback.Visibility = Visibility.Hidden;
        // Keep transient save feedback below the compact header, independent of its
        // concrete layout type. Header actions must participate in its wrapping.
        var settingsBody = new Grid();
        settingsBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        settingsBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        settingsBody.Children.Add(_settingsFeedback);
        Grid.SetRow(_settingsTabs, 1); settingsBody.Children.Add(_settingsTabs);
        var page = PageShell("Settings", "App-wide preferences", settingsBody, null, [actions]);
        SettingsPage.Child = page;
        for (var category = 0; category < 3; category++) _settingsBaselines[category] = CaptureSettingsCategory(category);
        _settingsTabs.SelectedIndex = 0;
        _settingsTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _settingsTabs)) return;
            // Explorer registration is an external Windows fact, so it is re-read when the tab is
            // opened rather than cached from the last time the user looked at it.
            if (ReferenceEquals(_settingsTabs.SelectedItem, _settingsExplorerTab)) RefreshExplorerSettingsStatus();
            RefreshSettingsDraftHeader();
        };
        _settingsReady = true;
        RefreshSettingsDraftHeader();
    }

    private object CaptureSettingsCategory(int category) => category switch
    {
        0 => new GeneralDraft(_settingsAppearance.SelectedItem?.ToString() ?? "System", StartWithWindowsBox.IsChecked == true,
            _settingsToggles["running"].IsChecked == true, _settingsDensity.SelectedIndex == 1,
            _settingsIconStyle.SelectedItem?.ToString() ?? "Combined"),
        1 => new TransfersDraft(AutomationEnabledBox.IsChecked == true, AutomationIntervalBox.Text,
            ConcurrentPairsBox.Text, UploadLimitBox.Text, DownloadLimitBox.Text,
            _settingsResourceMode.SelectedItem?.ToString() ?? "Auto", _settingsConcurrentFiles.Text,
            _settingsOnlineOnlyReturn.SelectedItem?.ToString() ?? SyncTransferSettings.DefaultOnlineOnlyReturnInterval),
        2 => new NotificationsDraft(_settingsToggles["problems"].IsChecked == true, _settingsToggles["completed"].IsChecked == true,
            _settingsToggles["transfers"].IsChecked == true, _settingsToggles["sound"].IsChecked == true),
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private bool SettingsCategoryDirty(int category) => _settingsReady && category is >= 0 and < 3 &&
        !Equals(_settingsBaselines[category], CaptureSettingsCategory(category));

    private void SettingsDraftChanged()
    {
        if (!_settingsReady || _settingsLoading) return;
        if (_settingsFeedback.Text == "Changes Saved") _settingsFeedback.Visibility = Visibility.Hidden;
        _themeManager.PreviewPreference(_settingsAppearance.SelectedItem?.ToString() ?? "System");
        BrandedItemImages.ApplyStyle(_settingsIconStyle.SelectedItem?.ToString() ?? "Combined");
        _homeFeed.RowHeight = ActivityGrid.RowHeight = _settingsDensity.SelectedIndex == 1 ? 60 : 76;
        RefreshSettingsDraftHeader();
    }

    private void RefreshSettingsDraftHeader()
    {
        if (!_settingsReady) return;
        _settingsResourceStatus.Text = _authorityHost.TransferResourceStatus.Description;
        var dirty = SettingsCategoryDirty(_settingsTabs.SelectedIndex);
        _settingsSave.Visibility = _settingsDiscard.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        _settingsSave.IsEnabled = _settingsDiscard.IsEnabled = !_settingsSaving;
        var names = new[] { "General", "Sync & Performance", "Notifications" };
        for (var i = 0; i < names.Length; i++)
        {
            var tab = (TabItem)_settingsTabs.Items[i];
            tab.Header = names[i];
            tab.ToolTip = SettingsCategoryDirty(i) ? names[i] + ": Unsaved Changes" : names[i];
            tab.FontWeight = SettingsCategoryDirty(i) ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _settingsSave.ToolTip = "Save changes in " + (_settingsTabs.SelectedIndex < 3 ? names[Math.Max(0, _settingsTabs.SelectedIndex)] : "this category") + ". Other drafts are kept separate.";
        _settingsDiscard.ToolTip = "Restore this category's saved settings. Completed sync operations are not undone.";
    }

    private void DiscardSettingsCategory(int category)
    {
        if (category is < 0 or > 2) return;
        _settingsLoading = true;
        try
        {
            switch (_settingsBaselines[category])
            {
                case GeneralDraft value:
                    _settingsAppearance.SelectedItem = value.Theme; StartWithWindowsBox.IsChecked = value.Startup;
                    _settingsToggles["running"].IsChecked = value.KeepRunning; _settingsDensity.SelectedIndex = value.Compact ? 1 : 0;
                    _settingsIconStyle.SelectedItem = value.IconStyle;
                    break;
                case TransfersDraft value:
                    AutomationEnabledBox.IsChecked = value.Automatic; AutomationIntervalBox.Text = value.Interval;
                    ConcurrentPairsBox.Text = value.Concurrent; UploadLimitBox.Text = value.Upload; DownloadLimitBox.Text = value.Download;
                    _settingsResourceMode.SelectedItem = value.ResourceMode; _settingsConcurrentFiles.Text = value.ConcurrentFiles;
                    _settingsOnlineOnlyReturn.SelectedItem = value.OnlineOnlyReturn;
                    break;
                case NotificationsDraft value:
                    _settingsToggles["problems"].IsChecked = value.Problems; _settingsToggles["completed"].IsChecked = value.Completed;
                    _settingsToggles["transfers"].IsChecked = value.Transfers; _settingsToggles["sound"].IsChecked = value.Sound;
                    break;
            }
        }
        finally { _settingsLoading = false; }
        _settingsFeedback.Visibility = Visibility.Hidden;
        SettingsDraftChanged();
    }

    private async Task<bool> SaveSettingsCategoryAsync(int category)
    {
        if (!SettingsCategoryDirty(category)) return true;
        if (_settingsSaving) return false;
        _settingsSaving = true; RefreshSettingsDraftHeader();
        await _workspacePreferencesGate.WaitAsync();
        try
        {
            var draft = CaptureSettingsCategory(category);
            var priorPreferences = _workspacePreferencesStore.Load();
            var priorAutomation = _authorityHost.LoadAutomation();
            switch (draft)
            {
                case GeneralDraft value:
                    var startupChanged = value.Startup != priorAutomation.StartWithWindows;
                    try
                    {
                        if (startupChanged) SetDraftStartupRegistration(value.Startup);
                        await _authorityHost.SaveAutomationAsync(priorAutomation with { StartWithWindows = value.Startup });
                        // StatusIconStyle is the one status-icon preference; the General draft reads the
                        // same value back on the next launch, so the choice survives restart.
                        await _workspacePreferencesStore.SaveAsync(priorPreferences with { Theme = value.Theme,
                            KeepRunningWhenClosed = value.KeepRunning, CompactRows = value.Compact, StatusIconStyle = value.IconStyle });
                    }
                    catch
                    {
                        if (startupChanged) SetDraftStartupRegistration(priorAutomation.StartWithWindows);
                        await _authorityHost.SaveAutomationAsync(priorAutomation);
                        throw;
                    }
                    _themeManager.PreviewPreference(value.Theme);
                    break;
                case NotificationsDraft value:
                    await _workspacePreferencesStore.SaveAsync(priorPreferences with { NotifyProblems = value.Problems,
                        NotifyCompleted = value.Completed, NotifyTransfers = value.Transfers, NotificationSound = value.Sound });
                    break;
                case TransfersDraft value:
                    if (!int.TryParse(value.Interval, out var interval) || interval < SyncAutomationStore.MinimumIntervalMinutes || interval > SyncAutomationStore.MaximumIntervalMinutes)
                        throw new InvalidDataException("Safety Check Interval must be between 1 and 1440 minutes.");
                    if (!int.TryParse(value.Concurrent, out var concurrent) || !int.TryParse(value.Upload, out var upload) || !int.TryParse(value.Download, out var download) ||
                        !int.TryParse(value.ConcurrentFiles, out var concurrentFiles))
                        throw new InvalidDataException("Transfer controls must be whole numbers.");
                    // Start from the saved settings so fields this page does not edit (Recovery) are kept.
                    var transfers = SyncTransferSettings.Normalize(_authorityHost.LoadTransferSettings() with
                    {
                        MaximumConcurrentPairs = concurrent, UploadKiBPerSecond = upload,
                        DownloadKiBPerSecond = download, ResourceMode = value.ResourceMode,
                        MaximumConcurrentFiles = concurrentFiles,
                        OnlineOnlyReturnInterval = value.OnlineOnlyReturn
                    });
                    var automation = priorAutomation with { Enabled = value.Automatic, IntervalMinutes = interval };
                    await _authorityHost.SaveAutomationAsync(automation);
                    try { await _authorityHost.SaveTransferSettingsAsync(transfers); }
                    catch { await _authorityHost.SaveAutomationAsync(priorAutomation); throw; }
                    ConfigureAutomationTimer(automation);
                    foreach (var row in PairRows) row.UpdateGlobalLiveSync(automation.Enabled);
                    // The save is durable even when an unavailable provider prevents monitor activation.
                    try { await ConfigureLocalMonitorsAsync(automation); }
                    catch (Exception e) { ShowStatus("Settings saved. Monitoring needs attention: " + e.Message); }
                    break;
            }
            _workspacePreferences = _workspacePreferencesStore.Load();
            _settingsBaselines[category] = draft;
            _settingsFeedback.Text = "Changes Saved"; _settingsFeedback.Visibility = Visibility.Visible;
            RefreshWorkspace(); return true;
        }
        catch (Exception e)
        {
            _settingsFeedback.Text = "Changes were not saved: " + e.Message;
            _settingsFeedback.Visibility = Visibility.Visible;
            return false;
        }
        finally { _workspacePreferencesGate.Release(); _settingsSaving = false; RefreshSettingsDraftHeader(); }
    }

    private void SetDraftStartupRegistration(bool enabled)
    {
        if (StartupRegistrationForTest is not null) StartupRegistrationForTest(enabled);
        else WindowsStartupRegistration.SetEnabled(enabled);
    }

    private bool GuardWorkspaceNavigation(string page, bool updateSelection)
    {
        if (_restoringNavigation || _navigationSaving) return false;
        if (!_workspaceReady || page == _workspacePage) return true;
        var settingsDirty = _workspacePage == "Settings" && Enumerable.Range(0, 3).Any(SettingsCategoryDirty);
        var pairDirty = _workspacePage == "Pairs" && PairLocationDraftActive && CurrentPairEditorHasUnsavedChanges();
        if (!settingsDirty && !pairDirty) return true;
        var choice = UnsavedNavigationChoiceForTest?.Invoke() ?? AppDialogWindow.Choose(this, "Unsaved Changes",
            "Save changes before leaving?", settingsDirty
        ? "Save the edited Settings categories, discard their drafts, or stay here."
        : "Save this folder pair, discard its draft, or stay here.",
        "Save Changes", "Discard Changes", "Stay Here", secondaryIsDiscard: true);
        if (choice == AppDialogChoice.Secondary)
        {
            if (settingsDirty) for (var i = 0; i < 3; i++) DiscardSettingsCategory(i);
            if (pairDirty) DiscardCurrentPairDraft();
            return true;
        }
        RestoreWorkspaceSelection();
        if (choice == AppDialogChoice.Primary) _ = SaveBeforeNavigationAsync(page, updateSelection, settingsDirty);
        return false;
    }

    private async Task SaveBeforeNavigationAsync(string page, bool updateSelection, bool settings)
    {
        _navigationSaving = true;
        var saved = true;
        try
        {
            if (settings)
            {
                for (var i = 0; i < 3; i++) if (!await SaveSettingsCategoryAsync(i)) { saved = false; break; }
            }
            else saved = await SavePairAsync();
        }
        catch (Exception e) { saved = false; ShowStatus("Changes could not be saved: " + e.Message); }
        finally { _navigationSaving = false; }
        if (saved) NavigateTo(page, updateSelection);
    }

    private void RestoreWorkspaceSelection()
    {
        _restoringNavigation = true;
        try
        {
            Navigation.SelectedItem = Navigation.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _workspacePage);
            if (_utilityNavigation is not null) _utilityNavigation.SelectedItem = _utilityNavigation.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _workspacePage);
        }
        finally { _restoringNavigation = false; }
    }
}
