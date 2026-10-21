using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

// U4 Settings panels that own no draft: Explorer integration, the inline About & Updates
// identity and the read-only Diagnostics collector.
//
// Every value here is read from already-loaded product state or an existing read-only helper
// (ExplorerPackage.Status, SyncFastDiscovery, the retained account status projection). Nothing in
// this file syncs, signs in, repairs, elevates, scans folder content or contacts a provider, and
// no result is ever uploaded: the report is only previewed, copied or saved to a chosen path.
public partial class MainWindow
{
    // One shared grouped heading for the panels that group several rows under a subheading.
    private static TextBlock SettingsGroupHeading(string title)
    {
        var heading = Text(title, 13, strong: true);
        heading.Margin = new Thickness(0, 20, 0, 2);
        System.Windows.Automation.AutomationProperties.SetName(heading, title);
        return heading;
    }

    // A quiet action whose tooltip states the effect and scope of the action instead of merely
    // repeating the label, matching the shared settings rule for tooltips.
    private static Button ScopedAction(string label, string tooltip, RoutedEventHandler action)
    {
        var button = Button(label, action, true);
        button.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tooltip);
        return button;
    }

    // The destructive variant keeps the shared consequence tooltip and reuses the existing product
    // error brush instead of inventing another red, so it follows the active theme like every other
    // error affordance in this application.
    private Button DestructiveAction(string label, string tooltip, RoutedEventHandler action)
    {
        var button = ScopedAction(label, tooltip, action);
        button.Foreground = (Brush)FindResource("ErrorBrush");
        return button;
    }

    // --- Explorer ----------------------------------------------------------------------------

    private TextBlock _explorerSettingsStatus = null!;
    private Button _explorerSetupButton = null!, _explorerRepairButton = null!;
    private bool _explorerSettingsActionRunning;

    private UIElement BuildExplorerSettings()
    {
        var rows = new StackPanel();
        rows.Children.Add(SettingsGroupHeading("Explorer Menu"));
        _explorerSettingsStatus = Text(ExplorerMenuStatusForDisplay(), 12, brush: "MutedInk");
        _explorerSettingsStatus.MaxWidth = 380;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_explorerSettingsStatus,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        rows.Children.Add(SettingRow("Registration", "The Windows 11 right-click commands for Sync Shark folder pairs. Windows must trust the signed package; this action never changes certificate trust or another user.", _explorerSettingsStatus));
        _explorerSetupButton = ScopedAction("Set Up Explorer Menu",
            "Registers the signed Explorer package for this installed version only. An existing matching registration is reported as already set up, and no file, folder pair, account or certificate setting is changed.",
            async (_, _) => await SetUpExplorerMenuAsync());
        rows.Children.Add(SettingRow("Set Up or Update", "Register the signed Explorer package for this installed version. An existing matching registration is reported as already set up; no other registration, file, pair or account is changed.", _explorerSetupButton));
        _explorerRepairButton = ScopedAction("Repair Explorer Integration",
            "Re-registers the retained folder pairs and their Explorer commands. Folder pairs, files, accounts, settings and recovery data are unchanged and no file is copied.",
            async (_, _) => await RepairExplorerIntegrationAsync());
        rows.Children.Add(SettingRow("Repair Integration", "Re-register the retained folder pairs and their Explorer commands after preparing removal. Folder pairs, files, accounts, settings and recovery data are unchanged and no file is copied.", _explorerRepairButton));
        return FormPage(rows);
    }

    // Read-only. This is the same package query the setup action always ran; showing it on the page
    // means an already-registered package can never look like a silent no-op.
    private string ExplorerMenuStatusForDisplay()
    {
        if (!ExplorerIntegration.IsInstalledProcess)
            return "Available in the signed installed app. This build has not registered Explorer commands.";
        try { return ExplorerPackage.Status(); }
        catch (Exception error) { return "Explorer registration state could not be read: " + error.Message; }
    }

    private void RefreshExplorerSettingsStatus()
    {
        if (_explorerSettingsStatus is null) return;
        _explorerSettingsStatus.Text = ExplorerMenuStatusForDisplay();
    }

    private void SetExplorerSettingsBusy(bool busy)
    {
        _explorerSettingsActionRunning = busy;
        if (_explorerSetupButton is not null) _explorerSetupButton.IsEnabled = !busy;
        if (_explorerRepairButton is not null) _explorerRepairButton.IsEnabled = !busy;
    }

    // The guarded registration path already verifies a matching registration instead of mutating
    // it, so this page reports that exact case as a local success rather than staying silent.
    private async Task SetUpExplorerMenuAsync()
    {
        if (_explorerSettingsActionRunning) return;
        using var feedback = ActionFeedback.Begin(_explorerSetupButton);
        if (!feedback.Entered) return;
        if (!ExplorerIntegration.IsInstalledProcess)
        {
            _explorerSettingsStatus.Text = "Use the signed installed app to register Explorer commands. Nothing was changed.";
            return;
        }
        SetExplorerSettingsBusy(true);
        _explorerSettingsStatus.Text = "Setting up the Explorer menu\u2026";
        try
        {
            var before = ExplorerPackage.Status();
            await ExplorerPackage.RegisterAsync();
            var after = ExplorerPackage.Status();
            _explorerSettingsStatus.Text = string.Equals(before, after, StringComparison.Ordinal)
                ? "Already Set Up: no change was needed. " + after
                : "Set Up: " + after;
            ShowStatus(_explorerSettingsStatus.Text);
        }
        catch (Exception error)
        {
            _explorerSettingsStatus.Text = "Explorer Menu Not Ready: " + error.Message + " The classic menu remains available.";
            ShowStatus(_explorerSettingsStatus.Text);
        }
        finally { SetExplorerSettingsBusy(false); }
    }

    private async Task RepairExplorerIntegrationAsync()
    {
        if (_explorerSettingsActionRunning) return;
        using var feedback = ActionFeedback.Begin(_explorerRepairButton);
        if (!feedback.Entered) return;
        SetExplorerSettingsBusy(true);
        _explorerSettingsStatus.Text = "Repairing Explorer integration\u2026";
        try
        {
            await RestoreExplorerIntegrationAsync(propagateErrors: true);
            _explorerSettingsStatus.Text = "Repaired. " + ExplorerMenuStatusForDisplay();
            ShowStatus("Explorer Integration Repaired");
        }
        catch (Exception error)
        {
            _explorerSettingsStatus.Text = "Explorer Integration Was Not Repaired: " + error.Message;
            ShowStatus(_explorerSettingsStatus.Text);
        }
        finally { SetExplorerSettingsBusy(false); }
    }

    // --- About & Updates ---------------------------------------------------------------------

    // Product branding comes from the running app's build metadata. The Explorer package keeps
    // its original publisher as a compatibility identity; it is not this product's display name.
    internal static string ProductPublisher => typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Not recorded";

    private UIElement BuildAboutUpdatesSettings()
    {
        var rows = new StackPanel();
        rows.Children.Add(SettingsGroupHeading("Product"));
        var identity = new StackPanel { MaxWidth = 380 };
        identity.Children.Add(Text("Sync Shark", 14, strong: true));
        identity.Children.Add(Text("Version " + (typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unavailable") +
            "  \u00b7  " + UpdateVersionIdentity, 12, brush: "MutedInk"));
        identity.Children.Add(Text("Publisher: " + ProductPublisher, 12, brush: "MutedInk"));
        identity.Children.Add(Text("Source revision: " + BuildIdentity.SourceRevision, 12, brush: "MutedInk"));
        rows.Children.Add(SettingRow("Installed Product", "The exact product, version, publisher and source revision running on this computer. No separate About window is used.", identity));
        rows.Children.Add(SettingsGroupHeading("Updates"));
        rows.Children.Add(BuildUpdateSettings());
        rows.Children.Add(SettingsGroupHeading("Removal"));
        rows.Children.Add(SettingRow("Uninstall Sync Shark", "Remove the app after making managed files local. Accounts, folder pairs, settings and recovery data are kept; one exact app copy is retained for undo.",
            DestructiveAction("Uninstall Sync Shark", "Removes only this product's protected installation after making managed files local. Your files, accounts, folder pairs, settings and recovery data are kept.", async (_, _) => await PrepareRemovalAsync())));
        return FormPage(rows);
    }

    // --- Diagnostics -------------------------------------------------------------------------

    internal static class DiagnosticOutcomes
    {
        internal const string Pass = "Pass";
        internal const string Warning = "Warning";
        internal const string Failed = "Failed";
        internal const string Skipped = "Skipped";
    }

    // DIAG354-1: the page text (Check/Detail) may name a real folder pair and its local path, because the
    // page is local. The report text (ReportCheck/ReportDetail) is the shareable redacted form every copy,
    // preview, save and CLI surface renders. Both come from the same check, so they cannot disagree.
    internal sealed record DiagnosticCheck(string Check, string Outcome, string Detail, string Freshness,
        string ReportCheck, string ReportDetail)
    {
        internal DiagnosticCheck(string check, string outcome, string detail, string freshness)
            : this(check, outcome, detail, freshness, check, detail) { }
    }

    // DIAG354-3: the endpoints are described by the existing shared SyncEndpointContext.Capture, so two
    // local folders, a reversed cloud/local pair and two cloud folders are all described truthfully. A pair
    // whose declared endpoints cannot be described safely is retained as one bounded failure instead of
    // being guessed from its legacy local root.
    internal sealed record DiagnosticPairSnapshot(string Name, string Ordinal, bool Enabled,
        SyncEndpointContext? Endpoints, string EndpointFailure);

    // DIAG354-2: every retained active account is represented. `Checked` records whether a retained session
    // check exists at all, so an account that was never selected or refreshed is reported as not checked
    // rather than as a missing credential or as a healthy account.
    internal sealed record DiagnosticAccountSnapshot(string Provider, string Ordinal, bool Checked,
        bool HasProtectedCredential, bool LiveVerified, string? FailureType, DateTimeOffset? VerifiedUtc);

    // The values the whole result is rendered from. Nothing here is read live by any later surface.
    internal sealed record DiagnosticSnapshot(string Version, string InformationVersion, string SourceRevision,
        string Publisher, bool IsInstalledProcess, bool StateReadable, string StateDetail, bool AutomationEnabled,
        bool RunActive, bool Paused, int PendingOperations, int AttentionPairs, int RecoveryRecords,
        IReadOnlyList<DiagnosticPairSnapshot> Pairs, IReadOnlyList<DiagnosticAccountSnapshot> Accounts);

    // DIAG354-1: one completed immutable run: the exact captured snapshot, its checks and the instant the
    // checks were made. The page, the preview, the copy, the save and the CLI all render this record, so a
    // later navigation, filter, selection or state change cannot alter a reported result, and a failed new
    // run never replaces or relabels the retained one.
    internal sealed record DiagnosticRun(DiagnosticSnapshot Snapshot, DiagnosticCheck[] Checks,
        DateTimeOffset CheckedUtc);

    private readonly List<DiagnosticCheck> _diagnosticChecks = [];
    private DiagnosticRun? _diagnosticRetainedRun;
    private string? _diagnosticFailureMessage;
    private TextBlock _diagnosticStatus = null!;
    private StackPanel _diagnosticResults = null!;
    private Button _diagnosticRun = null!;
    private bool _diagnosticRunning;
    private DateTimeOffset? _diagnosticLastRunUtc;

    // Fixture seam. Production leaves this null, so no production path can fail a run on purpose. A
    // fixture throws the supplied failure from the capture step to prove against the real run path that a
    // failed new run never replaces the retained result or relabels it as fresh.
    internal static Exception? DiagnosticCaptureFailureForTest { get; set; }

    private UIElement BuildDiagnosticsSettings()
    {
        var rows = new StackPanel();
        rows.Children.Add(SettingsGroupHeading("Checks"));
        _diagnosticRun = ScopedAction("Run Diagnostics",
            "Runs read-only checks of this installation and its retained state. Nothing is synced, signed in, repaired or uploaded.",
            async (_, _) => await RunDiagnosticsAsync());
        var runActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        runActions.Children.Add(_diagnosticRun);
        rows.Children.Add(SettingRow("Read-Only Checks", "Check this installation, its retained state and its configured folders. Nothing is synced, signed in, repaired or uploaded, and no file or provider state is changed.", runActions));
        _diagnosticStatus = Text("", 12, brush: "MutedInk");
        _diagnosticStatus.MaxWidth = 560;
        _diagnosticStatus.Margin = new Thickness(0, 2, 0, 6);
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_diagnosticStatus,
            System.Windows.Automation.AutomationLiveSetting.Polite);
        _diagnosticResults = new StackPanel();
        RenderDiagnosticChecks();
        rows.Children.Add(_diagnosticStatus);
        rows.Children.Add(_diagnosticResults);

        rows.Children.Add(SettingsGroupHeading("Support Report"));
        var reportActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        reportActions.Children.Add(ScopedAction("Copy Report",
            "Copies the same redacted report that is previewed and saved. Nothing is uploaded.",
            (_, _) =>
            {
                SetClipboardText(BuildDiagnosticReportText());
                _diagnosticStatus.Text = "Support report copied to the clipboard. Nothing was uploaded.";
            }));
        reportActions.Children.Add(ScopedAction("Preview and Save Report",
            "Previews the redacted report, then saves it to a path you choose. Cancelling either step changes nothing.",
            (_, _) => PreviewDiagnosticReport()));
        rows.Children.Add(SettingRow("Local Report", "Preview the same read-only result, then save it to a path you choose. The report is for local support and debugging, it is never uploaded automatically, and cancelling either step changes nothing.", reportActions));
        return FormPage(rows);
    }

    private async Task RunDiagnosticsAsync()
    {
        if (_diagnosticRunning) return;
        using var feedback = ActionFeedback.Begin((object?)_diagnosticRun ?? this);
        _diagnosticRunning = true;
        if (_diagnosticRun is not null) _diagnosticRun.IsEnabled = false;
        if (_diagnosticStatus is not null) _diagnosticStatus.Text = "Running read-only checks\u2026";
        try
        {
            // DIAG354-1: the one shared read-only action performs the whole run and retains its result.
            await RunDiagnosticActionAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            // A failed run is reported truthfully and leaves the retained result exactly as it was: it is
            // never replaced, and its own checked time is never relabelled as the fresh one.
            _diagnosticFailureMessage = SafeDiagnosticFailure(error);
            if (_diagnosticStatus is not null)
                _diagnosticStatus.Text = "Diagnostics Could Not Finish: " + _diagnosticFailureMessage +
                    (_diagnosticRetainedRun is null
                        ? " No result has been recorded yet, so nothing is reported as checked."
                        : " The previous result is unchanged and is still the result checked " +
                          FormatDiagnosticTime(_diagnosticRetainedRun.CheckedUtc) + ".");
        }
        finally
        {
            _diagnosticRunning = false;
            if (_diagnosticRun is not null) _diagnosticRun.IsEnabled = true;
        }
    }

    // DIAG354-1: the ONE shared in-app read-only diagnostic action. The Diagnostics page button and the
    // `diagnostics run` CLI command both call exactly this method, so neither surface owns a private
    // implementation. It reads already-loaded product state and existing read-only helpers only: nothing is
    // synced, signed in, repaired, uploaded or written, no provider is contacted and no credential is read.
    internal async Task<DiagnosticRun> RunDiagnosticActionAsync(CancellationToken token)
    {
        // Only an explicit diagnostics run scans all retained database pages and
        // foreign keys. Keep the synchronous SQLite work off the UI thread.
        await Task.Run(() => _authorityHost.VerifyHealthAsync(token), token);
        // The snapshot is captured on the UI thread, so the metadata-only endpoint checks can then run off
        // it without touching window state.
        var snapshot = Dispatcher.CheckAccess()
            ? CaptureDiagnosticSnapshot()
            : await Dispatcher.InvokeAsync(CaptureDiagnosticSnapshot);
        token.ThrowIfCancellationRequested();
        var checks = await Task.Run(() => CollectDiagnosticChecks(snapshot), token);
        var run = new DiagnosticRun(snapshot, checks.ToArray(), DateTimeOffset.Now);
        RetainDiagnosticRun(run);
        return run;
    }

    // Only a completed run replaces the retained one, and the page list holds the same check objects the
    // retained run holds, so the rendered rows and the rendered report cannot disagree.
    private void RetainDiagnosticRun(DiagnosticRun run)
    {
        _diagnosticRetainedRun = run;
        _diagnosticChecks.Clear();
        _diagnosticChecks.AddRange(run.Checks);
        _diagnosticLastRunUtc = run.CheckedUtc;
        _diagnosticFailureMessage = null;
        RenderDiagnosticChecks();
    }

    private static string FormatDiagnosticTime(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss");

    // A failed run reports the failure class only. A raw exception message can carry a local path, an
    // account address or provider text, so it never reaches the page or the shareable report.
    private static string SafeDiagnosticFailure(Exception error) =>
        error is InvalidDataException ? "the retained state could not be read safely"
        : error is OperationCanceledException ? "the run was cancelled"
        : "the run stopped with " + error.GetType().Name;

    private void RenderDiagnosticChecks()
    {
        // The shared action also runs from the command line, where the Diagnostics page may never have been
        // realised. The retained run is still recorded; there is simply nothing to draw yet.
        if (_diagnosticResults is null || _diagnosticStatus is null) return;
        _diagnosticResults.Children.Clear();
        if (_diagnosticChecks.Count == 0)
        {
            _diagnosticStatus.Text = "Not Yet Run. Choose Run Diagnostics to check this installation and its retained state.";
            _diagnosticResults.Children.Add(SettingRow("Results", "Pass, warning, failed and skipped checks appear here with their reasons and freshness.", Text("Not Yet Run", 12, brush: "MutedInk")));
            return;
        }
        var passed = _diagnosticChecks.Count(check => check.Outcome == DiagnosticOutcomes.Pass);
        var warnings = _diagnosticChecks.Count(check => check.Outcome == DiagnosticOutcomes.Warning);
        var failed = _diagnosticChecks.Count(check => check.Outcome == DiagnosticOutcomes.Failed);
        var skipped = _diagnosticChecks.Count(check => check.Outcome == DiagnosticOutcomes.Skipped);
        _diagnosticStatus.Text = (failed > 0 ? "Needs Attention: " : "Completed: ") +
            passed + " passed, " + warnings + " warning(s), " + failed + " failed, " + skipped + " skipped. Last run " +
            (_diagnosticLastRunUtc?.ToLocalTime().ToString("d MMM yyyy, HH:mm:ss") ?? "unknown") + ".";
        foreach (var check in _diagnosticChecks)
            _diagnosticResults.Children.Add(SettingRow(check.Check,
                check.Detail + "  \u00b7  " + check.Freshness,
                Text(check.Outcome, 12, strong: true, brush: DiagnosticOutcomeBrush(check.Outcome))));
    }

    private static string DiagnosticOutcomeBrush(string outcome) => outcome switch
    {
        DiagnosticOutcomes.Pass => "SuccessBrush",
        DiagnosticOutcomes.Warning => "WarningBrush",
        DiagnosticOutcomes.Failed => "ErrorBrush",
        _ => "MutedInk"
    };

    private DiagnosticSnapshot CaptureDiagnosticSnapshot()
    {
        if (DiagnosticCaptureFailureForTest is { } forcedFailure) throw forcedFailure;
        var stateReadable = true;
        var automationEnabled = false;
        var stateDetail = "Stored automation, preferences and folder pairs were read without error.";
        try { automationEnabled = _authorityHost.LoadAutomation().Enabled; _ = _workspacePreferencesStore.Load(); }
        catch (Exception error)
        {
            stateReadable = false;
            stateDetail = "Retained product state could not be read (" + error.GetType().Name + ").";
        }
        // DIAG354-2: every retained active account is represented, with no account filter and no dependence
        // on the selected account. Each one is described by the retained session check when there is one, and
        // never by a token request, a provider call, an authentication attempt or a credential read.
        var accounts = _authorityHost.LoadProviderConnections().Where(connection => connection.IsActive)
            .Select((connection, index) => DescribeDiagnosticAccount(connection, index + 1)).ToArray();
        // The attention set is the existing shared classifier Home and the tray use, so a broken pair and a
        // first-sync review are reported exactly as the rest of the product reports them, in its own order.
        var attentionPairIds = BuildAttentionCases().Select(item => item.Pair.Id)
            .Distinct(StringComparer.Ordinal).ToArray();
        var pairs = _pairSet.Pairs.Select((pair, index) => DescribeDiagnosticPair(pair, index + 1)).ToArray();
        return new DiagnosticSnapshot(typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unavailable",
            UpdateVersionIdentity, BuildIdentity.SourceRevision, ProductPublisher,
            ExplorerIntegration.IsInstalledProcess, stateReadable, stateDetail, automationEnabled,
            _runActive, TrayPaused, _globalPendingQueueCount, attentionPairIds.Length,
            // DIAG354-2: this is only the recovery records loaded into the Recovery page view for the pair it
            // was loaded for. It is an observation of that view rather than a global total, and the report and
            // the check row both state its scope and that zero loaded records prove nothing about retention.
            _allRecoveryRows.Count, pairs, accounts);
    }

    private DiagnosticAccountSnapshot DescribeDiagnosticAccount(ProviderConnectionRecord connection, int ordinal)
    {
        var provider = SyncProviders.DisplayName(connection.Provider);
        var label = "Account " + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " (" + provider + ")";
        return _knownAccountStatus.TryGetValue(connection.ConnectionId, out var status)
            ? new DiagnosticAccountSnapshot(provider, label, true, status.HasProtectedCredential,
                status.LiveVerified, status.FailureType, status.Binding?.LastVerifiedUtc)
            : new DiagnosticAccountSnapshot(provider, label, false, false, false, null, null);
    }

    private static DiagnosticPairSnapshot DescribeDiagnosticPair(SyncPair pair, int ordinal)
    {
        var label = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!pair.Enabled) return new DiagnosticPairSnapshot(pair.Name, label, false, null, string.Empty);
        try
        {
            // DIAG354-3: the pair's own typed ordered endpoints are described by the existing shared
            // capture, so both local folders, a reversed cloud/local pair and two cloud folders are all
            // described by their real kinds rather than assumed from one legacy local root.
            return new DiagnosticPairSnapshot(pair.Name, label, true, SyncEndpointContext.Capture(pair), string.Empty);
        }
        catch (Exception error)
        {
            // A pair whose declared endpoints cannot be described safely is retained as exactly one
            // bounded failure. It is never silently reinterpreted through the legacy local-root field.
            return new DiagnosticPairSnapshot(pair.Name, label, true, null, error.GetType().Name);
        }
    }

    // Metadata-only and read-only: folder checks use a bounded existence test, provider folders are
    // explicitly skipped, and no check opens, enumerates, hydrates or writes a file.
    private static IReadOnlyList<DiagnosticCheck> CollectDiagnosticChecks(DiagnosticSnapshot snapshot)
    {
        const string readNow = "Read just now";
        var checks = new List<DiagnosticCheck>();
        var boundRevision = snapshot.SourceRevision.Length == 40 && snapshot.SourceRevision.All(Uri.IsHexDigit);
        checks.Add(boundRevision
            ? new DiagnosticCheck("Application identity", DiagnosticOutcomes.Pass,
                "Sync Shark " + snapshot.Version + " is bound to source revision " + snapshot.SourceRevision + ".", readNow)
            : new DiagnosticCheck("Application identity", DiagnosticOutcomes.Failed,
                "The running build is not bound to a frozen source revision (reported '" + snapshot.SourceRevision +
                "'). Updates, support and rollback need an exact build identity.", readNow));
        checks.Add(snapshot.IsInstalledProcess
            ? new DiagnosticCheck("Signed installation", DiagnosticOutcomes.Pass,
                "This process is the signed installed application, so installation-only actions are available.", readNow)
            : new DiagnosticCheck("Signed installation", DiagnosticOutcomes.Warning,
                "This is not the signed installed application. Explorer registration, update installation and removal are unavailable; a development build is expected to report this.", readNow));
        checks.Add(snapshot.StateReadable
            ? new DiagnosticCheck("Stored product state", DiagnosticOutcomes.Pass, snapshot.StateDetail, readNow)
            : new DiagnosticCheck("Stored product state", DiagnosticOutcomes.Failed, snapshot.StateDetail, readNow));

        if (snapshot.Pairs.Count == 0)
            checks.Add(new DiagnosticCheck("Folder pairs", DiagnosticOutcomes.Skipped,
                "No folder pair is configured, so there is no local folder to check.", readNow));
        foreach (var pair in snapshot.Pairs)
        {
            var pageLabel = "Folder pair: " + pair.Name;
            var reportLabel = "Folder pair " + pair.Ordinal;
            if (!pair.Enabled)
            {
                const string disabled = "This pair is disabled, so its endpoints were not inspected. Nothing was opened or changed.";
                checks.Add(new DiagnosticCheck(pageLabel, DiagnosticOutcomes.Skipped, disabled, readNow,
                    reportLabel, disabled));
                continue;
            }
            if (pair.Endpoints is not { } endpoints)
            {
                const string unreadable = "The saved endpoint description of this pair cannot be read safely, so no local folder was inspected. Nothing was opened, moved or changed.";
                checks.Add(new DiagnosticCheck(pageLabel, DiagnosticOutcomes.Failed,
                    "The saved endpoints of this pair cannot be described safely, so no local folder was " +
                    "inspected (" + pair.EndpointFailure + "). Nothing was opened, moved or changed.",
                    readNow, reportLabel, unreadable));
                continue;
            }
            // DIAG354-3: only the pair's real local endpoints are inspected, with one bounded existing
            // directory metadata check each: both for local/local, one for cloud/local, and none for
            // cloud/cloud. A cloud identity, an empty compatibility local root and private staging are never
            // tested as if they were a local endpoint.
            var localSides = new[] { endpoints.Left, endpoints.Right }.Where(side => side.IsLocal).ToArray();
            if (localSides.Length == 0)
            {
                const string bothCloud = "Both endpoints are cloud folders, so there is no local folder to check. No provider was contacted, so remote availability is not claimed.";
                checks.Add(new DiagnosticCheck(pageLabel, DiagnosticOutcomes.Skipped, bothCloud, readNow,
                    reportLabel, bothCloud));
                continue;
            }
            foreach (var side in localSides)
            {
                var sideName = side.Side == SyncPairSide.Left ? "left" : "right";
                var folder = side.LocalPath ?? string.Empty;
                var pageSide = pageLabel + " \u00b7 " + sideName + " local folder";
                var reportSide = reportLabel + " " + sideName + " local endpoint";
                checks.Add(DirectoryExists(folder)
                    ? new DiagnosticCheck(pageSide, DiagnosticOutcomes.Pass,
                        "The saved local folder on the " + sideName + " endpoint is available.", readNow,
                        reportSide, "The saved local folder is available.")
                    : new DiagnosticCheck(pageSide, DiagnosticOutcomes.Failed,
                        "The saved local folder on the " + sideName + " endpoint is unavailable: " + folder +
                        ". Sync for this pair stops until it returns; nothing was moved, deleted or created.",
                        readNow, reportSide, "The saved local folder is unavailable. Sync for this pair stops " +
                        "until it returns; nothing was moved, deleted or created."));
            }
        }
        if (snapshot.Pairs.Any(pair => pair.Endpoints is { } captured &&
                (!captured.Left.IsLocal || !captured.Right.IsLocal)))
            checks.Add(new DiagnosticCheck("Cloud folders", DiagnosticOutcomes.Skipped,
                "Folder availability is checked separately from account access. This report never contacts a provider, so remote availability is not claimed here.", readNow));

        if (snapshot.Accounts.Count == 0)
            checks.Add(new DiagnosticCheck("Accounts", DiagnosticOutcomes.Skipped,
                "No connected account is retained for this Windows user, so there is no account to check.",
                "Not checked yet"));
        foreach (var account in snapshot.Accounts)
        {
            var name = account.Ordinal;
            var freshness = account.VerifiedUtc is { } verified
                ? "Last verified " + verified.ToLocalTime().ToString("d MMM yyyy, HH:mm")
                : "No recorded verification time";
            // DIAG354-2: an account with no retained check result is not checked, not a missing credential
            // and not healthy. Only a real retained check can report a credential, failure or success.
            if (!account.Checked)
                checks.Add(new DiagnosticCheck(name, DiagnosticOutcomes.Skipped,
                    "No account check result is retained in this session for this connected account. Refresh " +
                    "it on Connections to record one. Its credential state is not checked here: this is not a " +
                    "missing credential and it is not a healthy account.", "Not checked in this session"));
            else if (!account.HasProtectedCredential)
                checks.Add(new DiagnosticCheck(name, DiagnosticOutcomes.Failed,
                    "No protected credential is retained for this account, so its pairs cannot sync until it is reconnected.", freshness));
            else if (account.FailureType is { } failure)
                checks.Add(new DiagnosticCheck(name, DiagnosticOutcomes.Failed,
                    "The last account check reported " + failure + ". Reconnect or refresh this account.", freshness));
            else if (!account.LiveVerified)
                checks.Add(new DiagnosticCheck(name, DiagnosticOutcomes.Warning,
                    "A protected credential is retained but access was not verified live in this session. Refresh the account to confirm access and storage.", freshness));
            else checks.Add(new DiagnosticCheck(name, DiagnosticOutcomes.Pass,
                "Account access, identity and storage were verified for this session.", freshness));
        }

        checks.Add(snapshot.PendingOperations == 0
            ? new DiagnosticCheck("Pending operations", DiagnosticOutcomes.Pass, "No operation is waiting to finish.", readNow)
            : new DiagnosticCheck("Pending operations", DiagnosticOutcomes.Warning,
                snapshot.PendingOperations + " operation(s) are waiting to finish. Open Activity for the exact items and reasons.", readNow));
        checks.Add(snapshot.AttentionPairs == 0
            ? new DiagnosticCheck("Folder pairs needing attention", DiagnosticOutcomes.Pass,
                "No folder pair currently needs input, using the same attention decision Home and the tray use.", readNow)
            : new DiagnosticCheck("Folder pairs needing attention", DiagnosticOutcomes.Warning,
                snapshot.AttentionPairs + " folder pair(s) need input, using the same attention decision Home " +
                "and the tray use. Open Attention for the exact reasons.", readNow,
                "Folder pairs needing attention",
                "See the local Needs Attention page for the exact reasons; pair names are not exported."));
        // DIAG354-2: recovery rows are only an observation of the loaded Recovery page view. They are never
        // presented as a global total, and zero loaded records is explicitly not proof of no retention.
        checks.Add(new DiagnosticCheck("Loaded recovery records", DiagnosticOutcomes.Pass,
            snapshot.RecoveryRecords == 0
                ? "No recovery record is loaded in the Recovery page view for the pair it was loaded for. Zero " +
                  "loaded records does not prove that no backup is retained."
                : snapshot.RecoveryRecords + " recovery record(s) are loaded in the Recovery page view for the " +
                  "pair they were loaded for. This is what that view loaded, not a global total of retained backups.",
            readNow, "Loaded recovery records",
            snapshot.RecoveryRecords == 0
                ? "No recovery record is loaded in the Recovery page view for the pair it was loaded for. Zero " +
                  "loaded records does not prove that no backup is retained."
                : "Recovery records are loaded in the Recovery page view for the pair they were loaded for; " +
                  "this is not a global total of retained backups."));
        checks.Add(!snapshot.AutomationEnabled
            ? new DiagnosticCheck("Live sync", DiagnosticOutcomes.Warning,
                "Automatic sync is off. Watch for changes and Sync now still work when asked; enable Automatic Sync on Sync & Performance to resume automatic sync.", readNow)
            : snapshot.Paused
                ? new DiagnosticCheck("Live sync", DiagnosticOutcomes.Warning,
                    "Automatic sync is enabled but sync is paused. Resume it from the notification area or Connections when ready.", readNow)
                : new DiagnosticCheck("Live sync", DiagnosticOutcomes.Pass, "Automatic sync is enabled and not paused.", readNow));

        if (!snapshot.IsInstalledProcess)
            checks.Add(new DiagnosticCheck("Explorer registration", DiagnosticOutcomes.Skipped,
                "Explorer registration applies to the signed installed application, so it was not inspected.", readNow));
        else
        {
            try
            {
                var status = ExplorerPackage.Status();
                checks.Add(status switch
                {
                    "Main Explorer Menu Is Registered" => new DiagnosticCheck("Explorer registration",
                        DiagnosticOutcomes.Pass, "The Explorer package for this installed version is registered.", readNow),
                    "Main Menu Is Not Registered; Classic Menu Is Available" => new DiagnosticCheck("Explorer registration",
                        DiagnosticOutcomes.Warning, "The Explorer package is not registered. The classic menu remains available; use Set Up or Update on the Explorer tab to register it.", readNow),
                    "Package Identity Conflict" => new DiagnosticCheck("Explorer registration",
                        DiagnosticOutcomes.Failed, "Another package identity conflicts with this product. Nothing was changed; repair the signed installation before registering Explorer commands.", readNow),
                    _ => new DiagnosticCheck("Explorer registration", DiagnosticOutcomes.Skipped,
                        "Explorer registration reported an unrecognised state: " + status, readNow)
                });
            }
            catch (Exception error)
            {
                checks.Add(new DiagnosticCheck("Explorer registration", DiagnosticOutcomes.Warning,
                    "Explorer registration state could not be read (" + error.GetType().Name + ").", readNow));
            }
        }
        checks.Add(SyncFastDiscovery.IsInstalled
            ? new DiagnosticCheck("Fast File Discovery", DiagnosticOutcomes.Pass,
                "The protected Discovery helper and task are registered. " + SyncFastDiscovery.Status, readNow)
            : new DiagnosticCheck("Fast File Discovery", DiagnosticOutcomes.Skipped,
                "Fast File Discovery is not set up, so ordinary scanning is active. Set it up from Sync & Performance to share file metadata across pairs on one drive.", readNow));
        return checks;
    }

    private static bool DirectoryExists(string path)
    {
        try { return path.Length > 0 && Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    // DIAG354-1: the retained run is the only source for this report, and the retained run is the only
    // source of the page rows, so a later navigation, filter, account selection or state change cannot
    // alter what the page, the preview, the copy, the save and the CLI report say. Only the report's own
    // generated instant is new on each render, and it is kept explicitly distinct from the instant the
    // checks were made.
    private string BuildDiagnosticReportText() =>
        RenderDiagnosticReportText(_diagnosticRetainedRun, DateTimeOffset.Now);

    private string RenderDiagnosticReportText(DiagnosticRun? run, DateTimeOffset generatedUtc)
    {
        var text = new StringBuilder();
        text.AppendLine("Sync Shark support report (local, redacted)");
        text.AppendLine("Generated: " + generatedUtc.ToString("O") + " (" + TimeZoneInfo.Local.Id + ")");
        if (run is null)
        {
            // No valid prior run is reported as a truthful not-run state, never as a fresh or stale result.
            text.AppendLine("Checked: not run");
            text.AppendLine("Checks: not run in this session. Run Diagnostics, or the diagnostics run command, " +
                "to check this installation and its retained state.");
            text.AppendLine(NoShareableDetailNote);
            return text.ToString();
        }
        var snapshot = run.Snapshot;
        var checkedAt = FormatDiagnosticTime(run.CheckedUtc);
        text.AppendLine("Checked: " + checkedAt + " (" + TimeZoneInfo.Local.Id + ")");
        text.AppendLine("Product: Sync Shark " + snapshot.Version);
        text.AppendLine("Information version: " + snapshot.InformationVersion);
        text.AppendLine("Publisher: " + snapshot.Publisher);
        text.AppendLine("Source revision: " + snapshot.SourceRevision);
        text.AppendLine("Signed installed application: " + (snapshot.IsInstalledProcess ? "yes" : "no"));
        text.AppendLine("Folder pairs: " + snapshot.Pairs.Count + " | Loaded recovery records: " +
            snapshot.RecoveryRecords + " | Pending operations: " + snapshot.PendingOperations +
            " | Attention pairs: " + snapshot.AttentionPairs + " | Active run: " + snapshot.RunActive +
            " | Paused: " + snapshot.Paused);
        text.AppendLine("Loaded recovery records are only the records loaded into the Recovery page view for the " +
            "pair they were loaded for. Zero loaded records does not prove that no backup is retained.");
        if (_diagnosticFailureMessage is { } failure)
            text.AppendLine("Last run attempt: failed (" + failure + "). The result below is unchanged and is " +
                "still the result checked at the time shown above.");
        text.AppendLine("Checks (checked " + checkedAt + "):");
        // Names, absolute paths and user-supplied labels stay on the local page. The shareable report uses
        // each check's own safe label and safe detail, and unknown exception text is never exported.
        foreach (var check in run.Checks)
            text.AppendLine("  [" + check.Outcome + "] " + check.ReportCheck + " - " + check.ReportDetail +
                " (" + check.Freshness + ")");
        text.AppendLine(NoShareableDetailNote);
        return text.ToString();
    }

    private const string NoShareableDetailNote = "No credential, token, account address, connection identifier, " +
        "local path or folder pair name is included in this report.";

    // DIAG354-4: the structured, safe projection of one completed run. The report string is the same
    // redacted report the local copy, preview and save actions use, so the plain and JSON command output
    // and the page cannot disagree.
    private SyncCliDiagnosticsData BuildCliDiagnosticsData(DiagnosticRun run) => new(
        run.CheckedUtc.ToLocalTime().ToString("O"), BuildDiagnosticReportText(), run.Checks.Length,
        run.Checks.Count(check => check.Outcome == DiagnosticOutcomes.Pass),
        run.Checks.Count(check => check.Outcome == DiagnosticOutcomes.Warning),
        run.Checks.Count(check => check.Outcome == DiagnosticOutcomes.Failed),
        run.Checks.Count(check => check.Outcome == DiagnosticOutcomes.Skipped),
        run.Checks.Any(check => check.Outcome is DiagnosticOutcomes.Failed or DiagnosticOutcomes.Warning),
        run.Checks.Select(check => new SyncCliDiagnosticsCheckData(check.ReportCheck, check.Outcome,
            check.ReportDetail, check.Freshness)).ToArray(),
        "Runs the same shared read-only diagnostic action as the Diagnostics page. It shows no dialog, copies " +
        "nothing, creates no file, contacts no provider and reads no credential. The check names, details and " +
        "report are redacted. Loaded recovery records are only the records loaded into the Recovery page view " +
        "for the pair they were loaded for, so zero loaded records do not prove that no backup is retained.");
}
