using System.IO;
using System.Windows;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

public partial class App : System.Windows.Application
{
    private PilotInstanceLock? _singleInstance;
    private ThemeManager? _themeManager;
    private MainWindow? _mainWindow;
    private TrayFlyoutWindow? _flyout;
    private TrayClient? _trayClient;
    private IDisposable? _trayOwnership;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayPauseItem;
    private System.Drawing.Icon? _baseTrayIcon;
    private System.Drawing.Icon? _stateTrayIcon;
    private System.Drawing.Bitmap? _pauseMenuIcon;
    private System.Drawing.Bitmap? _resumeMenuIcon;
    private System.Drawing.Bitmap? _stopMenuIcon;
    private string _trayState = string.Empty;
    private string _attentionNotification = string.Empty;
    private DateTimeOffset? _notifiedRun;
    private readonly DateTimeOffset _notificationSessionStarted = DateTimeOffset.UtcNow;
    internal bool SuppressProductStartup { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (SuppressProductStartup)
        {
            base.OnStartup(e);
            return;
        }
        // The CLI is a whole-process mode: it must run headless, without a window or tray icon and
        // before every ordinary WPF or Explorer startup route. All other startup modes are unchanged.
        if (CliEntry.IsCliInvocation(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(CliEntry.Execute(e.Args));
            return;
        }
        // The core switch is read once, right after the CLI branch, so every route below sees arguments with the
        // switch removed exactly as the CLI does.
        var (useCoreV2, args, coreError) = CoreSwitch.Select(e.Args,
            Environment.GetEnvironmentVariable(CoreSwitch.EnvironmentVariable));
        if (coreError is not null)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppDialogWindow.Inform(null, "Invalid start option", "Sync Shark could not start.", coreError);
            Shutdown(2);
            return;
        }
        // Reversible opt-in while the remaining full-window screens are converted to IPC.
        // This branch must precede the legacy product lock and preference stores.
        if (useCoreV2 && args.Length == 0 && Environment.GetEnvironmentVariable("SYNCSHARK_SHARED_AUTOSYNC") == "1")
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new SharedAutoSyncWindow();
            MainWindow.Show();
            return;
        }
        // Pure background-tray client. Like the shared-client branch it must precede the product lock and
        // every preference store: it attaches to the one host and never owns an engine or the store.
        var trayRoute = TrayStartup.Decide(useCoreV2, args);
        if (trayRoute != TrayStartup.Route.NotTray)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (trayRoute == TrayStartup.Route.Invalid)
            {
                AppDialogWindow.Inform(null, "Tray could not start", "The background tray attaches to the core v2 AutoSync host.",
                    "Start it with --core v2 --tray; nothing was opened or changed.");
                Shutdown(2);
                return;
            }
            var host = BackgroundHostEntry.Client();
            var tray = TrayStartup.Start(useCoreV2, args, host.TrySendAsync, () => Shutdown(),
                message => WriteStartupProgress("tray", message));
            _trayOwnership = tray.Ownership;
            _trayClient = tray.Client;
            if (tray.ExitCode is { } exitCode) Shutdown(exitCode);
            return;
        }
        if (args.Length == 3 && args[0] == "--move-data")
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MoveDataAndRestart(args[1], args[2]);
            return;
        }
        if (args.Length == 1 && args[0] == "--check-rollback-ready")
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CheckRollbackReadiness();
            return;
        }
        if (args.Length == 1 && args[0] is "--register-explorer-package" or "--remove-explorer-package" or
            "--check-explorer-package" or "--check-no-explorer-package")
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunExplorerPackageOperation(args[0]);
            return;
        }
        ExplorerRequest? explorerRequest;
        try { explorerRequest = ExplorerRequest.Parse(args); }
        catch (Exception error) { AppDialogWindow.Inform(null, "Explorer Action Not Available", "Select one item in a Sync Shark folder pair.", error.Message); Shutdown(); return; }
        WriteStartupProgress("product-lock", "Acquiring the existing product owner lock; no fresh sync is being started.");
        try { _singleInstance = PilotInstanceLock.Acquire(SyncSharkRuntimePaths.InstanceLockFile); }
        catch (PilotReleaseRecoveryRequiredException exception)
        {
            WriteStartupProgress("release-recovery-required", exception.Message);
            AppDialogWindow.Inform(null, "Installation Recovery Required",
                "The interrupted installation or rollback must finish before Sync Shark can use its saved state.", exception.Message);
            Shutdown();
            return;
        }
        catch (InvalidDataException exception)
        {
            AppDialogWindow.Inform(null, "Invalid data location", "Sync Shark cannot open its configured data folder.", exception.Message);
            Shutdown(1); return;
        }
        catch (IOException exception)
        {
            if (!ExistingWindowActivation.TryRaise(explorerRequest))
                AppDialogWindow.Inform(null, "Sync Shark is already running",
                    useCoreV2 ? "Another GUI or the shared background host owns Sync Shark. Use SYNCSHARK_SHARED_AUTOSYNC=1 to open the client, or run autosync stop before opening this legacy window."
                        : "The existing Sync Shark window is still available from the notification area.", exception.Message);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        WriteStartupProgress("theme", "Loading the retained theme and display preferences.");
        _themeManager = new ThemeManager(new UiPreferencesStore(_singleInstance.StateAuthority));
        _themeManager.Initialize();
        WriteStartupProgress("window-construction", "Constructing the application controls and the existing authority host.");
        _mainWindow = new MainWindow(_singleInstance.StateAuthority, _themeManager, useCoreV2);
        if (!useCoreV2)
            _mainWindow.Title = CoreSwitch.LegacyNotice;
        WriteStartupProgress("cli-bridge", "Starting the same-process trusted CLI receiver; this is not full startup readiness.");
        if (!useCoreV2) StartCliBridge();
        if (explorerRequest is not null) _mainWindow.QueueExplorerRequest(explorerRequest);
        _mainWindow.CompactViewRequested += (_, _) => ShowTrayFlyout();
        WriteStartupProgress("tray", "Preparing the tray icon before local-state loading and startup recovery.");
        CreateTrayIcon();
        WriteStartupProgress("initial-window-load", "Starting the initial window load; full readiness follows completed initialization and recovery.");
        if (args.Any(value => string.Equals(value, "--background", StringComparison.OrdinalIgnoreCase)))
            _mainWindow.StartHidden();
        else
            _mainWindow.Show();
    }

    internal static void WriteStartupProgress(string phase, string detail)
    {
        try { Console.Error.WriteLine($"Sync Shark startup: [{DateTimeOffset.UtcNow:O}] {phase}; {detail}"); }
        catch (IOException) { /* A closed diagnostic stream cannot break product startup. */ }
        catch (ObjectDisposedException) { /* Logging remains best-effort when a reader disconnects. */ }
    }

    private async void CheckRollbackReadiness()
    {
        try
        {
            if (!ExplorerIntegration.IsInstalledProcess) { Shutdown(11); return; }
            _singleInstance = PilotInstanceLock.AcquireForMaintenance(SyncSharkRuntimePaths.InstanceLockFile);
            var ready = await Task.Run(() => RollbackReadiness.CheckAsync(_singleInstance.StateAuthority));
            Shutdown(ready ? 0 : 10);
        }
        catch { Shutdown(11); }
    }

    private async void RunExplorerPackageOperation(string operation)
    {
        try
        {
            if (operation == "--check-no-explorer-package") ExplorerPackage.VerifyAbsent();
            else if (operation == "--check-explorer-package")
                ExplorerPackage.VerifyRegistered(Path.GetDirectoryName(ExistingWindowActivation.ResolveExecutable(Environment.ProcessPath!))!,
                    typeof(App).Assembly.GetName().Version!);
            else
            {
                if (!ExplorerIntegration.IsInstalledProcess) throw new IOException("Only the active installed app can change its Explorer package.");
                _singleInstance = PilotInstanceLock.AcquireForMaintenance(SyncSharkRuntimePaths.InstanceLockFile);
                if (operation == "--register-explorer-package") await ExplorerPackage.RegisterAsync();
                else await ExplorerPackage.RemoveAsync();
            }
            Shutdown(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); Shutdown(12); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _flyout?.Close();
        _trayClient?.Dispose();
        _trayClient = null;
        _trayOwnership?.Dispose();
        _trayOwnership = null;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _themeManager?.Dispose();
        _cliBridge?.Dispose();
        _stateTrayIcon?.Dispose(); _baseTrayIcon?.Dispose();
        _pauseMenuIcon?.Dispose(); _resumeMenuIcon?.Dispose(); _stopMenuIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private SyncCliBridgeServer? _cliBridge;

    // Starts the same-product CLI bridge so a CLI invocation executes inside this running process,
    // against this host, without stealing the lock, showing the window or starting a second runtime.
    // A bridge failure never stops the product.
    private void StartCliBridge()
    {
        if (_mainWindow is null || _singleInstance is null) return;
        // The bridge runtime is v1-typed, so while the app runs on core v2 the bridge is skipped
        // explicitly here instead of being started and failing when it reaches the v1-only host.
        if (!_mainWindow.HostIsCoreV1)
        {
            WriteStartupProgress("cli-bridge", "CLI bridge is off while core v2 runs");
            return;
        }
        try
        {
            var runtime = new CliAppRuntime(_mainWindow.AuthorityHost, _singleInstance.StateAuthority,
                () => _mainWindow.TrayRunActive, () => _mainWindow.TrayPaused,
                () => Dispatcher.Invoke(ShowMainWindow),
                () => Dispatcher.InvokeAsync(async () =>
                {
                    if (_mainWindow is not null) await _mainWindow.PrepareProductExitAsync();
                }).Task.Unwrap(),
                (version, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    _mainWindow.ApplyRetainedUpdateFromCliAsync(version, cancellationToken)).Task.Unwrap(),
                (mode, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    _mainWindow.ConfigureDiscoveryFromCliAsync(mode, cancellationToken)).Task.Unwrap(),
                cancellationToken => Dispatcher.InvokeAsync(() =>
                    _mainWindow.BeforeCliSettingsSaveAsync(cancellationToken)).Task.Unwrap(),
                (previous, saved, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    _mainWindow.ApplyCliSavedSettingsAsync(previous, saved, cancellationToken)).Task.Unwrap(),
                () => Dispatcher.InvokeAsync(() => _mainWindow.CompleteCliSettingsSaveAsync()).Task.Unwrap(),
                (invocation, cancellationToken) => Dispatcher.InvokeAsync(() =>
                    _mainWindow.ExecuteCliAppActionAsync(invocation, cancellationToken)).Task.Unwrap(),
                cancellationToken => Dispatcher.InvokeAsync(() =>
                    _mainWindow.BeforeCliPauseChangeAsync(cancellationToken)).Task.Unwrap(),
                () => Dispatcher.InvokeAsync(() => _mainWindow.CompleteCliPauseChangeAsync()).Task.Unwrap(),
                () => Dispatcher.InvokeAsync(() => _mainWindow.LoadCliAttentionItemsAsync()).Task.Unwrap());
            var bridgeWindow = _mainWindow;
            bridgeWindow.Closed += (_, _) => bridgeWindow.DisposeCliOwnershipSessions();
            // Finish the checkpoint, return its result, THEN stop the process. The waiting client
            // observes process exit before reporting success; it never kills the application.
            _cliBridge = new SyncCliBridgeServer(runtime,
                () => Dispatcher.BeginInvoke(new Action(() => Shutdown())),
                () => _mainWindow.CliUpdateExitPrepared,
                () => Dispatcher.InvokeAsync(() => _mainWindow.RefreshAfterCliCommandAsync()).Task.Unwrap());
            _cliBridge.Start();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Sync Shark CLI bridge: " + SyncCli.DescribeException(error));
        }
    }

    private void CreateTrayIcon()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _pauseMenuIcon = CreateNativePauseIcon("pause");
        _resumeMenuIcon = CreateNativePauseIcon("play");
        _stopMenuIcon = CreateNativePauseIcon("stop");
        _trayStatusItem = new System.Windows.Forms.ToolStripMenuItem("Status: starting") { Enabled = false };
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Open Sync Shark", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("Sync now", null, (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            if (_mainWindow is not null) await _mainWindow.SyncNowFromBackgroundAsync();
            RefreshTrayState();
        }));
        _trayPauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause Sync", null,
            (_, _) => Dispatcher.InvokeAsync(async () =>
            {
                if (_mainWindow is not null && _mainWindow.TrayPaused) await _mainWindow.ResumePausedAsync(null);
                RefreshTrayState();
            }));
        // The native menu reuses the shared option set instead of its own list.
        foreach (var option in PauseOption.All)
        {
            var item = _trayPauseItem.DropDownItems.Add(option.Label,
                option.StopSquare ? _stopMenuIcon : _pauseMenuIcon, (_, _) => Dispatcher.InvokeAsync(async () =>
                { if (_mainWindow is not null) await _mainWindow.SetPausedAsync(option, null); RefreshTrayState(); }));
            item.ToolTipText = option.Detail;
        }
        _trayPauseItem.DropDownItems.Add("Resume Sync", _resumeMenuIcon, (_, _) => Dispatcher.InvokeAsync(async () =>
        { if (_mainWindow is not null) await _mainWindow.ResumePausedAsync(null); RefreshTrayState(); }));
        menu.Items.Add(_trayPauseItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitProduct));
        _baseTrayIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _baseTrayIcon,
            Text = FormatTrayTooltip("Starting Sync Shark"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button != System.Windows.Forms.MouseButtons.Left) return;
            Dispatcher.Invoke(ShowTrayFlyout);
        };
        if (_mainWindow is not null) {
            _mainWindow.WorkspacePresentationChanged += (_, _) => RefreshTrayState();
            // Update controls live on Settings > About & Updates; the old Settings > Diagnostics route
            // no longer exists, so the notification must not send anyone to an empty page.
            _mainWindow.UpdateAvailable += version => Notify("Sync Shark Update Available", "Version " + version + " is available. Open Settings > About & Updates to download, check and install when ready.", false);
        }
        RefreshTrayState();
    }

    private void ShowTrayFlyout()
    {
        if (_mainWindow is null) return;
        _flyout ??= new TrayFlyoutWindow(_mainWindow);
        _flyout.ToggleAtTray();
    }

    internal static System.Drawing.Bitmap CreateNativePauseIcon(string kind)
    {
        var image = new System.Drawing.Bitmap(16, 16);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        using var ink = new System.Drawing.SolidBrush(System.Drawing.SystemColors.MenuText);
        if (kind == "stop") graphics.FillRectangle(ink, 3, 3, 10, 10);
        else if (kind == "play") graphics.FillPolygon(ink, new System.Drawing.Point[]
            { new System.Drawing.Point(4, 2), new System.Drawing.Point(13, 8), new System.Drawing.Point(4, 14) });
        else
        {
            graphics.FillRectangle(ink, 3, 3, 3, 10);
            graphics.FillRectangle(ink, 10, 3, 3, 10);
        }
        return image;
    }

    private void RefreshTrayState()
    {
        if (_mainWindow is null || _trayStatusItem is null || _trayPauseItem is null) return;
        try
        {
            _trayStatusItem.Text = _mainWindow.TrayStatus;
            if (_trayIcon is not null) _trayIcon.Text = FormatTrayTooltip(_mainWindow.TrayStatus);
            _trayPauseItem.Text = _mainWindow.TrayPaused ? "Resume Sync" : "Pause Sync";
            _trayPauseItem.Image = _mainWindow.TrayPaused ? _resumeMenuIcon : _pauseMenuIcon;
            _trayPauseItem.ToolTipText = "All accounts. " + _mainWindow.PauseDetail;
            var state = !_mainWindow.TrayReady ? "starting" : _mainWindow.TrayPaused ? "paused"
                : _mainWindow.TrayAttention.Count > 0 ? "attention"
                    : _mainWindow.TrayRunActive || _mainWindow.TrayAutoRunActive
                        ? SystemParameters.ClientAreaAnimation && DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 2 % 2 == 1
                            ? "working-light" : "working"
                        : "idle";
            if (_trayState != state && _trayIcon is not null && _baseTrayIcon is not null)
            {
                var previous = _stateTrayIcon;
                _stateTrayIcon = CreateStateIcon(_baseTrayIcon, state); _trayIcon.Icon = _stateTrayIcon;
                _trayState = state; previous?.Dispose();
            }
            var preferences = _mainWindow.WorkspacePreferences;
            var attention = string.Join('|', _mainWindow.TrayAttention.Select(pair => pair.Id + pair.AttentionReason));
            if (attention != _attentionNotification)
            {
                _attentionNotification = attention;
                if (attention.Length > 0 && preferences.NotifyProblems)
                    Notify("Needs Attention", $"{_mainWindow.TrayAttention.Count} folder pair(s) need your input. Open Sync Shark for details.", true);
            }
            // SS-D02: the notification is bound to the newest run's CLASSIFIED outcome, never to its
            // completion timestamp. Only a real success says "Sync Completed"; a stopped, partial,
            // unverified or unrecorded run states its own truthful attention wording instead.
            if (_mainWindow.LatestRunForTray is { } latestRun && latestRun.CompletedUtc != _notifiedRun)
            {
                _notifiedRun = latestRun.CompletedUtc;
                if (latestRun.CompletedUtc > _notificationSessionStarted && preferences.NotifyCompleted)
                    Notify(latestRun.Outcome.Headline, latestRun.Outcome.NotificationDetail, latestRun.Outcome.Attention);
            }
            if (_mainWindow.LastWorkspaceFailure is { } failure && failure != _lastFailureNotification)
            { _lastFailureNotification = failure; if (preferences.NotifyTransfers) Notify("Transfer Needs Attention", "A transfer could not continue safely. Open Activity for the exact item and reason.", true); }
        }
        catch
        {
            _trayStatusItem.Text = "Status: needs attention";
        }
    }

    internal static string FormatTrayTooltip(string status)
    {
        var text = $"Sync Shark\n{status}";
        return text[..Math.Min(63, text.Length)];
    }

    private string? _lastFailureNotification;
    private void Notify(string title, string message, bool warning)
    {
        if (_trayIcon is null || _mainWindow is null) return;
        _trayIcon.BalloonTipTitle = title; _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(5000);
        if (_mainWindow.WorkspacePreferences.NotificationSound) System.Media.SystemSounds.Asterisk.Play();
    }

    private static System.Drawing.Icon CreateStateIcon(System.Drawing.Icon original, string state)
    {
        return TrayStatusIcons.Create(original, state);
    }

    private void ShowMainWindow()
    {
        _mainWindow?.ShowFromBackground();
    }

    private async void ExitProduct()
    {
        try
        {
            if (_mainWindow is not null) await _mainWindow.PrepareProductExitAsync();
            Shutdown();
        }
        catch (Exception exception)
        {
            AppDialogWindow.Inform(_mainWindow, "Sync Shark is still running",
                "The safe exit checkpoint did not finish.", exception.Message);
            RefreshTrayState();
        }
    }
}
