using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// UI29 remainder real-consumer checks on the actual MainWindow pair editor.
//
// The quiet Create Folder button, its dialog, the shared LocalFolderActions call behind it, the targeted
// parent refresh and the stale-completion guard all run through the production members the running
// application uses: the real MainWindow, the real pair draft (PrepareExistingPairEditorForTest, which
// runs the real StartPairDraft), the real click handlers, the real LocalRootTree and LocalPathEntryBox,
// the real SyncCli dispatcher and the real running-application runtime (CliAppRuntime) the window's CLI
// bridge uses.
//
// Real temporary directories supply the input: the filesystem action under test is never replaced by a
// fake. The single fake is the cloud folder browser, which exists only to prove that no cloud folder,
// account, pair or provider state is touched. No installed runtime, provider, credential or user file is
// read or changed, and the fixture window is shown off-screen without activation.
internal static class LocalFolderCreationUiChecks
{
    private const string Selector = "--local-folder-creation-ui-checks";

    internal static int Run(string root)
    {
        var outputRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(outputRoot);
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-local-create-ui", Guid.NewGuid().ToString("N"));
        var localRoot = Path.Combine(stateRoot, "LocalName");
        var plans = Path.Combine(localRoot, "Plans");
        var refusals = Path.Combine(localRoot, "Refusals");
        // A real existing folder outside the one root this tree shows.
        var outside = Path.Combine(stateRoot, "Outside The Shown Tree");
        Directory.CreateDirectory(plans);
        Directory.CreateDirectory(Path.Combine(localRoot, "Existing"));
        Directory.CreateDirectory(refusals);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(plans, "Already On Disk"));
        File.WriteAllText(Path.Combine(plans, "Blocked.txt"), "existing file content");

