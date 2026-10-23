using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private TextBlock _discoveryStatus = null!;
    private bool _discoverySetupActive;
    private CancellationTokenSource? _discoveryCheckCancellation;
    private System.Windows.Controls.Button? _cancelDiscoveryCheck;

    // Presentation only: the Discovery capability, its two status/setup rows and its read-only check
    // are unchanged. Restore and Remove move behind one advanced maintenance expander so the normal
    // rows stay status and setup, and every action reports its result on this page.
    private UIElement BuildDiscoverySettings()
    {
        var rows = new System.Windows.Controls.StackPanel();
        _discoveryStatus = Text(SyncFastDiscovery.Status, 12, brush: "MutedInk"); _discoveryStatus.MaxWidth = 380;
        rows.Children.Add(SettingRow("Status", "Whether the protected Discovery helper and its on-demand task are registered. Ordinary scanning stays available either way.", _discoveryStatus));
        rows.Children.Add(SettingRow("Set Up or Update", "On a Windows administrator account, one setup lets SyncSharkDiscovery.exe run when needed without a prompt for each sync. Sync Shark itself stays unelevated and ordinary scanning remains available.",
            ScopedAction("Set Up or Update",
                "Installs the signed read-only Discovery helper and one on-demand Windows task after a single administrator approval. Ordinary scanning stays available and Sync Shark remains unelevated.",
                async (_, _) => await ConfigureDiscoveryAsync("Configure"))));
        var check = AsyncButton("Check Selected Pair", async (_, _) => await CheckDiscoveryAsync(), true);
        check.ToolTip = "Check read-only file metadata for the pair selected in Folder Pairs. No files are synced or changed.";
        _cancelDiscoveryCheck = Button("Cancel Check", (_, _) => _discoveryCheckCancellation?.Cancel());
        _cancelDiscoveryCheck.Visibility = _discoveryCheckCancellation is null ? Visibility.Collapsed : Visibility.Visible;
        var checkActions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        checkActions.Children.Add(check); checkActions.Children.Add(_cancelDiscoveryCheck);
        rows.Children.Add(SettingRow("Check File Metadata", "Read-only check for the pair selected in Folder Pairs. No files are synced or changed.", checkActions));
        var maintenance = new System.Windows.Controls.StackPanel();
        maintenance.Children.Add(SettingRow("Restore Previous Setup", "Restore the previous protected helper and its matching app identities. If it does not match the installed app, normal scanning continues and nothing is replaced.",
            ScopedAction("Restore Previous Setup",
                "Restores the previous protected helper and its matching app identities. Nothing is replaced when it does not match the installed app.",
                async (_, _) => await ConfigureDiscoveryAsync("Restore"))));
        maintenance.Children.Add(SettingRow("Remove Setup", "Remove the fixed Discovery task and protected helper. Your files, accounts and sync settings stay unchanged; normal scanning continues.",
            ScopedAction("Remove Setup",
                "Removes only Sync Shark's fixed Discovery task and protected helper. Your files, accounts and sync settings are unchanged.",
                async (_, _) => await ConfigureDiscoveryAsync("Remove"))));
        var advanced = new Expander { Header = CommandIcons.Label("Advanced Maintenance", "settings"), Content = maintenance, Margin = new Thickness(0, 16, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(advanced, "Advanced Fast File Discovery maintenance");
        System.Windows.Automation.AutomationProperties.SetHelpText(advanced, "Restore or remove the Fast File Discovery setup. Only Sync Shark's own helper and task change.");
        rows.Children.Add(advanced);
        return rows;
    }

    private async Task CheckDiscoveryAsync()
    {
        if (!ExplorerIntegration.IsInstalledProcess) { ShowStatus("Use the signed installed app to check Fast File Discovery."); return; }
        if (_discoverySetupActive || _runActive) { ShowStatus("Finish the current sync or setup operation first."); return; }
        var pair = _pairSet.Pairs.SingleOrDefault(item => item.Id == _pairSet.SelectedPairId);
        if (pair is null) { ShowStatus("Select a saved pair in Folder Pairs first."); return; }
        _discoverySetupActive = true;
        using var cancellation = new CancellationTokenSource();
        _discoveryCheckCancellation = cancellation;
        if (_cancelDiscoveryCheck is not null) _cancelDiscoveryCheck.Visibility = Visibility.Visible;
        var elapsed = Stopwatch.StartNew();
        EventHandler closed = (_, _) => cancellation.Cancel(); Closed += closed;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => _discoveryStatus.Text = "Folder Pair: " + pair.Name + "\n" +
            SyncFastDiscovery.Status + $"\nElapsed: {elapsed.Elapsed:hh\\:mm\\:ss}";
        _discoveryStatus.Text = "Folder Pair: " + pair.Name + "\nChecking File Metadata…";
        timer.Start();
        try
        {
            var result = await Task.Run(() => _authorityHost.CheckFastDiscoveryAsync(pair.Id, cancellation.Token));
            _discoveryStatus.Text = DiscoveryCheckSummary(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { _discoveryStatus.Text = "Discovery Check Cancelled; No Files Were Changed"; }
        catch (Exception error) { _discoveryStatus.Text = "Discovery Check Did Not Complete: " + error.Message; }
        finally
        {
            timer.Stop(); Closed -= closed; _discoveryCheckCancellation = null; _discoverySetupActive = false;
            if (_cancelDiscoveryCheck is not null) _cancelDiscoveryCheck.Visibility = Visibility.Collapsed;
        }
    }

    internal static string DiscoveryCheckSummary(SyncDiscoveryCheckResult result) =>
        "Folder Pair: " + result.PairName + "\n" + result.Status +
        (result.Ready ? $"\nMetadata Entries: {result.MetadataEntries:N0}\nDuration: {result.Elapsed.TotalSeconds:N1} s" : "");

    private async Task<bool> ConfigureDiscoveryAsync(string mode)
    {
        if (!ExplorerIntegration.IsInstalledProcess)
        { _discoveryStatus.Text = "Use the signed installed app to configure Fast File Discovery. Nothing was changed."; ShowStatus(_discoveryStatus.Text); return false; }
        if (_discoverySetupActive || _runActive)
        { _discoveryStatus.Text = "Finish the current sync or setup operation first. Nothing was changed."; ShowStatus(_discoveryStatus.Text); return false; }
        var title = mode switch { "Configure" => "Set Up Fast File Discovery", "Remove" => "Remove Fast File Discovery", "Restore" => "Restore Previous Discovery Setup", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        var explanation = mode == "Remove"
            ? "Remove Sync Shark's fixed Discovery task and protected helper. Your files, accounts and sync settings stay unchanged; normal scanning continues."
            : mode == "Restore" ? "Restore the previous protected helper and its matching app identities. If it does not match the installed app, normal scanning continues."
            : "Install the signed read-only helper in Program Files and one fixed, on-demand Windows task. Windows asks for administrator approval once. Sync Shark itself stays unelevated, and no sync requires an additional approval prompt. No certificate or trust-store setting is changed.";
        if (!AppDialogWindow.Confirm(this, title, title + "?", explanation, mode == "Remove" ? "Remove Setup" : "Continue"))
        { _discoveryStatus.Text = title + " Was Cancelled. Nothing Was Changed."; return false; }
        _discoveryStatus.Text = title + "\u2026";
        try { await ExecuteDiscoverySetupAsync(mode, CancellationToken.None); return true; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { _discoveryStatus.Text = "Setup Cancelled; Nothing Was Enabled"; ShowStatus(_discoveryStatus.Text); return false; }
        catch (Exception error)
        { _discoveryStatus.Text = "Discovery Setup Not Ready: " + error.Message; ShowStatus(_discoveryStatus.Text); return false; }
    }

    internal async Task<SyncCliControlData> ConfigureDiscoveryFromCliAsync(string mode, CancellationToken cancellationToken)
    {
        await ExecuteDiscoverySetupAsync(mode, cancellationToken);
        return new SyncCliControlData("discovery " + mode.ToLowerInvariant(), true, SyncFastDiscovery.Status);
    }

    private async Task ExecuteDiscoverySetupAsync(string mode, CancellationToken cancellationToken)
    {
        if (mode is not ("Configure" or "Remove" or "Restore")) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!ExplorerIntegration.IsInstalledProcess)
            throw new InvalidOperationException("Use the signed installed app to configure Fast File Discovery.");
        if (_discoverySetupActive || _runActive)
            throw new InvalidOperationException("Finish the current sync or setup operation first.");
        cancellationToken.ThrowIfCancellationRequested();
        _discoverySetupActive = true;
        try
        {
            var directory = Path.GetDirectoryName(ExistingWindowActivation.ResolveExecutable(Environment.ProcessPath!))!;
            using var heldRuntime = PilotDirectoryLease.OpenInstalledRuntime(directory);
            var helperPath = Path.Combine(directory, "SyncSharkDiscovery.exe");
            using var heldHelper = SyncSharkDiscoverySetupNative.Hold(helperPath, false);
            SyncSharkDiscoverySetupNative.RequireProductSignature(helperPath);
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(helperPath));
            if (!SyncSharkSignerIdentity.PublisherThumbprints.Contains(certificate.Thumbprint) ||
                certificate.Subject != SyncSharkSignerIdentity.PublisherSubject || certificate.Issuer != SyncSharkSignerIdentity.PublisherSubject)
                throw new IOException("The Discovery setup signer does not match this product. Nothing was launched.");
            heldRuntime.VerifyBinding();
            var start = BuildDiscoverySetupStart(mode, directory, Environment.ProcessId);
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new IOException("Windows could not start the administrator setup.");
            // After Windows accepts this owned setup transaction, wait for its safe completion. A CLI
            // cancellation must not kill a privileged operation halfway through registration/rollback.
            await process.WaitForExitAsync(); heldRuntime.VerifyBinding();
            if (process.ExitCode != 0) throw new IOException(DiscoverySetupFailureText(process.ExitCode));
            await SyncFastDiscovery.RefreshAfterSetupAsync();
            _discoveryStatus.Text = SyncFastDiscovery.Status; ShowStatus(_discoveryStatus.Text);
        }
        finally { _discoverySetupActive = false; }
    }

    internal static string DiscoverySetupFailureText(int exitCode)
    {
        string[] phases = ["Starting Setup", "Checking the Installed App", "Checking Protected Folders",
            "Checking Windows Permissions", "Reading the Installed Files", "Verifying Signatures",
            "Checking Folder Permissions", "Opening the Setup Transaction", "Connecting to Task Scheduler",
            "Finding the Existing Task", "Verifying the Existing Task", "Recovering an Interrupted Setup",
            "Verifying the Existing Files", "Preparing the File Manifest", "Preparing the Setup Transaction",
            "Publishing the Protected Files", "Verifying the Published Files", "Registering the Windows Task",
            "Verifying the Windows Task", "Completing the Setup Transaction", "Enabling the Windows Task"];
        var phase = (exitCode >> 16) & 0xff;
        if ((exitCode & unchecked((int)0xff000000)) == 0x20000000 && phase < phases.Length)
            return $"Discovery Setup Stopped While {phases[phase]} (Error 0x{exitCode & 0xffff:X4}). Ordinary Scanning Remains Available.";
        return $"Discovery Setup Did Not Complete (Error 0x{exitCode:X8}). Ordinary Scanning Remains Available.";
    }

    internal static ProcessStartInfo BuildDiscoverySetupStart(string mode, string directory, int processId)
    {
        if (mode is not ("Configure" or "Remove" or "Restore") || processId <= 0)
            throw new ArgumentException("One exact Discovery setup operation and requesting process are required.");
        var start = new ProcessStartInfo(Path.Combine(directory, "SyncSharkDiscovery.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        // UAC names the fixed signed product executable. No script, caller path,
        // input stream, expression or general command can cross this interface.
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }
}
