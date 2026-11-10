using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static bool IsMixedFixtureOriginalReviewedRun(SyncAuthorityRunHistory run)
    {
        using var json = System.Text.Json.JsonDocument.Parse(run.RequestJson);
        var value = json.RootElement;
        return value.TryGetProperty("action", out var action) && action.GetString() == "reviewed_first_sync" &&
            (!value.TryGetProperty("retryOfRunId", out var parent) || parent.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    // Only provider input and the consent dialog are supplied. The real UI controls,
    // shared host, planner, executor, database, retry decision and file bytes are exercised.
    private static int RunMixedFileRetryUiChecks(string root, string coreFixture, bool uncertain = false)
    {
        // This branch proves that uncertainty stops NEW admission. With the
        // production concurrent pump, independent files may already be admitted
        // and must drain safely; their success is not an unsafe subsequent retry.
        // Pin only this isolated input scenario to the real pump's serial scope.
        // The ordinary mixed-file GUI branch keeps production adaptive scheduling.
        using var serialUncertainty = uncertain
            ? (IDisposable)typeof(SyncPair).Assembly
                .GetType("Massdex.SyncShark.SyncAuthorityOperationScheduler", throwOnError: true)!
                .GetMethod("OverrideMaximumInFlightForTest", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [1])!
            : null;
        Directory.CreateDirectory(root);
        var local = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        foreach (var name in new[] { "a-good.txt", "b-locked.txt", "c-good.txt" })
            File.WriteAllText(Path.Combine(local, name), "GUI fixture " + name);
        var remote = CreateCoreTestRemote(coreFixture);
        InvokeRemote(remote.Type, remote.Instance, "Put", "download.txt", "GUI remote bytes");
        var refusal = remote.Type.GetProperty("UploadFailureForTest")!;
        refusal.SetValue(remote.Instance, (Func<string, Exception?>)(path => path == "b-locked.txt"
            ? uncertain ? new HttpRequestException("Response lost; effect is not known", null, HttpStatusCode.Conflict)
                : new DropboxApiException(HttpStatusCode.Conflict, "File locked by another editor") : null));
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        using var http = new HttpClient(new RejectRetryNetwork());
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:retry-ui", "retry-ui-root", "retry-ui-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            connections, new InspectionCredentialBoundary(), database, (_, _) => remote.Store);
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme)
        { Title = "Sync Shark — isolated mixed-file retry", Width = 1440, Height = 880,
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        BindAuthorityHost(window, host);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, flags)?.GetValue(window)
            ?? typeof(MainWindow).GetProperty(name, flags)?.GetValue(window)
            ?? throw new InvalidDataException("Missing fixture member: " + name));
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Check(bool value, string label)
        { if (!value) throw new InvalidDataException(label); Console.WriteLine("PASS " + label); }
        void Trace(string step) => Console.WriteLine($"TRACE {DateTimeOffset.UtcNow:O} {step}");
        void Await(Task task) => PumpTask(window, task);
        T Result<T>(Task<T> task) { Await(task); return task.GetAwaiter().GetResult(); }
        try
        {
            window.Show();
            Await(Task.Run(() => connections.SaveNewAsync(binding)));
            Await(Task.Run(() => host.LoadPairsAsync()));
            using var lease = PilotDirectoryLease.Open(local);
            var pair = SyncPair.FromPilot(new PilotSettings(local, lease.Identity.VolumeSerialNumber,
                lease.Identity.FileId, "/RetryUi", "id:retry-ui"), "Mixed file UI", binding.RootNamespaceId)
                with { ConnectionId = binding.ConnectionId, InitialReviewApproved = false };
            Await(Task.Run(async () =>
            {
                await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                    pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1, "manual_preserve_both",
                    "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                    RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
                await database.RetainPairDefinitionAsync(pair, select: true);
                await host.RefreshBootstrapInventoryAsync(pair.Id);
            }));
            window.LoadPairSetForTest(Result(Task.Run(() => host.LoadPairsAsync())));
            Call("OpenFirstSyncReview", pair.Id);
            Await(window.RefreshBootstrapReviewForTestAsync());
            var start = (Button)window.FindName("ApproveBootstrapButton");
            Check(start.IsEnabled, "The complete real first-sync inventory enables confirmation.");
            MainWindow.BootstrapApprovalConfirmationForTest = _ => true;
            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!start.IsEnabled, "The actual first-sync button acknowledges the click and prevents duplicate starts.");
            PumpUntil(window, () => !Field<bool>("_firstSyncApprovalInFlight") && !Field<bool>("_runActive"),
                "The actual mixed-file first sync did not finish.", TimeSpan.FromMinutes(1));
            if (uncertain)
            {
                Check(ReadRemoteCount(remote.Type, remote.Instance, "UploadCount") == 1 &&
                    ReadRemoteCount(remote.Type, remote.Instance, "DownloadCount") == 0,
                    "An ambiguous response stops further effects instead of pretending that it is a known-safe refusal.");
                var stopped = Result(Task.Run(() => database.LoadRunHistoryAsync(20)))
                    .First(IsMixedFixtureOriginalReviewedRun);
                var uncertainEligibilityGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.OperationRetryReadCheckpointForTest = () => uncertainEligibilityGate.Task;
                window.OpenOperationLog(stopped.Id);
                Await((Task)Call("LoadOperationLogAsync", true)!);
                PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The uncertain run did not load.");
                Check(!Field<Button>("_verifyRunOutcomes").IsEnabled && !Field<Button>("_retryFailed").IsEnabled &&
                    Field<Button>("_verifyRunOutcomes").ToolTip?.ToString()?.Contains("still being loaded") == true &&
                    Field<DataGrid>("_operationLogGrid").Items.Count > 0,
                    "The uncertain run's retained log is readable while verification eligibility is still loading.");
                window.OperationRetryReadCheckpointForTest = null;
                uncertainEligibilityGate.SetResult();
                PumpUntil(window, () => Field<bool>("HasCurrentOperationRetryPlan"),
                    "The uncertain run's independent retry eligibility did not settle.");
                var plan = Result(Task.Run(() => host.LoadRunRetryAsync(stopped.Id)));
                Check(plan.Items.Single(item => item.Path == "b-locked.txt") is { State: "Verify first", CanRetry: false } &&
                    plan.Items.Count(item => item.State == "Not attempted") == 2 &&
                    Field<Button>("_verifyRunOutcomes").IsEnabled && !Field<Button>("_retryFailed").IsEnabled,
                    "The GUI and retained retry plan require verification for the uncertain file and distinguish two unattempted files.");
                Check((Result(Task.Run(() => new SyncAuthorityDatabase(database.DatabasePath).LoadRunByIdAsync(stopped.Id))))!
                    .ResultJson == stopped.ResultJson, "The stopped uncertain outcome survives reopening unchanged.");
                Console.WriteLine("PASS actual GUI uncertainty guard; no blind retry or subsequent independent effect.");
                return 0;
            }
            Check(ReadRemoteCount(remote.Type, remote.Instance, "UploadCount") == 2 &&
                ReadRemoteCount(remote.Type, remote.Instance, "DownloadCount") == 1 &&
                File.ReadAllText(Path.Combine(local, "download.txt")) == "GUI remote bytes",
                "The real GUI sync completes two uploads and one download despite the isolated refusal.");
            var original = Result(Task.Run(() => database.LoadRunHistoryAsync(20)))
                .First(IsMixedFixtureOriginalReviewedRun);
            var originalBytes = original.ResultJson;
            Trace("Opening the original retained run log.");
            window.OpenOperationLog(original.Id);
            Trace("Requesting the retained log refresh.");
            Await((Task)Call("LoadOperationLogAsync", true)!);
            Trace("Waiting for the retained log refresh to finish.");
            PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "The failed run log remained loading.");
            Trace("Reading the failed file from the rendered log rows.");
            var grid = Field<DataGrid>("_operationLogGrid");
            var failure = grid.Items.Cast<OperationLogRow>().First(row => row.Target == "b-locked.txt" && row.Severity == "Error");
            Check(failure.Detail.Contains("409") && failure.Detail.Contains("locked"),
                "The visible GUI log names the failed file and its exact provider refusal.");
            var summary = (RunHistoryRow)Call("ProjectRun", original)!;
            Check(summary.StatusLabel == "Completed with Failures", "The same GUI run is labelled partial completion.");
            grid.SelectedItem = failure;
            window.UpdateLayout();
            IEnumerable<Button> Buttons(DependencyObject parent)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                {
                    if (child is Button button) yield return button;
                    foreach (var nested in Buttons(child)) yield return nested;
                }
            }
            var resolution = Buttons(Field<StackPanel>("_detailFields")).ToArray();
            var accountAction = resolution.Single(button => System.Windows.Automation.AutomationProperties.GetName(button)
                .StartsWith("Open account: ", StringComparison.Ordinal));
            Check(accountAction.IsEnabled && resolution.Any(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "Open pair repair and status"),
                "Selecting the actual failed event exposes account and pair repair beside its exact cause.");
            accountAction.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Field<string>("_selectedConnectionId") == binding.ConnectionId && Field<string>("_workspacePage") == "Connections",
                "The resolution action selects only the event's recorded account, without starting authorization.");
            window.OpenOperationLog(original.Id);
            Await((Task)Call("LoadOperationLogAsync", true)!);
            PumpUntil(window, () => !Field<bool>("_operationLogLoading"), "Returning from repair did not reload the original run.");
            grid.SelectedItem = grid.Items.Cast<OperationLogRow>().First(row => row.Target == "b-locked.txt" && row.Severity == "Error");
            var retry = Field<Button>("_retryFailed");
            PumpUntil(window, () => retry.IsEnabled, "Failed-only retry was not available on the actual run.");
            refusal.SetValue(remote.Instance, null); // The external file-specific cause is fixed.
            retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Field<bool>("_runActive"), "The actual Retry failed files control starts and shows its in-flight state.");
            PumpUntil(window, () => !Field<bool>("_runActive") && !Field<bool>("_operationLogLoading"),
                "The GUI failed-only retry did not finish.", TimeSpan.FromMinutes(1));
            Check(ReadRemoteCount(remote.Type, remote.Instance, "UploadCount") == 3 &&
                ReadRemoteCount(remote.Type, remote.Instance, "DownloadCount") == 1,
                "GUI retry copies only the repaired file; the three successful transfers are not repeated.");
            var reopened = new SyncAuthorityDatabase(database.DatabasePath);
            var historical = Result(Task.Run(() => reopened.LoadRunByIdAsync(original.Id)));
            var linked = Result(Task.Run(() => reopened.LoadRunRetriesAsync(original.Id)));
            Check(historical!.ResultJson == originalBytes && linked.Count == 1 && linked[0].CompletedUtc is not null,
                "Reopening storage retains the original failure unchanged and its completed linked retry.");
            Check(Field<System.Windows.Controls.TextBox>("_operationLogRun").Text == linked[0].Id,
                "The GUI follows its exact retry run, not a different session or the old failure.");
            Console.WriteLine("PASS actual GUI mixed-file sync and failed-only retry; real bytes and retained outcomes, isolated fixture only.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Mixed-file GUI assertion failed before cleanup: " + error);
            throw;
        }
        finally
        {
            Trace("Closing the isolated mixed-file window.");
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
            Trace("Isolated mixed-file window closed.");
        }
    }

    private sealed class RejectRetryNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This isolated GUI test has no live provider or credential authority.");
    }
}