        if (MainWindow.LocalRootNodeFactoryForTest is not null || MainWindow.PairFolderBrowserFactoryForTest is not null ||
            MainWindow.PairHealthLoaderForTest is not null || MainWindow.SuppressLoadedStartupForTest)
            throw new InvalidDataException("A production test override was active before this fixture installed it.");

        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        using var localLease = PilotDirectoryLease.Open(localRoot);
        var pair = new SyncPair("local-create-pair", "Project files", localRoot, localLease.Identity.VolumeSerialNumber,
            localLease.Identity.FileId, "/RemoteName", "id:remote-name", ConnectionId: "local-create-fixture-account",
            Provider: SyncProviders.Dropbox, Enabled: true, SelectionRules: [], SelectionDefaultIncluded: false,
            InitialReviewApproved: true,
            RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                "id:recovery", "root"));
        Pump(new SyncPairStore(appLock.StateAuthority).SaveAsync(new SyncPairSet([pair], pair.Id)));
        using (var http = new HttpClient(new LocalCreateNoNetworkHandler()))
        using (var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http))
            host.Authority.InitializeAsync("local-create-fixture", "fixture-profile", "fixture-source", "{}")
                .GetAwaiter().GetResult();

        var browser = new LocalCreateFolderBrowser();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.PairFolderBrowserFactoryForTest = _ => Task.FromResult<IDropboxFolderBrowserService>(browser);
        MainWindow.PairHealthLoaderForTest = (id, _) => Task.FromResult(Health(id));
        MainWindow.LocalRootNodeFactoryForTest = _ => [LocalFolderNode.CreateForTest(localRoot,
            ["Plans", "Existing", "Refusals"])];
        var window = new MainWindow(appLock.StateAuthority, theme)
        {
            Width = 1240, Height = 790, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            Drain();
            Call(window, "NavigateTo", "Pairs", true);
            Drain();
            window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
            window.PrepareExistingPairEditorForTest(pair);
            PumpUntil(() => ((FrameworkElement)Required(window, "PairEditorPanel")).IsVisible,
                "the saved pair editor did not open");
            PumpTask(window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 0;
            PumpUntil(() => FindLocal(window, plans) is not null, "the local folder tree did not load");

            CheckButtonPlacementAndAvailability(window);
            CheckCreateThroughTheEditor(window, pair, browser, plans);
            CheckDialogRefusals(window, refusals, outside);
            CheckDialogScopeAndKeys(window, plans);
            CheckStaleCompletion(window, plans);
            CheckClosedDraftRefusal(window, localRoot);
            CheckRunningApplicationCliDispatch(window, appLock, localRoot);

            Console.WriteLine("PASS " + Selector +
                "; real pair-editor consumer checks over isolated real folders only, no provider, credential," +
                " signing, installation or user-file effect");
            return 0;
        }
        finally
        {
            MainWindow.LocalRootNodeFactoryForTest = null;
            MainWindow.PairFolderBrowserFactoryForTest = null;
            MainWindow.PairHealthLoaderForTest = null;
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    // ---- checks ---------------------------------------------------------------------------------

    private static void CheckButtonPlacementAndAvailability(MainWindow window)
    {
        var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
        var goButton = (Button)Required(window, "GoToLocalPathButton");
        var createButton = (Button)Required(window, "CreateLocalFolderButton");
        var localPanel = (Border)Required(window, "LocalRootPanel");
        Require(localPanel.IsAncestorOf(createButton) && localPanel.IsAncestorOf(goButton) &&
                localPanel.IsAncestorOf(pathBox),
            "the Create Folder button sits inside the local folder panel beside the existing path and Go controls");
        Require(ReferenceEquals(createButton.Parent, goButton.Parent) &&
                Grid.GetRow(createButton) == Grid.GetRow(goButton) &&
                Grid.GetColumn(createButton) == Grid.GetColumn(goButton) + 1,
            "the Create Folder button is the next control in the same local path row");
        Require(createButton.IsVisible && createButton.IsEnabled && createButton.Focusable && createButton.IsTabStop &&
                CommandLabel(createButton) == "Create Folder" &&
                AutomationProperties.GetName(createButton).Length > 0 &&
                !string.IsNullOrWhiteSpace(createButton.ToolTip?.ToString()),
            "the one Create Folder button is visible, enabled, keyboard reachable, labelled and explained [" +
            "visible=" + createButton.IsVisible + ", enabled=" + createButton.IsEnabled + ", focusable=" +
            createButton.Focusable + ", tabStop=" + createButton.IsTabStop + ", label='" + CommandLabel(createButton) +
            "', name='" + AutomationProperties.GetName(createButton) + "', pathEntryEnabled=" + pathBox.IsEnabled +
            ", panelVisible=" + localPanel.IsVisible + "]");
        Require(((FrameworkElement)Required(window, "CreateLocalFolderDialogHost")).Visibility == Visibility.Collapsed,
            "the Create Folder dialog stays closed until the button is used");
        // The button follows the editable local roots instead of carrying a second, independent state.
        pathBox.IsEnabled = false;
        Drain();
        Require(!createButton.IsEnabled,
            "the Create Folder button is unavailable while the local path entry is not editable");
        pathBox.IsEnabled = true;
        Drain();
        Require(createButton.IsEnabled, "the Create Folder button returns with the editable local roots");
        Require(PairLocationDraftActive(window), "the pair draft is open for the consumer checks");
    }

    private static void CheckCreateThroughTheEditor(MainWindow window, SyncPair pair, LocalCreateFolderBrowser browser,
        string plans)
    {
        var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
        var createButton = (Button)Required(window, "CreateLocalFolderButton");
        var dialogHost = (FrameworkElement)Required(window, "CreateLocalFolderDialogHost");
        var parentText = (TextBlock)Required(window, "CreateLocalFolderParentText");
        var nameBox = (TextBox)Required(window, "CreateLocalFolderNameBox");
        var errorText = (TextBlock)Required(window, "CreateLocalFolderErrorText");
        var confirm = (Button)Required(window, "CreateLocalFolderConfirmButton");
        var cancel = (Button)Required(window, "CreateLocalFolderCancelButton");

        var generation = Field<int>(window, "_pairEditorGeneration");
        var draftAccount = Field<string?>(window, "_pairDraftConnectionId");
        var planBefore = window.LocalGlobalPlanForTest;
        var rulesBefore = window.PendingSelectionRulesForTest;
        var defaultBefore = window.PendingSelectionDefaultIncludedForTest;
        var pairSetBefore = JsonSerializer.Serialize(Field<SyncPairSet>(window, "_pairSet"));
        var selectedBefore = ((DataGrid)Required(window, "PairList")).SelectedItem;
        var plansNodeBefore = FindLocal(window, plans);
        var localRootChildrenBefore = window.LocalRootNodes[0].Children.ToArray();
        var runActiveBefore = Field<bool>(window, "_uiRunActive");

        // The typed path is the exact parent the dialog must name, exactly as the folder picker reads it.
        pathBox.Text = plans;
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(dialogHost.Visibility == Visibility.Visible && parentText.Text == plans && nameBox.Text.Length == 0 &&
                errorText.Visibility == Visibility.Collapsed && confirm.IsEnabled && cancel.IsEnabled,
            "the dialog opens for the exact typed parent with one empty name field, Create and Cancel");
        Require(nameBox.IsKeyboardFocused, "the dialog puts keyboard focus in the one folder-name field");

        var created = Path.Combine(plans, "Evidence Folder");
        var entriesBefore = Entries(plans);
        nameBox.Text = "Evidence Folder";
        confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(!confirm.IsEnabled && !cancel.IsEnabled,
            "the first confirmed request disables Create and Cancel so a duplicate submit cannot repeat it");
        // A second raise reaches the same handler while the first request owns the effect.
        confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => Directory.Exists(created), "the shared action did not create the folder through the editor");
        PumpUntil(() => dialogHost.Visibility == Visibility.Collapsed, "the dialog did not close after the create");
        PumpUntil(() => FindLocal(window, created) is not null,
            "the created folder was not revealed in the local tree");
        PumpFor(TimeSpan.FromMilliseconds(150));

        Require(Entries(plans).SequenceEqual(entriesBefore.Append(created).OrderBy(item => item,
                StringComparer.OrdinalIgnoreCase)),
            "the editor created exactly the one named folder and changed nothing else in the parent");
        var plansNode = FindLocal(window, plans);
        Require(ReferenceEquals(plansNode, plansNodeBefore) && plansNode!.IsExpanded,
            "the affected parent row keeps its identity and is the only row that was re-read");
        Require(plansNode!.Children.Select(child => child.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(Directory.EnumerateDirectories(plans)
                    .Select(folder => Path.GetFileName(folder)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
            "the affected parent row now lists exactly its real children, so the new folder is really shown");
        var revealed = FindLocal(window, created)!;
        Require(revealed.IsChecked != true && revealed.CanSelect,
            "the revealed folder is not ticked for sync and stays an ordinary selectable row");
        Require(window.LocalRootNodes[0].Children.SequenceEqual(localRootChildrenBefore),
            "no other branch of the tree was rebuilt or collapsed by the refresh");
        Require(browser.CreateFolderCalls == 0,
            "the local create never asked the provider to create a cloud folder");
        Require(Field<int>(window, "_pairEditorGeneration") == generation &&
                Field<string?>(window, "_pairDraftConnectionId") == draftAccount &&
                Field<bool>(window, "_uiRunActive") == runActiveBefore &&
                ReferenceEquals(((DataGrid)Required(window, "PairList")).SelectedItem, selectedBefore),
            "the editor generation, the draft account, the selected pair row and the run state were unchanged");
        Require(JsonSerializer.Serialize(Field<SyncPairSet>(window, "_pairSet")) == pairSetBefore &&
                window.PendingSelectionRulesForTest.SequenceEqual(rulesBefore) &&
                window.PendingSelectionDefaultIncludedForTest == defaultBefore &&
                SamePlan(window.LocalGlobalPlanForTest, planBefore),
            "no pair was bound or saved and no folder selection, tick or default changed");
        Require(StatusLog(window).Contains("Created " + created, StringComparison.Ordinal) &&
                StatusLog(window).Contains("Nothing was ticked for sync", StringComparison.Ordinal),
            "the editor reports the exact created path and that nothing was ticked for sync");
        Require(pair.Id.Length > 0 && Directory.Exists(plans) && revealed.Name == "Evidence Folder",
            "the fixture pair is untouched and the created row carries the exact requested name");
    }

    private static void CheckDialogRefusals(MainWindow window, string refusals, string outside)
    {
        var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
        var createButton = (Button)Required(window, "CreateLocalFolderButton");
        var dialogHost = (FrameworkElement)Required(window, "CreateLocalFolderDialogHost");
        var parentText = (TextBlock)Required(window, "CreateLocalFolderParentText");
        var nameBox = (TextBox)Required(window, "CreateLocalFolderNameBox");
        var errorText = (TextBlock)Required(window, "CreateLocalFolderErrorText");
        var confirm = (Button)Required(window, "CreateLocalFolderConfirmButton");
        var cancel = (Button)Required(window, "CreateLocalFolderCancelButton");
        var entriesBefore = Entries(refusals);

        // A parent the tree does not show is refused before any dialog or effect exists.
        pathBox.Text = outside;
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(dialogHost.Visibility == Visibility.Collapsed &&
                StatusLog(window).Contains("is not one of the folders shown here", StringComparison.Ordinal) &&
                Entries(outside).Length == 0,
            "a parent outside the folders shown in this tree opens no dialog and creates nothing");
        pathBox.Text = Path.Combine(refusals, "Not There");
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(dialogHost.Visibility == Visibility.Collapsed &&
                StatusLog(window).Contains("is not an existing folder", StringComparison.Ordinal),
            "a parent that does not exist opens no dialog and creates nothing");
        foreach (var refusedEntry in new[] { "relative" + Path.DirectorySeparatorChar + "Refusals", @"\\?\C:\Refusals",
                     @"\\server\share\Refusals", "C:" })
        {
            pathBox.Text = refusedEntry;
            createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain();
            Require(dialogHost.Visibility == Visibility.Collapsed,
                "the refused parent '" + refusedEntry + "' opens no dialog");
        }
        Require(Entries(refusals).SequenceEqual(entriesBefore),
            "no refused parent created anything in the real folder");

        // The dialog itself reports every unusable name without creating anything.
        pathBox.Text = refusals;
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(dialogHost.Visibility == Visibility.Visible && parentText.Text == refusals,
            "the dialog opens for the selected existing parent");
        foreach (var refusedName in new[] { "", "Sub/Name", "Bad:Name", "CON", "Name.", "Name ", "..",
                     new string('x', 256) })
        {
            // Every refused name is observed on a freshly opened dialog, so the error that is read below
            // belongs to that exact attempt instead of a previous one.
            if (errorText.Visibility == Visibility.Visible)
            {
                cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Drain();
                createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Drain();
            }
            Require(dialogHost.Visibility == Visibility.Visible && errorText.Visibility == Visibility.Collapsed,
                "the dialog is open and clean before the refused name '" + refusedName + "'");
            nameBox.Text = refusedName;
            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain();
            Require(dialogHost.Visibility == Visibility.Visible && errorText.Visibility == Visibility.Visible &&
                    errorText.Text.Length > 0 && confirm.IsEnabled,
                "the dialog keeps its inline error and stays usable for the refused name '" + refusedName + "'");
            Require(LocalFolderActions.NameRule == errorText.Text,
                "the inline error is the shared action's own name rule for '" + refusedName + "'");
        }
        Require(Entries(refusals).SequenceEqual(entriesBefore),
            "no refused name created anything in the real folder");

        // An occupied exact name is refused by the shared action and reported inline.
        Directory.CreateDirectory(Path.Combine(refusals, "Taken"));
        File.WriteAllText(Path.Combine(refusals, "Taken.txt"), "existing file content");
        foreach (var occupied in new[] { "Taken", "Taken.txt" })
        {
            nameBox.Text = occupied;
            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            // The buttons are re-enabled only after the shared action refused this exact attempt.
            PumpUntil(() => confirm.IsEnabled && errorText.Visibility == Visibility.Visible &&
                            errorText.Text.Contains("already exists", StringComparison.Ordinal),
                "the occupied name " + occupied + " was not reported");
            Require(dialogHost.Visibility == Visibility.Visible &&
                    errorText.Text.Contains("already exists", StringComparison.Ordinal) && confirm.IsEnabled,
                "an existing folder or file named '" + occupied + "' is refused inline without overwriting it");
        }
        Require(Directory.EnumerateFileSystemEntries(refusals).OrderBy(item => item,
                    StringComparer.OrdinalIgnoreCase).SequenceEqual(entriesBefore.Append(Path.Combine(refusals, "Taken"))
                    .Append(Path.Combine(refusals, "Taken.txt")).OrderBy(item => item, StringComparer.OrdinalIgnoreCase)) &&
                File.ReadAllText(Path.Combine(refusals, "Taken.txt")) == "existing file content",
            "the refused collisions left the existing folder and file exactly as they were");

        // Cancel before confirmation changes nothing at all.
        nameBox.Text = "Cancelled Folder";
        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(dialogHost.Visibility == Visibility.Collapsed &&
                !Directory.Exists(Path.Combine(refusals, "Cancelled Folder")) &&
                StatusLog(window).Contains("Create folder cancelled", StringComparison.Ordinal),
            "cancelling before confirmation closes the dialog, explains it and creates nothing");
    }

    private static void CheckDialogScopeAndKeys(MainWindow window, string plans)
    {
        var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
        var createButton = (Button)Required(window, "CreateLocalFolderButton");
        var host = (FrameworkElement)Required(window, "CreateLocalFolderDialogHost");
        var nameBox = (TextBox)Required(window, "CreateLocalFolderNameBox");
        var confirm = (Button)Required(window, "CreateLocalFolderConfirmButton");
        var generation = Field<int>(window, "_pairEditorGeneration");
        pathBox.Text = plans;
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        nameBox.Text = "Wrong Draft";
        Set(window, "_pairEditorGeneration", generation + 1);
        confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        Require(host.Visibility == Visibility.Collapsed && !Directory.Exists(Path.Combine(plans, "Wrong Draft")),
            "Changing the draft before confirmation refuses creation rather than adopting the new draft");
        Set(window, "_pairEditorGeneration", generation);
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
        Require(KeyboardNavigation.GetTabNavigation(host) == KeyboardNavigationMode.Cycle &&
            KeyboardNavigation.GetControlTabNavigation(host) == KeyboardNavigationMode.Cycle,
            "Create Folder contains keyboard traversal within its controls");
        nameBox.Text = "Escape Cancelled";
        nameBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(nameBox), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Drain();
        Require(host.Visibility == Visibility.Collapsed && !Directory.Exists(Path.Combine(plans, "Escape Cancelled")) && window.IsVisible,
            "Escape closes only Create Folder and leaves the main window running without creating anything");
    }

    private static void CheckStaleCompletion(MainWindow window, string plans)
    {
        var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
        var createButton = (Button)Required(window, "CreateLocalFolderButton");
        var dialogHost = (FrameworkElement)Required(window, "CreateLocalFolderDialogHost");
        var nameBox = (TextBox)Required(window, "CreateLocalFolderNameBox");
        var confirm = (Button)Required(window, "CreateLocalFolderConfirmButton");
        var stale = Path.Combine(plans, "Stale Folder");
        var generation = Field<int>(window, "_pairEditorGeneration");
        var pairSetBefore = JsonSerializer.Serialize(Field<SyncPairSet>(window, "_pairSet"));
        var selectedBefore = ((DataGrid)Required(window, "PairList")).SelectedItem;

        pathBox.Text = plans;
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        nameBox.Text = "Stale Folder";
        confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The editor generation advances exactly as a real pair or draft change does, without cancelling
        // the in-flight effect, so the stale guard is observed deterministically.
        Set(window, "_pairEditorGeneration", generation + 1);
        PumpUntil(() => Directory.Exists(stale), "the in-flight create did not finish");
        PumpUntil(() => dialogHost.Visibility == Visibility.Collapsed, "the stale completion kept the dialog open");
        PumpFor(TimeSpan.FromMilliseconds(150));
        Require(FindLocal(window, stale) is null,
            "a completion for a changed editor is not applied to the local tree");
        Require(JsonSerializer.Serialize(Field<SyncPairSet>(window, "_pairSet")) == pairSetBefore &&
                ReferenceEquals(((DataGrid)Required(window, "PairList")).SelectedItem, selectedBefore),
            "the stale completion changed no pair definition and no selected pair row");
        Set(window, "_pairEditorGeneration", generation);
        Drain();
        Require(Directory.Exists(stale) && StatusLog(window).Contains("The pair editor changed", StringComparison.Ordinal),
            "the created folder is kept and the changed editor is told the completion no longer belongs to it");
    }

    private static void CheckClosedDraftRefusal(MainWindow window, string localRoot)
    {
        Call(window, "EndPairDraft");
        Drain();
        Require(!PairLocationDraftActive(window), "the pair draft is closed for the refusal check");
        var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
        var createButton = (Button)Required(window, "CreateLocalFolderButton");
        var dialogHost = (FrameworkElement)Required(window, "CreateLocalFolderDialogHost");
        var before = Entries(localRoot);
        pathBox.Text = localRoot;
        createButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(dialogHost.Visibility == Visibility.Collapsed &&
                StatusLog(window).Contains("Open a folder pair before creating a folder", StringComparison.Ordinal),
            "Create Folder refuses to act without an open pair draft and explains why");
        Require(Entries(localRoot).SequenceEqual(before),
            "the refusal without a draft created nothing");
    }

    private static void CheckRunningApplicationCliDispatch(MainWindow window, PilotInstanceLock appLock, string localRoot)
    {
        // Exactly the runtime the window's own CLI bridge builds, so the running-application path is real.
        var runtime = new CliAppRuntime(window.AuthorityHost, appLock.StateAuthority, () => window.TrayRunActive,
            () => window.TrayPaused, () => { }, () => Task.CompletedTask,
            appAction: (invocation, cancellationToken) =>
                window.ExecuteCliAppActionAsync(invocation, cancellationToken));
        Require(runtime.Mode == "running-application",
            "the CLI check runs the running-application operations surface");
        var parent = Path.Combine(localRoot, "Plans");
        var created = Path.Combine(parent, "From The Running App");
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Pump(SyncCli.ExecuteAsync(["--cli", "folders", "create-local", "--local", parent,
            "--name", "From The Running App", "--yes", "--json"], output, error, () => runtime));
        Require(exit == 0 && Directory.Exists(created) &&
                output.ToString().Contains("\"path\":" + JsonSerializer.Serialize(created), StringComparison.Ordinal),
            "the running-application path creates the exact folder through the same shared action and returns it");
        var refusedOutput = new StringWriter();
        var refusedError = new StringWriter();
        var refused = Pump(SyncCli.ExecuteAsync(["--cli", "folders", "create-local", "--local", parent,
            "--name", "No Confirmation", "--json"], refusedOutput, refusedError, () => runtime));
        Require(refused == 2 && !Directory.Exists(Path.Combine(parent, "No Confirmation")) &&
                refusedError.ToString().Contains("requires --yes", StringComparison.Ordinal),
            "the running-application path refuses a missing confirmation with 2 and creates nothing");
        var refusedName = new StringWriter();
        var refusedNameError = new StringWriter();
        var badName = Pump(SyncCli.ExecuteAsync(["--cli", "folders", "create-local", "--local", parent,
            "--name", "CON", "--yes", "--json"], refusedName, refusedNameError, () => runtime));
        Require(badName == 2 && refusedNameError.ToString().Contains(LocalFolderActions.NameRule,
                    StringComparison.Ordinal),
            "the running-application path reports the same shared name rule as the direct path");
    }

    // ---- fixture support ------------------------------------------------------------------------

    private sealed class LocalCreateNoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture must not make a provider request: " +
                                                request.RequestUri);
    }

    // The cloud side is not the consumer under test; it only proves that no cloud folder is created.
    private sealed class LocalCreateFolderBrowser : IDropboxFolderBrowserService
    {
        private static readonly DropboxFolderPage RootPage = new(
            [new DropboxFolderEntry("RemoteName", "/RemoteName", "id:remote-name")], null);

        internal int CreateFolderCalls { get; private set; }

        public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(folderId is null or "root" ? RootPage : new DropboxFolderPage([], null));

        public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage([], null));

        public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage([], null));

        public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage([], null));

        public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
            CancellationToken cancellationToken = default)
        {
            CreateFolderCalls++;
            throw new InvalidOperationException(
                "Cloud folder creation is outside this packet and must never be reached here.");
        }
    }

    private static SyncFolderSelectionTree Health(string pairId) =>
        new(pairId, [], 0, 0, 0, 0, new SyncFolderRollup(0, 0, 0, 0, 0, 0), LocalRootFiles: 0,
            DropboxRootFiles: 0);

    private static LocalFolderNode? FindLocal(MainWindow window, string path) =>
        Descendants(window.LocalRootNodes).FirstOrDefault(node => !node.IsPlaceholder &&
            string.Equals(Path.GetFullPath(node.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<LocalFolderNode> Descendants(IEnumerable<LocalFolderNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Descendants(node.Children)) yield return child;
        }
    }

    private static string[] Entries(string folder) => Directory.EnumerateFileSystemEntries(folder)
        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool SamePlan(FolderTreeSelectionPlan? first, FolderTreeSelectionPlan? second) =>
        JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    private static bool PairLocationDraftActive(MainWindow window) =>
        (bool)(window.GetType().GetProperty("PairLocationDraftActive",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window) ?? throw new InvalidOperationException("PairLocationDraftActive is unavailable."));

    private static string StatusLog(MainWindow window) => ((TextBox)Required(window, "ConsoleText")).Text;

    // The window's shared command decorator replaces a plain label with an icon and text panel.
    private static string CommandLabel(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(item => item.Text)),
        _ => button.Content?.ToString() ?? string.Empty
    };

    private static DependencyObject Required(DependencyObject window, string name) =>
        ((FrameworkElement)window).FindName(name) as DependencyObject ??
        throw new InvalidDataException("The window has no element named " + name + ".");

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(1);
            if (Environment.TickCount64 - started > 60000)
                throw new InvalidOperationException("Timed out waiting for a local Create Folder task");
        }
        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpTask(Task task)
    {
        try { Pump(task); }
        catch (Exception error) when (error is OperationCanceledException or TaskCanceledException) { }
    }

    private static void PumpUntil(Func<bool> condition, string label)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            Drain();
            Thread.Sleep(10);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for " + label);
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < duration.TotalMilliseconds)
        {
            Drain();
            Thread.Sleep(5);
        }
    }
}
