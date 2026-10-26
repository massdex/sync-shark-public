using Massdex.SyncShark;
using System.IO;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// U4 Settings structure consumer checks against the real MainWindow, its real tab set and its real
// settings controls. Isolated fixture state only: no provider, installation, signature, startup or
// system change is made, and the diagnostics run is proven to leave files and sync state alone.
internal static class SettingsStructureChecks
{
    private static readonly string[] ExpectedTabs =
        ["General", "Sync & Performance", "Notifications", "Explorer", "About & Updates", "Diagnostics"];

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(instance.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1180, Height = 800 };
        window.StartupRegistrationForTest = _ => throw new InvalidOperationException(
            "This settings check must not modify Windows startup registration.");
        var presentRoot = Path.Combine(root, "present");
        var missingRoot = Path.Combine(root, "missing");
        Directory.CreateDirectory(presentRoot);
        using var lease = PilotDirectoryLease.Open(presentRoot);
        var present = new SyncPair("settings-present", "Present Pair", presentRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/present", "id:present");
        var missing = present with { Id = "settings-missing", Name = "Missing Pair", LocalRoot = missingRoot,
            DropboxRoot = "/missing", DropboxFolderId = "id:missing" };
        try
        {
            window.Show(); Drain();
            window.LoadPairSetForTest(new SyncPairSet([present, missing], present.Id));
            Call(window, "NavigateTo", "Settings", true); Drain();
            CheckTabOrder(window);
            CheckGeneralDraftAndRestart(window, instance.StateAuthority, preferences);
            CheckAppearanceAndReplacedControls(window, preferences);
            CheckSyncAndPerformance(window, instance.StateAuthority);
            CheckNotifications(window);
            CheckExplorer(window);
            CheckAboutAndUpdates(window);
            StartRunningApplicationBridge(app, window, instance, theme);
            CheckDiagnostics(window, root, presentRoot, missingRoot, instance, present);
            Console.WriteLine("PASS Settings structure consumer checks; no provider, install or system effect");
            return 0;
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = false;
            window.RequestProductExit();
            app.Shutdown();
        }
    }

    private static void CheckTabOrder(MainWindow window)
    {
        var tabs = Field<TabControl>(window, "_settingsTabs");
        Require(tabs.Items.Count == ExpectedTabs.Length, "Settings exposes exactly six tabs");
        for (var index = 0; index < ExpectedTabs.Length; index++)
        {
            Select(window, index);
            Require(((TabItem)tabs.Items[index]).Header.ToString() == ExpectedTabs[index],
                "Settings tab " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " is " + ExpectedTabs[index]);
        }
    }

    private static void CheckAppearanceAndReplacedControls(MainWindow window, UiPreferencesStore preferences)
    {
        Select(window, 0);
        var appearance = Field<ComboBox>(window, "_settingsAppearance");
        var original = preferences.Load().Theme;
        foreach (var option in appearance.ItemsSource.Cast<string>().ToArray())
        {
            appearance.SelectedItem = option; Drain();
            var save = (Task<bool>)Call(window, "SaveSettingsCategoryAsync", 0)!; Pump(save);
            Require(save.Result && preferences.Load().Theme == option,
                "Actual Appearance selection saves and reloads " + option);
        }
        appearance.SelectedItem = original; Drain();
        Pump((Task<bool>)Call(window, "SaveSettingsCategoryAsync", 0)!);
        foreach (var name in new[] { "SystemThemeChoice", "LightThemeChoice", "AmoledThemeChoice", "SaveTransferSettingsButton" })
        {
            var old = window.FindName(name) as DependencyObject
                ?? throw new InvalidDataException("Static inventory field missing: " + name);
            Require(Window.GetWindow(old) is null,
                "Legacy XAML control " + name + " is detached; the current Settings control is the tested dynamic surface");
        }
    }

