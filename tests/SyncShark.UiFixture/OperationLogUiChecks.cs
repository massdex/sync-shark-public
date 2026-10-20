using Massdex.SyncShark;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    // Consumes the actual retained database produced by FirstSyncEffectChecks,
    // including its successful files, exact provider refusal and linked retry.
    // No projected rows, run outcomes or log queries are substituted here.
    private static int RunOperationLogUiChecks(string root, string databasePath, bool inspect)
    {
        if (File.Exists(Path.Combine(root, "manual-interrupted-boundary.json")) ||
            File.Exists(Path.Combine(root, "manual-missing-envelope-proof.json")))
            return RunOriginalManualLogUiChecks(root, databasePath, inspect);
        Directory.CreateDirectory(root);
        var database = new SyncAuthorityDatabase(databasePath);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        using var http = new HttpClient(new InspectionNetworkBoundary());
        using var runtime = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(), database,
            (_, _) => throw new InvalidOperationException("The retained-log inspection has no provider-effect authority."));
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme)
        { Title = "Sync Shark — isolated retained log checks", Width = 1440, Height = 880,
            WindowStartupLocation = WindowStartupLocation.CenterScreen };
        if (!inspect)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }
        BindAuthorityHost(window, runtime);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Await(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(5);
            }
            if (!task.IsCompleted) throw new TimeoutException("The real operation-log UI did not settle.");
            task.GetAwaiter().GetResult();
        }
        void Reload()
        {
            Await((Task)Call("LoadOperationLogAsync", true)!);
            PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The exact log load remained active.");
            PumpUntil(window, () => Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null,
                "The separate retry-eligibility read did not settle.");
        }
        void Check(bool value, string reason)
        { if (!value) throw new InvalidDataException(reason); Console.WriteLine("PASS " + reason); }
        try
        {
            window.Show();
            var pairsTask = Task.Run(() => runtime.LoadPairsAsync()); Await(pairsTask);
            window.LoadPairSetForTest(pairsTask.Result);
            var runsTask = Task.Run(() => database.LoadRunHistoryAsync(100)); Await(runsTask);
            var original = runsTask.Result.First(row => row.ResultJson?.Contains("normal-denied.txt") == true &&
                !row.RequestJson.Contains("retryOfRunId"));
            var retry = runsTask.Result.Single(row => row.RequestJson.Contains("\"retryOfRunId\":\"" + original.Id + "\""));
            Field<ToggleButton>("_operationLogFollow").IsChecked = false;
            window.OpenOperationLog(original.Id); Reload();
            var grid = Field<DataGrid>("_operationLogGrid");
            foreach (var (name, purpose) in new[] { ("Refresh log", "pending activity records"),
                         ("Export complete log", "not just visible rows") })
            {
                var command = FindVisualChildren<Button>(window).Single(button => AutomationProperties.GetName(button) == name);
                Check(command.ToolTip is string explanation && explanation.Contains(purpose, StringComparison.Ordinal) &&
                      AutomationProperties.GetHelpText(command) == explanation,
                    "The actual " + name + " control explains its exact retained-log action.");
            }
            var admissionReadDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < admissionReadDeadline &&
                   grid.Items.Cast<OperationLogRow>().All(row => row.Entry.Outcome != "Preparing Command" ||
                       !row.Detail.Contains("PRAGMA quick_check", StringComparison.Ordinal)))
            {
                var before = grid.Items.Count;
                Await((Task)Call("LoadOperationLogAsync", false)!);
                if (grid.Items.Count == before) break;
            }
            Check(grid.Items.Cast<OperationLogRow>().Any(row =>
                    row.RunId == original.Id && row.Entry.Outcome == "Preparing Command" &&
                    row.Detail.Contains("PRAGMA quick_check", StringComparison.Ordinal)),
                "The actual normal run retains its early database admission work in the same navigable GUI log.");
            var follow = Field<ToggleButton>("_operationLogFollow");
            var retryReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.OperationRetryReadCheckpointForTest = () => retryReadGate.Task;
            window.OpenOperationLog(retry.Id);
            PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The new run's log page did not settle.");
            Check(grid.Items.Count > 0 && grid.Items.Cast<OperationLogRow>().All(row => row.RunId == retry.Id) &&
                    Field<CancellationTokenSource?>("_operationRetryReadCancellation") is not null &&
                    !Field<Button>("_retrySelected").IsEnabled &&
                    Field<TextBlock>("_operationLogNote").Text.Contains("the log is ready to read"),
                "Real retained rows render while eligibility is deliberately delayed, with disabled actions and honest feedback.");
            window.OpenOperationLog(original.Id);
            PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "Retargeted log did not settle.");
            window.OperationRetryReadCheckpointForTest = null;
            retryReadGate.SetResult();
            PumpUntil(window, () => Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null,
                "Retargeted eligibility did not finish.");
            Check(Field<SyncRunRetryPlan>("_operationRetryPlan").RunId == original.Id &&
                    grid.Items.Cast<OperationLogRow>().All(row => row.RunId == original.Id),
                "A delayed old eligibility response cannot replace the newly selected run's plan or rows.");
            follow.IsChecked = false;
            while (Field<System.Collections.ObjectModel.ObservableCollection<OperationLogRow>>("_operationLogRows")
                       .All(row => !row.Target.Contains("normal-denied.txt") || !row.Detail.Contains("locked")))
            {
                var before = grid.Items.Count;
                Await((Task)Call("LoadOperationLogAsync", false)!);
                if (grid.Items.Count == before) break;
            }
            window.UpdateLayout();
            var viewSwitch = window._activityViewSwitch!;
            Check(grid.ActualHeight > 300 && grid.HeadersVisibility == DataGridHeadersVisibility.None &&
                grid.Columns.Count == 4 && viewSwitch.Visibility == Visibility.Visible &&
                viewSwitch.PageKey == "operationlog" && viewSwitch.SimpleSegment.IsChecked == true,
                "The operation log defaults to the shared full-size Simple table with the visible global view switch.");
            // Use a real retained per-file stage classified as a pair event: that is
            // precisely the case where the shared feed's default title hid the file.
            var targetStage = grid.Items.Cast<OperationLogRow>().First(row =>
                row.Activity.IsAppEvent && row.Target.Contains("normal-good.txt", StringComparison.Ordinal));
            void CheckTargetTitle(string mode)
            {
                grid.ScrollIntoView(targetStage); window.UpdateLayout();
                var content = grid.Columns[0].GetCellContent(targetStage);
                var title = content is null ? null : FindVisualChildren<TextBlock>(content).FirstOrDefault(text =>
                    BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == nameof(ActivityRow.OperationTarget));
                Check(title?.Text == targetStage.Entry.RelativePath &&
                        title.ToolTip?.ToString() == targetStage.Entry.RelativePath,
                    mode + " renders the actual retained per-file stage target as the primary title, with the full path available.");
            }
            CheckTargetTitle("Simple");
            Check((targetStage.Activity with { Path = "", Outcome = "Committing inventory transaction" }).OperationTarget ==
                    "Committing inventory transaction" &&
                  (targetStage.Activity with { Path = "", Outcome = "" }).OperationTarget == "Run / background process" &&
                  targetStage.Activity.DisplayName == targetStage.Activity.PairName,
                "Background-only log records use their operation, without changing the shared Activity feed's classification or title.");
            viewSwitch.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => new UiPreferencesStore(instance.StateAuthority).Load()
                .PageViewModes?.GetValueOrDefault("operationlog") == UiPreferencesStore.DetailedView,
                "The real Detailed button did not persist the operation-log preference.");
            Check(grid.HeadersVisibility == DataGridHeadersVisibility.Column && grid.Columns.Count == 9 &&
                viewSwitch.DetailedSegment.IsChecked == true,
                "The same log switches to the shared Detailed table and persists the choice.");
            CheckTargetTitle("Detailed");
            var rows = grid.Items.Cast<OperationLogRow>().ToArray();
            Check(rows.Length > 0 && rows.All(row => row.RunId == original.Id), "Every displayed event belongs to the selected historical run.");
            var failed = rows.First(row => row.Target == "normal-denied.txt" && row.Detail.Contains("locked"));
            Check(failed.Detail.Contains("409") && failed.Severity == "Error", "The actual failed file, HTTP cause and error state are readable in the table.");
            grid.SelectedItem = failed;
            Await((Task)Call("OpenLogRowRunAsync")!);
            Check(Field<TabControl>("_activityTabs").SelectedIndex == 2 && Field<string>("_runSelectedId") == original.Id,
                "Selecting an event opens that exact retained run rather than the current session.");
            var runRow = (RunHistoryRow)Call("ProjectRun", original)!;
            Check(runRow.StatusLabel == "Completed with Failures", "The real mixed-success run is not labelled a total failure or plain success.");
            var linkedTask = Task.Run(() => database.LoadRunRetriesAsync(original.Id)); Await(linkedTask);
            Check(linkedTask.Result.Any(row => row.Id == retry.Id), "The original run exposes its exact linked retry.");
            window.OpenOperationLog(retry.Id); Reload();
            Check(grid.Items.Cast<OperationLogRow>().All(row => row.RunId == retry.Id), "Opening the retry never mixes in the original failed-run rows.");
            window.OpenOperationLog(original.Id); Reload();
            Field<System.Windows.Controls.TextBox>("_operationLogSearch").Text = "normal-denied.txt";
            Reload();
            Check(grid.Items.Count > 0 && grid.Items.Cast<OperationLogRow>().All(row =>
                    row.Target.Contains("normal-denied.txt") || row.Detail.Contains("normal-denied.txt")),
                "Search runs against the retained log and preserves the exact historical run scope.");
            Field<ComboBox>("_operationLogSeverity").SelectedItem = "Error";
            PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The real result filter did not settle.");
            Check(grid.Items.Count > 0 && grid.Items.Cast<OperationLogRow>().All(row => row.Severity == "Error"),
                "Changing the result filter itself retrieves exact failed events, without a test-only reload call.");
            var filteredRow = grid.Items.Cast<OperationLogRow>().First();
            grid.SelectedItem = filteredRow;
            var selectedRows = grid.SelectedItems.Cast<OperationLogRow>().ToArray();
            var source = grid.ItemsSource;
            var view = CollectionViewSource.GetDefaultView(source);
            view.SortDescriptions.Add(new SortDescription(nameof(OperationLogRow.Detail), ListSortDirection.Descending));
            var beforeSwitch = grid.Items.Cast<OperationLogRow>().Select(row => row.Entry.EventId).ToArray();
            var retryEnabled = Field<Button>("_retrySelected").IsEnabled;
            follow.IsChecked = true;
            viewSwitch.SimpleSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            PumpUntil(window, () => new UiPreferencesStore(instance.StateAuthority).Load()
                .PageViewModes?.GetValueOrDefault("operationlog") == UiPreferencesStore.SimpleView,
                "The real Simple button did not persist the operation-log preference.");
            Check(grid.HeadersVisibility == DataGridHeadersVisibility.None && grid.Columns.Count == 4 &&
                ReferenceEquals(source, grid.ItemsSource) &&
                selectedRows.SequenceEqual(grid.SelectedItems.Cast<OperationLogRow>()) &&
                beforeSwitch.SequenceEqual(grid.Items.Cast<OperationLogRow>().Select(row => row.Entry.EventId)) &&
                Field<System.Windows.Controls.TextBox>("_operationLogSearch").Text == "normal-denied.txt" &&
                Field<ComboBox>("_operationLogSeverity").SelectedItem?.ToString() == "Error" &&
                Field<System.Windows.Controls.TextBox>("_operationLogRun").Text == original.Id &&
                Field<Button>("_retrySelected").IsEnabled == retryEnabled && follow.IsChecked == true,
                "Simple preserves actual rows, historical run, filters, detailed-column sort, selection, retry eligibility and live follow.");
            viewSwitch.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            Check(grid.Columns.Count == 9 && ReferenceEquals(grid.SelectedItem, filteredRow) &&
                beforeSwitch.SequenceEqual(grid.Items.Cast<OperationLogRow>().Select(row => row.Entry.EventId)) &&
                follow.IsChecked == true,
                "Returning to Detailed preserves the same selected failure, sorted events and follow state without a reload.");
            Await((Task)Call("SetDetailedViewAsync", "operationlog", true)!);
            var stored = new UiPreferencesStore(instance.StateAuthority).Load();
            typeof(MainWindow).GetField("_workspacePreferences", flags)!.SetValue(window, stored);
            window.LoadViewModes();
            Check(window.IsDetailedView("operationlog") && grid.HeadersVisibility == DataGridHeadersVisibility.Column,
                "Reloading saved preferences restores Detailed independently of the other Activity tabs.");
            grid.RaiseEvent(new ScrollEventArgs(ScrollEventType.ThumbTrack, 0)
            { RoutedEvent = ScrollBar.ScrollEvent, Source = new ScrollBar { Orientation = Orientation.Vertical } });
            Check(follow.IsChecked == false, "A user's vertical scrollbar action pauses live follow; layout changes do not.");
            view.SortDescriptions.Clear();
            var resumeReason = Field<Button>("_resumeUnattempted").ToolTip;
            var verifyReason = Field<Button>("_verifyRunOutcomes").ToolTip;
            var eligibilityLoading = Field<CancellationTokenSource?>("_operationRetryReadCancellation") is not null;
            var eligibilityPlan = Field<SyncRunRetryPlan?>("_operationRetryPlan");
            Console.WriteLine($"RETRY ACTION STATE: resume={resumeReason}; verify={verifyReason}; " +
                $"loading={eligibilityLoading}; planRun={eligibilityPlan?.RunId}; planReason={eligibilityPlan?.UnavailableReason}");
            if (eligibilityLoading)
                Check(resumeReason?.ToString()?.Contains("still being loaded") == true &&
                      verifyReason?.ToString()?.Contains("still being loaded") == true &&
                      !Field<Button>("_resumeUnattempted").IsEnabled && !Field<Button>("_verifyRunOutcomes").IsEnabled,
                    "While the independent eligibility read is active, disabled actions explain that actual pending read.");
            PumpUntil(window, () => Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null,
                "The filtered historical run's separate eligibility read did not settle.");
            Check(Field<Button>("_resumeUnattempted").ToolTip?.ToString()?.Contains("no unattempted") == true &&
                  Field<Button>("_verifyRunOutcomes").ToolTip?.ToString()?.Contains("no uncertain") == true,
                "Disabled actions explain the current exact run, not stale instructions to select a run.");
            var linkedFailure = grid.Items.Cast<OperationLogRow>()
                .First(row => row.Entry.RelativePath == "normal-denied.txt");
            grid.SelectedItem = linkedFailure;
            PumpUntil(window, () => Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null &&
                    Field<Button>("_retryFailed").ToolTip?.ToString()?
                        .Contains("no failed files currently eligible", StringComparison.Ordinal) == true,
                "The selected linked-retry failure's disabled retry explanation did not settle.");
            var realPlan = Field<SyncRunRetryPlan?>("_operationRetryPlan")!;
            var linkedPlanItem = realPlan.Items.Single(item => item.Path == "normal-denied.txt");
            Check(linkedPlanItem.State == "Completed" && !linkedPlanItem.CanRetry &&
                  linkedPlanItem.Reason.Contains("Completed by linked retry " + retry.Id, StringComparison.Ordinal),
                "The actual retained plan records the exact linked retry that completed the selected original failure.");
            var selectedRetryTooltip = Field<Button>("_retrySelected").ToolTip?.ToString();
            Check(!Field<Button>("_retrySelected").IsEnabled &&
                  selectedRetryTooltip?.Contains("normal-denied.txt", StringComparison.Ordinal) == true &&
                  selectedRetryTooltip?.Contains("Completed by linked retry " + retry.Id, StringComparison.Ordinal) == true &&
                  selectedRetryTooltip?.Contains("never replayed", StringComparison.Ordinal) == true &&
                  selectedRetryTooltip?.Contains("conflict", StringComparison.OrdinalIgnoreCase) != true &&
                  selectedRetryTooltip?.Contains("uncertain", StringComparison.OrdinalIgnoreCase) != true,
                "Disabled Retry Selected explains the selected file's real linked-retry completion and names no absent state class.");
            var failedTooltip = Field<Button>("_retryFailed").ToolTip?.ToString();
            Check(!Field<Button>("_retryFailed").IsEnabled &&
                  failedTooltip?.Contains("no failed files currently eligible", StringComparison.Ordinal) == true &&
                  failedTooltip?.Contains("Completed by linked retry " + retry.Id, StringComparison.Ordinal) == true &&
                  failedTooltip?.Contains("Conflicts and uncertain outcomes", StringComparison.Ordinal) == false,
                "Disabled Retry Failed reports the actual completed-by-linked-retry outcome instead of suggesting unresolved conflicts or uncertain work.");
            typeof(MainWindow).GetField("_operationRetryPlan", flags)!.SetValue(window, realPlan with { Items = [] });
            Call("UpdateOperationRetryActions");
            var emptyTooltip = Field<Button>("_retryFailed").ToolTip?.ToString();
            Check(!Field<Button>("_retryFailed").IsEnabled &&
                  emptyTooltip?.Contains("no approved file choices to retry", StringComparison.Ordinal) == true &&
                  emptyTooltip?.Contains("already completed", StringComparison.Ordinal) == false,
                "An empty retry plan is never reported as if every approved file had completed.");
            typeof(MainWindow).GetField("_operationRetryPlan", flags)!.SetValue(window, realPlan with
            {
                Items = [new SyncRunRetryItem("a.txt", "Completed",
                             "Completed by linked retry " + retry.Id + "; the original failure history is unchanged.", false),
                         new SyncRunRetryItem("b.txt", "Conflict", "conflicting generations", false),
                         new SyncRunRetryItem("c.txt", "Verify first", "uncertain retained effect", false)]
            });
            Call("UpdateOperationRetryActions");
            var mixedTooltip = Field<Button>("_retryFailed").ToolTip?.ToString();
            Check(!Field<Button>("_retryFailed").IsEnabled &&
                  mixedTooltip?.Contains("1 already completed", StringComparison.Ordinal) == true &&
                  mixedTooltip?.Contains("1 conflicting file(s) must be resolved first", StringComparison.Ordinal) == true &&
                  mixedTooltip?.Contains("1 uncertain outcome(s) must be verified first", StringComparison.Ordinal) == true &&
                  mixedTooltip?.Contains("Completed by linked retry " + retry.Id, StringComparison.Ordinal) == true,
                "A genuinely mixed plan states every real retained outcome and reason, including the linked completion, not only the first one.");
            typeof(MainWindow).GetField("_operationRetryPlan", flags)!.SetValue(window, realPlan with
            {
                Items = [new SyncRunRetryItem("a.txt", "Completed", "Already completed; never replayed by retry.", false)]
            });
            Call("UpdateOperationRetryActions");
            var completedOnlyTooltip = Field<Button>("_retryFailed").ToolTip?.ToString();
            Check(!Field<Button>("_retryFailed").IsEnabled &&
                  completedOnlyTooltip?.Contains("already completed", StringComparison.Ordinal) == true &&
                  completedOnlyTooltip?.Contains("conflicting", StringComparison.OrdinalIgnoreCase) != true &&
                  completedOnlyTooltip?.Contains("uncertain", StringComparison.OrdinalIgnoreCase) != true,
                "A wholly completed plan never claims a conflict or an uncertain outcome that is not present.");
            typeof(MainWindow).GetField("_operationRetryPlan", flags)!.SetValue(window, realPlan);
            Call("UpdateOperationRetryActions");
            Check(ReferenceEquals(Field<SyncRunRetryPlan?>("_operationRetryPlan"), realPlan),
                "The actual retained retry plan is restored after the empty, mixed and completed controls.");
            grid.SelectedItem = null;
            var runInput = Field<System.Windows.Controls.TextBox>("_operationLogRun");
            var runLink = Field<Button>("_operationLogRunLink");
            runInput.Clear();
            Check(!runLink.IsEnabled && runLink.ToolTip?.ToString()?.Contains("enter an exact retained run ID") == true &&
                    AutomationProperties.GetHelpText(runLink).Contains("enter an exact retained run ID"),
                "An empty run link exposes the same current tooltip reason through accessibility.");
            runInput.Text = original.Id;
            Check(runLink.IsEnabled && runLink.ToolTip?.ToString()?.Contains(original.Id) == true &&
                    AutomationProperties.GetHelpText(runLink).Contains(original.Id),
                "Typing a retained run immediately enables its link and replaces the stale disabled help.");
            Await((Task)Call("OpenLogRowRunAsync")!);
            Check(Field<string>("_runSelectedId") == original.Id,
                "The run link remains functional after a filter clears row selection.");
            window.OpenOperationLog(original.Id); Reload();
            follow.IsChecked = false;
            Call("RefreshFollowingOperationLog");
            Check(follow.IsChecked == false && Field<System.Windows.Controls.TextBox>("_operationLogRun").Text == original.Id,
                "A historical run with follow disabled is not silently retargeted or scrolled by live refresh.");
            Field<System.Windows.Controls.TextBox>("_operationLogSearch").Clear(); Reload();
            Field<ComboBox>("_operationLogSeverity").SelectedIndex = 0; Reload();
            var export = Path.Combine(root, "operation-log.jsonl");
            File.WriteAllText(export, "existing export is preserved");
            var previewed = false;
            window.OperationLogExportConfirmForTest = preview =>
            { previewed = preview.Contains("file contents are never included") && preview.Contains("Preview"); return false; };
            window.OperationLogExportPathForTest = () => export;
            Await((Task)Call("ExportOperationLogAsync")!);
            Check(previewed && File.ReadAllText(export) == "existing export is preserved",
                "Export previews the actual redacted data and cancelling preview changes no destination file.");
            window.OperationLogExportConfirmForTest = _ => true;
            window.OperationLogExportProgressForTest = _ => Field<CancellationTokenSource>("_logExportCancellation").Cancel();
            Await((Task)Call("ExportOperationLogAsync")!);
            Check(File.ReadAllText(export) == "existing export is preserved" && !Directory.EnumerateFiles(root, "*.partial-*").Any(),
                "Cancelling a real export preserves the existing destination and removes only its own temporary file.");
            window.OperationLogExportProgressForTest = null;
            Await((Task)Call("ExportOperationLogAsync")!);
            var exported = File.ReadLines(export).Select(line => System.Text.Json.JsonSerializer.Deserialize<SyncAuthorityActivityProjection>(line)!).ToArray();
            Check(exported.Length >= grid.Items.Count && exported.All(row => row.RunId == original.Id) &&
                    exported.Any(row => row.RelativePath == "normal-denied.txt" && row.Severity == "Error"),
                "The complete export contains the selected real run, including its exact failed file, not another run or only the visible selection.");
            // Isolated pagination input: never append test events to the retained effect evidence.
            var pagination = new SyncAuthorityDatabase(Path.Combine(root, "pagination.db"));
            Await(pagination.InitializeAsync("log-pagination", "log-pagination", "fixture", "{}"));
            SyncActivityEvent PageEvent(int index) => new("page-event-" + index, DateTimeOffset.UtcNow,
                [SyncActivityCategories.Diagnostics], "Information", "Application", "Checking fixture",
                "Pagination fixture event " + index);
            Await(Task.Run(() => LegacyHistoryFixture.SeedAsync(pagination, 
                Enumerable.Range(0, 500).Select(PageEvent).ToArray())));
            using (var paginationRuntime = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                       new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                       new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(), pagination))
            {
                typeof(MainWindow).GetField("_authorityHost", flags)!.SetValue(window, paginationRuntime);
                window.OpenOperationLog(null); Reload(); follow.IsChecked = false;
                var more = Field<Button>("_operationLogLoadMore");
                Check(grid.Items.Count == 500 && !more.IsEnabled &&
                        more.ToolTip?.ToString()?.Contains("All matching events") == true &&
                        AutomationProperties.GetHelpText(more).Contains("All matching events"),
                    "An exactly full final page disables Load More and explains that all current events are loaded.");
                Await(LegacyHistoryFixture.SeedAsync(pagination, PageEvent(500)));
                Reload();
                Check(grid.Items.Count == 500 && more.IsEnabled &&
                        grid.Items.Cast<OperationLogRow>().Last().Entry.EventId == "history:page-event-500" &&
                        grid.Items.Cast<OperationLogRow>().All(row => row.Entry.EventId != "history:page-event-0"),
                    "A new log opens the latest full page immediately and exposes earlier retained history separately.");
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(!more.IsEnabled && more.ToolTip?.ToString()?.Contains("being loaded") == true,
                    "Load Earlier immediately reports its real in-flight state and rejects repeated activation.");
                PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The next real page did not settle.");
                Check(grid.Items.Count == 501 && !more.IsEnabled &&
                        more.ToolTip?.ToString()?.Contains("All matching events") == true &&
                        grid.Items.Cast<OperationLogRow>().Select(row => row.Entry.EventId).Distinct().Count() == 501,
                    "The earlier page retains each event once and restores truthful terminal tooltip feedback.");
                Await(LegacyHistoryFixture.SeedAsync(pagination, PageEvent(501)));
                follow.IsChecked = true;
                Call("RefreshFollowingOperationLog");
                PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "Live follow did not settle.");
                Check(grid.Items.Count == 500 && more.IsEnabled &&
                        grid.Items.Cast<OperationLogRow>().Last().Entry.EventId == "history:page-event-501" &&
                        grid.Items.Cast<OperationLogRow>().First().Entry.EventId == "history:page-event-2",
                    "Follow Live shows the bounded current tail after a new event, while earlier records remain available.");
                Await(LegacyHistoryFixture.SeedAsync(pagination, Enumerable.Range(502, 1100).Select(PageEvent).ToArray()));
                Call("RefreshFollowingOperationLog");
                PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The burst live-tail refresh did not settle.");
                Check(grid.Items.Count == 500 && more.IsEnabled &&
                        grid.Items.Cast<OperationLogRow>().Last().Entry.EventId == "history:page-event-1601" &&
                        grid.Items.Cast<OperationLogRow>().First().Entry.EventId == "history:page-event-1102" &&
                        Field<TextBlock>("_operationLogNote").Text.Contains("Earlier events remain retained"),
                    "A burst larger than two pages jumps directly to the actual newest operation, never slowly replays old progress as live.");
                follow.IsChecked = false;
                var selectedTail = grid.Items.Cast<OperationLogRow>().Last();
                grid.SelectedItem = selectedTail;
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "Earlier live-tail history did not settle.");
                Check(grid.Items.Count == 1000 && ReferenceEquals(grid.SelectedItem, selectedTail) &&
                        grid.Items.Cast<OperationLogRow>().Select(row => row.Entry.EventId).Distinct().Count() == 1000 &&
                        grid.Items.Cast<OperationLogRow>().First().Entry.EventId == "history:page-event-602" && follow.IsChecked == false,
                    "Loading earlier records pauses following, preserves the selected current operation and adds the preceding page without duplicates.");
                Await(LegacyHistoryFixture.SeedAsync(pagination, PageEvent(1602)));
                follow.IsChecked = true;
                follow.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The real Follow Live action did not settle.");
                Check(grid.Items.Count == 500 && grid.Items.Cast<OperationLogRow>().Last().Entry.EventId == "history:page-event-1602",
                    "Turning Follow Live back on immediately returns to the newest retained operation instead of continuing old pagination.");
                var tailExport = Path.Combine(root, "live-tail-complete-log.jsonl");
                window.OperationLogExportPathForTest = () => tailExport;
                Await((Task)Call("ExportOperationLogAsync")!);
                var fullTail = File.ReadLines(tailExport)
                    .Select(line => System.Text.Json.JsonSerializer.Deserialize<SyncAuthorityActivityProjection>(line)!).ToArray();
                Check(fullTail.Length == 1603 && fullTail.Select(row => row.EventId).Distinct().Count() == 1603 &&
                        fullTail.First().EventId == "history:page-event-0" && fullTail.Last().EventId == "history:page-event-1602",
                    "Complete export still includes every retained event in order, not only the bounded live window.");
                follow.IsChecked = false;
                foreach (var filtered in new[] { false, true })
                {
                    Await(pagination.RegisterPairAsync("scan-pair", 1));
                    var accepted = pagination.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "scan-pair", null, null,
                        "{\"pairIds\":[\"scan-pair\"],\"trigger\":\"First sync scan\"}", SyncAuthorityCommandPurpose.NormalEffect);
                    Await(accepted);
                    var scanRun = accepted.Result.CommandRequestId;
                    Await(pagination.TransitionCommandRequestAsync(scanRun, "executing"));
                    window.OpenOperationLog(scanRun);
                    Field<TextBox>("_operationLogSearch").Text = filtered ? "no-event-matches-this-filter" : "";
                    Reload();
                    Check(Field<TextBlock>("_operationLogNote").Text.Contains("still active"),
                        "A real retained active scan explains why retry is not yet available.");
                    var sourceBeforeCompletion = grid.ItemsSource;
                    Await(pagination.TransitionCommandRequestAsync(scanRun, "completed", "{}"));
                    var terminalEligibilityGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    window.OperationRetryReadCheckpointForTest = () => terminalEligibilityGate.Task;
                    follow.IsChecked = true;
                    Call("RefreshFollowingOperationLog");
                    PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The completed live scan did not settle.");
                    Check(ReferenceEquals(sourceBeforeCompletion, grid.ItemsSource) &&
                            Field<CancellationTokenSource?>("_operationRetryReadCancellation") is not null &&
                            Field<TextBlock>("_operationLogNote").Text.Contains("Checking retry eligibility") &&
                            !Field<Button>("_retryFailed").IsEnabled,
                        "Live follow keeps the rendered rows while terminal eligibility is pending, including an unmatched filter.");
                    window.OperationRetryReadCheckpointForTest = null;
                    terminalEligibilityGate.SetResult();
                    PumpUntil(window, () => Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null,
                        "The completed live scan's independent eligibility read did not settle.");
                    Check(ReferenceEquals(sourceBeforeCompletion, grid.ItemsSource) &&
                            Field<TextBlock>("_operationLogNote").Text.Contains("only scanned") &&
                            !Field<TextBlock>("_operationLogNote").Text.Contains("still active") &&
                            !Field<Button>("_retryFailed").IsEnabled &&
                            Field<Button>("_retryFailed").ToolTip?.ToString()?.Contains("only scanned") == true,
                        "Live follow refreshes the actual terminal retry state without a reset, including a filter with no matching events.");
                    if (filtered) Check(grid.Items.Count == 0, "The terminal refresh preserves the unmatched search filter.");
                    else
                    {
                        var completed = grid.Items.Cast<OperationLogRow>().Single(row => row.Entry.Outcome == "run pair completed");
                        Check(completed.Action == "Folder pair command completed" &&
                                completed.Activity.OperationTarget == completed.Action &&
                                completed.Activity.EventText.StartsWith(completed.Action, StringComparison.Ordinal),
                            "Raw command identities retain readable titles and actions without claiming that a scan transferred files.");
                    }
                    follow.IsChecked = false;
                }
            }
            typeof(MainWindow).GetField("_authorityHost", flags)!.SetValue(window, runtime);
            window.OpenOperationLog(original.Id); Reload(); follow.IsChecked = false;
            Console.WriteLine("PASS retained operation-log GUI checks; actual prior fixture effects and history consumed, no provider or production state.");
            if (inspect)
            {
                window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) app.Shutdown(); };
                app.Run();
            }
            return 0;
        }
        finally
        {
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    // This branch consumes the original native interruption, not a copied database,
    // newly admitted restore or fabricated log. The normal mixed/linked suite above
    // remains separate and is still executed by the full UI batch.
    private static int RunOriginalManualLogUiChecks(string root, string databasePath, bool inspect)
    {
        const string originalCommand = "command-7d6eb12fadfe482ebc845373ffacd1eb";
        const string originalPair = "0bfb3d6b2d0844d48fde3c028bea8161";
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build", "home-activity-tray", "ui-integration",
            "root617-batch19-manual-interruption-fixture", "M4-manual-missing-envelope");
        void Check(bool value, string reason)
        { if (!value) throw new InvalidDataException(reason); Console.WriteLine("PASS " + reason); }
        Check(Path.GetFullPath(root).Equals(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
              Path.GetFullPath(databasePath).Equals(Path.Combine(expectedRoot, "authority", "syncshark.db"),
                  StringComparison.OrdinalIgnoreCase) && File.Exists(databasePath),
            "Manual GUI input is the exact original retained native case and authority database.");
        using var boundaryDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manual-interrupted-boundary.json")));
        using var proofDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manual-missing-envelope-proof.json")));
        var boundary = boundaryDocument.RootElement;
        var checkpoint = boundary.GetProperty("Checkpoint");
        var proof = proofDocument.RootElement;
        Check(boundary.GetProperty("Root").GetString() == root &&
              boundary.GetProperty("DatabasePath").GetString() == databasePath &&
              boundary.GetProperty("PairId").GetString() == originalPair &&
              boundary.GetProperty("CommandId").GetString() == originalCommand &&
              boundary.GetProperty("Phase").GetString() == "after-promotion" &&
              checkpoint.GetProperty("Phase").GetString() == "promotion_pending" &&
              proof.GetProperty("boundary").GetProperty("CommandId").GetString() == originalCommand,
            "Manual GUI input preserves the original command, pair and real promotion-pending checkpoint.");
        var envelope = boundary.GetProperty("SidecarPath").GetString()!;
        var preserved = proof.GetProperty("retained").GetProperty("Path").GetString()!;
        Check(!File.Exists(envelope) && preserved == Path.Combine(root, "preserved-original-access-envelope.bin") &&
              File.Exists(preserved), "Original envelope remains absent and its exact encrypted recovery input is retained.");
        string Dacl(string path) => new FileInfo(path).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string Stamp(string path)
        {
            Check(Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Manual original file read remains inside its exact owned allocation.");
            string hash; long length; PilotDirectoryIdentity identity;
            using (var file = SyncLocalFileAuthority.OpenExistingExclusive(path))
            {
                identity = file.VerifySingleLink(); length = file.Stream.Length;
                hash = Convert.ToHexString(SHA256.HashData(file.Stream));
                Check(file.VerifySingleLink() == identity && file.Stream.Length == length,
                    "Manual retained bytes and native identity are read on the same exclusive handle.");
            }
            return JsonSerializer.Serialize(new { identity, length, hash, dacl = Dacl(path) });
        }
        var sourcePath = checkpoint.GetProperty("SourceArchivePath").GetString()!;
        var preimagePath = boundary.GetProperty("Retained").GetProperty("Displaced").GetProperty("Path").GetString()!;
        var target = checkpoint.GetProperty("TargetPath").GetString()!;
        var sourceBefore = Stamp(sourcePath); var preimageBefore = Stamp(preimagePath);
        var envelopeBefore = Stamp(preserved); var targetDaclBefore = Dacl(target);
        Check(targetDaclBefore == proof.GetProperty("dacl").GetString(),
            "The original isolated target access state is unchanged before GUI verification.");
        string Counts()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM command_request),(SELECT count(*) FROM operation)," +
                "(SELECT count(*) FROM operation_attempt),(SELECT count(*) FROM recovery_artifact)," +
                "(SELECT count(*) FROM synchronization_point),(SELECT count(*) FROM effect_receipt);";
            using var reader = command.ExecuteReader(); reader.Read();
            return string.Join(',', Enumerable.Range(0, 6).Select(reader.GetInt64));
        }
        Check(Counts() == "4,2,3,2,3,3", "Original manual authority effect counts match the actual native producer.");
        var database = new SyncAuthorityDatabase(databasePath);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority)); theme.Initialize();
        using var transport = new ManualLogDenyTransport(); using var http = new HttpClient(transport);
        var providerCalls = 0;
        using var runtime = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(), database,
            (_, _) => { providerCalls++; throw new InvalidOperationException("Manual log consumer cannot prepare a provider."); });
        MainWindow.SuppressLoadedStartupForTest = true;
        var previousBrowserFactory = MainWindow.PairFolderBrowserFactoryForTest;
        // The retained-pair editor queues browser setup when its rows are selected.
        // That unrelated input must not acquire a live browser inside this local,
        // verify-only consumer. The real Host verification and original DB remain intact.
        MainWindow.PairFolderBrowserFactoryForTest = _ => Task.FromException<IDropboxFolderBrowserService>(
            new InvalidOperationException("Folder browsing is outside the original manual verification fixture."));
        var window = new MainWindow(instance.StateAuthority, theme, http, runtime)
            { Width = 1440, Height = 880, ShowActivated = inspect, ShowInTaskbar = inspect,
              WindowStartupLocation = WindowStartupLocation.Manual, Left = inspect ? 40 : -32000, Top = inspect ? 40 : -32000 };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Read<T>(Task<T> task)
        { PumpUntil(window, () => task.IsCompleted, "Original manual GUI authority read did not settle."); return task.GetAwaiter().GetResult(); }
        void Reload()
        {
            var load = (Task)Call("LoadOperationLogAsync", true)!;
            PumpUntil(window, () => load.IsCompleted, "Original manual log did not settle."); load.GetAwaiter().GetResult();
            PumpUntil(window, () => !Field<bool>("_operationLogLoading") &&
                Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null, "Original manual log eligibility did not settle.");
        }
        string RetainedCheckpoint(SyncManualLocalRestoreCommand command)
        { using var json = JsonDocument.Parse(command.ResultJson!); return json.RootElement.GetProperty("manualLocalRestore").GetRawText(); }
        try
        {
            window.Show();
            var pairs = Read(runtime.LoadPairsAsync()); window.LoadPairSetForTest(pairs);
            Check(pairs.Pairs.Single().Id == originalPair && pairs.Pairs.Single().LocalRoot == checkpoint.GetProperty("RootPath").GetString(),
                "The GUI uses the original retained pair without replacing or rebinding its roots.");
            var commandBefore = Read(database.LoadManualLocalRestoreCommandAsync(originalCommand))!;
            var checkpointBefore = RetainedCheckpoint(commandBefore);
            var artifactsBefore = Read(database.LoadRecoveryArtifactsAsync()).ToArray();
            var eventsBefore = Read(database.LoadActivityProjectionPageAsync(500,
                query: new SyncActivityQuery(RunId: originalCommand, DetailedLog: true))).Select(row => row.EventId).ToHashSet();
            window.OpenOperationLog(originalCommand); Reload();
            Field<ToggleButton>("_operationLogFollow").IsChecked = false;
            var grid = Field<DataGrid>("_operationLogGrid");
            var plan = Field<SyncRunRetryPlan>("_operationRetryPlan");
            var verify = Field<Button>("_verifyRunOutcomes");
            Check(grid.Items.Count > 0 && grid.Items.Cast<OperationLogRow>().All(row => row.RunId == originalCommand) &&
                  plan.RunId == originalCommand && plan.PairId == originalPair && plan.Items.Count == 1 &&
                  plan.Items.Single().Path == "destination/manual-interrupted.txt" &&
                  plan.Items.Single().State == "Verify first" && !plan.Items.Single().CanRetry && verify.IsEnabled &&
                  !Field<Button>("_retrySelected").IsEnabled && !Field<Button>("_retryFailed").IsEnabled &&
                  !Field<Button>("_resumeUnattempted").IsEnabled,
                "Original manual uncertainty is readable and verify-only; no replay action is enabled.");
            var viewSwitch = window._activityViewSwitch!;
            viewSwitch.SimpleSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.UpdateLayout();
            Check(grid.ActualHeight > 300 && grid.Columns.Count == 4 && grid.HeadersVisibility == DataGridHeadersVisibility.None,
                "Original manual events use the shared full-size Simple operation table.");
            viewSwitch.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.UpdateLayout();
            Check(grid.Columns.Count == 9 && grid.HeadersVisibility == DataGridHeadersVisibility.Column &&
                  grid.Items.Cast<OperationLogRow>().All(row => row.RunId == originalCommand),
                "Original manual events switch to Detailed without changing command scope.");
            grid.SelectedItem = grid.Items[0];
            var openRun = (Task)Call("OpenLogRowRunAsync")!;
            PumpUntil(window, () => openRun.IsCompleted, "Original manual run link did not settle."); openRun.GetAwaiter().GetResult();
            var run = Read(database.LoadRunHistoryAsync(100)).Single(row => row.Id == originalCommand);
            Check(Field<string>("_runSelectedId") == originalCommand &&
                  ((RunHistoryRow)Call("ProjectRun", run)!).StatusLabel == "Needs Attention",
                "The actual event link opens the original blocked manual run, not a fabricated successful pair run.");
            window.OpenOperationLog(originalCommand); Reload();
            var transportBeforeVerify = transport.Calls;
            verify.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(AutomationProperties.GetItemStatus(verify) == "In progress",
                "The real Verify uncertain outcomes click immediately exposes its running feedback.");
            PumpUntil(window, () => !window.RunActiveForTest && AutomationProperties.GetItemStatus(verify) != "In progress" &&
                !Field<bool>("_operationLogLoading") && Field<CancellationTokenSource?>("_operationRetryReadCancellation") is null,
                "The actual manual GUI verification did not finish.");
            var afterEvents = Read(database.LoadActivityProjectionPageAsync(500,
                query: new SyncActivityQuery(RunId: originalCommand, DetailedLog: true)));
            var fresh = afterEvents.Where(row => !eventsBefore.Contains(row.EventId)).ToArray();
            Check(fresh.Any(row => row.RunId == originalCommand && row.PairId == originalPair &&
                row.RelativePath == "destination/manual-interrupted.txt" && row.Severity == "Warning" &&
                row.Outcome == "Manual restore access verification" &&
                row.Reason.Contains("envelope is absent", StringComparison.OrdinalIgnoreCase) &&
                row.Reason.Contains("does not prove restore completion", StringComparison.OrdinalIgnoreCase)),
                "The actual GUI click appends a new original-command warning with the exact missing-envelope reason.");
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "manual GUI terminal feedback",
                feedback = Field<TextBlock>("_operationFeedback").Text,
                actionStatus = AutomationProperties.GetItemStatus(verify),
                plan = Field<SyncRunRetryPlan>("_operationRetryPlan") }));
            Check(Field<TextBlock>("_operationFeedback").Text.Contains("Verification finished with unresolved outcomes") &&
                  Field<SyncRunRetryPlan>("_operationRetryPlan").Items.All(item => !item.CanRetry),
                "The GUI reports unresolved outcomes, not success inferred from zero ordinary failures.");
            var commandAfter = Read(database.LoadManualLocalRestoreCommandAsync(originalCommand))!;
            var invariants = new { blocked = commandAfter.Status == "blocked",
                request = commandAfter.RequestJson == commandBefore.RequestJson,
                checkpoint = RetainedCheckpoint(commandAfter) == checkpointBefore,
                counts = Counts(), artifacts = artifactsBefore.SequenceEqual(Read(database.LoadRecoveryArtifactsAsync())),
                source = Stamp(sourcePath) == sourceBefore, preimage = Stamp(preimagePath) == preimageBefore,
                envelope = Stamp(preserved) == envelopeBefore, envelopeAbsent = !File.Exists(envelope),
                targetDacl = Dacl(target) == targetDaclBefore, providerCalls, transportCalls = transport.Calls,
                verificationTransportCalls = transport.Calls - transportBeforeVerify };
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "manual GUI original authority invariants", invariants }));
            Check(invariants.blocked && invariants.request && invariants.checkpoint && invariants.counts == "4,2,3,2,3,3" &&
                  invariants.artifacts && invariants.source && invariants.preimage && invariants.envelope &&
                  invariants.envelopeAbsent && invariants.targetDacl && invariants.providerCalls == 0 &&
                  invariants.transportCalls == 0 && invariants.verificationTransportCalls == 0,
                "GUI verification preserves original checkpoint, source, preimage, encrypted envelope and isolated target; zero new commands/effects/receipts/provider calls.");
            Console.WriteLine("PASS original manual missing-envelope GUI consumer; normal mixed/linked table suite is a separate invocation.");
            if (inspect) { window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) app.Shutdown(); }; app.Run(); }
            return 0;
        }
        finally
        {
            window.RequestProductExit(); MainWindow.SuppressLoadedStartupForTest = false;
            MainWindow.PairFolderBrowserFactoryForTest = previousBrowserFactory;
            app.Shutdown();
        }
    }

    private sealed class ManualLogDenyTransport : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "denied manual GUI transport attempt",
                origin = request.RequestUri?.GetLeftPart(UriPartial.Authority), path = request.RequestUri?.AbsolutePath,
                callers = Environment.StackTrace }));
            throw new InvalidOperationException("Original manual log consumer has no transport authority.");
        }
    }
}
