using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// W2-C correction 5: the REAL Host to the REAL UI presentation, with no hand-constructed tree.
//
// A local-to-local pair is retained through the real public Core authority, the actual
// SyncAuthorityRuntimeHost.LoadFolderSelectionTreeAsync produces the tree, and that RETURNED tree is fed
// into the existing production presentation path (ApplyFolderHierarchyPresentationForTest, the same method
// the folder-selection page uses). Cloud routing remains covered by the real Host HTTP fixtures.
internal static class EndpointHierarchyUiChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        var stateRoot = Path.Combine(Path.GetFullPath(root), "endpoint-hierarchy-ui");
        Directory.CreateDirectory(stateRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        // The retained authority lives at the same state-authority-derived path the window's own host uses.
        var database = new SyncAuthorityDatabase(Path.Combine(stateRoot, "vNext", "state", "syncshark.db"));
        var installationId = new SyncPairStore(instance.StateAuthority).GetOrCreateInstallationIdAsync().GetAwaiter().GetResult();
        Pump(database.InitializeAsync(installationId, "ui-hierarchy-profile", "fixture", "{}"));
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1100, Height = 740 };
        var passed = 0;
        void Check(bool value, string label)
        {
            WriteObservation(window, label, value);
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }

        try
        {
            window.Show();
            Drain();
            var leftRoot = Path.Combine(stateRoot, "left");
            var rightRoot = Path.Combine(stateRoot, "right");
            Directory.CreateDirectory(Path.Combine(leftRoot, "LeftFolder"));
            Directory.CreateDirectory(Path.Combine(rightRoot, "RightFolder"));
            using var leftLease = PilotDirectoryLease.Open(leftRoot);
            using var rightLease = PilotDirectoryLease.Open(rightRoot);
            var pair = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
                SyncEndpoint.Local(leftLease.Path, SyncLocalEndpointIdentity.From(
                    leftLease.Identity.VolumeSerialNumber, leftLease.Identity.FileId)),
                SyncEndpoint.Local(rightLease.Path, SyncLocalEndpointIdentity.From(
                    rightLease.Identity.VolumeSerialNumber, rightLease.Identity.FileId)),
                SyncMode.TwoWay), "UI hierarchy local-to-local");
            Pump(database.RegisterPairAsync(pair.Id, pair.PolicyRevision));
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, true));

            // The ACTUAL Host reads the tree; nothing here constructs one.
            var tree = Pump(window.AuthorityHost.LoadFolderSelectionTreeAsync(pair.Id, CancellationToken.None));
            Check(tree.EndpointContext is not null && tree.EndpointContext.Left.IsLocal &&
                  tree.EndpointContext.Right.IsLocal && tree.LeftEndpoint is { Available: true } &&
                  tree.RightEndpoint is { Available: true },
                "The real Host read both local endpoints of a retained local-to-local pair");
            Check(tree.Folders.Any(item => item.RelativePath == "LeftFolder") &&
                  tree.Folders.Any(item => item.RelativePath == "RightFolder"),
                "The real Host returned BOTH real folders of the two local roots");

            // The RETURNED tree goes straight into the production presentation path.
            window.ApplyFolderHierarchyPresentationForTest(tree);
            Drain();
            var summary = ((TextBlock)window.FindName("FolderSelectionSummary")).Text;
            var leftHeading = ((TextBlock)window.FindName("LocalHierarchyRootNameText")).Text;
            var rightHeading = ((TextBlock)window.FindName("DropboxHierarchyRootNameText")).Text;
            var leftEmpty = ((TextBlock)window.FindName("LocalHierarchyEmptyStateText")).Text;
            var rightEmpty = ((TextBlock)window.FindName("DropboxHierarchyEmptyStateText")).Text;
            Check(summary.Contains("Only on Left \u2014 This computer", StringComparison.Ordinal) &&
                  summary.Contains("Only on Right \u2014 This computer", StringComparison.Ordinal),
                "The production summary renders the ORDERED headings and each side's own counts: " + summary);
            Check(leftHeading.Contains("Left \u2014 This computer", StringComparison.Ordinal) &&
                  rightHeading.Contains("Right \u2014 This computer", StringComparison.Ordinal),
                "The production root headings name the ordered local endpoints rather than an assumed local left");
            Check(leftEmpty.Contains("no subfolders", StringComparison.Ordinal) &&
                  rightEmpty.Contains("no subfolders", StringComparison.Ordinal),
                "The production empty-state text renders each ordered endpoint's own root-file count: " + leftEmpty + " | " + rightEmpty);

            // ---- correction 4: the stale-result race through the REAL version-checked consumer ----
            SyncPair RetainPair(string tag, int leftFolders, int rightFolders)
            {
                var left = Path.Combine(stateRoot, tag + "-left");
                var right = Path.Combine(stateRoot, tag + "-right");
                Directory.CreateDirectory(left);
                Directory.CreateDirectory(right);
                for (var index = 0; index < leftFolders; index++) Directory.CreateDirectory(Path.Combine(left, "L" + index));
                for (var index = 0; index < rightFolders; index++) Directory.CreateDirectory(Path.Combine(right, "R" + index));
                using var leftLease = PilotDirectoryLease.Open(left);
                using var rightLease = PilotDirectoryLease.Open(right);
                var created = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
                    SyncEndpoint.Local(leftLease.Path, SyncLocalEndpointIdentity.From(
                        leftLease.Identity.VolumeSerialNumber, leftLease.Identity.FileId)),
                    SyncEndpoint.Local(rightLease.Path, SyncLocalEndpointIdentity.From(
                        rightLease.Identity.VolumeSerialNumber, rightLease.Identity.FileId)),
                    SyncMode.TwoWay), "UI race " + tag);
                Pump(database.RegisterPairAsync(created.Id, created.PolicyRevision));
                Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(created, true));
                return created;
            }
            string SummaryText() => ((TextBlock)window.FindName("FolderSelectionSummary")).Text;
            bool TreesEnabled() => ((Control)window.FindName("LocalFolderSelectionTree")).IsEnabled &&
                                   ((Control)window.FindName("DropboxFolderSelectionTree")).IsEnabled;

            var raceA = RetainPair("race-a", 1, 0);
            var raceB = RetainPair("race-b", 1, 1);
            // The normal pair-set and pair-selection setup the production page performs. The diagnostic run
            // proved this was the omission: with pairRows=0 the row-based enablement never ran at all, while
            // runActive, attention and dirty were all already False.
            window.LoadPairSetForTest(new SyncPairSet([raceA, raceB], raceB.Id));
            // Pair selection starts a real asynchronous root-browser initialization that legitimately
            // owns the draft summary. Await that setup before testing the separate versioned loader;
            // a dispatcher drain alone does not await background I/O. Do not start a duplicate editor.
            Pump(window.ExistingPairEditorInitializationForTest);
            Drain();
            var editorNotice = ((TextBlock)window.FindName("PairEditorStatusText")).Text;
            var globalNotice = (TextBlock?)typeof(MainWindow).GetField("_operationFeedback",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            Check(editorNotice.Contains("complete folder browser", StringComparison.Ordinal) &&
                  globalNotice is not null && !globalNotice.Text.Contains("complete folder browser", StringComparison.Ordinal),
                "The real asynchronous pair browser reports in the pair editor, not the global operation footer");
            Pump(Call(window, "RefreshPairSyncStatusAsync", true));
            Check(((TextBlock)window.FindName("PairHealthLocalTitle")).Text == "Left \u2014 This computer" &&
                  ((TextBlock)window.FindName("PairHealthRemoteTitle")).Text == "Right \u2014 This computer" &&
                  ((TextBlock)window.FindName("PairHealthLocalPath")).Text == raceB.LeftEndpoint!.LocalPath &&
                  ((TextBlock)window.FindName("PairHealthRemotePath")).Text == raceB.RightEndpoint!.LocalPath,
                "R538 real status tab names both local endpoints and their exact paths without a Dropbox account fallback");
            var realTreeA = Pump(window.AuthorityHost.LoadFolderSelectionTreeAsync(raceA.Id, CancellationToken.None));

            // (i) The legitimate in-order case, with the seam left NULL so it must use the real Host.
            window.FolderSelectionTreeLoaderForTest = null;
            Pump(Call(window, "LoadFolderSelectionPageAsync", raceB.Id, true, false));
            Drain();
            Check(SummaryText().Contains("Folders Loaded: 2", StringComparison.Ordinal) && TreesEnabled(),
                "In-order: the NULL seam used the real Host and pair B published normally with the trees re-enabled");

            // (ii) Stale SUCCESS: A is delayed, B completes, then A is released ignoring cancellation.
            var releaseA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FolderSelectionTreeLoaderForTest = async (id, token) =>
            {
                if (id != raceA.Id) return await window.AuthorityHost.LoadFolderSelectionTreeAsync(id, token);
                aEntered.TrySetResult(true);
                await releaseA.Task;
                return realTreeA;
            };
            var lateA = Call(window, "LoadFolderSelectionPageAsync", raceA.Id, true, false);
            Pump(aEntered.Task);
            Pump(Call(window, "LoadFolderSelectionPageAsync", raceB.Id, true, false));
            Drain();
            Check(SummaryText().Contains("Folders Loaded: 2", StringComparison.Ordinal),
                "Stale success: pair B published while the delayed A was still pending");
            // Captured from B alone, so the later assertion can prove the late A changed nothing.
            var syncNowEnabledForB = ((Button)window.FindName("EditorSyncNowButton")).IsEnabled;
            releaseA.SetResult(true);
            Pump(lateA);
            Drain();
            Check(SummaryText().Contains("Folders Loaded: 2", StringComparison.Ordinal) &&
                  !SummaryText().Contains("Folders Loaded: 1", StringComparison.Ordinal) && TreesEnabled(),
                "Stale success: the late A completion did NOT replace B's presentation and did not disturb B's state");
            // R3 expansion, stale SUCCESS. Every element below was isolated first by printing what B
            // actually carries; only the elements that genuinely hold are asserted.
            var bLeftPath = Path.Combine(stateRoot, "race-b-left");
            var bRightPath = Path.Combine(stateRoot, "race-b-right");
            var aLeftPath = Path.Combine(stateRoot, "race-a-left");
            var localHeading = ((TextBlock)window.FindName("LocalHierarchyRootNameText")).Text;
            var remoteHeading = ((TextBlock)window.FindName("DropboxHierarchyRootNameText")).Text;
            var rowPaths = window.FolderSelectionRows.Select(item => item.RelativePath).ToArray();
            var rowPairs = window.FolderSelectionRows.Select(item => item.PairId).Distinct().ToArray();
            Check(localHeading.Contains(bLeftPath, StringComparison.OrdinalIgnoreCase) &&
                  remoteHeading.Contains(bRightPath, StringComparison.OrdinalIgnoreCase) &&
                  !localHeading.Contains(aLeftPath, StringComparison.OrdinalIgnoreCase) &&
                  !remoteHeading.Contains(aLeftPath, StringComparison.OrdinalIgnoreCase),
                "Stale success: B's OWN ordered headings survive and the late A never published a heading");
            Check(rowPaths.Length == 2 && rowPaths.Contains("L0") && rowPaths.Contains("R0"),
                "Stale success: B's OWN rows survive the late A completion: " + string.Join(",", rowPaths));
            Check(rowPairs.Length == 1 && rowPairs[0] == raceB.Id,
                "Stale success: every published row still belongs to B's pair: " + string.Join(",", rowPairs));
            Check(((UIElement)window.FindName("LocalHierarchyNotice")).Visibility == Visibility.Collapsed &&
                  ((UIElement)window.FindName("DropboxHierarchyNotice")).Visibility == Visibility.Collapsed,
                "Stale success: B's own endpoint health (no unavailable notice) survives");
            // THE MANDATED CONTRACT, asserted strictly and with observed versus expected printed, even if
            // it is false: B's own action stays B's AND remains enabled, both as captured from B alone and
            // after the late A completed.
            var syncNowAfterLateA = ((Button)window.FindName("EditorSyncNowButton")).IsEnabled;
            Console.WriteLine("R3-ACTION-DIAG runActive=" + window.RunActiveForTest +
                " attention=" + window.PairAttentionSourcesForTest(raceB.Id) +
                " dirty=" + (bool)typeof(MainWindow).GetMethod("CurrentPairEditorHasUnsavedChanges",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)! +
                " pairRows=" + window.PairRows.Count +
                " bAloneFlag=" + syncNowEnabledForB + " afterLateA=" + syncNowAfterLateA);
            Check(syncNowEnabledForB && syncNowAfterLateA,
                "Stale success: B's OWN action stays B's and remains enabled (expected True/True, observed " +
                "B-alone=" + syncNowEnabledForB + ", after-late-A=" + syncNowAfterLateA + ")");

            // (iii) Stale EXCEPTION: A fails late; B must survive and no error must replace B's summary.
            var releaseAError = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aErrorEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FolderSelectionTreeLoaderForTest = async (id, token) =>
            {
                if (id != raceA.Id) return await window.AuthorityHost.LoadFolderSelectionTreeAsync(id, token);
                aErrorEntered.TrySetResult(true);
                await releaseAError.Task;
                throw new InvalidDataException("stale A failure");
            };
            var lateAFailure = Call(window, "LoadFolderSelectionPageAsync", raceA.Id, true, false);
            Pump(aErrorEntered.Task);
            Pump(Call(window, "LoadFolderSelectionPageAsync", raceB.Id, true, false));
            Drain();
            releaseAError.SetResult(true);
            Pump(lateAFailure);
            Drain();
            Check(SummaryText().Contains("Folders Loaded: 2", StringComparison.Ordinal) &&
                  !SummaryText().Contains("unavailable", StringComparison.OrdinalIgnoreCase) && TreesEnabled(),
                "Stale exception: the late A error neither replaced B nor marked B unavailable");
            Check(((TextBlock)window.FindName("LocalHierarchyRootNameText")).Text
                      .Contains(bLeftPath, StringComparison.OrdinalIgnoreCase) &&
                  window.FolderSelectionRows.Count == 2 &&
                  window.FolderSelectionRows.Select(item => item.PairId).Distinct().All(id => id == raceB.Id),
                "Stale exception: B's OWN heading, rows and selection survive the late A failure");

            // (iv) Stale FINALLY: A finishes while B is still loading, so A's finally must not clear or
            // re-enable the controls that B is still using.
            var releaseB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAWhileB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var aWhileBEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FolderSelectionTreeLoaderForTest = async (id, token) =>
            {
                if (id == raceA.Id)
                {
                    aWhileBEntered.TrySetResult(true);
                    await releaseAWhileB.Task;
                    return realTreeA;
                }
                bEntered.TrySetResult(true);
                await releaseB.Task;
                return await window.AuthorityHost.LoadFolderSelectionTreeAsync(id, token);
            };
            var aWhileB = Call(window, "LoadFolderSelectionPageAsync", raceA.Id, true, false);
            Pump(aWhileBEntered.Task);
            var bWhileA = Call(window, "LoadFolderSelectionPageAsync", raceB.Id, true, false);
            Pump(bEntered.Task);
            releaseAWhileB.SetResult(true);
            Pump(aWhileB);
            Drain();
            Check(!TreesEnabled(),
                "Stale finally: the late A finally did NOT clear or re-enable the controls while B was still loading");
            Check(!window.FolderSelectionRows.Any(item => item.PairId == raceA.Id) &&
                  !((TextBlock)window.FindName("LocalHierarchyRootNameText")).Text
                      .Contains(aLeftPath, StringComparison.OrdinalIgnoreCase),
                "Stale finally: no A-side row or heading was published while B was still loading");
            releaseB.SetResult(true);
            Pump(bWhileA);
            Drain();
            Check(TreesEnabled() && SummaryText().Contains("Folders Loaded: 2", StringComparison.Ordinal),
                "The still-loading pair B then completed normally and published its own result");
            Check(((TextBlock)window.FindName("LocalHierarchyRootNameText")).Text
                      .Contains(bLeftPath, StringComparison.OrdinalIgnoreCase) &&
                  ((TextBlock)window.FindName("DropboxHierarchyRootNameText")).Text
                      .Contains(bRightPath, StringComparison.OrdinalIgnoreCase) &&
                  window.FolderSelectionRows.Count == 2 &&
                  window.FolderSelectionRows.Select(item => item.PairId).Distinct().All(id => id == raceB.Id),
                "After B completed, B's OWN headings, rows and selection are the published state");
            window.FolderSelectionTreeLoaderForTest = null;
            // The GUI's manual commands must reach shared dispatch even when no cloud account
            // exists. This seam records dispatch only; real byte effects and account rejection
            // are exercised by the host fixtures, not replaced by this UI admission proof.
            var runnable = raceB with { Enabled = true, InitialReviewApproved = true };
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(runnable, true));
            var current = Pump(window.AuthorityHost.LoadPairsAsync());
            window.LoadPairSetForTest(current);
            window.PrepareExistingPairEditorForTest(runnable);
            Pump(window.ExistingPairEditorInitializationForTest);
            var dispatches = new List<string>();
            MainWindow.DropboxConnectionCheckForTest = () => Task.FromResult(false);
            MainWindow.UnsavedDraftRunChoiceForTest = () => AppDialogChoice.Secondary;
            MainWindow.PairRunDispatcherForTest = (pairs, trigger) =>
            {
                Check(pairs.Any(item => item.Id == runnable.Id), "R538 manual dispatch includes the exact accountless local pair");
                dispatches.Add(trigger);
                return Task.CompletedTask;
            };
            foreach (var (handler, control) in new[] { ("RunPairNow_Click", "EditorSyncNowButton"), ("RunAllPairsNow_Click", "SyncAllNowButton") })
            {
                var before = dispatches.Count;
                typeof(MainWindow).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window.FindName(control), new RoutedEventArgs()]);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (dispatches.Count == before && DateTime.UtcNow < deadline) Drain();
                Check(dispatches.Count == before + 1, "R538 " + handler + " reaches shared dispatch without requiring a cloud connection");
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (window.RunActiveForTest && DateTime.UtcNow < deadline) Drain();
                Check(!window.RunActiveForTest,
                    "R538 " + handler + " completes its actual retained-outcome readback before the next manual command");
            }
        }
        finally
        {
            MainWindow.DropboxConnectionCheckForTest = null;
            MainWindow.UnsavedDraftRunChoiceForTest = null;
            MainWindow.PairRunDispatcherForTest = null;
            window.FolderSelectionTreeLoaderForTest = null;
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
        Console.WriteLine(passed + " real Host to real UI hierarchy checks passed; no cloud account, credential or provider call was used.");
        return 0;
    }

    // Capture before Check can throw, synchronously on the same dispatcher as its predicate.
    // This observes the real controls; it neither drains work nor changes the expected contract.
    private static void WriteObservation(MainWindow window, string label, bool actual)
    {
        window.Dispatcher.VerifyAccess();
        object? Field(string name) => typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        string Text(string name) => (window.FindName(name) as TextBlock)?.Text ?? "<missing>";
        object ControlState(string name)
        {
            if (window.FindName(name) is not UIElement control) return new { name, missing = true };
            var ancestors = new List<object>();
            for (var node = System.Windows.Media.VisualTreeHelper.GetParent(control);
                 node is not null && ancestors.Count < 32;
                 node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement parent)
                    ancestors.Add(new { type = node.GetType().Name,
                        name = (node as FrameworkElement)?.Name,
                        enabled = parent.IsEnabled,
                        localEnabled = parent.ReadLocalValue(UIElement.IsEnabledProperty).ToString() });
            }
            return new { name, enabled = control.IsEnabled,
                localEnabled = control.ReadLocalValue(UIElement.IsEnabledProperty).ToString(),
                visibility = control.Visibility.ToString(), ancestors };
        }
        var selected = (window.FindName("PairList") as DataGrid)?.SelectedItem;
        var observation = new
        {
            label, expected = true, actual,
            summary = Text("FolderSelectionSummary"),
            loading = Field("_loadingFolderSelection"),
            hierarchyVersion = Field("_folderSelectionLoadVersion"),
            previewVersion = Field("_pairDraftPreviewVersion"),
            editorGeneration = Field("_pairEditorGeneration"),
            draftActive = typeof(MainWindow).GetProperty("PairLocationDraftActive",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window),
            editingPairId = Field("_editingPairRootId"),
            selectedPairId = selected?.GetType().GetProperty("Id")?.GetValue(selected),
            leftHeading = Text("LocalHierarchyRootNameText"),
            rightHeading = Text("DropboxHierarchyRootNameText"),
            rows = window.FolderSelectionRows.Select(row => new { row.PairId, row.RelativePath }).ToArray(),
            leftTree = ControlState("LocalFolderSelectionTree"),
            rightTree = ControlState("DropboxFolderSelectionTree"),
            leftNotice = ControlState("LocalHierarchyNotice"),
            rightNotice = ControlState("DropboxHierarchyNotice"),
            leftNoticeText = Text("LocalHierarchyNoticeText"),
            rightNoticeText = Text("DropboxHierarchyNoticeText"),
            syncNow = ControlState("EditorSyncNowButton")
        };
        Console.WriteLine("R3-STATE " + System.Text.Json.JsonSerializer.Serialize(observation));
    }

    private static void Pump(Task task) { while (!task.IsCompleted) Drain(); task.GetAwaiter().GetResult(); }
    private static T Pump<T>(Task<T> task) { while (!task.IsCompleted) Drain(); return task.GetAwaiter().GetResult(); }
    private static Task Call(MainWindow window, string name, params object[] arguments) => (Task)typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments)!;
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