    private static void CheckGeneralDraftAndRestart(MainWindow window, PilotStateAuthority authority,
        UiPreferencesStore preferences)
    {
        Select(window, 0);
        var style = Field<ComboBox>(window, "_settingsIconStyle");
        var save = Field<Button>(window, "_settingsSave");
        var discard = Field<Button>(window, "_settingsDiscard");
        var saved = preferences.Load().StatusIconStyle;
        var chosen = saved == "Colour" ? "Symbol" : "Colour";
        Require(style.ItemsSource.Cast<string>().SequenceEqual(new[] { "Colour", "Symbol", "Combined" }),
            "General exposes the three persisted status icon styles in order");
        Require((string?)style.SelectedItem == saved, "The General status icon control starts on the saved style");
        Require(Texts(window, 0).Contains("Status Icons"),
            "The icon style is an ordinary General setting row");
        Require(Visuals(ContentOf(window, 0)).OfType<ShellTypeImage>().Count() >= 3,
            "The General status icon row previews the real shared item marks");
        Require(save.Visibility != Visibility.Visible, "A clean General category hides Save");
        style.SelectedItem = chosen;
        Require(save.Visibility == Visibility.Visible && discard.Visibility == Visibility.Visible,
            "Choosing a status icon style marks the General draft dirty and offers Save and Discard");
        Require(preferences.Load().StatusIconStyle != chosen,
            "The General icon preview does not persist an unsaved draft");
        foreach (var index in new[] { 3, 4, 5 })
        {
            Select(window, index);
            Require(save.Visibility != Visibility.Visible && discard.Visibility != Visibility.Visible,
                ExpectedTabs[index] + " collapses Save and Discard for the General draft");
        }
        Select(window, 0);
        Require(save.Visibility == Visibility.Visible && (string?)style.SelectedItem == chosen,
            "Returning to General keeps the same unsaved status icon draft");
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
        Require((string?)style.SelectedItem == preferences.Load().StatusIconStyle &&
            save.Visibility != Visibility.Visible, "Discard restores the saved status icon style");
        style.SelectedItem = chosen;
        var saveOutcome = (Task<bool>)Call(window, "SaveSettingsCategoryAsync", 0)!;
        Pump(saveOutcome);
        Require(saveOutcome.Result,
            "The General category saves the chosen status icon style");
        Require(preferences.Load().StatusIconStyle == chosen,
            "Save persists the chosen status icon style");
        Require(new UiPreferencesStore(authority).Load().StatusIconStyle == chosen,
            "A fresh preference load after restart returns the saved status icon style");
    }

    private static void CheckSyncAndPerformance(MainWindow window, PilotStateAuthority authority)
    {
        Select(window, 1);
        var texts = Texts(window, 1);
        var transfers = texts.IndexOf("Transfers");
        var discovery = texts.IndexOf("Fast File Discovery");
        Require(transfers >= 0 && discovery > transfers,
            "Sync & Performance groups Transfers before Fast File Discovery");
        Require(texts.Contains("Automatic Sync") && texts.Contains("Concurrent Pairs") &&
            texts.Contains("Upload Limit") && texts.Contains("Download Limit"),
            "Sync & Performance still carries the transfer and concurrency controls");
        Require(texts.Contains("Status") && texts.Contains("Set Up or Update") && texts.Contains("Check File Metadata"),
            "Discovery status, setup and its read-only check are explicit rows");
        var expander = Visuals(ContentOf(window, 1)).OfType<Expander>().Single();
        Require(!expander.IsExpanded, "Discovery advanced maintenance is collapsed by default");
        var maintenance = Logical((DependencyObject)expander.Content!).OfType<TextBlock>()
            .Select(text => text.Text).ToArray();
        Require(maintenance.Contains("Restore Previous Setup") && maintenance.Contains("Remove Setup"),
            "Discovery restore and remove are grouped as advanced maintenance actions");
        Require(!maintenance.Contains("Status") && !maintenance.Contains("Set Up or Update") &&
            !maintenance.Contains("Check File Metadata"),
            "Discovery status, setup and its read-only check stay ordinary rows outside advanced maintenance");

        // Decision 76: the Online-only section owns one draft control with the wording the owner chose.
        var onlineOnly = texts.IndexOf("Online-only");
        Require(onlineOnly >= 0 && texts.Contains("Return opened online-only files to the cloud after:"),
            "Sync & Performance carries the Online-only section with the decision 76 wording");
        var returnInterval = Field<ComboBox>(window, "_settingsOnlineOnlyReturn");
        Require(returnInterval.ItemsSource.Cast<string>()
                .SequenceEqual(new[] { "1 day", "1 hour", "1 week", "Never" }),
            "The Online-only return interval offers 1 day, 1 hour, 1 week and Never");
        var store = new SyncTransferSettingsStore(authority);
        Require((string?)returnInterval.SelectedItem == store.Load().OnlineOnlyReturnInterval &&
                (string?)returnInterval.SelectedItem == "1 day",
            "The Online-only return control starts on the saved 1 day default");
        var save = Field<Button>(window, "_settingsSave");
        var discard = Field<Button>(window, "_settingsDiscard");
        returnInterval.SelectedItem = "1 week";
        Require(returnInterval.SelectedItem?.ToString() == "1 week" &&
                save.Visibility == Visibility.Visible && discard.Visibility == Visibility.Visible,
            "Changing the Online-only return interval marks the Sync & Performance draft dirty");
        Require(store.Load().OnlineOnlyReturnInterval == "1 day",
            "The Online-only return draft is not persisted before Save");
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
        Require((string?)returnInterval.SelectedItem == "1 day" && save.Visibility != Visibility.Visible,
            "Discard restores the saved Online-only return interval");
        returnInterval.SelectedItem = "1 hour";
        var saveOutcome = (Task<bool>)Call(window, "SaveSettingsCategoryAsync", 1)!;
        Pump(saveOutcome);
        Require(saveOutcome.Result && new SyncTransferSettingsStore(authority).Load().OnlineOnlyReturnInterval == "1 hour",
            "Save persists the chosen Online-only return interval");
    }

