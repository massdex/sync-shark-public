using System.IO;
using System.Reflection;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static int RunEndpointPairUiChecks(string root)
    {
        Directory.CreateDirectory(root);
        var left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
        var right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
        File.WriteAllText(Path.Combine(left, "payload.txt"), "UI fixture payload");
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        var priorClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        App? app = null;
        MainWindow? window = null;
        try
        {
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
            theme.Initialize();
            using var http = new HttpClient(new EndpointUiNoNetwork());
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
                new SyncAuthorityDatabase(Path.Combine(root, "state", "authority.db")));
            Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
            MainWindow.SuppressLoadedStartupForTest = true;
            window = new MainWindow(instance.StateAuthority, theme)
            { Width = 1240, Height = 790, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            BindAuthorityHost(window, host);
            window.Show();
            EndpointUiCall(window, "BeginNewPairEditor");
            EndpointUiRequire(!EndpointUiField<TextBox>(window, "LeftEndpointPathBox").IsHitTestVisible &&
                !EndpointUiField<TextBox>(window, "RightEndpointPathBox").IsHitTestVisible,
                "Endpoint UI: empty folder fields cannot launch Explorer");
            EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.Local;
            var session = EndpointUiField<SyncPairEditorSession>(window, "_endpointSession");
            EndpointUiRequire(session.KindOf(SyncPairSide.Left) == SyncEndpointKind.Local &&
                session.KindOf(SyncPairSide.Right) == SyncEndpointKind.Local,
                "Endpoint UI: actual provider selector independently switches the right endpoint");
            foreach (var selection in new[] { (SyncPairSide.Left, left), (SyncPairSide.Right, right) })
            {
                var picker = session.PickerFor(selection.Item1);
                EndpointUiRequire(picker.ApplyLocalFolder(picker.BeginPick(), selection.Item2).Applied,
                    "Endpoint UI: production picker binds the exact " + selection.Item1 + " local identity");
            }
            EndpointUiField<TextBox>(window, "PairNameBox").Text = "Local UI pair";
            EndpointUiField<ComboBox>(window, "PairDirectionBox").SelectedValue = SyncMode.UploadOnly;
            EndpointUiCall(window, "UpdatePairSaveAvailability");
            EndpointUiRequire(session.Direction == SyncMode.UploadOnly,
                "Endpoint UI: actual direction control updates the same draft used by Save");
            EndpointUiRequire(EndpointUiField<Button>(window, "SavePairButton").IsEnabled &&
                EndpointUiField<ComboBox>(window, "RightEndpointAccountBox").Visibility == Visibility.Collapsed,
                "Endpoint UI: a two-local-folder draft needs no cloud account and enables Save");
            // Exercise the real routed event and host persistence. The native folder dialog itself is
            // outside this fixture: its selected paths above enter the exact production picker.
            // Dispatch like a real input event so asynchronous handlers inherit WPF's context.
            _ = window.Dispatcher.InvokeAsync(() => EndpointUiField<Button>(window, "SavePairButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            PumpUntil(window, () => window.PairRows.Any(row => row.Name == "Local UI pair"),
                "the actual local/local Save click persists and displays its pair");
            var pairs = Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
            var saved = pairs.Pairs.Single();
            EndpointUiRequire(saved.LeftEndpoint is { IsLocal: true } && saved.RightEndpoint is { IsLocal: true } &&
                saved.Direction == SyncMode.UploadOnly && saved.InitialReviewApproved == false &&
                !File.Exists(Path.Combine(right, "payload.txt")),
                "Endpoint UI: shared Save retains both roots/direction and requires first-sync review before copying");
            EndpointUiRequire(saved.RightControlNamespace is not null && saved.RecoveryRoot is not null,
                "Endpoint UI: saved pair retains its real right-local private recovery binding");
            // UI354-ICON1: both endpoint-kind marks are bounded by a real measure and arrange pass for
            // every offered kind, so an intrinsic provider size can never inflate the icon.
            EndpointUiCall(window, "BeginNewPairEditor");
            foreach (var kind in new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            {
                EndpointUiField<ComboBox>(window, "LeftEndpointKindBox").SelectedValue = kind;
                EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = kind;
                window.UpdateLayout();
                foreach (var hostName in new[] { "LeftEndpointKindIcon", "RightEndpointKindIcon" })
                {
                    var markHost = EndpointUiField<ContentControl>(window, hostName);
                    markHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    markHost.Arrange(new Rect(markHost.DesiredSize));
                    var drawn = markHost.Content as FrameworkElement;
                    drawn?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    drawn?.Arrange(new Rect(drawn.DesiredSize));
                    var markTemplateKey = SyncEndpointPresentation.IconTemplateKey(kind);
                    var markTemplate = window.TryFindResource(markTemplateKey) as DataTemplate;
                    EndpointUiRequire(markTemplate is not null,
                        "Endpoint UI: the window resource scope declares the " + kind + " endpoint mark template (" +
                        markTemplateKey + ")");
                    // Prove the artwork actually drawn is that registered template's own artwork, by
                    // rendering it beside a reference composition of the same existing template. A generic
                    // cloud fallback cannot match, and the three kinds are proven distinct below.
                    var shownArtwork = EndpointUiRenderHash(markHost);
                    var referenceArtwork = EndpointUiRenderHash(EndpointUiUniformReference(markTemplate!));
                    EndpointUiRequire(shownArtwork == referenceArtwork,
                        "Endpoint UI: the " + hostName + " mark displays the registered " + kind +
                        " artwork, not a fallback (shown " + shownArtwork.Substring(0, 12) + " vs template " +
                        referenceArtwork.Substring(0, 12) + ")");
                    EndpointUiRequire(markHost.Content is Viewbox,
                        "Endpoint UI: the " + hostName + " mark hosts the artwork in the shared uniform viewbox for " + kind);
                    EndpointUiRequire(markHost.Width == 28 && markHost.Height == 28 &&
                        markHost.ActualWidth <= 28.01 && markHost.ActualHeight <= 28.01 &&
                        drawn is { } mark && mark.ActualWidth <= 28.01 && mark.ActualHeight <= 28.01 &&
                        mark.ActualWidth > 0 && mark.ActualHeight > 0 &&
                        (mark is not Viewbox viewbox || viewbox.Stretch == System.Windows.Media.Stretch.Uniform),
                        "Endpoint UI: the " + hostName + " mark measures, arranges and fits inside 28x28 for " + kind +
                        " (content " + (markHost.Content?.GetType().Name ?? "none") + ", host " +
                        markHost.ActualWidth + "x" + markHost.ActualHeight + ", mark " +
                        (drawn?.ActualWidth ?? 0) + "x" + (drawn?.ActualHeight ?? 0) + ")");
                }
            }
            var renderedByKind = new Dictionary<SyncEndpointKind, string>();
            foreach (var kind in new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            {
                EndpointUiField<ComboBox>(window, "LeftEndpointKindBox").SelectedValue = kind;
                EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = kind;
                window.UpdateLayout();
                var leftHost = EndpointUiField<ContentControl>(window, "LeftEndpointKindIcon");
                leftHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                leftHost.Arrange(new Rect(leftHost.DesiredSize));
                renderedByKind[kind] = EndpointUiRenderHash(leftHost);
            }
            EndpointUiRequire(renderedByKind[SyncEndpointKind.Local] != renderedByKind[SyncEndpointKind.Dropbox] &&
                renderedByKind[SyncEndpointKind.Local] != renderedByKind[SyncEndpointKind.GoogleDrive] &&
                renderedByKind[SyncEndpointKind.Dropbox] != renderedByKind[SyncEndpointKind.GoogleDrive],
                "Endpoint UI: the local folder, Dropbox and Google Drive marks render as three distinct existing artworks");
            // UI354-PROVIDER1: choosing Google Drive with no account yet must describe that kind on every
            // visible label, mark and tooltip immediately, and never leave the former provider's data.
            EndpointUiCall(window, "BeginNewPairEditor");
            // Present the former provider's folder state through the realised controls, which is what a
            // stale tree action would have to act on.
            EndpointUiField<TextBox>(window, "DropboxRootBox").Text = "/Former Dropbox folder";
            EndpointUiField<TextBlock>(window, "DropboxFolderIdText").Text = "id:former-folder";
            EndpointUiField<TreeView>(window, "DropboxRootTree").ItemsSource = null;
            EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.GoogleDrive;
            window.UpdateLayout();
            var rightSummary = EndpointUiField<TextBlock>(window, "RightEndpointSummary");
            var rightMark = EndpointUiField<ContentControl>(window, "RightEndpointKindIcon");
            var rightAccount = EndpointUiField<ComboBox>(window, "RightEndpointAccountBox");
            var rightBrowse = EndpointUiField<Button>(window, "RightEndpointBrowseButton");
            EndpointUiRequire(window.ProviderPresentation.Key == SyncProviders.GoogleDrive &&
                window.ProviderPresentation.CloudPanelSubtitle == "Paired folder in Google Drive",
                "Endpoint UI: the retained panel caption names the chosen Google Drive kind, not the former provider");
            EndpointUiRequire(!EndpointUiTexts(window).Any(text => text == "Paired folder in Dropbox"),
                "Endpoint UI: no visible caption still claims the former provider after an unsaved kind change");
            EndpointUiRequire(rightSummary.Text.Contains("Google Drive", StringComparison.Ordinal) &&
                !rightSummary.Text.Contains("Dropbox", StringComparison.Ordinal),
                "Endpoint UI: the right endpoint summary names the chosen kind with no account chosen");
            EndpointUiRequire(System.Windows.Automation.AutomationProperties.GetName(rightMark) == "Google Drive endpoint mark",
                "Endpoint UI: the right endpoint mark publishes the chosen kind as its accessible name");
            EndpointUiRequire((rightBrowse.ToolTip as string ?? string.Empty).Contains("Google Drive", StringComparison.Ordinal) &&
                !(rightBrowse.ToolTip as string ?? string.Empty).Contains("Dropbox", StringComparison.Ordinal),
                "Endpoint UI: the right endpoint browse action describes the chosen kind");
            EndpointUiRequire(rightAccount.Visibility == Visibility.Visible &&
                (rightAccount.ToolTip as string ?? string.Empty).Contains("Google Drive", StringComparison.Ordinal),
                "Endpoint UI: the account chooser prompts for the chosen kind's account");
            EndpointUiRequire(EndpointUiField<TextBox>(window, "DropboxRootBox").Text.Length == 0 &&
                EndpointUiField<TextBlock>(window, "DropboxFolderIdText").Text == "Not selected" &&
                EndpointUiField<TreeView>(window, "DropboxRootTree").Items.Count == 0,
                "Endpoint UI: the former provider's folder selection is invalidated on the kind change");
            // The offered accounts for the chosen kind never include another provider's account.
            var offered = (rightAccount.ItemsSource as System.Collections.IEnumerable)?.Cast<object>()
                .Select(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)!).ToArray() ?? [];
            EndpointUiRequire(offered.All(label => !label.Contains("Dropbox", StringComparison.Ordinal)),
                "Endpoint UI: only the chosen kind's accounts are offered for this side");
            // UI354-DRAFT1: the new-pair hint is friendly copy and never enum or stable-id vocabulary.
            EndpointUiCall(window, "BeginNewPairEditor");
            EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.Dropbox;
            window.UpdateLayout();
            var hint = EndpointUiField<TextBlock>(window, "PairEndpointValidationText").Text;
            EndpointUiRequire(hint.Contains("Choose a folder on this computer.", StringComparison.Ordinal) ||
                hint.Contains("Choose an account for each cloud side.", StringComparison.Ordinal) ||
                hint.Contains("Choose a folder for each cloud side.", StringComparison.Ordinal),
                "Endpoint UI: the new-pair hint states a friendly instruction (" + hint + ")");
            EndpointUiRequire(!hint.Contains("Missing", StringComparison.Ordinal) &&
                !hint.Contains("MissingLocalPath", StringComparison.Ordinal) &&
                !hint.Contains("MissingCloudAccount", StringComparison.Ordinal) &&
                !hint.Contains("MissingCloudFolder", StringComparison.Ordinal),
                "Endpoint UI: the new-pair hint never exposes a validation enum code");
            EndpointUiRequire(hint.Split("Choose a folder on this computer.", StringSplitOptions.None).Length <= 2 &&
                hint.Split("Choose an account for each cloud side.", StringSplitOptions.None).Length <= 2 &&
                hint.Split("Choose a folder for each cloud side.", StringSplitOptions.None).Length <= 2,
                "Endpoint UI: identical validation messages are de-duplicated in the new-pair hint");
            EndpointUiRequire(!EndpointUiField<Button>(window, "SavePairButton").IsEnabled,
                "Endpoint UI: an incomplete explicit draft stays unsavable while its hint is friendly");
            // UI354-DRAFT2: while the explicit session is active the legacy Connection panel is collapsed
            // and Pair Name spans its width, and the existing responsive layout keeps that rule.
            var syncTypePanel = EndpointUiField<StackPanel>(window, "SyncTypePanel");
            var namePanel = EndpointUiField<TextBox>(window, "PairNameBox").Parent as FrameworkElement;
            EndpointUiRequire(syncTypePanel.Visibility == Visibility.Collapsed,
                "Endpoint UI: the explicit endpoint editor hides the legacy Connection panel");
            EndpointUiRequire(namePanel is not null && System.Windows.Controls.Grid.GetColumnSpan(namePanel) == 2,
                "Endpoint UI: the pair name spans the width freed by the hidden legacy panel");
            foreach (var layoutWidth in new[] { 900d, 1240d })
            {
                window.Width = layoutWidth;
                EndpointUiCall(window, "UpdateResponsiveLayout");
                window.UpdateLayout();
                EndpointUiRequire(syncTypePanel.Visibility == Visibility.Collapsed && namePanel is not null &&
                    System.Windows.Controls.Grid.GetColumnSpan(namePanel) == 2,
                    "Endpoint UI: the explicit editor layout holds at " + layoutWidth + " DIP after a responsive pass");
            }
            // Exercise the actual GUI consumer of verified account bindings, not just the core helper.
            var legacyAccount = InspectionAccount("personal", "Example Personal");
            Task.Run(() => new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(legacyAccount))
                .GetAwaiter().GetResult();
            var legacyPair = saved with { Id = "legacy-editor", Provider = SyncProviders.Dropbox,
                ConnectionId = legacyAccount.ConnectionId, DropboxRoot = "/First", DropboxFolderId = "id:first",
                EndpointSchemaVersion = null, LeftEndpoint = null, RightEndpoint = null };
            EndpointUiSet(window, "_pairSet", new SyncPairSet([legacyPair], legacyPair.Id));
            EndpointUiSet(window, "_editingNewPair", false);
            EndpointUiSet(window, "_editingPairRootId", legacyPair.Id);
            var legacySession = (SyncPairEditorSession)typeof(MainWindow)
                .GetMethod("CreateEndpointSession", EndpointUiFlags)!.Invoke(window, null)!;
            EndpointUiRequire(legacySession.Draft(SyncPairSide.Right).NamespaceId == legacyAccount.RootNamespaceId &&
                    legacySession.Draft(SyncPairSide.Right).FolderId == "id:first" &&
                    legacySession.Draft(SyncPairSide.Right).ConnectionId == legacyAccount.ConnectionId,
                "The real existing-pair GUI session supplies its exact verified account namespace and retains the folder");
            // The nested case uses the same synthetic account as the existing policy fixture. Keep its
            // registered-client input scoped to this process and restore it even when the case fails.
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "fixture-client";
            RunEndpointNestedPartitionCase(Path.Combine(root, "nested-partition"));
            Console.WriteLine("PASS endpoint WPF local/local save and legacy account binding; native dialog and installed acceptance remain separate.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = priorClient;
            MainWindow.PairFolderBrowserFactoryForTest = null;
            MainWindow.RecoveryFolderDialogForTest = null;
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            window?.RequestProductExit();
            app?.Shutdown();
            foreach (var held in ReversedUiInstanceLocks) held.Dispose();
            ReversedUiInstanceLocks.Clear();
        }
    }

    private const BindingFlags EndpointUiFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>
    /// R28/EUI-S5: the ACTUAL editor draft and the real Save admit a proven excluded nested child through
    /// the one shared partition policy, and refuse the same parent shape without that exclusion. The
    /// retained child is real state this case holds first through the same Save path, and the parent's
    /// exclusion rules come from the same matching-plan derivation the save consumes.
    /// </summary>
    private static void RunEndpointNestedPartitionCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: false, server =>
        {
            server.AddFolder("nest-parent", "Parent", "root");
            server.AddFolder("nest-child", "Child", "nest-parent");
        }, out _);
        var parentLocal = Directory.CreateDirectory(Path.Combine(fixture, "parent")).FullName;
        var childLocal = Directory.CreateDirectory(Path.Combine(parentLocal, "Child")).FullName;
        File.WriteAllText(Path.Combine(childLocal, "child.txt"), "nested child payload");
        var parentFolderId = harness.Server.IdByName("root", "Parent");
        var childFolderId = harness.Server.IdByName("nest-parent", "Child");

        // The parent draft is authored through the real controls with its matching plan installed BEFORE
        // the availability decision, because only a still-matching plan may supply the exclusion rules
        // that make a nested parent saveable at all.
        void AuthorParentDraft(string pairName, IReadOnlyList<SyncSelectionRule> rules)
        {
            EndpointUiCall(harness.Window, "BeginNewPairEditor");
            var session = EndpointUiField<SyncPairEditorSession>(harness.Window, "_endpointSession");
            EndpointUiField<TextBox>(harness.Window, "PairNameBox").Text = pairName;
            EndpointUiField<ComboBox>(harness.Window, "LeftEndpointKindBox").SelectedValue =
                SyncEndpointKind.GoogleDrive;
            EndpointUiField<ComboBox>(harness.Window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.Local;
            EndpointUiField<ComboBox>(harness.Window, "LeftEndpointAccountBox").SelectedValue =
                harness.Binding.ConnectionId;
            var leftPicker = session.PickerFor(SyncPairSide.Left);
            EndpointUiRequire(leftPicker.ApplyCloudFolder(leftPicker.BeginPick(),
                    new DropboxFolderEntry("Parent", "/Parent", parentFolderId)).Applied,
                "Nested partition: the real left picker binds the parent cloud folder for " + pairName);
            var rightPicker = session.PickerFor(SyncPairSide.Right);
            EndpointUiRequire(rightPicker.ApplyLocalFolder(rightPicker.BeginPick(), parentLocal).Applied,
                "Nested partition: the real right picker binds the parent local root for " + pairName);
            EndpointUiField<ComboBox>(harness.Window, "PairDirectionBox").SelectedValue = SyncMode.TwoWay;
            SetEndpointUiField(harness.Window, "_globalDraftPlan", new PairDraftBindingPlan(parentLocal,
                new DropboxFolderEntry("Parent", "/Parent", parentFolderId), true, rules));
        }

        // (0) The retained child is real state this case already holds, saved through the same real path.
        AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Nested child", childLocal, childFolderId,
            "/Parent/Child");
        SaveEndpointPolicyDraft(harness.Window, "Nested child");
        var child = LoadEndpointPolicyPair(harness.Host, "Nested child");
        var controlRelative = child.ControlNamespace is { Local.RootPath.Length: > 0 } control
            ? Path.GetRelativePath(parentLocal, control.Local.RootPath).Replace('\\', '/')
            : null;
        EndpointUiRequire(controlRelative is not null &&
            controlRelative.StartsWith(".syncshark-control-", StringComparison.Ordinal) &&
            !controlRelative.StartsWith("..", StringComparison.Ordinal),
            "Nested partition: the retained child's own private control namespace sits inside the parent root (" +
            (controlRelative ?? "absent") + ")");
        var exclusionRules = new List<SyncSelectionRule> { new SyncSelectionRule("Child", false) };
        if (controlRelative is not null) exclusionRules.Add(new SyncSelectionRule(controlRelative, false));

        // (1) Positive: the parent draft excludes the whole child subtree, data plus private control.
        AuthorParentDraft("Excluded parent", exclusionRules);
        EndpointUiCall(harness.Window, "UpdatePairSaveAvailability");
        var admitted = harness.Window.EndpointDraft();
        EndpointUiRequire(admitted is { CanSave: true },
            "Nested partition: the complete policy admits the parent draft that excludes the whole child subtree" +
            (admitted is null ? " (no draft)" : " (" + admitted.Validation.Describe() + ")"));
        SaveEndpointPolicyDraft(harness.Window, "Excluded parent");
        var parent = LoadEndpointPolicyPair(harness.Host, "Excluded parent");
        EndpointUiRequire(parent.SelectionDefaultIncluded &&
            parent.SelectionRules is { Count: var savedCount } && savedCount == exclusionRules.Count &&
            parent.SelectionRules.Any(rule => rule.FolderPath == "Child" && !rule.Included) &&
            (controlRelative is null ||
                parent.SelectionRules.Any(rule => rule.FolderPath == controlRelative && !rule.Included)),
            "Nested partition: the real Save persists the exact child and private-control exclusions");
        var childAfter = LoadEndpointPolicyPair(harness.Host, "Nested child");
        EndpointUiRequire(string.Equals(childAfter.Id, child.Id, StringComparison.Ordinal) &&
            string.Equals(childAfter.LocalRoot, child.LocalRoot, StringComparison.OrdinalIgnoreCase),
            "Nested partition: the retained child pair is never rebound or overwritten by the parent save");

        // Every remaining control edits the EXISTING retained parent through its own real editor row, so
        // the refusal is proven on that exact retained identity and can never be a duplicate-root artefact.
        void ReopenParentEditor()
        {
            var row = harness.Window.PairRows.Single(item =>
                string.Equals(item.Id, parent.Id, StringComparison.Ordinal));
            harness.Window.PairList.ScrollIntoView(row);
            harness.Window.PairList.UpdateLayout();
            harness.Window.PairList.SelectedItem = row;
            PumpUntil(harness.Window, () =>
                    EndpointUiNullableField<SyncPairEditorSession>(harness.Window, "_endpointSession") is { } opened &&
                    string.Equals(opened.EditingPairId, parent.Id, StringComparison.Ordinal),
                "Nested partition: the retained parent's own row reopens its real editor draft");
        }

        void SetParentPlan(IReadOnlyList<SyncSelectionRule> rules) =>
            SetEndpointUiField(harness.Window, "_globalDraftPlan", new PairDraftBindingPlan(parentLocal,
                new DropboxFolderEntry("Parent", "/Parent", parentFolderId), true, rules));

        void WaitForSaveCompletion()
        {
            PumpUntil(harness.Window, () => !EndpointSaveInFlight(harness.Window),
                "Nested partition: the real save handler for the retained parent completes");
        }

        void RequireRefusedAndUnchanged(string label)
        {
            EndpointUiCall(harness.Window, "UpdatePairSaveAvailability");
            var controlDraft = harness.Window.EndpointDraft();
            EndpointUiRequire(controlDraft is { CanSave: false } && controlDraft.Validation.Refusals.Any(item =>
                    item.Error == SyncEndpointError.OverlappingEndpointAcrossPairs),
                "Nested partition: " + label + " is refused with the specific overlap error" +
                (controlDraft is null ? " (no draft)" : " (" + controlDraft.Validation.Describe() + ")"));
            EndpointUiRequire(!EndpointUiField<Button>(harness.Window, "SavePairButton").IsEnabled,
                "Nested partition: " + label + " keeps the real save gate closed, so no save can mutate state");
            RaiseEndpointPolicyButton(harness.Window, "SavePairButton");
            WaitForSaveCompletion();
            var after = Task.Run(() => harness.Host.LoadPairsAsync()).GetAwaiter().GetResult();
            var parentAfter = after.Pairs.Single(item => string.Equals(item.Id, parent.Id, StringComparison.Ordinal));
            var childAgain = after.Pairs.Single(item => string.Equals(item.Id, child.Id, StringComparison.Ordinal));
            EndpointUiRequire(after.Pairs.Count == 2 &&
                parentAfter.PolicyRevision == parent.PolicyRevision &&
                parentAfter.SelectionDefaultIncluded == parent.SelectionDefaultIncluded &&
                SyncSelectionPolicy.Normalize(parentAfter.SelectionRules)
                    .SequenceEqual(SyncSelectionPolicy.Normalize(parent.SelectionRules)) &&
                SyncIgnorePolicy.Normalize(parentAfter.IgnoreRules)
                    .SequenceEqual(SyncIgnorePolicy.Normalize(parent.IgnoreRules)) &&
                childAgain.PolicyRevision == child.PolicyRevision &&
                string.Equals(childAgain.LocalRoot, child.LocalRoot, StringComparison.OrdinalIgnoreCase),
                "Nested partition: " + label +
                " leaves the persisted parent and child definitions, revisions and authority unchanged");
        }

        // (2) The child DATA exclusion removed on the existing retained parent.
        ReopenParentEditor();
        SetParentPlan([new SyncSelectionRule(controlRelative!, false)]);
        RequireRefusedAndUnchanged("removing the child data exclusion on the retained parent");

        // (3) An INCLUDED descendant exception inside the excluded child invalidates the proof.
        ReopenParentEditor();
        SetParentPlan([new SyncSelectionRule("Child", false),
            new SyncSelectionRule("Child/child.txt", true),
            new SyncSelectionRule(controlRelative!, false)]);
        RequireRefusedAndUnchanged("an included exception inside the excluded child subtree");

        // (4) The child's own private control exclusion removed on the same retained parent.
        ReopenParentEditor();
        SetParentPlan([new SyncSelectionRule("Child", false)]);
        RequireRefusedAndUnchanged("removing the child's private control exclusion");

        // (5) Positive edit: with the whole-child policy intact a real prompt-free policy change saves
        // through the same retained identity, and the asynchronous save completion is observed on the
        // persisted pair rather than inferred from a dispatched event.
        ReopenParentEditor();
        SetParentPlan(exclusionRules);
        EndpointUiField<CheckBox>(harness.Window, "PairEnabledBox").IsChecked = !parent.Enabled;
        EndpointUiCall(harness.Window, "UpdatePairSaveAvailability");
        EndpointUiRequire(harness.Window.EndpointDraft() is { CanSave: true },
            "Nested partition: the retained parent stays saveable while its whole-child exclusion is intact");
        RaiseEndpointPolicyButton(harness.Window, "SavePairButton");
        WaitForSaveCompletion();
        var editedParent = LoadEndpointPolicyPair(harness.Host, "Excluded parent");
        var editedChild = LoadEndpointPolicyPair(harness.Host, "Nested child");
        EndpointUiRequire(editedParent.Enabled != parent.Enabled &&
            SyncSelectionPolicy.Normalize(editedParent.SelectionRules)
                .SequenceEqual(SyncSelectionPolicy.Normalize(parent.SelectionRules)) &&
            editedParent.SelectionDefaultIncluded == parent.SelectionDefaultIncluded &&
            string.Equals(editedChild.Id, child.Id, StringComparison.Ordinal) &&
            string.Equals(editedChild.LocalRoot, child.LocalRoot, StringComparison.OrdinalIgnoreCase),
            "Nested partition: the completed real edit save persists the changed policy, keeps the exact child exclusions and leaves the child pair untouched");
    }

    private static T EndpointUiField<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, EndpointUiFlags)?.GetValue(window)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, name));

    private static void EndpointUiCall(MainWindow window, string name) =>
        (typeof(MainWindow).GetMethod(name, EndpointUiFlags)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, name)).Invoke(window, null);

    private static void EndpointUiSet(MainWindow window, string name, object? value) =>
        (typeof(MainWindow).GetField(name, EndpointUiFlags)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, name)).SetValue(window, value);

    private static IEnumerable<string> EndpointUiTexts(DependencyObject parent)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            if (node is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) yield return text.Text;
            foreach (var nested in EndpointUiTexts(node)) yield return nested;
        }
    }

    /// <summary>The pixels actually drawn by one 28 DIP endpoint-kind mark host, hashed.</summary>
    private static string EndpointUiRenderHash(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(28, 28, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[28 * 28 * 4];
        bitmap.CopyPixels(pixels, 28 * 4, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }

    /// <summary>
    /// The same registered artwork drawn through the same shared uniform host the production helper uses,
    /// so the reference is the existing template itself and never a redrawing of the mark.
    /// </summary>
    private static FrameworkElement EndpointUiUniformReference(DataTemplate template)
    {
        var artwork = template.LoadContent() as UIElement
            ?? throw new InvalidDataException("The registered endpoint mark template has no artwork.");
        var viewbox = new Viewbox { Stretch = System.Windows.Media.Stretch.Uniform, Width = 28, Height = 28,
            Child = artwork };
        viewbox.Measure(new Size(28, 28));
        viewbox.Arrange(new Rect(0, 0, 28, 28));
        viewbox.UpdateLayout();
        return viewbox;
    }

    private static void EndpointUiRequire(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }

    private sealed class EndpointUiNoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The local endpoint UI attempted a cloud HTTP operation.");
    }
}
