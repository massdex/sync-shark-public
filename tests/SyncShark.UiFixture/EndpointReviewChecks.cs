using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// EPS4A/EPS4B presentation consumer checks.
//
// The nine ordered kind combinations are configuration inputs to one shared mapping, never nine
// presentation engines. Every label the review surfaces render is produced from the captured
// ordered endpoints, the stored group/action values stay the existing physical keys, and a pair
// whose endpoints cannot be described is refused instead of being attributed to whichever account
// happens to be selected.
internal static class EndpointReviewChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1100, Height = 740 };
        var localA = Path.Combine(root, "left-local");
        var localB = Path.Combine(root, "right-local");
        Directory.CreateDirectory(localA);
        Directory.CreateDirectory(localB);
        using var leaseA = PilotDirectoryLease.Open(localA);
        using var leaseB = PilotDirectoryLease.Open(localB);
        var passed = 0;
        try
        {
            window.Show();
            Drain();

            // One ordered combination at a time, all nine of them, through the shipped mapping.
            foreach (var leftKind in OrderedKinds())
                foreach (var rightKind in OrderedKinds())
                {
                    var pair = Pair($"combo-{leftKind}-{rightKind}", leftKind, rightKind, leaseA, leaseB);
                    var endpoints = EndpointReviewPresentation.For(pair);
                    var localPort = rightKind == SyncEndpointKind.Local && leftKind != SyncEndpointKind.Local;
                    var leftPort = localPort ? SyncAuthorityCloudSides.LogicalRight : SyncAuthorityCloudSides.LogicalLeft;
                    var rightPort = localPort ? SyncAuthorityCloudSides.LogicalLeft : SyncAuthorityCloudSides.LogicalRight;
                    Require(endpoints.Context.Left.CompatibilityPort == leftPort &&
                            endpoints.Context.Right.CompatibilityPort == rightPort,
                        $"{leftKind}→{rightKind}: the ordered sides keep the existing physical port mapping");
                    Require(endpoints.LeftTitle == "Left — " + SectionTitle(leftKind) &&
                            endpoints.RightTitle == "Right — " + SectionTitle(rightKind),
                        $"{leftKind}→{rightKind}: the headings name the logical left and right endpoints");
                    Require(endpoints.LocalPortTitle == (localPort ? endpoints.RightTitle : endpoints.LeftTitle) &&
                            endpoints.RemotePortTitle == (localPort ? endpoints.LeftTitle : endpoints.RightTitle),
                        $"{leftKind}→{rightKind}: the local and cloud port titles follow the physical layout, not the provider name");
                    Require(MainWindow.GroupLabel("Local only", endpoints) == "Only on " + endpoints.LocalPortTitle &&
                            MainWindow.GroupLabel("Dropbox only", endpoints) == "Only on " + endpoints.RemotePortTitle,
                        $"{leftKind}→{rightKind}: the displayed groups name the true endpoint holding the item");
                    Require(MainWindow.ActionLabel("Upload", endpoints) == "Copy to " + endpoints.RemotePortTitle &&
                            MainWindow.ActionLabel("Download", endpoints) == "Copy to " + endpoints.LocalPortTitle,
                        $"{leftKind}→{rightKind}: the destination choices name the true endpoint the copy reaches");
                    Require(endpoints.Keep(SyncPairSide.Left) == (leftPort == SyncAuthorityCloudSides.LogicalLeft
                                ? SyncAuthorityConflictResolutionKind.KeepLocal
                                : SyncAuthorityConflictResolutionKind.KeepDropbox) &&
                            endpoints.Keep(SyncPairSide.Right) != endpoints.Keep(SyncPairSide.Left),
                        $"{leftKind}→{rightKind}: each logical side maps centrally to its own physical resolution");
                    passed++;
                }
            Console.WriteLine($"PASS {passed} ordered endpoint combinations map through one shared presentation");

            // Reversed pair: the local endpoint occupies the right logical side.
            var reversed = Pair("reversed-cloud-left", SyncEndpointKind.Dropbox, SyncEndpointKind.Local,
                leaseA, leaseB);
            var reversedEndpoints = EndpointReviewPresentation.For(reversed);
            Require(reversedEndpoints.LocalPortTitle == reversedEndpoints.RightTitle &&
                    reversedEndpoints.RemotePortTitle == reversedEndpoints.LeftTitle &&
                    !reversedEndpoints.LeftUsesLocalPort,
                "A reversed pair presents the local folder as the right endpoint and the cloud folder as the left endpoint");
            Require(MainWindow.ActionLabel("Upload", reversedEndpoints) == "Copy to " + reversedEndpoints.LeftTitle,
                "In a reversed pair an Upload copies to the left cloud endpoint, which the layout stores on the remote port");

            // Two accounts of the same provider stay distinguishable by side and account identity.
            var sameProvider = Pair("same-provider",
                SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox, leaseA, leaseB,
                leftConnection: "connection-" + new string('a', 24), rightConnection: "connection-" + new string('b', 24));
            var sameEndpoints = EndpointReviewPresentation.For(sameProvider);
            Require(sameEndpoints.LeftTitle == "Left — Dropbox" && sameEndpoints.RightTitle == "Right — Dropbox" &&
                    FirstIdentity(sameEndpoints.Context.Left) != FirstIdentity(sameEndpoints.Context.Right) &&
                    sameEndpoints.Keep(SyncPairSide.Left) == SyncAuthorityConflictResolutionKind.KeepLocal &&
                    sameEndpoints.Keep(SyncPairSide.Right) == SyncAuthorityConflictResolutionKind.KeepDropbox,
                "Two accounts of one provider stay distinguishable by ordered side and each keeps its own physical resolution");
            Require(Location(sameEndpoints.Context.Left) == "/account-a-folder" &&
                    Location(sameEndpoints.Context.Right) == "/account-b-folder",
                "Each same-provider side keeps its own recorded folder identity and never borrows the other");

            // The review row carries the context: groups, destinations and detail values are logical.
            var logicalRow = new BootstrapRow("combo", "notes.txt", "Local only", "Upload", "Copy to Dropbox",
                "10 bytes", "20 bytes", "2026-09-01 10:00", "2026-09-02 11:00", "Pair", "file", "TXT",
                "Upload", SyncProviders.Dropbox, reversedEndpoints);
            Require(logicalRow.Group == "Local only" && logicalRow.AvailableActions.SequenceEqual(["Upload", "Skip for now"]) &&
                    logicalRow.ActionChoices.Select(choice => choice.Value).SequenceEqual(["Upload", "Skip for now"]),
                "The stored group key and the plan action values stay the existing physical keys");
            Require(logicalRow.ActionChoices[0].Display == "Copy to " + reversedEndpoints.RemotePortTitle &&
                    logicalRow.DisplayGroup == "Only on " + reversedEndpoints.LocalPortTitle,
                "A reversed review row shows its true endpoints while keeping the plan value Upload");
            Require(logicalRow.LeftSize == "20 bytes" && logicalRow.RightSize == "10 bytes" &&
                    logicalRow.LeftModified == "2026-09-02 11:00" && logicalRow.RightModified == "2026-09-01 10:00",
                "Left and right detail sizes and times present the logical sides, not the physical ports");
            logicalRow.SelectedAction = "Skip for now";
            Require(logicalRow.SelectedAction == "Skip for now" &&
                    logicalRow.Effect == "Leave unchanged and keep waiting",
                "Choosing through the displayed label stores the plan value and reports the unchanged effect");
            var moveEvidence = new SyncBootstrapMoveReview(SyncAuthorityOperationKind.MoveLocal,
                "old.txt", "object", "local-version", "remote-version");
            var moveRow = new BootstrapRow("combo", "new.txt", "Dropbox only", "MoveLocal", "Move",
                "", "10 bytes", "", "", "Pair", "file", "TXT", null, SyncProviders.GoogleDrive,
                reversedEndpoints, moveEvidence);
            Require(moveRow.AvailableActions.SequenceEqual(["MoveLocal", "Skip for now"]) &&
                    moveRow.ActionChoices[0].Display == "Move on " + reversedEndpoints.LocalPortTitle &&
                    moveRow.Effect.Contains("old.txt → new.txt"),
                "Reviewed moves show ordered endpoints and both paths without offering an unapproved copy");
            moveRow.SelectedAction = "Skip for now";
            moveRow.SelectedAction = "MoveLocal";
            Require(moveRow.Effect.Contains("old.txt → new.txt") && moveRow.Move == moveEvidence,
                "Re-selecting a move preserves its exact path and identity evidence");
            Require(MainWindow.GroupLabel("Unexpected group", reversedEndpoints) == "Unexpected group" &&
                    MainWindow.GroupLabel("Local only", null) == "Local only" &&
                    MainWindow.ActionLabel("Upload", null) == "Upload",
                "An unknown group or an absent context stays visible and neutral; no endpoint is invented");

            // Conflict presentation: logical titles, the two central choices and a missing one-side byte.
            // The normal orientation puts the local folder on the logical left, so a missing left byte
            // must stay missing instead of being filled with the right endpoint's bytes.
            var normal = Pair("normal-local-left", SyncEndpointKind.Local, SyncEndpointKind.Dropbox,
                leaseA, leaseB);
            var normalEndpoints = EndpointReviewPresentation.For(normal);
            var rightOnly = new SyncConflictContent("conflict-1", normal.Id, "docs/report.txt",
                null, 42, null, "right-hash", null, DateTimeOffset.Parse("2026-09-02T11:00:00Z"),
                null, "RIGHT SIDE CONTENT"u8.ToArray(), null);
            var rightOnlyWindow = new ConflictComparisonWindow(rightOnly, normal);
            Require(rightOnlyWindow.LocalText.Text.Length == 0 &&
                    !rightOnlyWindow.LocalText.Text.Contains("RIGHT SIDE CONTENT", StringComparison.Ordinal) &&
                    rightOnlyWindow.SideBySideTab.Visibility == Visibility.Collapsed,
                "A missing left byte case never coalesces the right endpoint bytes into the left pane");
            Require(rightOnlyWindow.MetadataText.Text.IndexOf(normalEndpoints.LeftTitle + ":", StringComparison.Ordinal) <
                    rightOnlyWindow.MetadataText.Text.IndexOf(normalEndpoints.RightTitle + ":", StringComparison.Ordinal) &&
                    rightOnlyWindow.MetadataText.Text.Contains("Location: " + Location(normalEndpoints.Context.Left)) &&
                    rightOnlyWindow.MetadataText.Text.Contains("Location: " + Location(normalEndpoints.Context.Right)),
                "Conflict metadata is ordered left then right with each endpoint's own recorded location");
            // The shared command decorator replaces a button's text with its icon composite and
            // title-cases the caption, so the accessible name is the wording the button actually
            // carries. Each choice must still name its own logical side and never the other one.
            var leftChoice = System.Windows.Automation.AutomationProperties.GetName(rightOnlyWindow.ChooseLeftButton);
            var rightChoice = System.Windows.Automation.AutomationProperties.GetName(rightOnlyWindow.ChooseRightButton);
            Require(leftChoice.Contains("Left", StringComparison.OrdinalIgnoreCase) &&
                    leftChoice.Contains("computer", StringComparison.OrdinalIgnoreCase) &&
                    !leftChoice.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) &&
                    rightChoice.Contains("Right", StringComparison.OrdinalIgnoreCase) &&
                    rightChoice.Contains("Dropbox", StringComparison.OrdinalIgnoreCase),
                "The conflict choices name the logical left and right endpoints and never the other side");
            rightOnlyWindow.Close();

            var bothSides = new SyncConflictContent("conflict-2", reversed.Id, "docs/report.txt",
                11, 22, "left-hash", "right-hash", DateTimeOffset.Parse("2026-09-01T10:00:00Z"),
                DateTimeOffset.Parse("2026-09-02T11:00:00Z"), "LOCAL BYTES"u8.ToArray(),
                "CLOUD BYTES"u8.ToArray(), null);
            var bothWindow = new ConflictComparisonWindow(bothSides, reversed);
            Require(bothWindow.LocalText.Text.Contains("CLOUD BYTES", StringComparison.Ordinal) &&
                    bothWindow.DropboxText.Text.Contains("LOCAL BYTES", StringComparison.Ordinal),
                "In a reversed pair the left pane shows the left cloud version and the right pane shows the local version");
            Require(bothWindow.MetadataText.Text.IndexOf(reversedEndpoints.LeftTitle + ":", StringComparison.Ordinal) <
                    bothWindow.MetadataText.Text.IndexOf(reversedEndpoints.RightTitle + ":", StringComparison.Ordinal),
                "The conflict metadata section keeps logical left before right");
            bothWindow.Close();

            // A local-to-local pair reviews with local endpoints only. This fixture registers no
            // connection at all, so a passing review proves no cloud login or primary account is used.
            var localPair = Pair("local-local", SyncEndpointKind.Local, SyncEndpointKind.Local, leaseA, leaseB);
            var localEndpoints = EndpointReviewPresentation.For(localPair);
            Require(localEndpoints.LeftTitle == "Left — This computer" &&
                    localEndpoints.RightTitle == "Right — This computer" &&
                    localEndpoints.Context.Left.ConnectionId is null &&
                    localEndpoints.Context.Right.ConnectionId is null &&
                    MainWindow.ActionLabel("Download", localEndpoints) == "Copy to " + localEndpoints.LocalPortTitle &&
                    MainWindow.GroupLabel("Dropbox only", localEndpoints) == "Only on " + localEndpoints.RemotePortTitle,
                "A local-to-local pair presents two local endpoints and never invents a cloud account");
            var localLoads = 0;
            MainWindow.BootstrapReviewPageLoaderForTest = (_, _, _, _, _, _, _) =>
            {
                localLoads++;
                return Task.FromResult(Page(localPair.Id));
            };
            window.LoadPairSetForTest(new SyncPairSet([localPair], localPair.Id));
            Call(window, "OpenFirstSyncReview", localPair.Id);
            Drain();
            Require(localLoads == 1 && window.BootstrapRows.Count == 1 &&
                    Control<Button>(window, "ApproveBootstrapButton").IsEnabled &&
                    Control<TextBlock>(window, "BootstrapEndpointSummary").Text.Contains("This computer") &&
                    Control<TextBlock>(window, "BootstrapSummary").Text.Contains("Only on Left — This computer"),
                "A local-to-local review loads and becomes actionable with no cloud login and shows both local endpoints");

            // R4: the first-sync endpoint summary states the ordered side ONCE through the shared
            // presentation, keeps the endpoint kind and its own path, and never repeats the prefix.
            var firstSyncEndpoints = Control<TextBlock>(window, "BootstrapEndpointSummary").Text;
            Require(firstSyncEndpoints.Contains("Left: This computer", StringComparison.Ordinal) &&
                    firstSyncEndpoints.Contains("Right: This computer", StringComparison.Ordinal) &&
                    firstSyncEndpoints.Contains(leaseA.Path, StringComparison.Ordinal) &&
                    !firstSyncEndpoints.Contains("Left: Left", StringComparison.Ordinal) &&
                    !firstSyncEndpoints.Contains("Right: Right", StringComparison.Ordinal) &&
                    !firstSyncEndpoints.Contains("Left \u2014 Left", StringComparison.Ordinal) &&
                    !firstSyncEndpoints.Contains("Right \u2014 Right", StringComparison.Ordinal),
                "R4: the first-sync endpoint summary states each ordered side once with its own path: " + firstSyncEndpoints);

            // A pair whose endpoints cannot be described is refused; no page is requested and no account
            // is consulted in place of the missing context.
            var undescribable = new SyncPair("undescribable", "Undescribable", localA,
                leaseA.Identity.VolumeSerialNumber, leaseA.Identity.FileId, "/a", "id:a",
                Provider: "not-a-registered-provider", InitialReviewApproved: false);
            var loads = 0;
            MainWindow.BootstrapReviewPageLoaderForTest = (_, _, _, _, _, _, _) =>
            {
                loads++;
                return Task.FromResult(new SyncBootstrapReviewPage([], 0, 0, 0, 0, 0, 0, false, true, "l", "c"));
            };
            window.LoadPairSetForTest(new SyncPairSet([undescribable], undescribable.Id));
            Call(window, "OpenFirstSyncReview", undescribable.Id);
            Drain();
            Require(loads == 0 && window.BootstrapRows.Count == 0 &&
                    !Control<Button>(window, "ApproveBootstrapButton").IsEnabled &&
                    Control<TextBlock>(window, "BootstrapSummary").Text.Contains("refused rather than guessed"),
                "A pair whose endpoints cannot be described is refused with a reason and never falls back to a selected account");

            // Bulk conflict actions are ordered positions, never providers. Each of the seven ordered
            // kinds names its own ACTUAL endpoints through the same captured context the comparison and
            // first-sync surfaces use, states that the OPPOSITE version is retained, and resolves the
            // logical side the control names.
            var leftButton = Control<Button>(window, "KeepLocalConflictButton");
            var rightButton = Control<Button>(window, "KeepDropboxConflictButton");
            var bulkKinds = new (string Label, SyncEndpointKind Left, SyncEndpointKind Right)[]
            {
                ("local to dropbox", SyncEndpointKind.Local, SyncEndpointKind.Dropbox),
                ("local to google", SyncEndpointKind.Local, SyncEndpointKind.GoogleDrive),
                ("google to local", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Local),
                ("local to local", SyncEndpointKind.Local, SyncEndpointKind.Local),
                ("dropbox to google", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive),
                ("google to dropbox", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox),
                ("same provider different accounts", SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox)
            };
            foreach (var kind in bulkKinds)
            {
                var bulkPair = Pair("bulk-" + kind.Label.Replace(' ', '-'), kind.Left, kind.Right, leaseA, leaseB);
                window.LoadPairSetForTest(new SyncPairSet([bulkPair], bulkPair.Id));
                window.ApplyConflictActionPresentation(bulkPair);
                var endpoints = EndpointReviewPresentation.For(bulkPair);
                var leftName = System.Windows.Automation.AutomationProperties.GetName(leftButton);
                var rightName = System.Windows.Automation.AutomationProperties.GetName(rightButton);
                var leftIdentity = endpoints.LeftTitle + " at " + Location(endpoints.Context.Left);
                var rightIdentity = endpoints.RightTitle + " at " + Location(endpoints.Context.Right);
                Require(ButtonLabel(leftButton) == "Use left" && ButtonLabel(rightButton) == "Use right",
                    kind.Label + ": the bulk conflict controls carry the exact ordered left/right wording");
                Require(leftName.Contains("left endpoint's version", StringComparison.Ordinal) &&
                        leftName.Contains("right endpoint's version,", StringComparison.Ordinal) &&
                        leftName.Contains("is retained in Recovery first.", StringComparison.Ordinal) &&
                        leftName.Contains(leftIdentity, StringComparison.Ordinal) &&
                        leftName.Contains(rightIdentity, StringComparison.Ordinal),
                    kind.Label + ": the left action names its own pair identity and states the opposite version is retained");
                Require(rightName.Contains("right endpoint's version", StringComparison.Ordinal) &&
                        rightName.Contains("left endpoint's version,", StringComparison.Ordinal) &&
                        rightName.Contains("is retained in Recovery first.", StringComparison.Ordinal) &&
                        rightName.Contains(leftIdentity, StringComparison.Ordinal) &&
                        rightName.Contains(rightIdentity, StringComparison.Ordinal),
                    kind.Label + ": the right action names its own pair identity and states the opposite version is retained");
                var production = window.BuildConflictActionPresentation(endpoints);
                Require(production.LeftResolution == SyncPairSide.Left && production.RightResolution == SyncPairSide.Right,
                    kind.Label + ": the left control resolves the logical left endpoint and the right control the logical right endpoint");
            }

            // A Google Drive left endpoint is named as Google Drive, never as local or Dropbox, and a
            // pair or selection change never leaves the previous side's presentation behind.
            var googleLeft = Pair("bulk-stale-google-left", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Local,
                leaseA, leaseB);
            var localDropbox = Pair("bulk-stale-local-dropbox", SyncEndpointKind.Local, SyncEndpointKind.Dropbox,
                leaseA, leaseB);
            window.LoadPairSetForTest(new SyncPairSet([googleLeft, localDropbox], googleLeft.Id));
            window.ApplyConflictActionPresentation(googleLeft);
            Require(System.Windows.Automation.AutomationProperties.GetName(leftButton)
                        .Contains("Left — " + SectionTitle(SyncEndpointKind.GoogleDrive), StringComparison.Ordinal),
                "A Google Drive left endpoint is named as Google Drive, never as local or Dropbox");
            window.ApplyConflictActionPresentation(localDropbox);
            var replaced = System.Windows.Automation.AutomationProperties.GetName(leftButton);
            Require(replaced.Contains("Left — This computer", StringComparison.Ordinal) &&
                    !replaced.Contains("Google Drive", StringComparison.Ordinal),
                "A pair change replaces the previous endpoint presentation instead of leaving the old side behind");
            var grid = Control<DataGrid>(window, "ConflictGrid");
            window.ConflictRows.Clear();
            window.ConflictRows.Add(ConflictRow(googleLeft.Id, "conflict-a.txt"));
            window.ConflictRows.Add(ConflictRow(localDropbox.Id, "conflict-b.txt"));
            Drain();
            grid.SelectedItem = window.ConflictRows[0];
            Drain();
            Require(System.Windows.Automation.AutomationProperties.GetName(leftButton)
                        .Contains("Google Drive", StringComparison.Ordinal),
                "Selecting the Google Drive left pair names that pair's actual endpoint on the real grid");
            grid.SelectedItem = window.ConflictRows[1];
            Drain();
            var selected = System.Windows.Automation.AutomationProperties.GetName(leftButton);
            Require(selected.Contains("Left — This computer", StringComparison.Ordinal) &&
                    !selected.Contains("Google Drive", StringComparison.Ordinal),
                "Selecting the local pair replaces the Google Drive presentation on the real grid selection");
            grid.UnselectAll();
            Drain();
            var neutral = System.Windows.Automation.AutomationProperties.GetName(leftButton);
            Require(neutral.Contains("left endpoint's version for the selected conflicts", StringComparison.Ordinal) &&
                    !neutral.Contains("Google Drive", StringComparison.Ordinal),
                "With no single pair selected the bulk actions keep the neutral ordered wording");
            window.ApplyConflictActionPresentation(googleLeft);
            var conflictMenu = Control<DataGrid>(window, "ConflictGrid").ContextMenu!;
            var menuTexts = conflictMenu.Items.OfType<MenuItem>()
                .Select(item => System.Windows.Automation.AutomationProperties.GetName(item) + "|" + HeaderText(item))
                .ToArray();
            Require(menuTexts.Any(text => text.Contains("left endpoint's version", StringComparison.Ordinal) &&
                                          text.Contains("Google Drive", StringComparison.Ordinal)) &&
                    menuTexts.Any(text => text.Contains("right endpoint's version", StringComparison.Ordinal)) &&
                    !menuTexts.Any(text => text.Contains("Use local", StringComparison.Ordinal) ||
                                           text.Contains("Use Dropbox", StringComparison.Ordinal)),
                "The conflict context menu carries ordered-side captions and the actual pair identity, not the obsolete local/Dropbox wording");

            Console.WriteLine("PASS endpoint review presentation checks; no provider effect, credential or live account used");
            return 0;
        }
        finally
        {
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    private static IEnumerable<SyncEndpointKind> OrderedKinds() =>
        [SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive];

    private static string SectionTitle(SyncEndpointKind kind) => kind switch
    {
        SyncEndpointKind.Local => "This computer",
        SyncEndpointKind.GoogleDrive => SyncProviders.DisplayName(SyncProviders.GoogleDrive),
        _ => SyncProviders.DisplayName(SyncProviders.Dropbox)
    };

    private static string FirstIdentity(SyncEndpointContextSide side) =>
        (side.ConnectionId ?? string.Empty) + "|" + (side.FolderId ?? side.LocalPath ?? string.Empty);

    private static string Location(SyncEndpointContextSide side) =>
        (side.IsLocal ? side.LocalPath : side.FolderPath ?? side.FolderId) ?? "Not recorded";

    private static string ButtonLabel(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(child => child.Text)),
        _ => string.Empty
    };

    private static string HeaderText(MenuItem item) => item.Header switch
    {
        string text => text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(child => child.Text)),
        _ => string.Empty
    };

    private static DecisionRow ConflictRow(string pairId, string path) =>
        new(pairId, pairId, path, SyncDecisionKind.Conflict.ToString(), "Both versions are retained.",
            ConflictId: "conflict-" + path, ConflictGeneration: 1);

    // One explicit symmetric endpoint document per ordered combination. A local side carries the real
    // folder identity of a real directory; a cloud side carries the registered provider literal, its own
    // account and its own folder identity.
    private static SyncPair Pair(string id, SyncEndpointKind leftKind, SyncEndpointKind rightKind,
        PilotDirectoryLease leftLocal, PilotDirectoryLease rightLocal, string leftConnection = "connection-a",
        string rightConnection = "connection-b") =>
        new SyncPair(id, id, leftLocal.Path, leftLocal.Identity.VolumeSerialNumber, leftLocal.Identity.FileId,
            "/legacy-root", "id:legacy-folder", InitialReviewApproved: false) with
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = Record(SyncPairSide.Left, leftKind, leftLocal, rightLocal, leftConnection,
                AccountFolder(1)),
            RightEndpoint = Record(SyncPairSide.Right, rightKind, leftLocal, rightLocal, rightConnection,
                AccountFolder(2))
        };

    private static string AccountFolder(int index) => index == 1 ? "/account-a-folder" : "/account-b-folder";

    private static SyncEndpointRecord Record(SyncPairSide side, SyncEndpointKind kind,
        PilotDirectoryLease leftLocal, PilotDirectoryLease rightLocal, string connectionId, string folderPath)
    {
        if (kind == SyncEndpointKind.Local)
        {
            var lease = side == SyncPairSide.Left ? leftLocal : rightLocal;
            return new SyncEndpointRecord(kind, LocalPath: lease.Path,
                LocalVolumeSerialNumber: lease.Identity.VolumeSerialNumber, LocalRootFileId: lease.Identity.FileId);
        }
        var provider = kind == SyncEndpointKind.GoogleDrive ? SyncProviders.GoogleDrive : SyncProviders.Dropbox;
        return new SyncEndpointRecord(kind, Provider: provider, ConnectionId: connectionId,
            NamespaceId: "namespace-" + provider, FolderPath: folderPath);
    }

    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static SyncBootstrapReviewPage Page(string pairId) => new(
        [new SyncBootstrapReviewItem(pairId, pairId + ".txt", "Local only", "File", "TXT", "Upload",
            "Copy to cloud", 1, null, DateTimeOffset.UtcNow, null)],
        0, 1, 1, 1, 0, 0, false, true, "local-1", "cloud-1");

    private static void Call(MainWindow window, string name, params object[] arguments) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidDataException(description); Console.WriteLine("PASS " + description); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