    private static void CheckNotifications(MainWindow window)
    {
        Select(window, 2);
        var texts = Texts(window, 2);
        Require(!texts.Contains("Open Tray") && !texts.Contains("Tray"),
            "Notifications carries preferences only, with no tray navigation row");
        foreach (var row in new[] { "Needs Attention", "Sync Completed", "Transfer Problems", "Additional Alert Sound" })
            Require(texts.Contains(row), "Notifications still offers " + row);
    }

    private static void CheckExplorer(MainWindow window)
    {
        Select(window, 3);
        var status = Field<TextBlock>(window, "_explorerSettingsStatus");
        Require(status.Text.Length > 0, "The Explorer tab reports its registration state on the page");
        var setup = Field<Button>(window, "_explorerSetupButton");
        setup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
        Require(status.Text.Contains("signed installed app") && status.Text.Contains("Nothing was changed"),
            "Explorer setup reports its refusal locally instead of silently doing nothing");
        Require(setup.IsEnabled, "Explorer setup re-enables itself after reporting the result");
        Require(!window.RunActiveForTest, "An Explorer setup attempt never starts a sync run");
        var texts = Texts(window, 3);
        Require(texts.Contains("Registration") && texts.Contains("Repair Integration"),
            "The Explorer tab carries registration status and the repair action");
        Require(!texts.Any(text => text.Contains("Fast File Discovery")),
            "Fast File Discovery stays under Sync & Performance");
    }

    private static void CheckAboutAndUpdates(MainWindow window)
    {
        Select(window, 4);
        var texts = Texts(window, 4);
        var product = texts.IndexOf("Installed Product");
        var version = texts.FindIndex(text => text.StartsWith("Version ", StringComparison.Ordinal));
        var publisher = texts.IndexOf("Publisher: Massdex");
        Require(MainWindow.ProductPublisher == "Massdex" &&
            !texts.Any(text => text.Contains(ExplorerPackage.Publisher, StringComparison.Ordinal)),
            "Product publisher comes from app metadata, not the legacy Explorer package identity");
        var status = texts.IndexOf("Update Status");
        var primary = texts.IndexOf("Check for Updates");
        var automatic = texts.IndexOf("Automatic Checks");
        var uninstall = texts.IndexOf("Uninstall Sync Shark");
        Require(product >= 0 && version > product && publisher > product,
            "About & Updates shows product, version and publisher inline");
        Require(status > product && primary > status && automatic > primary,
            "Update status, the primary update action and the automatic-check preference are ordered inline");
        Require(uninstall > automatic, "Uninstall is the last action on About & Updates");
        Require(!texts.Contains("About"), "The one-value About popup action is gone");
        Require(!texts.Any(text => text.StartsWith("Installed Version", StringComparison.Ordinal)),
            "The duplicated installed-version row is gone");
        // UI354-TTIP1: every update action carries concise help that differs from its caption and states
        // its real effect, published as both the tooltip and the accessibility help.
        var helpCases = new (string Field, string Caption, string MustMention)[]
        {
            ("_updateCheck", "Check for Updates", "public release channel"),
            ("_updateDownload", "Download and Check", "verifies its signature"),
            ("_updateInstall", "Install and Restart", "rollback")
        };
        foreach (var helpCase in helpCases)
        {
            var button = Field<Button>(window, helpCase.Field);
            var help = button.ToolTip as string ?? string.Empty;
            Require(help.Length > 0 && help.Contains(helpCase.MustMention, StringComparison.Ordinal) &&
                !string.Equals(help, helpCase.Caption, StringComparison.Ordinal),
                "Update help for " + helpCase.Caption + " describes its real effect instead of repeating the caption");
            Require(System.Windows.Automation.AutomationProperties.GetHelpText(button) == help,
                "The accessible help for " + helpCase.Caption + " matches its tooltip");
        }
        var automaticButton = Field<Button>(window, "_updateAutomatic");
        var automaticHelp = automaticButton.ToolTip as string ?? string.Empty;
        Require(automaticHelp.Contains("automatic update check", StringComparison.Ordinal) &&
            automaticHelp.Contains("File syncing", StringComparison.Ordinal) &&
            !string.Equals(automaticHelp, "Pause Update Checks", StringComparison.Ordinal) &&
            !string.Equals(automaticHelp, "Resume Update Checks", StringComparison.Ordinal),
            "Pause and Resume help states that only automatic update checks are affected, not file syncing");
        Require(System.Windows.Automation.AutomationProperties.GetHelpText(automaticButton) == automaticHelp,
            "The accessible help for the automatic-check action matches its tooltip");
        foreach (var caption in new[] { "Check Downloaded Package", "Choose Local Update Folder" })
        {
            var button = UpdateButtons(window).FirstOrDefault(candidate =>
                System.Windows.Automation.AutomationProperties.GetName(candidate) == caption);
            var help = button?.ToolTip as string ?? string.Empty;
            Require(button is not null && help.Length > 0 &&
                !string.Equals(help, caption, StringComparison.Ordinal) &&
                System.Windows.Automation.AutomationProperties.GetHelpText(button) == help,
                "Update help for " + caption + " differs from its caption and is published as accessible help");
        }
    }

