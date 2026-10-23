using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using System.ComponentModel;
using Massdex.SyncShark.V2;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace Massdex.SyncShark;

public partial class MainWindow : Window
{
    internal static Func<TimeSpan, CancellationToken, Task<bool>>? ExitCheckpointWaitForTest { get; set; }
    internal static Action<string>? ClipboardTextSetterForTest { get; set; }
    internal static Func<string, CancellationToken, Task<SyncFolderSelectionTree>>? PairHealthLoaderForTest { get; set; }

    /// <summary>
    /// W2-C correction 4 (root387): the ONE internal INSTANCE-SCOPED test input boundary for the
    /// version-checked folder-selection load. It is null in production, and null or default calls the exact
    /// existing real Host path with the same pair id, cancellation and scheduling, so production behaviour
    /// is unchanged when nothing is assigned. It supplies asynchronous INPUT only: publication remains
    /// decided entirely by the real version, selection, error and finally logic in
    /// LoadFolderSelectionPageAsync. It is not a static hook, has no public, CLI or config activation and
    /// adds no persisted setting, IPC, service, endpoint or authentication change.
    /// </summary>
    internal Func<string, CancellationToken, Task<SyncFolderSelectionTree>>? FolderSelectionTreeLoaderForTest
    {
        get; set;
    }
    internal static Func<string, Task<IReadOnlyList<SyncAuthorityMonitorProjection>>>? MonitorProjectionLoaderForTest { get; set; }
    internal static Func<IReadOnlyList<SyncPair>, string, Task>? PairRunDispatcherForTest { get; set; }
    // SS-D01 diagnostic seam: a queued UI invocation pauses here BEFORE it hands its own live cancellation
    // token to the real host method, so a check can hold the invocation pre-host, drive the real Stop control
    // and then release it. It cannot change the decision under test: the token it receives is the invocation's
    // own token, the real host call still runs afterwards, and the hold performs no provider work itself.
    internal static Func<string, CancellationToken, Task>? QueuedInvocationHoldForTest { get; set; }
    internal static Func<SyncPairBindingRequest, Task<SyncPairBindingResult>>? PairBindingForTest { get; set; }
    internal static Func<Task<bool>>? DropboxConnectionCheckForTest { get; set; }
    internal static Func<bool, Task<DropboxConnectionStatus>>? DropboxConnectionStatusLoaderForTest { get; set; }
    internal static Func<Task>? DropboxReconnectForTest { get; set; }
    internal static Func<Task>? DropboxDisconnectForTest { get; set; }
    internal static Func<string, Task>? PairRetirementFollowUpForTest { get; set; }
    internal static Func<PairRow, bool>? PairRetirementConfirmationForTest { get; set; }
    internal static Func<string, bool>? BootstrapApprovalConfirmationForTest { get; set; }
    internal static Func<string, CancellationToken, Task<SyncPair>>? BootstrapApprovalForTest { get; set; }
    internal static Func<SyncBootstrapActionPlan, CancellationToken, Task<SyncBootstrapActionResult>>?
        BootstrapActionPlanForTest { get; set; }
    internal static Func<string, string?, string?, string?, string?, Task<IReadOnlyList<SyncBootstrapReviewItem>>>?
        BootstrapReviewLoaderForTest { get; set; }
    internal static Func<string, int, int, string?, string?, string?, string?, Task<SyncBootstrapReviewPage>>?
        BootstrapReviewPageLoaderForTest { get; set; }
    internal static Func<int, int, string?, string?, string?, string?, DateTimeOffset?, string?,
        Task<IReadOnlyList<SyncAuthorityActivityProjection>>>? ActivityProjectionLoaderForTest { get; set; }
    internal static Func<Task<int>>? PendingQueueCountLoaderForTest { get; set; }
    internal static Func<IReadOnlyList<SyncPair>, Func<Task<bool>>, Func<string, Task>,
        Task<IReadOnlyList<SyncAuthorityMonitorStartResult>>>? MonitorConfiguratorForTest { get; set; }
    internal static Func<Task>? MonitorStopperForTest { get; set; }
    internal static Func<AppDialogChoice>? UnsavedDraftRunChoiceForTest { get; set; }
    internal static Func<CancellationToken, Task<IDropboxFolderBrowserService>>? PairFolderBrowserFactoryForTest { get; set; }
    internal static Func<CancellationToken, Task<DropboxFolderEntry>>? RecommendedRecoveryFolderForTest { get; set; }
    internal static Func<DropboxFolderBrowserWindow, DropboxFolderEntry?>? RecoveryFolderDialogForTest { get; set; }
    // UI397_A: the endpoint editor's cloud browse opens the same modal through the same production call. A
    // null value opens the real window and reads the real selection exactly as before; the narrow seam exists
    // only so a fixture can observe the exact label the real handler passes to that modal.
    internal static Func<DropboxFolderBrowserWindow, DropboxFolderEntry?>? PairEndpointFolderBrowserDialogForTest { get; set; }
    // UI16: bounded seam for the recovery page fetch. It exists so a fixture can prove the
    // superseded-page guard with a controlled completion order; a null value uses the real host.
    internal static Func<string, int, string?, bool, int, string?, CancellationToken,
        Task<SyncRecoveryCatalogPage>>? RecoveryCatalogLoaderForTest { get; set; }
    internal static Func<string?, IReadOnlyList<LocalFolderNode>>? LocalRootNodeFactoryForTest { get; set; }
    // UF4: two narrow, default-null fixture inputs for the two explicit inline-feedback actions. A null
    // value calls the exact existing Host member with the same arguments and scheduling, so production
    // behaviour is unchanged. The isolated visual fixture has no connected provider, so it substitutes a
    // controlled loader only to drive the real handler's loading, result and refusal feedback, and to prove
    // that an empty prerequisite never reaches a provider loader at all.
    internal static Func<string, string, Task<SyncConflictContent>>? ConflictContentLoaderForTest { get; set; }
    // Delay only the read boundary in fixtures; the production host still performs the actual query.
    internal static Func<Task>? ConflictPageCheckpointForTest { get; set; }
    internal static Func<Task>? ConflictSelectionCheckpointForTest { get; set; }
    internal static Func<string, string, Task<IReadOnlyList<SyncRecoveryCatalogItem>>>? RevisionCatalogLoaderForTest { get; set; }
    internal static TimeSpan? PairHealthTimeoutForTest { get; set; }
    internal static bool SuppressLoadedStartupForTest { get; set; }
    internal static Func<string, Task>? StartupCheckpointForTest { get; set; }
    internal static bool StartupLocalOnlyForTest { get; set; }
    /// <summary>root729: the fixture answers the repair confirmation so the real dialog cannot block the harness;
    /// the repair itself still calls the real guarded Host action and the detector still runs for real.</summary>
    internal static Func<bool>? StartupRepairConfirmationForTest { get; set; }
    private SyncAuthorityDatabase.SyncInterruptedPublicationIncident? _startupRepairIncident;
    private string? _startupRepairDiagnostic;
    internal string? StartupRepairDiagnosticForTest => _startupRepairDiagnostic;
    private bool _startupLoading;
    private const int ProjectionPageSize = 200;
    private readonly List<ActivityRow> _liveActivityRows = [];
    private bool _liveActivityRefreshQueued;
    // Provider destinations are explicitly validated; a response must not move
    // an authorized content request to another origin behind the adapter's back.
    private readonly HttpClient _httpClient;
    private readonly ThemeManager _themeManager;
    private readonly ISyncHost _authorityHost;
    // Core v2 only: the platform's power-resume and network events, forwarded to continuous sync. Null under v1.
    private readonly ISystemWakeSource? _wakeSource;
    // The running-application CLI bridge executes every command against this exact host, so the CLI and
    // the window can never hold two runtimes or two run gates.
    internal SyncAuthorityRuntimeHost AuthorityHost => _authorityHost as SyncAuthorityRuntimeHost
        ?? throw new InvalidOperationException("The running-app CLI bridge needs core v1.");
    // The running-app CLI bridge is v1-only. This reports the host kind without throwing, so the app can
    // skip the bridge with a single log line while core v2 runs instead of catching the getter above.
    internal bool HostIsCoreV1 => _authorityHost is SyncAuthorityRuntimeHost;
    private Task _existingPairEditorInitialization = Task.CompletedTask;
    internal Task ExistingPairEditorInitializationForTest => _existingPairEditorInitialization;
    private readonly List<DecisionRow> _allRows = [];
    private readonly List<DecisionRow> _allConflictRows = [];
    private readonly List<RecoveryRow> _allRecoveryRows = [];
    private readonly Dictionary<string, string> _monitorStateByPair = new(StringComparer.Ordinal);
    private readonly HashSet<string> _attentionPairIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runFailurePairIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _runFailureReportedUtcByPair = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _endpointAttentionReasonByPair = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _monitorAttentionReasonByPair = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _runAttentionReasonByPair = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _monitorConfigurationGate = new(1, 1);
    private int _monitorConfigurationGeneration;
    private readonly Dictionary<string, SyncFolderSelectionTree> _folderHealthByPair = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pairHealthCheckIds = new(StringComparer.Ordinal);
    private readonly List<FolderSelectionRow> _folderSelectionSourceRows = [];
    private readonly DispatcherTimer _automationTimer = new();
    private readonly DispatcherTimer _recoverySearchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _folderSelectionSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _bootstrapSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly CancellationTokenSource _pairRootBrowserLifetime = new();
    private SyncPairSet _pairSet = new([], null);
    private DropboxFolderEntry? _selectedDropboxFolder;
    private DropboxFolderEntry? _selectedRecoveryFolder;
    private SyncAccountStatus? _connectionStatus;
    private SyncRewindSession? _selectedRewindSession;
    private Uri? _rewindFolderUrl;
    private bool _uiRunActive;
    private bool _readingRunOutcomes;
    // CLI work shares this host but does not enter a button handler. Existing UI guards must also
    // observe the actual host, not just the window's own operation flag.
    private bool _runActive
    {
        get => _uiRunActive || _authorityHost?.HasActiveRun == true;
        set => _uiRunActive = value;
    }
    // SS-D01: the UI invocation that is queued or already running on a worker thread before - or outside -
    // a host run of its own. Its token is created BEFORE _runActive is set and before the work is queued,
    // so a stop reaches first-sync work that the host cannot see yet: the actual host/fixture call receives
    // this token instead of CancellationToken.None. Only the owning invocation clears it.
    private CancellationTokenSource? _pendingRunCancellation;
    private bool _closing;
    // UI12 row-body interaction: the press that began a click decides whether that activation
    // toggles the row, and the row handlers attach once with the pair page presentation.
    private string? _pairRowPressId;
    private bool _pairRowPressExpanded;
    private bool _pairRowPressWasSelected;
    private bool _pairRowPressIsBody;
    private bool _pairRowBodyHandlersAttached;
    private bool _productExitRequested;
    private bool _closeRequestQueued;
    private string? _pairValidationReason;
    private bool _themeReady;
    private bool _loadingRecovery;
    private bool _loadingFolderSelection;
    private bool _loadingBootstrap;
    // UI1: the one synchronous single-flight guard for the First Sync Start action. It is acquired before the
    // handler's first await and released only after that whole load, confirmation and run interval has ended,
    // so a second click can never enqueue a second run and a later refresh cannot re-enable Start while held.
    private bool _firstSyncApprovalInFlight;
    private CancellationTokenSource? _folderSelectionLoadCancellation;
    private int _folderSelectionLoadVersion;
    private int _folderSelectionOffset;
    private bool _folderSelectionHasMore;
    private int _bootstrapQueryVersion;
    private int _bootstrapOffset;
    private bool _bootstrapHasMore;
    private int _bootstrapTotalItems;
    private int _bootstrapLocalOnlyItems;
    private int _bootstrapDropboxOnlyItems;
    private int _bootstrapConflictItems;
    private bool _bootstrapInventoryComplete;
    private string? _bootstrapLocalScanGenerationId;
    private string? _bootstrapDropboxScanGenerationId;
    private readonly Dictionary<BootstrapActionKey, BootstrapChoiceState> _bootstrapActionOverrides = [];
    private string _folderSelectionBaseSummary = "Save and select a pair to load its folder hierarchy.";
    private int _activityQueryVersion;
    private int _activityRetainedOffset;
    private int _globalPendingQueueCount;
    private int _queueQueryVersion;
    private int _conflictQueryVersion;
    private IReadOnlyList<SyncConflictSelection> _matchingConflictSelection = [];
    private string _matchingConflictGroup = "All groups";
    private string _matchingConflictSearch = string.Empty;
    private int _recoveryQueryVersion;
    private int _recoveryArchiveOffset;
    private string? _recoveryDeletedCursor;
    private bool _recoveryHasMoreArchives;
    private bool _recoveryHasMoreDeleted;
    private bool _recoveryDeletedRequested;
    private bool _recoveryReloadPending;
    private string? _recoverySelectedPairId;
    private bool _selectingRecoveryPair;
    private bool _sidebarCollapsed;
    private HwndSource? _windowSource;
    private IDropboxFolderBrowserService? _pairDropboxBrowser;
    private int _pairDropboxRootLoadVersion;
    private CancellationTokenSource? _pairDraftCancellation;
    private int _pairEditorGeneration;
    private int _pairDraftPreviewVersion;
    private bool _editingNewPair;
    private string? _editingPairRootId;
    private ContentControl? _activePairEditorHost;
    private DropboxFolderNode? _selectedDropboxRootNode;
    private string? _draftBrowseLocalRoot;
    private DropboxFolderEntry? _draftBrowseDropboxRoot;
    private SyncPairDraftPreview? _draftPreview;
    private IReadOnlyList<SyncSelectionRule> _draftSelectionRules = [];
    private bool _draftSelectionDefaultIncluded;
    private string? _pairDraftConnectionId;
    private string? _duplicateSourcePairId;
    private string? _expandedPairId;
    private bool _suppressPairSelection;
    private readonly FolderTreeSelectionDraft _localGlobalSelection =
        new(FolderTreePathKind.Windows);
    private readonly FolderTreeSelectionDraft _dropboxGlobalSelection =
        new(FolderTreePathKind.Dropbox);
    private PairDraftBindingPlan? _globalDraftPlan;
    private bool _updatingGlobalTreeSelection;
    private bool _replaceUnavailableLocalOnNextCheck;
    private bool _replaceUnavailableDropboxOnNextCheck;
    private bool _recoveryPreparationFailed;
    private bool _responsiveUpdatePending;

    public ObservableCollection<DecisionRow> VisibleRows { get; } = [];
    public ObservableCollection<ActivityRow> ActivityRows { get; } = [];
    public ObservableCollection<ActivityPairChoice> ActivityPairChoices { get; } = [];
    public ObservableCollection<DecisionRow> ConflictRows { get; } = [];
    public ObservableCollection<PairRow> PairRows { get; } = [];
    public ObservableCollection<QueueRow> QueueRows { get; } = [];
    public ObservableCollection<FolderSelectionRow> FolderSelectionRows { get; } = [];
    public ObservableCollection<FolderSelectionNode> FolderSelectionRoots { get; } = [];
    public ObservableCollection<PairPreviewRow> PairPreviewRows { get; } = [];
    public ObservableCollection<BootstrapRow> BootstrapRows { get; } = [];
    public ObservableCollection<RecoveryRow> RecoveryRows { get; } = [];
    public ObservableCollection<SyncRecoveryPairChoice> RecoveryPairChoices { get; } = [];
    public ObservableCollection<RewindRow> RewindRows { get; } = [];
    public ObservableCollection<LocalFolderNode> LocalRootNodes { get; } = [];
    public ObservableCollection<DropboxFolderNode> DropboxRootNodes { get; } = [];

    public MainWindow(PilotStateAuthority stateAuthority, ThemeManager themeManager, bool useCoreV2 = false)
        : this(stateAuthority, themeManager, useCoreV2,
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                // Above the explicit batch operation timeout, so a slow finish_batch_v2 or delete_batch is never cut
                // by the transport's implicit 100-second default.
                Timeout = EngineComposition.DropboxHttpTimeoutFor(V2Settings.Default)
            })
    {
    }

    private MainWindow(PilotStateAuthority stateAuthority, ThemeManager themeManager, bool useCoreV2,
        HttpClient httpClient)
        : this(stateAuthority, themeManager, httpClient,
            useCoreV2
                ? V2SyncHost.OpenProduction(stateAuthority, httpClient)
                : new SyncAuthorityRuntimeHost(stateAuthority, httpClient))
    {
    }

    // Fixtures supply the complete runtime boundary, so a forgotten action hook cannot fall back
    // to a production transport or credential store. The window owns these supplied dependencies.
    internal MainWindow(PilotStateAuthority stateAuthority, ThemeManager themeManager, HttpClient httpClient,
        ISyncHost authorityHost)
    {
        stateAuthority.VerifyAuthority();
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _authorityHost = authorityHost ?? throw new ArgumentNullException(nameof(authorityHost));
        _authorityHost.AuthorizationReturnRequested += AuthorizationReturnRequested;
        _authorityHost.AuthorizationAttemptChanged += AuthorizationAttemptChanged;
        if (_authorityHost is V2SyncHost v2Host)
        {
            // Continuous sync drives the tray colour and the automatic-run balloon, and wakes on power resume and a
            // returning network. Under core v1 the host is not a V2SyncHost, so none of this is wired.
            v2Host.AutoSyncChanged += OnAutoSyncChanged;
            var wakeSource = new SystemWakeSource();
            v2Host.WatchSystemWakes(wakeSource);
            _wakeSource = wakeSource;
        }
        _workspacePreferencesStore = new UiPreferencesStore(stateAuthority);
        _workspacePreferences = _workspacePreferencesStore.Load();
        BrandedItemImages.ApplyStyle(_workspacePreferences.StatusIconStyle);
        _themeManager = themeManager;
        InitializeComponent();
        LocalPathEntryBox.TextChanged += (_, _) => UpdateLocalPathAvailability();
        UpdateLocalPathAvailability();
        _activePairEditorHost = NewPairEditorHost;
        DataContext = this;
        PairEditorPanel.DataContext = this;
        SelectTheme(_themeManager.Preference);
        _themeReady = true;
        LoadAutomation();
        LoadTransferSettings();
        SetRunControls();
        UpdateResponsiveLayout();
        UpdateSidebarPresentation();
        BuildWorkspace();
        BuildFullApp();
        BindActionExplanations();
        RefreshConflictActionPresentation();
        _automationTimer.Tick += AutomationTimer_Tick;
        _recoverySearchTimer.Tick += async (_, _) =>
        {
            _recoverySearchTimer.Stop();
            if (!_loadingRecovery && RecoveryPage.Visibility == Visibility.Visible)
                await LoadRecoveryPageAsync(reset: true, _recoveryDeletedRequested);
        };
        _folderSelectionSearchTimer.Tick += (_, _) =>
        {
            _folderSelectionSearchTimer.Stop();
            if (PairsPage.Visibility == Visibility.Visible)
            {
                ApplyFolderSelectionFilterAndBuild();
                ApplyGlobalFolderTreeFilter();
            }
        };
        _bootstrapSearchTimer.Tick += async (_, _) =>
        {
            _bootstrapSearchTimer.Stop();
            if (!_loadingBootstrap && BootstrapPage.Visibility == Visibility.Visible)
                await RefreshBootstrapReviewAsync();
        };
        Loaded += MainWindow_Loaded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _authorityHost.AuthorizationReturnRequested -= AuthorizationReturnRequested;
        _authorityHost.AuthorizationAttemptChanged -= AuthorizationAttemptChanged;
        _closing = true;
        _operationRetryReadCancellation?.Cancel();
        // AUTH2: window-lifetime cancellation is the shared exact-target attempt operation, not a
        // window-owned token. The attempt id is captured before the host is released.
        if (_observedAuthorizationAttemptId is { } pendingAttempt)
            _authorityHost.RequestAuthorizationAttemptCancellation(pendingAttempt);
        _folderSelectionLoadCancellation?.Cancel();
        _folderSelectionLoadCancellation?.Dispose();
        _pairDraftCancellation?.Cancel();
        _pairDraftCancellation?.Dispose();
        _pairRootBrowserLifetime.Cancel();
        _automationTimer.Stop();
        _recoverySearchTimer.Stop();
        _folderSelectionSearchTimer.Stop();
        _bootstrapSearchTimer.Stop();
        _windowSource?.RemoveHook(WindowMessageHook);
        _workspaceTimer.Stop();
        if (_authorityHost is V2SyncHost v2Host) v2Host.AutoSyncChanged -= OnAutoSyncChanged;
        _wakeSource?.Dispose();
        _authorityHost.Dispose();
        _httpClient.Dispose();
        _pairRootBrowserLifetime.Dispose();
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_productExitRequested)
        {
            e.Cancel = true;
            // Alt+F4 is already inside WPF's close operation. A synchronously
            // completed checkpoint must not call Close again before it unwinds.
            if (!_closeRequestQueued)
            {
                _closeRequestQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    _closeRequestQueued = false;
                    if (_productExitRequested) return;
                    if (_workspacePreferences.KeepRunningWhenClosed) HideToBackground();
                    else Close_Click(this, new RoutedEventArgs());
                }));
            }
            return;
        }
        base.OnClosing(e);
    }

    public void StartHidden()
    {
        Show();
        ShowInTaskbar = false;
        Hide();
    }

    private void AuthorizationReturnRequested()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_closing) ShowFromBackground(); }));
    }

    public void ShowFromBackground()
    {
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void RequestProductExit()
    {
        _productExitRequested = true;
        Close();
    }

    public async Task PrepareProductExitAsync()
    {
        if (_changingPairStorage)
            throw new InvalidOperationException("Wait for the current storage change before exiting Sync Shark.");
        if (!TryCloseSharingEditor())
            throw new InvalidOperationException("Finish the sharing operation or discard its unsaved changes before exiting Sync Shark.");
        _closing = true;
        _automationTimer.Stop();
        try
        {
            await _authorityHost.StopMonitorsAsync();
            // SS-D01: the queued UI invocation is signalled before the host stop and the exit checkpoint,
            // so first-sync work the host has not registered as a run cannot outlive this exit attempt.
            CancelPendingRun();
            if (_runActive) await _authorityHost.StopActiveRunAsync();
            var checkpointReached = ExitCheckpointWaitForTest is null
                ? await _authorityHost.WaitForActiveRunCheckpointAsync(TimeSpan.FromSeconds(30))
                : await ExitCheckpointWaitForTest(TimeSpan.FromSeconds(30), CancellationToken.None);
            if (!checkpointReached)
                throw new TimeoutException(
                    "The active sync did not reach its durable stop checkpoint within 30 seconds. " +
                    "Sync Shark is still running; retry Exit after the current provider operation finishes.");
            _productExitRequested = true;
            Close();
        }
        catch
        {
            _closing = false;
            _productExitRequested = false;
            var settings = _authorityHost.LoadAutomation();
            ConfigureAutomationTimer(settings);
            try { await ConfigureLocalMonitorsAsync(settings); }
            catch { /* Preserve the original safe-exit failure for the user. */ }
            throw;
        }
    }

    public async Task SyncNowFromBackgroundAsync()
    {
        if (_runActive) return;
        if (!await AdmitManualSyncWithDraftAsync()) return;
        _pairSet = await _authorityHost.LoadPairsAsync();
        var enabled = _pairSet.Pairs.Where(pair => pair.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            ShowStatus("Sync now has no automatically enabled pairs.");
            return;
        }
        await RunPairsAsync(enabled, "Tray");
    }

    public bool AutomaticSyncEnabled => _authorityHost.LoadAutomation().Enabled;

    public async Task<bool> ToggleAutomaticSyncFromBackgroundAsync()
    {
        var prior = _authorityHost.LoadAutomation();
        var updated = prior with { Enabled = !prior.Enabled, RunOnStartup = false };
        await _authorityHost.SaveAutomationAsync(updated);
        AutomationEnabledBox.IsChecked = updated.Enabled;
        foreach (var row in PairRows) row.UpdateGlobalLiveSync(updated.Enabled);
        ConfigureAutomationTimer(updated);
        await ConfigureLocalMonitorsAsync(updated);
        AutomationStatus.Text = updated.Enabled
            ? $"Live sync is on for enabled pairs. A full safety check runs every {updated.IntervalMinutes} minutes for changes that were not reported promptly."
            : "Live sync is off for all pairs. Sync now remains available.";
        ShowStatus(updated.Enabled ? "Live sync resumed for enabled pairs." : "Live sync paused for all pairs.");
        return updated.Enabled;
    }

    private void HideToBackground()
    {
        ShowInTaskbar = false;
        Hide();
        ShowStatus("Sync Shark is still running in the notification area. Use Exit from the tray menu to stop it.");
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeButton is not null) MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (UiOperationBusy) return;
        if (_workspacePage == "Dashboard" && _workspaceAccountId is { } reconnectId && _workspaceScopeStatus is { HasProtectedCredential: false })
        {
            _selectedConnectionId = reconnectId; RefreshAccountPickers(); NavigateTo("Connections");
            ShowStatus("Reconnect this account to resume its folder pairs. Other accounts are unchanged."); return;
        }
        if (!await AdmitManualSyncWithDraftAsync()) return;
        if (_workspacePage == "Dashboard")
        {
            try
            {
                _pairSet = await _authorityHost.LoadPairsAsync();
                var targets = _pairSet.Pairs.Where(item => item.Enabled && ScopeMatches(item.Id)).ToArray();
                if (targets.Length == 0) { ShowStatus("No enabled folder pairs in this account selection."); NavigateTo("Pairs"); return; }
                await RunPairsAsync(targets, "Manual");
            }
            catch (Exception error) { ShowStatus("Sync could not start: " + error.Message); }
            return;
        }
        SyncPair? pair;
        try
        {
            _pairSet = await _authorityHost.LoadPairsAsync();
            pair = _pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, _pairSet.SelectedPairId, StringComparison.Ordinal));
        }
        catch (Exception exception) { ShowStatus("Saved pair or connection is not accepted: " + exception.Message); return; }
        if (pair is null) { ShowStatus("Bind and select one identity-bound sync pair first."); NavigateTo("Pairs"); return; }
        await RunPairsAsync([pair], "Manual");
    }

    private async Task RunPairsAsync(IEnumerable<SyncPair> pairs, string trigger)
    {
        if (GlobalPauseActive) { ShowStatus(PauseDescription + ". Resume sync before starting a run."); return; }
        // UI1: the one shared admission predicate, rechecked here after every caller's awaited preparation. A
        // pending first-sync approval already holds this pair's reviewed plan and its own authorisation, so the
        // manual, tray, explorer and scheduled routes that all enter through here never overlap it.
        if (UiOperationBusy) return;
        var requested = pairs.ToArray();
        var blocked = requested.Where(pair => IsPairKnownUnhealthy(pair.Id)).ToArray();
        var runnable = requested.Where(pair => !IsPairKnownUnhealthy(pair.Id) && !_authorityHost.IsPairAccountPaused(pair)).ToArray();
        if (runnable.Length == 0 && requested.Any(pair => _authorityHost.IsPairAccountPaused(pair)))
            ShowStatus("The selected account is paused. Resume it from Connections before syncing.");
        if (blocked.Length > 0)
        {
            var names = string.Join(", ", blocked.Select(pair => pair.Name));
            ShowStatus($"Repair {names} before syncing. Open Sync status for the blocking folder problem.");
            if (PairList.SelectedItem is PairRow selected && blocked.Any(pair => pair.Id == selected.Id))
                PairEditorTabs.SelectedIndex = 3;
        }
        if (runnable.Length == 0) return;
        // This attempt does not inherit an earlier transient callback reason.
        // Retained Problems/Activity/Runs remain untouched and independently
        // visible until a real later success resolves the current issue.
        foreach (var pair in runnable) _runAttentionReasonByPair.Remove(pair.Id);
        // SS-D01: this queued invocation owns one cancellation token, created before the busy state is
        // exposed and before the host is called, so Stop cancels this exact invocation instead of passing
        // CancellationToken.None to a run the host cannot see yet.
        var runCancellation = BeginPendingRunCancellation();
        _runActive = true;
        SetRunControls();
        RunProgress.IsIndeterminate = true;
        try
        {
            ResetRunPresentation("Checking folders", "Preparing the selected folder pairs for a fresh check…");
            QueueRows.Clear();
            UpdateQueueCount();
            if (QueuedInvocationHoldForTest is { } holdQueued) await holdQueued("Sync", runCancellation.Token);
            if (PairRunDispatcherForTest is not null)
                await PairRunDispatcherForTest(runnable, trigger);
            else
                await Task.Run(() => _authorityHost.RunPairsAsync(runnable, trigger,
                    progress => PresentBatchProgressAsync(progress, trigger, recoveryOnly: false),
                    cancellationToken: runCancellation.Token,
                    admissionProgress: message => Dispatcher.Invoke(() => ShowRunStatus(message))));
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            // SS-D01: the user's stop cancelled this queued invocation, so no transfer started and no
            // failure is reported in place of the stop that actually ended it.
            ShowRunStatus("Stop requested; the queued sync work is cancelled and no file change will start.");
        }
        catch (Exception exception)
        {
            await PresentCommandFailureAsync(exception, runnable, "Sync command");
        }
        finally
        {
            EndPendingRunCancellation(runCancellation);
            await FinishRunPresentationAsync("Sync", () => RefreshAuthorityProjectionsAsync(requireSuccess: true));
            // "Sync now" and the other manual runs re-evaluate continuous sync: a manual run can clear a hold a
            // pair's automatic loop is waiting on, and a Kick is the only way that loop learns it.
            foreach (var pair in runnable) KickV2Monitors(pair.Id);
        }
    }

    // The action is not settled until its recorded outcomes have been read back.
    // Keep the existing single-flight guard through that interval. A display
    // read failure must never rewrite a committed transfer or escape an async
    // button handler, and every caller releases its controls even on rejection.
    private async Task FinishRunPresentationAsync(string action, Func<Task> refresh,
        Action<string>? feedback = null, string? terminalFeedback = null)
    {
        // Callers pass their already-computed result before readback temporarily
        // replaces it with progress. Never infer completion from that progress.
        _readingRunOutcomes = true;
        _workspaceReadbackFailure = null;
        try
        {
            _workspaceStage = "Refreshing recorded outcomes";
            RunProgress.IsIndeterminate = true;
            SetRunControls();
            ShowRunStatus("Reading recorded outcomes for " + action + ". No new file transfer is running.");
            await refresh();
        }
        catch (Exception exception)
        {
            var detail = action + " ended, but its recorded outcomes could not be refreshed. " +
                "This display error does not change any recorded file outcome. Refresh Activity or Runs before retrying. " +
                SyncActivityText.Failure(exception);
            _workspaceReadbackFailure = detail;
            feedback?.Invoke(detail);
            await ReportActionExceptionAsync("Refresh " + action + " results", detail);
        }
        finally
        {
            _readingRunOutcomes = false;
            _runActive = false;
            RunProgress.IsIndeterminate = false;
            SetRunControls();
            // The readback's temporary status must not replace the terminal
            // cause, including in the legacy presentation before workspace
            // initialization. A display failure remains distinct from a
            // transfer failure and never changes recorded file outcomes.
            var terminalDetail = ReadbackFailureDetail(scoped: false) ??
                CommandFailureDetail(scoped: false) ?? terminalFeedback ?? AttentionProblemDetail(scoped: false) ??
                action + " has ended. Recorded outcomes are loaded; open Activity or Runs for results.";
            ShowRunStatus(terminalDetail);
            feedback?.Invoke(terminalDetail);
            if (_workspaceReady) RefreshWorkspace();
        }
    }

    private async Task PresentCommandFailureAsync(Exception exception, IEnumerable<SyncPair> attemptedPairs,
        string action)
    {
        var detail = action + " reported an error: " + SyncActivityText.Failure(exception) +
            "\nCompleted files retain their recorded outcomes. Open the operation log and any retained run outcomes before retrying.";
        _workspaceCommandFailure = (detail, attemptedPairs.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal),
            DateTimeOffset.UtcNow);
        _workspaceRunningPair = null; _workspaceRunningPairId = null;
        _workspaceStage = action + " needs attention";
        ShowRunStatus(detail);
        // Also covers admission failures with no accepted run. Do not invent
        // a run/file identity or replace the exact cause with its type.
        await ReportActionExceptionAsync(action, detail);
    }

    private Task PresentBatchProgressAsync(SyncAuthorityBatchProgress progress, string trigger,
        bool recoveryOnly)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            QueueExplorerStatus(progress);
            // Terminal batch outcomes may carry the previous comparison as last-target context.
            // Do not let that old callback hide the failure/result or revive its progress animation.
            var batchOutcome = progress.Action is null && (progress.Result is not null ||
                progress.FailureType is not null || progress.State is
                    "Completed" or "Stopped" or "Needs attention" or "Blocked" or "Failed");
            if (!batchOutcome && progress.Action is null && progress.Comparison is { } comparison)
            {
                if (_workspaceRunningPairId != progress.Pair.Id || _workspaceStage != comparison.Phase)
                    _workspacePhaseStarted = comparison.ReportedUtc;
                _workspaceRunningPair = progress.Pair.Name; _workspaceRunningPairId = progress.Pair.Id;
                _workspaceStage = comparison.Phase;
                // ShowRunStatus advances the clock only for changed work,
                // not a repeated report with a newer heartbeat timestamp.
                RunProgress.IsIndeterminate = comparison.TotalChecks <= 0 || comparison.Phase == "Saving Comparison Results";
                RunProgress.Maximum = Math.Max(1, comparison.TotalChecks); RunProgress.Value = comparison.CompletedChecks;
                ShowRunStatus(SyncProgressDescription.Describe(comparison));
                var observation = new ActivityRow(comparison.ReportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    comparison.RelativePath, comparison.Phase, SyncProgressDescription.Describe(comparison),
                    LabelForPair(progress.Pair), progress.Pair.Id, progress.Pair.Name,
                    ItemKind: "System", Categories: [SyncActivityCategories.Sync], Entity: "Folder pair", RunId: _authorityHost.ActiveRun?.Id,
                    ConnectionId: progress.Pair.ConnectionId, EndpointContext: SyncEndpointContext.Capture(progress.Pair));
                AddLiveActivity(observation);
                return;
            }
            if (progress.Action is { } action)
            {
                var outcome = $"{action.Stage} {action.Action.ToLowerInvariant()}";
                var row = new ActivityRow(
                    action.OccurredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    action.RelativePath, outcome, action.Detail, LabelForPair(progress.Pair), progress.Pair.Id,
                    progress.Pair.Name, action.OperationId, action.ItemKind, action.FileType,
                    LocalRoot: progress.Pair.LocalRoot, DropboxRoot: progress.Pair.DropboxRoot,
                    ConnectionId: progress.Pair.ConnectionId,
                    EndpointContext: SyncEndpointContext.Capture(progress.Pair),
                    OperationKind: action.OperationKind, Phase: action.Phase,
                    Categories: [SyncActivityCategories.Files, SyncActivityCategories.Sync], RunId: _authorityHost.ActiveRun?.Id,
                    Entity: action.ItemKind == "Directory" ? "Folder" : "File");
                RunProgress.IsIndeterminate = true;
                _workspaceRunningPair = progress.Pair.Name; _workspaceRunningPairId = progress.Pair.Id;
                _workspaceStage = action.Stage;
                TrackWorkspaceAction(row);
                AddLiveActivity(row);
                ShowRunStatus(action.Batch is { } batch
                    ? batch.Describe() + $"\nLatest: {action.Action} — {action.Stage} — {action.RelativePath}\n{action.Detail}"
                    : $"{action.Action} — {action.Stage}\n{action.RelativePath}\n{action.Detail}");
                return;
            }
            if (progress.State == "Pending")
            {
                // Queue membership is not active work. Replacing only the pair here
                // combined the next pair's name with the previous pair's error/progress.
                return;
            }

            if (progress.State is "Checking" or "Comparing")
            {
                _workspaceRunningPair = progress.Pair.Name; _workspaceRunningPairId = progress.Pair.Id;
                _workspaceStage = progress.State == "Comparing" ? "Comparing and saving results" : "Checking folders";
                ShowRunStatus(progress.State == "Comparing"
                    ? $"{progress.Pair.Name}: folder inventories are complete. Comparing items and saving the results…"
                    : TryResolveCurrentEndpoints(progress.Pair, out var endpoints)
                        ? "Verifying the saved folder identities and preparing their inventory checks.\n" +
                          SyncEndpointPresentation.HierarchyTitle(endpoints.Left) + ": " +
                          SyncEndpointPresentation.HierarchyLocation(endpoints.Left) + "\n" +
                          SyncEndpointPresentation.HierarchyTitle(endpoints.Right) + ": " +
                          SyncEndpointPresentation.HierarchyLocation(endpoints.Right)
                        : $"{progress.Pair.Name}: verifying the saved folder identities before reading their contents…");
                return;
            }

            SetQueueState(progress.Pair.Id, progress.State);
            if (progress.FailureMessage is not null)
            {
                var reportedUtc = DateTimeOffset.UtcNow;
                LastWorkspaceFailure = progress.Pair.Id + ":" + reportedUtc.ToString("O");
                _runFailurePairIds.Add(progress.Pair.Id);
                _runFailureReportedUtcByPair[progress.Pair.Id] = reportedUtc;
                _runAttentionReasonByPair[progress.Pair.Id] = HumanizePairFailure(progress.Pair,
                    progress.FailureMessage);
            }
            else if (string.Equals(progress.State, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _runFailurePairIds.Remove(progress.Pair.Id);
                _runFailureReportedUtcByPair.Remove(progress.Pair.Id);
                _runAttentionReasonByPair.Remove(progress.Pair.Id);
                _endpointAttentionReasonByPair.Remove(progress.Pair.Id);
            }
            if (progress.State == "Running")
            {
                _workspaceRunningPair = progress.Pair.Name; _workspaceRunningPairId = progress.Pair.Id;
                _workspaceStage = recoveryOnly ? "Checking interrupted work" : "Preparing comparison";
                ShowRunStatus(recoveryOnly
                    ? $"Startup recovery: reconciling interrupted work for {progress.Pair.Name} before any fresh planning…"
                    : $"{progress.Pair.Name}: inventories complete; preparing the comparison…");
                return;
            }
            if (progress.Result is not null && (!recoveryOnly || progress.Result.Decisions.Count > 0))
                ShowResult(progress.Pair, progress.Result);
            if (progress.FailureMessage is not null)
            {
                _workspaceRunningPair = progress.Pair.Name; _workspaceRunningPairId = progress.Pair.Id;
                _workspaceStage = recoveryOnly ? "Recovery needs attention" : "Sync needs attention";
                AddActivity(progress.Pair.Name, recoveryOnly ? "Recovery blocked" : "Blocked",
                    (progress.FailureType ?? "Failure") + ": " + progress.FailureMessage, progress.Pair);
                ShowRunStatus($"{progress.Pair.Name} stopped safely; independent pairs remain runnable: {progress.FailureMessage}");
            }
        }).Task;
    }

    private void SetQueueState(string pairId, string state)
    {
        // Pair progress is not an operation transition. Only the durable operation
        // projection may replace a Pending row's state.
        UpdateQueueCount();
    }

    private void UpdateQueueCount()
    {
        QueueCount.Text = _globalPendingQueueCount.ToString();
        if (_workspaceReady) RefreshWorkspace();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (SuppressLoadedStartupForTest) { StartupLoadingHost.Visibility = Visibility.Collapsed; return; }
        if (_startupLoading || _closing) return;
        _startupLoading = true;
        _startupGeneration = checked(_startupGeneration + 1);
        _startupStartedUtc = DateTimeOffset.UtcNow;
        _startupCompletedUtc = null;
        _explorerStartupFailed = false;
        _explorerStartupReady = false;
        _explorerLocalReady = false;
        StartupLoadingHost.Visibility = Visibility.Visible;
        StartupLoadingProgress.Visibility = Visibility.Visible;
        StartupRetryButton.Visibility = Visibility.Collapsed;
        StartupLoadingText.Text = "Checking the saved sync database and loading accounts and folder pairs. No new sync is being started.";
        ReportStartupPhase("saved-accounts", "Reading the protected saved account bindings; no live account check yet.");
        StartupLoadingText.Text = "Reading saved accounts and folder pairs. No new sync is being started.";
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            // Paint the protected local binding immediately. Live Dropbox verification is deliberately
            // second so a healthy saved account is never presented as disconnected while the network responds.
            if (StartupCheckpointForTest is { } checkpoint) await checkpoint("saved accounts");
            // The account projection also loads pairs and their retained history. Admit the same
            // authority first through its existing background path so the real SQLite/legacy steps
            // are visible instead of being hidden behind a misleading credential-read label.
            await RunStartupWorkAsync("retained-database", "Checking the retained sync database before account and history projections; no file transfer is running.", async () =>
                await _authorityHost.LoadPairsAsync(startupProgress: detail => Dispatcher.Invoke(() =>
                {
                    if (_closing || !_startupLoading) return;
                    ReportStartupPhase("retained-database", detail);
                })));
            await RunStartupWorkAsync("saved-accounts", "Reading the protected saved account binding and its last-success summary; no live account check yet.",
                () => RefreshConnectionStateAsync(verifyLive: false));
            if (StartupCheckpointForTest is { } pairsCheckpoint) await pairsCheckpoint("folder pairs");
            await RunStartupWorkAsync("folder-pairs", "Loading saved folder pair definitions and their selected endpoints.", LoadPairsAsync);
            _explorerLocalReady = true;
            // Fixture isolation stops before Windows integration, live account checks or recovery.
            // Its one required history pass exercises the same reads used after real recovery.
            if (StartupLocalOnlyForTest)
            {
                ReportStartupPhase("retained-history", "Reading retained activity, run results and issues from the local database.");
                await RefreshAuthorityProjectionsAsync(requireSuccess: true);
                StartupLoadingHost.Visibility = Visibility.Collapsed;
                ReportStartupPhase("local-fixture-complete", "Local fixture loading completed; native initialization and recovery were not exercised.", "local-only");
                return;
            }
            await RunStartupWorkAsync("queued-explorer-request", "Checking the queued Explorer request after local state became available.", DispatchExplorerRequestAsync);
            try { await RunStartupWorkAsync("explorer-visibility", "Reconciling the existing Explorer menu visibility, if this is the installed application.",
                () => Task.Run(ExplorerIntegration.ReconcileCommandVisibility)); }
            catch (Exception error) { ShowStatus("Explorer Menu Needs Attention: " + error.Message); }
            await RunStartupWorkAsync("live-account-check", "Checking the saved provider connections; no file transfer is running.",
                () => RefreshConnectionStateAsync());
            var settings = _authorityHost.LoadAutomation();
            // Recovery reads durable authority itself; it does not consume UI history tables.
            // Load those tables ONCE after recovery, rather than three serial full passes.
            await RunStartupWorkAsync("startup-recovery", "Reconciling retained interrupted work before any fresh planning.",
                async () => await RecoverOnStartupAsync());
            ReportStartupPhase("recovery-results", "Reading the completed startup-recovery results and retained first-sync reviews.");
            if (StartupCheckpointForTest is { } resultsCheckpoint) await resultsCheckpoint("recovery results");
            await RefreshAuthorityProjectionsAsync(requireSuccess: true);
            // Recovery and the attention summary above do not require loading a hidden
            // inventory table. Its real navigation/tab handlers load it on demand.
            if (_workspacePage == "Attention" && _attentionTabs?.SelectedIndex == 1)
                await RunStartupWorkAsync("recovery-results", "Reading the visible retained first-sync review after recovery.", () => RefreshBootstrapReviewAsync());
            if (!_closing) await RunStartupWorkAsync("local-monitors", "Applying the saved local monitoring settings; automation remains governed by those settings.",
                () => ConfigureLocalMonitorsAsync(settings));
            await RunStartupWorkAsync("explorer-status", "Reading the existing Explorer integration status.", () => RefreshExplorerIntegrationAsync());
            _explorerStartupReady = true;
            await RunStartupWorkAsync("final-explorer-request", "Finishing the queued Explorer request after startup recovery.", DispatchExplorerRequestAsync);
            if (_closing) ReportStartupPhase("closing", "The application is closing; readiness is not being claimed.", "stopping");
            else
            {
                StartupLoadingHost.Visibility = Visibility.Collapsed;
                ReportStartupPhase("complete", "Application initialization and startup recovery completed.", "ready");
            }
        }
        catch (Exception exception)
        {
            _explorerStartupFailed = true;
            ReportStartupPhase(_startupPhase, "Startup stopped safely; saved state is retained. Retry loading to continue.",
                "error", SyncCli.DescribeException(exception));
            StartupLoadingHost.Visibility = Visibility.Visible;
            StartupLoadingProgress.Visibility = Visibility.Collapsed;
            StartupLoadingText.Text = "Startup could not finish: " + SyncActivityText.Failure(exception) +
                " Your saved state has been retained. Retry loading to continue.";
            StartupRetryButton.Visibility = Visibility.Visible;
            ShowStatus("Startup stopped safely: " + exception.Message);
            await DispatchExplorerRequestAsync();
            await OfferStartupRepairAsync();
        }
        finally { _startupLoading = false; }
    }

    /// <summary>root729: the startup repair OFFER. The read-only detector decides whether to offer the guarded
    /// repair at all, so an unrelated startup failure never presents a blind repair, and the affected pair is
    /// named from retained authority rather than a parsed display string.</summary>
    /// <summary>root731: retraceable repair diagnostics use the EXISTING durable activity/history append
    /// (SyncAuthorityDatabase.AppendActivityEventAsync, which applies the shared credential/URL redaction).
    /// Best-effort only: a logging failure never changes the repair outcome or the UI, and no secret is written.</summary>
    private async Task RecordStartupRepairAsync(string action, string severity, string entity, string message,
        string? pairId)
    {
        try
        {
            await _authorityHost.AppendDiagnosticActivityAsync(new SyncActivityEvent(
                "", DateTimeOffset.UtcNow,
                new[] { SyncActivityCategories.Diagnostics }, severity, entity, action, message, PairId: pairId));
        }
        catch (Exception)
        {
            // Diagnostics are best-effort; they must never break the repair or startup.
        }
    }

    private async Task OfferStartupRepairAsync()
    {
        StartupRepairButton.Visibility = Visibility.Collapsed;
        StartupRepairButton.IsEnabled = true;
        StartupRepairStatus.Visibility = Visibility.Collapsed;
        _startupRepairIncident = null;
        try
        {
            var incidents = await _authorityHost.InterruptedPublicationIncidentsAsync();
            if (incidents.Count == 0)
            {
                _startupRepairDiagnostic = "detector: no recoverable interrupted publication incident";
                return;
            }
            _startupRepairIncident = incidents[0];
            _startupRepairDiagnostic = "detected pair=" + incidents[0].PairId + " orphan=" +
                incidents[0].OrphanRevision + " published=" + incidents[0].PublishedRevision +
                (incidents.Count > 1 ? " (+" + (incidents.Count - 1) + " more)" : "");
            StartupRepairStatus.Text = "An interrupted setup was detected for \"" + incidents[0].Name +
                "\" (published revision " + incidents[0].PublishedRevision + ", interrupted revision " +
                incidents[0].OrphanRevision + ").";
            StartupRepairStatus.Visibility = Visibility.Visible;
            StartupRepairButton.Visibility = Visibility.Visible;
            await RecordStartupRepairAsync("startup repair offered", "Warning", "Folder pair",
                "An interrupted setup was detected for \"" + incidents[0].Name + "\" (pair " +
                incidents[0].PairId + "): published revision " + incidents[0].PublishedRevision +
                ", interrupted revision " + incidents[0].OrphanRevision + ".", incidents[0].PairId);
        }
        catch (Exception error)
        {
            // The inspection is read-only; a failure to inspect must never present a repair.
            _startupRepairDiagnostic = "detector failed: " + error.GetType().Name;
        }
    }

    private async void StartupRepair_Click(object sender, RoutedEventArgs e)
    {
        using var feedback = ActionFeedback.Begin(sender);
        if (!feedback.Entered || _startupRepairIncident is not { } incident) return;
        var confirmed = StartupRepairConfirmationForTest?.Invoke() ?? AppDialogWindow.Confirm(this,
            "Repair interrupted setup",
            "Repair \"" + incident.Name + "\"?",
            "This restores the last coherent PUBLISHED revision of that folder pair. Only saved setup metadata " +
            "is rewritten - no file is transferred, moved, deleted, approved or synced, and completed work is not " +
            "repeated.",
            "Repair setup");
        if (!confirmed)
        {
            _startupRepairDiagnostic = "cancelled pair=" + incident.PairId;
            await RecordStartupRepairAsync("startup repair cancelled", "Warning", "Folder pair",
                "The interrupted-setup repair for \"" + incident.Name + "\" (pair " + incident.PairId +
                ") was cancelled; nothing was changed.", incident.PairId);
            ActionFeedback.Report("Repair cancelled; nothing was changed.");
            return;
        }
        await RecordStartupRepairAsync("startup repair attempt", "Information", "Folder pair",
            "Repairing the interrupted setup for \"" + incident.Name + "\" (pair " + incident.PairId +
            "): restoring the last coherent published revision " + incident.PublishedRevision + ".",
            incident.PairId);
        StartupRepairButton.IsEnabled = false;
        StartupRepairStatus.Visibility = Visibility.Visible;
        StartupRepairStatus.Text = "Repairing \"" + incident.Name + "\"â€¦";
        try
        {
            var repaired = await _authorityHost.RecoverInterruptedPublicationAsync(incident.PairId);
            _startupRepairDiagnostic = (repaired ? "repaired" : "no-op") + " pair=" + incident.PairId +
                " orphan=" + incident.OrphanRevision + " published=" + incident.PublishedRevision;
            StartupRepairStatus.Text = repaired
                ? "Repaired \"" + incident.Name + "\". Reloading the saved state."
                : "\"" + incident.Name + "\" was already coherent; nothing was changed. Reloading.";
            ActionFeedback.Report(StartupRepairStatus.Text);
            await RecordStartupRepairAsync(repaired ? "startup repair repaired" : "startup repair no-op",
                repaired ? "Success" : "Warning", "Folder pair",
                (repaired
                    ? "Restored the last coherent published revision " + incident.PublishedRevision +
                      " for \"" + incident.Name + "\" (pair " + incident.PairId + ")."
                    : "\"" + incident.Name + "\" (pair " + incident.PairId +
                      ") was already coherent; nothing was changed."), incident.PairId);
            _startupRepairIncident = null;
            StartupRepairButton.Visibility = Visibility.Collapsed;
            StartupRepairButton.IsEnabled = true;
            MainWindow_Loaded(this, new RoutedEventArgs());
        }
        catch (Exception error)
        {
            _startupRepairDiagnostic = "refused pair=" + incident.PairId + " " + error.GetType().Name + ": " +
                error.Message;
            StartupRepairStatus.Text = "Repair refused: " + SyncActivityText.Failure(error) +
                " Nothing was changed.";
            ActionFeedback.Report(StartupRepairStatus.Text);
            await RecordStartupRepairAsync("startup repair refused", "Error", "Folder pair",
                "The interrupted-setup repair for \"" + incident.Name + "\" (pair " + incident.PairId +
                ") was refused: " + error.GetType().Name + ": " + error.Message + " Nothing was changed.",
                incident.PairId);
            StartupRepairButton.IsEnabled = true;
            StartupRepairButton.Visibility = Visibility.Visible;
        }
    }

    private async Task<IReadOnlySet<string>> RecoverOnStartupAsync()
    {
        var startupReadyPairs = new HashSet<string>(StringComparer.Ordinal);
        _pairSet = await _authorityHost.LoadPairsAsync();
        var pairs = _pairSet.Pairs
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pairs.Length == 0) return startupReadyPairs;
        // Resolve availability per pair in the host. Local/local recovery does not require
        // a cloud login, and one disconnected account must not hide other pairs' recovery.

        // SS-D01: this queued recovery owns one cancellation token, created before the run is marked active
        // and before the host is called, so Stop cancels the queued recovery rather than passing None.
        var recoveryCancellation = BeginPendingRunCancellation();
        _runActive = true;
        SetRunControls();
        RunProgress.IsIndeterminate = true;
        try
        {
            ResetRunPresentation("Checking interrupted work",
                "Startup recovery: reconciling interrupted work before any fresh planning…");
            QueueRows.Clear();
            UpdateQueueCount();
            if (QueuedInvocationHoldForTest is { } holdQueued)
                await holdQueued("Startup recovery", recoveryCancellation.Token);
            var batch = await Task.Run(() => _authorityHost.RecoverPairsAsync(pairs,
                progress => PresentBatchProgressAsync(progress, "Startup recovery", recoveryOnly: true),
                cancellationToken: recoveryCancellation.Token,
                admissionProgress: message => Dispatcher.Invoke(() => ShowRunStatus(message))));
            foreach (var item in batch.Items.Where(item => item.FailureMessage is null &&
                         item.Result is { Blocked: 0, Conflicts: 0, NeedsVerification: 0, Stopped: false }))
                startupReadyPairs.Add(item.Pair.Id);
            var failedPairs = batch.Items.Count(item => item.FailureMessage is not null);
            var presentedRecovery = batch.Items.Any(item => item.Result?.Decisions.Count > 0);
            if (failedPairs > 0)
                ShowStatus($"Startup recovery retained {failedPairs} pair(s) for attention; those pairs will not be freshly planned.");
            else if (!presentedRecovery)
                ShowStatus("Startup recovery found no interrupted effects; fresh planning may now proceed.");
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
            // SS-D01: the user's stop cancelled the queued recovery. No interrupted effect was replayed and
            // no fresh planning is authorised, so the existing startup refusal is preserved below.
            ShowStatus("Startup recovery was stopped before any transfer. No file change was started.");
            throw;
        }
        catch (Exception exception)
        {
            await PresentCommandFailureAsync(exception, pairs, "Startup recovery");
            // Preserve the existing startup refusal: failed recovery is not
            // permission to proceed with fresh planning.
            throw;
        }
        finally
        {
            EndPendingRunCancellation(recoveryCancellation);
            _runActive = false;
            RunProgress.IsIndeterminate = false;
            SetRunControls();
        }
        return startupReadyPairs;
    }

    private async void AutomationTimer_Tick(object? sender, EventArgs e)
    {
        if (!_runActive) await RunScheduledAsync("Safety poll");
    }

    private async Task<bool> RunScheduledAsync(string trigger, IReadOnlySet<string>? eligiblePairIds = null)
    {
        if (_closing) return true;
        if (GlobalPauseActive) return false;
        if (_runActive) return false;
        try
        {
            var settings = _authorityHost.LoadAutomation();
            if (!settings.Enabled) return true;
            _pairSet = await _authorityHost.LoadPairsAsync();
            var enabled = _pairSet.Pairs
                .Where(item => AutomaticSyncEligible(item) &&
                    (eligiblePairIds is null || eligiblePairIds.Contains(item.Id)))
                .ToArray();
            if (enabled.Length == 0)
            {
                ShowStatus(eligiblePairIds is null
                    ? "Live sync has no enabled pairs."
                    : "Startup sync has no recovery-cleared enabled pairs; no fresh plan was run.");
                return true;
            }
            await RunPairsAsync(enabled, trigger);
            return true;
        }
        catch (Exception exception)
        {
            ShowStatus("Live sync stopped safely: " + exception.Message);
            return true;
        }
    }

    // ENG5: the ONE shared rule for automatic Monitor/Safety poll eligibility. A pair whose first-sync review
    // is still pending is never polled automatically - its plan and effects must be reviewed first - while the
    // explicit manual first-sync scan and approval remain available. Enabled, global, account and pair pause
    // semantics are unchanged, and the host still applies its own pause admission to every automatic run.
    private bool AutomaticSyncEligible(SyncPair pair) =>
        pair.Enabled && !AwaitingFirstSyncReview(pair) && !_authorityHost.IsPairAccountPaused(pair);

    // The one predicate that says a pair is still awaiting its reviewed first sync. It reads only the retained
    // pair definition, so it is the same answer for the polls, the monitor configuration and the UI.
    internal static bool AwaitingFirstSyncReview(SyncPair pair) => pair.InitialReviewApproved == false;

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (DashboardPage.Visibility == Visibility.Visible && _workspaceAccountId is { } account && !GlobalPauseActive)
        {
            if (_authorityHost.IsAccountPaused(account)) await SetAccountPauseAsync(account, null, false);
            else OpenAccountPauseMenu((UIElement)sender, account);
        }
        else await HandleWorkspacePauseActionAsync((UIElement)sender);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        // SS-D01: the queued UI invocation is signalled BEFORE the host stop is awaited, so a first-sync
        // scan or a first-sync run that has not reached the host yet is cancelled rather than left running.
        var cancelledPending = CancelPendingRun();
        if (await _authorityHost.StopActiveRunAsync())
            ShowRunStatus("Stop requested; the active transfer is cancelled and no next transfer will start.");
        else if (cancelledPending)
            ShowRunStatus("Stop requested; the queued first-sync work is cancelled and no file change will start.");
    }

    /// <summary>
    /// SS-D01: one cancellation token per UI invocation that queues work before (or without) a host run.
    /// A superseded invocation is cancelled and disposed, never silently reused.
    /// </summary>
    private CancellationTokenSource BeginPendingRunCancellation()
    {
        var cancellation = new CancellationTokenSource();
        var superseded = Interlocked.Exchange(ref _pendingRunCancellation, cancellation);
        if (superseded is not null) { superseded.Cancel(); superseded.Dispose(); }
        return cancellation;
    }

    // SS-D01: only the invocation that owns this exact instance clears it, and only its own instance is
    // disposed. An earlier continuation therefore cannot clear, cancel or dispose a later run's token.
    private void EndPendingRunCancellation(CancellationTokenSource own)
    {
        if (ReferenceEquals(Volatile.Read(ref _pendingRunCancellation), own))
            _pendingRunCancellation = null;
        own.Dispose();
    }

    // SS-D01: signals the queued UI invocation, if any, and reports whether a live token was cancelled.
    // This runs before any host call is awaited by its callers.
    private bool CancelPendingRun()
    {
        var pending = Volatile.Read(ref _pendingRunCancellation);
        if (pending is null) return false;
        try { pending.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder on this computer", Multiselect = false };
        if (dialog.ShowDialog(this) == true) LocalRootBox.Text = dialog.FolderName;
    }

    private void OpenLocalPairFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PairList.SelectedItem is PairRow selected &&
            _folderHealthByPair.TryGetValue(selected.Id, out var health) && !health.LocalHierarchyAvailable)
        {
            PairEditorTabs.SelectedIndex = 0;
            ChangePairRoots_Click(sender, e);
            ShowStatus("Choose a replacement local folder and its matching cloud folder. Nothing changes until you save the repaired pair.");
            return;
        }
        if (string.IsNullOrWhiteSpace(LocalRootBox.Text) || !Directory.Exists(LocalRootBox.Text))
        {
            ShowStatus("The saved local folder is unavailable. Choose different folders to repair this pair.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LocalRootBox.Text}\"")
            { UseShellExecute = true });
    }

    private void BrowsePairInSyncShark_Click(object sender, RoutedEventArgs e)
    {
        PairEditorTabs.SelectedIndex = 0;
        FolderSelectionSearchBox.Clear();
        if (PairList.SelectedItem is PairRow row)
        {
            if (!string.Equals(_editingPairRootId, row.Id, StringComparison.Ordinal))
                ApplyPairSelection(row.Id);
            DropboxRootTree.Focus();
            ShowStatus("The complete cloud hierarchy is open at this pair's saved selection.");
        }
    }

    private void OpenPairOnDropbox_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DropboxRootBox.Text)) return;
        var url = BuildDropboxFolderUrl(DropboxRootBox.Text);
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        ShowStatus("Opened this pair's Dropbox folder on dropbox.com.");
    }

    private async void CheckPairConnection_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || PairList.SelectedItem is not PairRow row) return;
        _pairEditorBusyReason = "Checking whether both saved folder endpoints are available. Wait for the check to finish.";
        PairEditorPanel.IsEnabled = false;
        try
        {
            _folderHealthByPair.Remove(row.Id);
            await RefreshPairSyncStatusAsync(forceHealthCheck: true);
            ShowStatus(row.NeedsAttention
                ? "Folder connection checked. Open Sync status for the problem and repair action."
                : "Folder connection checked. Both paired folders are available.");
        }
        catch (Exception exception) { ShowStatus("Folder connection needs attention: " + exception.Message); }
        finally { PairEditorPanel.IsEnabled = true; _pairEditorBusyReason = null; }
    }

    private void RescanPairStructure_Click(object sender, RoutedEventArgs e)
    {
        if (_runActive || PairList.SelectedItem is not PairRow row) return;
        PairEditorTabs.SelectedIndex = 0;
        ApplyPairSelection(row.Id);
        ShowStatus("The complete local and cloud folder browsers were refreshed. No sync effects were applied.");
    }

    private void CleanupRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (PairList.SelectedItem is not PairRow row) return;
        RecoveryPairBox.SelectedValue = row.Id;
        NavigateTo("Recovery");
        ShowStatus("Review this pair's retained recovery copies before choosing any cleanup action.");
    }

    private async void PairEditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PairEditorTabs)) return;
        if (PairEditorTabs.SelectedIndex == 3)
        {
            await RefreshPairSyncStatusAsync();
            return;
        }
        if (PairEditorTabs.SelectedIndex == PairStorageTabIndex)
        {
            if (!_suppressPairStorageTabLoad) await RefreshPairStorageTabAsync();
            return;
        }
        if (PairEditorTabs.SelectedIndex != 1) return;
        PairPreviewTotals.Children.Clear();
        if (PairList.SelectedItem is not PairRow row || _editingNewPair)
        {
            PairPreviewRows.Clear();
            PairPreviewSummary.Text = "Save the pair before loading pending changes.";
            PairPreviewUnavailable.Visibility = Visibility.Collapsed;
            PairPreviewGrid.Visibility = Visibility.Visible;
            return;
        }
        if (row.NeedsAttention)
        {
            PairPreviewRows.Clear();
            PairPreviewSummary.Text = "This pair must be repaired before its pending changes can be previewed.";
            PairPreviewUnavailableReason.Text = row.StatusDetail;
            PairPreviewUnavailable.Visibility = Visibility.Visible;
            PairPreviewGrid.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var pending = (await _authorityHost.LoadQueueProjectionAsync())
                .Where(item => string.Equals(item.PairId, row.Id, StringComparison.Ordinal)).ToArray();
            var folders = await _authorityHost.LoadFolderSelectionPageAsync(row.Id, 1, 0,
                refreshIndex: false);
            var conflicts = (await Task.Run(() => _authorityHost.LoadOpenConflictComparisonsAsync()))
                .Count(item => string.Equals(item.PairId, row.Id, StringComparison.Ordinal));
            PairPreviewRows.Clear();
            PairPreviewUnavailable.Visibility = Visibility.Collapsed;
            PairPreviewGrid.Visibility = Visibility.Visible;
            foreach (var item in pending)
                PairPreviewRows.Add(new PairPreviewRow(PreviewChangeLabel(item.Operation), item.RelativePath,
                    item.State));
            var counts = CountPreviewOperations(pending.Select(item => item.Operation));
            PresentPairPreviewTotals(counts, conflicts, folders.Rollup.ExcludedItems, folders.Rollup.OnlineOnlyItems);
            PairPreviewSummary.Text = pending.Length == 0
                ? "No pending transfers. Sync Now performs a fresh check of both folders."
                : "Conflicts remain unchanged until you choose a resolution.";
        }
        catch (Exception exception)
        {
            PairPreviewRows.Clear();
            PairPreviewTotals.Children.Clear();
            PairPreviewSummary.Text = "Sync preview is unavailable.";
            PairPreviewUnavailableReason.Text = HumanizePairFailure(RowToPair(row), exception.Message);
            PairPreviewUnavailable.Visibility = Visibility.Visible;
            PairPreviewGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void PresentPairPreviewTotals(PairPreviewCounts counts, int conflicts, long excluded, long onlineOnly)
    {
        PairPreviewTotals.Children.Clear();
        foreach (var (label, count) in new (string, long)[]
                 { ("Uploads", counts.Uploads), ("Downloads", counts.Downloads), ("Recoverable Deletions", counts.Deletions),
                   ("Moves", counts.Moves), ("Conflicts", conflicts), ("Excluded Items", excluded),
                   ("Online-Only Items", onlineOnly), ("Other Changes", counts.Other) })
        {
            var field = new StackPanel { Margin = new Thickness(0, 4, 22, 10) };
            field.Children.Add(WorkspaceVisuals.Text(label, 12, brush: "MutedInk"));
            field.Children.Add(WorkspaceVisuals.Text(count.ToString("N0"), 15, strong: true));
            PairPreviewTotals.Children.Add(field);
        }
    }

    private SyncPair RowToPair(PairRow row) => _pairSet.Pairs.Single(pair =>
        string.Equals(pair.Id, row.Id, StringComparison.Ordinal));

    private async void OpenPairSyncStatus_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (sender is not System.Windows.Controls.Button { DataContext: PairRow row }) return;
        PairList.SelectedItem = row;
        ExpandPairRow(row);
        if (PairEditorTabs.SelectedIndex == 3)
            await RefreshPairSyncStatusAsync();
        else
            PairEditorTabs.SelectedIndex = 3;
        e.Handled = true;
    }

    private async Task RefreshPairSyncStatusAsync(bool forceHealthCheck = false)
    {
        if (_editingNewPair || PairList.SelectedItem is not PairRow row)
        {
            PairHealthOverallText.Text = "Save this pair to begin health checks.";
            PairHealthReasonText.Text = "Sync status becomes available after the folder pair is saved.";
            PairHealthLocalText.Text = "Not checked";
            PairHealthDropboxText.Text = "Not checked";
            PairHealthLiveSyncText.Text = "Not started";
            PairHealthMonitorText.Text = "Not started";
            PairHealthFirstSyncText.Text = "Not started";
            PairHealthPendingText.Text = "0";
            PairHealthConflictsText.Text = "0";
            PairHealthRecoveryText.Text = "Not configured";
            return;
        }

        var pair = _pairSet.Pairs.Single(item => string.Equals(item.Id, row.Id, StringComparison.Ordinal));
        if (TryResolveCurrentEndpoints(pair, out var endpointContext))
        {
            var localSide = endpointContext.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft
                ? endpointContext.Left : endpointContext.Right;
            var remoteSide = endpointContext.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalRight
                ? endpointContext.Left : endpointContext.Right;
            PairHealthLocalTitle.Text = SyncEndpointPresentation.HierarchyTitle(localSide);
            PairHealthRemoteTitle.Text = SyncEndpointPresentation.HierarchyTitle(remoteSide);
            PairHealthLocalPath.Text = SyncEndpointPresentation.HierarchyLocation(localSide);
            PairHealthRemotePath.Text = SyncEndpointPresentation.HierarchyLocation(remoteSide);
        }
        PairHealthOverallText.Text = row.NeedsAttention ? "Needs attention" : row.Status;
        PairHealthReasonText.Text = row.StatusDetail;
        var health = await EnsurePairHealthAsync(row, forceHealthCheck);
        var incompleteCheckReason = health is null ? PairHealthReasonText.Text : null;
        if (health is not null)
        {
            // W2-C: each health line names the endpoint that actually occupies that physical port, so a
            // reversed or two-cloud pair is never labelled "this computer" or "Dropbox" by assumption.
            var titles = HierarchyPortTitles(health);
            PairHealthLocalText.Text = health.LocalHierarchyAvailable
                ? titles.Local + ": available"
                : titles.Local + ": unavailable\n" + (health.LocalHierarchyMessage ?? "Folder unavailable.");
            PairHealthDropboxText.Text = health.DropboxHierarchyAvailable
                ? titles.Remote + ": available"
                : titles.Remote + ": unreachable\n" + (health.DropboxHierarchyMessage ?? "Folder unavailable.");
        }
        else
        {
            PairHealthLocalText.Text = "Not checked";
            PairHealthDropboxText.Text = "Not checked";
        }

        PairHealthLiveSyncText.Text = !pair.Enabled
            ? "Pair paused"
            : !_authorityHost.LoadAutomation().Enabled ? "Live sync is off in Settings" : "On";
        PairHealthMonitorText.Text = row.MonitorState;
        PairHealthFirstSyncText.Text = pair.InitialReviewApproved == false
            ? "Review required before Sync Shark changes files"
            : pair.InitialReviewApproved == true ? "Reviewed" : "Not yet recorded";
        PairHealthRecoveryText.Text = pair.RecoveryRoot is null
            ? "Not configured"
            : $"Status: Ready\nLocation: {pair.RecoveryRoot.RootPath}";
        try
        {
            var pending = (await _authorityHost.LoadQueueProjectionAsync())
                .Count(item => string.Equals(item.PairId, row.Id, StringComparison.Ordinal));
            var conflicts = (await _authorityHost.LoadOpenConflictComparisonsAsync())
                .Count(item => string.Equals(item.PairId, row.Id, StringComparison.Ordinal));
            PairHealthPendingText.Text = pending.ToString("N0");
            PairHealthConflictsText.Text = conflicts.ToString("N0");
        }
        catch (Exception exception)
        {
            PairHealthPendingText.Text = "Unavailable";
            PairHealthConflictsText.Text = "Unavailable";
            if (!row.NeedsAttention) PairHealthReasonText.Text = "Status details could not be loaded: " + exception.Message;
        }
        var blockingReason = PairHealthBlockingReason(row.Id);
        PairHealthOverallText.Text = blockingReason is null
            ? row.NeedsAttention ? "Needs attention" : row.Status
            : "Needs attention";
        PairHealthReasonText.Text = incompleteCheckReason is { Length: > 0 }
            ? blockingReason is null ? incompleteCheckReason : incompleteCheckReason + "\nLast confirmed problem: " + blockingReason
            : blockingReason ?? row.StatusDetail;
        EditorSyncNowButton.IsEnabled = !UiOperationBusy && blockingReason is null;
        await RefreshPairOwnershipAsync(pair);
    }

    private async Task<SyncFolderSelectionTree?> EnsurePairHealthAsync(PairRow row, bool force)
    {
        if (!force && _folderHealthByPair.TryGetValue(row.Id, out var retained)) return retained;
        if (!_pairHealthCheckIds.Add(row.Id))
            return _folderHealthByPair.GetValueOrDefault(row.Id);
        PairHealthLocalText.Text = "Checking…";
        PairHealthDropboxText.Text = "Checking…";
        try
        {
            using var timeout = new CancellationTokenSource(PairHealthTimeoutForTest ?? TimeSpan.FromSeconds(20));
            var tree = PairHealthLoaderForTest is null
                ? await _authorityHost.LoadFolderSelectionTreeAsync(row.Id, timeout.Token)
                : await PairHealthLoaderForTest(row.Id, timeout.Token);
            ApplyFolderHierarchyHealth(tree);
            return tree;
        }
        catch (OperationCanceledException)
        {
            var reason = "The folder check did not finish in time. The saved folders were not marked missing. " +
                         "Try Check again when the current sync settles.";
            PairHealthLocalText.Text = "Check Timed Out";
            PairHealthDropboxText.Text = "Check Timed Out";
            PairHealthReasonText.Text = reason;
            // A timeout is an unknown observation, not evidence that either endpoint is missing.
            // Preserve previously confirmed health without turning a slow check into a sync block.
            return null;
        }
        catch (Exception exception)
        {
            var pair = _pairSet.Pairs.Single(item => string.Equals(item.Id, row.Id, StringComparison.Ordinal));
            var reason = HumanizePairFailure(pair, exception.Message);
            // W2-C: a failed or incomplete hierarchy check never manufactures a MISSING local endpoint for
            // a pair that has none, and never claims the OPPOSITE endpoint healthy. Both sides are published
            // as not verified, and neither side's previously confirmed health is replaced with a fresh
            // "checked" claim.
            var failed = new SyncFolderSelectionTree(pair.Id, [], 0, 0, 0, 0,
                LocalHierarchyAvailable: false,
                LocalHierarchyMessage: reason,
                DropboxHierarchyAvailable: false,
                DropboxHierarchyMessage: null);
            ApplyFolderHierarchyHealth(failed);
            PairHealthLocalText.Text = "Not verified";
            PairHealthDropboxText.Text = "Not verified";
            PairHealthReasonText.Text =
                "The folder hierarchy could not be checked just now; neither endpoint is reported healthy. " +
                "No saved identity has been marked missing. Refresh to try again. " + reason;
            return failed;
        }
        finally { _pairHealthCheckIds.Remove(row.Id); }
    }

    private bool IsPairKnownUnhealthy(string pairId) =>
        _localRootProblems.ContainsKey(pairId) || _endpointAttentionReasonByPair.ContainsKey(pairId);

    private Task<bool> IsDropboxConnectedForUiAsync() => DropboxConnectionCheckForTest is null
        ? _authorityHost.IsAnyAccountConnectedAsync()
        : DropboxConnectionCheckForTest();

    private string? PairHealthBlockingReason(string pairId) =>
        _localRootProblems.GetValueOrDefault(pairId) ?? _endpointAttentionReasonByPair.GetValueOrDefault(pairId);

    private void UpdatePairAttentionPresentation(string pairId)
    {
        var pair = _pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        var row = PairRows.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        if (pair is null || row is null) return;
        var needsAttention = _attentionPairIds.Contains(pair.Id) ||
                             IsPairKnownUnhealthy(pair.Id) ||
                             _monitorAttentionReasonByPair.ContainsKey(pair.Id) ||
                             _runAttentionReasonByPair.ContainsKey(pair.Id);
        row.UpdateHealth(needsAttention, PairAttentionReason(pair, needsAttention));
        EditorSyncNowButton.IsEnabled = !UiOperationBusy && !IsPairKnownUnhealthy(pair.Id);
    }

    private static string PreviewChangeLabel(string operation) => operation switch
    {
        "create_remote" or "update_remote" => "Upload",
        "create_local" or "update_local" => "Download",
        "delete_remote" or "delete_local" => "Recoverable deletion",
        "move_remote" or "move_local" => "Move",
        _ => operation.Replace('_', ' ')
    };

    internal static PairPreviewCounts CountPreviewOperations(IEnumerable<string> operations)
    {
        var uploads = 0;
        var downloads = 0;
        var deletions = 0;
        var moves = 0;
        var other = 0;
        foreach (var operation in operations)
        {
            switch (operation.Trim().ToLowerInvariant())
            {
                case "create_remote" or "update_remote" or "upload new" or "upload update": uploads++; break;
                case "create_local" or "update_local" or "download new" or "download update": downloads++; break;
                case "delete_remote" or "delete_local" or "archive local" or "archive dropbox": deletions++; break;
                case "move_remote" or "move_local" or "cross_pair_move" or "move local" or "move dropbox" or "cross-pair move": moves++; break;
                default: other++; break;
            }
        }
        return new PairPreviewCounts(uploads, downloads, deletions, moves, other);
    }

    private async void BrowseDropbox_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        try
        {
            var folderBrowser = await _authorityHost.CreateFolderBrowserAsync(connectionId: SelectedPairConnectionId);
            var browser = new DropboxFolderBrowserWindow(folderBrowser, PairAccountLabel, ProviderPresentation.Key) { Owner = this };
            if (browser.ShowDialog() == true && browser.SelectedFolder is not null)
            {
                _selectedDropboxFolder = browser.SelectedFolder;
                DropboxRootBox.Text = _selectedDropboxFolder.Path;
                DropboxFolderIdText.Text = _selectedDropboxFolder.FolderId;
                ShowStatus($"Selected {_selectedDropboxFolder.Path}. Bind pair will verify its stable folder identity and perform an effect-free two-observation discovery.");
            }
        }
        catch (Exception exception) { ShowStatus(ProviderPresentation.Name + " folders could not be opened: " + exception.Message); }
    }

    private async void BrowseRecovery_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        try
        {
            var folderBrowser = PairFolderBrowserFactoryForTest is null
                ? await _authorityHost.CreateFolderBrowserAsync(connectionId: SelectedPairConnectionId)
                : await PairFolderBrowserFactoryForTest(CancellationToken.None);
            var browser = CreateRecoveryFolderBrowser(folderBrowser, PairAccountLabel);
            browser.Owner = this;
            var selected = RecoveryFolderDialogForTest is null
                ? browser.ShowDialog() == true ? browser.SelectedFolder : null
                : RecoveryFolderDialogForTest(browser);
            if (selected is not null)
            {
                SyncDropboxArtifactPaths.RequireRecoveryContainer(selected.Path);
                _selectedRecoveryFolder = selected;
                _recoveryPreparationFailed = false;
                RecoveryRootBox.Text = _selectedRecoveryFolder.Path;
                RecoveryFolderIdText.Text = _selectedRecoveryFolder.FolderId;
                UpdatePairSaveAvailability();
                ShowPairEditorStatus($"Selected recovery folder {_selectedRecoveryFolder.Path}. Save pair will verify its identity.");
                ShowStatus($"Selected recovery folder {_selectedRecoveryFolder.Path}. Save pair will verify its stable ID and exclude Recovery automatically if it is inside this pair.");
            }
        }
        catch (Exception exception)
        {
            ShowPairEditorStatus("The recovery folder could not be selected: " + exception.Message,
                isError: true);
            ShowStatus("The recovery folder could not be selected: " + exception.Message);
        }
    }

    internal static DropboxFolderBrowserWindow CreateRecoveryFolderBrowser(
        IDropboxFolderBrowserService folderBrowser, string connectionLabel) =>
        new(folderBrowser, connectionLabel, DropboxFolderGraphBudget.CreateDefault(),
            allowFolderCreation: false,
            selectionPredicate: entry => IsCanonicalRecoveryFolder(entry.Path),
            selectionRestrictionMessage:
            $"Recovery must use {SyncDropboxArtifactPaths.RecoveryContainerPath}. Use recommended if it does not exist yet.")
        {
            Title = "Select dedicated Sync Shark recovery folder"
        };

    private static bool IsCanonicalRecoveryFolder(string path)
    {
        try
        {
            SyncDropboxArtifactPaths.RequireRecoveryContainer(path);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async void UseRecommendedRecovery_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await UseRecommendedRecoveryAsync();
    }

    private async Task UseRecommendedRecoveryAsync()
    {
        if (_runActive) return;
        _pairEditorBusyReason = "Creating or verifying this account's recommended Recovery folder. Wait for the provider result.";
        PairEditorPanel.IsEnabled = false;
        try
        {
            ShowStatus($"Creating or verifying {SyncDropboxArtifactPaths.RecoveryContainerPath}…");
            var selected = RecommendedRecoveryFolderForTest is null
                ? await _authorityHost.EnsureRecommendedRecoveryFolderAsync(connectionId: SelectedPairConnectionId)
                : await RecommendedRecoveryFolderForTest(CancellationToken.None);
            SyncDropboxArtifactPaths.RequireRecoveryContainer(selected.Path);
            _selectedRecoveryFolder = selected;
            _recoveryPreparationFailed = false;
            RecoveryRootBox.Text = _selectedRecoveryFolder.Path;
            RecoveryFolderIdText.Text = _selectedRecoveryFolder.FolderId;
            UpdatePairSaveAvailability();
            ShowPairEditorStatus($"Recommended cloud Recovery is ready at {SyncDropboxArtifactPaths.RecoveryContainerPath}. Its stable folder identity will be bound when the pair is saved.");
            ShowStatus($"Recommended cloud Recovery is ready at {SyncDropboxArtifactPaths.RecoveryContainerPath}. Its stable folder identity will be bound when the pair is saved.");
        }
        catch (Exception exception)
        {
            _selectedRecoveryFolder = null;
            _recoveryPreparationFailed = true;
            RecoveryRootBox.Clear();
            RecoveryFolderIdText.Text = "Not selected";
            UpdatePairSaveAvailability();
            ShowPairEditorStatus("Recommended Recovery could not be prepared: " + exception.Message,
                isError: true);
            ShowStatus("Recommended Recovery could not be prepared: " + exception.Message);
        }
        finally { PairEditorPanel.IsEnabled = true; _pairEditorBusyReason = null; }
    }

    internal Task UseRecommendedRecoveryForTestAsync() => UseRecommendedRecoveryAsync();

    private async void SavePair_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await SavePairAsync();
    }

    /// <summary>
    /// SS-D03: the one committed-save pipeline both Save inputs share. A successful authoritative bind
    /// return IS the commit, so the returned identity is adopted and this exact draft is closed before any
    /// follow-up runs. Every follow-up afterwards runs under its own guard, so a failure is reported
    /// against that exact step instead of being presented as a refused save.
    /// </summary>
    private async Task<List<string>> FinishPairSaveAsync(SyncPairBindingResult result, string status,
        string activityDetail)
    {
        _pairSet = result.PairSet;
        EndPairDraft();
        _lastCommittedPairId = result.Pair.Id;
        _pairSaveFollowUpDetail = null;
        // A new commit replaces the previous save's outstanding follow-up set.
        _pendingPairSaveFollowUps.Clear();
        var problems = new List<string>();
        await RunPairSaveFollowUpAsync(problems, "the pair list refresh", () =>
        {
            RefreshPairRows(result.Pair.Id);
            ExpandPairRow(PairRows.Single(item => item.Id == result.Pair.Id));
            AddActivity("<pair>", "Bound", activityDetail);
            ShowPairEditorStatus(status);
            ShowStatus(status);
            return Task.CompletedTask;
        });
        await RunPairSaveFollowUpAsync(problems, "the local monitor refresh",
            () => ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation()));
        return problems;
    }

    /// <summary>SS-D03: one named follow-up of a committed pair save.</summary>
    private sealed record PairSaveFollowUp(string Label, Func<Task> Run);

    // SS-D03: the identity this editor most recently committed, and the follow-ups of that exact save that
    // have not yet succeeded. They are the only thing a retry of a committed save re-runs.
    private string? _lastCommittedPairId;
    private readonly List<PairSaveFollowUp> _pendingPairSaveFollowUps = [];
    private string? _pairSaveFollowUpDetail;

    // SS-D03: the fixture's only input into the committed-save follow-up pipeline. It names the one step
    // whose real member must fail so that step's isolation can be proved. Null - every production call -
    // runs each real follow-up unchanged.
    internal static string? PairSaveFollowUpFailureStepForTest { get; set; }

    internal string? LastCommittedPairIdForTest => _lastCommittedPairId;
    internal string? PairSaveFollowUpDetailForTest => _pairSaveFollowUpDetail;
    internal IReadOnlyList<string> PendingPairSaveFollowUpLabelsForTest =>
        _pendingPairSaveFollowUps.Select(followUp => followUp.Label).ToArray();

    /// <summary>
    /// SS-D03: runs one committed-save follow-up under its own guard. A failure is reported against that
    /// exact step and retained for a retry of that step only, so one failed follow-up cannot hide the
    /// others, cannot invent a rollback and cannot escape into the pre-commit refusal path.
    /// </summary>
    private async Task RunPairSaveFollowUpAsync(List<string> problems, string label, Func<Task> followUp)
    {
        try
        {
            if (string.Equals(PairSaveFollowUpFailureStepForTest, label, StringComparison.Ordinal))
                throw new IOException("The " + label + " follow-up was refused for this proof.");
            await followUp();
        }
        catch (Exception error)
        {
            problems.Add(label);
            _pairSaveFollowUpDetail = label + ": " + SyncActivityText.Failure(error);
            _pendingPairSaveFollowUps.RemoveAll(item => string.Equals(item.Label, label, StringComparison.Ordinal));
            _pendingPairSaveFollowUps.Add(new PairSaveFollowUp(label, followUp));
            PresentPairSaveFollowUpProblem(label);
        }
    }

    // The one truthful wording for a committed save whose follow-up did not complete: the pair IS saved,
    // its identity is retained, and the exact step that needs attention is named.
    private void PresentPairSaveFollowUpProblem(string label)
    {
        var message = "Pair saved; the saved pair identity is retained. Follow-up needs attention: " + label + "." +
            (_pairSaveFollowUpDetail is { } detail ? " " + detail : "");
        ShowPairEditorStatus(message, isError: true);
        ShowStatus(message);
    }

    private static string PairSaveStatusWithFollowUps(string savedStatus, IReadOnlyList<string> problems) =>
        problems.Count == 0 ? savedStatus
        : savedStatus + " Follow-up needs attention: " + string.Join(", ", problems) + ".";

    /// <summary>
    /// SS-D03: re-runs ONLY the follow-ups that have not yet succeeded for the pair that actually
    /// committed. It never calls Bind, so a retry cannot increment the bind count or create a duplicate
    /// pair. It returns false when nothing is outstanding, which keeps every ordinary Save path unchanged.
    /// </summary>
    private async Task<bool> RetryCommittedPairSaveFollowUpsAsync()
    {
        if (_lastCommittedPairId is null || _pendingPairSaveFollowUps.Count == 0) return false;
        ShowPairEditorStatus("Retrying the follow-up that needs attention for the saved pair…");
        var retrying = _pendingPairSaveFollowUps.ToArray();
        _pendingPairSaveFollowUps.Clear();
        var problems = new List<string>();
        foreach (var followUp in retrying)
            await RunPairSaveFollowUpAsync(problems, followUp.Label, followUp.Run);
        if (problems.Count > 0) return true;
        _lastCommittedPairId = null;
        _pairSaveFollowUpDetail = null;
        ShowPairEditorStatus("The saved pair's follow-up completed. The saved pair identity is unchanged.");
        ShowStatus("The saved pair's follow-up completed. The saved pair identity is unchanged.");
        return true;
    }

    private async Task<bool> SavePairAsync()
    {
        if (_runActive)
        {
            ShowPairEditorStatus("Wait for the current sync to finish before saving this pair.", isError: true);
            return false;
        }
        // SS-D03: a committed save whose follow-ups have not all succeeded retries THOSE, for the pair that
        // actually committed. Its draft is already closed and nothing is authored, so nothing is submitted
        // again: no second Bind and no duplicate pair is possible.
        if (!PairLocationDraftActive && EndpointDraft() is not { Endpoints: not null } &&
            await RetryCommittedPairSaveFollowUpsAsync()) return true;
        if (EndpointCombinationReason() is { } unsupportedEndpoints)
        {
            ShowPairEditorStatus(unsupportedEndpoints, isError: true);
            return false;
        }
        // One endpoint Save for every ordered combination this build can bind (EUI-S1). A draft whose
        // explicit session cannot name both endpoints yet keeps using the retained two-tree draft below.
        if (EndpointDraft() is { Endpoints: not null } endpointDraft)
            return await SaveEndpointPairAsync(endpointDraft);
        if (_recoveryPreparationFailed)
        {
            ShowPairEditorStatus(
                $"Recovery is not ready. Retry the recommended Recovery folder at {SyncDropboxArtifactPaths.RecoveryContainerPath} before saving.",
                isError: true);
            return false;
        }
        SavePairButton.IsEnabled = false;
        HidePairEditorStatus();
        try
        {
            var selectedId = !_editingNewPair && PairList.SelectedItem is PairRow selected ? selected.Id : null;
            var changingLocations = _editingNewPair || _editingPairRootId is not null;
            var draftPlan = changingLocations
                ? _globalDraftPlan ?? (_draftPreview is null
                    ? throw new InvalidOperationException(
                        "Choose matching folders on this computer and in the cloud before saving this pair.")
                    : CreateDraftBindingPlan(_draftPreview, _draftSelectionRules,
                        _draftSelectionDefaultIncluded))
                : null;
            var localRoot = draftPlan?.LocalRoot ?? LocalRootBox.Text;
            var dropboxRoot = draftPlan?.DropboxRoot ?? _selectedDropboxFolder;
            if (dropboxRoot is null)
                throw new InvalidOperationException("Browse the selected cloud account and select a specific folder before binding the pair.");
            if (!int.TryParse(PairPriorityBox.Text, out var priority))
                throw new InvalidDataException("Pair priority must be a whole number from -100 to 100.");
            var existing = selectedId is null
                ? null
                : _pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, selectedId,
                    StringComparison.Ordinal));
            // The compatibility save path uses the same opening snapshot as the endpoint editor.
            // A refreshed pair list must not turn an old draft into authority to overwrite a newer policy.
            var expectedPolicyRevision = _endpointSession?.EditingPolicyRevision;
            if (selectedId is not null && (existing is null ||
                _endpointSession?.EditingPairId != selectedId || expectedPolicyRevision != existing.PolicyRevision))
                throw new InvalidOperationException(
                    "This pair changed while the editor was open. Reopen it before saving your changes.");
            var name = PairNameBox.Text.Trim();
            if (name.Length == 0)
                throw new InvalidDataException("Enter a name for this folder pair.");
            if (_pairSet.Pairs.Any(item => !string.Equals(item.Id, selectedId, StringComparison.Ordinal) &&
                                          string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Another folder pair already uses that name.");
            if (_duplicateSourcePairId is not null)
            {
                var source = _pairSet.Pairs.SingleOrDefault(item =>
                    string.Equals(item.Id, _duplicateSourcePairId, StringComparison.Ordinal));
                if (source is not null && !DuplicateEndpointsDiffer(source, localRoot, dropboxRoot))
                    throw new InvalidOperationException(
                        "Choose a different local folder or cloud folder before saving this duplicate.");
            }
            if (_selectedRecoveryFolder is null)
            {
                ShowPairEditorStatus($"Preparing {SyncDropboxArtifactPaths.RecoveryContainerPath} as part of Save…");
                var recovery = await _authorityHost.EnsureRecommendedRecoveryFolderAsync(connectionId: SelectedPairConnectionId);
                SyncDropboxArtifactPaths.RequireRecoveryContainer(recovery.Path);
                _selectedRecoveryFolder = recovery;
                RecoveryRootBox.Text = recovery.Path;
                RecoveryFolderIdText.Text = recovery.FolderId;
            }
            var requestedFilesOnDemand = PairFilesOnDemandBox.IsChecked == true;
            var makeAvailableOfflineAfterSave = false;
            var filesOnDemandForBinding = FilesOnDemandValueForBinding(existing?.FilesOnDemandEnabled,
                requestedFilesOnDemand) || _draftNeedsCloudFiles;
            if (_draftNeedsCloudFiles && !AppDialogWindow.Confirm(this, "Enable Folder Storage Controls",
                "Let Sync Shark manage storage for this pair?",
                "The pair's default stays Keep Offline. Only folders you choose as Cloud Only can be freed after their exact Dropbox copy is verified. This does not take over folders managed by another provider.",
                "Enable and Save")) return false;
            var retention = SelectedRecoveryRetentionDays();
            var bindingRequest = new SyncPairBindingRequest(selectedId,
                name, localRoot, dropboxRoot, _selectedRecoveryFolder,
                PairEnabledBox.IsChecked == true, priority, PairDeletionBox.IsChecked == true,
                filesOnDemandForBinding,
                changingLocations ? draftPlan!.Rules : _draftSelectionRules,
                changingLocations ? draftPlan!.DefaultIncluded : _draftSelectionDefaultIncluded,
                retention, (SyncMode)SyncTypeBox.SelectedIndex, DirectoryStorageRules: _draftDirectoryStorageRules,
                ConnectionId: SelectedPairConnectionId, DefaultStorage: requestedFilesOnDemand
                    ? SyncDirectoryStorage.CloudOnly : SyncDirectoryStorage.AlwaysAvailable,
                IgnoreRules: _draftIgnoreRules, ExpectedPolicyRevision: expectedPolicyRevision);
            var result = PairBindingForTest is null
                ? await _authorityHost.BindPairAsync(bindingRequest,
                    progress: detail => Dispatcher.Invoke(() => ShowPairEditorStatus(detail)))
                : await PairBindingForTest(bindingRequest);
            // SS-D03: the authoritative bind return IS the commit. The saved status is derived from the
            // committed result alone, the identity is adopted and the draft is closed by the shared
            // pipeline below, and every remaining step is a separately reported follow-up: a storage,
            // details, row, monitor or Explorer failure can no longer be presented as a refused save, and
            // none of them can be retried into a second Bind.
            var savedStatus = result.IdentityChanged
                ? "Folder identity changed: Sync Shark created a new pair identity. Its next run is effect-free initial discovery."
                : existing is null
                    ? result.Pair.SelectionDefaultIncluded
                        ? "Pair saved for the complete parent folders, including parent files and future content. The first complete inventory remains effect-free until reviewed."
                        : result.Pair.SelectionRules?.Any(rule => rule.Included) == true
                            ? "Pair saved with the chosen two-way folder branches. Unselected parent files and future sibling folders remain excluded; the first complete inventory remains effect-free until reviewed."
                            : "Pair saved safely with nothing selected. The first complete inventory remains effect-free until reviewed."
                    : "Pair saved: both folder identities, Recovery, and folder choices are retained. The first complete inventory remains effect-free until reviewed.";
            var followUpProblems = await FinishPairSaveAsync(result, savedStatus, result.IdentityChanged
                ? "Authoritative folder identity changed; a new pair ID was created and the old baseline was retired."
                : "Local and cloud folder IDs, local staging, and the selected recovery folder were verified; initial discovery will be effect-free.");
            await RunPairSaveFollowUpAsync(followUpProblems, "the storage settings", async () =>
            {
                if (!result.Pair.FilesOnDemandEnabled || PairBindingForTest is not null) return;
                var storage = await _authorityHost.ApplyPendingDirectoryStorageAsync(result.Pair.Id,
                    onProgress: entry => Dispatcher.InvokeAsync(() => ShowPairEditorStatus(
                        entry.Comparison is { } detail ? SyncProgressDescription.Describe(detail) : entry.State)).Task);
                _pairSet = await _authorityHost.LoadPairsAsync();
                if (storage.Failures.Count != 0)
                    throw new IOException("Storage remains pending for " + storage.Failures.Count +
                        " items; existing bytes are preserved. " + string.Join(Environment.NewLine,
                            storage.Failures.Select(SyncActivityText.Safe)));
            });
            if (makeAvailableOfflineAfterSave)
            {
                try
                {
                    ShowStatus("Pair settings were saved. Making every managed file available offline…");
                    _pairSet = await _authorityHost.DisableFilesOnDemandAsync(result.Pair.Id);
                    ExplorerIntegration.UnregisterRoot(result.Pair);
                    var availableOfflinePair = _pairSet.Pairs.Single(item =>
                        string.Equals(item.Id, result.Pair.Id, StringComparison.Ordinal));
                    result = result with { PairSet = _pairSet, Pair = availableOfflinePair };
                }
                catch (Exception exception)
                {
                    ShowStatus("Pair settings were saved, but storage remains Online-only because making every file available offline did not complete: " +
                        exception.Message);
                }
            }
            // The saved pair's own displayed identity: a missing local binding is now reported as a
            // follow-up that needs attention instead of discarding the committed pair.
            await RunPairSaveFollowUpAsync(followUpProblems, "the saved pair details", () =>
            {
                var localSettings = result.Settings
                    ?? throw new InvalidDataException("The local folder binding returned no local settings.");
                LocalRootBox.Text = localSettings.LocalRoot;
                DropboxRootBox.Text = localSettings.DropboxRoot;
                DropboxFolderIdText.Text = localSettings.DropboxFolderId;
                _selectedDropboxFolder = new DropboxFolderEntry(DropboxFolderDisplayName(localSettings.DropboxRoot),
                    localSettings.DropboxRoot, localSettings.DropboxFolderId);
                RecoveryRootBox.Text = _selectedRecoveryFolder?.Path ?? "Not used by this provider";
                RecoveryFolderIdText.Text = _selectedRecoveryFolder?.FolderId ?? "";
                return Task.CompletedTask;
            });
            await RunPairSaveFollowUpAsync(followUpProblems, "the Explorer integration refresh",
                () => RefreshExplorerIntegrationAsync());
            if (followUpProblems.Count > 0)
            {
                // Projection refreshes can open the saved pair's editor again. Close that draft before
                // presenting a retry, so Save retries only this committed identity's retained follow-ups.
                EndPairDraft();
                var followUpStatus = PairSaveStatusWithFollowUps(savedStatus, followUpProblems);
                ShowPairEditorStatus(followUpStatus, isError: true);
                ShowStatus(followUpStatus);
            }
            return true;
        }
        catch (Exception exception)
        {
            ShowPairEditorStatus("Pair was not saved: " + exception.Message, isError: true);
            ShowStatus("Pair was not saved: " + exception.Message);
            return false;
        }
        finally { SetRunControls(); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await ConnectDropboxFromUiAsync();
    }

    private async Task ConnectDropboxFromUiAsync()
    {
        if (_runActive) { ShowStatus("Wait for the current sync to finish before changing the Dropbox connection."); return; }
        if (!DropboxProviderConfiguration.IsLiveProviderBound) { ShowStatus("The registered Dropbox app key is not bound in this build."); return; }
        // The local credential store is authoritative for this decision. A delayed or failed presentation
        // refresh must never offer a duplicate OAuth flow for an account that is already connected.
        if (await _authorityHost.IsDropboxConnectedAsync())
        {
            await RefreshConnectionStateAsync(verifyLive: false);
            NavigateTo("Connections");
            ShowStatus("Dropbox is already connected. Account details and reconnect controls are shown here.");
            _ = RefreshConnectionStateAsync();
            return;
        }
        ConnectButton.IsEnabled = false;
        ConnectionConnectButton.IsEnabled = false;
        try
        {
            await AuthorizeDropboxFromUiAsync(token =>
                _authorityHost.ConnectDropboxAsync(OpenAuthorizationPageForUiAsync, token));
            AddActivity("<connection>", "Connected", "Saved account and namespace identities verified.");
            ShowStatus("Dropbox connection completed after protected credential and append-only binding persistence.");
            await RefreshConnectionStateAsync();
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
        }
        catch (Exception exception) { ShowStatus("Dropbox connection was not completed: " + exception.Message); }
        finally
        {
            ConnectButton.IsEnabled = DropboxProviderConfiguration.IsLiveProviderBound;
            ConnectionConnectButton.IsEnabled = DropboxProviderConfiguration.IsLiveProviderBound &&
                                                   !AuthorizationAttemptWaiting && _connectionStatus?.Binding is null;
        }
    }

    internal Task ConnectDropboxFromUiForTestAsync() => ConnectDropboxFromUiAsync();
    internal Task RefreshConnectionStateForTestAsync(bool verifyLive) =>
        RefreshConnectionStateAsync(verifyLive);

    private static Task OpenAuthorizationPageAsync(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive) return;
        var providerName = _connectionStatus?.Binding?.ProviderName ?? "Account";
        // Capture the exact reconnect target before the first await: the account selection can change while the
        // reauthorization is in flight, so authorisation and the post-success Kick must both use this one id, with
        // the default account resolved when no selection is made.
        var reconnectId = _selectedConnectionId ?? _connectionStatus?.Binding?.ConnectionId;
        ConnectionReauthorizeButton.IsEnabled = false;
        try
        {
            if (reconnectId is not null && _authorityHost.LoadGoogleDriveConnection(reconnectId) is { } google)
                await AuthorizeAccountFromUiAsync(token => _authorityHost.ConnectGoogleDriveAsync(
                    OpenAuthorizationPageForUiAsync, token, google.ConnectionId), "Google Drive");
            else if (DropboxReconnectForTest is null)
                await AuthorizeDropboxFromUiAsync(token => _authorityHost.ReauthorizeDropboxAsync(
                    OpenAuthorizationPageForUiAsync, token, connectionId: reconnectId));
            else
                await DropboxReconnectForTest();
            AddActivity("<connection>", "Reconnected", providerName + " authorization and live account identity were renewed.");
            await RefreshConnectionStateAsync();
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
            // A reconnect makes this account's pairs eligible again; reconfiguration's Start is a no-op, so kick
            // every pair on the exact account that was reconnected, never whatever is selected now.
            if (reconnectId is { } reconnectedId) KickV2MonitorsForAccount(reconnectedId);
            ShowStatus(providerName + " was reconnected and every dependent pair retained its identity and baseline.");
        }
        catch (Exception exception)
        {
            ShowConnectionFailure(providerName + " reconnection was not completed; the prior connection was retained: " + exception.Message);
        }
        finally { SetRunControls(); }
    }

    private void ManageDropbox_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_connectionStatus?.Binding?.Provider == SyncProviders.GoogleDrive
            ? "https://drive.google.com/drive/my-drive" : "https://www.dropbox.com/account") { UseShellExecute = true });

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive) return;
        var providerName = _connectionStatus?.Binding?.ProviderName ?? "Account";
        var dependentPairs = _connectionStatus?.DependentPairCount ?? 0;
        if (!AppDialogWindow.Confirm(this, "Disconnect " + (_connectionStatus?.Binding?.ProviderName ?? "Account"),
                $"Pause {dependentPairs:N0} folder pair(s) and remove this connection?",
                "Pair settings, sync history, files and Recovery copies stay in place. Reconnecting the same account resumes access to those pairs.",
                "Disconnect", destructive: true)) return;
        ConnectionDisconnectButton.IsEnabled = false;
        try
        {
            if (_connectionStatus?.Binding?.Provider == SyncProviders.GoogleDrive && _selectedConnectionId is not null)
                await _authorityHost.DisconnectGoogleDriveAsync(_selectedConnectionId);
            else if (DropboxDisconnectForTest is null)
                await _authorityHost.DisconnectDropboxAsync(connectionId: _selectedConnectionId);
            else
                await DropboxDisconnectForTest();
            AddActivity("<connection>", "Disconnected", "Protected credential removed; pair definitions and data retained.");
            await RefreshConnectionStateAsync();
            ShowStatus(providerName + " disconnected. Retained pairs are paused until the matching account is connected again.");
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
        }
        catch (Exception exception)
        {
            await RefreshConnectionStateAsync();
            try
            {
                await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
                ShowConnectionFailure(providerName + " was not disconnected cleanly; the retained connection and automatic monitoring were restored: " + exception.Message);
            }
            catch (Exception monitorException)
            {
                ShowConnectionFailure(providerName + " was not disconnected cleanly and automatic monitoring could not restart. " +
                           exception.Message + " Monitor restart: " + monitorException.Message);
            }
        }
    }

    private async Task LoadPairsAsync()
    {
        try
        {
            _pairSet = await _authorityHost.LoadPairsAsync();
            RefreshPairRows(_pairSet.SelectedPairId);
            await RefreshRecoveryPairChoicesAsync(_pairSet.SelectedPairId);
            if (_pairSet.Pairs.Count == 0) BeginNewPairEditor();
        }
        catch (Exception exception) { ShowStatus("Saved pairs could not be loaded: " + exception.Message); }
    }

    private async void NewPair_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive) return;
        if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
        BeginNewPairEditor();
    }

    private async Task RefreshRecoveryPairChoicesAsync(string? preferredPairId = null)
    {
        if (!_authorityHost.AuthorityStateExists) return;
        var prior = preferredPairId ?? RecoveryPairBox.SelectedValue?.ToString();
        var choices = await Task.Run(() => _authorityHost.LoadRecoveryPairChoicesAsync());
        var wasLoading = _loadingRecovery;
        _loadingRecovery = true;
        var wasSelecting = _selectingRecoveryPair;
        _selectingRecoveryPair = true;
        try
        {
        RecoveryPairChoices.Clear();
        var multipleAccounts = _authorityHost.LoadProviderConnections().Count(item => item.IsActive) > 1;
        foreach (var choice in choices) RecoveryPairChoices.Add(multipleAccounts && _pairSet.Pairs.Any(pair => pair.Id == choice.Id)
            ? choice with { Name = choice.Name + " — " + LabelForPairId(choice.Id) } : choice);
        RecoveryPairBox.SelectedValue = choices.Any(item => string.Equals(item.Id, prior, StringComparison.Ordinal))
            ? prior
            : choices.FirstOrDefault()?.Id;
        }
        finally { _loadingRecovery = wasLoading; _selectingRecoveryPair = wasSelecting; }
    }

    private void CancelPairEdit_Click(object sender, RoutedEventArgs e)
    {
        if (PairLocationDraftActive)
        {
            DiscardCurrentPairDraft();
            return;
        }
        CollapseExpandedPair();
        ShowStatus("Pair editing closed without changing its saved settings.");
    }

    private void DiscardCurrentPairDraft()
    {
        var editedPairId = _editingPairRootId;
        var wasNew = _editingNewPair;
        EndPairDraft();
        if (wasNew)
        {
            NewPairEditorHost.Visibility = Visibility.Collapsed;
            PairList.SelectedItem = null;
            ShowStatus("The unsaved folder pair was discarded. Saved pairs were not changed.");
        }
        else if (editedPairId is not null)
        {
            CollapseExpandedPair();
            ShowStatus("The folder changes were discarded. The saved pair was not changed.");
        }
    }

    private void PairSearch_Changed(object sender, TextChangedEventArgs e)
    {
        // The shared predicate also carries the exact-account scope the linked-pair count selects.
        ApplyPairViewFilter();
    }

    private void PairName_Changed(object sender, TextChangedEventArgs e)
    {
        UpdatePairSaveAvailability();
    }

    private void UpdatePairSaveAvailability()
    {
        if (SavePairButton is null || PairNameBox is null || PairList is null || PairEnabledBox is null ||
            PairDeletionBox is null || PairStorageBox is null || PairPriorityBox is null ||
            RecoveryRetentionBox is null) return;
        RefreshPairEndpointStrip();
        var endpointDraft = EndpointDraft();
        var name = PairNameBox.Text.Trim();
        var selectedId = !_editingNewPair && PairList.SelectedItem is PairRow selected ? selected.Id : null;
        var uniqueName = name.Length > 0 && !_pairSet.Pairs.Any(item =>
            !string.Equals(item.Id, selectedId, StringComparison.Ordinal) &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        string? pendingLocalRoot = null;
        DropboxFolderEntry? pendingDropboxRoot = null;
        var locationsChosen = false;
        var rootsDisjoint = false;
        var connected = false;
        var duplicateChanged = true;
        if (endpointDraft is { Endpoints: not null })
        {
            // EUI-S1/EUI-S4: the explicit session is the authoring surface for every ordered
            // combination this build can bind, so its own validation decides availability. Nothing here
            // reads the retained local-left/cloud-right shape or the two-tree planner.
            UpdateEndpointSaveAvailability(endpointDraft);
            locationsChosen = true;
            rootsDisjoint = endpointDraft.Validation.IsValid;
            connected = true;
        }
        else
        {
            UpdateStorageOptionsForSelectedRoot();
            try
            {
                var pendingPlan = PairLocationDraftActive
                    ? _globalDraftPlan ?? (_draftPreview is null
                        ? null
                        : CreateDraftBindingPlan(_draftPreview, _draftSelectionRules,
                            _draftSelectionDefaultIncluded))
                    : null;
                pendingLocalRoot = pendingPlan?.LocalRoot ?? (PairLocationDraftActive ? null : LocalRootBox.Text);
                pendingDropboxRoot = pendingPlan?.DropboxRoot ?? (PairLocationDraftActive ? null : _selectedDropboxFolder);
            }
            catch (InvalidOperationException) { }
            var recoveryForValidation = _selectedRecoveryFolder ?? new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath, string.Empty);
            locationsChosen = !string.IsNullOrWhiteSpace(pendingLocalRoot) && pendingDropboxRoot is not null;
            rootsDisjoint = locationsChosen && PairRootsAreDisjoint(pendingLocalRoot!, pendingDropboxRoot!,
                recoveryForValidation, _pairSet.Pairs, selectedId, SelectedPairConnectionId);
            var pairAccount = PairAccountBinding;
            connected = pairAccount is not null && (_connectionStatus?.Binding?.ConnectionId != pairAccount.ConnectionId ||
                                                       _connectionStatus.HasProtectedCredential);
            if (_duplicateSourcePairId is not null)
            {
                var source = _pairSet.Pairs.SingleOrDefault(item =>
                    string.Equals(item.Id, _duplicateSourcePairId, StringComparison.Ordinal));
                if (source is not null && locationsChosen)
                {
                    duplicateChanged = pendingLocalRoot is not null && pendingDropboxRoot is not null &&
                                       DuplicateEndpointsDiffer(source, pendingLocalRoot, pendingDropboxRoot);
                }
            }
        }
        var rootsReady = locationsChosen && rootsDisjoint && !_recoveryPreparationFailed;
        SavePairButton.IsEnabled = !_runActive && uniqueName && rootsReady && connected && duplicateChanged && EndpointCombinationSupported();
        var hasUnsavedChanges = PairEditorHasUnsavedChanges(pendingLocalRoot, pendingDropboxRoot);
        SavePairButton.Visibility = hasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;
        CancelPairButton.Visibility = hasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed;
        SavePairButton.Content = CommandIcons.Label(_editingNewPair ? "Create Pair" : "Save Changes", "save");
        // A new pair has no saved draft to destroy, so its Cancel stays neutral; only an existing
        // pair's Discard Changes takes the shared destructive presentation.
        CommandIcons.SetUnsavedAction(CancelPairButton, _editingNewPair ? "Cancel" : "Discard Changes",
            _editingNewPair ? UnsavedActionIntent.Neutral : UnsavedActionIntent.Discard);
        if (EditorSyncNowButton is not null && EditorSyncNowButton.Visibility == Visibility.Visible)
            EditorSyncNowButton.IsEnabled = !UiOperationBusy && !hasUnsavedChanges &&
                                            selectedId is not null && !IsPairKnownUnhealthy(selectedId);
        if (!PairLocationDraftActive || PairEditorStatus is null) return;
        var reason = _runActive ? "A sync is running. You can browse this pair, but settings cannot be changed until it finishes."
            : name.Length == 0 ? "Give this folder pair a name."
            : !uniqueName ? "Another folder pair already uses this name."
            : endpointDraft is { UnsupportedBindingReason: { } unsupportedReason } ? unsupportedReason
            : endpointDraft is { Endpoints: not null, CanSave: false } ? "This pair cannot be saved yet: " + endpointDraft.Validation.Describe()
            : !connected ? "Connect the selected cloud account before saving this pair."
            : _recoveryPreparationFailed ? $"Recovery is not ready. Retry {SyncDropboxArtifactPaths.RecoveryContainerPath} before saving."
            : !locationsChosen ? "Tick at least one folder on this computer and one matching cloud folder."
            : !rootsDisjoint ? "These folders overlap another pair or the Recovery folder. Choose a different combination."
            : !duplicateChanged ? "A duplicate must use a different folder on this computer or in the cloud."
            : null;
        // Validation must not replace the result of Save on every timer tick.
        // Save/Discard already communicate dirty state without moving the tabs.
        if (string.Equals(reason, _pairValidationReason, StringComparison.Ordinal)) return;
        var showingPreviousValidation = string.Equals(PairEditorStatusText.Text, _pairValidationReason,
            StringComparison.Ordinal);
        _pairValidationReason = reason;
        if (reason is not null) ShowPairEditorStatus(reason, !rootsReady || !uniqueName || !connected);
        else if (showingPreviousValidation) HidePairEditorStatus();
    }

    private bool PairEditorHasUnsavedChanges(string? pendingLocalRoot, DropboxFolderEntry? pendingDropboxRoot)
    {
        if (_editingNewPair) return true;
        if (_editingPairRootId is null) return false;
        var pair = _pairSet.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, _editingPairRootId, StringComparison.Ordinal));
        if (pair is null) return true;
        // EUI-S4: an explicit endpoint draft is compared through the one physical projection, so a
        // retained cloud-left/local-right pair is dirty only when its own ordered endpoints changed.
        // An editor created by a background selection is not an edit. Missing
        // preview data means the saved roots have not finished loading, not that
        // the user selected different roots.
        var locationsChanged = false;
        if (EndpointDraft() is { Endpoints: { } endpoints })
        {
            var layout = SyncEndpointEngineLayout.Create(endpoints);
            locationsChanged =
                !string.Equals(PilotPath.NormalizeLocalRoot(layout.Local.LocalPath!), pair.LocalRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                (!layout.Remote.IsLocal &&
                 !string.Equals(layout.Remote.FolderId, pair.DropboxFolderId, StringComparison.Ordinal));
        }
        else if (!string.IsNullOrWhiteSpace(pendingLocalRoot) && pendingDropboxRoot is not null)
        {
            locationsChanged =
                !string.Equals(PilotPath.NormalizeLocalRoot(pendingLocalRoot), pair.LocalRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pendingDropboxRoot.FolderId, pair.DropboxFolderId, StringComparison.Ordinal);
        }
        var pendingRules = SyncSelectionPolicy.Normalize(_globalDraftPlan?.Rules ?? _draftSelectionRules);
        var savedRules = SyncSelectionPolicy.Normalize(pair.SelectionRules);
        return locationsChanged ||
               !string.Equals(PairNameBox.Text.Trim(), pair.Name, StringComparison.Ordinal) ||
               PairEnabledBox.IsChecked != pair.Enabled || PairDeletionBox.IsChecked != pair.PropagateDeletions ||
               SyncTypeBox.SelectedIndex != (int)pair.Direction ||
               !SyncIgnorePolicy.Normalize(_draftIgnoreRules).SequenceEqual(SyncIgnorePolicy.Normalize(pair.IgnoreRules)) ||
               (PairStorageBox.SelectedIndex == 1) != (SyncDirectoryStoragePolicy.Default(pair) == SyncDirectoryStorage.CloudOnly) ||
               !SyncDirectoryStoragePolicy.Normalize(_draftDirectoryStorageRules ?? pair.DirectoryStorageRules).SequenceEqual(SyncDirectoryStoragePolicy.Normalize(pair.DirectoryStorageRules)) ||
               !int.TryParse(PairPriorityBox.Text, out var priority) || priority != pair.Priority ||
               SelectedRecoveryRetentionDays() != pair.RecoveryRetentionDays ||
               (_globalDraftPlan?.DefaultIncluded ?? _draftSelectionDefaultIncluded) != pair.SelectionDefaultIncluded ||
               !pendingRules.SequenceEqual(savedRules);
    }

    private bool CurrentPairEditorHasUnsavedChanges()
    {
        if (!PairLocationDraftActive) return false;
        if (_editingNewPair)
            return !string.IsNullOrWhiteSpace(PairNameBox.Text) || _globalDraftPlan is not null ||
                   PairDeletionBox.IsChecked == true || PairStorageBox.SelectedIndex != 0 ||
                   SyncTypeBox.SelectedIndex != 0 ||
                   _draftIgnoreRules.Count != 0 ||
                   PairEnabledBox.IsChecked != true || PairPriorityBox.Text != "0" ||
                   SelectedRecoveryRetentionDays() != 90;
        DropboxFolderEntry? dropbox = null;
        string? local = null;
        try
        {
            var plan = _globalDraftPlan ?? (_draftPreview is null
                ? null
                : CreateDraftBindingPlan(_draftPreview, _draftSelectionRules, _draftSelectionDefaultIncluded));
            local = plan?.LocalRoot;
            dropbox = plan?.DropboxRoot;
        }
        catch (InvalidOperationException) { return true; }
        return PairEditorHasUnsavedChanges(local, dropbox);
    }

    private bool _pairSwitchActive;
    private async Task<bool> PrepareToLeaveCurrentPairDraftAsync()
    {
        if (_changingPairStorage) { ShowStatus("Wait for the current storage change before leaving this pair."); return false; }
        if (_pairSwitchActive) return false;
        if (!PairLocationDraftActive) return true;
        _pairSwitchActive = true;
        try
        {
            if (CurrentPairEditorHasUnsavedChanges())
            {
                var choice = UnsavedNavigationChoiceForTest?.Invoke() ?? AppDialogWindow.Choose(this,
        "Unsaved Changes", "Save this folder pair before leaving?",
        "Save its changes, discard its draft, or stay in this editor.",
        "Save Changes", "Discard Changes", "Stay Here", secondaryIsDiscard: true);
                if (choice == AppDialogChoice.Primary) return await SavePairAsync();
                if (choice != AppDialogChoice.Secondary) return false;
            }
            var wasNew = _editingNewPair;
            EndPairDraft();
            if (wasNew) NewPairEditorHost.Visibility = Visibility.Collapsed;
            return true;
        }
        finally { _pairSwitchActive = false; }
    }

    private void PairEditorSetting_Changed(object sender, RoutedEventArgs e)
    {
        UpdatePairDeletionDescription();
        UpdatePairSaveAvailability();
    }

    internal static string DeletionDirectionDescription(SyncMode mode, string provider = SyncProviders.Dropbox) => mode switch
    {
        SyncMode.UploadOnly => $"Apply local deletions to {SyncProviders.DisplayName(provider)} after saving a Recovery copy",
        SyncMode.DownloadOnly => $"Apply {SyncProviders.DisplayName(provider)} deletions to this computer after saving a Recovery copy",
        _ => "Apply deletions in both directions after saving a Recovery copy"
    };

    private void UpdatePairDeletionDescription()
    {
        if (PairDeletionBox is null || SyncTypeBox is null) return;
        var description = DeletionDirectionDescription((SyncMode)SyncTypeBox.SelectedIndex, ProviderPresentation.Key);
        PairDeletionBox.Content = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        PairDeletionBox.ToolTip = description + ". Conflicts and unsafe deletions still require attention.";
    }

    private void UpdateStorageOptionsForSelectedRoot()
    {
        if (PairStorageBox is null || StorageSupportText is null || PairStorageBox.Items.Count < 2) return;
        // EUI-S4: physical storage controls read the local folder through the shared engine layout, so a
        // cloud-left/local-right pair never assumes the local root sits on the left.
        var localRoot = PhysicalLocalRootForStorage();
        var controlledByAnotherProvider = !string.IsNullOrWhiteSpace(localRoot) &&
                                          Directory.Exists(localRoot) &&
                                          WindowsCloudFilesProvider.IsCloudFilesPlaceholder(localRoot) &&
                                          !WindowsCloudFilesProvider.IsOwnedSyncRoot(localRoot);
        if (PairStorageBox.Items[1] is ComboBoxItem onlineOnly)
        {
            onlineOnly.IsEnabled = !controlledByAnotherProvider;
            onlineOnly.ToolTip = controlledByAnotherProvider
                ? "Another provider controls this folder. Resolve its ownership before using Sync Shark's online-only storage."
                : "Files appear in File Explorer and download when opened, saving space on this computer.";
        }
        if (controlledByAnotherProvider && PairStorageBox.SelectedIndex == 1)
            PairStorageBox.SelectedIndex = 0;
        StorageSupportText.Text = controlledByAnotherProvider
            ? "Another provider controls online-only storage for this folder. Its ownership must be resolved first."
            : string.Empty;
    }

    /// <summary>
    /// The folder on this computer the current draft actually owns, whichever ordered side it occupies.
    /// </summary>
    private string? PhysicalLocalRootForStorage() =>
        EndpointDraft() is { Endpoints: { } endpoints }
            ? SyncEndpointEngineLayout.Create(endpoints).Local.LocalPath
            : LocalRootBox?.Text;

    private void PairEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (PairEnabledBox is null) return;
        PairEnabledBox.Content = PairEnabledBox.IsChecked == true ? "Live sync" : "Pair paused";
        UpdatePairSaveAvailability();
    }

    private void PairStorage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PairFilesOnDemandBox is null || PairStorageBox is null) return;
        PairFilesOnDemandBox.IsChecked = PairStorageBox.SelectedIndex == 1;
        UpdatePairSaveAvailability();
    }

    private void PairPriorityChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PairPriorityBox is null || PairPriorityChoice?.SelectedItem is not ComboBoxItem choice) return;
        PairPriorityBox.Text = choice.Tag?.ToString() ?? "0";
        UpdatePairSaveAvailability();
    }

    private void LoadPairSyncOrder(int priority)
    {
        PairPriorityChoice.SelectedIndex = priority > 0 ? 0 : priority < 0 ? 2 : 1;
        // Selecting the display group fires its change handler. Preserve the exact saved value
        // afterwards; opening an older pair is not an instruction to rewrite its priority.
        PairPriorityBox.Text = priority.ToString();
        UpdatePairSaveAvailability();
    }

    private async void TogglePairEditor_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (sender is not System.Windows.Controls.Button { Tag: PairRow row }) return;
        // Selecting a row opens it, so the state captured when the press began decides whether
        // this activation still has to open the row or is the second half of a toggle.
        var wasExpanded = string.Equals(_pairRowPressId, row.Id, StringComparison.Ordinal)
            ? _pairRowPressExpanded
            : string.Equals(_expandedPairId, row.Id, StringComparison.Ordinal);
        ClearPairRowPress();
        await TogglePairRowAsync(row, wasExpanded);
    }

    // One toggle path for the leading provider control, the row body and the keyboard, so all
    // three keep the same draft guard, selection and expansion behaviour.
    private async Task TogglePairRowAsync(PairRow row, bool wasExpanded)
    {
        if (wasExpanded && string.Equals(_expandedPairId, row.Id, StringComparison.Ordinal))
        {
            if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
            CollapseExpandedPair();
            return;
        }
        if (ReferenceEquals(PairList.SelectedItem, row)) ExpandPairRow(row);
        else PairList.SelectedItem = row;
    }

    // UI12: the row body toggles the details with the mouse and with Enter. Every control that
    // has its own action keeps it, and none of these handlers marks a routed event handled.
    private void PairRowBody_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = source is null ? null : FindAncestor<DataGridRow>(source)?.Item as PairRow;
        _pairRowPressId = row?.Id;
        _pairRowPressExpanded = row is not null && string.Equals(_expandedPairId, row.Id, StringComparison.Ordinal);
        _pairRowPressWasSelected = row is not null && ReferenceEquals(PairList.SelectedItem, row);
        _pairRowPressIsBody = row is not null && source is not null && e.ClickCount == 1 &&
                              !RowChildKeepsOwnAction(source);
    }

    private async void PairRowBody_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressedId = _pairRowPressId;
        var wasBody = _pairRowPressIsBody;
        var wasSelected = _pairRowPressWasSelected;
        var wasExpanded = _pairRowPressExpanded;
        ClearPairRowPress();
        if (pressedId is null || !wasBody) return;
        var source = e.OriginalSource as DependencyObject;
        if (source is not null && RowChildKeepsOwnAction(source)) return;
        var row = source is null ? null : FindAncestor<DataGridRow>(source)?.Item as PairRow;
        if (row is null || !string.Equals(row.Id, pressedId, StringComparison.Ordinal)) return;
        // A press that selected the row already opened it, so only an already-selected row body
        // still has to toggle. Every other control kept its own action above.
        if (!wasSelected) return;
        await TogglePairRowAsync(row, wasExpanded);
    }

    // Enter on the row body toggles exactly what the mouse toggles. A control with its own
    // action, including everything in the shared pair editor, keeps the keyboard.
    private async void PairRowBody_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.OriginalSource is DependencyObject source && RowChildKeepsOwnAction(source)) return;
        if (PairList.SelectedItem is not PairRow row) return;
        e.Handled = true;
        await TogglePairRowAsync(row, string.Equals(_expandedPairId, row.Id, StringComparison.Ordinal));
    }

    private void ClearPairRowPress()
    {
        _pairRowPressId = null;
        _pairRowPressIsBody = false;
        _pairRowPressWasSelected = false;
        _pairRowPressExpanded = false;
    }

    // The shared pair editor lives in the same row details, so a control or text box with its
    // own action, and the details area itself, never count as the row body.
    private static bool RowChildKeepsOwnAction(DependencyObject source) =>
        FindAncestor<System.Windows.Controls.Primitives.DataGridDetailsPresenter>(source) is not null ||
        FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null ||
        FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null ||
        FindAncestor<System.Windows.Controls.ComboBox>(source) is not null;

    private void ExpandPairRow(PairRow row)
    {
        if (_editingNewPair)
        {
            EndPairDraft();
            NewPairEditorHost.Visibility = Visibility.Collapsed;
        }
        if (!string.Equals(_expandedPairId, row.Id, StringComparison.Ordinal)) CollapseExpandedPair();
        PairList.ScrollIntoView(row);
        PairList.UpdateLayout();
        if (PairList.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
        {
            container.DetailsVisibility = Visibility.Visible;
            _expandedPairId = row.Id;
            row.SetExpanded(true);
            ApplyPairSelection(row.Id);
            container.UpdateLayout();
            AttachPairEditorToExpandedRow(container, row.Id);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (string.Equals(_expandedPairId, row.Id, StringComparison.Ordinal))
                    AttachPairEditorToExpandedRow(container, row.Id);
            });
        }
    }

    private void AttachPairEditorToExpandedRow(DataGridRow container, string pairId)
    {
        var host = FindDescendant<ContentControl>(container, "PairRowDetailsHost");
        if (host is null) return;
        MovePairEditorTo(host);
        SetPairExpandedState(container, pairId, expanded: true);
        PairEditorPanel.Visibility = Visibility.Visible;
        PairEditorPanel.BringIntoView();
        ResetPairEditorViewport();
    }

    private void ResetPairEditorViewport()
    {
        void Reset()
        {
            PairsScroller.ScrollToHorizontalOffset(0);
            (FindDescendants<ScrollViewer>(PairList).FirstOrDefault(item => item.Name == "DG_ScrollViewer") ??
             FindDescendant<ScrollViewer>(PairList))?.ScrollToHorizontalOffset(0);
            PairEditorPanel.UpdateLayout();
            try
            {
                var editorTop = PairEditorPanel.TransformToAncestor(PairsScroller)
                    .Transform(new System.Windows.Point(0, 0)).Y;
                PairsScroller.ScrollToVerticalOffset(Math.Max(0, PairsScroller.VerticalOffset + editorTop - 8));
            }
            catch (InvalidOperationException)
            {
                PairEditorHeading.BringIntoView();
            }
        }
        Reset();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            Reset);
    }

    private void CollapseExpandedPair()
    {
        if (_expandedPairId is null) return;
        var row = PairRows.FirstOrDefault(item => string.Equals(item.Id, _expandedPairId,
            StringComparison.Ordinal));
        MovePairEditorTo(NewPairEditorHost);
        NewPairEditorHost.Visibility = Visibility.Collapsed;
        if (row is not null && PairList.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
        {
            container.DetailsVisibility = Visibility.Collapsed;
            SetPairExpandedState(container, row.Id, expanded: false);
        }
        _expandedPairId = null;
        row?.SetExpanded(false);
        ClearPairStorageTab();
        if (!_editingNewPair && _editingPairRootId is not null) EndPairDraft();
    }

    // The leading control keeps the pair's provider mark, so expansion is reported through the
    // accessible state of the row and its control instead of by swapping a glyph.
    private void SetPairExpandedState(DependencyObject root, string pairId, bool expanded)
    {
        var row = PairRows.FirstOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        row?.SetExpanded(expanded);
        if (root is UIElement element)
            System.Windows.Automation.AutomationProperties.SetItemStatus(element,
                expanded ? "Expanded" : "Collapsed");
    }

    private void PairLiveSync_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox toggle) return;
        toggle.Loaded -= PairLiveSync_Loaded;
        ActionAvailability.Bind(toggle, () => _runActive
            ? "A scan or sync is running. Wait for it to finish before changing this pair's Live Sync."
            : "This pair's Live Sync change is still being saved. Wait for its result.");
    }

    private async void PairLiveSync_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (sender is not CheckBox { Tag: PairRow row } toggle) return;
        if (_runActive)
        {
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, row.Enabled);
            const string waiting = "Wait for the current sync to finish before changing Live sync.";
            ShowPairEditorStatus(waiting, isError: true);
            // UI397_B: the same refusal is reported on the Folder Pairs page itself, so it stays readable
            // while this pair's details and its editor are collapsed.
            SharedPresentation.SetInlineFeedback(PairPageActionFeedback, waiting, isError: true);
            return;
        }
        var enabled = toggle.IsChecked == true;
        var previousEnabled = row.Enabled;
        var synchronizeSavedDraft = !_editingNewPair && _editingPairRootId == row.Id &&
                                    PairEnabledBox.IsChecked == previousEnabled;
        toggle.SetCurrentValue(IsEnabledProperty, false);
        // The saved row can update before its monitors finish configuring. Replace any old
        // refusal immediately while the shared action spinner owns this command's lifetime.
        SharedPresentation.SetInlineFeedback(PairPageActionFeedback, enabled
            ? $"Turning on live sync for {row.Name}…"
            : $"Pausing live sync for {row.Name}…", isError: false);
        try
        {
            _pairSet = await _authorityHost.SetPairEnabledAsync(row.Id, enabled);
            // The row action is already saved. Rebase only this unchanged editor field, not genuine
            // edits to its name, endpoints or policies, and never a different pair opened meanwhile.
            if (synchronizeSavedDraft && !_editingNewPair && _editingPairRootId == row.Id &&
                PairEnabledBox.IsChecked == previousEnabled)
                PairEnabledBox.IsChecked = enabled;
            RefreshPairRows(row.Id);
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
            var result = enabled
                ? $"Live sync is on for {row.Name}."
                : $"{row.Name} is paused. Manual Sync now remains available.";
            ShowStatus(result);
            SharedPresentation.SetInlineFeedback(PairPageActionFeedback, result, isError: false);
        }
        catch (Exception exception)
        {
            toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, row.Enabled);
            var refusal = "Live sync was not changed: " + exception.Message;
            ShowStatus(refusal);
            SharedPresentation.SetInlineFeedback(PairPageActionFeedback, refusal, isError: true);
        }
        finally
        {
            toggle.SetCurrentValue(IsEnabledProperty, !_runActive);
            ActionAvailability.Refresh(toggle);
        }
    }

    private void DuplicatePair_Click(object sender, RoutedEventArgs e)
    {
        if (_runActive) return;
        if (PairList.SelectedItem is not PairRow row) return;
        var source = _pairSet.Pairs.Single(item => string.Equals(item.Id, row.Id, StringComparison.Ordinal));
        BeginNewPairEditor();
        _duplicateSourcePairId = source.Id;
        PairNameBox.Text = NextDuplicatePairName(source.Name);
        PairEnabledBox.IsChecked = false;
        PairDeletionBox.IsChecked = source.PropagateDeletions;
        PairStorageBox.SelectedIndex = SyncDirectoryStoragePolicy.Default(source) == SyncDirectoryStorage.CloudOnly ? 1 : 0;
        SyncTypeBox.SelectedIndex = (int)source.Direction;
        PairPriorityChoice.SelectedIndex = source.Priority > 0 ? 0 : source.Priority < 0 ? 2 : 1;
        RecoveryRetentionBox.SelectedIndex = source.RecoveryRetentionDays switch
        {
            30 => 0,
            180 => 2,
            null => 3,
            _ => 1
        };
        _selectedRecoveryFolder = source.RecoveryRoot is null ? null :
            new DropboxFolderEntry(DropboxFolderDisplayName(source.RecoveryRoot.RootPath),
                source.RecoveryRoot.RootPath, source.RecoveryRoot.RootFolderId);
        RecoveryRootBox.Text = source.RecoveryRoot?.RootPath ?? string.Empty;
        RecoveryFolderIdText.Text = source.RecoveryRoot?.RootFolderId ?? "Not selected";
        _draftBrowseLocalRoot = source.LocalRoot;
        _draftBrowseDropboxRoot = new DropboxFolderEntry(DropboxFolderDisplayName(source.DropboxRoot),
            source.DropboxRoot, source.DropboxFolderId);
        _draftSelectionRules = SyncSelectionPolicy.Normalize(source.SelectionRules);
        LoadIgnoreRules(source.IgnoreRules);
        _draftSelectionDefaultIncluded = source.SelectionDefaultIncluded;
        LocalRootBox.Text = source.LocalRoot;
        DropboxRootBox.Text = source.DropboxRoot;
        _ = LoadNewPairDraftPreviewAsync();
        ShowStatus("Duplicate draft created in a paused state. Choose a different local or cloud folder before saving.");
    }

    private string NextDuplicatePairName(string sourceName) =>
        NextDuplicatePairName(sourceName, _pairSet.Pairs.Select(pair => pair.Name));

    internal static string NextDuplicatePairName(string sourceName, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = "Copy of " + sourceName;
        if (!names.Contains(stem)) return stem;
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = $"{stem} ({suffix})";
            if (!names.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("No unique duplicate pair name is available.");
    }

    internal static bool DuplicateEndpointsDiffer(SyncPair source, string localRoot,
        DropboxFolderEntry dropboxRoot) =>
        !string.Equals(PilotPath.NormalizeLocalRoot(localRoot), source.LocalRoot,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(dropboxRoot.FolderId, source.DropboxFolderId, StringComparison.Ordinal);

    internal static bool PairRootsAreDisjoint(string localRoot, DropboxFolderEntry dropboxRoot,
        DropboxFolderEntry? recoveryRoot, IEnumerable<SyncPair> activePairs, string? replacingPairId, string? connectionId = null)
    {
        var local = PilotPath.NormalizeLocalRoot(localRoot).TrimEnd(Path.DirectorySeparatorChar);
        var dropbox = PilotPath.NormalizeDropboxRoot(dropboxRoot.Path);
        var recovery = recoveryRoot is null ? null : PilotPath.NormalizeDropboxRoot(recoveryRoot.Path);
        if (recovery is not null && !RecoveryPlacementAllowedForEditor(dropbox, recovery)) return false;
        foreach (var pair in activePairs.Where(pair =>
                     !string.Equals(pair.Id, replacingPairId, StringComparison.Ordinal)))
        {
            var otherLocal = PilotPath.NormalizeLocalRoot(pair.LocalRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (LocalPathsOverlapForEditor(local, otherLocal)) return false;
            if ((connectionId is null || pair.ConnectionId == connectionId || string.IsNullOrWhiteSpace(pair.ConnectionId)) &&
                (DropboxPathsOverlapForEditor(dropbox, pair.DropboxRoot) ||
                 recovery is not null && DropboxPathsOverlapForEditor(recovery, pair.DropboxRoot))) return false;
        }
        return true;
    }

    private static bool LocalPathsOverlapForEditor(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool DropboxPathsOverlapForEditor(string left, string right)
    {
        var first = PilotPath.NormalizeDropboxRoot(left);
        var second = PilotPath.NormalizeDropboxRoot(right);
        if (first == "/" || second == "/") return true;
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith(second + "/", StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RecoveryPlacementAllowedForEditor(string pairRoot, string recoveryRoot)
    {
        var pair = PilotPath.NormalizeDropboxRoot(pairRoot);
        var recovery = PilotPath.NormalizeDropboxRoot(recoveryRoot);
        if (string.Equals(pair, recovery, StringComparison.OrdinalIgnoreCase)) return false;
        return pair == "/" || recovery.StartsWith(pair + "/", StringComparison.OrdinalIgnoreCase) ||
               !pair.StartsWith(recovery + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async void DeletePair_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || PairList.SelectedItem is not PairRow row) return;
        var warning = DeletePairWarning(row);
        if (!(PairRetirementConfirmationForTest?.Invoke(row) ??
              AppDialogWindow.Confirm(this, "Delete folder pair", $"Stop syncing “{row.Name}”?", warning,
                  "Delete pair", destructive: true))) return;
        PairList.IsEnabled = false;
        var retirementCommitted = false;
        var followUp = "retiring the folder pair";
        void ReportRetirement(string value, bool isError = false)
        {
            SharedPresentation.SetInlineFeedback(PairPageActionFeedback, value, isError);
            ShowStatus(value);
        }
        ReportRetirement("Removing this folder pair; local/cloud files, Recovery and history will be retained…");
        try
        {
            CollapseExpandedPair();
            var retiredPair = _pairSet.Pairs.Single(pair => pair.Id == row.Id);
            _pairSet = await _authorityHost.RetirePairAsync(row.Id);
            retirementCommitted = true;
            followUp = "unregistering the retired pair from Explorer";
            if (PairRetirementFollowUpForTest is not null)
                await PairRetirementFollowUpForTest("explorer-unregister");
            ExplorerIntegration.UnregisterRoot(retiredPair);
            followUp = "refreshing the folder-pair display";
            RefreshPairRows(_pairSet.SelectedPairId);
            followUp = "refreshing Recovery choices";
            if (PairRetirementFollowUpForTest is not null)
                await PairRetirementFollowUpForTest("recovery-choices");
            await RefreshRecoveryPairChoicesAsync(row.Id);
            followUp = "refreshing local monitors";
            if (PairRetirementFollowUpForTest is not null)
                await PairRetirementFollowUpForTest("local-monitors");
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
            if (_pairSet.Pairs.Count == 0) BeginNewPairEditor();
            followUp = "refreshing Explorer status";
            if (PairRetirementFollowUpForTest is not null)
                await PairRetirementFollowUpForTest("explorer-status");
            await RefreshExplorerIntegrationAsync();
            ReportRetirement($"{row.Name} was deleted as a pair. Local files, cloud files, Recovery and activity history were retained.");
        }
        catch (Exception exception)
        {
            if (!retirementCommitted)
            {
                ReportRetirement("The folder pair was not deleted: " + exception.Message, isError: true);
            }
            else
            {
                // Refresh is read-only. Never replay retirement to repair presentation
                // or imply that a committed mutation was rolled back by a later failure.
                var refreshWarning = string.Empty;
                try
                {
                    _pairSet = await _authorityHost.LoadPairsAsync();
                    RefreshPairRows(_pairSet.SelectedPairId);
                }
                catch (Exception refreshException)
                {
                    refreshWarning = " The folder-pair display also could not be refreshed: " + refreshException.Message;
                }
                ReportRetirement($"{row.Name} was deleted as a pair. Local files, cloud files, Recovery and activity history were retained. " +
                           $"Follow-up failed while {followUp}: {exception.Message}. Refresh Folder Pairs before continuing." +
                           refreshWarning, isError: true);
            }
        }
        finally { PairList.IsEnabled = true; }
    }

    internal static string DeletePairWarning(PairRow row) =>
        "Stops syncing this pair and removes its settings. Files on this computer and in the cloud " +
        "are not deleted. Recovery and activity history remain available." +
        (row.FilesOnDemandEnabled
            ? " Online-only files will be downloaded first so they remain available on this computer."
            : string.Empty);

    private void BeginNewPairEditor()
    {
        PairIdentityDisclosure.IsExpanded = true;
        _editingNewPair = true;
        _editingPairRootId = null;
        _duplicateSourcePairId = null;
        if (_pairAccountPicker is not null)
        {
            _updatingAccountPickers = true;
            try { _pairAccountPicker.SelectedValue = null; }
            finally { _updatingAccountPickers = false; }
        }
        RefreshAccountPickers();
        var (generation, token) = StartPairDraft();
        CollapseExpandedPair();
        PairList.SelectedItem = null;
        PairEditorTabs.SelectedIndex = 0;
        FolderSelectionSearchBox.Clear();
        FolderSelectionFilterBox.SelectedIndex = 0;
        NewPairEditorHost.Visibility = Visibility.Visible;
        MovePairEditorTo(NewPairEditorHost);
        EditorSyncNowButton.Visibility = Visibility.Collapsed;
        SavedDropboxActions.Visibility = Visibility.Collapsed;
        OpenLocalFolderButton.Visibility = Visibility.Collapsed;
        PairNameBox.Clear();
        LoadIgnoreRules([]);
        PairPriorityBox.Text = "0";
        PairEnabledBox.IsChecked = true;
        PairDeletionBox.IsChecked = false;
        PairFilesOnDemandBox.IsChecked = false;
        PairFilesOnDemandBox.IsEnabled = true;
        DisableFilesOnDemandButton.IsEnabled = false;
        AvailabilityPathBox.Clear();
        LocalRootBox.Clear();
        DropboxRootBox.Clear();
        DropboxFolderIdText.Text = "Not selected";
        _selectedDropboxFolder = null;
        _draftBrowseLocalRoot = null;
        _draftBrowseDropboxRoot = null;
        _draftPreview = null;
        _draftSelectionRules = [];
        _draftSelectionDefaultIncluded = false;
        _localGlobalSelection.Clear();
        _dropboxGlobalSelection.Clear();
        _globalDraftPlan = null;
        _replaceUnavailableLocalOnNextCheck = false;
        _replaceUnavailableDropboxOnNextCheck = false;
        _recoveryPreparationFailed = false;
        WholeRootSelectionBox.IsChecked = false;
        RecoveryRootBox.Text = SyncDropboxArtifactPaths.RecoveryContainerPath;
        RecoveryFolderIdText.Text = "Created and verified when you save";
        _selectedRecoveryFolder = null;
        RecoveryRetentionBox.SelectedIndex = 1;
        PairStorageBox.SelectedIndex = 0;
        SyncTypeBox.SelectedIndex = 0;
        PairPriorityChoice.SelectedIndex = 1;
        RecoveryUsageText.Text = "Nothing has been retained for this unsaved pair.";
        PairConnectionSummary.Text = PairAccountLabel;
        FolderSelectionRows.Clear();
        _folderSelectionSourceRows.Clear();
        FolderSelectionRoots.Clear();
        FolderSelectionSummary.Text = "Tick one folder on each side. Tick siblings or exclude children and Sync Shark derives the shared selective-sync parent.";
        LocalHierarchyNotice.Visibility = Visibility.Collapsed;
        DropboxHierarchyNotice.Visibility = Visibility.Collapsed;
        LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
        DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
        OpenLocalFolderButton.Content = CommandIcons.Label("Open in File Explorer", "folder");
        PairEditorHeading.Text = "Add folder pair";
        LocalRootTree.Visibility = Visibility.Visible;
        DropboxRootTree.Visibility = Visibility.Visible;
        LocalFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxRootBrowserActions.Visibility = Visibility.Visible;
        ChangePairRootsButton.Visibility = Visibility.Collapsed;
        PairEditorPanel.BringIntoView();
        ResetPairEditorViewport();
        PairNameBox.Focus();
        BeginPairEndpointStrip();
        ShowStatus("Ready to add a folder pair. Selection stays in this draft until Save and cannot broaden synchronization beforehand.");
        _ = InitializeNewPairEditorServicesAsync(generation, token);
    }

    private async Task InitializeNewPairEditorServicesAsync(int generation, CancellationToken cancellationToken)
    {
        await InitializeNewPairRootBrowserAsync(generation, cancellationToken);
        if (!IsCurrentPairDraft(generation)) return;
        UpdatePairSaveAvailability();
    }

    private (int Generation, CancellationToken Token) StartPairDraft()
    {
        _pairDraftCancellation?.Cancel();
        _pairDraftCancellation?.Dispose();
        _pairDraftCancellation = CancellationTokenSource.CreateLinkedTokenSource(_pairRootBrowserLifetime.Token);
        _pairDraftConnectionId = SelectedPairConnectionId;
        LocalPathEntryBox.IsEnabled = true;
        UpdateLocalPathAvailability();
        return (Interlocked.Increment(ref _pairEditorGeneration), _pairDraftCancellation.Token);
    }

    private void EndPairDraft()
    {
        CloseCreateLocalFolderDialog();
        EndPairEndpointStrip();
        _pairValidationReason = null;
        HidePairEditorStatus();
        _editingNewPair = false;
        _editingPairRootId = null;
        _duplicateSourcePairId = null;
        Interlocked.Increment(ref _pairEditorGeneration);
        _pairDraftCancellation?.Cancel();
        _pairDraftCancellation?.Dispose();
        _pairDraftCancellation = null;
        _draftPreview = null;
        _draftSelectionRules = [];
        _draftBrowseLocalRoot = null;
        _draftDirectoryStorageRules = null;
        _draftNeedsCloudFiles = false;
        _draftBrowseDropboxRoot = null;
        _selectedDropboxRootNode = null;
        _pairDraftConnectionId = null;
        _globalDraftPlan = null;
        _replaceUnavailableLocalOnNextCheck = false;
        _replaceUnavailableDropboxOnNextCheck = false;
        _recoveryPreparationFailed = false;
    }

    private bool PairLocationDraftActive => _editingNewPair || _editingPairRootId is not null;

    private bool IsCurrentPairDraft(int generation) =>
        !_closing && PairLocationDraftActive && generation == _pairEditorGeneration &&
        (_editingNewPair
            ? PairList.SelectedItem is null
            : PairList.SelectedItem is PairRow selected &&
              string.Equals(selected.Id, _editingPairRootId, StringComparison.Ordinal)) &&
        string.Equals(_pairDraftConnectionId, SelectedPairConnectionId,
            StringComparison.Ordinal);

    private async Task InitializeNewPairRootBrowserAsync(int generation, CancellationToken cancellationToken,
        string? localPathToExpand = null)
    {
        LocalRootNodes.Clear();
        var localRoots = LocalRootNodeFactoryForTest is null
            ? await LocalFolderNode.CreateDriveRootsAsync(cancellationToken)
            : LocalRootNodeFactoryForTest(localPathToExpand);
        if (!IsCurrentPairDraft(generation)) return;
        foreach (var root in localRoots) LocalRootNodes.Add(root);
        if (!string.IsNullOrWhiteSpace(localPathToExpand))
            await ExpandLocalTreeToSavedPathAsync(localPathToExpand, cancellationToken);
        RefreshGlobalSelectionTrees();
        DropboxRootNodes.Clear();
        _pairDropboxBrowser = null;
        try
        {
            _pairDropboxBrowser = PairFolderBrowserFactoryForTest is null
                ? await _authorityHost.CreateFolderBrowserAsync(cancellationToken, SelectedPairConnectionId)
                : await PairFolderBrowserFactoryForTest(cancellationToken);
            if (!IsCurrentPairDraft(generation)) return;
            var rootIdentity = PairAccountBinding?.Provider == SyncProviders.GoogleDrive
                ? new DropboxFolderEntry("My Drive", "/", "root")
                : PairAccountBinding?.RootNamespaceId is { Length: > 0 } namespaceId
                ? new DropboxFolderEntry(ProviderPresentation.Name, "/", PilotPath.DropboxNamespaceRootIdentity(namespaceId))
                : null;
            var root = DropboxFolderNode.CreateRoot(DropboxFolderGraphBudget.CreateDefault(), rootIdentity,
                rootIdentity?.Name ?? ProviderPresentation.Name);
            DropboxRootNodes.Add(root);
            _selectedDropboxRootNode = root;
            await LoadDropboxRootNodeAsync(root, refresh: true, generation, cancellationToken);
            if (!IsCurrentPairDraft(generation)) return;
            RefreshGlobalSelectionTrees();
            if (NewPairEditorHost.IsVisible)
                ShowStatus("Tick one folder on each side. Browsing and checkbox changes remain a draft until Save pair.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsCurrentPairDraft(generation)) return;
            ShowStatus(ProviderPresentation.Name + " folders are unavailable in the pair editor: " + exception.Message);
        }
    }

    private async void LocalRootTree_Expanded(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (e.OriginalSource is not TreeViewItem { DataContext: LocalFolderNode node }) return;
        node.IsExpanded = true;
        try
        {
            await node.LoadChildrenAsync(_pairDraftCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { return; }
        RefreshLocalGlobalSelectionTree();
        ApplyGlobalFolderTreeFilter();
    }

    private void LocalRootTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!PairLocationDraftActive || e.NewValue is not LocalFolderNode { IsPlaceholder: false } node) return;
        AvailabilityPathBox.Text = node.Path;
    }

    private async void DropboxRootTree_Expanded(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!PairLocationDraftActive || _pairDraftCancellation is null) return;
        var generation = _pairEditorGeneration;
        if (e.OriginalSource is DropboxFolderNode node)
            await LoadDropboxRootNodeAsync(node, refresh: false, generation, _pairDraftCancellation.Token);
        else if (e.OriginalSource is TreeViewItem { DataContext: DropboxFolderNode item })
            await LoadDropboxRootNodeAsync(item, refresh: false, generation, _pairDraftCancellation.Token);
    }

    private void DropboxRootTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!PairLocationDraftActive || e.NewValue is not DropboxFolderNode node) return;
        _selectedDropboxRootNode = node;
        UpdateDropboxRootLoadMoreButton();
        if (node.Entry is not null) AvailabilityPathBox.Text = node.Entry.Path;
    }

    private void LocalGlobalFolderCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LocalFolderNode { IsPlaceholder: false } node }) return;
        if (_runActive)
        {
            ((CheckBox)sender).IsChecked = node.IsChecked;
            ShowStatus("Folder choices are locked until the active sync finishes. You can still browse and expand both trees.");
            e.Handled = true;
            return;
        }
        if (_updatingGlobalTreeSelection) return;
        var include = node.IsChecked != true;
        ApplyLocalGlobalSelection(node.Path, include);
        e.Handled = true;
    }

    private void ApplyLocalGlobalSelection(string path, bool include)
    {
        var localBefore = _localGlobalSelection.CreatePlan();
        var dropboxBefore = _dropboxGlobalSelection.CreatePlan();
        var localRulesBefore = _localGlobalSelection.Rules.ToArray();
        var dropboxRulesBefore = _dropboxGlobalSelection.Rules.ToArray();
        var replacingUnavailableLocal = _replaceUnavailableLocalOnNextCheck;
        if (replacingUnavailableLocal)
        {
            _localGlobalSelection.Clear();
            _replaceUnavailableLocalOnNextCheck = false;
            LocalHierarchyNotice.Visibility = Visibility.Collapsed;
        }
        _localGlobalSelection.Set(path, include);
        if (!replacingUnavailableLocal && FolderTreeSelectionDraft.Equivalent(localBefore, dropboxBefore) && localBefore is not null &&
            dropboxBefore is not null &&
            MapLocalPathToDropbox(path, localBefore.RootPath, dropboxBefore.RootPath) is { } matchingPath)
        {
            var matching = FindDropboxNode(matchingPath);
            _dropboxGlobalSelection.Set(matchingPath, include,
                matching?.Entry is null ? null : "dropbox:" + matching.Entry.FolderId);
        }
        if (RestoreUnrepresentableLinkedSelection(localBefore, dropboxBefore, localRulesBefore,
                dropboxRulesBefore))
        {
            if (replacingUnavailableLocal)
            {
                _replaceUnavailableLocalOnNextCheck = true;
                LocalHierarchyNotice.Visibility = Visibility.Visible;
                LocalHierarchyNoticeText.Text = "Saved folder no longer exists. Choose a replacement.";
            }
            return;
        }
        RecomputeGlobalDraftPlan();
    }

    internal void SetGlobalLocalSelectionForTest(string path, bool include) =>
        ApplyLocalGlobalSelection(path, include);

    internal void SetGlobalDropboxSelectionForTest(string path, bool include)
    {
        var node = FindDropboxNode(path) ?? throw new InvalidOperationException("The Dropbox test folder is not loaded.");
        if ((node.IsChecked == true) == include) return;
        var checkbox = new CheckBox { DataContext = node };
        DropboxGlobalFolderCheck_Click(checkbox,
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private void DropboxGlobalFolderCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DropboxFolderNode { IsPlaceholder: false } node } ||
            node.Entry is null) return;
        if (_runActive)
        {
            ((CheckBox)sender).IsChecked = node.IsChecked;
            ShowStatus("Folder choices are locked until the active sync finishes. You can still browse and expand both trees.");
            e.Handled = true;
            return;
        }
        if (_updatingGlobalTreeSelection) return;
        var include = node.IsChecked != true;
        var localBefore = _localGlobalSelection.CreatePlan();
        var dropboxBefore = _dropboxGlobalSelection.CreatePlan();
        var localRulesBefore = _localGlobalSelection.Rules.ToArray();
        var dropboxRulesBefore = _dropboxGlobalSelection.Rules.ToArray();
        var replacingUnavailableDropbox = _replaceUnavailableDropboxOnNextCheck ||
                                          (dropboxBefore is not null &&
                                           FindDropboxNode(dropboxBefore.RootPath)?.Entry is null);
        if (replacingUnavailableDropbox)
        {
            _dropboxGlobalSelection.Clear();
            _replaceUnavailableDropboxOnNextCheck = false;
            DropboxHierarchyNotice.Visibility = Visibility.Collapsed;
            DropboxHierarchyNotice.ToolTip = null;
        }
        _dropboxGlobalSelection.Set(node.Entry.Path, include, "dropbox:" + node.Entry.FolderId);
        if (!replacingUnavailableDropbox && FolderTreeSelectionDraft.Equivalent(localBefore, dropboxBefore) && localBefore is not null &&
            dropboxBefore is not null &&
            MapDropboxPathToLocal(node.Entry.Path, dropboxBefore.RootPath, localBefore.RootPath) is { } matchingPath)
        {
            var matching = FindLocalNode(matchingPath);
            _localGlobalSelection.Set(matching?.Path ?? matchingPath, include);
        }
        if (RestoreUnrepresentableLinkedSelection(localBefore, dropboxBefore, localRulesBefore,
                dropboxRulesBefore))
        {
            if (replacingUnavailableDropbox)
            {
                _replaceUnavailableDropboxOnNextCheck = true;
                DropboxHierarchyNotice.Visibility = Visibility.Visible;
                DropboxHierarchyNoticeText.Text = "Saved folder no longer exists. Choose a replacement.";
            }
            e.Handled = true;
            return;
        }
        RecomputeGlobalDraftPlan();
        e.Handled = true;
    }

    private bool RestoreUnrepresentableLinkedSelection(FolderTreeSelectionPlan? localBefore,
        FolderTreeSelectionPlan? dropboxBefore, IReadOnlyList<FolderTreeAbsoluteRule> localRulesBefore,
        IReadOnlyList<FolderTreeAbsoluteRule> dropboxRulesBefore)
    {
        if (!FolderTreeSelectionDraft.Equivalent(localBefore, dropboxBefore) || localBefore is null ||
            dropboxBefore is null || FolderTreeSelectionDraft.Equivalent(_localGlobalSelection.CreatePlan(),
                _dropboxGlobalSelection.CreatePlan())) return false;
        _localGlobalSelection.Restore(localRulesBefore);
        _dropboxGlobalSelection.Restore(dropboxRulesBefore);
        RecomputeGlobalDraftPlan();
        const string message = "That sibling cannot be added because the matching folders on this computer and in the cloud have different names. The existing selection was kept. Choose new paired folders first if you want a wider parent.";
        FolderSelectionSummary.Text = message;
        ShowStatus(message);
        return true;
    }

    private void FolderChevron_Click(object sender, RoutedEventArgs e) => e.Handled = true;

    private void FolderTreeItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item || e.OriginalSource is not DependencyObject source) return;
        // WPF raises MouseDoubleClick on every TreeViewItem between the clicked row and the tree root.
        // Only the nearest row may act, otherwise one double click collapses the whole expanded
        // ancestor chain back to the drive root.
        if (!ReferenceEquals(FindAncestor<TreeViewItem>(source), item)) return;
        if (FindAncestor<CheckBox>(source) is not null || FindAncestor<ToggleButton>(source) is not null) return;
        switch (item.DataContext)
        {
            case LocalFolderNode { CanExpand: true } local:
                local.IsExpanded = !local.IsExpanded;
                item.IsExpanded = local.IsExpanded;
                e.Handled = true;
                break;
            case DropboxFolderNode { CanExpand: true } dropbox:
                dropbox.IsExpanded = !dropbox.IsExpanded;
                item.IsExpanded = dropbox.IsExpanded;
                e.Handled = true;
                break;
        }
    }

    private void LocalPathEntry_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            GoToLocalPath_Click(sender, new RoutedEventArgs());
            return;
        }
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        LocalPathEntryBox.Clear();
        LocalRootTree.Focus();
        ShowStatus("Path entry cancelled. The folder tree is unchanged.");
    }

    /// <summary>
    /// Opens one typed local folder by expanding only the folders along that path. Nothing is ticked
    /// for sync, no pair row is selected, and no drive is scanned beyond the typed ancestors.
    /// </summary>
    private async void GoToLocalPath_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!PairLocationDraftActive)
        {
            ShowStatus("Open a folder pair before typing a folder path.");
            return;
        }
        var typed = LocalPathEntryBox.Text.Trim();
        if (typed.Length == 0)
        {
            ShowStatus("Type a full folder path, such as C:\\Users\\You\\Documents, to open it here.");
            LocalPathEntryBox.Focus();
            return;
        }
        if (!Path.IsPathFullyQualified(typed))
        {
            ShowStatus("Type a full path including the drive, such as C:\\Users\\You\\Documents.");
            return;
        }
        string full;
        try
        {
            full = Path.GetFullPath(typed);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowStatus("That folder path is not valid Windows syntax: " + typed);
            return;
        }
        if (!LocalRootNodes.Any(root => !root.IsPlaceholder && LocalPathWithin(full, root.Path)))
        {
            ShowStatus($"Nothing is loaded for {full}, so nothing was scanned. Open that drive or folder first, or use File Explorer.");
            return;
        }
        if (!Directory.Exists(full))
        {
            ShowStatus($"{full} is not an existing folder. Nothing was loaded and nothing changed.");
            return;
        }
        LocalPathEntryBox.IsEnabled = false;
        GoToLocalPathButton.IsEnabled = false;
        var generation = _pairEditorGeneration;
        try
        {
            await ExpandLocalTreeToSavedPathAsync(full, _pairDraftCancellation?.Token ?? CancellationToken.None);
            if (!IsCurrentPairDraft(generation)) return;
            ShowStatus(FindLocalNode(full) is null
                ? $"Only the folders along {full} were read, and the exact folder is not listed. Continue in the tree."
                : $"Showing {full}. Typing a path only opens the tree; no folder was ticked.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            if (IsCurrentPairDraft(generation))
                ShowStatus($"Windows did not allow {full} to be read: " + error.Message);
        }
        finally
        {
            if (IsCurrentPairDraft(generation))
            {
                LocalPathEntryBox.IsEnabled = true;
                UpdateLocalPathAvailability();
            }
        }
    }

    private void UpdateLocalPathAvailability()
    {
        if (GoToLocalPathButton is null || LocalPathEntryBox is null) return;
        GoToLocalPathButton.IsEnabled = LocalPathEntryBox.IsEnabled && !string.IsNullOrWhiteSpace(LocalPathEntryBox.Text);
        GoToLocalPathButton.ToolTip = !LocalPathEntryBox.IsEnabled ? "The requested folder path is still being opened."
            : string.IsNullOrWhiteSpace(LocalPathEntryBox.Text) ? "Enter a full local folder path first."
            : "Open the entered path in the folder tree without selecting it for sync.";
    }

    private void RecomputeGlobalDraftPlan()
    {
        var local = _localGlobalSelection.CreatePlan();
        var dropbox = _dropboxGlobalSelection.CreatePlan();
        var knownDropboxRoot = _selectedDropboxFolder;
        _globalDraftPlan = null;
        _draftBrowseLocalRoot = local?.RootPath;
        if (local is not null) ApplyLocalEndpointBrowse(local.RootPath);
        _draftBrowseDropboxRoot = null;
        _selectedDropboxFolder = null;
        LocalRootBox.Text = local?.RootPath ?? string.Empty;
        DropboxRootBox.Text = dropbox?.RootPath ?? string.Empty;
        DropboxFolderIdText.Text = "Not selected";

        var resolvedDropboxRoot = dropbox is null
            ? null
            : FindDropboxNode(dropbox.RootPath)?.Entry ??
              (knownDropboxRoot is not null && string.Equals(
                  PilotPath.NormalizeDropboxRoot(knownDropboxRoot.Path),
                  PilotPath.NormalizeDropboxRoot(dropbox.RootPath), StringComparison.OrdinalIgnoreCase)
                  ? knownDropboxRoot
                  : null);
        if (resolvedDropboxRoot is { } dropboxRoot)
        {
            _draftBrowseDropboxRoot = dropboxRoot;
            _selectedDropboxFolder = dropboxRoot;
            DropboxFolderIdText.Text = dropboxRoot.FolderId;
            if (_endpointSession is { } endpointSession && endpointSession.Draft(SyncPairSide.Right).IsCloud &&
                endpointSession.Draft(SyncPairSide.Right).ConnectionId == SelectedPairConnectionId)
                endpointSession.SetCloudFolder(SyncPairSide.Right, dropboxRoot.FolderId, dropboxRoot.Path);
        }

        if (local is null || dropbox is null)
            FolderSelectionSummary.Text = local is null && dropbox is null
                ? "Tick one folder on this computer and one in the selected cloud account."
                : local is null
                    ? "The cloud folder is selected. Tick its matching folder on this computer."
                    : "This computer is selected. Tick its matching folder in the cloud.";
        else if (!FolderTreeSelectionDraft.Equivalent(local, dropbox))
            FolderSelectionSummary.Text =
                "The two sides currently describe different subfolders. Match the ticks on both sides before saving.";
        else if (_selectedDropboxFolder is null)
            FolderSelectionSummary.Text = "Expand the selected cloud branch so its stable folder identity can be verified.";
        else
        {
            _draftSelectionDefaultIncluded = local.DefaultIncluded;
            _draftSelectionRules = dropbox.Rules;
            _globalDraftPlan = new PairDraftBindingPlan(local.RootPath, _selectedDropboxFolder,
                local.DefaultIncluded, dropbox.Rules);
            WholeRootSelectionBox.IsChecked = local.Rules.Count == 0 ? local.DefaultIncluded : null;
            FolderSelectionSummary.Text = local.Rules.Count == 0 && local.DefaultIncluded
                ? "Everything inside the selected folders is included."
                : $"Selective sync is enabled with {local.Rules.Count:N0} folder rule(s). Excluded folders stay out of sync.";
            if (_editingNewPair && string.IsNullOrWhiteSpace(PairNameBox.Text))
                PairNameBox.Text = LocalFolderDisplayName(local.RootPath);
        }

        RefreshGlobalSelectionTrees();
        UpdatePairSaveAvailability();
    }

    private void RefreshGlobalSelectionTrees()
    {
        _updatingGlobalTreeSelection = true;
        try
        {
            RefreshLocalGlobalSelectionTree();
            RefreshDropboxGlobalSelectionTree();
        }
        finally { _updatingGlobalTreeSelection = false; }
        ApplyGlobalFolderTreeFilter();
    }

    private void ApplyGlobalFolderTreeFilter()
    {
        if (FolderSelectionSearchBox is null || FolderSelectionFilterBox is null ||
            FolderSearchScopeText is null) return;
        var query = FolderSelectionSearchBox.Text.Trim();
        var filter = (FolderSelectionFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ??
                     "All folders";
        var localPlan = _localGlobalSelection.CreatePlan();
        var dropboxPlan = _dropboxGlobalSelection.CreatePlan();
        var localVisible = LocalRootNodes.Count(node => ApplyLocalNodeFilter(node, query, filter,
            localPlan, dropboxPlan));
        var dropboxVisible = DropboxRootNodes.Count(node => ApplyDropboxNodeFilter(node, query, filter,
            localPlan, dropboxPlan));
        FolderSearchScopeText.Text = query.Length > 0 || !string.Equals(filter, "All folders",
            StringComparison.Ordinal)
            ? localVisible + dropboxVisible == 0
                ? "No loaded folders match. Clear the search or filter, or expand a folder to load more."
                : "Showing matches from loaded folders. Expand or load a folder to search deeper."
            : "Searches the folders currently loaded. Expand or load a folder to search deeper.";
    }

    private bool ApplyLocalNodeFilter(LocalFolderNode node, string query, string filter,
        FolderTreeSelectionPlan? localPlan, FolderTreeSelectionPlan? dropboxPlan)
    {
        var childMatches = node.Children.Where(child => !child.IsPlaceholder).Select(child =>
            ApplyLocalNodeFilter(child, query, filter, localPlan, dropboxPlan)).ToArray();
        var childMatch = childMatches.Any(value => value);
        var searchMatch = query.Length == 0 || node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          node.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
        var needsCounterpart = filter is "Mixed" or "Only on this computer";
        var counterpart = needsCounterpart && localPlan is not null && dropboxPlan is not null &&
                          LocalPathWithin(node.Path, localPlan.RootPath)
            ? MapLocalPathToDropbox(node.Path, localPlan.RootPath, dropboxPlan.RootPath)
            : null;
        var counterpartLoaded = counterpart is not null && FindDropboxNode(counterpart) is not null;
        var filterMatch = filter switch
        {
            "Excluded" => node.IsChecked == false,
            "Needs attention" => NodeStatusNeedsAttention(node.Status),
            "Online-only" => node.Status.Contains("Online-only", StringComparison.OrdinalIgnoreCase),
            "Mixed" => counterpartLoaded,
            "Only on this computer" => counterpart is not null && !counterpartLoaded,
            "Only in cloud" => false,
            _ => true
        };
        var visible = node.IsPlaceholder || searchMatch && filterMatch || childMatch;
        node.SetFilterVisible(visible);
        return visible;
    }

    private bool ApplyDropboxNodeFilter(DropboxFolderNode node, string query, string filter,
        FolderTreeSelectionPlan? localPlan, FolderTreeSelectionPlan? dropboxPlan)
    {
        var childMatches = node.Children.Where(child => !child.IsPlaceholder).Select(child =>
            ApplyDropboxNodeFilter(child, query, filter, localPlan, dropboxPlan)).ToArray();
        var childMatch = childMatches.Any(value => value);
        var searchMatch = query.Length == 0 || node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          node.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
        var needsCounterpart = filter is "Mixed" or "Only in cloud";
        var counterpart = needsCounterpart && node.Entry is not null && localPlan is not null &&
                          dropboxPlan is not null &&
                          DropboxPathWithin(node.Entry.Path, dropboxPlan.RootPath)
            ? MapDropboxPathToLocal(node.Entry.Path, dropboxPlan.RootPath, localPlan.RootPath)
            : null;
        var counterpartLoaded = counterpart is not null && FindLocalNode(counterpart) is not null;
        var filterMatch = filter switch
        {
            "Excluded" => node.IsChecked == false,
            "Needs attention" => NodeStatusNeedsAttention(node.Status),
            "Online-only" => false,
            "Mixed" => counterpartLoaded,
            "Only in cloud" => counterpart is not null && !counterpartLoaded,
            "Only on this computer" => false,
            _ => true
        };
        var visible = node.IsPlaceholder || searchMatch && filterMatch || childMatch;
        node.SetFilterVisible(visible);
        return visible;
    }

    private static bool NodeStatusNeedsAttention(string status) =>
        status.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("blocked", StringComparison.OrdinalIgnoreCase);

    private void RefreshLocalGlobalSelectionTree()
    {
        foreach (var node in Descendants(LocalRootNodes))
            if (!node.IsPlaceholder)
            {
                node.SetChecked(_localGlobalSelection.CheckState(node.Path));
                node.SetStorageChoice(StorageChoiceForLocal(node.Path));
            }
    }

    private void RefreshDropboxGlobalSelectionTree()
    {
        foreach (var node in Descendants(DropboxRootNodes))
            if (!node.IsPlaceholder && node.Entry is not null)
            {
                node.SetChecked(_dropboxGlobalSelection.CheckState(node.Entry.Path));
                node.SetStorageChoice(StorageChoiceForDropbox(node.Entry.Path));
            }
    }

    private static IEnumerable<LocalFolderNode> Descendants(IEnumerable<LocalFolderNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Descendants(node.Children)) yield return child;
        }
    }

    private static IEnumerable<DropboxFolderNode> Descendants(IEnumerable<DropboxFolderNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Descendants(node.Children)) yield return child;
        }
    }

    private LocalFolderNode? FindLocalNode(string path) => Descendants(LocalRootNodes).FirstOrDefault(node =>
        !node.IsPlaceholder && string.Equals(Path.GetFullPath(node.Path), Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase));

    private DropboxFolderNode? FindDropboxNode(string path)
    {
        var normalized = PilotPath.NormalizeDropboxRoot(path);
        return Descendants(DropboxRootNodes).FirstOrDefault(node => !node.IsPlaceholder && node.Entry is not null &&
            string.Equals(PilotPath.NormalizeDropboxRoot(node.Entry.Path), normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool LocalPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool DropboxPathWithin(string path, string root)
    {
        var normalizedPath = PilotPath.NormalizeDropboxRoot(path);
        var normalizedRoot = PilotPath.NormalizeDropboxRoot(root);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedRoot == "/" || normalizedPath.StartsWith(normalizedRoot + "/",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string RelativeDropboxPath(string root, string path)
    {
        var normalizedRoot = PilotPath.NormalizeDropboxRoot(root);
        var normalizedPath = PilotPath.NormalizeDropboxRoot(path);
        return normalizedRoot == "/" ? normalizedPath.TrimStart('/') :
            string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : normalizedPath[(normalizedRoot.Length + 1)..];
    }

    private static string CombineDropboxPath(string root, string relative)
    {
        var normalizedRoot = PilotPath.NormalizeDropboxRoot(root);
        if (string.IsNullOrWhiteSpace(relative)) return normalizedRoot;
        var normalizedRelative = PilotPath.NormalizeRelative(relative);
        return normalizedRoot == "/"
            ? "/" + normalizedRelative
            : normalizedRoot + "/" + normalizedRelative;
    }

    private static string? MapLocalPathToDropbox(string path, string localRoot, string dropboxRoot)
    {
        var common = CommonLocalParent(path, localRoot);
        if (common is null) return null;
        var localCursor = Path.GetFullPath(localRoot);
        var remoteCursor = PilotPath.NormalizeDropboxRoot(dropboxRoot);
        while (!string.Equals(localCursor, common, StringComparison.OrdinalIgnoreCase))
        {
            localCursor = Directory.GetParent(localCursor)?.FullName ?? string.Empty;
            remoteCursor = DropboxParent(remoteCursor) ?? string.Empty;
            if (localCursor.Length == 0 || remoteCursor.Length == 0) return null;
        }
        var rawRelative = Path.GetRelativePath(common, Path.GetFullPath(path));
        var relative = rawRelative == "." ? string.Empty : PilotPath.NormalizeRelative(rawRelative);
        return CombineDropboxPath(remoteCursor, relative);
    }

    private static string? MapDropboxPathToLocal(string path, string dropboxRoot, string localRoot)
    {
        var common = CommonDropboxParent(path, dropboxRoot);
        if (common is null) return null;
        var remoteCursor = PilotPath.NormalizeDropboxRoot(dropboxRoot);
        var localCursor = Path.GetFullPath(localRoot);
        while (!string.Equals(remoteCursor, common, StringComparison.OrdinalIgnoreCase))
        {
            remoteCursor = DropboxParent(remoteCursor) ?? string.Empty;
            localCursor = Directory.GetParent(localCursor)?.FullName ?? string.Empty;
            if (remoteCursor.Length == 0 || localCursor.Length == 0) return null;
        }
        var relative = RelativeDropboxPath(common, path);
        return Path.GetFullPath(Path.Combine(localCursor,
            relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string? CommonLocalParent(string first, string second)
    {
        var candidate = Path.GetFullPath(first);
        var other = Path.GetFullPath(second);
        while (!LocalPathWithin(other, candidate))
        {
            candidate = Directory.GetParent(candidate)?.FullName ?? string.Empty;
            if (candidate.Length == 0) return null;
        }
        return candidate;
    }

    private static string? CommonDropboxParent(string first, string second)
    {
        var candidate = PilotPath.NormalizeDropboxRoot(first);
        var other = PilotPath.NormalizeDropboxRoot(second);
        while (!DropboxPathWithin(other, candidate))
        {
            candidate = DropboxParent(candidate) ?? string.Empty;
            if (candidate.Length == 0) return null;
        }
        return candidate;
    }

    private static string? DropboxParent(string path)
    {
        var normalized = PilotPath.NormalizeDropboxRoot(path);
        if (normalized == "/") return null;
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "/" : normalized[..separator];
    }

    private async Task<DropboxNodeLoadOutcome> LoadDropboxRootNodeAsync(DropboxFolderNode node, bool refresh, int generation,
        CancellationToken cancellationToken)
    {
        if (_pairDropboxBrowser is null) return DropboxNodeLoadOutcome.Unavailable;
        try { await node.LoadGate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return DropboxNodeLoadOutcome.Cancelled; }
        if (!IsCurrentPairDraft(generation))
        {
            node.LoadGate.Release();
            return DropboxNodeLoadOutcome.Stale;
        }
        if (!refresh && node.IsLoaded)
        {
            node.LoadGate.Release();
            return DropboxNodeLoadOutcome.Complete;
        }
        var version = Interlocked.Increment(ref _pairDropboxRootLoadVersion);
        node.ActiveOperationVersion = version;
        node.IsLoading = true;
        try
        {
            var page = await _pairDropboxBrowser.ListFoldersAsync(node.Entry?.FolderId,
                cancellationToken);
            if (!IsCurrentPairDraft(generation) || node.ActiveOperationVersion != version)
                return DropboxNodeLoadOutcome.Stale;
            node.ReplacePage(page, isSearchResults: false);
            await LoadRemainingDraftFolderPagesAsync(node, _pairDropboxBrowser, generation, version, cancellationToken);
            if (!IsCurrentPairDraft(generation) || node.ActiveOperationVersion != version)
                return DropboxNodeLoadOutcome.Stale;
            RefreshDropboxGlobalSelectionTree();
            ApplyGlobalFolderTreeFilter();
            DropboxRootBrowserStatus.Text = node.PaginationBlockedReason ??
                                            (node.ContinueCursor is null
                                                 ? $"Loaded {node.LoadedChildCount:N0} folder(s)."
                                                 : $"Loaded {node.LoadedChildCount:N0} folder(s); more are available.");
            return DropboxNodeLoadOutcome.Complete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DropboxNodeLoadOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            if (!IsCurrentPairDraft(generation)) return DropboxNodeLoadOutcome.Stale;
            var detail = "This " + ProviderPresentation.Name + " folder could not be expanded: " + exception.Message;
            ShowPairEditorStatus(detail, isError: true);
            // This browser request may finish after the user starts or navigates to
            // a sync. Retain its diagnostic without replacing that operation's output.
            ReportStatus(detail, presentOperation: false);
            DropboxRootBrowserStatus.Text = "Folder list unavailable: " + exception.Message;
            return DropboxNodeLoadOutcome.Failed;
        }
        finally
        {
            if (node.ActiveOperationVersion == version) node.IsLoading = false;
            if (IsCurrentPairDraft(generation)) UpdateDropboxRootLoadMoreButton();
            node.LoadGate.Release();
        }
    }

    private async void LoadMoreDropboxRoot_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await LoadMoreDropboxRootAsync();
    }

    private async Task LoadMoreDropboxRootAsync()
    {
        if (!PairLocationDraftActive || _pairDropboxBrowser is null || _pairDraftCancellation is null) return;
        var node = _selectedDropboxRootNode ?? DropboxRootNodes.FirstOrDefault();
        if (node is null || node.ContinueCursor is null) return;
        if (!node.CanLoadMore)
        {
            DropboxRootBrowserStatus.Text = node.PaginationBlockedReason ?? "No more folders are available.";
            UpdateDropboxRootLoadMoreButton();
            return;
        }
        var generation = _pairEditorGeneration;
        await ContinueDropboxNodeAsync(node, generation, _pairDraftCancellation.Token);
    }

    internal async Task<DropboxFolderNode> PrepareDropboxDraftBrowserForTestAsync(
        IDropboxFolderBrowserService browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        EndPairDraft();
        _editingNewPair = true;
        PairList.SelectedItem = null;
        var (generation, token) = StartPairDraft();
        _pairDropboxBrowser = browser;
        DropboxRootNodes.Clear();
        var root = DropboxFolderNode.CreateRoot(DropboxFolderGraphBudget.CreateDefault());
        DropboxRootNodes.Add(root);
        _selectedDropboxRootNode = root;
        await LoadDropboxRootNodeAsync(root, refresh: true, generation, token);
        return root;
    }

    internal async Task<DropboxFolderNode> PrepareDropboxBrowserForCurrentDraftForTestAsync(
        IDropboxFolderBrowserService browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        if (!PairLocationDraftActive || _pairDraftCancellation is null)
            throw new InvalidOperationException("A pair draft must be open before loading its Dropbox test hierarchy.");
        _pairDropboxBrowser = browser;
        DropboxRootNodes.Clear();
        var root = DropboxFolderNode.CreateRoot(DropboxFolderGraphBudget.CreateDefault());
        DropboxRootNodes.Add(root);
        _selectedDropboxRootNode = root;
        await LoadDropboxRootNodeAsync(root, refresh: true, _pairEditorGeneration, _pairDraftCancellation.Token);
        RefreshDropboxGlobalSelectionTree();
        ApplyGlobalFolderTreeFilter();
        return root;
    }

    internal Task LoadMoreDropboxRootForTestAsync() => LoadMoreDropboxRootAsync();
    internal Task ExpandDropboxTreeForTestAsync(string path) =>
        ExpandDropboxTreeToSavedPathAsync(path, _pairEditorGeneration,
            _pairDraftCancellation?.Token ?? CancellationToken.None);
    internal void ExpandLocalTreeForTest(string path)
    {
        var target = Path.GetFullPath(path);
        var current = LocalRootNodes.FirstOrDefault(root => LocalPathWithin(target, root.Path));
        if (current is null) return;
        current.IsExpanded = true;
        current.LoadChildren();
        var relative = Path.GetRelativePath(current.Path, target);
        if (relative != ".")
        {
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current.Children.FirstOrDefault(child => !child.IsPlaceholder &&
                    string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (next is null) break;
                current = next;
                current.IsExpanded = true;
                current.LoadChildren();
            }
        }
        RefreshLocalGlobalSelectionTree();
        BringTreeNodeIntoView(LocalRootTree, current);
    }

    private void UpdateDropboxRootLoadMoreButton()
    {
        if (LoadMoreDropboxRootButton is null) return;
        var node = _selectedDropboxRootNode ?? DropboxRootNodes.FirstOrDefault();
        var visible = node?.ContinueCursor is not null;
        LoadMoreDropboxRootButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreDropboxRootButton.IsEnabled = visible && node?.IsLoading != true && node?.CanLoadMore == true;
        LoadMoreDropboxRootButton.ToolTip = node?.PaginationBlockedReason ??
                                            "Load the next cloud folder page for this location.";
    }

    private void ResetDraftSelectionForRoots()
    {
        if (!PairLocationDraftActive) return;
        Interlocked.Increment(ref _pairDraftPreviewVersion);
        _draftPreview = null;
        _draftSelectionRules = [];
        _draftSelectionDefaultIncluded = false;
        WholeRootSelectionBox.IsChecked = false;
        FolderSelectionRows.Clear();
        _folderSelectionSourceRows.Clear();
        FolderSelectionRoots.Clear();
        LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
        DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
        LocalFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxFolderSelectionTree.Visibility = Visibility.Collapsed;
        LocalRootTree.Visibility = Visibility.Visible;
        DropboxRootTree.Visibility = Visibility.Visible;
        DropboxRootBrowserActions.Visibility = Visibility.Visible;
        FolderSelectionSummary.Text = _draftBrowseLocalRoot is null || _draftBrowseDropboxRoot is null
            ? "Choose a browse folder on both sides. Nothing is committed while you browse."
            : "Loading the matching folder structure from both sides…";
    }

    private async Task LoadNewPairDraftPreviewAsync()
    {
        if (!PairLocationDraftActive || _pairDraftCancellation is null || _draftBrowseLocalRoot is null ||
            _draftBrowseDropboxRoot is null) return;
        var generation = _pairEditorGeneration;
        var version = Interlocked.Increment(ref _pairDraftPreviewVersion);
        var localRoot = _draftBrowseLocalRoot;
        var dropboxRoot = _draftBrowseDropboxRoot;
        LocalFolderSelectionTree.IsEnabled = false;
        DropboxFolderSelectionTree.IsEnabled = false;
        FolderSelectionSummary.Text = "Reading folder names and presence from both sides without copying or hashing file contents…";
        try
        {
            var preview = await _authorityHost.LoadPairDraftPreviewAsync(localRoot, dropboxRoot,
                _pairDraftCancellation.Token, SelectedPairConnectionId);
            if (!IsCurrentPairDraft(generation) || version != _pairDraftPreviewVersion ||
                !string.Equals(localRoot, _draftBrowseLocalRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(dropboxRoot.FolderId, _draftBrowseDropboxRoot?.FolderId,
                    StringComparison.Ordinal)) return;
            _draftPreview = preview;
            PopulateDraftSelectionRows();
            UpdatePairSaveAvailability();
            ShowStatus("Folder choices are ready. Tick either column; both sides change together and the shared parent is derived before Save.");
        }
        catch (OperationCanceledException) when (_pairDraftCancellation?.IsCancellationRequested != false) { }
        catch (Exception exception)
        {
            if (!IsCurrentPairDraft(generation) || version != _pairDraftPreviewVersion) return;
            _draftPreview = null;
            FolderSelectionRows.Clear();
            LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
            DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
            FolderSelectionSummary.Text = "The draft folder structure could not be read: " + exception.Message;
        }
        finally
        {
            if (IsCurrentPairDraft(generation) && version == _pairDraftPreviewVersion)
            {
                LocalFolderSelectionTree.IsEnabled = true;
                DropboxFolderSelectionTree.IsEnabled = true;
            }
        }
    }

    private void PopulateDraftSelectionRows()
    {
        if (!PairLocationDraftActive || _draftPreview is null) return;
        var selection = SyncSelectionPolicy.Compile(_draftSelectionRules, _draftSelectionDefaultIncluded);
        _folderSelectionSourceRows.Clear();
        foreach (var folder in _draftPreview.Folders)
        {
            var states = _draftPreview.Folders.Where(candidate =>
                    string.Equals(candidate.RelativePath, folder.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    candidate.RelativePath.StartsWith(folder.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => selection.IsIncluded(candidate.RelativePath)).ToArray();
            bool? checkState = states.All(state => state) ? true : states.All(state => !state) ? false : null;
            var prefix = new string(' ', Math.Max(0, folder.Depth - 1) * 4);
            _folderSelectionSourceRows.Add(CreateFolderSelectionRow(string.Empty, folder, prefix + folder.Name,
                checkState));
        }
        ApplyFolderSelectionFilterAndBuild();
        var plan = CreateDraftBindingPlan(_draftPreview, _draftSelectionRules, _draftSelectionDefaultIncluded);
        LocalRootBox.Text = plan.LocalRoot;
        DropboxRootBox.Text = plan.DropboxRoot.Path;
        DropboxFolderIdText.Text = plan.DropboxRoot.FolderId;
        _selectedDropboxFolder = plan.DropboxRoot;
        var includedBranches = SyncSelectionPolicy.Normalize(_draftSelectionRules).Count(rule => rule.Included);
        var rootFiles = $"Parent files: {FormatKnownCount(_draftPreview.LocalRootFiles)} on this computer, " +
                        $"{_draftPreview.DropboxRootFiles:N0} in the cloud.";
        FolderSelectionSummary.Text = _draftSelectionDefaultIncluded
            ? $"Everything is selected, including parent files and future content. {rootFiles}"
            : includedBranches == 0
                ? $"Nothing is selected. {rootFiles} Parent files and future sibling folders stay excluded."
                : $"{includedBranches:N0} selected branch(es). Pair roots: {plan.LocalRoot} ⇄ {plan.DropboxRoot.Path}. " +
                  $"Unselected parent files and future sibling folders stay excluded. {rootFiles}";
        var allFolderStates = _draftPreview.Folders.Select(folder => selection.IsIncluded(folder.RelativePath))
            .ToArray();
        WholeRootSelectionBox.IsChecked = _draftSelectionDefaultIncluded && allFolderStates.All(state => state)
            ? true
            : !_draftSelectionDefaultIncluded && allFolderStates.All(state => !state)
                ? false
                : null;
        ApplyHierarchyRootPresentation(_draftPreview.Folders.Count, localAvailable: true,
            _draftPreview.LocalRootFiles < 0 ? null : _draftPreview.LocalRootFiles,
            dropboxAvailable: true, _draftPreview.DropboxRootFiles,
            "This computer", LocalFolderDisplayName(LocalRootBox.Text),
            ProviderPresentation.Name,
            DropboxFolderDisplayName(DropboxRootBox.Text, ProviderPresentation.Name));
        ShowFolderSelectionTrees();
    }

    private void ApplyFolderSelectionFilterAndBuild()
    {
        if (FolderSelectionSearchBox is null || FolderSelectionFilterBox is null) return;
        var search = FolderSelectionSearchBox.Text.Trim();
        var filter = (FolderSelectionFilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All folders";
        var directlyVisible = _folderSelectionSourceRows.Where(row =>
                (search.Length == 0 || row.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
                FolderRowMatchesFilter(row, filter))
            .Select(row => row.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (search.Length > 0 || !string.Equals(filter, "All folders", StringComparison.Ordinal))
        {
            foreach (var path in directlyVisible.ToArray())
            {
                var parent = path;
                while ((parent = ParentRelativePath(parent)).Length > 0) directlyVisible.Add(parent);
            }
        }
        FolderSelectionRows.Clear();
        foreach (var row in _folderSelectionSourceRows.Where(row =>
                     search.Length == 0 && string.Equals(filter, "All folders", StringComparison.Ordinal) ||
                     directlyVisible.Contains(row.RelativePath)))
            FolderSelectionRows.Add(row);
        RebuildFolderSelectionTrees();
    }

    private static bool FolderRowMatchesFilter(FolderSelectionRow row, string filter) => filter switch
    {
        "Only on this computer" => row.Presence == "Only on this computer",
        "Only in cloud" => row.Presence == "Only in cloud",
        "Online-only" => row.Presence == "Online-only",
        "Mixed" => row.Presence == "Both sides",
        "Excluded" => row.IsChecked == false,
        "Needs attention" => row.ErrorItems > 0,
        _ => true
    };

    private static string ParentRelativePath(string path)
    {
        var normalized = PilotPath.NormalizeRelative(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private void RebuildFolderSelectionTrees()
    {
        var nodes = new Dictionary<string, FolderSelectionNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in FolderSelectionRows.OrderBy(item => item.RelativePath.Count(character => character == '/'))
                     .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            nodes[row.RelativePath] = new FolderSelectionNode(row);
        FolderSelectionRoots.Clear();
        foreach (var node in nodes.Values.OrderBy(item => item.RelativePath.Count(character => character == '/'))
                     .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = ParentRelativePath(node.RelativePath);
            if (parentPath.Length > 0 && nodes.TryGetValue(parentPath, out var parent)) parent.Children.Add(node);
            else FolderSelectionRoots.Add(node);
        }
    }

    private void ShowFolderSelectionTrees()
    {
        LocalRootTree.Visibility = Visibility.Visible;
        DropboxRootTree.Visibility = Visibility.Visible;
        DropboxRootBrowserActions.Visibility = Visibility.Visible;
        LocalFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxFolderSelectionTree.Visibility = Visibility.Collapsed;
        ChangePairRootsButton.Visibility = Visibility.Collapsed;
    }

    private static bool DraftFolderMatchesFilter(SyncFolderPresence presence, bool? selected, string filter) =>
        filter switch
        {
            "Only on this computer" => presence == SyncFolderPresence.LocalOnly,
            "Only in cloud" => presence == SyncFolderPresence.CloudOnly,
            "Online-only" => presence == SyncFolderPresence.OnlineOnly,
            "Mixed" => presence == SyncFolderPresence.Mixed,
            "Excluded" => selected == false,
            "Needs attention" => false,
            _ => true
        };

    private static string FormatKnownCount(int count) => count < 0 ? "unknown" : count.ToString("N0");

    internal void PreparePairDraftForTest(SyncPairDraftPreview preview,
        IReadOnlyList<SyncSelectionRule>? rules = null, bool defaultIncluded = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        EndPairDraft();
        _editingNewPair = true;
        PairList.SelectedItem = null;
        StartPairDraft();
        _draftBrowseLocalRoot = preview.LocalBrowseRoot;
        _draftBrowseDropboxRoot = preview.DropboxBrowseRoot;
        _draftPreview = preview;
        _draftSelectionRules = SyncSelectionPolicy.Normalize(rules);
        _draftSelectionDefaultIncluded = defaultIncluded;
        PopulateDraftSelectionRows();
    }

    internal void SetDraftFolderSelectionForTest(string relativePath, bool included)
    {
        var row = FolderSelectionRows.Single(item =>
            string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        ApplyDraftFolderSelection(row, included, showStatus: false);
    }

    internal void PrepareExistingPairEditorForTest(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        EndPairDraft();
        _pairSet = new SyncPairSet([pair], pair.Id);
        RefreshPairRows(pair.Id);
        var row = PairRows.Single(item => item.Id == pair.Id);
        PairList.SelectedItem = row;
        ExpandPairRow(row);
    }

    internal void LoadPairSetForTest(SyncPairSet pairSet)
    {
        _pairSet = pairSet;
        RefreshPairRows(pairSet.SelectedPairId);
    }

    internal static PairDraftBindingPlan CreateDraftBindingPlan(SyncPairDraftPreview preview,
        IReadOnlyList<SyncSelectionRule>? rules, bool defaultIncluded)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var normalized = SyncSelectionPolicy.Normalize(rules);
        if (defaultIncluded)
            return new PairDraftBindingPlan(preview.LocalBrowseRoot, preview.DropboxBrowseRoot, true, normalized);
        var included = normalized.Where(rule => rule.Included).Select(rule => rule.FolderPath).ToArray();
        if (included.Length == 0)
            return new PairDraftBindingPlan(preview.LocalBrowseRoot, preview.DropboxBrowseRoot, false, []);
        var common = CommonRelativeParent(included);
        if (common.Length == 0)
            return new PairDraftBindingPlan(preview.LocalBrowseRoot, preview.DropboxBrowseRoot, false, normalized);
        var commonNode = preview.Folders.SingleOrDefault(folder =>
            string.Equals(folder.RelativePath, common, StringComparison.OrdinalIgnoreCase));
        if (commonNode?.LocalStableFolderIdentity is null || commonNode.DropboxStableFolderIdentity is null)
            return new PairDraftBindingPlan(preview.LocalBrowseRoot, preview.DropboxBrowseRoot, false, normalized);
        var commonIncluded = SyncSelectionPolicy.Compile(normalized, false).IsIncluded(common);
        var shifted = normalized.Where(rule =>
                rule.FolderPath.StartsWith(common + "/", StringComparison.OrdinalIgnoreCase))
            .Select(rule => new SyncSelectionRule(rule.FolderPath[(common.Length + 1)..], rule.Included,
                rule.StableFolderIdentity)).ToArray();
        var localRoot = Path.Combine(preview.LocalBrowseRoot,
            common.Replace('/', Path.DirectorySeparatorChar));
        var browseRoot = PilotPath.NormalizeDropboxRoot(preview.DropboxBrowseRoot.Path);
        var dropboxPath = browseRoot == "/" ? "/" + common : browseRoot + "/" + common;
        var dropboxRoot = new DropboxFolderEntry(commonNode.Name, PilotPath.NormalizeDropboxRoot(dropboxPath),
            commonNode.DropboxStableFolderIdentity);
        return new PairDraftBindingPlan(localRoot, dropboxRoot, commonIncluded,
            SyncSelectionPolicy.Normalize(shifted));
    }

    private static string CommonRelativeParent(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return string.Empty;
        var components = paths.Select(path => PilotPath.NormalizeRelative(path).Split('/')).ToArray();
        var length = components.Min(parts => parts.Length);
        var shared = 0;
        while (shared < length && components.Skip(1).All(parts =>
                   string.Equals(parts[shared], components[0][shared], StringComparison.OrdinalIgnoreCase)))
            shared++;
        return shared == 0 ? string.Empty : string.Join('/', components[0].Take(shared));
    }

    private void RefreshPairRows(string? selectedId)
    {
        _suppressPairSelection = true;
        var globalLiveSyncEnabled = _authorityHost.LoadAutomation().Enabled;
        var retainedById = PairRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        foreach (var pair in _pairSet.Pairs)
        {
            var needsAttention = _attentionPairIds.Contains(pair.Id) ||
                                 IsPairKnownUnhealthy(pair.Id) ||
                                 _monitorAttentionReasonByPair.ContainsKey(pair.Id) ||
                                 _runAttentionReasonByPair.ContainsKey(pair.Id);
            var monitor = _monitorStateByPair.GetValueOrDefault(pair.Id, pair.Enabled ? "Not running" : "Off");
            if (retainedById.Remove(pair.Id, out var row))
                row.Update(pair, LabelForPair(pair), monitor, needsAttention, globalLiveSyncEnabled,
                    PairAttentionReason(pair, needsAttention));
            else
                PairRows.Add(new PairRow(pair.Id, pair.Name, pair.LocalRoot, pair.DropboxRoot, LabelForPair(pair),
                    pair.Enabled, pair.Priority, pair.PropagateDeletions, pair.FilesOnDemandEnabled, monitor,
                    needsAttention, globalLiveSyncEnabled, PairAttentionReason(pair, needsAttention), pair.Direction,
                    pair.Provider, pair.EndpointDocument));
        }
        foreach (var removed in retainedById.Values) PairRows.Remove(removed);
        for (var target = 0; target < _pairSet.Pairs.Count; target++)
        {
            var current = PairRows.IndexOf(PairRows.Single(row => row.Id == _pairSet.Pairs[target].Id));
            if (current != target) PairRows.Move(current, target);
        }
        var selected = PairRows.FirstOrDefault(item => string.Equals(item.Id, selectedId ?? _pairSet.SelectedPairId, StringComparison.Ordinal));
        PairList.SelectedItem = _editingNewPair ? null : selected;
        // Refresh rebuilds row-detail containers. Row properties already notify;
        // defer filter rebuilding while their shared editor owns an active draft,
        // including the post-save monitor refresh, so it cannot detach the editor.
        if (!PairLocationDraftActive) CollectionViewSource.GetDefaultView(PairRows)?.Refresh();
        if (RewindPairBox is not null && selected is not null && RewindPairBox.SelectedItem is null)
            RewindPairBox.SelectedItem = selected;
        RefreshActivityPairChoices();
        var sameOpenDraft = !_editingNewPair && selected is not null && PairLocationDraftActive &&
                            string.Equals(_editingPairRootId, selected.Id, StringComparison.Ordinal);
        if (!_editingNewPair && selected is not null && !sameOpenDraft) ApplyPairSelection(selected.Id);
        _suppressPairSelection = false;
    }

    private string PairAttentionReason(SyncPair pair, bool needsAttention)
    {
        if (_localRootProblems.TryGetValue(pair.Id, out var local)) return local;
        if (_endpointAttentionReasonByPair.TryGetValue(pair.Id, out var endpoint)) return endpoint;
        if (_monitorAttentionReasonByPair.TryGetValue(pair.Id, out var monitor)) return monitor;
        if (_runAttentionReasonByPair.TryGetValue(pair.Id, out var run)) return run;
        if (pair.InitialReviewApproved == false)
            return "First sync review is required before Sync Shark changes files.";
        return needsAttention
            ? "A pending sync operation or conflict needs review. Open Sync status for details."
            : pair.Enabled ? "This pair is ready for live sync." : "This pair is paused.";
    }

    private static string HumanizePairFailure(SyncPair pair, string failure)
    {
        if (!Directory.Exists(pair.LocalRoot) ||
            failure.Contains("selected local folder could not be opened", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("directory not found", StringComparison.OrdinalIgnoreCase))
            return $"Local folder unavailable: {pair.LocalRoot}. Choose another folder to repair this pair.";
        if (failure.Contains("identity", StringComparison.OrdinalIgnoreCase) &&
            failure.Contains("local", StringComparison.OrdinalIgnoreCase))
            return $"The folder at {pair.LocalRoot} is not the same folder this pair was linked to. Choose the correct folder to repair it.";
        if (failure.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("authoriz", StringComparison.OrdinalIgnoreCase))
            // A retained failure describes the check that failed, not the account's current state.
            // Reauthorization does not prove this pair's saved folder identity or access either.
            return $"The last check could not access {SyncProviders.DisplayName(pair.Provider)} for this pair. " +
                   "Refresh account details in Connections. Reconnect only if account verification fails; " +
                   "if it succeeds, refresh this pair's Sync status to check its folders.";
        if (failure.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("same folder", StringComparison.OrdinalIgnoreCase))
            return $"The {SyncProviders.DisplayName(pair.Provider)} folder {pair.DropboxRoot} is no longer the same folder this pair was linked to. Choose the correct cloud folder to repair it.";
        if (failure.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("HTTP", StringComparison.OrdinalIgnoreCase))
            return $"{SyncProviders.DisplayName(pair.Provider)} is temporarily unavailable for this pair. Check the connection and try again.";
        return failure.Trim();
    }

    private async void PairList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PairList)) return;
        if (_suppressPairSelection) return;
        if (PairList.SelectedItem is not PairRow selected) return;
        if (_changingPairStorage && _pairStorageOverview is { } changing && selected.Id != changing.Pair.Id)
        {
            _suppressPairSelection = true;
            PairList.SelectedItem = PairRows.FirstOrDefault(row => row.Id == changing.Pair.Id);
            _suppressPairSelection = false;
            ShowStatus("Wait for the current storage change before switching pairs."); return;
        }
        if (PairLocationDraftActive && !string.Equals(_editingPairRootId, selected.Id, StringComparison.Ordinal))
        {
            var priorId = _editingPairRootId;
            var wasNew = _editingNewPair;
            // Save always binds the editor's original pair, never the newly clicked row.
            _suppressPairSelection = true;
            PairList.SelectedItem = wasNew ? null : PairRows.SingleOrDefault(item => item.Id == priorId);
            _suppressPairSelection = false;
            if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
            selected = PairRows.SingleOrDefault(item => item.Id == selected.Id) ?? selected;
            _suppressPairSelection = true;
            PairList.SelectedItem = selected;
            _suppressPairSelection = false;
        }
        ExpandPairRow(selected);
        if (string.Equals(_pairSet.SelectedPairId, selected.Id, StringComparison.Ordinal))
        {
            ApplyPairSelection(selected.Id);
            return;
        }
        _ = SelectPairAsync(selected.Id);
    }

    private async Task SelectPairAsync(string pairId)
    {
        try
        {
            var selectedSet = await _authorityHost.SelectPairAsync(pairId);
            if (_editingNewPair || PairList.SelectedItem is not PairRow selected ||
                !string.Equals(selected.Id, pairId, StringComparison.Ordinal)) return;
            _pairSet = selectedSet;
            ApplyPairSelection(pairId);
            ShowStatus("Selected sync pair: " + (_pairSet.Pairs.Single(item => item.Id == pairId).Name));
        }
        catch (Exception exception) { ShowStatus("Pair selection was not saved: " + exception.Message); }
    }

    private void ApplyPairSelection(string pairId)
    {
        if (_editingNewPair) return;
        var pair = _pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        if (pair is null) return;
        if (PairLocationDraftActive) EndPairDraft();
        _editingNewPair = false;
        _editingPairRootId = pair.Id;
        RefreshAccountPickers();
        PairIdentityDisclosure.IsExpanded = false;
        var (generation, token) = StartPairDraft();
        PairEditorTabs.SelectedIndex = 0;
        ClearPairStorageTab();
        FolderSelectionSearchBox.Clear();
        FolderSelectionFilterBox.SelectedIndex = 0;
        NewPairEditorHost.Visibility = Visibility.Collapsed;
        EditorSyncNowButton.Visibility = Visibility.Visible;
        SavedDropboxActions.Visibility = Visibility.Visible;
        OpenLocalFolderButton.Visibility = Visibility.Visible;
        PairNameBox.Text = pair.Name;
        PairEditorHeading.Text = "Edit " + pair.Name;
        LocalRootTree.Visibility = Visibility.Visible;
        DropboxRootTree.Visibility = Visibility.Visible;
        LocalFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxRootBrowserActions.Visibility = Visibility.Visible;
        ChangePairRootsButton.Visibility = Visibility.Collapsed;
        PairPriorityBox.Text = pair.Priority.ToString();
        PairEnabledBox.IsChecked = pair.Enabled;
        PairDeletionBox.IsChecked = pair.PropagateDeletions;
        PairFilesOnDemandBox.IsChecked = pair.FilesOnDemandEnabled;
        PairFilesOnDemandBox.IsEnabled = !pair.FilesOnDemandEnabled;
        DisableFilesOnDemandButton.IsEnabled = pair.FilesOnDemandEnabled;
        AvailabilityPathBox.Clear();
        LocalRootBox.Text = pair.LocalRoot;
        DropboxRootBox.Text = pair.DropboxRoot;
        DropboxFolderIdText.Text = pair.DropboxFolderId;
        LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
        DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
        _selectedDropboxFolder = new DropboxFolderEntry(DropboxFolderDisplayName(pair.DropboxRoot), pair.DropboxRoot, pair.DropboxFolderId);
        RecoveryRootBox.Text = pair.RecoveryRoot?.RootPath ?? string.Empty;
        RecoveryFolderIdText.Text = pair.RecoveryRoot?.RootFolderId ?? "Not selected";
        _selectedRecoveryFolder = pair.RecoveryRoot is null
            ? null
            : new DropboxFolderEntry(DropboxFolderDisplayName(pair.RecoveryRoot.RootPath), pair.RecoveryRoot.RootPath,
                pair.RecoveryRoot.RootFolderId);
        PairStorageBox.SelectedIndex = SyncDirectoryStoragePolicy.Default(pair) == SyncDirectoryStorage.CloudOnly ? 1 : 0;
        SyncTypeBox.SelectedIndex = (int)pair.Direction;
        RecoveryRetentionBox.SelectedIndex = pair.RecoveryRetentionDays switch
        {
            30 => 0,
            180 => 2,
            null => 3,
            _ => 1
        };
        LoadPairSyncOrder(pair.Priority);
        PairConnectionSummary.Text = PairAccountLabel;
        _draftSelectionRules = SyncSelectionPolicy.Normalize(pair.SelectionRules);
        LoadIgnoreRules(pair.IgnoreRules);
        _draftSelectionDefaultIncluded = pair.SelectionDefaultIncluded;
        _localGlobalSelection.Load(pair.LocalRoot, pair.SelectionDefaultIncluded, pair.SelectionRules);
        _dropboxGlobalSelection.Load(pair.DropboxRoot, pair.SelectionDefaultIncluded, pair.SelectionRules);
        _draftBrowseLocalRoot = pair.LocalRoot;
        _draftBrowseDropboxRoot = _selectedDropboxFolder;
        _replaceUnavailableLocalOnNextCheck = !Directory.Exists(pair.LocalRoot);
        _replaceUnavailableDropboxOnNextCheck = false;
        _recoveryPreparationFailed = false;
        FolderSelectionSummary.Text = _replaceUnavailableLocalOnNextCheck
            ? "The saved folder on this computer is unavailable. Choose its replacement; the cloud selection remains unchanged until Save."
            : "Loading the complete folder structures and opening the saved selection…";
        if (_replaceUnavailableLocalOnNextCheck)
        {
            LocalHierarchyNoticeText.Text = "Saved folder no longer exists. Choose a replacement.";
            LocalHierarchyNotice.ToolTip = pair.LocalRoot;
            LocalHierarchyNotice.Visibility = Visibility.Visible;
            OpenLocalFolderButton.Content = "Choose replacement…";
        }
        else
        {
            LocalHierarchyNotice.Visibility = Visibility.Collapsed;
            LocalHierarchyNotice.ToolTip = null;
            OpenLocalFolderButton.Content = CommandIcons.Label("Open in File Explorer", "folder");
        }
        DropboxHierarchyNotice.Visibility = Visibility.Collapsed;
        RecoveryUsageText.Text = "Loading Recovery use…";
        _ = RefreshPairRecoveryUsageAsync(pair);
        PairEnabled_Changed(PairEnabledBox, new RoutedEventArgs());
        WholeRootSelectionBox.IsChecked = _draftSelectionRules.Count == 0
            ? _draftSelectionDefaultIncluded
            : null;
        RecomputeGlobalDraftPlan();
        EditorSyncNowButton.IsEnabled = !UiOperationBusy && !IsPairKnownUnhealthy(pair.Id);
        BeginPairEndpointStrip();
        _existingPairEditorInitialization = InitializeExistingPairEditorAsync(pair, generation, token);
    }

    private async Task InitializeExistingPairEditorAsync(SyncPair pair, int generation,
        CancellationToken cancellationToken)
    {
        await InitializeNewPairRootBrowserAsync(generation, cancellationToken, pair.LocalRoot);
        if (!IsCurrentPairDraft(generation)) return;
        await ExpandDropboxTreeToSavedPathAsync(pair.DropboxRoot, generation, cancellationToken);
        if (!IsCurrentPairDraft(generation)) return;
        RecomputeGlobalDraftPlan();
        ResetPairEditorViewport();
        ShowPairEditorStatus(_replaceUnavailableLocalOnNextCheck
            ? $"{pair.Name} needs attention because its saved local folder is unavailable. The complete browser is open; tick a replacement and Save pair."
            : _replaceUnavailableDropboxOnNextCheck
                ? $"{pair.Name} needs attention because its saved cloud folder is unavailable. The complete browser is open; tick a replacement and Save pair."
                : $"{pair.Name} is open in the complete folder browser. Its saved folders are ticked and expanded for editing.");
    }

    private async Task ExpandLocalTreeToSavedPathAsync(string savedPath, CancellationToken cancellationToken)
    {
        var generation = _pairEditorGeneration;
        var target = Path.GetFullPath(savedPath);
        var current = LocalRootNodes.FirstOrDefault(root => LocalPathWithin(target, root.Path));
        if (current is null) return;
        current.IsExpanded = true;
        await current.LoadChildrenAsync(cancellationToken);
        if (!IsCurrentPairDraft(generation) || cancellationToken.IsCancellationRequested) return;
        var relative = Path.GetRelativePath(current.Path, target);
        if (relative != ".")
        {
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current.Children.FirstOrDefault(child => !child.IsPlaceholder &&
                    string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (next is null) break;
                current = next;
                current.IsExpanded = true;
                await current.LoadChildrenAsync(cancellationToken);
                if (!IsCurrentPairDraft(generation) || cancellationToken.IsCancellationRequested) return;
            }
        }
        RefreshLocalGlobalSelectionTree();
        var nearest = current;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsCurrentPairDraft(generation) && !cancellationToken.IsCancellationRequested)
                BringTreeNodeIntoView(LocalRootTree, nearest);
        });
    }

    private async Task ExpandDropboxTreeToSavedPathAsync(string savedPath, int generation,
        CancellationToken cancellationToken)
    {
        var current = DropboxRootNodes.FirstOrDefault();
        if (current is null) return;
        current.IsExpanded = true;
        foreach (var segment in PilotPath.NormalizeDropboxRoot(savedPath).Split('/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var loaded = await LoadDropboxRootNodeAsync(current, refresh: false, generation, cancellationToken);
            if (!IsCurrentPairDraft(generation)) return;
            if (loaded is DropboxNodeLoadOutcome.Failed or DropboxNodeLoadOutcome.Unavailable or
                DropboxNodeLoadOutcome.Cancelled or DropboxNodeLoadOutcome.Stale)
            {
                DropboxHierarchyNoticeText.Text = "Cloud folders could not be checked. Try Refresh.";
                DropboxHierarchyNotice.Visibility = Visibility.Visible;
                return;
            }
            var lookup = await FindDropboxChildAsync(current, segment, generation, cancellationToken);
            if (lookup.Node is null)
            {
                if (!lookup.Exhaustive)
                {
                    DropboxHierarchyNoticeText.Text = "Cloud folders could not be checked completely. Try Refresh.";
                    DropboxHierarchyNotice.Visibility = Visibility.Visible;
                    return;
                }
                _replaceUnavailableDropboxOnNextCheck = true;
                DropboxHierarchyNoticeText.Text =
                    "Saved folder no longer exists. Choose a replacement.";
                DropboxHierarchyNotice.ToolTip = savedPath;
                DropboxHierarchyNotice.Visibility = Visibility.Visible;
                var nearest = current;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    () => BringTreeNodeIntoView(DropboxRootTree, nearest));
                return;
            }
            current = lookup.Node;
            await LoadDropboxRootNodeAsync(current, refresh: false, generation, cancellationToken);
            if (!IsCurrentPairDraft(generation)) return;
            current.IsExpanded = true;
        }
        _selectedDropboxRootNode = current;
        _replaceUnavailableDropboxOnNextCheck = false;
        DropboxHierarchyNotice.Visibility = Visibility.Collapsed;
        DropboxHierarchyNotice.ToolTip = null;
        RefreshDropboxGlobalSelectionTree();
        var selected = current;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            () => BringTreeNodeIntoView(DropboxRootTree, selected));
    }

    private void BringTreeNodeIntoView(ItemsControl tree, object item)
    {
        tree.UpdateLayout();
        var container = FindTreeItemContainer(tree, item);
        if (container is null) return;
        container.IsSelected = true;
        container.BringIntoView();
        ResetPairEditorViewport();
    }

    private static TreeViewItem? FindTreeItemContainer(ItemsControl parent, object item)
    {
        var chain = new List<object>();
        if (!FindModelPath(parent.Items.Cast<object>(), item, chain)) return null;
        TreeViewItem? result = null;
        foreach (var step in chain)
        {
            parent.ApplyTemplate();
            if (parent.ItemContainerGenerator.ContainerFromItem(step) is not TreeViewItem)
            {
                var presenter = FindDescendant<ItemsPresenter>(parent);
                presenter?.ApplyTemplate();
                if (presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0 &&
                    VisualTreeHelper.GetChild(presenter, 0) is FolderVirtualizingPanel panel)
                    panel.RevealIndex(parent.Items.IndexOf(step));
                parent.UpdateLayout();
            }
            result = parent.ItemContainerGenerator.ContainerFromItem(step) as TreeViewItem;
            if (result is null) return null;
            if (!ReferenceEquals(step, item)) result.IsExpanded = true;
            parent = result;
        }
        return result;
    }

    private static bool FindModelPath(IEnumerable<object> nodes, object target, List<object> chain)
    {
        foreach (var node in nodes)
        {
            chain.Add(node);
            if (ReferenceEquals(node, target)) return true;
            IEnumerable<object> children = node switch
            {
                LocalFolderNode local => local.Children,
                DropboxFolderNode cloud => cloud.Children,
                _ => []
            };
            if (FindModelPath(children, target, chain)) return true;
            chain.RemoveAt(chain.Count - 1);
        }
        return false;
    }

    private async Task<DropboxChildLookup> FindDropboxChildAsync(DropboxFolderNode parent,
        string name, int generation, CancellationToken cancellationToken)
    {
        while (true)
        {
            var match = parent.Children.FirstOrDefault(child => child.Entry is not null &&
                string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return new DropboxChildLookup(match, true);
            if (parent.ContinueCursor is null) return new DropboxChildLookup(null, true);
            if (!parent.CanLoadMore || _pairDropboxBrowser is null)
                return new DropboxChildLookup(null, false);
            var outcome = await ContinueDropboxNodeAsync(parent, generation, cancellationToken);
            if (outcome != DropboxNodeLoadOutcome.Complete)
                return new DropboxChildLookup(null, false);
        }
    }

    private async Task<DropboxNodeLoadOutcome> ContinueDropboxNodeAsync(DropboxFolderNode node, int generation,
        CancellationToken cancellationToken)
    {
        if (_pairDropboxBrowser is null) return DropboxNodeLoadOutcome.Unavailable;
        try { await node.LoadGate.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return DropboxNodeLoadOutcome.Cancelled; }
        var version = Interlocked.Increment(ref _pairDropboxRootLoadVersion);
        var browser = _pairDropboxBrowser;
        node.ActiveOperationVersion = version;
        node.IsLoading = true;
        UpdateDropboxRootLoadMoreButton();
        try
        {
            if (node.ContinueCursor is null) return DropboxNodeLoadOutcome.Complete;
            if (!node.CanLoadMore) return DropboxNodeLoadOutcome.Failed;
            var page = await browser.ContinueFoldersAsync(node.ContinueCursor, cancellationToken);
            if (!IsCurrentPairDraft(generation) || !ReferenceEquals(browser, _pairDropboxBrowser) ||
                node.ActiveOperationVersion != version) return DropboxNodeLoadOutcome.Stale;
            node.AppendPage(page);
            await LoadRemainingDraftFolderPagesAsync(node, browser, generation, version, cancellationToken);
            if (!IsCurrentPairDraft(generation) || !ReferenceEquals(browser, _pairDropboxBrowser) ||
                node.ActiveOperationVersion != version) return DropboxNodeLoadOutcome.Stale;
            RefreshDropboxGlobalSelectionTree();
            ApplyGlobalFolderTreeFilter();
            DropboxRootBrowserStatus.Text = node.PaginationBlockedReason ??
                                            (node.ContinueCursor is null
                                                ? $"Loaded all {node.LoadedChildCount:N0} folders."
                                                : $"Loaded {node.LoadedChildCount:N0} folders; more remain.");
            return DropboxNodeLoadOutcome.Complete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DropboxNodeLoadOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            if (IsCurrentPairDraft(generation))
                DropboxRootBrowserStatus.Text = "More folders could not be loaded: " + exception.Message;
            return DropboxNodeLoadOutcome.Failed;
        }
        finally
        {
            if (node.ActiveOperationVersion == version) node.IsLoading = false;
            if (IsCurrentPairDraft(generation)) UpdateDropboxRootLoadMoreButton();
            node.LoadGate.Release();
        }
    }

    // The caller owns this node's load gate. Continue only the opened folder level, never its descendants.
    private async Task LoadRemainingDraftFolderPagesAsync(DropboxFolderNode node,
        IDropboxFolderBrowserService browser, int generation, int version, CancellationToken cancellationToken)
    {
        for (var pages = 0; node.CanLoadMore && pages < 40; pages++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentPairDraft(generation) || !ReferenceEquals(browser, _pairDropboxBrowser) ||
                node.ActiveOperationVersion != version) return;
            var cursor = node.ContinueCursor;
            if (cursor is null) return;
            DropboxRootBrowserStatus.Text = $"Loading {node.Path}… {node.LoadedChildCount:N0} folders found so far.";
            var page = await browser.ContinueFoldersAsync(cursor, cancellationToken);
            if (!IsCurrentPairDraft(generation) || !ReferenceEquals(browser, _pairDropboxBrowser) ||
                node.ActiveOperationVersion != version) return;
            node.AppendPage(page);
        }
    }

    private async Task RefreshPairRecoveryUsageAsync(SyncPair pair)
    {
        try
        {
            var usage = await _authorityHost.LoadRecoveryUsageAsync(pair.Id);
            if (_editingNewPair || PairList.SelectedItem is not PairRow selected ||
                !string.Equals(selected.Id, pair.Id, StringComparison.Ordinal)) return;
            if (usage.ItemCount == 0)
            {
                RecoveryUsageText.Text = "No recovery copies for this pair.";
                return;
            }
            var oldest = usage.OldestRetainedUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "not loaded";
            RecoveryUsageText.Text = $"Saved Copies: {usage.ItemCount:N0}\nSize: {FormatByteSize(usage.Size)}\n" +
                $"Oldest Copy: {oldest}\nReady for Cleanup Review: {usage.CleanupEligibleCount:N0}";
        }
        catch (Exception exception)
        {
            if (!_editingNewPair && PairList.SelectedItem is PairRow selected &&
                string.Equals(selected.Id, pair.Id, StringComparison.Ordinal))
                RecoveryUsageText.Text = "Recovery use could not be loaded: " + exception.Message;
        }
    }

    private void PairRowDetailsHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_editingNewPair || sender is not ContentControl { DataContext: PairRow row } host ||
            PairList.SelectedItem is not PairRow selected ||
            !string.Equals(row.Id, selected.Id, StringComparison.Ordinal)) return;
        MovePairEditorTo(host);
    }

    private void PairRowDetailsHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl host && ReferenceEquals(_activePairEditorHost, host) &&
            ReferenceEquals(host.Content, PairEditorPanel))
        {
            host.Content = null;
            _activePairEditorHost = null;
        }
    }

    private void MovePairEditorTo(ContentControl host)
    {
        if (ReferenceEquals(_activePairEditorHost, host) && ReferenceEquals(host.Content, PairEditorPanel)) return;
        if (_activePairEditorHost is not null && ReferenceEquals(_activePairEditorHost.Content, PairEditorPanel))
            _activePairEditorHost.Content = null;
        host.Content = PairEditorPanel;
        _activePairEditorHost = host;
        DecorateCommands(PairEditorPanel);
        UpdatePairDeletionDescription();
    }

    private async void RefreshFolderSelection_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (PairLocationDraftActive)
        {
            if (_editingNewPair)
            {
                var generation = _pairEditorGeneration;
                await InitializeNewPairRootBrowserAsync(generation,
                    _pairDraftCancellation?.Token ?? CancellationToken.None);
                RecomputeGlobalDraftPlan();
            }
            else if (PairList.SelectedItem is PairRow row)
            {
                ApplyPairSelection(row.Id);
            }
            return;
        }
        var pairId = (PairList.SelectedItem as PairRow)?.Id;
        await LoadFolderSelectionAsync(pairId, refreshIndex: true);
    }

    private async void ChangePairRoots_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive) return;
        if (_editingNewPair)
        {
            _draftBrowseLocalRoot = null;
            _draftBrowseDropboxRoot = null;
            _selectedDropboxFolder = null;
            ResetDraftSelectionForRoots();
            ShowStatus("Choose one starting folder on this computer and one in the selected cloud account. Nothing changes until Save pair.");
            return;
        }
        if (PairList.SelectedItem is not PairRow row) return;
        EndPairDraft();
        _editingPairRootId = row.Id;
        var (generation, token) = StartPairDraft();
        _draftBrowseLocalRoot = null;
        _draftBrowseDropboxRoot = null;
        _draftPreview = null;
        _draftSelectionRules = [];
        _draftSelectionDefaultIncluded = false;
        _folderSelectionSourceRows.Clear();
        FolderSelectionRows.Clear();
        FolderSelectionRoots.Clear();
        LocalRootBox.Clear();
        DropboxRootBox.Clear();
        LocalHierarchyNotice.Visibility = Visibility.Collapsed;
        DropboxHierarchyNotice.Visibility = Visibility.Collapsed;
        LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
        DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
        OpenLocalFolderButton.Content = CommandIcons.Label("Open in File Explorer", "folder");
        DropboxFolderIdText.Text = "Not selected";
        _selectedDropboxFolder = null;
        WholeRootSelectionBox.IsChecked = false;
        LocalRootTree.Visibility = Visibility.Visible;
        DropboxRootTree.Visibility = Visibility.Visible;
        LocalFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxFolderSelectionTree.Visibility = Visibility.Collapsed;
        DropboxRootBrowserActions.Visibility = Visibility.Visible;
        ChangePairRootsButton.Visibility = Visibility.Collapsed;
        FolderSelectionSummary.Text = "Choose one starting folder on each side. Tickable two-way folders will then appear in these panels.";
        await InitializeNewPairRootBrowserAsync(generation, token);
        UpdatePairSaveAvailability();
        ShowStatus("Choose replacement starting folders. The saved pair remains unchanged until Save pair completes.");
    }

    private void FolderSelectionFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (FolderSelectionSearchBox is null || FolderSelectionFilterBox is null) return;
        _folderSelectionSearchTimer.Stop();
        _folderSelectionSearchTimer.Start();
    }

    private async void LoadMoreFolders_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_editingNewPair) return;
        if (_loadingFolderSelection || !_folderSelectionHasMore) return;
        await LoadFolderSelectionPageAsync((PairList.SelectedItem as PairRow)?.Id, reset: false,
            refreshIndex: false);
    }

    private void FolderSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_runActive || sender is not CheckBox { DataContext: FolderSelectionRow row } checkBox) return;
        var include = row.IsChecked != true;
        checkBox.IsChecked = include;
        var rowIndex = FolderSelectionRows.IndexOf(row);
        if (rowIndex >= 0) FolderSelectionRows[rowIndex] = row with { IsChecked = include };
        if (_editingNewPair)
        {
            ApplyDraftFolderSelection(row, include, showStatus: true);
            return;
        }
        _draftSelectionRules = SyncSelectionPolicy.SetFolder(_draftSelectionRules, row.RelativePath, include,
            row.StableFolderIdentity);
        ApplyPendingExistingSelectionRows();
        UpdatePairSaveAvailability();
        ShowStatus(include
            ? $"{row.RelativePath} and its whole subtree are selected in this draft. Choose Save pair to apply the change."
            : $"{row.RelativePath} and its whole subtree are excluded in this draft. Choose Save pair to apply the change.");
    }

    private void FolderSelectionTree_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FolderSelectionNode node } checkBox) return;
        if (_runActive)
        {
            checkBox.IsChecked = node.IsChecked;
            ShowStatus("Folder choices are locked until the active sync finishes. You can still browse and expand both trees.");
            e.Handled = true;
            return;
        }
        var include = checkBox.IsChecked == true;
        var row = node.Row;
        if (PairLocationDraftActive)
        {
            ApplyDraftFolderSelection(row, include, showStatus: true);
            return;
        }
        _draftSelectionRules = SyncSelectionPolicy.SetFolder(_draftSelectionRules, row.RelativePath, include,
            row.StableFolderIdentity);
        ApplyPendingExistingSelectionRows();
        UpdatePairSaveAvailability();
        ShowStatus(include
            ? $"{row.RelativePath} and everything inside it will sync after you choose Save pair."
            : $"{row.RelativePath} and everything inside it will be excluded after you choose Save pair.");
    }

    private void FolderSelectionTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderSelectionNode node) return;
        AvailabilityPathBox.Text = node.RelativePath;
    }

    private int? SelectedRecoveryRetentionDays()
    {
        var tag = (RecoveryRetentionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return string.Equals(tag, "Never", StringComparison.Ordinal) ? null :
            int.TryParse(tag, out var days) ? days : 90;
    }

    private void ApplyDraftFolderSelection(FolderSelectionRow row, bool include, bool showStatus)
    {
        _draftSelectionRules = SyncSelectionPolicy.SetFolder(_draftSelectionRules, row.RelativePath, include,
            row.StableFolderIdentity);
        PopulateDraftSelectionRows();
        if (!showStatus) return;
        ShowStatus(include
            ? $"Included {row.RelativePath} and its whole subtree in this draft; any child choices beneath it were replaced."
            : $"Excluded {row.RelativePath} and its whole subtree in this draft; any child choices beneath it were replaced.");
    }

    private bool? PendingExistingFolderState(string relativePath)
    {
        var normalizedPath = PilotPath.NormalizeRelative(relativePath);
        var selected = SyncSelectionPolicy.Compile(_draftSelectionRules, _draftSelectionDefaultIncluded)
            .IsIncluded(normalizedPath);
        var prefix = normalizedPath + "/";
        return _draftSelectionRules.Any(rule =>
            rule.FolderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            rule.Included != selected)
            ? null
            : selected;
    }

    private void ApplyPendingExistingSelectionRows()
    {
        for (var index = 0; index < _folderSelectionSourceRows.Count; index++)
        {
            var row = _folderSelectionSourceRows[index];
            _folderSelectionSourceRows[index] = row with { IsChecked = PendingExistingFolderState(row.RelativePath) };
        }
        ApplyFolderSelectionFilterAndBuild();
        WholeRootSelectionBox.IsChecked = _draftSelectionRules.Count == 0
            ? _draftSelectionDefaultIncluded
            : null;
    }

    internal void SetExistingFolderSelectionForTest(string relativePath, bool included,
        string? stableFolderIdentity = null)
    {
        if (!PairLocationDraftActive || _editingNewPair)
            throw new InvalidOperationException("An existing pair editor is not active.");
        _draftSelectionRules = SyncSelectionPolicy.SetFolder(_draftSelectionRules, relativePath, included,
            stableFolderIdentity);
        ApplyPendingExistingSelectionRows();
    }

    internal IReadOnlyList<SyncSelectionRule> PendingSelectionRulesForTest => _draftSelectionRules;
    internal bool PendingSelectionDefaultIncludedForTest => _draftSelectionDefaultIncluded;
    internal PairDraftBindingPlan? GlobalDraftPlanForTest => _globalDraftPlan;
    internal FolderTreeSelectionPlan? LocalGlobalPlanForTest => _localGlobalSelection.CreatePlan();
    internal FolderTreeSelectionPlan? DropboxGlobalPlanForTest => _dropboxGlobalSelection.CreatePlan();
    internal Task RefreshPairSyncStatusForTestAsync() => RefreshPairSyncStatusAsync(forceHealthCheck: true);
    internal Task DispatchPairsForTestAsync(IEnumerable<SyncPair> pairs, string trigger) =>
        RunPairsAsync(pairs, trigger);
    internal bool PairKnownUnhealthyForTest(string pairId) => IsPairKnownUnhealthy(pairId);
    internal string PairAttentionSourcesForTest(string pairId) =>
        $"endpoint={_endpointAttentionReasonByPair.GetValueOrDefault(pairId) ?? "<none>"}; " +
        $"monitor={_monitorAttentionReasonByPair.GetValueOrDefault(pairId) ?? "<none>"}; " +
        $"run={_runAttentionReasonByPair.GetValueOrDefault(pairId) ?? "<none>"}; " +
        $"projection={_attentionPairIds.Contains(pairId)}";
    internal void SetPairAttentionSourcesForTest(string pairId, string? endpoint, string? monitor,
        string? run, bool projection)
    {
        SetAttentionSource(_endpointAttentionReasonByPair, pairId, endpoint);
        SetAttentionSource(_monitorAttentionReasonByPair, pairId, monitor);
        SetAttentionSource(_runAttentionReasonByPair, pairId, run);
        if (projection) _attentionPairIds.Add(pairId);
        else _attentionPairIds.Remove(pairId);
        UpdatePairAttentionPresentation(pairId);
    }

    private static void SetAttentionSource(IDictionary<string, string> source, string pairId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) source.Remove(pairId);
        else source[pairId] = reason;
    }
    internal bool ReplaceUnavailableDropboxOnNextCheckForTest => _replaceUnavailableDropboxOnNextCheck;
    internal bool RunActiveForTest => _runActive;
    internal bool UpdatingGlobalTreeSelectionForTest => _updatingGlobalTreeSelection;
    internal void ApplyFolderTreeFilterForTest(string search, int filterIndex)
    {
        FolderSelectionSearchBox.Text = search;
        FolderSelectionFilterBox.SelectedIndex = filterIndex;
        ApplyFolderSelectionFilterAndBuild();
        ApplyGlobalFolderTreeFilter();
    }

    private void WholeRootSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_runActive) return;
        var include = WholeRootSelectionBox.IsChecked == true;
        if (PairLocationDraftActive)
        {
            _draftSelectionDefaultIncluded = include;
            _draftSelectionRules = [];
            PopulateDraftSelectionRows();
            ShowStatus(include
                ? "The complete parent folders are selected, including parent files and future content."
                : "The complete parent folders are excluded. Tick individual branches to build a selective two-way draft.");
            return;
        }
        if (PairList.SelectedItem is not PairRow) return;
        _draftSelectionDefaultIncluded = include;
        _draftSelectionRules = [];
        ApplyPendingExistingSelectionRows();
        UpdatePairSaveAvailability();
        ShowStatus(include
            ? "The complete pair is selected in this draft, including parent files and future content. Choose Save pair to apply the change."
            : "The complete pair is excluded in this draft. Tick branches to add them back, then choose Save pair.");
    }

    private void HierarchyRootSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_runActive || sender is not CheckBox checkBox) return;
        WholeRootSelectionBox.IsChecked = checkBox.IsChecked;
        WholeRootSelection_Click(WholeRootSelectionBox, e);
    }

    private async void KeepFolderOnDevice_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await SetSelectedFolderAvailabilityAsync(keepOnDevice: true);
    }

    private async void FreeFolderSpace_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await SetSelectedFolderAvailabilityAsync(keepOnDevice: false);
    }

    private void ChooseAvailabilityFile_Click(object sender, RoutedEventArgs e)
    {
        if (PairList.SelectedItem is not PairRow row) return;
        var pair = _pairSet.Pairs.Single(item => item.Id == row.Id);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a Sync Shark file",
            InitialDirectory = pair.LocalRoot,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var selected = Path.GetFullPath(dialog.FileName);
        var relative = PilotPath.NormalizeRelative(Path.GetRelativePath(pair.LocalRoot, selected));
        var rebound = PilotPath.ResolveContainedPath(pair.LocalRoot, relative);
        if (!string.Equals(selected, rebound, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus("Choose a file inside the selected pair root.");
            return;
        }
        AvailabilityPathBox.Text = relative;
        ShowStatus($"Selected file {relative}. Keep on this device and Free up space affect this file only.");
    }

    private void UseSelectedAvailabilityFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PairList.SelectedItem is not PairRow pairRow) return;
        var pair = _pairSet.Pairs.Single(item => item.Id == pairRow.Id);
        var relative = LocalRootTree.SelectedItem is LocalFolderNode local &&
                       LocalPathWithin(local.Path, pair.LocalRoot)
            ? PilotPath.NormalizeRelative(Path.GetRelativePath(pair.LocalRoot, local.Path))
            : DropboxRootTree.SelectedItem is DropboxFolderNode { Entry: { } entry } &&
              DropboxPathWithin(entry.Path, pair.DropboxRoot)
                ? RelativeDropboxPath(pair.DropboxRoot, entry.Path)
                : string.Empty;
        if (relative.Length == 0)
        {
            ShowStatus("Select a folder inside this pair first.");
            return;
        }
        AvailabilityPathBox.Text = relative;
        ShowStatus($"Selected folder {relative}. Storage actions affect it and its descendants.");
    }

    private async void DisableFilesOnDemand_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || PairList.SelectedItem is not PairRow row) return;
        if (!AppDialogWindow.Confirm(this, "Change storage",
                "Make every file in this pair available offline?",
                "Sync Shark downloads online-only content before turning off online-only storage. Nothing is deleted from this computer or Dropbox.",
                "Make available offline"))
            return;
        try
        {
            ShowStatus("Making every online-only file available offline before turning off Online-only storage…");
            var formerlyManaged = _pairSet.Pairs.Single(pair => pair.Id == row.Id);
            await DisablePairFilesOnDemandAsync(formerlyManaged, CancellationToken.None);
            ShowStatus("Online-only storage is off. Every managed file is available offline and this pair remains synchronized.");
        }
        catch (Exception exception)
        {
            ShowStatus("The storage change did not complete all required steps. Check this pair's current storage status before retrying: " +
                       exception.Message);
        }
    }

    private async Task DisablePairFilesOnDemandAsync(SyncPair formerlyManaged, CancellationToken cancellationToken)
    {
        if (_runActive) throw new InvalidOperationException("Finish the active sync before changing managed storage.");
        var selectedId = (PairList.SelectedItem as PairRow)?.Id;
        PairEditorPanel.IsEnabled = false;
        PairRootGrid.IsEnabled = false;
        try
        {
            _pairSet = await _authorityHost.DisableFilesOnDemandAsync(formerlyManaged.Id, cancellationToken,
                expectedConnectionId: formerlyManaged.ConnectionId, expectedPolicyRevision: formerlyManaged.PolicyRevision);
            ExplorerIntegration.UnregisterRoot(formerlyManaged);
            RefreshPairRows(selectedId);
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
            await RefreshExplorerIntegrationAsync(propagateErrors: true);
            if (selectedId == formerlyManaged.Id)
                await LoadFolderSelectionAsync(formerlyManaged.Id, refreshIndex: true);
        }
        finally { SetRunControls(); }
    }

    internal Task<bool> SavePairForTestAsync() => SavePairAsync();
    internal bool HasSelectedRecoveryForTest => _selectedRecoveryFolder is not null;

    internal static bool FilesOnDemandValueForBinding(bool? currentValue, bool requestedValue) =>
        currentValue == true && !requestedValue || requestedValue;

    private async Task SetSelectedFolderAvailabilityAsync(bool keepOnDevice)
    {
        if (_runActive || PairList.SelectedItem is not PairRow pairRow) return;
        var selectedPath = AvailabilityPathBox.Text.Trim();
        if (selectedPath.Length == 0)
        {
            var pair = _pairSet.Pairs.Single(item => item.Id == pairRow.Id);
            selectedPath = LocalRootTree.SelectedItem is LocalFolderNode local &&
                           LocalPathWithin(local.Path, pair.LocalRoot)
                ? PilotPath.NormalizeRelative(Path.GetRelativePath(pair.LocalRoot, local.Path))
                : DropboxRootTree.SelectedItem is DropboxFolderNode { Entry: { } entry } &&
                  DropboxPathWithin(entry.Path, pair.DropboxRoot)
                    ? RelativeDropboxPath(pair.DropboxRoot, entry.Path)
                    : string.Empty;
        }
        if (selectedPath.Length == 0)
        {
            ShowStatus("Choose a file or select a folder row first.");
            return;
        }
        PairRootGrid.IsEnabled = false;
        try
        {
            var pair = _pairSet.Pairs.Single(item => item.Id == pairRow.Id);
            if (!pair.FilesOnDemandEnabled)
                throw new InvalidOperationException(
                    "Set this pair's storage to Online-only before changing individual file or folder availability.");
            var result = await _authorityHost.SetPathAvailabilityAsync(pair.Id, selectedPath,
                keepOnDevice, onProgress: entry => Dispatcher.InvokeAsync(() => ShowStatus(
                    entry.Comparison is { } detail ? SyncProgressDescription.Describe(detail) : entry.State)).Task);
            ApplyPairSelection(pair.Id);
            ShowStatus((keepOnDevice
                ? $"Kept {result.FilesChanged} file(s) from {selectedPath} on this device."
                : $"Freed space for {result.FilesChanged} unchanged file(s) from {selectedPath}.") +
                  (result.Failures.Count == 0
                      ? string.Empty
                      : $" {result.Failures.Count} item(s) remain pending: " +
                        string.Join(Environment.NewLine, result.Failures.Select(SyncActivityText.Safe))));
        }
        catch (Exception exception)
        {
            ShowStatus("Local storage availability was not changed: " + exception.Message);
        }
        finally { SetRunControls(); }
    }

    private async Task LoadFolderSelectionAsync(string? pairId, bool refreshIndex = false)
    {
        await LoadFolderSelectionPageAsync(pairId, reset: true, refreshIndex);
    }

    private async Task LoadFolderSelectionPageAsync(string? pairId, bool reset, bool refreshIndex)
    {
        if (_loadingFolderSelection && !reset) return;
        _folderSelectionLoadCancellation?.Cancel();
        _folderSelectionLoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _folderSelectionLoadCancellation = cancellation;
        var version = Interlocked.Increment(ref _folderSelectionLoadVersion);
        _loadingFolderSelection = true;
        LocalFolderSelectionTree.IsEnabled = false;
        DropboxFolderSelectionTree.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(pairId))
            {
                _folderSelectionSourceRows.Clear();
                FolderSelectionRows.Clear();
                FolderSelectionRoots.Clear();
                LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
                DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
                FolderSelectionSummary.Text = "Save and select a pair to load its folder hierarchy.";
                _folderSelectionOffset = 0;
                _folderSelectionHasMore = false;
                return;
            }
            FolderSelectionSummary.Text = refreshIndex
                ? "Refreshing both folder trees…"
                : "Loading both folder trees…";
            // The chosen loader is captured PER REQUEST, so a later assignment cannot swap the input
            // mid-call. Null (the production default) runs the exact real Host path and scheduling.
            var treeLoader = FolderSelectionTreeLoaderForTest;
            var tree = await Task.Run(() => treeLoader is null
                ? _authorityHost.LoadFolderSelectionTreeAsync(pairId, cancellation.Token)
                : treeLoader(pairId, cancellation.Token), cancellation.Token);
            if (version != _folderSelectionLoadVersion) return;
            ApplyFolderHierarchyHealth(tree);
            _folderSelectionSourceRows.Clear();
            foreach (var folder in tree.Folders)
            {
                var prefix = new string(' ', Math.Max(0, folder.Depth - 1) * 4);
                _folderSelectionSourceRows.Add(CreateFolderSelectionRow(tree.PairId, folder,
                    prefix + folder.Name, PendingExistingFolderState(folder.RelativePath)));
            }
            _folderSelectionOffset = tree.Folders.Count;
            _folderSelectionHasMore = false;
            var pairRollup = tree.Rollup ?? new SyncFolderRollup(0, 0, 0, 0, 0, 0);
            var defaultIncluded = _draftSelectionDefaultIncluded;
            WholeRootSelectionBox.IsChecked = _draftSelectionRules.Count == 0
                ? _draftSelectionDefaultIncluded
                : null;
            _folderSelectionBaseSummary = BuildFolderSelectionSummary(tree, defaultIncluded, pairRollup);
            ApplyFolderSelectionFilterAndBuild();
            FolderSelectionSummary.Text = _folderSelectionBaseSummary;
            ShowFolderSelectionTrees();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version != _folderSelectionLoadVersion) return;
            if (reset)
            {
                _folderSelectionSourceRows.Clear();
                FolderSelectionRows.Clear();
                FolderSelectionRoots.Clear();
            }
            // R3: the notice names the endpoints this pair ACTUALLY has, taken from its captured ordered
            // context through the shared titles. Without a resolvable context it stays neutral left/right
            // rather than assuming a local-plus-cloud layout. The generation guards above stay authoritative.
            var failedContext = _pairSet.Pairs.SingleOrDefault(item =>
                string.Equals(item.Id, pairId, StringComparison.Ordinal)) is { } failedPair
                    ? TryConflictEndpointPresentation(failedPair)?.Context
                    : null;
            var portTitles = failedContext is null
                ? (Local: SyncEndpointPresentation.SideLabel(SyncPairSide.Left),
                    Remote: SyncEndpointPresentation.SideLabel(SyncPairSide.Right))
                : HierarchyPortTitles(new SyncFolderSelectionTree(string.Empty, [], 0, 0, 0, 0,
                    EndpointContext: failedContext));
            FolderSelectionSummary.Text = "Folder hierarchy unavailable: " + exception.Message;
            LocalHierarchyEmptyState.Visibility = Visibility.Collapsed;
            DropboxHierarchyEmptyState.Visibility = Visibility.Collapsed;
            LocalHierarchyNotice.Visibility = Visibility.Visible;
            DropboxHierarchyNotice.Visibility = Visibility.Visible;
            LocalHierarchyNoticeText.Text = portTitles.Local + " could not be checked.";
            DropboxHierarchyNoticeText.Text = portTitles.Remote + " could not be checked.";
        }
        finally
        {
            if (version == _folderSelectionLoadVersion)
            {
                _loadingFolderSelection = false;
                LocalFolderSelectionTree.IsEnabled = true;
                DropboxFolderSelectionTree.IsEnabled = true;
            }
        }
    }

    private void ApplyFolderHierarchyHealth(SyncFolderSelectionTree tree)
    {
        _folderHealthByPair[tree.PairId] = tree;
        var ports = HierarchyPorts(tree);
        var localAvailable = ports[0].Facts?.Available ?? tree.LocalHierarchyAvailable;
        var remoteAvailable = ports[1].Facts?.Available ?? tree.DropboxHierarchyAvailable;
        var localMessage = ports[0].Facts?.Message ?? tree.LocalHierarchyMessage;
        var remoteMessage = ports[1].Facts?.Message ?? tree.DropboxHierarchyMessage;
        var localDefinitive = ports[0].Facts?.Definitive ?? true;
        var remoteDefinitive = ports[1].Facts?.Definitive ?? true;
        ApplyHierarchyRootPresentation(tree.Folders.Count, localAvailable,
            ports[0].Facts?.RootFiles ?? tree.LocalRootFiles, remoteAvailable,
            ports[1].Facts?.RootFiles ?? tree.DropboxRootFiles, ports[0].Title, ports[0].Location,
            ports[1].Title, ports[1].Location);
        LocalHierarchyNotice.Visibility = localAvailable ? Visibility.Collapsed : Visibility.Visible;
        LocalHierarchyNoticeText.Text = localAvailable
            ? string.Empty
            : localDefinitive
                ? "Saved " + ports[0].Title + " folder is unavailable. Choose a replacement."
                : "The " + ports[0].Title + " folder could not be checked just now. Refresh to try again.";
        LocalHierarchyNotice.ToolTip = localAvailable ? null : localMessage;
        DropboxHierarchyNotice.Visibility = remoteAvailable ? Visibility.Collapsed : Visibility.Visible;
        DropboxHierarchyNoticeText.Text = remoteAvailable
            ? string.Empty
            : remoteDefinitive
                ? "Saved " + ports[1].Title + " folder is unavailable. Choose a replacement."
                : "The " + ports[1].Title + " folder could not be checked just now. Refresh to try again.";
        DropboxHierarchyNotice.ToolTip = remoteAvailable ? null : remoteMessage;
        OpenLocalFolderButton.Content = localAvailable
            ? "Open in File Explorer"
            : "Choose replacement...";

        var failures = new List<string>();
        if (!localAvailable && localDefinitive && !string.IsNullOrWhiteSpace(localMessage))
            failures.Add(localMessage!);
        if (!remoteAvailable && remoteDefinitive && !string.IsNullOrWhiteSpace(remoteMessage))
            failures.Add(remoteMessage!);
        if (failures.Count == 0)
        {
            _endpointAttentionReasonByPair.Remove(tree.PairId);
            if (localAvailable && remoteAvailable &&
                _runAttentionReasonByPair.TryGetValue(tree.PairId, out var runReason) &&
                runReason.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase))
                _runAttentionReasonByPair.Remove(tree.PairId);
        }
        else
            _endpointAttentionReasonByPair[tree.PairId] = string.Join(" ", failures);
        var pair = _pairSet.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, tree.PairId, StringComparison.Ordinal));
        var row = PairRows.SingleOrDefault(item =>
            string.Equals(item.Id, tree.PairId, StringComparison.Ordinal));
        if (pair is not null && row is not null)
        {
            var needsAttention = _attentionPairIds.Contains(pair.Id) ||
                                 IsPairKnownUnhealthy(pair.Id) ||
                                 _monitorAttentionReasonByPair.ContainsKey(pair.Id) ||
                                 _runAttentionReasonByPair.ContainsKey(pair.Id);
            row.UpdateHealth(needsAttention, PairAttentionReason(pair, needsAttention));
            EditorSyncNowButton.IsEnabled = !UiOperationBusy && !IsPairKnownUnhealthy(pair.Id) &&
                                            !CurrentPairEditorHasUnsavedChanges();
        }
        if (PairEditorTabs.SelectedIndex == 3) _ = RefreshPairSyncStatusAsync();
    }

    /// <summary>
    /// W2-C: the two hierarchy data sides named by the PHYSICAL compatibility port each one occupies. The
    /// retained Local/Dropbox tree fields belong to the endpoint carrying that port - which is NOT always
    /// the first ordered endpoint - so a surface renders each port's OWN endpoint name and location rather
    /// than assuming a local-left layout. Without a captured context the existing physical wording is kept.
    /// </summary>
    private (SyncEndpointHierarchySide? Facts, string Title, string Location)[] HierarchyPorts(
        SyncFolderSelectionTree tree)
    {
        var context = tree.EndpointContext;
        if (context is null)
            return
            [
                (null, "This computer", LocalFolderDisplayName(LocalRootBox.Text)),
                (null, ProviderPresentation.Name,
                    DropboxFolderDisplayName(DropboxRootBox.Text, ProviderPresentation.Name))
            ];
        var leftIsLocalPort = string.Equals(context.Left.CompatibilityPort,
            SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal);
        var localSide = leftIsLocalPort ? context.Left : context.Right;
        var remoteSide = leftIsLocalPort ? context.Right : context.Left;
        return
        [
            (leftIsLocalPort ? tree.LeftEndpoint : tree.RightEndpoint,
                SyncEndpointPresentation.HierarchyTitle(localSide),
                SyncEndpointPresentation.HierarchyLocation(localSide)),
            (leftIsLocalPort ? tree.RightEndpoint : tree.LeftEndpoint,
                SyncEndpointPresentation.HierarchyTitle(remoteSide),
                SyncEndpointPresentation.HierarchyLocation(remoteSide))
        ];
    }

    /// <summary>The same port mapping as titles only, for the static summary builder.</summary>
    private static (string Local, string Remote) HierarchyPortTitles(SyncFolderSelectionTree tree)
    {
        if (tree.EndpointContext is not { } context) return ("This computer", "Cloud");
        var leftIsLocalPort = string.Equals(context.Left.CompatibilityPort,
            SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal);
        return (SyncEndpointPresentation.HierarchyTitle(leftIsLocalPort ? context.Left : context.Right),
            SyncEndpointPresentation.HierarchyTitle(leftIsLocalPort ? context.Right : context.Left));
    }

    private void ApplyHierarchyRootPresentation(int folderCount, bool localAvailable, int? localRootFiles,
        bool dropboxAvailable, int? dropboxRootFiles, string localTitle, string localLocation,
        string remoteTitle, string remoteLocation)
    {
        LocalHierarchyEmptyState.Visibility = localAvailable && folderCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        DropboxHierarchyEmptyState.Visibility = dropboxAvailable && folderCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalHierarchyRootNameText.Text = localTitle + " - " + localLocation;
        DropboxHierarchyRootNameText.Text = remoteTitle + " - " + remoteLocation;
        LocalHierarchyEmptyStateText.Text = EmptyHierarchyDescription(localRootFiles);
        DropboxHierarchyEmptyStateText.Text = EmptyHierarchyDescription(dropboxRootFiles);
    }

    private static string BuildFolderSelectionSummary(SyncFolderSelectionTree tree, bool defaultIncluded,
        SyncFolderRollup pairRollup)
    {
        var titles = HierarchyPortTitles(tree);
        if (tree.Folders.Count == 0)
        {
            var parts = new List<string>();
            if (tree.LocalHierarchyAvailable)
                parts.Add(titles.Local + ": " + EmptyHierarchyDescription(tree.LocalRootFiles) + ".");
            if (tree.DropboxHierarchyAvailable)
                parts.Add(titles.Remote + ": " + EmptyHierarchyDescription(tree.DropboxRootFiles) + ".");
            parts.AddRange(new[] { tree.LocalHierarchyMessage, tree.DropboxHierarchyMessage }
                .Where(message => !string.IsNullOrWhiteSpace(message)).Cast<string>());
            parts.Add(defaultIncluded
                ? "The complete pair remains selected."
                : "The complete pair is not selected.");
            return string.Join(" ", parts);
        }
        if (!tree.LocalHierarchyAvailable || !tree.DropboxHierarchyAvailable)
            return $"{tree.Folders.Count:N0} folder(s) loaded from the available side. " +
                   string.Join(" ", new[] { tree.LocalHierarchyMessage, tree.DropboxHierarchyMessage }
                       .Where(message => !string.IsNullOrWhiteSpace(message)));
        return $"Folders Loaded: {tree.Folders.Count:N0}    On Both Sides: {tree.MixedFolders:N0}    " +
               $"Only on {titles.Local}: {tree.LocalOnlyFolders:N0}    Only on {titles.Remote}: {tree.CloudOnlyFolders:N0}\n" +
               $"Online-Only Folders: {tree.OnlineOnlyFolders:N0}    Local Items: {pairRollup.LocalItems:N0}    " +
               $"Online-Only Items: {pairRollup.OnlineOnlyItems:N0}    Mixed Items: {pairRollup.MixedItems:N0}\n" +
               $"Excluded Items: {pairRollup.ExcludedItems:N0}    Pending Items: {pairRollup.PendingItems:N0}    " +
               $"Errors: {pairRollup.ErrorItems:N0}";
    }
    private static string EmptyHierarchyDescription(int? rootFiles) => rootFiles switch
    {
        null => "File count unavailable; no subfolders",
        0 => "Empty folder; no subfolders",
        1 => "1 file at this level; no subfolders",
        _ => $"{rootFiles.Value:N0} files at this level; no subfolders"
    };

    internal void ApplyFolderHierarchyPresentationForTest(SyncFolderSelectionTree tree)
    {
        ApplyFolderHierarchyHealth(tree);
        var rollup = tree.Rollup ?? new SyncFolderRollup(0, 0, 0, 0, 0, 0);
        _folderSelectionBaseSummary = BuildFolderSelectionSummary(tree, _draftSelectionDefaultIncluded, rollup);
        FolderSelectionSummary.Text = _folderSelectionBaseSummary;
        ShowFolderSelectionTrees();
    }

    private async Task<bool> RefreshBootstrapReviewAsync(bool reset = true)
    {
        if (_loadingBootstrap && !reset) return false;
        var version = reset ? ++_bootstrapQueryVersion : _bootstrapQueryVersion;
        var requestedOffset = reset ? 0 : _bootstrapOffset;
        var pairId = BootstrapPairBox.SelectedValue as string;
        if (pairId is not null && !ScopeMatches(pairId)) pairId = null;
        if (reset)
        {
            _firstSyncReviewContext = null;
            _bootstrapInventoryComplete = false;
            BootstrapRows.Clear();
            ApproveBootstrapButton.IsEnabled = false;
            BootstrapSummary.Text = "Loading this pair's first-sync plan…";
        }
        if (string.IsNullOrWhiteSpace(pairId))
        {
            _loadingBootstrap = false;
            BootstrapRows.Clear();
            BootstrapSummary.Text = "Select a pair to inspect its first-sync inventory.";
            ApproveBootstrapButton.IsEnabled = false;
            LoadMoreBootstrapButton.IsEnabled = false;
            UpdateFirstSyncActionAvailability();
            return false;
        }
        _loadingBootstrap = true;
        LoadMoreBootstrapButton.IsEnabled = false;
        try
        {
            var pair = _pairSet.Pairs.Single(item => item.Id == pairId);
            // EPS4A: this review is defined by both ordered endpoints. A pair whose endpoints cannot
            // be described is refused explicitly; a selected or primary account never stands in.
            if (!TryCaptureEndpointReview(pair, out var endpoints)) return false;
            var group = SelectedGroupKey(BootstrapGroupFilter) ?? "All groups";
            var itemKind = (BootstrapItemFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All items";
            var fileType = string.IsNullOrWhiteSpace(BootstrapTypeFilter.Text)
                ? "All types"
                : BootstrapTypeFilter.Text.Trim();
            var page = BootstrapReviewPageLoaderForTest is null
                ? await _authorityHost.LoadBootstrapReviewPageAsync(pairId, ProjectionPageSize,
                    requestedOffset, BootstrapSearchBox.Text.Trim(), group, itemKind, fileType)
                : await BootstrapReviewPageLoaderForTest(pairId, ProjectionPageSize, requestedOffset,
                    BootstrapSearchBox.Text.Trim(), group, itemKind, fileType);
            if (version != _bootstrapQueryVersion || !ScopeMatches(pairId) ||
                !string.Equals(BootstrapPairBox.SelectedValue as string, pairId, StringComparison.Ordinal)) return false;
            if (!SameCapturedEndpoints(pairId, pair.ConnectionId,
                    FirstSyncReviewContext.Identity(endpoints.Context.Left),
                    FirstSyncReviewContext.Identity(endpoints.Context.Right)))
            {
                // An either-side rebind invalidates this pair's plan and draft choices only.
                DiscardFirstSyncPlan(pairId,
                    "This pair's endpoints changed while its plan was loading. The plan and draft choices were discarded; scan again to review the current endpoints.");
                return false;
            }
            if (page.Items.Any(item => !string.Equals(item.PairId, pairId, StringComparison.Ordinal)))
                throw new InvalidDataException("The returned inventory does not belong to this pair.");
            if (!reset && (!string.Equals(_bootstrapLocalScanGenerationId, page.LocalScanGenerationId,
                               StringComparison.Ordinal) ||
                           !string.Equals(_bootstrapDropboxScanGenerationId, page.DropboxScanGenerationId,
                               StringComparison.Ordinal)))
            {
                _firstSyncReviewContext = null;
                _bootstrapInventoryComplete = false;
                _bootstrapHasMore = false;
                BootstrapSummary.Text = "The inventory changed while more rows were loading. Scan again to review one exact plan.";
                ApproveBootstrapButton.IsEnabled = false;
                return false;
            }
            if (reset)
            {
                _bootstrapOffset = 0;
                _bootstrapHasMore = false;
                _bootstrapLocalScanGenerationId = null;
                _bootstrapDropboxScanGenerationId = null;
                BootstrapRows.Clear();
            }
            _bootstrapLocalScanGenerationId = page.LocalScanGenerationId;
            _bootstrapDropboxScanGenerationId = page.DropboxScanGenerationId;
            var generations = (page.LocalScanGenerationId, page.DropboxScanGenerationId);
            if (_firstSyncChoiceGenerations.TryGetValue(pairId, out var previous) && previous != generations)
                foreach (var key in _bootstrapActionOverrides.Keys.Where(key => key.PairId == pairId).ToArray())
                    _bootstrapActionOverrides.Remove(key);
            _firstSyncChoiceGenerations[pairId] = generations;
            foreach (var item in page.Items)
            {
                // Display-only keys also include provider names that cannot be Windows paths.
                // Only actionable rows reach the separately validated effect choices below.
                var key = new BootstrapActionKey(item.PairId, item.RelativePath);
                var selectedAction = _bootstrapActionOverrides.TryGetValue(key, out var saved) &&
                                     string.Equals(saved.Group, item.Group, StringComparison.Ordinal) &&
                                     BootstrapActionAllowed(item.Group, saved.Action, item.Move)
                    ? saved.Action
                    : item.Recommendation;
                var row = new BootstrapRow(item.PairId, item.RelativePath, item.Group,
                    item.Recommendation, item.Effect, FormatConflictSize(item.LocalSize),
                    FormatConflictSize(item.DropboxSize), FormatConflictTime(item.LocalModifiedUtc),
                    FormatConflictTime(item.DropboxModifiedUtc), pair.Name, item.ItemKind, item.FileType,
                    selectedAction, pair.Provider, endpoints, item.Move);
                row.ActionChanged += BootstrapRow_ActionChanged;
                BootstrapRows.Add(row);
            }
            _bootstrapOffset = requestedOffset + page.Items.Count;
            _bootstrapHasMore = page.HasMore;
            _bootstrapTotalItems = page.TotalItems;
            _bootstrapLocalOnlyItems = page.LocalOnlyItems + page.LocalUpdatedItems;
            _bootstrapDropboxOnlyItems = page.DropboxOnlyItems + page.DropboxUpdatedItems;
            _bootstrapConflictItems = page.ConflictItems;
            _bootstrapInventoryComplete = page.InventoryComplete;
            BootstrapSummary.Text = !page.InventoryComplete
                ? $"{pair.Name}: scan both folders to create the first-sync plan."
                : page.TotalItems == 0
                    ? $"{pair.Name}: both selected folders are empty. Start first sync to watch for future changes."
                : $"Folder Pair: {pair.Name}\nLeft: {endpoints.LeftTitle}    Right: {endpoints.RightTitle}\n" +
                  $"Inventoried: {page.TotalItems:N0}    Only on {endpoints.LocalPortTitle}: {page.LocalOnlyItems:N0}    " +
                  $"Only on {endpoints.RemotePortTitle}: {page.DropboxOnlyItems:N0}    " +
                  $"Changed since sync: {page.LocalUpdatedItems + page.DropboxUpdatedItems:N0}    " +
                  $"Unresolved Conflicts: {page.ConflictItems:N0}\n" +
                  $"Showing {BootstrapRows.Count:N0} of {page.TotalMatchingItems:N0} matching items. Conflicts stay unchanged until resolved.";
            _firstSyncReviewContext = page.InventoryComplete && page.LocalScanGenerationId is { } localGeneration &&
                page.DropboxScanGenerationId is { } cloudGeneration
                ? new FirstSyncReviewContext(pairId, endpoints, pair.ConnectionId, localGeneration,
                    cloudGeneration, version) : null;
            ApproveBootstrapButton.IsEnabled = FirstSyncStartAvailable(pairId);
            // WF2: the primary action says what pressing it does - it confirms the choices and syncs. The
            // authoritative plan is captured independently of the rows currently displayed, so confirmation never
            // requires every displayed page to be loaded first.
            var approvalLabel = pair.InitialReviewApproved == false ? "Confirm choices and sync" : "First Sync Complete";
            ApproveBootstrapButton.Content = CommandIcons.Label(approvalLabel, pair.InitialReviewApproved == false ? "play" : "history");
            System.Windows.Automation.AutomationProperties.SetName(ApproveBootstrapButton, approvalLabel);
            LoadMoreBootstrapButton.Visibility = page.TotalMatchingItems > ProjectionPageSize
                ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreBootstrapButton.IsEnabled = page.HasMore;
            ApplyFirstSyncEndpointLabels(endpoints);
            UpdateBootstrapChoiceSummary(pairId);
            PresentRetainedFirstSyncProblem();
            return true;
        }
        catch (Exception exception)
        {
            if (version != _bootstrapQueryVersion) return false;
            _firstSyncReviewContext = null;
            _bootstrapInventoryComplete = false;
            _bootstrapHasMore = false;
            ApproveBootstrapButton.IsEnabled = false;
            BootstrapSummary.Text = "The first-sync plan could not be loaded. Scan again to retry. " + exception.Message;
            return false;
        }
        finally
        {
            if (version == _bootstrapQueryVersion)
            {
                _loadingBootstrap = false;
                UpdateFirstSyncActionAvailability();
            }
        }
    }

    private void BootstrapFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (BootstrapGroupFilter is null || BootstrapSearchBox is null) return;
        _bootstrapSearchTimer.Stop();
        InvalidateFirstSyncReview("Updating the first-sync view…");
        _bootstrapSearchTimer.Start();
    }

    private void BootstrapTypeFilter_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        BootstrapFilter_Changed(sender, e);

    private static bool BootstrapActionAllowed(string group, string action, SyncBootstrapMoveReview? move = null) =>
        SyncBootstrapActions.For(group, move).Contains(action, StringComparer.Ordinal);

    // WF2: one visible draft action that returns every displayed row to its existing recommendation. It changes
    // only local draft choices, so nothing is copied, replaced or deleted until the reviewed run is confirmed.
    private void UseRecommendedBootstrapChoices_Click(object sender, RoutedEventArgs e)
    {
        if (UiOperationBusy) return;
        var applied = 0;
        foreach (var row in BootstrapRows)
            if (BootstrapActionAllowed(row.Group, row.Recommendation, row.Move))
            {
                row.SelectedAction = row.Recommendation;
                applied++;
            }
        var pairId = BootstrapPairBox.SelectedValue as string;
        if (pairId is not null) UpdateBootstrapChoiceSummary(pairId);
        SetFirstSyncFeedback($"Every displayed first-sync item now uses its recommended action ({applied:N0} item(s)); " +
            "no files have changed until you confirm choices and sync.");
    }

    private void BootstrapRow_ActionChanged(object? sender, EventArgs e)
    {
        if (sender is not BootstrapRow row || !row.CanChooseAction ||
            _firstSyncReviewContext is not { } context || !IsCurrentFirstSyncContext(context) || row.PairId != context.PairId) return;
        var key = new BootstrapActionKey(row.PairId, PilotPath.NormalizeRelative(row.Path));
        if (string.Equals(row.SelectedAction, row.Recommendation, StringComparison.Ordinal))
            _bootstrapActionOverrides.Remove(key);
        else
            _bootstrapActionOverrides[key] = new BootstrapChoiceState(row.Group, row.SelectedAction);
        UpdateBootstrapChoiceSummary(row.PairId);
    }

    private void UpdateBootstrapChoiceSummary(string pairId)
    {
        if (BootstrapChoiceSummary is null) return;
        var context = _firstSyncReviewContext is { } current &&
                      string.Equals(current.PairId, pairId, StringComparison.Ordinal) ? current : null;
        var endpoints = context?.Endpoints;
        var remoteTitle = endpoints?.RemotePortTitle ?? "the other endpoint";
        var localTitle = endpoints?.LocalPortTitle ?? "this computer";
        var skipped = _bootstrapActionOverrides.Count(item =>
            string.Equals(item.Key.PairId, pairId, StringComparison.Ordinal) &&
            string.Equals(item.Value.Action, "Skip for now", StringComparison.Ordinal));
        var skipUploads = _bootstrapActionOverrides.Count(item => item.Key.PairId == pairId &&
            item.Value.Action == "Skip for now" && SyncBootstrapActions.TransferAction(item.Value.Group) == "Upload");
        var skipDownloads = _bootstrapActionOverrides.Count(item => item.Key.PairId == pairId &&
            item.Value.Action == "Skip for now" && SyncBootstrapActions.TransferAction(item.Value.Group) == "Download");
        BootstrapChoiceSummary.Text = $"Selected: {_bootstrapLocalOnlyItems - skipUploads:N0} action(s) on {remoteTitle}, " +
            $"{_bootstrapDropboxOnlyItems - skipDownloads:N0} on {localTitle}, {skipped:N0} skipped. " +
            "Each row shows whether it copies, updates or moves a file. These are draft choices; no files have changed. " +
            "Confirm choices and sync shows the complete plan before applying it.";
    }

    private async Task ReconcileBootstrapActionOverridesAsync(string pairId)
    {
        var inventory = BootstrapReviewLoaderForTest is null
            ? await _authorityHost.LoadBootstrapReviewAsync(pairId)
            : await BootstrapReviewLoaderForTest(pairId, null, null, null, null);
        var current = inventory.Where(item => SyncBootstrapActions.IsActionable(item.Group))
            .ToDictionary(item => new BootstrapActionKey(pairId, PilotPath.NormalizeRelative(item.RelativePath)),
                item => item);
        foreach (var saved in _bootstrapActionOverrides.Where(item =>
                     string.Equals(item.Key.PairId, pairId, StringComparison.Ordinal)).ToArray())
            if (!current.TryGetValue(saved.Key, out var item) ||
                !string.Equals(item.Group, saved.Value.Group, StringComparison.Ordinal) ||
                !BootstrapActionAllowed(item.Group, saved.Value.Action, item.Move))
                _bootstrapActionOverrides.Remove(saved.Key);
        UpdateBootstrapChoiceSummary(pairId);
    }

    private void SetSelectedBootstrapAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string requested) return;
        ApplySelectedBootstrapAction(requested);
    }

    private void ApplySelectedBootstrapAction_Click(object sender, RoutedEventArgs e)
    {
        if (BootstrapSelectedActionBox.SelectedItem is not ComboBoxItem { Tag: string requested }) return;
        ApplySelectedBootstrapAction(requested);
    }

    private void ApplySelectedBootstrapAction(string requested)
    {
        var rows = BootstrapGrid.SelectedItems.OfType<BootstrapRow>().Where(item => item.CanChooseAction).ToArray();
        if (rows.Length == 0)
        {
            ShowStatus("Select one or more copy or move rows first.");
            return;
        }
        var changed = 0;
        foreach (var row in rows)
        {
            var action = string.Equals(requested, "Recommended", StringComparison.Ordinal)
                ? row.Recommendation
                : requested;
            if (!BootstrapActionAllowed(row.Group, action, row.Move)) continue;
            row.SelectedAction = action;
            changed++;
        }
        ShowStatus(changed == 0
            ? $"None of the selected items can use {requested}."
            : $"Updated {changed:N0} selected first-sync action(s).");
    }

    private async void SetAllMatchingBootstrapAction_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || _loadingBootstrap || BootstrapPairBox.SelectedValue is not string pairId ||
            sender is not System.Windows.Controls.Button button || button.Tag is not string requested) return;
        await ApplyAllMatchingBootstrapActionAsync(pairId, requested);
    }

    private async void ApplyAllMatchingBootstrapAction_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || _loadingBootstrap || BootstrapPairBox.SelectedValue is not string pairId ||
            BootstrapMatchingActionBox.SelectedItem is not ComboBoxItem { Tag: string requested }) return;
        await ApplyAllMatchingBootstrapActionAsync(pairId, requested);
    }

    private async Task ApplyAllMatchingBootstrapActionAsync(string pairId, string requested)
    {
        var context = _firstSyncReviewContext;
        if (context is null || !IsCurrentFirstSyncContext(context) || context.PairId != pairId) return;
        try
        {
            _loadingBootstrap = true;
            var group = SelectedGroupKey(BootstrapGroupFilter) ?? "All groups";
            var itemKind = (BootstrapItemFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All items";
            var fileType = string.IsNullOrWhiteSpace(BootstrapTypeFilter.Text)
                ? "All types"
                : BootstrapTypeFilter.Text.Trim();
            var matching = BootstrapReviewLoaderForTest is null
                ? await _authorityHost.LoadBootstrapReviewAsync(pairId, BootstrapSearchBox.Text.Trim(),
                    group, itemKind, fileType)
                : await BootstrapReviewLoaderForTest(pairId, BootstrapSearchBox.Text.Trim(), group, itemKind,
                    fileType);
            if (!ReferenceEquals(context, _firstSyncReviewContext) || context.QueryVersion != _bootstrapQueryVersion ||
                BootstrapPairBox.SelectedValue as string != pairId || !ScopeMatches(pairId)) return;
            var actionable = matching.Where(item => item.PairId == pairId &&
                SyncBootstrapActions.IsActionable(item.Group)).ToArray();
            var matchingPaths = actionable.Select(item => PilotPath.NormalizeRelative(item.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = 0;
            foreach (var item in actionable)
            {
                var key = new BootstrapActionKey(pairId, PilotPath.NormalizeRelative(item.RelativePath));
                var action = string.Equals(requested, "Recommended", StringComparison.Ordinal)
                    ? item.Recommendation
                    : requested;
                if (!BootstrapActionAllowed(item.Group, action, item.Move)) continue;
                changed++;
                if (string.Equals(action, item.Recommendation, StringComparison.Ordinal))
                    _bootstrapActionOverrides.Remove(key);
                else
                    _bootstrapActionOverrides[key] = new BootstrapChoiceState(item.Group, action);
            }
            foreach (var row in BootstrapRows.Where(item => item.CanChooseAction &&
                         matchingPaths.Contains(item.Path)))
            {
                var action = string.Equals(requested, "Recommended", StringComparison.Ordinal)
                    ? row.Recommendation
                    : requested;
                if (BootstrapActionAllowed(row.Group, action, row.Move)) row.SelectedAction = action;
            }
            UpdateBootstrapChoiceSummary(pairId);
            ShowStatus(changed == 0
                ? $"No matching items can use {requested}."
                : $"Updated {changed:N0} matching first-sync action(s), including unloaded rows.");
        }
        catch (Exception exception)
        {
            ShowStatus("Matching first-sync actions could not be updated: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(context, _firstSyncReviewContext)) _loadingBootstrap = false;
        }
    }

    private async void BootstrapPair_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _updatingAttentionPairs) return;
        InvalidateFirstSyncReview("Loading this pair's first-sync plan…");
        try
        {
            await RefreshBootstrapReviewAsync();
        }
        catch (Exception exception)
        {
            ShowStatus("The first-sync pair could not be selected: " + exception.Message);
        }
    }

    private async void LoadMoreBootstrap_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!_bootstrapHasMore || _loadingBootstrap) return;
        try { await RefreshBootstrapReviewAsync(reset: false); }
        catch (Exception exception) { ShowStatus("More first-sync items could not be loaded: " + exception.Message); }
    }

    private async void ReviewBootstrapConflicts_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        var selectedGroup = SelectedGroupKey(BootstrapGroupFilter) ??
                            "All groups";
        var conflictGroup = selectedGroup is "Local newer" or "Dropbox newer" or "Approximately equal time" or
            "Unreliable or missing time" ? selectedGroup : "All groups";
        ConflictGroupFilter.SelectedItem = ConflictGroupFilter.Items.OfType<ComboBoxItem>()
            .Single(item => string.Equals((item.Tag ?? item.Content)?.ToString(), conflictGroup, StringComparison.Ordinal));
        ConflictSearchBox.Text = BootstrapSearchBox.Text.Trim();
        NavigateTo("Conflicts");
        try
        {
            await LoadConflictPageAsync(reset: true);
            ShowStatus(_bootstrapConflictItems == 0
                ? "No first-sync conflicts are waiting. Return to First sync review to start the remaining uploads and downloads."
                : "Opened matching conflicts. Compare rows, then use Select all matching to capture every exact conflict generation, including unloaded rows.");
        }
        catch (Exception exception)
        {
            ShowStatus("Matching conflicts could not be loaded: " + exception.Message);
        }
    }

    private void OpenSelectiveFromBootstrap_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("Pairs");
        var row = PairRows.SingleOrDefault(item =>
            string.Equals(item.Id, BootstrapPairBox.SelectedValue as string, StringComparison.Ordinal));
        if (row is null) return;
        PairList.SelectedItem = row;
        ExpandPairRow(row);
        PairEditorTabs.SelectedIndex = 0;
    }

    private async void RefreshBootstrap_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (UiOperationBusy || BootstrapPairBox.SelectedValue is not string pairId || !ScopeMatches(pairId)) return;
        // SS-D01: this scan owns one cancellation token, created before the run is marked active and
        // before its work is queued, so Stop cancels the scan itself instead of passing None.
        var scanCancellation = BeginPendingRunCancellation();
        _runActive = true;
        ResetRunPresentation("Scanning folders", "Checking both selected folders without changing files…");
        SetRunControls();
        RunProgress.IsIndeterminate = true;
        try
        {
            BootstrapSummary.Text = "Scanning both selected folders again without changing files…";
            // ENG3: the same host progress stream as the batch runs, so the First Sync surface shows this scan's
            // real phase, current path and observed count while it runs.
            await Task.Run(() => _authorityHost.RefreshBootstrapInventoryAsync(pairId, scanCancellation.Token,
                progress => PresentBatchProgressAsync(progress, "First sync", recoveryOnly: false)));
            if (BootstrapPairBox.SelectedValue as string != pairId) return;
            if (!await RefreshBootstrapReviewAsync())
            {
                SetFirstSyncFeedback("The scan finished, but its review could not be loaded. Try Scan Again; no sync actions were started.");
                return;
            }
            await ReconcileBootstrapActionOverridesAsync(pairId);
            SetFirstSyncFeedback("First-sync plan updated from a fresh, effect-free scan.");
        }
        catch (Exception exception)
        {
            if (BootstrapPairBox.SelectedValue as string == pairId)
            {
                InvalidateFirstSyncReview("First-sync scan could not be completed. No files were changed. Try Scan Again.");
                SetFirstSyncFeedback("First-sync review could not be refreshed: " + exception.Message);
            }
        }
        finally
        {
            EndPendingRunCancellation(scanCancellation);
            await FinishRunPresentationAsync("First-sync scan",
                () => RefreshAuthorityProjectionsAsync(requireSuccess: true), SetFirstSyncFeedback,
                BootstrapChoiceSummary.Text);
        }
    }

    private async void ApproveBootstrap_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_firstSyncApprovalInFlight || _runActive || _firstSyncReviewContext is not { } context ||
            !IsCurrentFirstSyncContext(context)) return;
        // UI1: the single-flight guard is taken synchronously, before this handler's first await, and the
        // validating feedback is shown immediately, so the click is acknowledged before any awaited load starts
        // and a second click can never enqueue a second run. The guard spans the whole load, confirmation and
        // run interval, and the shared control pass keeps Start disabled throughout.
        _firstSyncApprovalInFlight = true;
        SetFirstSyncFeedback("Checking the reviewed inventory for this folder pair before applying your choices…");
        SetRunControls();
        try
        {
            await ApproveBootstrapCoreAsync(context);
        }
        finally
        {
            // The run's own completion path clears _runActive before this, so releasing the guard here can never
            // leave Start enabled while a load, a confirmation or a run is still in flight.
            _firstSyncApprovalInFlight = false;
            SetRunControls();
        }
    }

    // The existing first-sync approval body. It runs only while the single-flight guard above is held.
    private async Task ApproveBootstrapCoreAsync(FirstSyncReviewContext context)
    {
        var pairId = context.PairId;
        if (PairHealthBlockingReason(pairId) is { } blockingReason)
        {
            ShowStatus("First sync was not started. Repair this pair first: " + blockingReason);
            NavigateTo("Pairs");
            if (PairRows.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal)) is { } row)
            {
                PairList.SelectedItem = row;
                ExpandPairRow(row);
                PairEditorTabs.SelectedIndex = 3;
            }
            return;
        }
        if (!_bootstrapInventoryComplete)
        {
            ShowStatus("Scan both folders before starting the first sync.");
            return;
        }
        if (_bootstrapLocalScanGenerationId is null || _bootstrapDropboxScanGenerationId is null)
        {
            ShowStatus("Scan both folders before starting the first sync.");
            return;
        }
        SyncBootstrapActionPlan actionPlan;
        int uploads;
        int downloads;
        int skipped;
        try
        {
            var inventory = BootstrapReviewLoaderForTest is null
                ? await _authorityHost.LoadBootstrapReviewAsync(pairId,
                    reviewedLocalScanId: context.LocalGeneration, reviewedRemoteScanId: context.CloudGeneration)
                : await BootstrapReviewLoaderForTest(pairId, null, null, null, null);
            if (!IsCurrentFirstSyncContext(context))
            {
                SetFirstSyncFeedback("The displayed plan changed. Review the current pair before starting first sync.");
                return;
            }
            if (inventory.Any(item => item.PairId != pairId))
                throw new InvalidDataException("The complete inventory does not belong to the displayed pair.");
            var currentKeys = new HashSet<BootstrapActionKey>();
            var choices = new List<SyncBootstrapActionChoice>();
            foreach (var item in inventory.Where(item => SyncBootstrapActions.IsActionable(item.Group)))
            {
                var key = new BootstrapActionKey(pairId, PilotPath.NormalizeRelative(item.RelativePath));
                currentKeys.Add(key);
                var action = _bootstrapActionOverrides.TryGetValue(key, out var saved) &&
                             string.Equals(saved.Group, item.Group, StringComparison.Ordinal) &&
                             BootstrapActionAllowed(item.Group, saved.Action, item.Move)
                    ? saved.Action
                    : item.Recommendation;
                choices.Add(new SyncBootstrapActionChoice(item.RelativePath, item.Group, action,
                    SyncBootstrapActions.IsMove(action) ? item.Move : null));
            }
            foreach (var stale in _bootstrapActionOverrides.Keys.Where(key =>
                         string.Equals(key.PairId, pairId, StringComparison.Ordinal) &&
                         !currentKeys.Contains(key)).ToArray())
                _bootstrapActionOverrides.Remove(stale);
            actionPlan = new SyncBootstrapActionPlan(pairId, context.LocalGeneration,
                context.CloudGeneration, choices);
            uploads = choices.Count(item => string.Equals(item.Action, "Upload", StringComparison.Ordinal));
            downloads = choices.Count(item => string.Equals(item.Action, "Download", StringComparison.Ordinal));
            skipped = choices.Count(item => string.Equals(item.Action, "Skip for now", StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            ShowStatus("The complete first-sync plan could not be captured: " + exception.Message);
            return;
        }
        var reviewPair = _pairSet.Pairs.Single(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        var providerName = SyncProviders.DisplayName(reviewPair.Provider);
        // EPS4A: the confirmation names the two ordered endpoints this reviewed plan was built from,
        // so a reversed pair, a second same-provider account and a local-to-local pair read correctly.
        var endpoints = context.Endpoints;
        var moveChoices = actionPlan.Choices.Where(item => SyncBootstrapActions.IsMove(item.Action)).ToArray();
        var updatedUploads = actionPlan.Choices.Count(item => item.Action == "Upload" &&
            item.ExpectedGroup == SyncBootstrapActions.LocalUpdatedGroup);
        var updatedDownloads = actionPlan.Choices.Count(item => item.Action == "Download" &&
            item.ExpectedGroup == SyncBootstrapActions.RemoteUpdatedGroup);
        var movePreview = string.Join("\n", moveChoices.Select(item =>
            $"Move on {(item.Action == "MoveLocal" ? endpoints.LocalPortTitle : endpoints.RemotePortTitle)}: " +
            $"{item.Move!.FromPath} → {item.RelativePath}"));
        var preview = $"Folder Pair: {reviewPair.Name}\nAccount: {LabelForPair(reviewPair)}\n\n" +
                      $"Left endpoint: {endpoints.LeftTitle} — {EndpointReviewPresentation.Location(endpoints.Context.Left)}\n" +
                      $"Right endpoint: {endpoints.RightTitle} — {EndpointReviewPresentation.Location(endpoints.Context.Right)}\n\n" +
                      $"Copy {uploads - updatedUploads:N0} new item(s) from {endpoints.LocalPortTitle} to {endpoints.RemotePortTitle}.\n" +
                      $"Copy {downloads - updatedDownloads:N0} new item(s) from {endpoints.RemotePortTitle} to {endpoints.LocalPortTitle}.\n" +
                      $"Update {updatedUploads:N0} unchanged item(s) on {endpoints.RemotePortTitle} from {endpoints.LocalPortTitle}.\n" +
                      $"Update {updatedDownloads:N0} unchanged item(s) on {endpoints.LocalPortTitle} from {endpoints.RemotePortTitle}.\n" +
                      $"Move {moveChoices.Length:N0} existing file(s) to their reviewed paths.\n" +
                      (moveChoices.Length == 0 ? "" : movePreview + "\n") +
                      $"Skip {skipped:N0} item(s) for now; they remain unchanged and waiting here.\n" +
                      $"Leave {_bootstrapConflictItems:N0} conflicting item(s) unchanged for explicit resolution.\n" +
                      $"Inventory total: {_bootstrapTotalItems:N0}.\n\n" +
                      "Identical items only establish a baseline. Folders excluded in Folder pairs remain excluded.\n\n" +
                      "Sync Shark checks both folders again before changing files. Each reviewed path is checked on " +
                      "its own: your reviewed uploads and downloads continue safely on the latest version of that " +
                      "same item. Updates require the destination to still match the saved synchronized version; " +
                      "competing edits are never overwritten. Moves require the exact file identities and old paths shown here; " +
                      "changed move evidence is left for review. A path that changed or became a conflict is left unchanged and reported " +
                      "with its exact reason, and anything that appeared after this review waits for its own review.";
        var approved = BootstrapApprovalConfirmationForTest?.Invoke(preview) ??
                       AppDialogWindow.Confirm(this, "Start first sync",
                           "Review the first-sync plan", preview,
                           "Start first sync");
        if (!approved)
        {
            // UI1: a cancelled confirmation restores the coherent idle state instead of leaving the validating
            // text in place, and states exactly what did and did not change.
            SetFirstSyncFeedback("First sync cancelled. Your draft choices are unchanged; no files were changed.");
            return;
        }
        if (!IsCurrentFirstSyncContext(context))
            return;
        // SS-D01: this approval owns one cancellation token, created before any work is queued and before
        // the run is marked active, so Stop cancels the first-sync run itself instead of passing None.
        var approvalCancellation = BeginPendingRunCancellation();
        try
        {
            if (BootstrapApprovalForTest is not null && BootstrapActionPlanForTest is null)
            {
                var legacyTestPair = await BootstrapApprovalForTest(pairId, approvalCancellation.Token);
                _pairSet = await _authorityHost.LoadPairsAsync();
                await RefreshBootstrapReviewAsync();
                await RunPairsAsync([legacyTestPair], "Approved first sync");
                return;
            }

            var selectedPair = _pairSet.Pairs.Single(item => string.Equals(item.Id, pairId,
                StringComparison.Ordinal));
            _runActive = true;
            SetRunControls();
            RunProgress.IsIndeterminate = true;
            // UI2: this operation's own phase start, progress instant and detail replace the previous run's
            // through the same shared reset, and the pair this exact operation is for is named from the reviewed
            // context rather than inherited from the last run.
            ResetRunPresentation("Starting First Sync",
                "Checking the reviewed inventory before applying your choices.");
            _workspaceRunningPairId = pairId;
            _workspaceRunningPair = selectedPair.Name;
            SetFirstSyncFeedback($"Starting {selectedPair.Name}: {uploads:N0} uploads, {downloads:N0} downloads, " +
                $"{moveChoices.Length:N0} moves, {skipped:N0} skipped.");
            RefreshWorkspace();
            var applied = BootstrapActionPlanForTest is not null
                ? await BootstrapActionPlanForTest(actionPlan, approvalCancellation.Token)
                : await Task.Run(() => _authorityHost.RunBootstrapActionPlanAsync(actionPlan,
                    cancellationToken: approvalCancellation.Token,
                    onProgress: progress => PresentBatchProgressAsync(progress, "First sync", recoveryOnly: false),
                    // Admission/health/review reads precede an active run. Keep
                    // their actual process detail visible on First Sync and Home
                    // through the existing presentation, without inventing a run
                    // or claiming that files are already being transferred.
                    onPreparationProgress: message => Dispatcher.Invoke(() => ShowRunStatus(message))));
            Exception? refreshFailure = null;
            var reviewRefreshed = false;
            try
            {
                _pairSet = await _authorityHost.LoadPairsAsync();
                reviewRefreshed = await RefreshBootstrapReviewAsync();
            }
            catch (Exception exception)
            {
                refreshFailure = exception;
            }
            if (applied.Result.Decisions.Count > 0) ShowResult(applied.Pair, applied.Result);
            if (!reviewRefreshed || refreshFailure is not null)
            {
                SetFirstSyncFeedback($"{applied.Pair.Name}: {applied.AppliedItems:N0} reviewed action(s) completed. " +
                    (applied.FirstSyncComplete ? "Your approval is saved; do not approve these actions again. " : "Some reviewed items still need attention. ") +
                    "The page could not refresh. Completed operations remain in Activity; refresh the page for current status." +
                    (refreshFailure is null ? "" : $" ({refreshFailure.GetType().Name})"));
                return;
            }
            if (applied.FirstSyncComplete)
            {
                SetFirstSyncFeedback($"{applied.Pair.Name}: reviewed actions completed and approval saved. Applied: {applied.AppliedItems:N0}; skipped: {applied.SkippedItems:N0}. " +
                    "Normal sync checks later changes and any remaining conflicts. Existing pause and monitoring settings are unchanged.");
                await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
                // The pair is now past its first-sync review; reconfiguration's Start is a no-op, so kick it here
                // to start continuous sync for it at once.
                KickV2Monitors(applied.Pair.Id);
            }
            else
            {
                SetFirstSyncFeedback($"{applied.Pair.Name}: applied {applied.AppliedItems:N0} reviewed action(s). " +
                           $"{applied.SkippedItems:N0} item(s) remain waiting in First sync review.");
            }
        }
        catch (Exception exception)
        {
            SetFirstSyncFeedback("First-sync process stopped safely: " + exception.Message +
                       " Check Activity before trying again.");
        }
        finally
        {
            EndPendingRunCancellation(approvalCancellation);
            await FinishRunPresentationAsync("First sync",
                () => RefreshAuthorityProjectionsAsync(requireSuccess: true), SetFirstSyncFeedback,
                BootstrapChoiceSummary.Text);
        }
    }

    private static string? SelectedGroupKey(System.Windows.Controls.ComboBox box) =>
        box.SelectedItem is ComboBoxItem item ? (item.Tag ?? item.Content)?.ToString() : null;

    private static string FolderPresenceLabel(SyncFolderPresence presence) => presence switch
    {
        SyncFolderPresence.LocalOnly => "Only on this computer",
        SyncFolderPresence.CloudOnly => "Only in cloud",
        SyncFolderPresence.Mixed => "Both sides",
        SyncFolderPresence.OnlineOnly => "Online-only",
        SyncFolderPresence.LocalUnloaded => "This PC not loaded",
        SyncFolderPresence.DropboxUnloaded => "Cloud not loaded",
        SyncFolderPresence.Excluded => "Excluded",
        _ => presence.ToString()
    };

    private static FolderSelectionRow CreateFolderSelectionRow(string pairId, SyncFolderSelectionNode folder,
        string displayPath, bool? isChecked)
    {
        var rollup = folder.Rollup ?? new SyncFolderRollup(0, 0, 0, 0, 0, 0);
        return new FolderSelectionRow(pairId, folder.RelativePath, displayPath, isChecked,
            FolderPresenceLabel(folder.Presence), folder.DescendantFolders, folder.StableFolderIdentity,
            rollup.LocalItems, rollup.OnlineOnlyItems, rollup.MixedItems, rollup.ExcludedItems,
            rollup.PendingItems, rollup.ErrorItems);
    }

    private async Task RefreshConnectionStateAsync(bool verifyLive = true)
    {
        if (_closing) return;
        var refreshGeneration = Interlocked.Increment(ref _accountRefreshGeneration);
        try
        {
            var priorDraftConnection = _pairDraftConnectionId;
            SyncAccountStatus status = DropboxConnectionStatusLoaderForTest is null
                ? await _authorityHost.GetAccountStatusAsync(verifyLive, connectionId: _selectedConnectionId)
                : await DropboxConnectionStatusLoaderForTest(verifyLive);
            if (_closing || refreshGeneration != _accountRefreshGeneration) return;
            RememberAccountStatus(status);
            _connectionStatus = !verifyLive && status.Binding is { } cachedBinding
                ? _knownAccountStatus.GetValueOrDefault(cachedBinding.ConnectionId, status) : status;
            var binding = _connectionStatus.Binding;
            var providerName = binding?.ProviderName ?? "Dropbox";
            var connected = binding is not null && _connectionStatus.HasProtectedCredential;
            ConnectionText.Text = connected
                ? $"{providerName}: {(string.IsNullOrWhiteSpace(binding!.DisplayName) ? binding.Email : binding.DisplayName)}"
                : "Not connected";
            HeaderState.Text = connected
                ? (_connectionStatus.LiveVerified || _connectionStatus.FailureType is null
                    ? providerName + " connected"
                    : providerName + " needs attention")
                : "Safe idle";
            HeaderAccountButton.Content = connected
                ? (string.IsNullOrWhiteSpace(binding!.DisplayName) ? providerName + " account" : binding.DisplayName)
                : "No account";
            ConnectionProviderText.Text = binding is null ? "—" : providerName;
            ConnectionNameText.Text = binding is null
                ? "Not connected"
                : string.IsNullOrWhiteSpace(binding.DisplayName) ? providerName + " account" : binding.DisplayName;
            ConnectionEmailText.Text = binding is null || string.IsNullOrWhiteSpace(binding.Email) ? "—" : binding.Email;
            ConnectionTypeText.Text = binding is null || string.IsNullOrWhiteSpace(binding.AccountType)
                ? "—"
                : char.ToUpperInvariant(binding.AccountType[0]) + binding.AccountType[1..];
            ConnectionAccessText.Text = binding is null ? "—" : "Full " + providerName;
            ConnectionHealthText.Text = _connectionStatus.Health +
                                        (_connectionStatus.FailureType is null ? string.Empty : " (" + _connectionStatus.FailureType + ")");
            ConnectionVerifiedText.Text = binding?.LastVerifiedUtc?.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss") ?? "—";
            ConnectionVerifiedText.ToolTip = "Local time: " + TimeZoneInfo.Local.DisplayName;
            ConnectionLastSyncText.Text = _connectionStatus.LastSuccessfulSyncUtc?.ToLocalTime()
                .ToString("d MMM yyyy, HH:mm:ss") ?? "—";
            ConnectionLastSyncText.ToolTip = "Local time: " + TimeZoneInfo.Local.DisplayName;
            ConnectionPairsText.Text = $"{_connectionStatus.DependentPairCount} Linked Folder Pair{(_connectionStatus.DependentPairCount == 1 ? "" : "s")}";
            if (_storageSummary is not null)
                _storageSummary.Text = _connectionStatus.SpaceUsage is { } usage
                    ? usage.IsUnlimited ? $"{FormatByteSize(usage.UsedBytes)} used · No storage limit" : $"{FormatByteSize(usage.UsedBytes)} of {FormatByteSize(usage.AllocatedBytes)} used" +
                        (usage.IsTeam ? " (Team Storage)" : "") + $"\n{FormatByteSize(Math.Max(0, usage.AllocatedBytes - usage.UsedBytes))} available"
                    : verifyLive ? "Storage usage unavailable. Account access is shown separately." : "Refresh account to retrieve storage usage.";
            ConnectionIdText.Text = binding?.ConnectionId ?? "—";
            PairConnectionSummary.Text = PairAccountLabel;
            ConnectButton.Content = connected ? "Connection" : "Connect Dropbox";
            ConnectionConnectButton.Visibility = binding is not null ? Visibility.Collapsed : Visibility.Visible;
            ConnectionConnectButton.IsEnabled = !_runActive && !AuthorizationAttemptWaiting && !connected && DropboxProviderConfiguration.IsLiveProviderBound;
            ConnectionReauthorizeButton.IsEnabled = !_runActive && !AuthorizationAttemptWaiting && binding is not null;
            ConnectionManageButton.IsEnabled = connected;
            ConnectionManageButton.Content = CommandIcons.Label("Manage on " + providerName, "external");
            System.Windows.Automation.AutomationProperties.SetName(ConnectionManageButton, "Manage on " + providerName);
            ConnectionManageButton.ToolTip = "Manage this account on " + providerName;
            ConnectionReauthorizeButton.ToolTip = "Renew authorization for this exact " + providerName + " account connection.";
            ConnectionDisconnectButton.ToolTip = "Stops live sync and removes this account's protected credential. Pair definitions and Recovery data are retained.";
            ConnectionDisconnectButton.IsEnabled = !_runActive && connected;
            RefreshPairRows(_pairSet.SelectedPairId);
            RefreshAccountPickers();
            if (_editingNewPair && !string.Equals(priorDraftConnection, SelectedPairConnectionId,
                    StringComparison.Ordinal))
                BeginNewPairEditor();
        }
        catch (Exception exception)
        {
            if (_closing || refreshGeneration != _accountRefreshGeneration) return;
            var retainedConnection = _connectionStatus?.Binding is not null &&
                                     _connectionStatus.HasProtectedCredential;
            ConnectionText.Text = retainedConnection ? ConnectionLabel : "Connection check unavailable";
            ConnectionHealthText.Text = "Connection check could not be completed (" + exception.GetType().Name + ").";
            var providerName = _connectionStatus?.Binding?.ProviderName ?? "Cloud storage";
            HeaderState.Text = retainedConnection ? providerName + " needs attention" : "Connection check unavailable";
            HeaderAccountButton.Content = retainedConnection
                ? (string.IsNullOrWhiteSpace(_connectionStatus!.Binding!.DisplayName)
                    ? providerName + " account"
                    : _connectionStatus.Binding.DisplayName)
                : "Check connection";
            PairConnectionSummary.Text = retainedConnection ? ConnectionLabel : "Connection check unavailable";
            ConnectButton.Content = retainedConnection ? "Connection" : "Check connection";
            ConnectionConnectButton.Visibility = Visibility.Collapsed;
            ConnectionConnectButton.IsEnabled = false;
        }
        ConnectButton.IsEnabled = DropboxProviderConfiguration.IsLiveProviderBound;
        UpdatePairSaveAvailability();
    }

    private async Task RefreshAuthorityProjectionsAsync(bool requireSuccess = false, string? selectedPairId = null)
    {
        if (!_authorityHost.AuthorityStateExists) return;
        var loadActivity = !_startupLoading || _workspacePage == "Activity";
        try
        {
            // Publish the current terminal reason first. An unrelated activity,
            // queue or history read must not leave yesterday's failure on screen.
            await RunStartupProjectionWorkAsync("Reading retained first-sync and sync-command outcomes for each pair, including exact failure causes and run links; previous failures are not discarded.",
                RefreshRetainedRunProblemsAsync);
            IReadOnlyDictionary<string, string> pairNames = new Dictionary<string, string>();
            await RunStartupProjectionWorkAsync("Reading saved pair identities and names for the history tables.", async () =>
                pairNames = (await Task.Run(() => _authorityHost.LoadPairsAsync())).Pairs.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal));
            // Home/tray and durable attention still load below. Do not also read
            // the hidden Activity table during startup; entering it loads fresh rows.
            if (loadActivity)
                await RunStartupProjectionWorkAsync("Reading the first retained Activity page with the selected account, category and outcome filters.",
                    () => LoadActivityPageAsync(reset: true, pairNames));
            await RunStartupProjectionWorkAsync("Reading the first retained pending-operation page; no queued file operation is being executed.",
                () => LoadQueuePageAsync(reset: true, pairNames));
            await RunStartupProjectionWorkAsync("Reading recent changes and file-only activity for the Home and tray feeds.", RefreshHomeHistoryAsync);
            if (_fullAppReady)
            {
                await RunStartupProjectionWorkAsync("Reading the first run-history page and the selected scope's last successful sync; retained failures remain visible.",
                    () => LoadRunHistoryAsync(true));
            }
            if (requireSuccess && ((loadActivity && _activityLoadFailed) || _queueLoadFailed || _runLoadFailed))
                throw new IOException("Saved activity, pending operations or run history could not be loaded. No empty result has been assumed.");
            await RunStartupProjectionWorkAsync("Reading the first unresolved-conflict page; no conflict resolution is being applied.",
                () => LoadConflictPageAsync(reset: true, pairNames));
            IReadOnlySet<string> durableAttention = new HashSet<string>();
            await RunStartupProjectionWorkAsync("Reading the pair IDs with blocked, uncertain or cleanup-pending operations and unresolved conflicts.",
                async () => durableAttention = await Task.Run(() => _authorityHost.LoadPairIdsNeedingAttentionAsync()));
            _attentionPairIds.Clear();
            _attentionPairIds.UnionWith(durableAttention);
            _attentionPairIds.UnionWith(_runFailurePairIds);
            foreach (var pair in _pairSet.Pairs)
                if (_monitorStateByPair.TryGetValue(pair.Id, out var monitor) &&
                    (monitor.Contains("attention", StringComparison.OrdinalIgnoreCase) ||
                     monitor.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     monitor.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                     monitor.Contains("unavailable", StringComparison.OrdinalIgnoreCase)))
                    _attentionPairIds.Add(pair.Id);
            RefreshPairRows(selectedPairId ?? _pairSet.SelectedPairId);
            UpdateQueueCount();
        }
        catch (Exception exception)
        {
            ShowStatus("SQLite authority projections could not be loaded: " + exception.Message);
            if (requireSuccess) throw;
        }
    }

    private async Task LoadActivityPageAsync(bool reset, IReadOnlyDictionary<string, string>? pairNames = null)
    {
        _activityPageLoads++;
        if (_workspaceReady) RefreshWorkspaceFeeds();
        try { await LoadActivityPageCoreAsync(reset, pairNames); }
        catch (Exception exception)
        {
            _activityLoadFailed = true; _activityLoadFailure = exception.Message;
            // UI09: the list's own summary states the real failure, so the error is visible where the
            // rows would be instead of only in the shared status line.
            ActivityPageSummary.Text = ActivityCountText();
            ShowStatus("Activity could not be loaded: " + exception.Message);
        }
        finally
        {
            _activityPageLoads--;
            if (_workspaceReady) RefreshWorkspaceFeeds();
        }
    }

    private async Task LoadActivityPageCoreAsync(bool reset, IReadOnlyDictionary<string, string>? pairNames = null)
    {
        RefreshActivityAccountScope();
        if (!_authorityHost.AuthorityStateExists || ActivitySearchBox is null || ActivityFilterBox is null ||
            ActivityTypeFilter is null ||
            ActivityDateBox is null ||
            LoadMoreActivityButton is null || ActivityPageSummary is null) return;
        _activityLoadFailed = false; _activityLoadFailure = null;
        var version = Interlocked.Increment(ref _activityQueryVersion);
        // UI09: the account scope is bound to this request. A response that arrives after the user
        // changed the account is discarded instead of being applied to the new scope.
        var accountScope = _workspaceAccountId;
        if (reset) _activityRetainedOffset = 0;
        var offset = _activityRetainedOffset;
        var search = ActivitySearchBox.Text.Trim();
        var filter = SelectedGroupKey(ActivityFilterBox) ?? "All actions";
        if (_workspaceReady && _activityTabs.SelectedIndex == 0 && !_includeRoutineChecks && filter == "All actions")
            filter = "Changes";
        var itemType = string.IsNullOrWhiteSpace(ActivityTypeFilter.Text)
            ? "All types"
            : ActivityTypeFilter.Text.Trim();
        var pairId = (ActivityPairBox.SelectedItem as ActivityPairChoice)?.Id;
        var connectionId = accountScope;
        var sinceUtc = ActivitySinceUtc();
        var categories = SelectedActivityCategories();
        // Severity, entity and run stay independent of the selected categories; every dimension is
        // carried by the one merged scoped query rather than a second competing filter.
        var query = new SyncActivityQuery(categories.Count == 0 ? null : categories,
            CurrentActivitySeverity() is { Length: > 0 } severity ? severity : null,
            CurrentActivityEntity() is { Length: > 0 } entity ? entity : null,
            CurrentActivityRun() is { Length: > 0 } run ? run : null,
            ActivityReadsOldestFirst());
        LoadMoreActivityButton.IsEnabled = false;
        await _authorityHost.MaintainOrdinaryActivityHistoryAsync();
        // The retention window is a SQLite read like the page below, so it runs on the worker rather
        // than on the UI thread: a slow read here would freeze the Activity refresh.
        var retainedWindow = await Task.Run(() => _authorityHost.LoadActivityRetentionWindowAsync(runId: query.RunId));
        var page = ActivityProjectionLoaderForTest is null
            ? await Task.Run(() => _authorityHost.LoadActivityProjectionPageAsync(ProjectionPageSize + 1, offset,
                search, filter, itemType, pairId, sinceUtc, connectionId: connectionId, query: query))
            : await ActivityProjectionLoaderForTest(ProjectionPageSize + 1, offset, search, filter, itemType, pairId,
                sinceUtc, connectionId);
        if (version != _activityQueryVersion || !string.Equals(accountScope, _workspaceAccountId, StringComparison.Ordinal))
            return;
        // UI09: record the query generation that actually reached the list, so Refresh reports what
        // was applied instead of what it requested.
        _activityLastAppliedQueryVersion = version;
        _activityRetentionWindow = retainedWindow;
        pairNames ??= _pairSet.Pairs.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        var selectedKey = (ActivityGrid?.SelectedItem as ActivityRow)?.EventKey;
        if (reset) ActivityRows.Clear();
        foreach (var entry in page.Take(ProjectionPageSize))
        {
            ActivityRows.Add(RetainedActivityRow(entry));
        }
        // A retained-page read may overlap new events or their short database batch. Never erase
        // those live observations when the older read completes. The buffer is presentation-only,
        // bounded, and replaced by the matching authoritative row as soon as that row is available.
        foreach (var live in _liveActivityRows.ToArray())
        {
            if (ActivityRows.Any(row => SameLiveObservation(row, live))) _liveActivityRows.Remove(live);
            else if (ActivityRowMatchesCurrentFilter(live) && !ActivityRows.Contains(live)) ActivityRows.Add(live);
        }
        _activityRetainedOffset += Math.Min(page.Count, ProjectionPageSize);
        _activityHasMore = page.Count > ProjectionPageSize;
        LoadMoreActivityButton.IsEnabled = _activityHasMore;
        LoadMoreActivityButton.Visibility = _activityHasMore || ActivityRows.Count > ProjectionPageSize
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshWorkspaceFeeds();
        RestoreActivitySelection(selectedKey);
        ActivityPageSummary.Text = ActivityCountText();
    }

    // UI09: a valid selection survives refresh by stable event identity, never by row index.
    private void RestoreActivitySelection(string? selectedKey)
    {
        if (ActivityGrid is null || string.IsNullOrEmpty(selectedKey)) return;
        var restored = _recentRows.FirstOrDefault(row =>
            string.Equals(row.EventKey, selectedKey, StringComparison.Ordinal));
        if (restored is not null) ActivityGrid.SelectedItem = restored;
        else if (_inspectedActivity is { } inspected &&
                 string.Equals(inspected.EventKey, selectedKey, StringComparison.Ordinal)) _inspectedActivity = null;
    }

    private async Task LoadQueuePageAsync(bool reset, IReadOnlyDictionary<string, string>? pairNames = null)
    {
        _queuePageLoads++;
        try { await LoadQueuePageCoreAsync(reset, pairNames); _queueLoadFailed = false; }
        catch (Exception exception)
        {
            _queueLoadFailed = true;
            QueuePageSummary.Text = "Pending operations could not be loaded. Refresh to retry.";
            ShowStatus("Pending operations could not be loaded: " + exception.Message);
        }
        finally { _queuePageLoads--; }
    }

    private async Task LoadQueuePageCoreAsync(bool reset, IReadOnlyDictionary<string, string>? pairNames = null)
    {
        RefreshActivityAccountScope();
        if (!_authorityHost.AuthorityStateExists || QueueSearchBox is null || QueueFilterBox is null ||
            LoadMoreQueueButton is null || QueuePageSummary is null) return;
        var version = Interlocked.Increment(ref _queueQueryVersion);
        var offset = reset ? 0 : QueueRows.Count;
        var search = QueueSearchBox.Text.Trim();
        var filter = SelectedGroupKey(QueueFilterBox) ?? "All";
        var queuePair = (ActivityPairBox.SelectedItem as ActivityPairChoice)?.Id;
        var queueAccount = _workspaceAccountId;
        var queueType = string.IsNullOrWhiteSpace(ActivityTypeFilter.Text) ? "All types" : ActivityTypeFilter.Text.Trim();
        var queueSince = ActivitySinceUtc();
        LoadMoreQueueButton.IsEnabled = false;
        _globalPendingQueueCount = PendingQueueCountLoaderForTest is null
            ? await Task.Run(() => _authorityHost.LoadPendingQueueCountAsync())
            : await PendingQueueCountLoaderForTest();
        var page = await Task.Run(() => _authorityHost.LoadQueueProjectionPageAsync(ProjectionPageSize + 1, offset,
            search, filter, pairId: queuePair, itemType: queueType, sinceUtc: queueSince,
            connectionId: queueAccount));
        if (version != _queueQueryVersion || queueAccount != _workspaceAccountId) return;
        pairNames ??= _pairSet.Pairs.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        if (reset) QueueRows.Clear();
        foreach (var entry in page.Take(ProjectionPageSize))
        {
            var pairName = pairNames.GetValueOrDefault(entry.PairId, entry.PairId);
            QueueRows.Add(new QueueRow(entry.PairId,
                entry.EnqueuedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), pairName,
                entry.RelativePath, entry.Priority, entry.Operation,
                entry.RecoveryRequired ? entry.State + " (Recovery First)" : entry.State, LabelForPairId(entry.PairId), entry.OperationId,
                entry.ItemKind, entry.FileType));
        }
        var hasMore = page.Count > ProjectionPageSize;
        LoadMoreQueueButton.IsEnabled = hasMore;
        LoadMoreQueueButton.Visibility = hasMore || QueueRows.Count > ProjectionPageSize
            ? Visibility.Visible : Visibility.Collapsed;
        QueuePageSummary.Text = $"Showing {QueueRows.Count} matching item(s)" +
                                (hasMore ? "; more are available." : ".");
        UpdateQueueCount();
    }

    private bool ActivityRowMatchesCurrentFilter(ActivityRow row)
    {
        if (ActivitySearchBox is null || ActivityFilterBox is null || ActivityTypeFilter is null ||
            ActivityPairBox is null ||
            ActivityDateBox is null) return true;
        var pairId = (ActivityPairBox.SelectedItem as ActivityPairChoice)?.Id;
        if (!row.MatchesAccount(_workspaceAccountId)) return false;
        if (!row.InCategories(SelectedActivityCategories())) return false;
        if (CurrentActivitySeverity() is { Length: > 0 } severity && !string.Equals(row.Severity, severity, StringComparison.OrdinalIgnoreCase)) return false;
        if (CurrentActivityEntity() is { Length: > 0 } entity && !string.Equals(row.Entity, entity, StringComparison.OrdinalIgnoreCase)) return false;
        if (CurrentActivityRun() is { Length: > 0 } run && !string.Equals(row.RunId, run, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(pairId) && !string.Equals(row.PairId, pairId, StringComparison.Ordinal))
            return false;
        var query = ActivitySearchBox.Text.Trim();
        if (query.Length > 0 && !new[] { row.PairName, row.Path, row.Outcome, row.Reason, row.OperationId ?? "" }
                .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return false;
        var sinceUtc = ActivitySinceUtc();
        if (sinceUtc is not null && (!DateTimeOffset.TryParse(row.When, out var occurred) ||
                                     occurred.ToUniversalTime() < sinceUtc.Value)) return false;
        var itemType = string.IsNullOrWhiteSpace(ActivityTypeFilter.Text)
            ? "All types"
            : ActivityTypeFilter.Text.Trim();
        if (!ActivityTypeMatches(row, itemType)) return false;
        var filter = SelectedGroupKey(ActivityFilterBox) ?? "All actions";
        return filter switch
        {
            "Transfers" => row.Outcome.Contains("upload", StringComparison.OrdinalIgnoreCase) ||
                           row.Outcome.Contains("download", StringComparison.OrdinalIgnoreCase) ||
                           row.Outcome.Contains("transfer", StringComparison.OrdinalIgnoreCase),
            "Moves" => row.Outcome.Contains("move", StringComparison.OrdinalIgnoreCase),
            "Conflicts" => row.Outcome.Contains("conflict", StringComparison.OrdinalIgnoreCase),
            "Recovery / deletions" => row.Outcome.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                                      row.Outcome.Contains("recover", StringComparison.OrdinalIgnoreCase),
            "Needs attention" => row.Outcome.Contains("block", StringComparison.OrdinalIgnoreCase) ||
                                 row.Outcome.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
                                 row.Outcome.Contains("attention", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private DateTimeOffset? ActivitySinceUtc()
    {
        var range = (ActivityDateBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All time";
        var now = DateTimeOffset.Now;
        return range switch
        {
            "Today" => new DateTimeOffset(now.Date, now.Offset).ToUniversalTime(),
            "Last 7 days" => now.AddDays(-7).ToUniversalTime(),
            "Last 30 days" => now.AddDays(-30).ToUniversalTime(),
            _ => null
        };
    }

    private async void ActivityFilter_Changed(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!IsLoaded || _updatingActivityPairs) return;
        // Editable ComboBox.SelectionChanged precedes its Text update. Read the
        // committed text on the next dispatcher turn, including rapid selections.
        // Normal priority still runs after this selection event has unwound, but
        // unlike Background it cannot be starved by continuous rendering work.
        if (ReferenceEquals(sender, ActivityTypeFilter) && e is SelectionChangedEventArgs)
        {
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Normal);
            if (!IsLoaded || _updatingActivityPairs) return;
        }
        if (_fullAppReady && _activityTabs.SelectedIndex == 1) { await LoadQueuePageAsync(true); ApplyPendingViewFilter(); }
        else if (_fullAppReady && _activityTabs.SelectedIndex == 2) RenderRunHistory();
        else await LoadActivityPageAsync(reset: true);
    }

    private void ActivityTypeFilter_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ActivityFilter_Changed(sender, e);

    private static bool ActivityTypeMatches(ActivityRow row, string itemType) => itemType switch
    {
        "All types" => true,
        "Folders" => string.Equals(row.ItemKind, "Folder", StringComparison.OrdinalIgnoreCase),
        "Files" => string.Equals(row.ItemKind, "File", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(row.FileType, itemType.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
    };

    private async void QueueFilter_Changed(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (IsLoaded) await LoadQueuePageAsync(reset: true);
    }

    private async void LoadMoreActivity_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await LoadActivityPageAsync(reset: false);
    }

    private async void LoadMoreQueue_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!_runActive) await LoadQueuePageAsync(reset: false);
    }

    private async Task LoadConflictPageAsync(bool reset, IReadOnlyDictionary<string, string>? pairNames = null)
    {
        var version = Interlocked.Increment(ref _conflictQueryVersion);
        _loadingConflicts = true;
        _conflictLoadFailure = null;
        try { await LoadConflictPageCoreAsync(reset, pairNames, version); }
        catch (Exception exception)
        {
            if (version != _conflictQueryVersion) return;
            _conflictLoadFailure = "Conflicts could not be loaded: " + exception.Message;
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, _conflictLoadFailure, isError: true);
            ShowStatus(_conflictLoadFailure);
        }
        finally
        {
            if (version == _conflictQueryVersion)
            {
                _loadingConflicts = false;
                ActionAvailability.Refresh(LoadMoreConflictsButton);
            }
        }
    }

    private async Task LoadConflictPageCoreAsync(bool reset, IReadOnlyDictionary<string, string>? pairNames,
        int version)
    {
        if (!_authorityHost.AuthorityStateExists || ConflictSearchBox is null || LoadMoreConflictsButton is null)
            return;
        var offset = reset ? 0 : _allConflictRows.Count;
        var search = ConflictSearchBox.Text.Trim();
        var group = SelectedGroupKey(ConflictGroupFilter) ?? "All groups";
        var accountId = _workspaceAccountId;
        if (reset) ClearMatchingConflictSelection();
        LoadMoreConflictsButton.IsEnabled = false;
        ActionAvailability.Refresh(LoadMoreConflictsButton);
        SharedPresentation.SetInlineFeedback(ConflictActionFeedback, "Loading the conflicts matching these filters…", isError: false);
        if (ConflictPageCheckpointForTest is not null) await ConflictPageCheckpointForTest();
        var page = await Task.Run(() => _authorityHost.LoadOpenConflictComparisonsPageAsync(ProjectionPageSize + 1,
            offset, search, group, connectionId: accountId));
        if (version != _conflictQueryVersion || accountId != _workspaceAccountId) return;
        pairNames ??= _pairSet.Pairs.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal);
        if (reset) _allConflictRows.Clear();
        var conflictContexts = _pairSet.Pairs.ToDictionary(pair => pair.Id, TryConflictEndpointPresentation,
            StringComparer.Ordinal);
        foreach (var conflict in page.Take(ProjectionPageSize))
        {
            var pairName = pairNames.GetValueOrDefault(conflict.PairId, conflict.PairId);
            // W2-B: the pair and its captured endpoint context are resolved ONCE per batch, never per row
            // and never through a provider or account call. Every size and time below is the ordered side
            // the pair's own layout assigns, so a reversed or two-cloud pair is never described as local
            // or Dropbox. A pair whose endpoints cannot be described keeps the neutral ordered wording.
            var endpoints = conflictContexts.GetValueOrDefault(conflict.PairId);
            _allConflictRows.Add(new DecisionRow(conflict.PairId, pairName, conflict.RelativePath,
                SyncDecisionKind.Conflict.ToString(),
                ConflictSummaryReason(endpoints, conflict.LocalSize, conflict.DropboxSize),
                LabelForPairId(conflict.PairId), conflict.Group, conflict.Recommendation,
                FormatConflictTime(endpoints is null ? conflict.LocalModifiedUtc
                    : endpoints.LeftValue(conflict.LocalModifiedUtc, conflict.DropboxModifiedUtc)),
                FormatConflictTime(endpoints is null ? conflict.DropboxModifiedUtc
                    : endpoints.RightValue(conflict.LocalModifiedUtc, conflict.DropboxModifiedUtc)),
                conflict.ConflictId, conflict.Generation, endpoints?.Context, conflict.Options));
        }
        var hasMore = page.Count > ProjectionPageSize;
        LoadMoreConflictsButton.IsEnabled = hasMore;
        LoadMoreConflictsButton.Visibility = hasMore || _allConflictRows.Count > ProjectionPageSize
            ? Visibility.Visible : Visibility.Collapsed;
        SharedPresentation.SetInlineFeedback(ConflictActionFeedback,
            $"Loaded {_allConflictRows.Count:N0} matching conflict(s)." + (hasMore ? " More rows are available." : ""), isError: false);
        ApplyConflictFilter();
        RefreshConflictActionPresentation();
        if (reset) _attentionConflictRows.RemoveAll(row => ScopeMatches(row.PairId));
        foreach (var row in _allConflictRows)
        {
            _attentionConflictRows.RemoveAll(item => item.PairId == row.PairId && item.Path == row.Path);
            _attentionConflictRows.Add(row);
        }
        _attentionFingerprint = ""; RefreshFullApp();
        if (_matchingConflictSelection.Count > 0) ConflictGrid.SelectAll();
    }

    private async void LoadMoreConflicts_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await LoadConflictPageAsync(reset: false);
    }

    private async void RecoveryPair_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // UI16: only the programmatic selection written by RefreshRecoveryPairChoicesAsync is ignored.
        // A real pair or account change always rebases the list, even while a page is still in flight,
        // so the presented rows and the selector can never disagree about which pair is displayed.
        if (_selectingRecoveryPair || RecoveryPage is null || RecoveryPage.Visibility != Visibility.Visible) return;
        await RefreshRecoveryAsync(includeDeleted: _recoveryTabs?.SelectedIndex == 1);
    }

    private async void RefreshRecovery_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await RefreshRecoveryAsync(includeDeleted: _recoveryTabs?.SelectedIndex == 1);
    }

    private async Task RefreshRecoveryAsync(bool includeDeleted)
    {
        _recoveryDeletedRequested = includeDeleted;
        // UI16: a refresh or pair change that arrives while a page is in flight is recorded rather
        // than dropped, so the list can never keep the previous pair's rows.
        if (_loadingRecovery)
        {
            _recoveryReloadPending = true;
            ++_recoveryQueryVersion;
            ApplyRecoveryContext(CurrentRecoveryPairId());
            return;
        }
        try { await RefreshRecoveryPairChoicesAsync(); }
        catch (Exception exception)
        {
            RecoverySummary.Text = "Recovery folder pairs could not be loaded: " + exception.Message;
            return;
        }
        await LoadRecoveryPageAsync(reset: true, includeDeleted);
    }

    private string? CurrentRecoveryPairId() =>
        (RecoveryPairBox.SelectedItem as SyncRecoveryPairChoice)?.Id ?? _pairSet.SelectedPairId;

    // UI16: one recovery context at a time. The selected saved copy and the revision path belong to
    // the pair they were loaded for, so a different pair or account discards them instead of leaving
    // a row that could be restored against the wrong pair.
    private void ApplyRecoveryContext(string? pairId)
    {
        var next = string.IsNullOrWhiteSpace(pairId) ? null : pairId;
        var drive = _pairSet.Pairs.Any(pair => pair.Id == next && pair.Provider == SyncProviders.GoogleDrive);
        if (_recoveryTabs is not null)
            foreach (var tab in _recoveryTabs.Items.Cast<TabItem>().Skip(2))
            {
                tab.IsEnabled = !drive;
                tab.ToolTip = drive ? "Available for Dropbox pairs only." : null;
            }
        if (string.Equals(next, _recoverySelectedPairId, StringComparison.Ordinal)) return;
        _recoverySelectedPairId = next;
        ++_recoveryQueryVersion;
        _allRecoveryRows.Clear();
        RecoveryRows.Clear();
        RecoveryGrid.SelectedItem = null;
        RecoveryPathBox.Text = string.Empty;
        ShowRecoveryDetails(null);
    }

    private async Task LoadRecoveryPageAsync(bool reset, bool includeDeleted)
    {
        var pairId = CurrentRecoveryPairId();
        ApplyRecoveryContext(pairId);
        if (string.IsNullOrWhiteSpace(pairId))
        {
            RecoveryRows.Clear();
            RecoverySummary.Text = "Create and select a pair before browsing Recovery.";
            return;
        }
        if (_loadingRecovery)
        {
            _recoveryReloadPending = true;
            return;
        }
        _loadingRecovery = true;
        RecoveryPairBox.IsEnabled = false;
        LoadMoreRecoveryButton.IsEnabled = false;
        var version = Interlocked.Increment(ref _recoveryQueryVersion);
        try
        {
            var search = RecoverySearchBox.Text.Trim();
            var google = _pairSet.Pairs.Any(pair => pair.Id == pairId && pair.Provider == SyncProviders.GoogleDrive);
            var loadDeleted = includeDeleted && search.Length == 0 && (reset || _recoveryHasMoreDeleted);
            RecoverySummary.Text = reset
                ? loadDeleted
                    ? "Loading retained copies and deleted cloud items…"
                    : "Loading the first matching protected-archive page…"
                : "Loading the next recovery page…";
            if (reset)
            {
                _recoveryArchiveOffset = 0;
                _recoveryDeletedCursor = null;
                _recoveryHasMoreArchives = true;
                _recoveryHasMoreDeleted = loadDeleted;
            }
            var page = RecoveryCatalogLoaderForTest is null
                ? await _authorityHost.LoadRecoveryCatalogPageAsync(pairId, _recoveryArchiveOffset,
                    _recoveryDeletedCursor, loadDeleted, ProjectionPageSize, search)
                : await RecoveryCatalogLoaderForTest(pairId, _recoveryArchiveOffset, _recoveryDeletedCursor,
                    loadDeleted, ProjectionPageSize, search, CancellationToken.None);
            // UI16: a superseded request, and a page for a pair the user has already left, never
            // reach the list.
            if (version != _recoveryQueryVersion ||
                !string.Equals(pairId, CurrentRecoveryPairId(), StringComparison.Ordinal)) return;
            if (page.Items.Any(item => !string.Equals(item.PairId, pairId, StringComparison.Ordinal)))
                throw new InvalidDataException("The returned recovery page does not belong to the selected pair.");
            if (reset) _allRecoveryRows.Clear();
            var known = _allRecoveryRows.Select(item => (item.Item.Source, item.Item.Key)).ToHashSet();
            foreach (var item in page.Items)
                if (known.Add((item.Source, item.Key))) _allRecoveryRows.Add(ToRecoveryRow(item));
            _recoveryArchiveOffset = page.NextArchiveOffset;
            _recoveryDeletedCursor = page.DeletedCursor;
            _recoveryHasMoreArchives = page.HasMoreArchives;
            _recoveryHasMoreDeleted = loadDeleted && page.HasMoreDeleted;
            ApplyRecoveryFilter();
            var loaded = _allRecoveryRows.Select(item => item.Item).ToArray();
            var local = loaded.Count(item => item.Source == SyncRecoverySource.LocalArchive);
            var cloud = loaded.Count(item => item.Source == SyncRecoverySource.DropboxArchive);
            var deleted = loaded.Count(item => item.Source is SyncRecoverySource.DropboxDeleted or SyncRecoverySource.CloudTrash);
            var size = loaded.Where(item => item.Size is not null).Sum(item => item.Size ?? 0);
            var hasMore = _recoveryHasMoreArchives || _recoveryHasMoreDeleted;
            LoadMoreRecoveryButton.Visibility = hasMore || !reset ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreRecoveryButton.IsEnabled = hasMore;
            RecoverySummary.Text = $"Local Copies: {local:N0}    Cloud Copies: {cloud:N0}" +
                                   (loadDeleted || deleted > 0 ? $"    Deleted Paths: {deleted:N0}" : string.Empty) +
                                   $"\nRecorded Size: {FormatByteSize(size)}" +
                                   (hasMore ? "\nMore copies are available. Use Load More." : string.Empty) +
                                   (search.Length > 0 ? " Search covers retained copies; clear it to load deleted cloud items." : string.Empty) +
                                   "\nCleanup is a separate action and requires confirmation." +
                                   (google && includeDeleted ? " Enter a binary file path to load Google revision history. Trash restores original cloud items only." : "") +
                                   (page.Warnings?.Count > 0 ? "\n" + string.Join("\n", page.Warnings) : "");
        }
        catch (Exception exception)
        {
            if (version != _recoveryQueryVersion) return;
            RecoverySummary.Text = "Recovery could not be refreshed: " + exception.Message;
            ShowStatus(RecoverySummary.Text);
        }
        finally
        {
            RecoveryPairBox.IsEnabled = true;
            _loadingRecovery = false;
            var reload = _recoveryReloadPending;
            _recoveryReloadPending = false;
            if (reload && RecoveryPage.Visibility == Visibility.Visible)
                await LoadRecoveryPageAsync(reset: true, _recoveryDeletedRequested);
        }
    }

    private async void LoadMoreRecovery_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await LoadRecoveryPageAsync(reset: false, _recoveryDeletedRequested);
    }

    private async void LoadRecoveryRevisions_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || _loadingRecovery) return;
        var pairId = (RecoveryGrid.SelectedItem as RecoveryRow)?.Item.PairId ??
                     (RecoveryPairBox.SelectedItem as SyncRecoveryPairChoice)?.Id ?? _pairSet.SelectedPairId;
        if (string.IsNullOrWhiteSpace(pairId) || string.IsNullOrWhiteSpace(RecoveryPathBox.Text))
        {
            // UF2: an explicit user action reports on the invoking Recovery page, through the one shared
            // inline surface, and returns before the host or any provider is asked for anything. The
            // existing console recording continues unchanged behind it.
            var missingPrerequisite = string.IsNullOrWhiteSpace(pairId)
                ? "Choose a folder pair above before loading revisions."
                : "Enter or select one pair-relative cloud path before loading revisions.";
            SharedPresentation.SetInlineFeedback(RecoverySummary, missingPrerequisite, isError: false);
            ShowStatus(missingPrerequisite);
            return;
        }
        _loadingRecovery = true;
        var generation = _recoveryQueryVersion;
        try
        {
            var path = RecoveryPathBox.Text.Trim().TrimStart('/');
            if (_recoveryTabs is not null) _recoveryTabs.SelectedIndex = 1;
            RecoverySummary.Text = "Loading exact cloud revisions for " + path + "…";
            var revisions = RevisionCatalogLoaderForTest is not null
                ? await RevisionCatalogLoaderForTest(pairId, path)
                : await _authorityHost.LoadDropboxRevisionCatalogAsync(pairId, path);
            if (generation != _recoveryQueryVersion || pairId != CurrentRecoveryPairId()) return;
            _allRecoveryRows.RemoveAll(item => item.Item.Source == SyncRecoverySource.DropboxRevision &&
                                               string.Equals(item.Item.PairId, pairId, StringComparison.Ordinal) &&
                                               string.Equals(item.Item.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            _allRecoveryRows.AddRange(revisions.Select(ToRecoveryRow));
            ApplyRecoveryFilter();
            RecoverySummary.Text = revisions.Count == 0
                ? "The provider returned no retained revisions for " + path + "."
                : $"Loaded {revisions.Count:N0} exact revision(s) for {path}. Select one to preview a restore.";
        }
        catch (Exception exception)
        {
            RecoverySummary.Text = "Cloud revision history could not be loaded: " + exception.Message;
            ShowStatus(RecoverySummary.Text);
        }
        finally
        {
            _loadingRecovery = false;
            if (_recoveryReloadPending && RecoveryPage.Visibility == Visibility.Visible)
            {
                _recoveryReloadPending = false;
                await LoadRecoveryPageAsync(reset: true, _recoveryDeletedRequested);
            }
        }
    }

    private void RecoveryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowRecoveryDetails(RecoveryGrid.SelectedItem as RecoveryRow);
    }

    // UI16: one projection drives the shared details host, the exact per-copy action state and the
    // plain-text record. Selecting another saved copy can never leave the previous copy's actions
    // enabled, and the uncertainty guards keep their existing wording.
    private void ShowRecoveryDetails(RecoveryRow? row)
    {
        var idle = !_runActive && !_loadingRecovery;
        RestoreOriginalButton.IsEnabled = row?.Item.CanRestore == true && idle;
        RestoreCopyButton.IsEnabled = row?.Item.CanRestoreCopy == true && idle;
        RemoveRecoveryButton.IsEnabled = row?.Item.CanRemove == true && idle;
        if (row is null)
        {
            RecoveryDetails.Text = "No recovery item selected.";
            RenderRecoveryDetails(null);
            return;
        }
        RecoveryPathBox.Text = row.Path;
        RecoveryDetails.Text = row.DetailText();
        RenderRecoveryDetails(row);
    }

    private void RenderRecoveryDetails(RecoveryRow? row)
    {
        if (_recoveryDetailFields is null) return;
        _recoveryDetailsHost?.Present(row!);
        if (_recoveryInspector is not null && _recoveryInspectorColumn is not null)
        {
            _recoveryInspector.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
            _recoveryInspectorColumn.Width = row is null ? new GridLength(0) : new GridLength(0.8, GridUnitType.Star);
        }
        _recoveryDetailFields.Children.Clear();
        if (row is null)
        {
            _recoveryDetailFields.Children.Add(WorkspaceVisuals.Text("No recovery item selected.", 13,
                brush: "MutedInk"));
            return;
        }
        foreach (var field in row.DetailFields())
            _recoveryDetailFields.Children.Add(Field(field.Label + ":",
                WorkspaceVisuals.Text(field.Value, 13, brush: field.RequiresReading ? "MutedInk" : "Ink")));
    }

    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await RestoreSelectedRecoveryAsync(restoreAsCopy: false);
    }

    private async void RestoreCopy_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await RestoreSelectedRecoveryAsync(restoreAsCopy: true);
    }

    private async void RemoveRecovery_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || _loadingRecovery || RecoveryGrid.SelectedItem is not RecoveryRow row ||
            !row.Item.CanRemove || !RecoverySelectionIsCurrent(row)) return;
        if (!AppDialogWindow.Confirm(this, "Remove Recovery copy",
                $"Permanently remove the saved copy of “{row.Path}”?",
                $"Pair: {row.PairName}\nLocation: {row.Location}\n\nThe synced file is not deleted. This Recovery copy cannot be restored after removal. Activity history is kept.",
                "Remove copy", destructive: true)) return;
        if (_runActive || _loadingRecovery || !RecoverySelectionIsCurrent(row)) return;
        _runActive = true;
        _loadingRecovery = true;
        SetRunControls();
        try
        {
            ShowStatus("Removing the selected Recovery copy after exact identity verification…");
            var result = await _authorityHost.RemoveRecoveryCopyAsync(row.Item);
            ShowStatus(result.Detail);
            AddActivity(row.Path, "Recovery copy removed", result.Detail);
        }
        catch (Exception exception) { ShowStatus("The Recovery copy was not removed: " + exception.Message); }
        finally
        {
            _loadingRecovery = false;
            await FinishRunPresentationAsync("Recovery-copy removal",
                () => RefreshRecoveryAsync(includeDeleted: false));
        }
    }

    private async Task RestoreSelectedRecoveryAsync(bool restoreAsCopy)
    {
        if (_runActive || _loadingRecovery)
        {
            RecoverySummary.Text = "Wait for the current sync or recovery action to reach a durable boundary.";
            ShowStatus(RecoverySummary.Text);
            return;
        }
        if (RecoveryGrid.SelectedItem is not RecoveryRow row || !row.Item.CanRestore || !RecoverySelectionIsCurrent(row))
        {
            ShowStatus("Select one recoverable copy or exact cloud revision first.");
            return;
        }
        try
        {
            var generation = _recoveryQueryVersion;
            _runActive = true;
            _loadingRecovery = true;
            SetRunControls();
            var preview = await _authorityHost.PreviewRecoveryAsync(row.Item, restoreAsCopy);
            if (generation != _recoveryQueryVersion || !RecoverySelectionIsCurrent(row))
            {
                ShowStatus("The recovery selection changed. Select the current copy and preview again.");
                return;
            }
            if (!AppDialogWindow.Confirm(this, "Restore from Recovery", preview.Summary,
                    $"Source: {row.Source}\nPath: {preview.SourceRelativePath}\n{(restoreAsCopy ? "Example copy name" : "Target")}: {preview.TargetRelativePath}\nConnection: {row.Connection}\n\n" +
                    (restoreAsCopy ? "A unique copy name is chosen when the restore starts; the final name appears in the result.\n" : string.Empty) +
                    "The Recovery record stays available.",
                    restoreAsCopy ? "Restore as copy" : "Restore original"))
                return;
            if (generation != _recoveryQueryVersion || !RecoverySelectionIsCurrent(row)) return;
            _runActive = true;
            _loadingRecovery = true;
            SetRunControls();
            RunProgress.IsIndeterminate = true;
            ShowStatus(restoreAsCopy ? "Restoring a new copy of " + preview.SourceRelativePath + "…" :
                "Restoring " + preview.SourceRelativePath + " to " + preview.TargetRelativePath + "…");
            var restored = await _authorityHost.RestoreRecoveryAsync(row.Item, restoreAsCopy);
            ShowStatus(restored.Detail + " Synchronization will reconcile the restored version from the retained baseline.");
            AddActivity(restored.TargetRelativePath, "Restored", restored.Detail);
        }
        catch (Exception exception) { ShowStatus("Recovery stopped safely: " + exception.Message); }
        finally
        {
            _loadingRecovery = false;
            await FinishRunPresentationAsync("Recovery restore", async () =>
            {
                await RefreshRecoveryAsync(includeDeleted: false);
                await RefreshAuthorityProjectionsAsync(requireSuccess: true);
            });
        }
    }

    private void RecoveryFilter_Changed(object sender, TextChangedEventArgs e)
    {
        ApplyRecoveryFilter();
        if (!IsLoaded || RecoveryPage is null || RecoveryPage.Visibility != Visibility.Visible) return;
        _recoverySearchTimer.Stop();
        _recoverySearchTimer.Start();
    }

    private void ApplyRecoveryFilter()
    {
        if (RecoveryRows is null || RecoverySearchBox is null) return;
        var selected = RecoveryGrid.SelectedItem as RecoveryRow;
        var query = RecoverySearchBox.Text.Trim();
        RecoveryRows.Clear();
        foreach (var row in _allRecoveryRows.Where(item => query.Length == 0 ||
                     item.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.Location.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.Health.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            if (_recoveryTabs?.SelectedIndex == 0 && row.Item.Source is SyncRecoverySource.DropboxDeleted or SyncRecoverySource.DropboxRevision or SyncRecoverySource.CloudTrash) continue;
            if (_recoveryTabs?.SelectedIndex == 1 && row.Item.Source is SyncRecoverySource.LocalArchive or SyncRecoverySource.DropboxArchive) continue;
            RecoveryRows.Add(row);
        }
        if (selected is not null)
            RecoveryGrid.SelectedItem = RecoveryRows.FirstOrDefault(row => row.Item.PairId == selected.Item.PairId &&
                row.Item.Source == selected.Item.Source && row.Item.Key == selected.Item.Key);
        // UI16: the empty state follows the rows that are actually presented, so the first load of a
        // pair with no retained copy shows the explanation instead of an unexplained blank table.
        if (_recoveryEmptyPanel is not null)
            _recoveryEmptyPanel.Visibility = RecoveryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool RecoverySelectionIsCurrent(RecoveryRow row) =>
        ReferenceEquals(RecoveryGrid.SelectedItem, row) && RecoveryRows.Contains(row) &&
        string.Equals(row.Item.PairId, CurrentRecoveryPairId(), StringComparison.Ordinal);

    private RecoveryRow ToRecoveryRow(SyncRecoveryCatalogItem item) => new(item,
        item.Connection, item.PairName, (item.Source switch
        {
            SyncRecoverySource.LocalArchive => "Local archive",
            SyncRecoverySource.DropboxArchive => RecoveryProviderName(item.PairId) + " Recovery",
            SyncRecoverySource.DropboxRevision => RecoveryProviderName(item.PairId) + " revision",
            SyncRecoverySource.CloudTrash => RecoveryProviderName(item.PairId) + " Trash",
            _ => RecoveryProviderName(item.PairId) + " deleted"
        }) + (item.ObjectKind == "folder" ? " · Folder" : " · File"), item.RelativePath, item.Location,
        item.Size is null ? "—" : FormatByteSize(item.Size.Value),
        item.RetainedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—", item.Retention,
        item.Health);

    private string RecoveryProviderName(string pairId) => SyncProviders.DisplayName(
        _pairSet.Pairs.FirstOrDefault(pair => pair.Id == pairId)?.Provider);

    private static string FormatByteSize(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var amount = (double)value;
        var index = 0;
        while (amount >= 1024 && index < units.Length - 1) { amount /= 1024; index++; }
        return index == 0 ? $"{value:N0} {units[index]}" : $"{amount:N1} {units[index]}";
    }

    private void RewindPair_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRewindState();

    private void RefreshRewindState()
    {
        if (RewindPairBox is null || RewindStatusText is null) return;
        BeginRewindRefresh();
        var pair = RewindPairBox.SelectedItem as PairRow;
        RewindRows.Clear();
        if (pair is null || pair.Provider == SyncProviders.GoogleDrive)
        {
            _selectedRewindSession = null;
            RewindAccountText.Text = pair is null ? "No connected account" : LabelForPairId(pair.Id);
            RewindPathText.Text = "—";
            RewindStatusText.Text = pair is null ? "Choose a pair before preparing Dropbox Rewind." :
                "Dropbox Rewind is not available for Google Drive. Nothing has been paused or changed.";
            PrepareRewindButton.IsEnabled = false;
            DetectRewindButton.IsEnabled = false;
            ApplyRewindButton.IsEnabled = false;
            ResumeRewindButton.IsEnabled = false;
            return;
        }
        RewindAccountText.Text = LabelForPairId(pair.Id);
        RewindPathText.Text = pair.DropboxRoot;
        _rewindFolderUrl = BuildDropboxFolderUrl(pair.DropboxRoot);
        if (!RewindSnapshotIsCurrent)
        {
            _selectedRewindSession = null;
            RewindStatusText.Text = _rewindRefreshFailure ?? "Reading the retained Rewind checkpoint…";
            PrepareRewindButton.IsEnabled = false;
            DetectRewindButton.IsEnabled = false;
            ApplyRewindButton.IsEnabled = false;
            ResumeRewindButton.IsEnabled = false;
            return;
        }
        _selectedRewindSession = RewindSessionsSnapshot.FirstOrDefault(item =>
            string.Equals(item.PairId, pair.Id, StringComparison.Ordinal) && item.Status != SyncRewindStatus.Resumed);
        if (_selectedRewindSession is null)
        {
            RewindStatusText.Text = "No active checkpoint. Preparing one inventories both sides and pauses only this pair before Dropbox opens.";
            PrepareRewindButton.IsEnabled = true;
            DetectRewindButton.IsEnabled = false;
            ApplyRewindButton.IsEnabled = false;
            ResumeRewindButton.IsEnabled = false;
            return;
        }
        var progress = (_selectedRewindSession.PathProgress ?? [])
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in _selectedRewindSession.Preview ?? [])
            RewindRows.Add(new RewindRow(item.RelativePath,
                progress.TryGetValue(item.RelativePath, out var state) ? state.Status.ToString() : "Pending",
                item.Group, item.ProposedEffect,
                item.LocalChanged ? "Yes — preserve as conflict" : "No",
                progress.TryGetValue(item.RelativePath, out state) && !string.IsNullOrWhiteSpace(state.Detail)
                    ? state.Detail
                    : item.Reason));
        RewindStatusText.Text = $"{_selectedRewindSession.Status}: {_selectedRewindSession.Detail}";
        PrepareRewindButton.IsEnabled = false;
        var hasCompletedEffect = (_selectedRewindSession.PathProgress ?? []).Any(item =>
            item.OperationId is not null && item.Status == SyncRewindPathStatus.Completed);
        DetectRewindButton.IsEnabled = _selectedRewindSession.Status is SyncRewindStatus.AwaitingDropbox or
            SyncRewindStatus.PreviewReady or SyncRewindStatus.Blocked ||
            _selectedRewindSession.Status == SyncRewindStatus.ApplyBlocked && !hasCompletedEffect;
        ApplyRewindButton.IsEnabled = _selectedRewindSession.Status is SyncRewindStatus.PreviewReady or
            SyncRewindStatus.Applying or SyncRewindStatus.PartiallyApplied or SyncRewindStatus.ApplyStopped or
            SyncRewindStatus.EffectUnknown or SyncRewindStatus.ApplyBlocked;
        ResumeRewindButton.IsEnabled = _selectedRewindSession.Status is SyncRewindStatus.AppliedPaused or
            SyncRewindStatus.AwaitingDropbox or SyncRewindStatus.PreviewReady or SyncRewindStatus.Blocked;
    }

    private async void PrepareRewind_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || RewindPairBox.SelectedItem is not PairRow pair)
        {
            ShowStatus("Choose a pair and wait for the current sync to finish before preparing Rewind.");
            return;
        }
        if (!AppDialogWindow.Confirm(this, "Prepare Dropbox Rewind",
                $"Checkpoint and pause “{pair.Name}”?",
                $"Sync Shark inventories both sides, creates a checkpoint, pauses this pair and opens {pair.DropboxRoot} on dropbox.com.\n\nExpected account: {LabelForPairId(pair.Id)}\nNo Dropbox content changes are made by this step.",
                "Checkpoint and open Dropbox"))
            return;
        try
        {
            _runActive = true;
            SetRunControls();
            RunProgress.IsIndeterminate = true;
            RewindStatusText.Text = "Creating complete local and Dropbox checkpoints…";
            var handoff = await _authorityHost.PrepareRewindAsync(pair.Id);
            _selectedRewindSession = handoff.Session;
            _rewindFolderUrl = handoff.DropboxFolderUrl;
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
            Process.Start(new ProcessStartInfo(handoff.DropboxFolderUrl.AbsoluteUri) { UseShellExecute = true });
            RewindStatusText.Text = handoff.Instruction + " Expected account: " + handoff.ExpectedConnection +
                                    ". Return here only after Dropbox reports completion.";
            ShowStatus("Rewind checkpoint is durable and the pair is paused. Dropbox opened at the expected folder.");
        }
        catch (Exception exception) { ShowStatus("Rewind checkpoint stopped safely: " + exception.Message); }
        finally
        {
            _runActive = false;
            RunProgress.IsIndeterminate = false;
            SetRunControls();
            RefreshRewindState();
        }
    }

    private void OpenDropboxRewind_Click(object sender, RoutedEventArgs e)
    {
        if (_rewindFolderUrl is null)
        {
            ShowStatus("Choose a pair first.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_rewindFolderUrl.AbsoluteUri) { UseShellExecute = true });
            ShowStatus("Opened the expected Dropbox folder. Choose Folder settings → Rewind this folder; Dropbox may limit Rewind by account plan.");
        }
        catch (Exception exception) { ShowStatus("Dropbox could not be opened: " + exception.Message); }
    }

    private async void DetectRewind_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || RewindPairBox.SelectedItem is not PairRow pair) return;
        try
        {
            _runActive = true;
            SetRunControls();
            RunProgress.IsIndeterminate = true;
            RewindStatusText.Text = "Inventorying the completed Dropbox rollback and post-checkpoint local work…";
            _selectedRewindSession = await _authorityHost.PreviewRewindImportAsync(pair.Id);
            RefreshRewindState();
            ShowStatus($"Rewind preview contains {_selectedRewindSession.Preview?.Count ?? 0:N0} changed Dropbox path(s); no local effect has run.");
        }
        catch (Exception exception) { ShowStatus("Rewind preview stopped safely: " + exception.Message); }
        finally
        {
            _runActive = false;
            RunProgress.IsIndeterminate = false;
            SetRunControls();
            RefreshRewindState();
        }
    }

    private async void ApplyRewind_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        var reviewedSession = _selectedRewindSession;
        if (_runActive || RewindPairBox.SelectedItem is not PairRow pair ||
            reviewedSession?.Status is not (SyncRewindStatus.PreviewReady or SyncRewindStatus.Applying or
                SyncRewindStatus.PartiallyApplied or SyncRewindStatus.ApplyStopped or
                SyncRewindStatus.EffectUnknown or SyncRewindStatus.ApplyBlocked))
            return;
        if (reviewedSession.PairId != pair.Id || reviewedSession.Preview is null ||
            string.IsNullOrWhiteSpace(reviewedSession.LocalPreviewScanId) ||
            string.IsNullOrWhiteSpace(reviewedSession.DropboxPreviewScanId))
        {
            ShowStatus("Detect and review this pair's Rewind preview before applying it.");
            return;
        }
        var confirmation = SyncRewindApplyConfirmation.FromSession(reviewedSession);
        var preview = reviewedSession.Preview;
        var conflicts = preview.Count(item => item.LocalChanged);
        var verb = reviewedSession.Status != SyncRewindStatus.PreviewReady
            ? "Resume the interrupted"
            : "Apply the exact";
        if (!AppDialogWindow.Confirm(this, "Apply Dropbox Rewind",
                $"{verb} reviewed rollback to “{pair.Name}”?",
                $"Dropbox rollback paths: {preview.Count:N0}\nLocal changes made after the checkpoint that need conflict handling: {conflicts:N0}\n\nThe pair remains paused until you resume it.",
                "Apply reviewed rollback", destructive: true))
            return;
        try
        {
            _runActive = true;
            SetRunControls();
            RunProgress.IsIndeterminate = true;
            var applied = await _authorityHost.ApplyRewindImportAsync(pair.Id, confirmation);
            _selectedRewindSession = applied.Session;
            ShowResult(_pairSet.Pairs.Single(item => item.Id == pair.Id), applied.Result);
            ShowStatus(applied.Session.Detail ?? "Rewind batch applied; pair remains paused.");
        }
        catch (Exception exception) { ShowStatus("Rewind apply stopped safely: " + exception.Message); }
        finally
        {
            await FinishRunPresentationAsync("Rewind apply",
                () => RefreshAuthorityProjectionsAsync(requireSuccess: true));
            RefreshRewindState();
        }
    }

    private async void ResumeRewind_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || RewindPairBox.SelectedItem is not PairRow pair) return;
        try
        {
            await _authorityHost.ResumeAfterRewindAsync(pair.Id);
            _selectedRewindSession = null;
            RefreshRewindState();
            await ConfigureLocalMonitorsAsync(_authorityHost.LoadAutomation());
            ShowStatus("Rewind checkpoint closed. Ordinary synchronization resumed for " + pair.Name + ".");
        }
        catch (Exception exception) { ShowStatus("Rewind could not resume: " + exception.Message); }
    }

    private static Uri BuildDropboxFolderUrl(string dropboxRoot)
    {
        var root = PilotPath.NormalizeDropboxRoot(dropboxRoot);
        var encoded = string.Join('/', root.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return new Uri("https://www.dropbox.com/home" + (encoded.Length == 0 ? string.Empty : "/" + encoded));
    }

    private static string DropboxFolderDisplayName(string dropboxRoot, string providerName = "Dropbox")
    {
        if (string.IsNullOrWhiteSpace(dropboxRoot)) return providerName;
        var root = PilotPath.NormalizeDropboxRoot(dropboxRoot);
        return root == "/" ? providerName : root.Split('/').Last();
    }

    private static string LocalFolderDisplayName(string localRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot)) return "Selected folder";
        try
        {
            var trimmed = localRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? localRoot : name;
        }
        catch (ArgumentException)
        {
            return localRoot;
        }
    }

    private void LoadAutomation()
    {
        try
        {
            var settings = _authorityHost.LoadAutomation();
            if (settings.StartWithWindows) WindowsStartupRegistration.RebindEnabledInstalledCommand();
            AutomationEnabledBox.IsChecked = settings.Enabled;
            StartWithWindowsBox.IsChecked = settings.StartWithWindows &&
                                            WindowsStartupRegistration.IsEnabledForCurrentExecutable();
            AutomationIntervalBox.Text = settings.IntervalMinutes.ToString();
            AutomationStatus.Text = DescribeAutomation(settings);
            ConfigureAutomationTimer(settings);
        }
        catch (Exception exception) { ShowStatus("Automation settings could not be loaded: " + exception.Message); }
    }

    private static string DescribeAutomation(SyncAutomationSettings settings) => settings.Enabled
        ? $"Live sync is on for enabled pairs. A full safety check runs every {settings.IntervalMinutes} minutes for changes that were not reported promptly. Closing the window leaves it running in the tray."
        : "Live sync is off. Sync now remains available. Closing the window still leaves Sync Shark in the tray.";

    private void LoadTransferSettings()
    {
        try
        {
            var settings = _authorityHost.LoadTransferSettings();
            ConcurrentPairsBox.Text = settings.MaximumConcurrentPairs.ToString();
            UploadLimitBox.Text = settings.UploadKiBPerSecond.ToString();
            DownloadLimitBox.Text = settings.DownloadKiBPerSecond.ToString();
            if (_settingsResourceMode is not null) _settingsResourceMode.SelectedItem = settings.ResourceMode;
            if (_settingsConcurrentFiles is not null) _settingsConcurrentFiles.Text = settings.MaximumConcurrentFiles.ToString();
            TransferSettingsStatus.Text = DescribeTransferSettings(settings);
        }
        catch (Exception exception)
        {
            ShowStatus("Transfer controls could not be loaded: " + exception.Message);
        }
    }

    private async void SaveTransferSettings_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        try
        {
            if (!int.TryParse(ConcurrentPairsBox.Text, out var concurrentPairs) ||
                !int.TryParse(UploadLimitBox.Text, out var uploadLimit) ||
                !int.TryParse(DownloadLimitBox.Text, out var downloadLimit))
                throw new InvalidDataException("Transfer controls must be whole numbers.");
            var prior = _authorityHost.LoadTransferSettings();
            var settings = SyncTransferSettings.Normalize(prior with { MaximumConcurrentPairs = concurrentPairs,
                UploadKiBPerSecond = uploadLimit, DownloadKiBPerSecond = downloadLimit });
            await _authorityHost.SaveTransferSettingsAsync(settings);
            TransferSettingsStatus.Text = DescribeTransferSettings(settings);
        }
        catch (Exception exception)
        {
            TransferSettingsStatus.Text = "Transfer controls were not saved: " + exception.Message;
        }
    }

    private static string DescribeTransferSettings(SyncTransferSettings settings)
    {
        var upload = settings.UploadKiBPerSecond == 0 ? "unlimited upload" :
            $"{settings.UploadKiBPerSecond:N0} KiB/s upload";
        var download = settings.DownloadKiBPerSecond == 0 ? "unlimited download" :
            $"{settings.DownloadKiBPerSecond:N0} KiB/s download";
        return $"{settings.ResourceMode}; up to {settings.MaximumConcurrentPairs} pair(s); " +
            $"file ceiling {(settings.MaximumConcurrentFiles == 0 ? "automatic" : settings.MaximumConcurrentFiles.ToString())}; {upload}; {download}.";
    }

    private async void SaveAutomation_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        try
        {
            if (!int.TryParse(AutomationIntervalBox.Text, out var interval))
                throw new InvalidDataException("The safety-poll interval must be a whole number of minutes.");
            var prior = _authorityHost.LoadAutomation();
            var priorStartup = WindowsStartupRegistration.IsEnabledForCurrentExecutable();
            var settings = new SyncAutomationSettings(AutomationEnabledBox.IsChecked == true, interval,
                RunOnStartup: false, StartWithWindows: StartWithWindowsBox.IsChecked == true);
            try
            {
                WindowsStartupRegistration.SetEnabled(settings.StartWithWindows);
                await _authorityHost.SaveAutomationAsync(settings);
                foreach (var row in PairRows) row.UpdateGlobalLiveSync(settings.Enabled);
            }
            catch
            {
                WindowsStartupRegistration.SetEnabled(priorStartup);
                await _authorityHost.SaveAutomationAsync(prior);
                throw;
            }
            ConfigureAutomationTimer(settings);
            await ConfigureLocalMonitorsAsync(settings);
            AutomationStatus.Text = DescribeAutomation(settings);
        }
        catch (Exception exception) { AutomationStatus.Text = "Automation settings were not saved: " + exception.Message; }
    }

    private void ConfigureAutomationTimer(SyncAutomationSettings settings)
    {
        _automationTimer.Stop();
        _automationTimer.Interval = TimeSpan.FromMinutes(settings.IntervalMinutes);
        // Under core v2 continuous sync owns the safety poll, so the window's own timer is never started.
        if (settings.Enabled && HostIsCoreV1) _automationTimer.Start();
    }

    private async Task ConfigureLocalMonitorsAsync(SyncAutomationSettings settings)
    {
        await _monitorConfigurationGate.WaitAsync();
        try
        {
            var generation = Interlocked.Increment(ref _monitorConfigurationGeneration);
            _monitorStateByPair.Clear();
            _monitorAttentionReasonByPair.Clear();
            if (!settings.Enabled || _closing)
            {
                // Monitors are genuinely stopped here, so no automatic run continues: drop the automatic-running set.
                _autoRunningPairs.Clear();
                if (MonitorStopperForTest is null) await _authorityHost.StopMonitorsAsync();
                else await MonitorStopperForTest();
                RefreshPairRows(_pairSet.SelectedPairId);
                return;
            }

            // Connection readiness belongs to each endpoint's preparation below. A missing cloud
            // account must not prevent an unrelated local/local pair from being monitored.
            _pairSet = await _authorityHost.LoadPairsAsync();
            // ENG5: the same shared rule the polls use decides which pairs may hold a live monitor. A pair
            // awaiting its first-sync review is reported honestly instead of being watched automatically, and
            // the host stops every monitor it is not asked to keep.
            var monitored = _pairSet.Pairs.Where(item => item.Enabled && !AwaitingFirstSyncReview(item)).ToArray();
            // An active reconfiguration keeps continuous sync running (Start is idempotent) and its loops do not
            // re-emit Running, so only pairs that are gone or disabled leave the automatic-running set; a pair in a
            // long automatic run keeps the tray working until its own next state change.
            var monitoredIds = monitored.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal);
            _autoRunningPairs.RemoveWhere(id => !monitoredIds.Contains(id));
            foreach (var pair in monitored)
                _monitorStateByPair[pair.Id] = "Starting";
            foreach (var pair in _pairSet.Pairs.Where(item => item.Enabled && AwaitingFirstSyncReview(item)))
                _monitorStateByPair[pair.Id] = "Waiting for first-sync review";
            RefreshPairRows(_pairSet.SelectedPairId);
            var results = ScopedMonitorConfiguratorForTest is { } scopedConfigurator
                ? await scopedConfigurator(monitored, pairIds => OnMonitorRunRequestedAsync(generation, pairIds),
                    pairId => OnMonitorHealthChangedAsync(pairId, generation))
                : MonitorConfiguratorForTest is null
                ? await _authorityHost.ConfigureMonitorsAsync(monitored,
                    pairIds => OnMonitorRunRequestedAsync(generation, pairIds),
                    pairId => OnMonitorHealthChangedAsync(pairId, generation))
                : await MonitorConfiguratorForTest(monitored,
                    () => OnMonitorRunRequestedAsync(generation),
                    pairId => OnMonitorHealthChangedAsync(pairId, generation));
            if (generation != Volatile.Read(ref _monitorConfigurationGeneration)) return;
            PresentMonitorResults(results);
        }
        finally
        {
            _monitorConfigurationGate.Release();
        }
    }

    private void PresentMonitorResults(IReadOnlyList<SyncAuthorityMonitorStartResult> results)
    {
        foreach (var result in results)
        {
            _monitorStateByPair[result.PairId] = result.State;
            if (result.FailureMessage is not null)
            {
                var pair = _pairSet.Pairs.Single(item => item.Id == result.PairId);
                // Preserve the actual startup cause. A capability or local watcher error is not
                // evidence of a provider outage, even if a compatibility class is named Dropbox.
                _monitorAttentionReasonByPair[result.PairId] = "Automatic change monitoring could not start: " +
                    result.FailureMessage;
                AddActivity(pair.Name, "Monitor blocked",
                    (result.FailureType ?? "Failure") + ": " + result.FailureMessage, pair);
            }
            else _monitorAttentionReasonByPair.Remove(result.PairId);
        }
        RefreshPairRows(_pairSet.SelectedPairId);
    }

    private Task<bool> OnMonitorRunRequestedAsync() =>
        OnMonitorRunRequestedAsync(Volatile.Read(ref _monitorConfigurationGeneration));

    private Task<bool> OnMonitorRunRequestedAsync(int generation) =>
        OnMonitorRunRequestedAsync(generation, _pairSet.Pairs.Select(pair => pair.Id).ToHashSet(StringComparer.Ordinal));

    // Instance-only INPUT seam: the fixture observes the callback constructed by
    // this window's real monitor configuration, never supplies admission results.
    internal Func<IReadOnlyList<SyncPair>, Func<IReadOnlySet<string>, Task<bool>>, Func<string, Task>,
        Task<IReadOnlyList<SyncAuthorityMonitorStartResult>>>? ScopedMonitorConfiguratorForTest { get; set; }

    private async Task<bool> OnMonitorRunRequestedAsync(int generation, IReadOnlySet<string> pairIds)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => OnMonitorRunRequestedAsync(generation, pairIds)).Task.Unwrap();
        if (_closing || generation != Volatile.Read(ref _monitorConfigurationGeneration) ||
            !_authorityHost.LoadAutomation().Enabled) return true;
        return await RunScheduledAsync("Monitor", pairIds);
    }

    private Task OnMonitorHealthChangedCurrentAsync(string pairId) =>
        OnMonitorHealthChangedAsync(pairId, Volatile.Read(ref _monitorConfigurationGeneration));

    private async Task OnMonitorHealthChangedAsync(string pairId, int generation)
    {
        if (generation != Volatile.Read(ref _monitorConfigurationGeneration) || _closing ||
            !_authorityHost.LoadAutomation().Enabled) return;
        var projections = MonitorProjectionLoaderForTest is null
            ? await _authorityHost.LoadMonitorProjectionAsync()
            : await MonitorProjectionLoaderForTest(pairId);
        var monitors = projections
            .Where(item => item.PairId == pairId).ToArray();
        var state = monitors.Any(item => item.State == "Monitor unavailable")
            ? "Monitor unavailable"
            : monitors.Any(item => item.State == "Full reconciliation required")
                ? "Full reconciliation required"
                : "Monitoring";
        var pending = monitors.Sum(item => item.PendingDirtyItems);
        if (pending > 0) state += $" · {pending} pending";
        await Dispatcher.InvokeAsync(() =>
        {
            if (generation != Volatile.Read(ref _monitorConfigurationGeneration) || _closing ||
                !_authorityHost.LoadAutomation().Enabled) return;
            _monitorStateByPair[pairId] = state;
            if (string.Equals(state, "Monitoring", StringComparison.Ordinal))
                _monitorAttentionReasonByPair.Remove(pairId);
            else if (state.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                _monitorAttentionReasonByPair[pairId] =
                    "Live monitoring is temporarily unavailable. Sync Shark will retry automatically; use Sync now to check this pair immediately.";
            RefreshPairRows(_pairSet.SelectedPairId);
        });
    }

    // UI2: the one narrow presentation reset every operation start shares - an ordinary run, startup recovery
    // and first sync. Beside the per-run result rows it replaces the run-specific phase start, progress
    // instant, running pair/id and detail with THIS operation's own start, so a new run can never inherit an
    // old phase or progress time. It fabricates no progress: the caller owns RunProgress's indeterminate state
    // and the value moves only when a real report arrives.
    private void ResetRunPresentation(string stage, string phaseDetail)
    {
        _workspaceCommandFailure = null;
        _workspaceReadbackFailure = null;
        var started = DateTimeOffset.Now;
        _workspaceRunStarted = started;
        _workspacePhaseStarted = started;
        _workspaceProgressUpdated = started;
        _lastReportedWorkspaceStage = stage;
        _workspaceStage = stage;
        _workspacePhaseDetail = phaseDetail;
        _workspaceRunningPair = null;
        _workspaceRunningPairId = null;
        _workspacePaused = false;
        _allRows.Clear();
        VisibleRows.Clear();
        ConflictRows.Clear();
        UploadedCount.Text = "0";
        DownloadedCount.Text = "0";
        IdenticalCount.Text = "0";
        ConflictCount.Text = "0";
        BlockedCount.Text = "0";
    }

    private void ShowResult(SyncPair pair, SyncRunResult result)
    {
        _workspaceRuns.Insert(0, new WorkspaceRun(pair, result, DateTimeOffset.Now));
        foreach (var decision in result.Decisions)
        {
            var row = new DecisionRow(pair.Id, pair.Name, decision.RelativePath, decision.Kind.ToString(),
                decision.Reason, LabelForPair(pair));
            _allRows.Add(row);
            if (decision.Kind is SyncDecisionKind.Conflict or SyncDecisionKind.Blocked)
            {
                _allConflictRows.Add(row);
                ApplyConflictFilter();
            }
            AddActivity(row.Path, row.Outcome, row.Reason, pair);
        }
        ApplyFilter();
        EmptyResultsSurface.Visibility = _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UploadedCount.Text = _allRows.Count(row => row.Outcome == SyncDecisionKind.Uploaded.ToString()).ToString();
        DownloadedCount.Text = _allRows.Count(row => row.Outcome == SyncDecisionKind.Downloaded.ToString()).ToString();
        IdenticalCount.Text = _allRows.Count(row => row.Outcome == SyncDecisionKind.Identical.ToString()).ToString();
        ConflictCount.Text = _allRows.Count(row => row.Outcome == SyncDecisionKind.Conflict.ToString()).ToString();
        BlockedCount.Text = _allRows.Count(row => row.Outcome == SyncDecisionKind.Blocked.ToString()).ToString();
        var summary = $"{pair.Name}: {result.Uploaded} uploaded, {result.Downloaded} downloaded, {result.Identical} identical, {result.Conflicts} conflicts, {result.Recovered} recovered, {result.OnlineOnly} cloud-only items visible without content download, {result.Blocked} blocked.";
        SummaryText.Text = summary + " One-sided updates require the retained baseline; two-sided changes remain preserved as conflicts.";
        ShowStatus(summary);
    }

    private void AddActivity(string path, string outcome, string reason, SyncPair? sourcePair = null)
    {
        var pairId = sourcePair?.Id ?? _pairSet.SelectedPairId;
        var pairName = sourcePair?.Name ?? _pairSet.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, pairId, StringComparison.Ordinal))?.Name ?? "App";
        var row = new ActivityRow(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), path,
            outcome, reason, LabelForPairId(pairId), pairId, pairName, null, ActivityItemKindFromPath(path),
            ActivityFileTypeFromPath(path), ConnectionId: ExactActivityConnectionId(sourcePair?.ConnectionId, pairId));
        // ShowResult repeats completed live operations. Retain one visible transfer, not a
        // second guessed file-kind row without its operation/account identity.
        if (_workspaceRunStarted is { } started && ActivityRows.Concat(_homeHistory).Any(item =>
                item.OperationId is not null && item.PairId == row.PairId && item.Path == row.Path &&
                item.HumanAction == row.HumanAction && DateTimeOffset.TryParse(item.When, out var occurred) &&
                occurred.ToUnixTimeSeconds() >= started.ToUnixTimeSeconds())) return;
        if (ActivityRowMatchesCurrentFilter(row)) ActivityRows.Insert(0, row);
        TrackWorkspaceAction(row);
    }

    private static string ActivityItemKindFromPath(string path) =>
        string.IsNullOrWhiteSpace(path) || path.StartsWith('<') ? "System" : "Unknown";

    private static string ActivityFileTypeFromPath(string path)
    {
        var kind = ActivityItemKindFromPath(path);
        return kind;
    }

    internal Task RefreshBootstrapReviewForTestAsync(bool reset = true) => RefreshBootstrapReviewAsync(reset);
    internal Task LoadActivityPageForTestAsync(bool reset = true) => LoadActivityPageAsync(reset);
    internal Task LoadQueuePageForTestAsync(bool reset = true) => LoadQueuePageAsync(reset);
    internal Task RefreshMonitorHealthForTestAsync(string pairId) => OnMonitorHealthChangedCurrentAsync(pairId);
    internal Task ConfigureLocalMonitorsForTestAsync(SyncAutomationSettings settings) =>
        ConfigureLocalMonitorsAsync(settings);

    private void ShowRunStatus(string value)
    {
        var advanced = _lastReportedWorkspaceStage != _workspaceStage ||
            !string.Equals(_workspacePhaseDetail, value, StringComparison.Ordinal);
        if (_lastReportedWorkspaceStage != _workspaceStage)
        { _workspacePhaseStarted = DateTimeOffset.UtcNow; _lastReportedWorkspaceStage = _workspaceStage; }
        if (advanced) _workspaceProgressUpdated = DateTimeOffset.UtcNow;
        _workspacePhaseDetail = value;
        if (_workspaceRunningPairId == BootstrapPairBox.SelectedValue as string && UiOperationBusy)
            BootstrapChoiceSummary.Text = value;
        ShowStatus(value);
    }

    private (string Text, string? AccountId, DateTimeOffset UntilUtc)? _workspaceActionFeedback;

    private void ShowActionStatus(string value)
    {
        // Only an explicit control result may temporarily replace the phase text.
        // Navigation and background status messages must never become action feedback.
        _workspaceActionFeedback = (value, _workspaceAccountId, DateTimeOffset.UtcNow.AddSeconds(8));
        ShowStatus(value);
    }

    private long _lastStatusPresentationTick;
    internal async Task ReportActionExceptionAsync(string action, string message)
    {
        ShowStatus(message);
        if (!_authorityHost.AuthorityStateExists) return;
        try
        {
            await _authorityHost.AppendDiagnosticActivityAsync(new SyncActivityEvent(
                "", DateTimeOffset.UtcNow, [SyncActivityCategories.Diagnostics],
                "Error", "Application", action + " did not complete", message,
                RelatedRunId: _authorityHost.ActiveRun?.Id));
        }
        catch (Exception loggingError)
        {
            ShowStatus(message + " The diagnostic could not be saved: " + SyncActivityText.Failure(loggingError));
        }
    }

    private void ShowStatus(string value)
        => ReportStatus(value, presentOperation: true);

    private void ReportStatus(string value, bool presentOperation)
    {
        ActionFeedback.Report(value);
        if (presentOperation && _operationFeedback is not null)
        {
            _operationFeedback.Text = value;
            _operationFeedback.ToolTip = value;
            _operationFeedback.Visibility = Visibility.Visible;
        }
        // Pair browsing and editor validation have their own local status surfaces.
        // Do not let those background messages replace the dashboard's run state.
        if (presentOperation && (_runActive || DashboardPage.Visibility == Visibility.Visible))
        {
            PhaseText.Text = value;
        }
        if (ConsoleText is null) return;
        ConsoleText.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {value}{Environment.NewLine}");
        if (ConsoleText.Text.Length > 131072) ConsoleText.Text = ConsoleText.Text[^65536..];
        ConsoleText.ScrollToEnd();
        // Every observation reaches the live text and retained event log. Rebuilding the entire
        // workspace for every file would itself make large scans appear frozen.
        var now = Environment.TickCount64;
        if (_workspaceReady && (!_runActive || now - _lastStatusPresentationTick >= 100))
        { _lastStatusPresentationTick = now; RefreshWorkspace(); }
    }

    private void ShowPairEditorStatus(string value, bool isError = false)
    {
        if (PairEditorStatus is null || PairEditorStatusText is null) return;
        PairEditorStatusText.Text = value;
        PairEditorStatusText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "ErrorBrush" : "MutedInk");
        PairEditorStatus.BorderBrush = (System.Windows.Media.Brush)FindResource(isError ? "ErrorBrush" : "Border");
        PairEditorStatus.Visibility = Visibility.Visible;
    }

    private void HidePairEditorStatus()
    {
        if (PairEditorStatus is not null) PairEditorStatus.Visibility = Visibility.Collapsed;
    }

    // UI1: the ONE availability decision for the First Sync Start control. The synchronous single-flight guard
    // and an active run both keep Start disabled, so a refresh recomputes through this same predicate and
    // cannot re-enable Start while the guard is held.
    private bool FirstSyncStartAvailable(string pairId) =>
        !_runActive && !_firstSyncApprovalInFlight && !_loadingBootstrap && ScopeMatches(pairId) &&
        !IsPairKnownUnhealthy(pairId) && _firstSyncReviewContext is { } review &&
        string.Equals(review.PairId, pairId, StringComparison.Ordinal) &&
        _pairSet.Pairs.FirstOrDefault(pair => string.Equals(pair.Id, pairId, StringComparison.Ordinal))
            ?.InitialReviewApproved == false;

    // UI1: the ONE shared busy/admission predicate for competing GUI sync and scan entry points. The first-sync
    // approval holds its single-flight guard before its run is active, so _runActive alone would still admit a
    // normal sync or scan while a reviewed plan is loading. The approval handler itself checks its own guard
    // first, so holding the guard never rejects its own work.
    private bool UiOperationBusy => _runActive || _firstSyncApprovalInFlight;

    private void AddLiveActivity(ActivityRow row)
    {
        _liveActivityRows.Add(row);
        if (_liveActivityRows.Count > 500) _liveActivityRows.RemoveAt(0);
        if (!ActivityRowMatchesCurrentFilter(row)) return;
        if (ActivityReadsOldestFirst()) ActivityRows.Add(row); else ActivityRows.Insert(0, row);
        // The viewport is bounded; the complete operation history remains in the retained log.
        while (ActivityRows.Count > 500)
            ActivityRows.RemoveAt(ActivityReadsOldestFirst() ? 0 : ActivityRows.Count - 1);
        ActivityPageSummary.Text = $"Showing {ActivityRows.Count:N0} matching events. " + (_activityRetentionWindow?.Describe() ?? "Open Activity to read the retained history window.");
        if (_liveActivityRefreshQueued) return;
        _liveActivityRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _liveActivityRefreshQueued = false;
            if (!_closing) RefreshWorkspaceFeeds();
        }));
    }

    private static bool SameLiveObservation(ActivityRow retained, ActivityRow live) =>
        retained.PairId == live.PairId && retained.RunId == live.RunId && retained.Path == live.Path &&
        retained.Reason == live.Reason && retained.OperationId == live.OperationId && retained.Phase == live.Phase &&
        (retained.OperationKind is not null && retained.OperationKind == live.OperationKind &&
            retained.Phase is not null || retained.Outcome == live.Outcome) &&
        DateTimeOffset.TryParse(retained.When, out var recorded) && DateTimeOffset.TryParse(live.When, out var observed) &&
        Math.Abs((recorded - observed).TotalSeconds) < 2;

    private string FirstSyncUnavailableReason => _firstSyncApprovalInFlight
        ? "Your choices are being checked or applied. Wait for this operation to finish."
        : _runActive ? "A scan or sync is running. Wait for it to finish before confirming the plan."
        : _loadingBootstrap ? "The saved first-sync plan is still loading."
        : BootstrapPairBox.SelectedValue is not string id ? "Select a folder pair to review."
        : !ScopeMatches(id) ? "Select this pair's account, or All Accounts."
        : PairHealthBlockingReason(id) is { } reason ? "Repair this folder pair first: " + reason
        : _pairSet.Pairs.FirstOrDefault(pair => pair.Id == id)?.InitialReviewApproved == true
            ? "First sync is already approved. Use Sync now for this pair."
        : _firstSyncReviewContext is null ? "A complete, current review is not available. Use Scan Again to prepare it."
        : "Confirm the reviewed choices and start syncing this folder pair.";

    // Pair-list rebinding suppresses SelectionChanged. Refresh from the final selection, not
    // the temporarily empty selector, on both navigation and retained-plan completion.
    private void UpdateFirstSyncActionAvailability()
    {
        RefreshBootstrapButton.IsEnabled = !UiOperationBusy && BootstrapPairBox.SelectedValue is string;
        RefreshBootstrapButton.ToolTip = UiOperationBusy ? "A scan or sync is already running. The live operation is shown above."
            : BootstrapPairBox.SelectedValue is not string ? "Select a folder pair before scanning."
            : "Read both selected folders again and update the review without copying, replacing or deleting files.";
        ApproveBootstrapButton.ToolTip = FirstSyncUnavailableReason;
        ApproveBootstrapButton.IsEnabled = BootstrapPairBox.SelectedValue is string id && FirstSyncStartAvailable(id);
        System.Windows.Automation.AutomationProperties.SetHelpText(RefreshBootstrapButton, RefreshBootstrapButton.ToolTip as string ?? "");
        System.Windows.Automation.AutomationProperties.SetHelpText(ApproveBootstrapButton, ApproveBootstrapButton.ToolTip as string ?? "");
    }

    private void SetRunControls()
    {
        RunButton.IsEnabled = !UiOperationBusy;
        RunButton.ToolTip = UiOperationBusy ? "A scan or sync is already running. View its current activity or wait for it to finish." : "Check and sync the selected account's enabled folder pairs.";
        UpdateFirstSyncActionAvailability();
        PauseButton.IsEnabled = _runActive && !_readingRunOutcomes;
        StopButton.IsEnabled = _runActive && !_readingRunOutcomes;
        ActionAvailability.Refresh(PauseButton);
        ActionAvailability.Refresh(StopButton);
        // UI1: a refresh that recomputes the controls applies that same rule, so a held guard or an active run
        // cannot be undone by a later enablement pass.
        // Keep browsing, search, expansion, Activity and Sync status responsive
        // during a run. Only controls that can change sync authority are locked.
        PairNameBox.IsReadOnly = _runActive;
        PairEnabledBox.IsEnabled = !_runActive;
        PairDeletionBox.IsEnabled = !_runActive;
        PairStorageBox.IsEnabled = !_runActive;
        PairPriorityChoice.IsEnabled = !_runActive;
        RecoveryRetentionBox.IsEnabled = !_runActive;
        BrowseRecoveryButton.IsEnabled = !_runActive;
        WholeRootSelectionBox.IsEnabled = !_runActive;
        AddPairButton.IsEnabled = !_runActive;
        SyncAllNowButton.IsEnabled = !UiOperationBusy;
        PairDuplicateMenuItem.IsEnabled = !_runActive;
        PairDeleteMenuItem.IsEnabled = !_runActive;
        PairSyncNowMenuItem.IsEnabled = !UiOperationBusy;
        ConnectionConnectButton.IsEnabled = !_runActive && !AuthorizationAttemptWaiting && _connectionStatus?.Binding is null &&
                                            DropboxProviderConfiguration.IsLiveProviderBound;
        ConnectionReauthorizeButton.IsEnabled = !_runActive && !AuthorizationAttemptWaiting && _connectionStatus?.Binding is not null;
        ConnectionDisconnectButton.IsEnabled = !_runActive && !AuthorizationAttemptWaiting &&
                                               _connectionStatus?.Binding is not null && _connectionStatus.HasProtectedCredential;
        if (SavePairButton is not null)
        {
            UpdatePairSaveAvailability();
        }
        TransferSettingsPanel.IsEnabled = !_runActive;
        KeepLocalConflictButton.IsEnabled = !_runActive;
        KeepDropboxConflictButton.IsEnabled = !_runActive;
        PreserveBothConflictButton.IsEnabled = !_runActive;
        SelectAllMatchingConflictsButton.IsEnabled = !_runActive && !_capturingConflictSelection;
        ActionAvailability.Refresh(SelectAllMatchingConflictsButton);
        foreach (var row in PairRows) row.SetCanChangeLiveSync(!_runActive);
        if (!_runActive) PauseButton.Content = "Pause sync";
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = FilterBox.Text.Trim();
        VisibleRows.Clear();
        foreach (var row in _allRows.Where(row => query.Length == 0 || row.PairName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                  row.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                                  row.Outcome.Contains(query, StringComparison.OrdinalIgnoreCase)))
            VisibleRows.Add(row);
        if (EmptyResultsSurface is not null)
            EmptyResultsSurface.Visibility = VisibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DashboardPage is null || Navigation.SelectedItem is not ListBoxItem item) return;
        NavigateTo(item.Tag?.ToString() ?? "Dashboard", updateSelection: false);
    }

    private string ConnectionLabel => _connectionStatus?.Binding is { } binding
        ? $"{binding.ProviderName}: {(string.IsNullOrWhiteSpace(binding.Email) ?
            string.IsNullOrWhiteSpace(binding.DisplayName) ? binding.ConnectionId : binding.DisplayName : binding.Email)}"
        : "Dropbox Account Not Connected";

    private void HeaderAccount_Click(object sender, RoutedEventArgs e) => NavigateTo("Connections");

    private void OpenActivity_Click(object sender, RoutedEventArgs e) => NavigateTo("Activity");
    private void OpenQueue_Click(object sender, RoutedEventArgs e) => NavigateTo("Queue");
    private void OpenConflicts_Click(object sender, RoutedEventArgs e) => NavigateTo("Conflicts");
    private void OpenRewind_Click(object sender, RoutedEventArgs e) => NavigateTo("Rewind");

    private void OpenFirstSync_Click(object sender, RoutedEventArgs e)
    {
        if (PairList.SelectedItem is PairRow row) OpenFirstSyncReview(row.Id);
    }

    private void ActivityGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        ActivityGrid.SelectedItem = row.Item;
        row.Focus();
    }

    private void ActivityContextMenu_Opening(object sender, RoutedEventArgs e)
    {
        var row = ActivityGrid.SelectedItem as ActivityRow;
        var pairExists = row is not null && !string.IsNullOrWhiteSpace(row.PairId) &&
                         PairRows.Any(item => string.Equals(item.Id, row.PairId, StringComparison.Ordinal));
        ActivityOpenPairMenuItem.IsEnabled = pairExists;
        ActivityOpenRecoveryMenuItem.IsEnabled = pairExists && row is not null && IsRecoveryActivity(row);
        ActivityCopyPathMenuItem.IsEnabled = row is not null && !string.IsNullOrWhiteSpace(row.Path) &&
                                             !row.Path.StartsWith('<');
        ActivityCopyDetailsMenuItem.IsEnabled = row is not null;
    }

    private static bool IsRecoveryActivity(ActivityRow row) =>
        row.Outcome.Contains("recover", StringComparison.OrdinalIgnoreCase) ||
        row.Outcome.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
        row.Reason.Contains("recover", StringComparison.OrdinalIgnoreCase) ||
        row.Reason.Contains("delete", StringComparison.OrdinalIgnoreCase);

    private void OpenActivityPair_Click(object sender, RoutedEventArgs e)
    {
        if ((_inspectedActivity ?? ActivityGrid.SelectedItem as ActivityRow) is not ActivityRow activity ||
            string.IsNullOrWhiteSpace(activity.PairId)) return;
        var row = PairRows.SingleOrDefault(item => string.Equals(item.Id, activity.PairId, StringComparison.Ordinal));
        if (row is null) return;
        ShowFromBackground();
        NavigateTo("Pairs");
        PairList.SelectedItem = row;
        ExpandPairRow(row);
    }

    private void OpenActivityRecovery_Click(object sender, RoutedEventArgs e)
    {
        if ((_inspectedActivity ?? ActivityGrid.SelectedItem as ActivityRow) is not ActivityRow activity ||
            string.IsNullOrWhiteSpace(activity.PairId) ||
            !PairRows.Any(item => string.Equals(item.Id, activity.PairId, StringComparison.Ordinal)) ||
            !IsRecoveryActivity(activity)) return;
        RecoveryPairBox.SelectedValue = activity.PairId;
        ShowFromBackground();
        NavigateTo("Recovery");
    }

    private void CopyActivityPath_Click(object sender, RoutedEventArgs e)
    {
        if ((_inspectedActivity ?? ActivityGrid.SelectedItem as ActivityRow) is ActivityRow row && !string.IsNullOrWhiteSpace(row.Path) &&
            !row.Path.StartsWith('<')) SetClipboardText(row.Path);
    }

    private void CopyActivityDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_activityTabs.SelectedIndex == 2 && _runHistoryGrid?.SelectedItem is RunHistoryRow run)
        {
            SetClipboardText(run.DetailText);
            return;
        }
        if ((_inspectedActivity ?? ActivityGrid.SelectedItem as ActivityRow) is not ActivityRow row) return;
        SetClipboardText($"{row.When}\t{row.PairName}\t{row.Path}\t{row.Outcome}\t{row.Reason}\t{row.OperationId}");
    }

    private void SetClipboardText(string text)
    {
        if (ClipboardTextSetterForTest is not null)
        {
            ClipboardTextSetterForTest(text);
            return;
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (COMException) when (attempt < 9)
            {
                Thread.Sleep(25);
            }
            catch (COMException)
            {
                ShowStatus("Windows is using the clipboard. Try Copy again.");
                return;
            }
        }
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        UpdateSidebarPresentation();
    }

    private void UpdateSidebarPresentation()
    {
        if (NavigationColumn is null || HeaderBrandColumn is null || Navigation is null) return;
        var width = _sidebarCollapsed ? 64d : 196d;
        NavigationColumn.Width = new GridLength(width);
        HeaderBrandColumn.Width = new GridLength(width);
        HeaderBrandText.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        HeaderBrandPanel.Margin = _sidebarCollapsed ? new Thickness(0) : new Thickness(16, 0, 16, 0);
        HeaderBrandPanel.HorizontalAlignment = _sidebarCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
        HeaderBrandIcon.Width = HeaderBrandIcon.Height = _sidebarCollapsed ? 36 : 42;
        HeaderBrandIcon.Margin = _sidebarCollapsed ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        Navigation.Margin = _sidebarCollapsed ? new Thickness(4, 14, 4, 8) : new Thickness(10, 14, 10, 8);
        if (_utilityNavigation is not null)
            _utilityNavigation.Margin = _sidebarCollapsed ? new Thickness(4, 8, 4, 0) : new Thickness(10, 8, 10, 0);
        foreach (var item in SidebarItems())
        {
            var (icon, label) = NavigationLabel(item.Tag?.ToString());
            item.Content = _sidebarCollapsed ? CommandIcons.Icon(PageIcon(item.Tag?.ToString()))
                : CommandIcons.Label(label, PageIcon(item.Tag?.ToString()));
            item.ToolTip = label;
            item.Padding = _sidebarCollapsed ? new Thickness(0, 12, 0, 12) : new Thickness(12, 12, 12, 12);
            item.HorizontalContentAlignment = _sidebarCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
            System.Windows.Automation.AutomationProperties.SetName(item, label);
        }
        // Expanded restores the hamburger plus its text label; collapsed keeps the hamburger
        // alone inside the centred rail cell. Padding, content alignment and the accessible
        // name stay paired with each state.
        SidebarToggle.Content = _sidebarCollapsed
            ? CommandIcons.Icon("sidebar")
            : CommandIcons.Label("Collapse sidebar", "sidebar");
        SidebarToggle.MinWidth = 0;
        SidebarToggle.Width = _sidebarCollapsed ? 40 : double.NaN;
        SidebarToggle.Margin = _sidebarCollapsed ? new Thickness(0, 8, 0, 12) : new Thickness(10, 8, 10, 12);
        SidebarToggle.HorizontalAlignment = _sidebarCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        SidebarToggle.Padding = _sidebarCollapsed ? new Thickness(0) : new Thickness(14, 0, 8, 0);
        SidebarToggle.HorizontalContentAlignment = _sidebarCollapsed
            ? System.Windows.HorizontalAlignment.Center
            : System.Windows.HorizontalAlignment.Left;
        SidebarToggle.ToolTip = _sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar";
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle,
            _sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar");
        UpdateResponsiveLayout();
    }

    private static (string Icon, string Label) NavigationLabel(string? page) => page switch
    {
        "Dashboard" => ("⌂", "Home"),
        "Pairs" => ("⇄", "Folder Pairs"),
        "Attention" => ("!", "Needs Attention"),
        "Connections" => ("◉", "Connections"),
        "Bootstrap" => ("✓", "First sync"),
        "Activity" => ("◷", "Activity"),
        "Queue" => ("≡", "Queue"),
        "Conflicts" => ("△", "Conflicts"),
        "Recovery" => ("↶", "Recovery"),
        "Sharing" => ("", "Sharing"),
        "Storage" => ("", "Storage"),
        "Rewind" => ("⟲", "Dropbox Rewind"),
        "Settings" => ("⚙", "Settings"),
        _ => ("•", page ?? "Page")
    };

    private void PairList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        row.IsSelected = true;
        row.Focus();
    }

    private void PairList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedPair();

    private void EditPair_Click(object sender, RoutedEventArgs e) => EditSelectedPair();

    private void EditSelectedPair()
    {
        if (PairList.SelectedItem is not PairRow row)
        {
            ShowStatus("Select a folder pair to edit.");
            return;
        }
        ExpandPairRow(row);
        PairEditorPanel.BringIntoView();
        PairNameBox.Focus();
        PairNameBox.SelectAll();
        ShowStatus($"Editing {row.Name}. Save pair applies changes; changing either folder checkbox changes both sides.");
    }

    private async void RunPairNow_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || PairList.SelectedItem is not PairRow row) return;
        var requestedPairId = row.Id;
        if (!await AdmitManualSyncWithDraftAsync()) return;
        _pairSet = await _authorityHost.LoadPairsAsync();
        var pair = _pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, requestedPairId,
                       StringComparison.Ordinal)) ??
                   _pairSet.Pairs.SingleOrDefault(item => string.Equals(item.Id, _pairSet.SelectedPairId,
                       StringComparison.Ordinal));
        if (pair is null)
        {
            ShowStatus("The selected folder pair is no longer available.");
            return;
        }
        if (IsPairKnownUnhealthy(pair.Id))
        {
            PairEditorTabs.SelectedIndex = 3;
            const string guidance = "Repair this pair before syncing. Sync status explains the blocking problem.";
            PairHealthReasonText.Text = PairHealthBlockingReason(pair.Id) ?? guidance;
            PhaseText.Text = guidance;
            ShowStatus(guidance);
            return;
        }
        // The shared host validates each endpoint's exact account when preparing this pair.
        // A global cloud-account check incorrectly blocks an accountless local/local pair.
        await RunPairsAsync([pair], "Manual");
    }

    private async void RunAllPairsNow_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive) return;
        if (!await AdmitManualSyncWithDraftAsync()) return;
        _pairSet = await _authorityHost.LoadPairsAsync();
        var pairs = _pairSet.Pairs.Where(item => item.Enabled).ToArray();
        if (pairs.Length == 0)
        {
            ShowStatus("There are no active folder pairs to sync.");
            return;
        }
        await RunPairsAsync(pairs, "Manual all-pairs");
    }

    private async Task<bool> AdmitManualSyncWithDraftAsync()
    {
        if (!CurrentPairEditorHasUnsavedChanges()) return true;
        // A context-menu command can target another row while this editor has a dirty draft.
        // Keep the visible editor selected while the user chooses what to do; otherwise navigation
        // would silently replace the draft before the save/discard/cancel decision is applied.
        if (_editingPairRootId is not null)
        {
            _suppressPairSelection = true;
            PairList.SelectedItem = PairRows.SingleOrDefault(item =>
                string.Equals(item.Id, _editingPairRootId, StringComparison.Ordinal));
            _suppressPairSelection = false;
        }
        NavigateTo("Pairs");
        ResetPairEditorViewport();
        var choice = UnsavedDraftRunChoiceForTest?.Invoke() ?? AppDialogWindow.Choose(this,
            "Unsaved folder-pair changes", "What should Sync Shark do before syncing?",
        "The open editor contains changes that have not been saved. Sync Shark will not run older saved settings behind what you can see.",
        "Save changes", "Discard changes", "Cancel sync", secondaryIsDiscard: true);
        switch (choice)
        {
            case AppDialogChoice.Primary:
                ShowPairEditorStatus("Saving the open folder-pair changes before syncing.");
                if (await SavePairAsync())
                {
                    ShowStatus("Folder-pair changes saved. Sync is starting with the saved settings.");
                    return true;
                }
                ShowPairEditorStatus("The changes could not be saved, so sync did not start.", isError: true);
                ShowStatus("Sync did not start because the open folder-pair changes could not be saved.");
                return false;
            case AppDialogChoice.Secondary:
                DiscardCurrentPairDraft();
                ShowStatus("Unsaved folder-pair changes discarded. Sync is starting with the saved settings.");
                return true;
            default:
                const string message = "Sync cancelled. Your unsaved folder-pair changes are still open.";
                ShowPairEditorStatus(message);
                ShowStatus(message);
                return false;
        }
    }

    private void NavigateTo(string page, bool updateSelection = true)
    {
        if (_changingPairStorage) { ShowStatus("Wait for the current storage change before leaving this pair."); return; }
        if (!GuardWorkspaceNavigation(page, updateSelection)) return;
        if (page != "Sharing" && !TryCloseSharingEditor()) return;
        if (page != "Sharing") _sharingLoad?.Cancel();
        var firstSyncRequested = page == "Bootstrap";
        var enteringActivity = page == "Activity" && _workspacePage != "Activity";
        var enteringConnections = page == "Connections" && _workspacePage != "Connections";
        var reopeningFirstSync = page == "Attention" && _workspacePage != "Attention" &&
            _attentionTabs?.SelectedIndex == 1;
        if (_fullAppReady)
        {
            if (page is "Bootstrap" or "Conflicts")
            { _attentionTabs!.SelectedIndex = page == "Bootstrap" ? 1 : 2; page = "Attention"; }
            if (page == "Rewind") { _recoveryTabs!.SelectedIndex = 2; page = "Recovery"; }
        }
        if (_workspaceReady)
        {
            if (page == "Queue") { OpenWorkspaceActivity(1); return; }
            if (_workspacePage != page)
            {
                _workspaceBackStack.Push(_workspacePage);
                _workspacePage = page;
            }
            _workspaceBack.Visibility = Visibility.Collapsed;
        }
        DashboardPage.Visibility = page == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        PairsPage.Visibility = page == "Pairs" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        QueuePage.Visibility = _workspaceReady || page == "Queue" ? Visibility.Visible : Visibility.Collapsed;
        ConflictsPage.Visibility = _fullAppReady || page == "Conflicts" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPage.Visibility = page == "Recovery" ? Visibility.Visible : Visibility.Collapsed;
        RewindPage.Visibility = _fullAppReady || page == "Rewind" ? Visibility.Visible : Visibility.Collapsed;
        BootstrapPage.Visibility = _fullAppReady || page == "Bootstrap" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = page == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (_attentionPage is not null) _attentionPage.Visibility = page == "Attention" ? Visibility.Visible : Visibility.Collapsed;
        if (_sharingPage is not null)
        {
            var entering = page == "Sharing" && _sharingPage.Visibility != Visibility.Visible;
            _sharingPage.Visibility = page == "Sharing" ? Visibility.Visible : Visibility.Collapsed;
            if (entering) { RefreshSharingAccountChoices(); _ = LoadAccountSharingAsync(true); }
        }
        if (_storagePage is not null)
        {
            var entering = page == "Storage" && _storagePage.Visibility != Visibility.Visible;
            _storagePage.Visibility = page == "Storage" ? Visibility.Visible : Visibility.Collapsed;
            if (entering) { RefreshStorageChoices(true); _ = LoadStoragePageAsync(true); }
        }
        if (page == "Pairs" && PairList.SelectedItem is PairRow pair &&
            !string.Equals(_editingPairRootId, pair.Id, StringComparison.Ordinal))
            ApplyPairSelection(pair.Id);
        if (firstSyncRequested || reopeningFirstSync) _ = RefreshBootstrapReviewAsync();
        if (page == "Recovery")
        {
            if (_recoveryTabs?.SelectedIndex == 2) RefreshRewindState();
            else _ = RefreshRecoveryAsync(includeDeleted: _recoveryTabs?.SelectedIndex == 1);
        }
        if (page == "Rewind") RefreshRewindState();
        if (enteringActivity) _ = RefreshVisibleActivityAsync();
        if (enteringConnections && !SuppressLoadedStartupForTest) _ = RefreshConnectionStateAsync();
        if (page is "Dashboard" or "Pairs" or "Attention") _ = RefreshLocalRootHealthAsync(force: true);
        if (_utilityNavigation is not null)
        {
            if (page is "Connections" or "Settings") Navigation.SelectedItem = null;
            else _utilityNavigation.SelectedItem = null;
        }
        if (!updateSelection) return;
        foreach (var candidate in SidebarItems())
            if (string.Equals(candidate.Tag?.ToString(), page, StringComparison.Ordinal))
            {
                if ((page is "Connections" or "Settings") && _utilityNavigation is not null) _utilityNavigation.SelectedItem = candidate;
                else Navigation.SelectedItem = candidate;
                break;
            }
    }

    private async void ThemeChoice_Checked(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!_themeReady || sender is not RadioButton { Tag: string theme }) return;
        ThemeChoices.IsEnabled = false;
        try
        {
            await _themeManager.SetPreferenceAsync(theme);
            ThemeStatus.Text = string.Empty;
        }
        catch (Exception exception) { ThemeStatus.Text = "Theme preference was not saved: " + exception.Message; SelectTheme(_themeManager.Preference); }
        finally { ThemeChoices.IsEnabled = true; }
    }

    private void SelectTheme(string theme)
    {
        SystemThemeChoice.IsChecked = theme == UiPreferencesStore.SystemTheme;
        LightThemeChoice.IsChecked = theme == UiPreferencesStore.LightTheme;
        AmoledThemeChoice.IsChecked = theme == UiPreferencesStore.AmoledTheme;
    }

    private void ResultGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        ResultGrid.SelectedItem = row.Item;
        row.Focus();
    }

    private void ConflictGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        ConflictGrid.SelectedItem = row.Item;
        row.Focus();
    }

    private async void ConflictFilter_Changed(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (!IsLoaded) return;
        await LoadConflictPageAsync(reset: true);
    }

    private void ApplyConflictFilter()
    {
        if (ConflictRows is null || ConflictGroupFilter is null || ConflictSearchBox is null) return;
        var group = SelectedGroupKey(ConflictGroupFilter) ?? "All groups";
        var query = ConflictSearchBox.Text.Trim();
        ConflictRows.Clear();
        foreach (var row in _allConflictRows.Where(item =>
                     ScopeMatches(item.PairId) &&
                     (group == "All groups" || string.Equals(item.Group, group, StringComparison.Ordinal)) &&
                     (query.Length == 0 || item.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                      item.PairName.Contains(query, StringComparison.OrdinalIgnoreCase))))
            ConflictRows.Add(row);
        if (ConflictCount is not null) ConflictCount.Text = _allConflictRows.Count.ToString();
    }

    private async void SelectAllConflicts_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_runActive || _capturingConflictSelection) return;
        if (_matchingConflictSelection.Count > 0)
        {
            ClearMatchingConflictSelection();
            ConflictGrid.UnselectAll();
            ShowStatus("Cleared the all-matching conflict selection.");
            return;
        }
        try
        {
            var group = SelectedGroupKey(ConflictGroupFilter) ?? "All groups";
            var search = ConflictSearchBox.Text.Trim();
            var accountId = _workspaceAccountId;
            var version = _conflictQueryVersion;
            _capturingConflictSelection = true;
            SelectAllMatchingConflictsButton.IsEnabled = false;
            ActionAvailability.Refresh(SelectAllMatchingConflictsButton);
            ShowStatus("Capturing the exact conflict generations matching the current filters…");
            if (ConflictSelectionCheckpointForTest is not null) await ConflictSelectionCheckpointForTest();
            var selection = await _authorityHost.CaptureOpenConflictSelectionAsync(
                search, group, connectionId: accountId);
            if (_runActive || version != _conflictQueryVersion || accountId != _workspaceAccountId ||
                search != ConflictSearchBox.Text.Trim() || group != (SelectedGroupKey(ConflictGroupFilter) ?? "All groups"))
            {
                ShowStatus("The conflict view or sync state changed while selecting. No old selection was applied; select again in the current view.");
                return;
            }
            _matchingConflictGroup = group;
            _matchingConflictSearch = search;
            _matchingConflictSelection = selection;
            if (_matchingConflictSelection.Count == 0)
            {
                ClearMatchingConflictSelection();
                ShowStatus("No open conflicts match the current filters.");
                return;
            }
            ConflictGrid.SelectAll();
            SetMatchingConflictSelectionLabel($"Clear All Matching ({_matchingConflictSelection.Count:N0})", "close");
            ConflictSelectionSummary.Text =
                $"Exact snapshot selected: {_matchingConflictSelection.Count:N0} conflict(s), group “{_matchingConflictGroup}”" +
                (_matchingConflictSearch.Length == 0 ? "." : $", search “{_matchingConflictSearch}”.");
            ShowStatus($"Selected {_matchingConflictSelection.Count:N0} exact matching conflict generation(s), including unloaded rows.");
        }
        catch (Exception exception)
        {
            ClearMatchingConflictSelection();
            ShowStatus("The all-matching conflict selection could not be captured: " + exception.Message);
        }
        finally
        {
            _capturingConflictSelection = false;
            SelectAllMatchingConflictsButton.IsEnabled = !_runActive;
            ActionAvailability.Refresh(SelectAllMatchingConflictsButton);
        }
    }

    private void ClearMatchingConflictSelection()
    {
        _matchingConflictSelection = [];
        _matchingConflictGroup = "All groups";
        _matchingConflictSearch = string.Empty;
        SetMatchingConflictSelectionLabel("Select All Matching", "check");
        if (ConflictSelectionSummary is not null)
            ConflictSelectionSummary.Text = "No all-matching selection is active.";
    }

    private void SetMatchingConflictSelectionLabel(string label, string icon)
    {
        if (SelectAllMatchingConflictsButton is null) return;
        SelectAllMatchingConflictsButton.Content = CommandIcons.Label(label, icon);
        System.Windows.Automation.AutomationProperties.SetName(SelectAllMatchingConflictsButton, label);
    }

    // The two bulk conflict actions are ordered positions, never providers: the left control always
    // resolves the logical left endpoint and the right control the logical right endpoint, and the
    // existing compatibility handler and enum names are untouched. The caption is the exact ordered
    // side wording; the accessible name carries the pair's actual endpoint identities plus the fact
    // that the OPPOSITE endpoint's version is retained in Recovery first.
    internal sealed record ConflictActionPresentation(SyncPairSide LeftResolution, SyncPairSide RightResolution,
        string LeftLabel, string LeftAccessibleName, string RightLabel, string RightAccessibleName);

    internal ConflictActionPresentation BuildConflictActionPresentation(EndpointReviewPresentation? presentation) => new(
        SyncPairSide.Left, SyncPairSide.Right,
        EndpointReviewPresentation.ConflictBulkLabel(SyncPairSide.Left),
        EndpointReviewPresentation.ConflictBulkAccessibleName(SyncPairSide.Left, presentation),
        EndpointReviewPresentation.ConflictBulkLabel(SyncPairSide.Right),
        EndpointReviewPresentation.ConflictBulkAccessibleName(SyncPairSide.Right, presentation));

    internal void ApplyConflictActionPresentation(SyncPair? pair) =>
        ApplyConflictActionPresentation(pair is null ? null : EndpointReviewPresentation.For(pair));

    internal void ApplyConflictActionPresentation(EndpointReviewPresentation? presentation)
    {
        var action = BuildConflictActionPresentation(presentation);
        ApplyConflictBulkAction(KeepLocalConflictButton, "back", action.LeftLabel, action.LeftAccessibleName);
        ApplyConflictBulkAction(KeepDropboxConflictButton, "right", action.RightLabel, action.RightAccessibleName);
        ApplyConflictBulkAction(ConflictMenuItem("keep-left"), "back", action.LeftLabel, action.LeftAccessibleName);
        ApplyConflictBulkAction(ConflictMenuItem("keep-right"), "right", action.RightLabel, action.RightAccessibleName);
    }

    // The grid's context menu is the second surface for the same two ordered actions, so it carries
    // the identical caption and accessible name. It is located by its stable tag because a context
    // menu's items are not part of the window's own name scope.
    private MenuItem? ConflictMenuItem(string tag) => ConflictGrid?.ContextMenu?.Items.OfType<MenuItem>()
        .FirstOrDefault(item => (item.Tag as string) == tag);

    private static void ApplyConflictBulkAction(FrameworkElement? control, string icon, string label,
        string accessibleName)
    {
        if (control is null) return;
        var content = CommandIcons.Label(label, icon);
        if (control is MenuItem item) item.Header = content;
        else if (control is ContentControl contentControl) contentControl.Content = content;
        control.ToolTip = accessibleName;
        System.Windows.Automation.AutomationProperties.SetName(control, accessibleName);
    }

    // The conflicts page resolves its ordered pair context from the current selection and the exact
    // all-matching snapshot. Exactly one pair names that pair's actual endpoints; several pairs (or
    // none) keep the neutral ordered wording, so left and right stay unambiguous positions.
    private void RefreshConflictActionPresentation()
    {
        if (KeepLocalConflictButton is null || KeepDropboxConflictButton is null) return;
        var pairIds = (ConflictGrid?.SelectedItems.OfType<DecisionRow>().Select(row => row.PairId) ?? [])
            .Concat(_matchingConflictSelection.Select(item => item.PairId))
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        var pair = pairIds.Length == 1 ? _pairSet.Pairs.FirstOrDefault(item => item.Id == pairIds[0]) : null;
        ApplyConflictActionPresentation(pair);
    }

    private void ConflictGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshConflictActionPresentation();

    private async void CompareConflict_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        var row = ConflictGrid.SelectedItem as DecisionRow ?? ResultGrid.SelectedItem as DecisionRow;
        if (row?.NoticeOptions is { Count: > 0 })
        {
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback,
                "Use this notice's options to review/rebind, pause the pair, or leave it as is.", isError: false);
            return;
        }
        if (row is null || row.Outcome != SyncDecisionKind.Conflict.ToString())
        {
            // UF1: an explicit user action reports on the invoking Conflicts page through the one shared inline
            // surface, while the console recording continues unchanged behind it.
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, "Select one conflict to compare.", isError: false);
            ShowStatus("Select one conflict to compare.");
            return;
        }
        try
        {
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, "Loading both retained conflict versions for read-only comparison…", isError: false);
            ShowStatus("Loading both retained conflict versions for read-only comparison…");
            var content = ConflictContentLoaderForTest is not null
                ? await ConflictContentLoaderForTest(row.PairId, row.Path)
                : await _authorityHost.LoadConflictContentAsync(row.PairId, row.Path);
            var comparisonPair = _pairSet.Pairs.Single(pair => pair.Id == row.PairId);
            var comparison = new ConflictComparisonWindow(content, comparisonPair) { Owner = this };
            comparison.ShowDialog();
            if (comparison.RequestedResolution is { } resolution)
                await ResolveSelectedConflictAsync(resolution, row);
            else
            {
                SharedPresentation.SetInlineFeedback(ConflictActionFeedback,
                    "Comparison closed; neither version was changed.", isError: false);
                ShowStatus("Comparison closed; neither version was changed.");
            }
        }
        catch (Exception exception)
        {
            var failure = "Conflict comparison could not be opened: " + exception.Message;
            SharedPresentation.SetInlineFeedback(ConflictActionFeedback, failure, isError: true);
            ShowStatus(failure);
        }
    }

    private async void ResolveKeepLocal_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await ResolveSelectedConflictAsync(SyncAuthorityConflictResolutionKind.KeepLocal, keepSide: SyncPairSide.Left);
    }

    private async void ResolveKeepDropbox_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await ResolveSelectedConflictAsync(SyncAuthorityConflictResolutionKind.KeepDropbox, keepSide: SyncPairSide.Right);
    }

    private async void ResolvePreserveBoth_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        await ResolveSelectedConflictAsync(SyncAuthorityConflictResolutionKind.PreserveBoth);
    }

    private async Task ResolveSelectedConflictAsync(SyncAuthorityConflictResolutionKind resolution,
        DecisionRow? exactRow = null, SyncPairSide? keepSide = null)
    {
        if (_runActive) { ShowStatus("Wait for the current sync to finish before resolving a conflict."); return; }
        var useMatchingSnapshot = exactRow is null && _matchingConflictSelection.Count > 0;
        var rows = exactRow is not null ? new[] { exactRow } : ConflictGrid.SelectedItems.OfType<DecisionRow>()
            .Where(item => item.Outcome == SyncDecisionKind.Conflict.ToString()).ToArray();
        if (rows.Length == 0 && ResultGrid.SelectedItem is DecisionRow dashboardRow &&
            dashboardRow.Outcome == SyncDecisionKind.Conflict.ToString())
            rows = [dashboardRow];
        if (!useMatchingSnapshot && rows.Length == 0)
        {
            ShowStatus("Select one or more conflict rows first.");
            return;
        }
        var targets = new List<(string PairId, string Path, string? ConflictId, long? Generation)>();
        if (useMatchingSnapshot)
            targets.AddRange(_matchingConflictSelection.Select(item =>
                (item.PairId, item.RelativePath, (string?)item.ConflictId, (long?)item.Generation)));
        else
            targets.AddRange(rows.Select(item =>
                (item.PairId, item.Path, item.ConflictId, item.ConflictGeneration)));
        var effect = keepSide is { } chosenSide
            ? $"For every selected path, retain the displaced version in Recovery, then copy the {SyncEndpointPresentation.SideLabel(chosenSide).ToLowerInvariant()} endpoint's version to the other endpoint."
            : resolution switch
        {
            SyncAuthorityConflictResolutionKind.KeepLocal =>
                "Retain the displaced version in Recovery, then copy the chosen version to the other endpoint.",
            SyncAuthorityConflictResolutionKind.KeepDropbox =>
                "Retain the displaced version in Recovery, then copy the chosen version to the other endpoint.",
            _ => "For every selected path, retain both versions and create a collision-checked visible copy."
        };
        var scope = useMatchingSnapshot
            ? $"Exact all-matching snapshot: {targets.Count:N0} conflict(s) in group “{_matchingConflictGroup}”" +
              (_matchingConflictSearch.Length == 0 ? string.Empty : $" with search “{_matchingConflictSearch}”")
            : $"Selected conflicts: {targets.Count:N0}";
        if (!AppDialogWindow.Confirm(this, "Resolve conflicts", "Review the exact effect before continuing",
                $"{effect}\n\n{scope}\n\nRecommendations are not applied automatically.",
                "Resolve selected conflicts"))
            return;
        try
        {
            _pairSet = await _authorityHost.LoadPairsAsync();
            _runActive = true;
            SetRunControls();
            RunProgress.IsIndeterminate = true;
            var pairs = _pairSet.Pairs.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var requests = new List<SyncConflictResolutionRequest>();
            foreach (var target in targets)
            {
                if (!pairs.TryGetValue(target.PairId, out var pair))
                    throw new InvalidOperationException("A selected conflict belongs to a pair that is no longer retained.");
                if (target.ConflictId is null || target.Generation is null)
                    throw new InvalidOperationException("This row has no exact conflict generation. Refresh Conflicts before choosing a resolution.");
                var targetResolution = keepSide is { } side
                    ? EndpointReviewPresentation.For(pair).Keep(side) : resolution;
                requests.Add(new(new(target.ConflictId, target.PairId, target.Path, target.Generation.Value), targetResolution));
            }
            var batch = await _authorityHost.ResolveConflictSelectionCommandAsync(requests,
                onProgress: progress => PresentBatchProgressAsync(progress, "Resolve conflicts", false),
                admissionProgress: message => Dispatcher.Invoke(() => ShowStatus(message)));
            var completed = 0;
            foreach (var item in batch.Items)
            {
                if (item.Result is not { } result) continue;
                foreach (var decision in result.Decisions.Where(decision => decision.Kind == SyncDecisionKind.Recovered))
                {
                    _allRows.RemoveAll(row => row.PairId == item.Pair.Id &&
                        string.Equals(row.Path, decision.RelativePath, StringComparison.OrdinalIgnoreCase));
                    _allConflictRows.RemoveAll(row => row.PairId == item.Pair.Id &&
                        string.Equals(row.Path, decision.RelativePath, StringComparison.OrdinalIgnoreCase));
                    completed++;
                }
                ShowResult(item.Pair, result);
            }
            ShowStatus(completed == targets.Count
                ? $"Resolved {completed:N0} conflict(s) from the exact approved snapshot; every displaced version was retained first."
                : $"Resolved {completed:N0} of {targets.Count:N0}; remaining targets were not completed. Open this run's log for each exact outcome and safe next step.");
        }
        catch (Exception exception) { ShowStatus("Conflict resolution stopped safely: " + exception.Message); }
        finally
        {
            ClearMatchingConflictSelection();
            await FinishRunPresentationAsync("Conflict resolution",
                () => RefreshAuthorityProjectionsAsync(requireSuccess: true));
        }
    }

    private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedDetails();
    private void OpenDetails_Click(object sender, RoutedEventArgs e) => OpenSelectedDetails();

    private void OpenSelectedDetails()
    {
        if (ResultGrid.SelectedItem is not DecisionRow row) { ShowStatus("Select a result row to open its details."); return; }
        if (string.Equals(row.Path, "<first-sync>", StringComparison.OrdinalIgnoreCase))
        {
            _ = OpenFirstSyncFromDashboardAsync(row.PairId);
            return;
        }
        DetailsText.Text = $"Pair: {row.PairName}{Environment.NewLine}Path: {row.Path}{Environment.NewLine}Outcome: {row.Outcome}{Environment.NewLine}Reason: {row.Reason}";
        TechnicalText.Text = $"pair_id={row.PairId}{Environment.NewLine}relative_path={row.Path}{Environment.NewLine}decision={row.Outcome}{Environment.NewLine}safety=expected-baseline; conflict-preserving; recoverable-archive; no-permanent-delete";
        if (_workspaceReady)
        {
            _workspaceDetailTitle.Text = row.Path;
            _workspaceInspector.Visibility = Visibility.Visible;
            _workspaceBody.ColumnDefinitions[1].Width = new GridLength(320);
        }
    }

    private async Task OpenFirstSyncFromDashboardAsync(string pairId)
    {
        try
        {
            OpenFirstSyncReview(pairId);
            await Task.CompletedTask;
            ShowStatus("Opened the first-sync review for this folder pair.");
        }
        catch (Exception exception)
        {
            ShowStatus("The first-sync review could not be opened: " + exception.Message);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is DecisionRow row) CopyText(row.Path, "path");
    }

    private void CopyTechnical_Click(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is DecisionRow row) CopyText($"Path: {row.Path}{Environment.NewLine}Outcome: {row.Outcome}{Environment.NewLine}Reason: {row.Reason}", "technical details");
    }

    private void CopyText(string value, string label)
    {
        try { Clipboard.SetText(value); ShowStatus($"Copied {label}."); }
        catch (Exception exception) { ShowStatus($"Could not copy {label}: {exception.Message}"); }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject source, string? name = null)
        where T : FrameworkElement
    {
        foreach (var child in FindDescendants<T>(source))
            if (name is null || string.Equals(child.Name, name, StringComparison.Ordinal)) return child;
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject source) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleResponsiveLayout();
    private void PairsScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Nested tree scrolling and row-details extent changes must not schedule a
        // fresh parent measure. Only the actual viewport owns responsive width.
        if (ReferenceEquals(e.OriginalSource, PairsScroller) && Math.Abs(e.ViewportWidthChange) > 0.5)
            ScheduleResponsiveLayout();
    }

    private void ScheduleResponsiveLayout()
    {
        if (_responsiveUpdatePending) return;
        _responsiveUpdatePending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _responsiveUpdatePending = false;
            UpdateResponsiveLayout();
        });
    }

    private static string FormatConflictSize(long? value) => value is null ? "unknown size" : $"{value:N0} bytes";
    private static string FormatConflictTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Unavailable";

    /// <summary>
    /// W2-B: the ordered presentation of one conflict row's pair, or null when the pair's declared
    /// endpoints cannot be described. It reads only the retained pair definition - no provider, account or
    /// credential access - and is resolved once per conflict page or batch, never per row.
    /// </summary>
    private static EndpointReviewPresentation? TryConflictEndpointPresentation(SyncPair pair)
    {
        try
        {
            return EndpointReviewPresentation.For(pair);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException
                                              or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// W2-B: the conflict row summary names the pair's ORDERED endpoints through the shared presentation.
    /// There is no 'This PC' assumption and no provider ternary: a pair whose endpoints cannot be described
    /// falls back to the neutral ordered wording rather than inventing a local side, and each size is the
    /// side its own layout assigns.
    /// </summary>
    internal static string ConflictSummaryReason(EndpointReviewPresentation? endpoints, long? localSize,
        long? dropboxSize)
    {
        var left = endpoints?.LeftTitle ?? SyncEndpointPresentation.SideLabel(SyncPairSide.Left) + " endpoint";
        var right = endpoints?.RightTitle ?? SyncEndpointPresentation.SideLabel(SyncPairSide.Right) + " endpoint";
        var leftBytes = endpoints is null ? localSize : endpoints.LeftValue(localSize, dropboxSize);
        var rightBytes = endpoints is null ? dropboxSize : endpoints.RightValue(localSize, dropboxSize);
        return $"{left} {FormatConflictSize(leftBytes)}, {right} {FormatConflictSize(rightBytes)}. " +
               "Recommendation is evidence only and never selects a winner.";
    }
    private double _lastResponsiveWidth = double.NaN;
    internal int ResponsiveLayoutPasses { get; private set; }
    private void UpdateResponsiveLayout()
    {
        // UI354-DRAFT2: the same one rule covers every layout and resize; the explicit endpoint session
        // keeps the legacy Connection panel collapsed while its own account selectors are visible.
        ApplyExplicitEditorLayout(_endpointSession is not null);
        if (CardsPanel is null) return;
        var width = PairsScroller?.ViewportWidth > 0 ? PairsScroller.ViewportWidth :
            ActualWidth > 0 ? ActualWidth - NavigationColumn.ActualWidth - 40 : Width;
        var contentWidth = Math.Max(320, width);
        if (!double.IsNaN(_lastResponsiveWidth) && Math.Abs(_lastResponsiveWidth - contentWidth) < 0.5) return;
        _lastResponsiveWidth = contentWidth;
        ResponsiveLayoutPasses++;
        // A DataGrid inside the outer scroller must not grow to the preferred
        // width of its own row-details content and feed that width back to it.
        if (PairList is not null) PairList.Width = contentWidth;
        if (PairEditorPanel is not null) PairEditorPanel.Width = Math.Max(280, contentWidth - 24);
        CardsPanel.Columns = contentWidth < 600 ? 2 : contentWidth < 1440 ? 3 : 6;
        if (PairsHeaderActions is not null)
        {
            var compactHeader = contentWidth < 820;
            Grid.SetRow(PairsHeaderActions, compactHeader ? 1 : 0);
            Grid.SetColumn(PairsHeaderActions, compactHeader ? 0 : 1);
            Grid.SetColumnSpan(PairsHeaderActions, compactHeader ? 2 : 1);
            PairsHeaderActions.HorizontalAlignment = compactHeader
                ? System.Windows.HorizontalAlignment.Left
                : System.Windows.HorizontalAlignment.Right;
            PairsHeaderActions.Margin = compactHeader ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            PairSearchHost.Width = compactHeader ? Math.Max(140, contentWidth - 290) : 220;
        }
        if (PairRootGrid is null) return;
        var wrapEditorActions = contentWidth < 940;
        Grid.SetRow(PairEditorActions, wrapEditorActions ? 1 : 0);
        Grid.SetColumn(PairEditorActions, wrapEditorActions ? 0 : 1);
        Grid.SetColumnSpan(PairEditorActions, wrapEditorActions ? 2 : 1);
        PairEditorActions.HorizontalAlignment = wrapEditorActions
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;
        PairEditorActions.Margin = wrapEditorActions ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        var wrapFolderToolbar = contentWidth < 1040;
        Grid.SetRow(FolderToolbar, wrapFolderToolbar ? 1 : 0);
        Grid.SetColumn(FolderToolbar, wrapFolderToolbar ? 0 : 1);
        Grid.SetColumnSpan(FolderToolbar, wrapFolderToolbar ? 2 : 1);
        FolderToolbar.HorizontalAlignment = wrapFolderToolbar
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;
        FolderToolbar.Margin = wrapFolderToolbar ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        var narrow = contentWidth < 820;
        if (narrow)
        {
            Grid.SetRow(LocalRootPanel, 0);
            Grid.SetColumn(LocalRootPanel, 0);
            Grid.SetColumnSpan(LocalRootPanel, 3);
            Grid.SetRow(PairDirectionGlyph, 1);
            Grid.SetColumn(PairDirectionGlyph, 0);
            Grid.SetColumnSpan(PairDirectionGlyph, 3);
            PairDirectionGlyph.Margin = new Thickness(0, 8, 0, 8);
            Grid.SetRow(DropboxRootPanel, 2);
            Grid.SetColumn(DropboxRootPanel, 0);
            Grid.SetColumnSpan(DropboxRootPanel, 3);
            SyncTypeColumn.Width = new GridLength(0);
            Grid.SetRow(SyncTypePanel, 1);
            Grid.SetColumn(SyncTypePanel, 0);
            Grid.SetColumnSpan(SyncTypePanel, 2);
            SyncTypePanel.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            Grid.SetRow(LocalRootPanel, 0);
            Grid.SetColumn(LocalRootPanel, 0);
            Grid.SetColumnSpan(LocalRootPanel, 1);
            Grid.SetRow(PairDirectionGlyph, 0);
            Grid.SetColumn(PairDirectionGlyph, 1);
            Grid.SetColumnSpan(PairDirectionGlyph, 1);
            PairDirectionGlyph.Margin = new Thickness(0);
            Grid.SetRow(DropboxRootPanel, 0);
            Grid.SetColumn(DropboxRootPanel, 2);
            Grid.SetColumnSpan(DropboxRootPanel, 1);
            SyncTypeColumn.Width = new GridLength(230);
            Grid.SetRow(SyncTypePanel, 0);
            Grid.SetColumn(SyncTypePanel, 1);
            Grid.SetColumnSpan(SyncTypePanel, 1);
            SyncTypePanel.Margin = new Thickness(0);
        }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_closing) return;
        if (_workspacePreferences.KeepRunningWhenClosed) { HideToBackground(); return; }
        try { await PrepareProductExitAsync(); System.Windows.Application.Current.Shutdown(); }
        catch (Exception exception) { AppDialogWindow.Inform(this, "Sync Shark Is Still Running", "The safe exit checkpoint did not finish.", exception.Message); }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam,
        ref bool handled)
    {
        if (message == 0x004A)
        {
            handled = true;
            if (ExistingWindowActivation.ReadRequest(wParam, lParam) is not { } request) return IntPtr.Zero;
            Dispatcher.InvokeAsync(() => QueueExplorerRequest(request)); return (IntPtr)1;
        }
        if (message == ExistingWindowActivation.ShowMessage)
        {
            ShowFromBackground(); handled = true; return IntPtr.Zero;
        }
        const int wmGetMinMaxInfo = 0x0024;
        const uint monitorDefaultToNearest = 0x00000002;
        if (message != wmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, monitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        info.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        info.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
        info.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        Marshal.StructureToPtr(info, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}

public sealed record DecisionRow(string PairId, string PairName, string Path, string Outcome, string Reason,
    string Connection = "Dropbox", string Group = "Needs review", string Recommendation = "Review",
    string LocalModified = "Unavailable", string DropboxModified = "Unavailable",
    string? ConflictId = null, long? ConflictGeneration = null, SyncEndpointContext? EndpointContext = null,
    IReadOnlyList<SyncNoticeOption>? NoticeOptions = null)
{
    /// <summary>
    /// W2-B: the displayed comparison category. The raw Group key is a physical compatibility port, so it is
    /// never shown as a Left/Right claim: 'Local newer' names the LOCAL port, which is the logical RIGHT for
    /// a reversed local layout. With a captured context the ordered side that actually carries that port is
    /// named; without one the truthful neutral category is used. The raw key is unchanged and still drives
    /// filtering, and no provider is ever substituted for a side that did not record one.
    /// </summary>
    public string GroupDisplay => Group switch
    {
        "Local newer" => NewerOnPort(SyncAuthorityCloudSides.LogicalLeft),
        "Dropbox newer" => NewerOnPort(SyncAuthorityCloudSides.LogicalRight),
        _ => Group
    };

    private string NewerOnPort(string compatibilityPort)
    {
        var side = EndpointContext is null ? null
            : EndpointContext.Left.CompatibilityPort == compatibilityPort ? EndpointContext.Left
            : EndpointContext.Right.CompatibilityPort == compatibilityPort ? EndpointContext.Right
            : null;
        return side is null
            ? SyncAuthorityRuntimeHost.NewerVersionGroup
            : "Newer on " + EndpointReviewPresentation.Title(side);
    }
}
public sealed record ActivityRow(string When, string Path, string Outcome, string Reason,
    string Connection = "Unknown", string? PairId = null, string PairName = "App",
    string? OperationId = null, string ItemKind = "Unknown", string FileType = "Unknown",
    long? SizeBytes = null, string? LocalRoot = null, string? DropboxRoot = null,
    string? EventId = null, long? Sequence = null, IReadOnlyList<string>? Categories = null,
    string Severity = "Information", string Entity = "Unknown", string? RunId = null,
    string? ConnectionId = null, SyncEndpointContext? EndpointContext = null,
    SyncAuthorityOperationKind? OperationKind = null, SyncActivityPhase? Phase = null,
    string? DecisionCode = null)
{
    public bool MatchesAccount(string? accountId) => accountId is null || (EndpointContext is { } context
        ? context.Left.ConnectionId == accountId || context.Right.ConnectionId == accountId
        : ConnectionId == accountId);
    public SyncEndpointActivityRow? EndpointPresentation => OperationKind is null ? null :
        SyncEndpointActivityPresentation.Resolve(OperationKind, EndpointContext, Phase ?? SyncActivityPhase.Unknown,
            Path.StartsWith('<') ? null : Path, ItemKind is "Folder" or "Directory");
    public static string EndpointLabel(SyncEndpointContextSide side) => side.IsLocal ? "This computer"
        : ActivityCloud.Label(side.Provider);
    public string? RecordedCloudProvider
    {
        get
        {
            if (EndpointContext is null) return ActivityCloud.ProviderForConnectionId(ConnectionId);
            var affected = EndpointPresentation?.Affected switch
            {
                SyncActivitySide.Left => EndpointContext.Left,
                SyncActivitySide.Right => EndpointContext.Right,
                _ => null
            };
            if (affected is { IsLocal: false }) return affected.Provider;
            var cloud = new[] { EndpointContext.Left, EndpointContext.Right }.Where(side => !side.IsLocal).ToArray();
            return cloud.Length == 1 ? cloud[0].Provider : null;
        }
    }
    public bool IsPairEvent => Entity.Equals("Folder pair", StringComparison.OrdinalIgnoreCase) ||
        ItemKind.Equals("Pair", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(PairId) && Path.StartsWith('<') &&
        (IsDiagnosticSetup || Outcome.Equals("Bound", StringComparison.OrdinalIgnoreCase));
    public bool IsAppEvent => EndpointPresentation?.Category is SyncActivityCategory.Internal or SyncActivityCategory.Configuration ||
        IsPairEvent || Entity is "Run" or "Application" or "Account" or "Diagnostics" or "Diagnostic" or "Policy" or "Storage policy" or "Recovery";
    public bool IsFileActivity => !IsAppEvent && !IsRoutineCheck && !Path.StartsWith('<') &&
        ItemKind is "File" or "Folder" && (CategoryTags.Count == 0 || HasCategory(SyncActivityCategories.Files));
    public bool IsGoogleDrive => RecordedCloudProvider == SyncProviders.GoogleDrive;
    public string CloudName => ActivityCloud.Label(RecordedCloudProvider);
    public string TimeLabel => DateTimeOffset.TryParse(When, out var date) ? date.ToLocalTime().ToString("HH:mm") : When;
    // UI06: the full recorded date and time stay available on hover and in details even though the
    // row and column show the short local time only.
    public string FullTimestamp => DateTimeOffset.TryParse(When, out var date)
        ? date.ToLocalTime().ToString("d MMM yyyy HH:mm:ss zzz") : When;
    public string TimeAccessibleLabel => "Recorded " + FullTimestamp;
    // Deduplication is by stable event identity only. Session rows carry their own generated id;
    // retained rows carry the projection's event id. Only a genuinely identity-less historical row
    // falls back to its recorded facts, and even then an operation id never collapses distinct
    // outcomes or instants into one row.
    public string EventKey => !string.IsNullOrEmpty(EventId) ? "id:" + EventId
        : "row:" + When + "|" + (PairId ?? "") + "|" + Path + "|" + Outcome + "|" + (OperationId ?? "");
    public IReadOnlyList<string> CategoryTags => Categories ?? [];
    public string CategoryLabel => CategoryTags.Count == 0 ? "Legacy / unknown" : string.Join(", ", CategoryTags);
    public bool HasCategory(string category) => CategoryTags.Contains(category, StringComparer.Ordinal);
    // A retained row without category evidence stays visible under every selection instead of
    // silently disappearing, and it is labelled as legacy in its details.
    public bool InCategories(IReadOnlyCollection<string>? selected) => selected is not { Count: > 0 } ||
        CategoryTags.Count == 0 || CategoryTags.Any(selected.Contains);
    public string EventDateLabel => DateTimeOffset.TryParse(When, out var date) ? date.ToLocalTime().ToString("d MMM yyyy\nHH:mm:ss") : When;
    public string DateGroup => DateTimeOffset.TryParse(When, out var date)
        ? date.LocalDateTime.Date == DateTime.Today ? "Today" : date.LocalDateTime.Date == DateTime.Today.AddDays(-1)
            ? "Yesterday" : date.ToLocalTime().ToString("ddd, d MMM yyyy") : "Earlier";
    public string FileFormat => ItemKind == "File" ? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() : string.Empty;
    public string FileCategory => FileFormat switch
    {
        "JPG" or "JPEG" or "PNG" or "GIF" or "WEBP" or "HEIC" or "SVG" or "BMP" => "Image",
        "PDF" or "TXT" or "DOC" or "DOCX" or "RTF" or "ODT" or "MD" => "Document",
        "XLS" or "XLSX" or "CSV" or "ODS" => "Spreadsheet",
        "MP3" or "M4A" or "WAV" or "FLAC" or "OGG" => "Audio",
        "MP4" or "MKV" or "MOV" or "AVI" or "WEBM" => "Video",
        "ZIP" or "7Z" or "RAR" or "TAR" or "GZ" => "Archive",
        "EXE" or "MSI" or "APK" => "Application",
        _ => "Other"
    };
    public string SizeLabel => ItemKind == "Folder" ? "—" : SizeBytes is not { } size ? "—"
        : size < 1024 ? $"{size:N0} B" : size < 1024 * 1024 ? $"{size / 1024d:0.#} KB"
        : size < 1024L * 1024 * 1024 ? $"{size / (1024d * 1024):0.#} MB" : $"{size / (1024d * 1024 * 1024):0.#} GB";
    public string DisplayName => IsAppEvent || string.IsNullOrWhiteSpace(Path) || Path.StartsWith('<')
        ? string.IsNullOrWhiteSpace(PairName) ? Entity == "Run" ? "Sync run" : "Sync Shark" : PairName :
        System.IO.Path.GetFileName(Path.Replace('/', '\\').TrimEnd('\\'));
    // A scan/hash/index event can be classified as a pair event and still have an exact
    // target. The operation log must show that retained target, not replace it with the
    // pair name. Keep its full path; unrelated activity feeds retain DisplayName.
    public string OperationTarget => !string.IsNullOrWhiteSpace(Path) && !Path.StartsWith('<')
        ? Path : string.IsNullOrWhiteSpace(Outcome) ? "Run / background process" : OutcomeLabel;
    // These are command-lifecycle records, not proof of a successful transfer.
    // Preserve the raw outcome for diagnostics and use one readable label in feeds/logs.
    public string OutcomeLabel => Outcome.StartsWith("run pair ", StringComparison.Ordinal)
        ? "Folder pair command " + Outcome[9..]
        : Outcome.StartsWith("run all ", StringComparison.Ordinal)
            ? "Multi-pair command " + Outcome[8..] : Outcome;
    public bool IsRoutineCheck => Outcome.Equals("Identical", StringComparison.OrdinalIgnoreCase) || Outcome.Equals("Unchanged", StringComparison.OrdinalIgnoreCase);
    public string HumanAction => EndpointPresentation is { } presentation
        ? presentation.Action + " · " + presentation.Status : Outcome.ToLowerInvariant() switch
    {
        "starting upload" or "started upload" => $"Uploading to {CloudName}",
        "completed upload" or "uploaded" => $"Uploaded to {CloudName}",
        "starting download" or "started download" => "Downloading to this computer",
        "completed download" or "downloaded" => "Downloaded to this computer",
        "completed" => Reason.ToLowerInvariant() switch
        {
            "the upload new operation completed." => $"Uploaded to {CloudName}",
            "the upload update operation completed." => $"Updated in {CloudName}",
            "the download new operation completed." => "Downloaded to this computer",
            "the download update operation completed." => "Updated on this computer",
            "the archive local operation completed." => "Removed locally; Recovery copy saved",
            "the archive dropbox operation completed." => $"Removed from {CloudName}; Recovery copy saved",
            "the move local operation completed." => "Moved on this computer",
            "the move dropbox operation completed." => $"Moved in {CloudName}",
            "the allocate_control operation completed." => "Folder pair setup completed",
            _ => Outcome
        },
        "identical" or "unchanged" => "Checked; no changes",
        "reconcile completed" => "Folder comparison completed",
        "reconcile executing" => "Comparing folders",
        "running" => "Sync in progress",
        _ => OutcomeLabel
    };
    public string ShortAction => HumanAction;
    // UI09: prepared sync storage is diagnostic setup, never a transfer. The label says so, and
    // this predicate keeps that setup row out of any transfer count or transfer filter.
    public bool IsDiagnosticSetup => Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
        Reason.Trim().Equals("The allocate_control operation completed.", StringComparison.OrdinalIgnoreCase);
    // UI06: no pair/time concatenation on one metadata string. The pair and the time are separate
    // elements, so the legacy single-line subtitle carries only the action and the pair.
    public string Subtitle => HumanAction + " · " + PairName;
    public string EventText => OutcomeLabel + " · " + Path + " · " + Reason;
    public string StatusGlyph => Phase is { } phase ? phase switch
    {
        SyncActivityPhase.Completed => "✓",
        SyncActivityPhase.Running => "↻",
        SyncActivityPhase.Blocked or SyncActivityPhase.Conflict or SyncActivityPhase.EffectUnknown => "!",
        _ => "·"
    } : Outcome.Contains("Starting", StringComparison.OrdinalIgnoreCase) || Outcome.Contains("Started", StringComparison.OrdinalIgnoreCase) ? "↻" :
        Outcome.Contains("blocked", StringComparison.OrdinalIgnoreCase) || Outcome.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
        Outcome.Contains("attention", StringComparison.OrdinalIgnoreCase) || Outcome.Contains("failed", StringComparison.OrdinalIgnoreCase) ? "!" :
        Outcome.Contains("completed", StringComparison.OrdinalIgnoreCase) || Outcome is "Uploaded" or "Downloaded" or "Identical" or "Unchanged" or "Recoverable deletion" ? "✓" : "·";
}
public sealed record ActivityPairChoice(string? Id, string DisplayName, string? ConnectionId = null,
    string ProviderKey = "", string? AccountName = null)
{
    public string CompactLabel => AccountName ?? DisplayName;
    public string Label => DisplayName;
}
internal enum DropboxNodeLoadOutcome { Complete, Failed, Cancelled, Stale, Unavailable }
internal sealed record DropboxChildLookup(DropboxFolderNode? Node, bool Exhaustive);
public sealed class PairRow : INotifyPropertyChanged
{
    private bool _needsAttention;
    private string _attentionReason;
    private bool _globalLiveSyncEnabled;
    private bool _canChangeLiveSync = true;
    private bool _isExpanded;
    private PairEndpointMarks _endpointMarks;

    public PairRow(string id, string name, string localRoot, string dropboxRoot, string connection,
        bool enabled, int priority, bool propagateDeletions, bool filesOnDemandEnabled, string monitorState,
        bool NeedsAttention = false, bool GlobalLiveSyncEnabled = true, string? AttentionReason = null,
        SyncMode direction = SyncMode.TwoWay, string provider = SyncProviders.Dropbox,
        SyncEndpointDocument? endpointDocument = null)
    {
        Id = id;
        Name = name;
        LocalRoot = localRoot;
        DropboxRoot = dropboxRoot;
        Connection = connection;
        Enabled = enabled;
        Priority = priority;
        PropagateDeletions = propagateDeletions;
        FilesOnDemandEnabled = filesOnDemandEnabled;
        Direction = direction;
        Provider = SyncProviders.Normalize(provider);
        MonitorState = monitorState;
        _needsAttention = NeedsAttention;
        _globalLiveSyncEnabled = GlobalLiveSyncEnabled;
        _attentionReason = AttentionReason ?? (NeedsAttention
            ? "This pair needs attention. Open Sync status for details."
            : enabled ? "This pair is ready for live sync." : "This pair is paused.");
        // The saved pair is the authority for a real row; this seed keeps every existing legacy row
        // constructor working unchanged until the next refresh recomputes from the saved pair.
        _endpointMarks = PairEndpointMarks.IsExplicitDocument(endpointDocument)
            ? PairEndpointMarks.FromDocument(endpointDocument!, direction)
            : PairEndpointMarks.FromLegacyRowFields(localRoot, provider, direction);
    }

    public string Id { get; }
    public string Name { get; private set; }
    public string LocalRoot { get; private set; }
    public string DropboxRoot { get; private set; }
    public string Connection { get; private set; }
    public bool Enabled { get; private set; }
    public int Priority { get; private set; }
    public bool PropagateDeletions { get; private set; }
    public bool FilesOnDemandEnabled { get; private set; }
    public string MonitorState { get; private set; }
    public bool NeedsAttention => _needsAttention;
    public bool GlobalLiveSyncEnabled => _globalLiveSyncEnabled;
    public bool CanChangeLiveSync => _canChangeLiveSync;
    public string AttentionReason => _attentionReason;
    public SyncMode Direction { get; private set; }
    public string Provider { get; private set; }
    /// <summary>Both endpoint marks of this pair, refreshed from the saved endpoint document on update.</summary>
    public PairEndpointMarks EndpointMarks => _endpointMarks;
    public string CloudName => SyncProviders.DisplayName(Provider);
    public string SyncType => SyncModePolicy.Label(Direction);
    public string Status => NeedsAttention ? "Needs attention" :
        !Enabled ? "Pair paused" :
        !GlobalLiveSyncEnabled ? "Automatic Sync Off" : MonitorState;
    public string StatusDetail => NeedsAttention
        ? AttentionReason
        : !GlobalLiveSyncEnabled && Enabled
            ? "Turn on Automatic Sync in Settings to monitor this pair automatically."
            : AttentionReason;
    public string StatusSummary => NeedsAttention ? ShortAttentionSummary(AttentionReason) :
        !GlobalLiveSyncEnabled && Enabled ? "Enable in Settings" :
        Enabled ? "Ready" : "Paused for this pair";
    public string LiveSyncLabel => Enabled ? "Live Sync" : "Pair Paused";
    public bool IsExpanded => _isExpanded;
    // UI12: the leading row control shows the pair's real provider mark instead of a chevron,
    // so expansion is reported through the accessible state rather than a swapped glyph.
    public string ExpandedStateLabel => _isExpanded ? "Expanded" : "Collapsed";
    public string ExpandCollapseHint => _isExpanded ? "Hide this pair in the list" : "Show this pair in the list";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SyncPair pair, string connection, string monitorState, bool needsAttention,
        bool globalLiveSyncEnabled, string attentionReason)
    {
        ArgumentNullException.ThrowIfNull(pair);
        Name = pair.Name;
        LocalRoot = pair.LocalRoot;
        DropboxRoot = pair.DropboxRoot;
        Connection = connection;
        Enabled = pair.Enabled;
        Priority = pair.Priority;
        PropagateDeletions = pair.PropagateDeletions;
        FilesOnDemandEnabled = pair.FilesOnDemandEnabled;
        Direction = pair.Direction;
        Provider = SyncProviders.Normalize(pair.Provider);
        _endpointMarks = PairEndpointMarks.For(pair);
        MonitorState = monitorState;
        _needsAttention = needsAttention;
        _globalLiveSyncEnabled = globalLiveSyncEnabled;
        _attentionReason = attentionReason;
        foreach (var property in new[]
                 {
                     nameof(Name), nameof(LocalRoot), nameof(DropboxRoot), nameof(Connection), nameof(Enabled),
                     nameof(Priority), nameof(PropagateDeletions), nameof(FilesOnDemandEnabled), nameof(Direction), nameof(SyncType),
                     nameof(MonitorState), nameof(NeedsAttention), nameof(GlobalLiveSyncEnabled),
                     nameof(AttentionReason), nameof(Status), nameof(StatusDetail), nameof(StatusSummary),
                     nameof(LiveSyncLabel), nameof(Provider), nameof(EndpointMarks), nameof(CloudName), nameof(IsExpanded), nameof(ExpandedStateLabel),
                     nameof(ExpandCollapseHint)
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded) return;
        _isExpanded = expanded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedStateLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandCollapseHint)));
    }

    public void UpdateHealth(bool needsAttention, string attentionReason)
    {
        if (_needsAttention == needsAttention && string.Equals(_attentionReason, attentionReason,
                StringComparison.Ordinal)) return;
        _needsAttention = needsAttention;
        _attentionReason = attentionReason;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsAttention)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttentionReason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusSummary)));
    }

    public void UpdateGlobalLiveSync(bool enabled)
    {
        if (_globalLiveSyncEnabled == enabled) return;
        _globalLiveSyncEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalLiveSyncEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusSummary)));
    }

    public void SetCanChangeLiveSync(bool enabled)
    {
        if (_canChangeLiveSync == enabled) return;
        _canChangeLiveSync = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanChangeLiveSync)));
    }

    private static string ShortAttentionSummary(string reason)
    {
        if (reason.Contains("local folder", StringComparison.OrdinalIgnoreCase)) return "Local folder unavailable";
        if (reason.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)) return "Dropbox folder unavailable";
        if (reason.Contains("First-sync", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("First sync", StringComparison.OrdinalIgnoreCase)) return "First sync needs attention";
        if (reason.Contains("conflict", StringComparison.OrdinalIgnoreCase)) return "Conflicts need review";
        return "Open for repair details";
    }
}
public sealed record QueueRow(string PairId, string Enqueued, string Pair, string Path, int Priority,
    string Operation, string State, string Connection = "Dropbox", string? OperationId = null,
    string ItemKind = "Unknown", string FileType = "Unknown")
{
    public string DisplayName => System.IO.Path.GetFileName(Path.Replace('/', '\\').TrimEnd('\\'));
    public string Subtitle => Operation + " · " + Pair + " · " + State;
    public string ShortAction => "Action: " + Operation + "\nStatus: " + StateLabel;
    public string StateLabel => DescribeState(State);
    internal static string DescribeState(string state) => state.Split('·')[0]
        .Replace(" (Recovery First)", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim().ToLowerInvariant() switch
    {
        "effect unknown" => "Verification Needed",
        "reconciling" => "Checking the Previous Attempt",
        "effect issuing" => "Applying the Change",
        "leased" => "Preparing",
        "planning" => "Checking the Item",
        "staging" => "Preparing the Transfer",
        "verifying" => "Verifying the Result",
        "committed" or "cleanup pending" => "Finishing Up",
        "blocked" => "Needs Attention",
        "queued" => "Queued",
        "complete" or "completed" => "Completed",
        "paused" => "Paused",
        "cancelled" => "Cancelled",
        "superseded" => "Replaced by Newer Work",
        _ => state.Replace(" · ", "\n")
    };
    public string PairName => Pair;
    public string TimeLabel => DateTimeOffset.TryParse(Enqueued, out var when) ? when.ToLocalTime().ToString("HH:mm") : string.Empty;
    public string StatusGlyph => State.Contains("blocked", StringComparison.OrdinalIgnoreCase) || State.Contains("unknown", StringComparison.OrdinalIgnoreCase) ? "!" : "↻";
}
public sealed record PairPreviewCounts(int Uploads, int Downloads, int Deletions, int Moves, int Other);
public sealed record FolderSelectionRow(string PairId, string RelativePath, string DisplayPath, bool? IsChecked,
    string Presence, int DescendantFolders, string? StableFolderIdentity = null, int LocalItems = 0,
    int OnlineOnlyItems = 0, int MixedItems = 0, int ExcludedItems = 0, int PendingItems = 0,
    int ErrorItems = 0)
{
    public string FolderName => RelativePath.Split('/').LastOrDefault() ?? DisplayPath.Trim();
    public Thickness IndentMargin => new(Math.Max(0, RelativePath.Count(character => character == '/')) * 18,
        0, 0, 0);
    public string SelectionTitle => IsChecked == true ? "Synced folder" : IsChecked == false ? "Excluded folder" :
        "Partly synced folder";
    public string SelectionDescription => IsChecked == true
        ? "This folder and everything inside it will sync."
        : IsChecked == false
            ? "This folder and everything inside it will not sync."
            : "This folder contains both synced and excluded folders.";
    public string LocalStatus => Presence switch
    {
        "Only on this computer" or "Both sides" or "Cloud not loaded" => "Available",
        "Online-only" => "Online-only",
        "Only in cloud" => "(missing)",
        "This PC not loaded" => "(not loaded)",
        _ => "(unknown)"
    };

    public string DropboxStatus => Presence switch
    {
        "Only in cloud" or "Both sides" or "Online-only" or "This PC not loaded" => "Available",
        "Only on this computer" => "(missing)",
        "Cloud not loaded" => "(not loaded)",
        _ => "(unknown)"
    };
}
public sealed class FolderSelectionNode
{
    public FolderSelectionNode(FolderSelectionRow row) => Row = row;

    public FolderSelectionRow Row { get; }
    public string RelativePath => Row.RelativePath;
    public string FolderName => Row.FolderName;
    public bool? IsChecked => Row.IsChecked;
    public string LocalStatus => Row.LocalStatus;
    public string DropboxStatus => Row.DropboxStatus;
    public string SelectionTitle => Row.SelectionTitle;
    public string SelectionDescription => Row.SelectionDescription;
    public ObservableCollection<FolderSelectionNode> Children { get; } = [];
}
public sealed record PairPreviewRow(string Change, string Path, string State)
{
    public string StateLabel => QueueRow.DescribeState(State);
}
internal sealed record PairDraftBindingPlan(string LocalRoot, DropboxFolderEntry DropboxRoot,
    bool DefaultIncluded, IReadOnlyList<SyncSelectionRule> Rules);
public sealed class BootstrapRow : INotifyPropertyChanged
{
    private string _selectedAction;
    private string _effect;

    public BootstrapRow(string pairId, string path, string group, string recommendation, string effect,
        string localSize, string dropboxSize, string localModified, string dropboxModified,
        string pairName = "Folder pair", string itemKind = "file", string fileType = "File",
        string? selectedAction = null, string provider = SyncProviders.Dropbox)
        : this(pairId, path, group, recommendation, effect, localSize, dropboxSize, localModified,
            dropboxModified, pairName, itemKind, fileType, selectedAction, provider, null)
    {
    }

    // EPS4A: a review row carries the exact ordered endpoints its pair was described from, so the
    // displayed group, the destination choices, the sizes and the times present the true logical left
    // and right sides. The stored group and action values stay the same physical keys.
    internal BootstrapRow(string pairId, string path, string group, string recommendation, string effect,
        string localSize, string dropboxSize, string localModified, string dropboxModified,
        string pairName, string itemKind, string fileType, string? selectedAction, string provider,
        EndpointReviewPresentation? endpoints, SyncBootstrapMoveReview? move = null)
    {
        Endpoints = endpoints;
        _reviewEffect = effect;
        Move = move;
        PairId = pairId;
        Path = path;
        Group = group;
        Recommendation = recommendation;
        LocalSize = localSize;
        DropboxSize = dropboxSize;
        LocalModified = localModified;
        DropboxModified = dropboxModified;
        var leftUsesLocalPort = endpoints is null || endpoints.LeftUsesLocalPort;
        LeftSize = leftUsesLocalPort ? localSize : dropboxSize;
        RightSize = leftUsesLocalPort ? dropboxSize : localSize;
        LeftModified = leftUsesLocalPort ? localModified : dropboxModified;
        RightModified = leftUsesLocalPort ? dropboxModified : localModified;
        PairName = pairName;
        CloudName = SyncProviders.DisplayName(provider);
        ItemKind = string.Equals(itemKind, "directory", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(itemKind, "folder", StringComparison.OrdinalIgnoreCase)
            ? "Folder"
            : "File";
        FileType = fileType;
        var allowed = SyncBootstrapActions.For(group, move);
        AvailableActions = allowed.Count == 0 ? [recommendation] : allowed;
        ActionChoices = AvailableActions
            .Select(action => new BootstrapActionChoice(action, ActionDisplay(action))).ToArray();
        _selectedAction = AvailableActions.Contains(selectedAction, StringComparer.Ordinal)
            ? selectedAction!
            : recommendation;
        _effect = EffectFor(_selectedAction, effect);
    }

    public string PairId { get; }
    public string Path { get; }
    public string Group { get; }
    public string Recommendation { get; }
    public SyncBootstrapMoveReview? Move { get; }
    public string LocalSize { get; }
    public string DropboxSize { get; }
    public string LocalModified { get; }
    public string DropboxModified { get; }
    public string LeftSize { get; }
    public string RightSize { get; }
    public string LeftModified { get; }
    public string RightModified { get; }
    public string PairName { get; }
    public string CloudName { get; }
    public string DisplayGroup => Endpoints is null
        ? Group.Replace("Dropbox", CloudName, StringComparison.Ordinal)
        : MainWindow.GroupLabel(Group, Endpoints);
    public string ItemKind { get; }
    public string FileType { get; }
    public IReadOnlyList<string> AvailableActions { get; }
    public IReadOnlyList<BootstrapActionChoice> ActionChoices { get; }
    public bool CanChooseAction => AvailableActions.Count > 1;
    public string Effect => _effect;
    internal EndpointReviewPresentation? Endpoints { get; }

    public string SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (string.Equals(_selectedAction, value, StringComparison.Ordinal) ||
                !AvailableActions.Contains(value, StringComparer.Ordinal)) return;
            _selectedAction = value;
            _effect = EffectFor(value, _effect);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAction)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Effect)));
            ActionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ActionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _reviewEffect;

    private string EffectFor(string action, string fallback) => action switch
    {
        "DeleteLocal" => _reviewEffect,
        "Upload" when Group == SyncBootstrapActions.LocalUpdatedGroup =>
            "Update on " + (Endpoints?.RemotePortTitle ?? CloudName) + "; destination unchanged since sync",
        "Download" when Group == SyncBootstrapActions.RemoteUpdatedGroup =>
            "Update on " + (Endpoints?.LocalPortTitle ?? "this computer") + "; destination unchanged since sync",
        "Upload" or "Download" => _reviewEffect,
        "MoveLocal" => "Move on " + (Endpoints?.LocalPortTitle ?? "this computer") +
            ": " + Move?.FromPath + " → " + Path,
        "MoveRemote" => "Move on " + (Endpoints?.RemotePortTitle ?? CloudName) +
            ": " + Move?.FromPath + " → " + Path,
        "Skip for now" => "Leave unchanged and keep waiting",
        "No change" => "Record as already in sync",
        "Review conflict" => "Nothing changes until you choose",
        "Check access" => "Nothing changes",
        _ => fallback
    };

    private string ActionDisplay(string action) =>
        action == "DeleteLocal" ? "Remove this copy (Recovery first)" :
        action == "Upload" && Group == SyncBootstrapActions.LocalUpdatedGroup
            ? "Update on " + (Endpoints?.RemotePortTitle ?? CloudName)
            : action == "Download" && Group == SyncBootstrapActions.RemoteUpdatedGroup
                ? "Update on " + (Endpoints?.LocalPortTitle ?? "this computer")
                : Endpoints is null ? action : MainWindow.ActionLabel(action, Endpoints);
}
// EPS4A: one selectable action as it reads in a picker. The displayed text names the true endpoint
// the copy reaches; the value stays the existing physical plan action.
public sealed record BootstrapActionChoice(string Value, string Display);
internal readonly record struct BootstrapActionKey(string PairId, string RelativePath);
internal sealed record BootstrapChoiceState(string Group, string Action);
// UI16: one labelled field of the contextual recovery details. RequiresReading marks the explanatory
// guard and placeholder lines so they render as muted supporting text rather than as recorded values.
internal sealed record RecoveryDetailField(string Label, string Value, bool RequiresReading = false);

public sealed record RecoveryRow(SyncRecoveryCatalogItem Item, string Connection, string PairName, string Source,
    string Path, string Location, string Size, string Retained, string Retention, string Health)
{
    internal const string NotRecorded = "Not recorded";
    internal const string GuardLabel = "Restore Guard";

    public string DisplayName => System.IO.Path.GetFileName(Path.Replace('/', '\\'));
    public string CompactSubtitle => "Source: " + Source + " · " + AccountLabel + " · " + PairLabel + " · " + Health;
    public string RetainedDisplay => DateTimeOffset.TryParse(Retained, out var when)
        ? when.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture) : Retained;
    // UI16: the compact row keeps the source account on the same quick-info line as the pair, so two
    // accounts holding an identical file name stay distinguishable without opening the details.
    public string Subtitle => "Source: " + Source + " · Account: " + AccountLabel +
                              "\nFolder Pair: " + PairLabel + " · Status: " + Health;

    internal string AccountLabel => string.IsNullOrWhiteSpace(Connection) ? NotRecorded : Connection;
    internal string PairLabel => string.IsNullOrWhiteSpace(PairName) ? NotRecorded : PairName;
    internal string ItemLabel => Item.ObjectKind?.ToLowerInvariant() switch
        { "folder" or "directory" => "Folder", "file" => "File", _ => NotRecorded };

    // UI16: the exact saved-copy or revision version identity. Only fields the recovery catalog
    // actually carries are projected, and a genuinely absent value keeps an explicit marker.
    // Public because the Detailed recovery column binds to it through WPF data binding.
    public string VersionIdentity
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Item.Revision)) parts.Add("Revision " + Item.Revision);
            if (!string.IsNullOrWhiteSpace(Item.Identity)) parts.Add("Identity " + Item.Identity);
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Item.ContentHash))
                parts.Add("Content hash " + Item.ContentHash);
            return parts.Count == 0 ? NotRecorded : string.Join(" · ", parts);
        }
    }

    // The three recovery uncertainty guards keep their existing wording; only their presentation
    // moved from one concatenated string into the shared contextual details.
    internal static string Guard(SyncRecoveryCatalogItem item) => item.Source == SyncRecoverySource.CloudTrash
        ? "Restores this exact cloud item to its original folder only. A stale version, unavailable parent or occupied destination is refused without replacement."
        : item.CanRestore
        ? "Restore original retains an occupied destination first; Restore as copy never replaces it."
        : item.Source == SyncRecoverySource.DropboxDeleted
            ? "Load revisions for this path to choose the exact version to restore."
            : "This item cannot be restored until its health issue is resolved.";

    // UI16: the contextual projection for one saved copy. Every value comes from the recovered
    // catalog item; a value the model genuinely does not carry keeps an explicit Not recorded
    // marker instead of disappearing from the inspector.
    internal IReadOnlyList<RecoveryDetailField> DetailFields()
    {
        var destination = string.IsNullOrWhiteSpace(Path) ? NotRecorded : Path + " (pair-relative)";
        return
        [
            new("Source Account", AccountLabel),
            new("Folder Pair", PairLabel),
            new("Recovery Copy", string.IsNullOrWhiteSpace(Item.Key) ? NotRecorded : Item.Key),
            new("Saved Copy or Revision", string.IsNullOrWhiteSpace(Source) ? NotRecorded : Source),
            new("Item", ItemLabel),
            new("Original Path", string.IsNullOrWhiteSpace(Path) ? NotRecorded : Path),
            new("Version Identity", VersionIdentity),
            new("Content Hash", string.IsNullOrWhiteSpace(Item.ContentHash) ? NotRecorded : Item.ContentHash!),
            new("Saved Location", string.IsNullOrWhiteSpace(Location) ? NotRecorded : Location),
            new("Size", Item.Size is null ? NotRecorded : Size),
            new("Saved", Item.RetainedUtc is null ? NotRecorded : Retained),
            new("Retention", string.IsNullOrWhiteSpace(Retention) ? NotRecorded : Retention),
            new("Health", string.IsNullOrWhiteSpace(Health) ? NotRecorded : Health),
            new("Restore Destination", destination),
            new("Restore As Copy Destination", Item.CanRestoreCopy
                ? "Not recorded until preview; a new name is chosen without replacing the current destination"
                : "Not available while this saved copy cannot be restored"),
            new(GuardLabel, Guard(Item), RequiresReading: true)
        ];
    }

    // The plain-text record carries the same values as the structured inspector.
    internal string DetailText() => string.Join("\n",
        DetailFields().Select(field => field.Label + ": " + field.Value));
}
public sealed record RewindRow(string Path, string Status, string Group, string Effect, string LocalChanged,
    string Reason);

public sealed class LocalFolderNode : INotifyPropertyChanged
{
    private bool? _isChecked;
    private bool _isExpanded;
    private bool _isFilterVisible = true;
    private string _status = string.Empty;
    private readonly object _loadGate = new();
    private Task? _loadTask;

    private LocalFolderNode(string name, string path, bool isPlaceholder = false, string? knownStatus = null)
    {
        Name = name;
        Path = path;
        IsPlaceholder = isPlaceholder;
        if (!isPlaceholder)
        {
            _status = knownStatus ?? AvailabilityStatus(path);
            Children.Add(new LocalFolderNode("Loading…", path, isPlaceholder: true));
        }
    }

    public string Name { get; }
    public string Path { get; }
    public bool IsPlaceholder { get; }
    public bool CanSelect => !IsPlaceholder;
    public bool IsLoaded { get; private set; }
    public ObservableCollection<LocalFolderNode> Children { get; } = [];
    public bool? IsChecked
    {
        get => _isChecked;
        private set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
    }
    public bool IsFilterVisible
    {
        get => _isFilterVisible;
        private set { if (_isFilterVisible == value) return; _isFilterVisible = value; OnPropertyChanged(nameof(IsFilterVisible)); }
    }
    public bool CanExpand => !IsPlaceholder && (!IsLoaded || Children.Any(child => !child.IsPlaceholder));
    /// <summary>
    /// Bounded left indentation for this row. The path browsers keep the folder name and the expand
    /// chevron inside the visible panel even when a hierarchy is nested deeply.
    /// </summary>
    public System.Windows.Thickness IndentMargin =>
        new(FolderVirtualizingPanel.BoundedIndent(Depth), 0, 0, 0);
    private int Depth
    {
        get
        {
            var root = System.IO.Path.GetPathRoot(Path) ?? string.Empty;
            var remainder = Path.Length > root.Length ? Path[root.Length..] : string.Empty;
            return remainder.Length == 0 ? 0 : 1 + remainder.Count(
                character => character == System.IO.Path.DirectorySeparatorChar);
        }
    }
    public string Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetChecked(bool? value) => IsChecked = value;
    public void SetStatus(string? value) => Status = value ?? string.Empty;
    public FolderStorageChoice? StorageChoice { get; private set; }
    public void SetStorageChoice(FolderStorageChoice? value)
    { StorageChoice = value; OnPropertyChanged(nameof(StorageChoice)); }
    public void SetFilterVisible(bool value) => IsFilterVisible = value;

    private static string AvailabilityStatus(string path)
    {
        try
        {
            return AvailabilityStatusForAttributes(File.GetAttributes(path));
        }
        catch (UnauthorizedAccessException) { return "Unavailable"; }
        catch (IOException) { return "Unavailable"; }
    }

    internal static string AvailabilityStatusForAttributes(FileAttributes attributes)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes recallOnDataAccess = (FileAttributes)0x00400000;
        return attributes.HasFlag(FileAttributes.Offline) || attributes.HasFlag(recallOnOpen) ||
               attributes.HasFlag(recallOnDataAccess)
            ? "Online-only"
            : string.Empty;
    }

    public static IReadOnlyList<LocalFolderNode> CreateDriveRoots()
    {
        return Environment.GetLogicalDrives()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new LocalFolderNode(path, path, knownStatus: string.Empty))
            .ToArray();
    }

    public static Task<IReadOnlyList<LocalFolderNode>> CreateDriveRootsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateDriveRoots());
    }

    internal static LocalFolderNode CreateForTest(string rootPath, IEnumerable<string> relativeFolders)
    {
        var root = new LocalFolderNode(System.IO.Path.GetFileName(rootPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : rootPath, rootPath);
        root.Children.Clear();
        root.IsLoaded = true;
        foreach (var relative in relativeFolders)
        {
            var current = root;
            foreach (var segment in relative.Replace('/', System.IO.Path.DirectorySeparatorChar).Split(
                         System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current.Children.FirstOrDefault(child =>
                    string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    next = new LocalFolderNode(segment, System.IO.Path.Combine(current.Path, segment));
                    next.Children.Clear();
                    next.IsLoaded = true;
                    current.Children.Add(next);
                }
                current = next;
            }
        }
        return root;
    }

    internal static LocalFolderNode CreateUnloadedForTest(string rootPath) => CreateUnloaded(rootPath);

    internal static LocalFolderNode CreateUnloaded(string rootPath) =>
        new(System.IO.Path.GetFileName(rootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 }
            name ? name : rootPath, rootPath, knownStatus: string.Empty);

    public void LoadChildren()
    {
        if (IsPlaceholder || IsLoaded) return;
        IsLoaded = true;
        Children.Clear();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path)
                         .OrderBy(item => System.IO.Path.GetFileName(item), StringComparer.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name)) name = folder;
                Children.Add(new LocalFolderNode(name, folder));
            }
        }
        catch (UnauthorizedAccessException) { Status = "Unavailable"; }
        catch (IOException) { Status = "Unavailable"; }
        OnPropertyChanged(nameof(CanExpand));
    }

    public Task LoadChildrenAsync(CancellationToken cancellationToken = default)
    {
        if (IsPlaceholder || IsLoaded) return Task.CompletedTask;
        Task loadTask;
        lock (_loadGate)
        {
            if (IsLoaded) return Task.CompletedTask;
            loadTask = _loadTask ??= LoadChildrenCoreAsync(cancellationToken);
        }
        return cancellationToken.CanBeCanceled ? loadTask.WaitAsync(cancellationToken) : loadTask;
    }

    private async Task LoadChildrenCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var descriptors = await Task.Run(() => Directory.EnumerateDirectories(Path)
                .Select(folder =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(name)) name = folder;
                    return (Name: name, Path: folder, Status: AvailabilityStatus(folder));
                })
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Children.Clear();
            foreach (var item in descriptors)
                Children.Add(new LocalFolderNode(item.Name, item.Path, knownStatus: item.Status));
            IsLoaded = true;
            Status = string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            IsLoaded = true;
            Children.Clear();
            Status = "Unavailable";
        }
        catch (IOException)
        {
            IsLoaded = true;
            Children.Clear();
            Status = "Unavailable";
        }
        finally
        {
            lock (_loadGate) _loadTask = null;
            OnPropertyChanged(nameof(CanExpand));
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
