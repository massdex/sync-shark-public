using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    // Real persisted producer -> host's account query -> actual MainWindow header/list.
    // Only provider/credential access is replaced; no fake filters substitute for the query.
    private static int RunActivityAccountChecks(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        using var http = new HttpClient(new InspectionNetworkBoundary());
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "vNext", "state", "syncshark.db"));
        var accounts = new[] { InspectionAccount("account-ui-a", "Account A"), InspectionAccount("account-ui-b", "Account B") };
        var pairs = Task.Run(async () =>
        {
            await connections.SaveNewAsync(accounts[0]);
            await connections.SaveAsync(accounts[1] with
            { CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(accounts[1].ConnectionId) });
            var installationId = await new SyncPairStore(instance.StateAuthority).GetOrCreateInstallationIdAsync();
            await database.InitializeAsync(installationId, "inspection-profile", "fixture", "{}");
            var result = new List<SyncPair>();
            var scanIds = new Dictionary<string, (string Local, string Dropbox)>(StringComparer.Ordinal);
            foreach (var (account, index) in accounts.Select((account, index) => (account, index)))
            {
                var local = Path.Combine(root, "computer", "account-" + index);
                Directory.CreateDirectory(local);
                await File.WriteAllTextAsync(Path.Combine(local, "cloud test.txt"), "Isolated account-scope fixture.\n");
                using var lease = PilotDirectoryLease.Open(local);
                // Equal display paths on different accounts are intentional, but Dropbox folder IDs
                // are provider-global: these two distinct fixture folders must have distinct IDs.
                var pair = new SyncPair("account-ui-" + index, "Same pair name", local,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/First", "id:account-ui-first-" + index,
                    ConnectionId: account.ConnectionId, InitialReviewApproved: true);
                result.Add(pair);
                await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                    "recoverable", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                    AutomaticPruning: false, RecoveryRetentionDays: int.MaxValue));
                await database.RetainPairDefinitionAsync(pair, index == 0);
                scanIds[pair.Id] = await SeedInspectionPathIndexAsync(database, pair, account);
                // Account A dominates the newest unfiltered page; B must not disappear merely
                // because an implementation filters the already-loaded All Accounts page.
                for (var row = 0; row < (index == 0 ? 240 : 65); row++)
                    await LegacyHistoryFixture.SeedAsync(database, new SyncActivityEvent("account-ui-" + index + "-" + row,
                        DateTimeOffset.UtcNow.AddDays(-index).AddSeconds(row), [SyncActivityCategories.Files],
                        "Success", "File", "Uploaded", "Fixture file uploaded", account.ConnectionId, pair.Id,
                        RelativePath: "file-" + row + ".txt"));
            }
            await SeedInspectionPendingAndConflictAsync(database, result, scanIds);
            // V6/F16-adjacent: retained terminal run rows in exactly the shape the product keeps, so the Home
            // last-success line has a real authoritative value per scope. They are written directly rather
            // than through the production transition, because that transition also appends presentation
            // history events and this same route already asserts the exact Activity page those events would
            // change. The primary account holds the older success, the secondary the newer one, and a later
            // unrelated failure plus a later recovery run must never be inferred from.
            void SeedCompletedRun(string id, SyncPair pair, bool failed, long minutesAgo, string kind = "run_pair")
            {
                var requested = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
                using var connection = new SqliteConnection("Data Source=" + database.DatabasePath);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO command_request(command_request_id,command_kind,pair_id," +
                    "expected_generation,payload_json,requested_utc,status,terminal_utc,result_json) " +
                    "VALUES($id,$kind,$pair,NULL,$payload,$requested,'completed',$terminal,$result);";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$pair", pair.Id);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }));
                command.Parameters.AddWithValue("$requested",
                    requested.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$terminal",
                    requested.AddSeconds(30).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(new
                {
                    completedPairs = failed ? 0 : 1, failedPairs = failed ? 1 : 0, Stopped = 0
                }));
                command.ExecuteNonQuery();
            }
            // The accountless local/local pair the product retains without any provider account: its success
            // is the newest of all, so All Accounts must include it while neither selected account may.
            var localOnlyRoot = Path.Combine(root, "computer", "local-only");
            Directory.CreateDirectory(localOnlyRoot);
            await File.WriteAllTextAsync(Path.Combine(localOnlyRoot, "local test.txt"), "Isolated local-only fixture.\n");
            using (var localLease = PilotDirectoryLease.Open(localOnlyRoot))
            {
                var localOnly = new SyncPair("account-ui-local-only", "Local only pair", localOnlyRoot,
                    localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId, "/Local", "id:local-only",
                    ConnectionId: "", InitialReviewApproved: true);
                result.Add(localOnly);
                await database.RegisterPairAsync(localOnly.Id, localOnly.PolicyRevision, new SyncAuthorityPairConfiguration(
                    "recoverable", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                    AutomaticPruning: false, RecoveryRetentionDays: int.MaxValue));
                await database.RetainPairDefinitionAsync(localOnly, false);
                SeedCompletedRun("run-scope-local-newest", localOnly, failed: false, minutesAgo: 5);
            }
            SeedCompletedRun("run-scope-primary-older", result[0], failed: false, minutesAgo: 90);
            SeedCompletedRun("run-scope-secondary-newer", result[1], failed: false, minutesAgo: 60);
            SeedCompletedRun("run-scope-later-failure", result[1], failed: true, minutesAgo: 40);
            SeedCompletedRun("run-scope-later-recovery", result[0], failed: false, minutesAgo: 20, kind: "reconcile");
            SqliteConnection.ClearAllPools();
            return new SyncPairSet(result, result[0].Id);
        }).GetAwaiter().GetResult();
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), connections, new InspectionCredentialBoundary(), database,
            (_, _) => throw new InvalidOperationException("No provider effects in account UI checks."));
        Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme)
        { Width = 1200, Height = 780, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
        BindAuthorityHost(window, host);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        void PumpUntil(Func<bool> completed, string failure)
        {
            if (completed()) return;
            var watch = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            // Keep the real dispatcher processing every priority. A low-priority no-op can be
            // starved by an active window even after the actual operation completed. The timer
            // observes the required result, rather than making global UI idleness a prerequisite.
            var timer = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                if (completed() || watch.Elapsed >= TimeSpan.FromSeconds(30)) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (!completed())
            {
                Console.Error.WriteLine("FAIL " + failure + "; query=" + Field<int>("_activityQueryVersion") +
                    "; applied=" + Field<int>("_activityLastAppliedQueryVersion") +
                    "; rows=" + window.ActivityRows.Count + "; type=" + Field<ComboBox>("ActivityTypeFilter").Text);
                throw new TimeoutException(failure);
            }
        }
        void Pump(Task task)
        {
            PumpUntil(() => task.IsCompleted, "Account UI fixture operation did not settle within 30 seconds.");
            task.GetAwaiter().GetResult();
            // The awaited operation has applied its UI result before completing. Do not then
            // wait for unrelated background/idle work (including deliberately delayed scope
            // requests below). Callers assert the actual result, or pump its explicit condition.
        }
        void Require(bool condition, string text)
        { if (!condition) throw new InvalidDataException(text); Console.WriteLine("PASS " + text); }
        ActivityRow[] HomeRows() => Field<IEnumerable<ActivityRow>>("_homeRows").ToArray();
        void OpenLinkedPairs(string account) => typeof(MainWindow).GetMethod("OpenPairsForAccount", flags,
            null, [typeof(string)], null)!.Invoke(window, [account]);
        void Select(string? account)
        {
            var picker = Field<List<ComboBox>>("_workspaceAccountPickers").Single(item =>
                AutomationProperties.GetName(item) == "Activity Account");
            picker.SelectedItem = picker.Items.OfType<ActivityPairChoice>().Single(item => item.ConnectionId == account);
            Pump((Task)Call("RefreshVisibleActivityAsync")!);
        }
        try
        {
            window.LoadPairSetForTest(pairs);
            Pump(window.ExistingPairEditorInitializationForTest);
            typeof(MainWindow).GetField("_accountLabels", flags)!.SetValue(window,
                accounts.ToDictionary(account => account.ConnectionId, account => account.DisplayName!));
            Call("RefreshWorkspaceAccountPickers");
            window.Show();
            // Only the two provider-bound pairs carry a recorded account identity. The accountless local/local
            // pair the V6 case adds is deliberately not part of this existing Home-identity expectation.
            var sampleRows = pairs.Pairs.Where(pair => !string.IsNullOrEmpty(pair.ConnectionId)).Select(pair => new ActivityRow(DateTimeOffset.UtcNow.ToString("O"),
                pair.Id + ".txt", "Uploaded", "Fixture", PairId: pair.Id, PairName: pair.Name,
                ItemKind: "File", FileType: "TXT", Categories: [SyncActivityCategories.Files], Entity: "File",
                ConnectionId: pair.ConnectionId)).ToArray();
            foreach (var row in sampleRows) Call("TrackWorkspaceAction", row);
            Call("RefreshWorkspaceFeeds");
            Require(HomeRows().Select(row => row.ConnectionId).Distinct().Count() == 2,
                "Home initially renders both recorded account identities");
            OpenLinkedPairs(accounts[1].ConnectionId);
            Call("NavigateTo", "Home", true);
            Require(HomeRows().Length > 0 && HomeRows().All(row => row.ConnectionId == accounts[1].ConnectionId),
                "Linked-pairs navigation synchronously removes Personal rows before Home displays Work");
            Pump(Field<Task>("_workspaceScopeRefresh"));
            Require(HomeRows().Length > 0 && HomeRows().All(row => row.ConnectionId == accounts[1].ConnectionId),
                "Linked-pairs navigation replenishes the selected account summary through the real host");
            var unknown = sampleRows[1] with { Path = "unknown-account.txt", ConnectionId = null };
            Call("TrackWorkspaceAction", unknown); Call("RefreshWorkspaceFeeds");
            Require(HomeRows().All(row => row.Path != unknown.Path) && !unknown.MatchesAccount(accounts[1].ConnectionId),
                "A historical row without recorded identity is not attributed from its current pair");
            Call("OpenTrayScope", accounts[0].ConnectionId, true);
            Require(HomeRows().All(row => row.ConnectionId == accounts[0].ConnectionId),
                "Tray account transition immediately clears the previous account's Home feed");
            Pump(Field<Task>("_workspaceScopeRefresh"));
            var delayedSummary = new TaskCompletionSource<IReadOnlyList<SyncAuthorityActivityProjection>>();
            MainWindow.SummaryHistoryLoaderForTest = _ => delayedSummary.Task;
            var staleSummary = (Task)Call("RefreshWorkspaceScopeDataAsync")!;
            MainWindow.SummaryHistoryLoaderForTest = null;
            OpenLinkedPairs(accounts[1].ConnectionId);
            Pump(Field<Task>("_workspaceScopeRefresh"));
            delayedSummary.SetResult([new(DateTimeOffset.UtcNow, pairs.Pairs[0].Id, "late-personal.txt",
                "Uploaded", "Late old-account response", null, "File", "TXT",
                Categories: [SyncActivityCategories.Files], Entity: "File", ConnectionId: accounts[0].ConnectionId)]);
            Pump(staleSummary);
            Require(HomeRows().Length > 0 && HomeRows().All(row => row.ConnectionId == accounts[1].ConnectionId) &&
                    Field<List<ActivityRow>>("_scopedHomeHistory").All(row => row.ConnectionId == accounts[1].ConnectionId),
                "Late Personal summary cannot overwrite Work after linked-pairs account transition");
            Select(null);
            Call("OpenWorkspaceActivity", 0, null);
            Pump((Task)Call("LoadActivityPageAsync", true, null)!);
            Require(window.ActivityRows.Count > 0, "All Accounts loads actual retained activity");
            Select(accounts[1].ConnectionId);
            Require(window.ActivityRows.Count == 65 && window.ActivityRows.All(row => row.PairId == pairs.Pairs[1].Id),
                "Account B query retrieves all 65 matches even behind 240 newer Account A events");
            Require(window.ActivityPairChoices.Count == 2 && window.ActivityPairChoices[1].Id == pairs.Pairs[1].Id,
                "Pair filter contains only the selected account, with no competing account selector");
            var typePicker = Field<ComboBox>("ActivityTypeFilter");
            void SelectType(string label) => typePicker.SelectedItem = typePicker.Items.OfType<ComboBoxItem>()
                .Single(item => string.Equals(item.Content?.ToString(), label, StringComparison.Ordinal));
            void AwaitTypeResult(int expectedCount, int previousVersion)
            {
                PumpUntil(() => Field<int>("_activityLastAppliedQueryVersion") > previousVersion &&
                        window.ActivityRows.Count == expectedCount, "Activity type-filter result did not settle.");
                Require(Field<int>("_activityLastAppliedQueryVersion") > previousVersion &&
                        window.ActivityRows.Count == expectedCount &&
                        window.ActivityRows.All(row => row.PairId == pairs.Pairs[1].Id),
                    "Type dropdown " + typePicker.Text + " refreshes its actual event path without focus loss or explicit reload");
            }
            // Do not call a reload helper here: the native bug was hidden by tests
            // that manually refreshed after setting the editable selection.
            var beforeType = Field<int>("_activityLastAppliedQueryVersion");
            // Reproduce a busy rendering dispatcher without replacing the real selection
            // handler or retained query. Filter commands must not wait for render idleness.
            var renderPressure = true;
            void QueueRenderPressure()
            {
                if (renderPressure)
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(QueueRenderPressure));
            }
            QueueRenderPressure();
            try
            {
                Console.WriteLine("CHECK selecting Folders while render work remains queued");
                SelectType("Folders");
                Console.WriteLine("CHECK Folders selection returned; waiting for its retained query");
                AwaitTypeResult(0, beforeType);
            }
            finally { renderPressure = false; }
            beforeType = Field<int>("_activityLastAppliedQueryVersion");
            SelectType("Files");
            AwaitTypeResult(65, beforeType);
            beforeType = Field<int>("_activityLastAppliedQueryVersion");
            SelectType("All types");
            AwaitTypeResult(65, beforeType);
            beforeType = Field<int>("_activityLastAppliedQueryVersion");
            SelectType("Folders");
            SelectType("Files");
            AwaitTypeResult(65, beforeType);
            beforeType = Field<int>("_activityLastAppliedQueryVersion");
            SelectType("All types");
            AwaitTypeResult(65, beforeType);
            Field<ComboBox>("ActivityPairBox").SelectedIndex = 1;
            Select(accounts[0].ConnectionId);
            Require(window.ActivityRows.All(row => row.PairId == pairs.Pairs[0].Id) &&
                    Field<ComboBox>("ActivityPairBox").SelectedItem is ActivityPairChoice { Id: null },
                "Switching account clears the old account's exact-pair filter and rows");
            var firstPage = window.ActivityRows.Count;
            Pump((Task)Call("LoadActivityPageAsync", false, null)!);
            Require(firstPage == 200 && window.ActivityRows.Count == 240 &&
                    window.ActivityRows.All(row => row.PairId == pairs.Pairs[0].Id),
                "Pagination retains account scope and reaches every matching saved event");
            Call("NavigateTo", "Settings", true);
            Select(accounts[1].ConnectionId);
            Call("OpenWorkspaceActivity", 3, null); // Diagnostics -> former Event Log route.
            Pump((Task)Call("LoadActivityPageAsync", true, null)!);
            Require(window.ActivityRows.All(row => row.PairId == pairs.Pairs[1].Id) && window.ActivityRows.Count == 65,
                "Returning from Diagnostics cannot display another account under the selected header");
            Field<TabControl>("_activityTabs").SelectedIndex = 1;
            Pump((Task)Call("LoadQueuePageAsync", true, null)!);
            Require(window.QueueRows.Count == 1 && window.QueueRows.Single().PairId == pairs.Pairs[1].Id,
                "Pending tab uses the same exact account in its real persisted query");
            Select(accounts[0].ConnectionId);
            Require(window.QueueRows.Count == 1 && window.QueueRows.Single().PairId == pairs.Pairs[0].Id,
                "Changing the header while Pending is open refreshes to the other account");
            Field<TabControl>("_activityTabs").SelectedIndex = 0;
            Pump((Task)Call("LoadActivityPageAsync", true, null)!);
            var delayed = new TaskCompletionSource<IReadOnlyList<SyncAuthorityActivityProjection>>();
            var receivedAccount = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow.ActivityProjectionLoaderForTest = (_, _, _, _, _, _, _, account) =>
            { receivedAccount.TrySetResult(account); return delayed.Task; };
            var oldRequest = (Task)Call("LoadActivityPageAsync", true, null)!;
            // The refresh awaits retention maintenance and the retained-window read before it reaches
            // the projection loader, so the loader is not invoked when Call returns. Read its own
            // completion signal; asserting an assumed call point raced that asynchronous hop.
            PumpUntil(() => receivedAccount.Task.IsCompleted,
                "Delayed activity request never reached its projection loader.");
            Require(string.Equals(receivedAccount.Task.Result, accounts[0].ConnectionId, StringComparison.Ordinal),
                "Delayed request captures the header's exact account");
            MainWindow.ActivityProjectionLoaderForTest = null;
            Select(accounts[1].ConnectionId);
            delayed.SetResult([new(DateTimeOffset.UtcNow, pairs.Pairs[0].Id, "stale.txt", "Uploaded", "Stale response",
                null, "File", "TXT", null, null, null)]);
            Pump(oldRequest);
            Require(window.ActivityRows.Count == 65 && window.ActivityRows.All(row => row.PairId == pairs.Pairs[1].Id),
                "Late old-account response cannot replace the new account's saved rows");
            var selectedEvent = new ActivityRow(DateTimeOffset.Now.ToString("O"), "file-0.txt", "Uploaded", "Fixture",
                PairId: pairs.Pairs[0].Id, PairName: pairs.Pairs[0].Name, ConnectionId: accounts[0].ConnectionId);
            Call("OpenWorkspaceActivity", 0, selectedEvent);
            Pump((Task)Call("LoadActivityPageAsync", true, null)!);
            Require(Field<string?>("_workspaceAccountId") is null &&
                    Field<ActivityRow>("_inspectedActivity").ConnectionId == accounts[0].ConnectionId,
                "Cross-account history opens under All Accounts rather than keeping an incompatible account header");
            Select(null);
            Pump((Task)Call("LoadActivityPageAsync", false, null)!);
            Require(window.ActivityRows.Select(row => row.PairId).Distinct().Count() == 2 && window.ActivityRows.Count == 305,
                "All Accounts restores both scopes through the actual paginated query");
            // UI398-3: the account an endpoint row shows is that row's OWN recorded connection. Selecting a
            // different account must not relabel it, and the label comes from the exact entry for that
            // connection rather than from the currently selected account.
            var endpointOwner = accounts[0].ConnectionId;
            var endpointScoped = new ActivityRow(DateTimeOffset.Now.ToString("O"), "endpoint-scope.txt", "Copied",
                "Recorded and verified.", PairId: pairs.Pairs[1].Id, PairName: pairs.Pairs[1].Name,
                ItemKind: "File", FileType: "TXT")
            {
                EndpointContext = new SyncEndpointContext(pairs.Pairs[1].Id, pairs.Pairs[1].Name,
                    new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Local, "local",
                        LocalPath: pairs.Pairs[1].LocalRoot),
                    new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                        SyncProviders.Dropbox, endpointOwner, "endpoint-ns", "id:endpoint", "/Recorded endpoint"))
            };
            Select(accounts[1].ConnectionId);
            // The actual account refresh may already have replaced the short seeded labels with
            // canonical email/display-name labels. Bind this assertion to the recorded owner's
            // entry, never to refresh timing or the currently selected account's entry.
            var endpointOwnerLabel = Field<Dictionary<string, string>>("_accountLabels")[endpointOwner];
            Call("InspectActivity", endpointScoped);
            var endpointLines = Field<StackPanel>("_detailFields").Children.OfType<StackPanel>()
                .Where(wrapper => wrapper.Children.OfType<TextBlock>().FirstOrDefault() is { Text: var heading } &&
                    heading.StartsWith("Right endpoint:", StringComparison.Ordinal))
                .SelectMany(wrapper => wrapper.Children.OfType<StackPanel>())
                .SelectMany(detail => detail.Children.OfType<TextBlock>().Skip(1))
                .Select(text => text.Text).ToList();
            Require(Field<string?>("_workspaceAccountId") == accounts[1].ConnectionId &&
                    endpointLines.Count == 1 && endpointLines[0] == "Account: " + endpointOwnerLabel &&
                    !endpointLines[0].Contains(accounts[1].DisplayName, StringComparison.Ordinal),
                "A historical endpoint keeps its own account label while another account is selected ('" +
                string.Join(" | ", endpointLines) + "')");
            typeof(MainWindow).GetField("_workspaceRunningPairId", flags)!.SetValue(window, pairs.Pairs[1].Id);
            typeof(MainWindow).GetField("_workspaceRunningPair", flags)!.SetValue(window, pairs.Pairs[1].Name);
            Call("OpenRunningPair");
            Require(Field<DataGrid>("PairList").SelectedItem is PairRow running && running.Id == pairs.Pairs[1].Id &&
                    Field<string>("_workspaceAccountId") == accounts[1].ConnectionId,
                "Running-pair navigation uses the exact pair id despite duplicate names and changes to its account");

            // ---------------------------------------------------------------------------------------------
            // V6/F16-adjacent: the Home last-success line describes the CURRENT account scope. All Accounts
            // resolves the latest successful retained run across EVERY pair (including a local/local pair,
            // which has no provider account); a selected account uses only its own pairs; the primary
            // connection status keeps its own primary-specific value; a late response for a previous scope
            // cannot overwrite the current one; and an absent value is stated truthfully.
            // ---------------------------------------------------------------------------------------------
            void PumpUntilLine(string expected, string label)
            {
                PumpUntil(() => Field<TextBlock>("_homeTiming").Text == expected,
                    "Home last-success line did not reach the expected account scope: " + label);
                Require(Field<TextBlock>("_homeTiming").Text == expected,
                    label + " (showed '" + Field<TextBlock>("_homeTiming").Text + "')");
            }
            Call("NavigateTo", "Home", true);
            Require(Field<bool>("_workspaceReady"), "V6 the workspace is ready before the last-success scope checks");
            Console.WriteLine("TRACE V6 beginning actual cached connection refresh");
            var connectionRefresh = (Task)Call("RefreshConnectionStateAsync", false)!;
            Console.WriteLine("TRACE V6 connection refresh returned task: " + connectionRefresh.Status);
            _ = connectionRefresh.ContinueWith(completed =>
                Console.WriteLine("TRACE V6 connection refresh task settled: " + completed.Status),
                TaskScheduler.Default);
            Pump(connectionRefresh);
            Console.WriteLine("TRACE V6 connection refresh dispatcher drain completed");
            var primaryStatus = Field<SyncAccountStatus?>("_connectionStatus");
            var allScopeValue = Task.Run(() => host.Authority.LoadLastSuccessfulSyncUtcAsync()).GetAwaiter().GetResult();
            var primaryScopeValue = Task.Run(() => host.Authority.LoadLastSuccessfulSyncUtcAsync(
                    allowedPairIds: [pairs.Pairs[0].Id])).GetAwaiter().GetResult();
            var secondaryScopeValue = Task.Run(() => host.Authority.LoadLastSuccessfulSyncUtcAsync(
                    allowedPairIds: [pairs.Pairs[1].Id])).GetAwaiter().GetResult();
            var localScopeValue = Task.Run(() => host.Authority.LoadLastSuccessfulSyncUtcAsync(
                    allowedPairIds: [pairs.Pairs[2].Id])).GetAwaiter().GetResult();
            Require(allScopeValue is not null && localScopeValue == allScopeValue &&
                primaryScopeValue is not null && secondaryScopeValue is not null &&
                allScopeValue > secondaryScopeValue && secondaryScopeValue > primaryScopeValue,
                "V6 fixture: the accountless local/local success is the newest across every pair, both account successes are older, and a later failure and recovery advanced no scope");
            var allLine = "Last successful sync: " + allScopeValue!.Value.ToLocalTime().ToString("g");
            var primaryLine = "Last successful sync: " + primaryScopeValue!.Value.ToLocalTime().ToString("g");
            var secondaryLine = "Last successful sync: " + secondaryScopeValue!.Value.ToLocalTime().ToString("g");

            Select(null);
            Pump((Task)Call("LoadRunHistoryAsync", true)!);
            Pump(Field<Task>("_workspaceScopeRefresh"));
            PumpUntilLine(allLine,
                "V6 Home under All Accounts shows the accountless local/local success as the newest across every pair");
            Require(ReferenceEquals(primaryStatus, Field<SyncAccountStatus?>("_connectionStatus")),
                "V6 the retained-page load never rewrote the primary-specific connection status with the all-accounts value");

            Select(accounts[0].ConnectionId);
            Pump(Field<Task>("_workspaceScopeRefresh"));
            PumpUntilLine(primaryLine,
                "V6 selecting the primary account shows only its own older success and excludes the accountless local pair");

            Select(accounts[1].ConnectionId);
            Pump(Field<Task>("_workspaceScopeRefresh"));
            PumpUntilLine(secondaryLine,
                "V6 selecting the secondary account shows only its own success and excludes the accountless local pair");

            var delayedScope = new TaskCompletionSource<IReadOnlyList<SyncAuthorityActivityProjection>>();
            MainWindow.SummaryHistoryLoaderForTest = _ => delayedScope.Task;
            Select(accounts[0].ConnectionId);
            var staleScopeRefresh = Field<Task>("_workspaceScopeRefresh");
            MainWindow.SummaryHistoryLoaderForTest = null;
            Select(null);
            Pump(Field<Task>("_workspaceScopeRefresh"));
            PumpUntilLine(allLine, "V6 the newer All Accounts scope rendered before the stale refresh was released");
            delayedScope.TrySetResult([new(DateTimeOffset.UtcNow, pairs.Pairs[0].Id, "late-personal.txt",
                "Uploaded", "Late old-account response", null, "File", "TXT",
                Categories: [SyncActivityCategories.Files], Entity: "File", ConnectionId: accounts[0].ConnectionId)]);
            Pump(staleScopeRefresh);
            Require(Field<TextBlock>("_homeTiming").Text == allLine,
                "V6 the completed stale scoped refresh for the previous account could not overwrite the current scope's last-success line");

            // Absence is truthful: with no successful retained run at all the line states it rather than
            // reusing the newest failure, the recovery run or any loaded page.
            using (var raw = new SqliteConnection("Data Source=" + database.DatabasePath))
            {
                raw.Open();
                // The command_request delete guard calls this function, which the product registers on
                // its own connection. Answer it on this isolated raw connection for exactly the three
                // fixture rows being removed, as the existing history fixtures do.
                raw.CreateFunction<string, long>("syncshark_run_retirement_allowed",
                    id => id is "run-scope-local-newest" or "run-scope-primary-older" or "run-scope-secondary-newer" ? 1L : 0L);
                using var delete = raw.CreateCommand();
                // Remove only this fixture's three unreferenced successful history rows. Keep the failed
                // and recovery rows, and every command that still owns pending operations.
                delete.CommandText = "DELETE FROM command_request WHERE command_request_id IN " +
                    "('run-scope-local-newest','run-scope-primary-older','run-scope-secondary-newer');";
                delete.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            Pump((Task)Call("RefreshWorkspaceScopeDataAsync")!);
            PumpUntilLine("Last successful sync: Not recorded",
                "V6 with no successful retained run the Home line is truthfully not recorded");
            // Drive actual current producers and retirement, then read the public history UI.
            // These are separate from the predecessor rows used for account/paging assertions.
            Pump(Task.Run(async () =>
            {
                var pair = pairs.Pairs[0];
                var admission = await database.CreateAcceptedCommandAdmissionAsync(
                    SyncAuthorityCommandReservation.Mint(), "run_pair", pair.Id, null, null,
                    JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }),
                    SyncAuthorityCommandPurpose.NormalEffect);
                var writer = await database.OpenRunActivityWriterAsync(admission);
                for (var index = 0; index < 3; index++)
                    await database.AppendRunActivityAsync([writer.Enqueue(new SyncActivityEvent("",
                        DateTimeOffset.UtcNow.AddDays(index == 2 ? 0 : -400),
                        [SyncActivityCategories.Files], "Success", "File", "Uploaded", "Window outcome",
                        pair.ConnectionId, pair.Id, admission.CommandRequestId,
                        RelativePath: "window-" + index + ".txt", EndpointContext: SyncEndpointContext.Capture(pair)))]);
                database.ActivityRetentionPolicyForTest = new(MaximumEvents: 1);
                await database.EnableActivityRetentionForTestAsync();
                await database.RetireOrdinaryActivityBatchAsync();
                // Simulate reopening after the process lost its final-drain acknowledgement.
                await database.CloseInterruptedActivityWritersAsync(default);
            }));
            Select(null);
            Call("OpenWorkspaceActivity", 0, null);
            Pump((Task)Call("LoadActivityPageAsync", true, null)!);
            var retainedNote = Field<TextBlock>("ActivityPageSummary").Text;
            Require(retainedNote.Contains("older events have expired", StringComparison.Ordinal) &&
                    retainedNote.Contains("exports include only retained events", StringComparison.Ordinal) &&
                    retainedNote.Contains("incomplete after an interrupted final log flush", StringComparison.Ordinal),
                "Activity visibly discloses actual expired rows, export limits and incomplete run history");
            Require(retainedNote.Contains("legacy evidence is also retained", StringComparison.Ordinal),
                "Activity distinguishes held predecessor evidence from the bounded recent window");
            Console.WriteLine("PASS real retained Activity/Pending account UI checks; no provider or installed-state effects");
            return 0;
        }
        finally
        {
            MainWindow.ActivityProjectionLoaderForTest = null;
            MainWindow.SummaryHistoryLoaderForTest = null;
            window.RequestProductExit(); MainWindow.SuppressLoadedStartupForTest = false; app.Shutdown();
        }
    }
}