    /// <summary>Every realised button under the window, used to find the inline update actions by name.</summary>
    private static IEnumerable<Button> UpdateButtons(DependencyObject parent)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            if (node is Button button) yield return button;
            foreach (var nested in UpdateButtons(node)) yield return nested;
        }
    }

    private static void CheckDiagnostics(MainWindow window, string root, string presentRoot, string missingRoot,
        PilotInstanceLock instance, SyncPair present)
    {
        Select(window, 5);
        Require(!Texts(window, 5).Contains("Event Log"),
            "Diagnostics carries no duplicate Event Log navigation row");

        // DIAG354-1: before any completed run the report is a truthful not-run state, never a fabricated
        // or stale fresh result.
        var notRunReport = (string)Call(window, "BuildDiagnosticReportText")!;
        Require(notRunReport.Contains("Checked: not run", StringComparison.Ordinal) &&
                notRunReport.Contains("Checks: not run in this session", StringComparison.Ordinal) &&
                !notRunReport.Contains("[Pass]", StringComparison.Ordinal),
            "The support report reports a truthful not-run state before any completed run");

        // DIAG354-2: two real retained active accounts, with a retained session check for only one of them.
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var firstConnection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:diagnostics-one", "diagnostics-root-one", "diagnostics-home-one",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        Pump(connections.SaveNewAsync(firstConnection));
        var secondConnection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:diagnostics-two", "diagnostics-root-two", "diagnostics-home-two",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        Pump(connections.SaveAsync(secondConnection with
        {
            CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(secondConnection.ConnectionId)
        }));
        var known = Field<Dictionary<string, SyncAccountStatus>>(window, "_knownAccountStatus");
        var binding = new SyncAccountBinding(firstConnection.ConnectionId, SyncProviders.Dropbox, "secret-account-id",
            "Display Name", "private@example.invalid", "pro", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        known[firstConnection.ConnectionId] = new SyncAccountStatus(binding, true, true, "Connected", null, 1,
            DateTimeOffset.UtcNow);

        // DIAG354-3: real local/local, reversed cloud/local and cloud/cloud pairs, one pair whose saved
        // endpoint document cannot be read, and one pair whose first-sync review is still outstanding.
        var localB = Directory.CreateDirectory(Path.Combine(root, "local-b")).FullName;
        var localC = Directory.CreateDirectory(Path.Combine(root, "local-c")).FullName;
        var localD = Directory.CreateDirectory(Path.Combine(root, "local-d")).FullName;
        using var leaseB = PilotDirectoryLease.Open(localB);
        using var leaseC = PilotDirectoryLease.Open(localC);
        using var leaseD = PilotDirectoryLease.Open(localD);
        var cloudOne = new SyncEndpointRecord(SyncEndpointKind.Dropbox, Provider: SyncProviders.Dropbox,
            ConnectionId: firstConnection.ConnectionId, NamespaceId: "ns:one", FolderId: "id:one");
        var cloudTwo = new SyncEndpointRecord(SyncEndpointKind.Dropbox, Provider: SyncProviders.Dropbox,
            ConnectionId: secondConnection.ConnectionId, NamespaceId: "ns:two", FolderId: "id:two");
        var missing = present with
        {
            Id = "settings-missing", Name = "Missing Pair", LocalRoot = missingRoot,
            DropboxRoot = "/missing", DropboxFolderId = "id:missing"
        };
        var localLocal = new SyncPair("settings-local-local", "Local Local", localB,
            leaseB.Identity.VolumeSerialNumber, leaseB.Identity.FileId, string.Empty, string.Empty,
            EndpointSchemaVersion: SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint: new SyncEndpointRecord(SyncEndpointKind.Local, localB,
                leaseB.Identity.VolumeSerialNumber, leaseB.Identity.FileId),
            RightEndpoint: new SyncEndpointRecord(SyncEndpointKind.Local, localC,
                leaseC.Identity.VolumeSerialNumber, leaseC.Identity.FileId));
        var reversed = new SyncPair("settings-reversed", "Reversed Pair", localD,
            leaseD.Identity.VolumeSerialNumber, leaseD.Identity.FileId, string.Empty, string.Empty,
            EndpointSchemaVersion: SyncEndpointSchema.SymmetricEndpoints, LeftEndpoint: cloudOne,
            RightEndpoint: new SyncEndpointRecord(SyncEndpointKind.Local, localD,
                leaseD.Identity.VolumeSerialNumber, leaseD.Identity.FileId));
        var cloudCloud = present with
        {
            Id = "settings-cloud-cloud", Name = "Cloud Cloud",
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = cloudOne, RightEndpoint = cloudTwo
        };
        var malformed = present with
        {
            Id = "settings-malformed", Name = "Malformed Pair", EndpointSchemaVersion = 99
        };
        var firstSync = present with
        {
            Id = "settings-first-sync", Name = "First Sync Pair", InitialReviewApproved = false
        };
        window.LoadPairSetForTest(new SyncPairSet([present, missing, localLocal, reversed, cloudCloud, malformed,
            firstSync], present.Id)); Drain();
        var expectedAttention = DistinctAttentionPairs(window);
        Require(expectedAttention >= 1 && Field<HashSet<string>>(window, "_attentionPairIds").Count == 0,
            "The fixture reaches the shared attention classifier from real pairs while the old attention projection is empty");
        var filesBefore = Directory.GetFileSystemEntries(presentRoot).Length;
        Pump((Task)Call(window, "RunDiagnosticsAsync")!);
        var checks = ((System.Collections.IEnumerable)Field<object>(window, "_diagnosticChecks")).Cast<object>().ToArray();
        var outcomes = checks.Select(check => (string)check.GetType().GetProperty("Outcome")!.GetValue(check)!).ToArray();
        var names = checks.Select(check => (string)check.GetType().GetProperty("Check")!.GetValue(check)!).ToArray();
        var details = checks.Select(check => (string)check.GetType().GetProperty("Detail")!.GetValue(check)!).ToArray();
        Require(outcomes.Contains("Failed") && outcomes.Contains("Pass") && outcomes.Contains("Skipped"),
            "Diagnostics reports real pass, failed and skipped outcomes together");
        Require(names.Any(name => name.Contains("Missing Pair")) &&
            details.Any(detail => detail.Contains(missingRoot, StringComparison.OrdinalIgnoreCase)),
            "A missing local folder is reported as a failed check with the exact reason");
        Require(names.Contains("Cloud folders") && outcomes[Array.IndexOf(names, "Cloud folders")] == "Skipped",
            "Provider folders are skipped with a reason instead of being claimed");
        Require(!names.Any(name => name.Contains("secret")) && details.All(detail => !detail.Contains("private@example.invalid", StringComparison.OrdinalIgnoreCase)),
            "The check rows identify accounts without exposing account addresses or connection identifiers");
        var status = Field<TextBlock>(window, "_diagnosticStatus").Text;
        Require(status.Contains("passed") && status.Contains("failed") && status.Contains("skipped"),
            "The page reports the real pass, failed and skipped counts after a run");
        Require(!window.RunActiveForTest && Directory.GetFileSystemEntries(presentRoot).Length == filesBefore,
            "Running diagnostics starts no sync and changes no file");
        var report = (string)Call(window, "BuildDiagnosticReportText")!;
        Require(report.Contains("Publisher: Massdex", StringComparison.Ordinal) &&
            !report.Contains(ExplorerPackage.Publisher, StringComparison.Ordinal),
            "The diagnostic report uses the same product publisher as About, not package compatibility identity");
        Require(report.Contains("redacted") && report.Contains("[Failed]") && report.Contains("[Skipped]"),
            "The support report carries the structured check outcomes");
        Require(!report.Contains("private@example.invalid", StringComparison.OrdinalIgnoreCase) &&
            !report.Contains("secret-connection-id", StringComparison.Ordinal) &&
            !report.Contains("secret-account-id", StringComparison.Ordinal) &&
            !report.Contains("Display Name", StringComparison.Ordinal),
            "The redacted report omits account addresses, display names and connection identifiers");
        Require(!report.Contains(missingRoot, StringComparison.OrdinalIgnoreCase) &&
            !report.Contains("Missing Pair", StringComparison.Ordinal) &&
            !report.Contains(presentRoot, StringComparison.OrdinalIgnoreCase),
            "The exported report omits local folder paths and user-chosen pair names");
        var target = Path.Combine(root, "diagnostic-report.txt");
        window.DiagnosticPreviewConfirmForTest = () => false;
        window.DiagnosticSavePathForTest = () => target;
        Call(window, "PreviewDiagnosticReport");
        Require(!File.Exists(target), "Cancelling the report preview writes no file");
        window.DiagnosticPreviewConfirmForTest = () => true;
        Call(window, "PreviewDiagnosticReport");
        Require(File.Exists(target) && StripGenerated(File.ReadAllText(target)) == StripGenerated(report),
            "The confirmed save writes exactly the previewed redacted report");
        window.DiagnosticPreviewConfirmForTest = null;
        window.DiagnosticSavePathForTest = null;

        // DIAG354-3: every typed endpoint shape is inspected through its own real sides, and only its real
        // local sides are inspected at all.
        var localChecks = names.Where(name => name.Contains("local folder", StringComparison.Ordinal)).ToArray();
        // Seven fixture pairs: one local side each for the two legacy local/cloud pairs, two for the
        // local/local pair, one for the reversed cloud/local pair, one for the first-sync pair, and none
        // for the cloud/cloud pair.
        Require(localChecks.Length == 6 &&
                localChecks.Count(name => name.Contains("Local Local", StringComparison.Ordinal)) == 2 &&
                localChecks.Any(name => name.Contains("Reversed Pair", StringComparison.Ordinal) &&
                    name.Contains("right local folder", StringComparison.Ordinal)) &&
                !localChecks.Any(name => name.Contains("Reversed Pair", StringComparison.Ordinal) &&
                    name.Contains("left local folder", StringComparison.Ordinal)),
            "Both local sides of a local/local pair, and only the local side of a reversed cloud/local pair, are inspected; " +
            "observed: " + string.Join(" | ", names.Select((name, index) => name + "=" + outcomes[index])));
        Require(outcomes[Array.IndexOf(names, "Folder pair: Cloud Cloud")] == "Skipped" &&
                details[Array.IndexOf(names, "Folder pair: Cloud Cloud")]
                    .Contains("Both endpoints are cloud folders", StringComparison.Ordinal) &&
                !localChecks.Any(name => name.Contains("Cloud Cloud", StringComparison.Ordinal)),
            "A cloud/cloud pair has no local endpoint to check and is skipped without any provider access");
        var malformedIndex = Array.IndexOf(names, "Folder pair: Malformed Pair");
        Require(malformedIndex >= 0 && outcomes[malformedIndex] == "Failed" &&
                details[malformedIndex].Contains("cannot be described safely", StringComparison.Ordinal) &&
                Directory.Exists(presentRoot),
            "An unreadable typed endpoint document is one bounded failure rather than a guessed legacy local-root fallback");

        // DIAG354-2: global attention is the shared classifier's distinct pair set, and every retained
        // active account is represented with the account that has no retained check reported as not checked.
        var attentionIndex = Array.IndexOf(names, "Folder pairs needing attention");
        Require(attentionIndex >= 0 && outcomes[attentionIndex] == "Warning" &&
                details[attentionIndex].Contains(expectedAttention.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " folder pair(s) need input", StringComparison.Ordinal),
            "Global attention reports the shared classifier's own distinct pair count");
        var accountNames = names.Where(name => name.StartsWith("Account ", StringComparison.Ordinal)).ToArray();
        var accountOutcomes = accountNames.Select(name => outcomes[Array.IndexOf(names, name)]).ToArray();
        Require(accountNames.Length == 2 && accountOutcomes.Count(outcome => outcome == "Pass") == 1 &&
                accountOutcomes.Count(outcome => outcome == "Skipped") == 1,
            "Every retained active account is represented and the account with no retained check is not checked");
        var uncachedIndex = Array.IndexOf(names, accountNames[Array.IndexOf(accountOutcomes, "Skipped")]);
        Require(details[uncachedIndex].Contains("No account check result is retained", StringComparison.Ordinal) &&
                details[uncachedIndex].Contains("not a missing credential", StringComparison.Ordinal),
            "An account with no retained check is neither a missing credential nor a healthy account");
        var recoveryIndex = Array.IndexOf(names, "Loaded recovery records");
        Require(recoveryIndex >= 0 && details[recoveryIndex]
                    .Contains("does not prove that no backup is retained", StringComparison.Ordinal) &&
                report.Contains("Loaded recovery records are only the records loaded", StringComparison.Ordinal),
            "Recovery rows are reported as loaded-view records whose zero count proves nothing about retention");

        // DIAG354-1: after the run, change displayed pairs, attention, account selection, pending work and
        // recovery rows, then prove the page rows and the report are still exactly that one run.
        Field<HashSet<string>>(window, "_attentionPairIds").Add("unrelated-pair-id");
        SetField(window, "_globalPendingQueueCount", 7);
        SetField(window, "_selectedConnectionId", secondConnection.ConnectionId);
        window.RecoveryRows.Clear();
        window.LoadPairSetForTest(new SyncPairSet([present], present.Id)); Drain();
        var afterMutation = (string)Call(window, "BuildDiagnosticReportText")!;
        Require(StripGenerated(afterMutation) == StripGenerated(report),
            "The report still renders the retained run after pairs, attention, account selection, pending work and recovery rows changed");
        var checksAfterMutation = ((System.Collections.IEnumerable)Field<object>(window, "_diagnosticChecks"))
            .Cast<object>().Select(item => (string)item.GetType().GetProperty("Check")!.GetValue(item)!).ToArray();
        Require(checksAfterMutation.SequenceEqual(names),
            "The page rows still render the retained run's own checks after the same changes");

        // DIAG354-1: a failed run is reported truthfully and never replaces or relabels the retained result.
        MainWindow.DiagnosticCaptureFailureForTest =
            new InvalidOperationException("controlled fixture capture failure");
        try { Pump((Task)Call(window, "RunDiagnosticsAsync")!); }
        finally { MainWindow.DiagnosticCaptureFailureForTest = null; }
        var failureStatus = Field<TextBlock>(window, "_diagnosticStatus").Text;
        var afterFailure = (string)Call(window, "BuildDiagnosticReportText")!;
        Require(failureStatus.Contains("Diagnostics Could Not Finish", StringComparison.Ordinal) &&
                failureStatus.Contains("previous result is unchanged", StringComparison.Ordinal) &&
                failureStatus.Contains("InvalidOperationException", StringComparison.Ordinal),
            "A failed run reports the real failure class without replacing the retained result");
        Require(CheckedLine(afterFailure) == CheckedLine(report) && CheckedLine(afterFailure) != "Checked: not run",
            "A failed run never relabels the retained result's checked time as fresh");
        Require(afterFailure.Contains("Last run attempt: failed", StringComparison.Ordinal),
            "The report states the failed attempt alongside the unchanged retained result");

        // DIAG354-4/5: the production registry routes diagnostics to this window's own running-application
        // action and returns the same redacted outcomes and report, with no dialog, clipboard or file effect.
        window.LoadPairSetForTest(new SyncPairSet([present, missing, localLocal, reversed, cloudCloud, malformed,
            firstSync], present.Id)); Drain();
        var filesBeforeCli = Directory.GetFileSystemEntries(root).Length;
        var cliRuntime = new CliAppRuntime(window.AuthorityHost, instance.StateAuthority, () => false, () => false,
            () => { }, () => Task.CompletedTask, appAction: window.ExecuteCliAppActionAsync);
        var cliOutput = new StringWriter();
        var cliError = new StringWriter();
        var cliTask = SyncCli.ExecuteAsync(["--cli", "diagnostics", "run", "--json"], cliOutput, cliError,
            () => cliRuntime);
        Pump(cliTask);
        // D2 diagnostic instrumentation (synthetic fixture output only; every assertion below is unchanged): emit
        // the actual CLI exit code, output and error together with the exact receiver window's owning process and
        // this fixture's own process, read through the existing transport helpers, so the failure can be classified
        // from the real values instead of inferred.
        var diagnosticReceiver = SyncCliBridgeTransport.FindWindow(null, SyncCliBridgeProtocol.ReceiverWindowTitle);
        var diagnosticReceiverPid = 0u;
        if (diagnosticReceiver != IntPtr.Zero)
            SyncCliBridgeTransport.GetWindowThreadProcessId(diagnosticReceiver, out diagnosticReceiverPid);
        Console.WriteLine("DIAG-D2 exit=" + cliTask.Result.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " receiverHandle=0x" + diagnosticReceiver.ToInt64().ToString("x") +
            " receiverPid=" + diagnosticReceiverPid.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " ownPid=" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " stdout=" + SyncCliDiagnosticSnippet(cliOutput.ToString()) +
            " stderr=" + SyncCliDiagnosticSnippet(cliError.ToString()));
        // Runtime selection is intentional CLI feedback on stderr, not a command failure.
        // Accept exactly that one line for this fixture's actual authority process; unexpected
        // errors/progress still fail, and stdout must remain the single success object below.
        var runtimeLines = cliError.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        const string runtimePrefix = "Sync Shark: [";
        var runtimeSuffix = $"] Execution runtime: running-application; authority process {Environment.ProcessId}.";
        var runtimeLine = runtimeLines.Length == 1 ? runtimeLines[0] : "";
        var timestampLength = runtimeLine.Length - runtimePrefix.Length - runtimeSuffix.Length;
        var runtimeFeedbackIsExact = timestampLength > 0 &&
            runtimeLine.StartsWith(runtimePrefix, StringComparison.Ordinal) &&
            runtimeLine.EndsWith(runtimeSuffix, StringComparison.Ordinal) &&
            DateTimeOffset.TryParse(runtimeLine.Substring(runtimePrefix.Length, timestampLength),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _);
        Require(cliTask.Result == 0 && runtimeFeedbackIsExact,
            "The production registry runs diagnostics inside this exact application, reports its runtime once and exits 0");
        var envelope = JsonNode.Parse(cliOutput.ToString().Trim())!.AsObject();
        Require(envelope["schemaVersion"]!.GetValue<int>() == 1 &&
                envelope["command"]!.GetValue<string>() == "diagnostics run" && envelope["ok"]!.GetValue<bool>(),
            "The diagnostics command returns one schemaVersion 1 success object");
        var cliData = envelope["data"]!.AsObject();
        var cliChecks = cliData["checks"]!.AsArray();
        Require(cliData["total"]!.GetValue<int>() == cliChecks.Count && cliData["failed"]!.GetValue<int>() >= 1 &&
                cliData["passed"]!.GetValue<int>() + cliData["warnings"]!.GetValue<int>() +
                    cliData["failed"]!.GetValue<int>() + cliData["skipped"]!.GetValue<int>() ==
                    cliData["total"]!.GetValue<int>(),
            "The CLI returns the retained run's real structured check tallies");
        var cliReport = cliData["report"]!.GetValue<string>();
        Require(StripGenerated(cliReport) == StripGenerated((string)Call(window, "BuildDiagnosticReportText")!),
            "The CLI returns the identical redacted report the page previews, copies and saves");
        foreach (var secret in new[] { firstConnection.ConnectionId, secondConnection.ConnectionId,
                     "private@example.invalid", "Display Name", "secret-account-id", presentRoot, missingRoot,
                     localB, localC, localD, "Present Pair", "Missing Pair", "Local Local", "Reversed Pair",
                     "Cloud Cloud", "Malformed Pair", "First Sync Pair" })
            Require(!cliReport.Contains(secret, StringComparison.OrdinalIgnoreCase) &&
                    !cliOutput.ToString().Contains(secret, StringComparison.OrdinalIgnoreCase),
                "The diagnostics command output omits " + secret);
        Require(cliChecks.All(item => !item!["check"]!.GetValue<string>().Contains("Pair", StringComparison.Ordinal)),
            "Every CLI check name uses a safe ordinal label instead of a user-chosen pair name");
        Require(Directory.GetFileSystemEntries(root).Length == filesBeforeCli &&
                !Field<bool>(window, "_diagnosticRunning"),
            "The diagnostics command created no file and left no run in progress");
    }

    // Only the generation timestamp may differ between two reads of the same report.
    private static string StripGenerated(string report) => string.Join("\n",
        report.Split('\n').Where(line => !line.StartsWith("Generated:", StringComparison.Ordinal)));

    /// <summary>The checked instant line of a report, used to prove a failed run never relabels it.</summary>
    private static string CheckedLine(string report) => report.Split('\n')
        .FirstOrDefault(line => line.StartsWith("Checked: ", StringComparison.Ordinal))?.TrimEnd('\r') ?? string.Empty;

    /// <summary>The expected global attention count, computed from the window's own shared classifier.</summary>
    private static int DistinctAttentionPairs(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("BuildAttentionCases",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ids = new List<string>();
        foreach (var item in (System.Collections.IEnumerable)method.Invoke(window, null)!)
        {
            var pair = item.GetType().GetProperty("Pair")!.GetValue(item)!;
            ids.Add((string)pair.GetType().GetProperty("Id")!.GetValue(pair)!);
        }
        return ids.Distinct(StringComparer.Ordinal).Count();
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    /// <summary>
    /// DIAG354-4/5: start the real production bridge exactly as the application starts it, so the running
    /// application publishes its real CLI receiver window and the diagnostics route is exercised against a
    /// genuinely running application instead of a simulated one. No provider, credential or install effect.
    /// </summary>
    private static void StartRunningApplicationBridge(App app, MainWindow window, PilotInstanceLock instance,
        ThemeManager theme)
    {
        void Set(string field, object value) => typeof(App).GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
        Set("_singleInstance", instance);
        Set("_themeManager", theme);
        Set("_mainWindow", window);
        typeof(App).GetMethod("StartCliBridge", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
        var receiver = SyncCliBridgeTransport.FindReceiver(includeCurrentProcess: true, out _);
        SyncCliBridgeTransport.GetWindowThreadProcessId(receiver, out var receiverPid);
        Require(receiver != IntPtr.Zero && receiverPid == (uint)Environment.ProcessId,
            "The running application discovers its own CLI receiver even with another installed build running");
    }

    private static void Select(MainWindow window, int index)
    {
        var tabs = Field<TabControl>(window, "_settingsTabs");
        tabs.SelectedIndex = index;
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    // The page for a tab is realised by the TabControl outside the TabItem itself, so the checks
    // read the tab's own Content element rather than the header container.
    private static DependencyObject ContentOf(MainWindow window, int index) =>
        (DependencyObject)((TabItem)Field<TabControl>(window, "_settingsTabs").Items[index]!).Content!;

    private static List<string> Texts(MainWindow window, int index) =>
        Visuals(ContentOf(window, index)).OfType<TextBlock>().Select(text => text.Text).ToList();

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Visuals(child)) yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Logical(child)) yield return descendant;
        }
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static object? Call(MainWindow window, string name, params object[] arguments) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        { Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("The settings structure fixture did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }

    // D2: one bounded, control-character-free snippet of the real CLI streams for the diagnostic line.
    private static string SyncCliDiagnosticSnippet(string value)
    {
        var text = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return "[" + text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "] " +
               (text.Length <= 600 ? text : text[..600] + "…");
    }
}
