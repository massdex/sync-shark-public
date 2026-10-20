using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using StackPanel = System.Windows.Controls.StackPanel;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private sealed record CheckedUpdate(string Installer, string Hash, string Version, string Previous);
    private CheckedUpdate? _checkedUpdate;
    internal bool CliUpdateExitPrepared { get; private set; }
    private TextBlock _updateStatus = null!;
    private Button _updateCheck = null!, _updateInstall = null!;
    private Button _updateDownload = null!, _updateAutomatic = null!;
    private TextBlock _updateFolderText = null!;
    private TextBlock _updateChecksState = null!;
    private readonly System.Windows.Threading.DispatcherTimer _updateDiscoveryTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly SyncPublicUpdates _publicUpdates = new();
    private SyncPublicUpdate? _onlineUpdate;
    private SyncDownloadedUpdate? _downloadedUpdate;
    private bool _checkingDeliveredUpdates;
    internal event Action<string>? UpdateAvailable;

    internal static string UpdateVersionIdentity =>
        FileVersionInfo.GetVersionInfo(typeof(MainWindow).Assembly.Location).ProductVersion ?? "Unknown";

    private UIElement BuildUpdateSettings()
    {
        var rows = new StackPanel();
        // Inline order: current state first, then the one primary action, then the automatic-check
        // preference, then the optional offline/local-package actions. Product identity lives on the
        // About & Updates identity row above these controls, so it is not repeated here.
        _updateStatus = Text("Check for a new release without signing in. Downloads are checked before installation. Updates are never installed automatically.", 12,
            brush: "MutedInk"); _updateStatus.MaxWidth = 380;
        rows.Children.Add(SettingRow("Update Status", "Check progress and result, including any failure. Nothing is downloaded or installed until you confirm it.", _updateStatus));
        _updateCheck = AsyncButton("Check for Updates", async (_, _) => await DiscoverDeliveredUpdateAsync(false), true);
        _updateCheck.Style = (Style)FindResource("PrimaryButton");
        // UI354-TTIP1: concise help that describes the real effect instead of repeating the caption.
        WithUpdateHelp(_updateCheck, "Reads the public release channel and your local update folder. Nothing is downloaded or installed.");
        rows.Children.Add(SettingRow("Check for Updates", "Look for a verified public release, or for a package in your local update folder. This only reads release information.", _updateCheck));
        _updateDownload = AsyncButton("Download and Check", async (_, _) => await DownloadOnlineUpdateAsync());
        _updateDownload.Visibility = Visibility.Collapsed;
        WithUpdateHelp(_updateDownload, "Downloads the newer release and verifies its signature. Nothing is installed until you confirm it.");
        rows.Children.Add(ActionRow("Download and Check", "Download the newer release and verify its signature before installing.", _updateDownload));
        _updateInstall = AsyncButton("Install and Restart", async (_, _) => await ApplyCheckedUpdateAsync());
        _updateInstall.Visibility = Visibility.Collapsed;
        WithUpdateHelp(_updateInstall, "Keeps the previous version and its state checkpoint for rollback, then installs and reopens Sync Shark. Accounts, folder pairs and settings are preserved.");
        rows.Children.Add(ActionRow("Install and Restart", "Keep the previous version and its state checkpoint for rollback, then install and reopen Sync Shark. Accounts, folder pairs and settings are preserved.", _updateInstall));
        _updateChecksState = Text("", 13, margin: new Thickness(0, 0, 12, 0));
        _updateAutomatic = AsyncButton("Pause Update Checks", async (_, _) =>
        {
            await _workspacePreferencesGate.WaitAsync();
            try { var saved = _workspacePreferencesStore.Load();
                _workspacePreferences = saved with { AutomaticUpdateChecks = !saved.AutomaticUpdateChecks };
                await _workspacePreferencesStore.SaveAsync(_workspacePreferences); }
            finally { _workspacePreferencesGate.Release(); }
            RefreshUpdateFolderLabel();
        }, true);
        var automatic = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        automatic.Children.Add(_updateChecksState); automatic.Children.Add(_updateAutomatic);
        rows.Children.Add(SettingRow("Automatic Checks", "Check the public release channel once an hour while Sync Shark is running. This only reads release information.", automatic));
        rows.Children.Add(SettingRow("Check Downloaded Package", "Verify one update installer you already have, then install it with the same confirmation. Use this only when the package is already on this computer.",
            WithUpdateHelp(AsyncButton("Check Downloaded Package", async (_, _) => await ChooseUpdateAsync(), true),
                "Verifies one update installer you already have before its confirmation. Nothing is installed until you confirm, and no account or folder changes.")));
        rows.Children.Add(SettingRow("Choose Local Update Folder", "Choose the folder where delivered update packages are kept.",
            WithUpdateHelp(AsyncButton("Choose Local Update Folder", async (_, _) => await ChooseUpdateFolderAsync(), true),
                "Selects the folder where delivered update packages are kept. It changes no account, folder pair or sync setting.")));
        _updateFolderText = Text("", 13); _updateFolderText.MaxWidth = 380;
        rows.Children.Add(SettingRow("Local Update Folder", "Delivered packages are verified from this folder before installation.", _updateFolderText));
        rows.Children.Add(SettingRow("Update Channel", "Signed public releases only; updates are never installed automatically.",
            Text("Public Signed Releases", 13)));
        rows.Children.Add(SettingRow("Release Source", "The public repository each release is verified against.", Text(SyncPublicUpdates.Repository, 13)));
        RefreshUpdateFolderLabel();
        _updateDiscoveryTimer.Tick += async (_, _) => await DiscoverDeliveredUpdateAsync(true);
        Loaded += async (_, _) => { if (ExplorerIntegration.IsInstalledProcess) { _updateDiscoveryTimer.Start(); await DiscoverDeliveredUpdateAsync(true); } };
        Closed += (_, _) => { _updateDiscoveryTimer.Stop(); _updateLifetime.Cancel();
            if (!_checkingDeliveredUpdates) { try { _downloadedUpdate?.Dispose(); } catch (IOException) { } }
            _publicUpdates.Dispose(); };
        return rows;
    }

    /// <summary>
    /// UI354-TTIP1: update actions publish concise help that states their real effect, so a control's
    /// tooltip and accessible help never merely repeat its caption. No effect is added or changed.
    /// </summary>
    private static Button WithUpdateHelp(Button button, string help)
    {
        button.ToolTip = help;
        System.Windows.Automation.AutomationProperties.SetHelpText(button, help);
        return button;
    }

    // An action row is shown exactly when its action is available, so a hidden
    // update action is never left behind as an empty settings row.
    private static FrameworkElement ActionRow(string title, string description, Button action)
    {
        var row = SettingRow(title, description, action);
        row.SetBinding(UIElement.VisibilityProperty,
            new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = action });
        return row;
    }

    private void RefreshUpdateFolderLabel()
    {
        var saved = _workspacePreferencesStore.Load();
        _updateChecksState.Text = saved.AutomaticUpdateChecks ? "Every Hour" : "Paused";
        _updateFolderText.Text = saved.UpdateDeliveryFolder is { Length: > 0 } folder ? folder : "None selected";
        var label = saved.AutomaticUpdateChecks ? "Pause Update Checks" : "Resume Update Checks";
        _updateAutomatic.Content = CommandIcons.Label(label, saved.AutomaticUpdateChecks ? "pause" : "play");
        System.Windows.Automation.AutomationProperties.SetName(_updateAutomatic, label);
        // UI354-TTIP1: Pause and Resume affect only automatic update checks, never file syncing.
        var automaticHelp = saved.AutomaticUpdateChecks
            ? "Stops the hourly automatic update check. File syncing, folder pairs and accounts are not affected."
            : "Resumes the hourly automatic update check. File syncing, folder pairs and accounts are not affected.";
        _updateAutomatic.ToolTip = automaticHelp;
        System.Windows.Automation.AutomationProperties.SetHelpText(_updateAutomatic, automaticHelp);
    }

    private async Task ChooseUpdateFolderAsync()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the Folder Containing Delivered Sync Shark Updates", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        await _workspacePreferencesGate.WaitAsync();
        try {
            var saved = _workspacePreferencesStore.Load() with { UpdateDeliveryFolder = Path.GetFullPath(picker.FolderName) };
            await _workspacePreferencesStore.SaveAsync(saved); _workspacePreferences = saved;
        }
        finally { _workspacePreferencesGate.Release(); }
        RefreshUpdateFolderLabel(); await DiscoverDeliveredUpdateAsync(false);
    }

    private async Task DiscoverDeliveredUpdateAsync(bool quiet)
    {
        if (_checkingDeliveredUpdates || _updateLifetime.IsCancellationRequested) return;
        var saved = _workspacePreferencesStore.Load();
        if (quiet && !saved.AutomaticUpdateChecks) return;
        var folder = saved.UpdateDeliveryFolder;
        _checkingDeliveredUpdates = true; _updateCheck.IsEnabled = _updateInstall.IsEnabled = false;
        try
        {
            if (!quiet) _updateStatus.Text = "Checking for Updates…";
            if (_downloadedUpdate is null && saved.DownloadedUpdateFolder is { } retained)
            {
                retained = SyncDownloadedUpdate.RequireDownloadDirectory(retained);
                // A crash or install may have left the single recorded download. Recheck
                // it with the installed verifier; never infer trust from its folder name.
                var candidatesInCache = await Task.Run(() => Directory.Exists(retained)
                    ? SyncUpdateDiscovery.FindCandidates(retained, UpdateVersionIdentity) : [], _updateLifetime.Token);
                if (candidatesInCache.Count == 1)
                {
                    try { ShowCheckedUpdate(await VerifyUpdateAsync(candidatesInCache[0].InstallerPath, _updateLifetime.Token)); }
                    catch (Exception error) when (error is not OperationCanceledException) { await CleanupDownloadedUpdateAsync(); }
                }
                else await CleanupDownloadedUpdateAsync();
            }
            var candidates = string.IsNullOrWhiteSpace(folder) ? [] :
                await Task.Run(() => SyncUpdateDiscovery.FindCandidates(folder, UpdateVersionIdentity), _updateLifetime.Token);
            foreach (var candidate in candidates.Take(8))
            {
                try
                {
                    var update = await VerifyUpdateAsync(candidate.InstallerPath, _updateLifetime.Token);
                    ShowCheckedUpdate(update);
                    await NotifyUpdateAsync(update.Version);
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException) { if (!quiet) _updateStatus.Text = "A delivered package was rejected: " + error.Message; }
            }
            _onlineUpdate = await _publicUpdates.FindAsync(UpdateVersionIdentity, _updateLifetime.Token);
            _updateDownload.Visibility = _onlineUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            if (_onlineUpdate is { } online)
            {
                if (_checkedUpdate?.Version == online.Version) _updateDownload.Visibility = Visibility.Collapsed;
                else _updateStatus.Text = "New Release: " + online.Version + "\nDownload Size: " +
                    (online.ArchiveBytes / 1048576d).ToString("0.0") + " MB\nDownload and check its signature before installing.";
                await NotifyUpdateAsync(online.Version);
            }
            else if (!quiet && _checkedUpdate is null) _updateStatus.Text = "No newer compatible public release is available. Nothing was installed.";
        }
        catch (Exception error) when (error is not OperationCanceledException) { if (!quiet) _updateStatus.Text = "Update Check Could Not Finish: " + error.Message; }
        catch (OperationCanceledException) { }
        finally { _checkingDeliveredUpdates = false; _updateCheck.IsEnabled = _updateInstall.IsEnabled = true; }
    }

    private async Task NotifyUpdateAsync(string version)
    {
        await _workspacePreferencesGate.WaitAsync(_updateLifetime.Token);
        try
        {
            var saved = _workspacePreferencesStore.Load();
            if (saved.LastNotifiedUpdateVersion == version) return;
            _workspacePreferences = saved with { LastNotifiedUpdateVersion = version };
            await _workspacePreferencesStore.SaveAsync(_workspacePreferences, _updateLifetime.Token);
        }
        finally { _workspacePreferencesGate.Release(); }
        UpdateAvailable?.Invoke(version);
    }

    private async Task RememberDownloadedUpdateAsync(string? folder)
    {
        await _workspacePreferencesGate.WaitAsync();
        try { _workspacePreferences = _workspacePreferencesStore.Load() with { DownloadedUpdateFolder = folder };
            await _workspacePreferencesStore.SaveAsync(_workspacePreferences); }
        finally { _workspacePreferencesGate.Release(); }
    }

    private async Task CleanupDownloadedUpdateAsync()
    {
        var retained = _workspacePreferencesStore.Load().DownloadedUpdateFolder;
        if (_downloadedUpdate is { } download) { await Task.Run(download.Dispose); _downloadedUpdate = null; }
        else if (retained is not null) await Task.Run(() => SyncDownloadedUpdate.CleanupRetained(retained));
        await RememberDownloadedUpdateAsync(null);
    }

    private async Task DownloadOnlineUpdateAsync()
    {
        if (_checkingDeliveredUpdates || _onlineUpdate is not { } online) return;
        _checkingDeliveredUpdates = true;
        _updateCheck.IsEnabled = _updateDownload.IsEnabled = _updateInstall.IsEnabled = false;
        _checkedUpdate = null; _updateInstall.Visibility = Visibility.Collapsed;
        try
        {
            await CleanupDownloadedUpdateAsync();
            _updateStatus.Text = "Downloading Version " + online.Version + "…";
            _downloadedUpdate = await _publicUpdates.DownloadAsync(online, UpdateVersionIdentity, _updateLifetime.Token, RememberDownloadedUpdateAsync);
            var installer = _downloadedUpdate.Installer;
            // The verifier owns immutable read leases from its first check through
            // execution. Release the allocation's DELETE-capable creation leases
            // before that independent process acquires its no-delete read guards.
            // The existing saved download pointer remains the crash/cleanup owner.
            _downloadedUpdate.ReleaseToInstaller(); _downloadedUpdate = null;
            _updateStatus.Text = "Checking Signature, Package Contents and Compatibility…";
            var update = await VerifyUpdateAsync(installer, _updateLifetime.Token);
            ShowCheckedUpdate(update); _updateDownload.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _updateStatus.Text = "Update Not Ready: " + error.Message + " You can retry; the installed app was not changed."; }
        finally
        {
            if (_updateLifetime.IsCancellationRequested) { try { _downloadedUpdate?.Dispose(); } catch (IOException) { } }
            _checkingDeliveredUpdates = false; _updateCheck.IsEnabled = _updateDownload.IsEnabled = _updateInstall.IsEnabled = true;
        }
    }

    private void ShowCheckedUpdate(CheckedUpdate update)
    {
        _checkedUpdate = update;
        _updateStatus.Text = "Update Available: " + update.Version + "\nSignature and Compatibility Verified\nSigned and locally pinned; not a Windows-trusted publisher";
        _updateInstall.Visibility = Visibility.Visible;
    }

    private static async Task<CheckedUpdate> VerifyUpdateAsync(string installer, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(installer);
        var volume = Path.GetPathRoot(path) ?? throw new IOException("The update installer has no local drive.");
        var drive = new DriveInfo(volume);
        if (volume.Length != 3 || drive.DriveType != DriveType.Fixed ||
            !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Copy the complete update package onto a local NTFS drive before checking it.");
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
            if (PilotPath.IsUnsafeAttributes(File.GetAttributes(directory)))
                throw new IOException("Update inputs cannot use redirected or offline folders.");
        if (PilotPath.IsUnsafeAttributes(File.GetAttributes(path)))
            throw new IOException("The update installer must already be an ordinary on-disk file.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > SyncPublicUpdates.MaxInstallerBytes)
            throw new InvalidDataException("The update installer exceeds its inspection size bound.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
        var current = UpdateVersionIdentity;
        using var process = await StartUpdateProcessAsync(path, hash, current, false);
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Length == 0 ? "Package verification failed." : error.Trim());
        var json = output.Split('\n').LastOrDefault(line => line.TrimStart().StartsWith("{\"ok\":", StringComparison.Ordinal))
            ?? throw new InvalidDataException("The update checker did not return a verified result.");
        using var document = JsonDocument.Parse(json); var result = document.RootElement;
        if (!result.GetProperty("ok").GetBoolean() || result.GetProperty("installerSha256").GetString() != hash)
            throw new InvalidDataException("The verified update identity changed.");
        return new(path, hash, result.GetProperty("version").GetString()!, current);
    }

    private async Task ChooseUpdateAsync()
    {
        if (_checkingDeliveredUpdates) { _updateStatus.Text = "An update check is already in progress."; return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a Sync Shark Update",
            Filter = "Sync Shark Installer|Install-SyncShark.ps1", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (_checkingDeliveredUpdates) return;
        _checkingDeliveredUpdates = true;
        _checkedUpdate = null; _updateInstall.Visibility = Visibility.Collapsed; _updateCheck.IsEnabled = false;
        _updateStatus.Text = "Checking Package, Signature and Version Compatibility…";
        try
        {
            ShowCheckedUpdate(await VerifyUpdateAsync(picker.FileName, _updateLifetime.Token));
        }
        catch (Exception error) { _updateStatus.Text = "Update Not Ready: " + error.Message; }
        finally { _checkingDeliveredUpdates = false; _updateCheck.IsEnabled = true; }
    }

    private async Task PrepareRemovalAsync()
    {
        if (!ExplorerIntegration.IsInstalledProcess)
        { ShowStatus("Removal preparation is available only in the signed installed app."); return; }
        if (_runActive || Enumerable.Range(0, 3).Any(SettingsCategoryDirty) || PairLocationDraftActive && CurrentPairEditorHasUnsavedChanges())
        { ShowStatus("Finish the current sync and save or discard drafts before preparing removal."); return; }
        if (!AppDialogWindow.Confirm(this, "Uninstall Sync Shark", "Make managed files local, then uninstall Sync Shark?",
            "This can download online-only content and needs enough disk space. Automatic sync and Windows startup will be turned off. An administrator prompt removes Sync Shark's protected installation and Fast Discovery. Your cloud files, local files, accounts, saved pairs, settings and recovery data are kept. One exact app copy is retained for undo, which can also require administrator approval. If a file cannot be made local, uninstall stops and the app remains available.", "Uninstall Sync Shark")) return;
        try
        {
            await StartRemovalHandoffAsync(CancellationToken.None);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception error) { ShowStatus("Removal Not Ready: " + error.Message + " Automatic sync remains paused; do not remove the app yet."); }
    }

    private async Task StartRemovalHandoffAsync(CancellationToken cancellationToken)
    {
        if (!ExplorerIntegration.IsInstalledProcess)
            throw new InvalidOperationException("Removal preparation requires the signed installed app.");
        RequireCleanCliSettingsDrafts();
        cancellationToken.ThrowIfCancellationRequested();
        _automationTimer.Stop();
        try
        {
            await SetSyncPauseAsync(null, true);
            await _authorityHost.StopMonitorsAsync();
            _pairSet = await _authorityHost.LoadPairsAsync();
            foreach (var pair in _pairSet.Pairs.Where(pair => pair.FilesOnDemandEnabled).ToArray())
            {
                ShowStatus("Making Files Local: " + pair.Name);
                _pairSet = await _authorityHost.DisableFilesOnDemandAsync(pair.Id);
                ExplorerIntegration.UnregisterRoot(pair);
            }
            foreach (var pair in _pairSet.Pairs.Where(pair => !pair.FilesOnDemandEnabled))
                ExplorerIntegration.UnregisterRoot(pair);
            await ExplorerPackage.RemoveAsync();
            ExplorerIntegration.UnregisterCommands();
            WindowsStartupRegistration.SetEnabled(false);
            await _authorityHost.SaveAutomationAsync(_authorityHost.LoadAutomation() with { Enabled = false, StartWithWindows = false });
            await _workspacePreferencesGate.WaitAsync();
            try
            {
                _workspacePreferences = _workspacePreferencesStore.Load() with { ExplorerIntegrationEnabled = false };
                await _workspacePreferencesStore.SaveAsync(_workspacePreferences);
            }
            finally { _workspacePreferencesGate.Release(); }
            RefreshPairRows(_pairSet.SelectedPairId); RefreshWorkspace();
            using var process = await StartReleaseProcessAsync("SyncShark.Uninstall.ps1",
                "Invoke-SyncSharkUninstall -CurrentVersion " + QuotePowerShell(UpdateVersionIdentity) + " -ParentProcessId " + Environment.ProcessId, true);
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ready = false;
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                    if (line.Trim() == "{\"ready\":true}") { ready = true; break; }
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw new IOException("Uninstall preparation did not finish. The app remains available.");
            }
            if (!ready) throw new IOException("Uninstall preparation failed: " + (await errors).Trim());
            _ = process.StandardOutput.ReadToEndAsync();
            try { await PrepareProductExitAsync(); }
            catch
            {
                // This owned launcher is still waiting for the current process to exit.
                // A failed checkpoint must not leave a surprise removal armed for a later exit.
                if (!process.HasExited) process.Kill();
                throw;
            }
        }
        catch (Exception error) { throw new IOException("Removal preparation did not complete. Automatic sync remains paused; the app has not been uninstalled.", error); }
    }

    private async Task RestoreExplorerIntegrationAsync(bool propagateErrors = false)
    {
        if (!ExplorerIntegration.IsInstalledProcess)
        {
            if (propagateErrors) throw new InvalidOperationException("Use the signed installed app to restore Explorer integration.");
            ShowStatus("Use the signed installed app to restore Explorer integration."); return;
        }
        await _workspacePreferencesGate.WaitAsync();
        try
        {
            _workspacePreferences = _workspacePreferencesStore.Load() with { ExplorerIntegrationEnabled = true };
            await _workspacePreferencesStore.SaveAsync(_workspacePreferences);
        }
        finally { _workspacePreferencesGate.Release(); }
        _explorerRegistrationFingerprint = ""; await RefreshExplorerIntegrationAsync(propagateErrors);
    }

    private async Task ApplyCheckedUpdateAsync()
    {
        if (_checkingDeliveredUpdates) { _updateStatus.Text = "Wait for the update check to finish before installing."; return; }
        if (_checkedUpdate is not { } update) return;
        if (!ExplorerIntegration.IsInstalledProcess) { _updateStatus.Text = "Install updates from the signed installed application."; return; }
        if (_runActive) { _updateStatus.Text = "Finish or stop the current sync before installing the update."; return; }
        if (Enumerable.Range(0, 3).Any(SettingsCategoryDirty) || PairLocationDraftActive && CurrentPairEditorHasUnsavedChanges())
        { _updateStatus.Text = "Save or discard your unsaved changes before installing the update."; return; }
        if (!AppDialogWindow.Confirm(this, "Install Sync Shark Update", "Install version " + update.Version + " and restart?",
            "Sync Shark will stop safely, verify the package again, preserve your current version and state for rollback, then reopen. Windows may ask for administrator approval to update the protected program files. No account or folder is removed.", "Install and Restart")) return;
        try
        {
            await StartCheckedUpdateHandoffAsync(update, CancellationToken.None);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception error) { _updateStatus.Text = "Update Could Not Start: " + error.Message; }
    }

    internal async Task<object> VerifyOrApplyLocalUpdateFromCliAsync(SyncCliInvocation command, CancellationToken cancellationToken)
    {
        if (!ExplorerIntegration.IsInstalledProcess)
            throw new SyncCliUnsupportedException("Use the signed installed app to verify or install an update. No package was executed.");
        if (command.Path is not { Length: > 0 } path || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), "Install-SyncShark.ps1", StringComparison.OrdinalIgnoreCase))
            throw new SyncCliUsageException("Supply --path with the full path to Install-SyncShark.ps1.");
        if (_checkingDeliveredUpdates) throw new InvalidOperationException("An update operation is already running.");
        if (command.Command == "updates apply") RequireUpdateHandoffReady();
        _checkingDeliveredUpdates = true;
        try
        {
            var update = await VerifyUpdateAsync(path, cancellationToken);
            if (command.Command == "updates verify")
                return new { verified = true, update.Version, installer = update.Installer,
                    note = "Package signature, exact hash and compatibility checked by the installed verifier; not installed." };
            if (!command.Yes || !string.Equals(update.Version, command.Update, StringComparison.Ordinal))
                throw new SyncCliUsageException("The verified version must match --update and --yes is required. Nothing was installed.");
            await StartCheckedUpdateHandoffAsync(update, cancellationToken);
            CliUpdateExitPrepared = true;
            return new SyncCliControlData("updates apply", true, "The exact local package passed verification and the installer acknowledged its handoff. The app will exit; installation and any Windows approval remain pending.");
        }
        finally { _checkingDeliveredUpdates = false; }
    }

    internal async Task<SyncCliControlData> ApplyRetainedUpdateFromCliAsync(string version, CancellationToken cancellationToken)
    {
        if (_checkingDeliveredUpdates) throw new InvalidOperationException("An update operation is already running.");
        RequireUpdateHandoffReady();
        _checkingDeliveredUpdates = true;
        try
        {
            var retained = _workspacePreferencesStore.Load().DownloadedUpdateFolder ??
                throw new InvalidOperationException("No downloaded package is retained. Run updates check --download first.");
            SyncDownloadedUpdate.RequireDownloadDirectory(retained);
            var candidates = SyncUpdateDiscovery.FindCandidates(retained, UpdateVersionIdentity);
            if (candidates.Count != 1)
                throw new InvalidDataException("The retained download does not contain exactly one compatible installer.");
            var update = await VerifyUpdateAsync(candidates[0].InstallerPath, cancellationToken);
            if (!string.Equals(update.Version, version, StringComparison.Ordinal))
                throw new InvalidDataException("The retained verified version does not match --update. Nothing was installed.");
            cancellationToken.ThrowIfCancellationRequested();
            await StartCheckedUpdateHandoffAsync(update, cancellationToken);
            CliUpdateExitPrepared = true;
            // App stops only after the authenticated bridge delivers this result. The existing signed
            // installer remains responsible for Windows approval, replacement, rollback and restart.
            return new SyncCliControlData("updates apply", true, "Version " + version +
                " passed the installed verifier and the installer acknowledged its handoff. Sync Shark has " +
                "checkpointed and will exit. Installation and any Windows approval are still pending.");
        }
        finally { _checkingDeliveredUpdates = false; }
    }

    private void RequireUpdateHandoffReady()
    {
        if (!ExplorerIntegration.IsInstalledProcess)
            throw new InvalidOperationException("Use the signed installed application to apply updates.");
        if (_runActive) throw new InvalidOperationException("Finish or stop the current sync before installing an update.");
        if (Enumerable.Range(0, 3).Any(SettingsCategoryDirty) || PairLocationDraftActive && CurrentPairEditorHasUnsavedChanges())
            throw new InvalidOperationException("Save or discard unsaved changes before installing an update.");
    }

    private async Task StartCheckedUpdateHandoffAsync(CheckedUpdate update, CancellationToken cancellationToken)
    {
        RequireUpdateHandoffReady();
        cancellationToken.ThrowIfCancellationRequested();
        // Shared by the button and CLI. The fixed embedded bridge verifies again, then waits for this
        // exact process to exit. Merely releasing download handles is never an installer handoff.
        using var process = await StartUpdateProcessAsync(update.Installer, update.Hash, update.Previous, true);
        try
        {
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var ready = false;
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                    if (line.Trim() == "{\"ready\":true}") { ready = true; break; }
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw new IOException("Update verification did not finish; the current application remains open.");
            }
            if (!ready) throw new IOException("Update verification failed: " + (await errors).Trim());
            _ = process.StandardOutput.ReadToEndAsync();
            _downloadedUpdate?.ReleaseToInstaller(); _downloadedUpdate = null;
            await PrepareProductExitAsync();
        }
        catch
        {
            // This owned verifier/launcher is still waiting for this process. Do not leave it armed
            // after a failed checkpoint to perform a surprise update at some unrelated later exit.
            if (!process.HasExited) process.Kill();
            throw;
        }
    }

    private static async Task<Process> StartUpdateProcessAsync(string path, string hash, string current, bool apply)
    {
        return await StartReleaseProcessAsync("SyncShark.Update.ps1", "Invoke-SyncSharkUpdate -Installer " + QuotePowerShell(path) +
            " -ExpectedInstallerHash " + QuotePowerShell(hash) + " -CurrentVersion " + QuotePowerShell(current) +
            " -ParentProcessId " + Environment.ProcessId + (apply ? " -Apply" : ""), apply);
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task<Process> StartReleaseProcessAsync(string component, string invocation, bool showFailure)
    {
        static string Resource(string name)
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("The installed update component is missing.");
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        var candidates = new[] {
            Environment.GetEnvironmentVariable("SYNCSHARK_PWSH_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries).Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe")))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        var runtime = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            ?? throw new InvalidOperationException("PowerShell was not found. Set SYNCSHARK_PWSH_PATH to an installed PowerShell executable.");
        var start = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-" }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException("The trusted update checker could not start.");
        var failure = component == "SyncShark.Uninstall.ps1"
            ? "if($script:SyncSharkReleaseReady){Show-SyncSharkUpdateFailure $_.Exception.Message 'Sync Shark Uninstall Not Completed'}else{[Console]::Error.WriteLine($_.Exception.Message)}; "
            : showFailure ? "Show-SyncSharkUpdateFailure $_.Exception.Message; " : "[Console]::Error.WriteLine($_.Exception.Message); ";
        var script = "$ErrorActionPreference='Stop'\n$ProgressPreference='SilentlyContinue'\nif(Get-Variable PSStyle -ErrorAction SilentlyContinue){$PSStyle.OutputRendering='PlainText'}\n$script:SyncSharkReleaseReady=$false\n" +
            "New-Module -ScriptBlock {\n" + Resource("SyncShark.Release.psm1") + "\n} | Import-Module\n\n" +
            Resource("SyncShark.Update.ps1") + "\n" + (component == "SyncShark.Update.ps1" ? "" : Resource(component)) +
            "\n\ntry { " + invocation + " } catch { " +
            failure + "exit 1 }\n";
        await process.StandardInput.WriteAsync(script); process.StandardInput.Close(); return process;
    }
}
