using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class StartupLoadingChecks
{
    internal static int Run(string root, bool inspect = false)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var originalErrorWriter = Console.Error;
        using var startupTrace = new StringWriter();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.StartupLocalOnlyForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme)
        { Width = 1100, Height = 780, ShowActivated = inspect, ShowInTaskbar = inspect };
        if (!inspect)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
        }
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidDataException(label); Console.WriteLine("PASS " + label); }
        var cliRuntime = new CliAppRuntime(window.AuthorityHost, instance.StateAuthority,
            () => false, () => false, () => { }, () => Task.CompletedTask,
            appAction: (command, token) => window.ExecuteCliAppActionAsync(command, token));
        SyncCliAppStatusData RuntimeStatus()
        {
            var command = SyncCliRegistry.Parse(["--cli", "app", "status", "--json"]);
            Check(!command.Yes && command.PairId is null &&
                  SyncCliRegistry.Rows.Single(item => item.Command == command.Command) is
                      { NeedsApplication: true, Mutating: false, RequiresYes: false },
                "The real registry exposes startup inspection without pair selection, approval or mutation.");
            return (SyncCliAppStatusData)SyncCliRegistry.DispatchAsync(cliRuntime, command,
                TextWriter.Null, CancellationToken.None).GetAwaiter().GetResult();
        }
        void Drain()
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        void Until(Func<bool> condition, string label)
        {
            var end = DateTime.UtcNow.AddSeconds(40);
            while (!condition() && DateTime.UtcNow < end) { Drain(); Thread.Sleep(5); }
            Check(condition(), label);
        }
        void Inspect(string phase)
        {
            if (!inspect) return;
            Console.WriteLine("NATIVE INSPECTION: " + phase + " (30 seconds; isolated local state only)");
            var until = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < until) { Drain(); Thread.Sleep(20); }
        }
        try
        {
            Console.SetError(startupTrace);
            Check(Field<Border>("StartupLoadingHost").Visibility == Visibility.Visible &&
                  Field<ProgressBar>("StartupLoadingProgress").IsIndeterminate &&
                  Field<TextBlock>("StartupLoadingText").Text.Contains("Loading saved"),
                "Before the first render, loading text and progress replace empty page shells.");
            window.Show(); Drain();
            MainWindow.SuppressLoadedStartupForTest = false;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = 0;
            MainWindow.StartupCheckpointForTest = _ => { starts++; return gate.Task; };
            var loaded = typeof(MainWindow).GetMethod("MainWindow_Loaded", flags)!;
            loaded.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Until(() => starts == 1, "The actual startup handler begins one local-state load.");
            var loadingStatus = RuntimeStatus();
            Check(loadingStatus.State == "loading" && loadingStatus.Phase == "saved-accounts" &&
                  !loadingStatus.InitializationComplete && loadingStatus.CompletedUtc is null &&
                  loadingStatus.ProcessId == Environment.ProcessId && loadingStatus.LastProgressUtc >= loadingStatus.StartedUtc,
                "Production CLI dispatch reports the actual awaited startup phase, not process or database-table readiness.");
            var repeatedStatus = RuntimeStatus();
            Check(repeatedStatus.LastProgressUtc == loadingStatus.LastProgressUtc &&
                  repeatedStatus.StageElapsedSeconds >= loadingStatus.StageElapsedSeconds && starts == 1,
                "Reading startup status does not manufacture progress, repeat initialization or touch providers.");
            Check(Field<Border>("StartupLoadingHost").IsVisible &&
                  Field<ProgressBar>("StartupLoadingProgress").IsVisible &&
                  Field<TextBlock>("StartupLoadingText").Text.Contains("Reading saved accounts"),
                "The real awaited load remains visibly explained and animated.");
            Inspect("loading");
            loaded.Invoke(window, new object[] { window, new RoutedEventArgs() }); Drain();
            Check(starts == 1, "A second startup entry cannot duplicate the in-flight load.");
            gate.SetException(new IOException("Fixture saved-state read unavailable"));
            Until(() => !Field<bool>("_startupLoading"), "A failed startup releases its loading guard.");
            var failedStatus = RuntimeStatus();
            Check(failedStatus.State == "error" && failedStatus.Phase == "saved-accounts" &&
                  failedStatus.Error?.Contains("Fixture saved-state read unavailable") == true &&
                  !failedStatus.InitializationComplete && failedStatus.CompletedUtc is null,
                "The actual CLI retains the failing startup phase and precise safe cause without claiming readiness.");
            var retry = Field<Button>("StartupRetryButton");
            Check(retry.IsVisible && Field<ProgressBar>("StartupLoadingProgress").Visibility == Visibility.Collapsed &&
                  Field<TextBlock>("StartupLoadingText").Text.Contains("Fixture saved-state read unavailable") &&
                  Field<TextBlock>("StartupLoadingText").Text.Contains("saved state has been retained"),
                "Failure shows the precise cause and a working Retry loading control, not an endless spinner.");
            // root729: an unrelated startup failure must NOT present a repair; a genuine recoverable
            // interrupted-publication incident MUST present the guarded repair, name the pair, and reload.
            void Pump(Task task)
            {
                var end = DateTime.UtcNow.AddSeconds(40);
                while (!task.IsCompleted && DateTime.UtcNow < end) { Drain(); Thread.Sleep(2); }
                task.GetAwaiter().GetResult();
            }
            string? Diagnostic() =>
                (string?)typeof(MainWindow).GetProperty("StartupRepairDiagnosticForTest", flags)!.GetValue(window);
            Inspect("retained-state error");
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow.StartupCheckpointForTest = _ => gate.Task;
            retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            Check(Field<bool>("_startupLoading") && Field<ProgressBar>("StartupLoadingProgress").IsVisible &&
                  !Field<bool>("_explorerStartupFailed"),
                "The real retry click restores loading feedback and clears the stale failure flag.");
            var retryStatus = RuntimeStatus();
            Check(retryStatus.State == "loading" && retryStatus.Error is null &&
                  retryStatus.StartedUtc >= loadingStatus.StartedUtc && !retryStatus.InitializationComplete,
                "A genuine retry resets its startup timing and clears only the prior error, not required recovery.");
            gate.SetResult();
            Until(() => !Field<bool>("_startupLoading"), "The retry finishes its real local-state reads.");
            var localStatus = RuntimeStatus();
            Check(localStatus.State == "local-only" && localStatus.Phase == "local-fixture-complete" &&
                  !localStatus.InitializationComplete && localStatus.CompletedUtc is null,
                "Local fixture completion explicitly does not stand in for native initialization and startup recovery.");
            Check(Field<Border>("StartupLoadingHost").Visibility == Visibility.Collapsed &&
                  Field<bool>("_explorerLocalReady") && !Field<bool>("_explorerStartupFailed"),
                "Successful local startup reveals content, including a legitimately empty saved state.");
            Check(startupTrace.ToString().Contains("retained-database; Startup: initializing the retained installation") &&
                  startupTrace.ToString().Contains("retained-database; Startup: installation initialization completed") &&
                  startupTrace.ToString().Contains("retained-database; Startup: command-record recovery completed"),
                "The actual startup forwards real authority admission, installation and recovery-record substeps instead of blaming account credentials.");
            Check(startupTrace.ToString().Contains("retained-history; Reading retained first-sync and sync-command outcomes for each pair") &&
                  startupTrace.ToString().Contains("including exact failure causes and run links; previous failures are not discarded.") &&
                  !startupTrace.ToString().Contains("retained-history; Reading the first retained Activity page") &&
                  startupTrace.ToString().Contains("retained-history; Reading the first retained pending-operation page") &&
                  startupTrace.ToString().Contains("retained-history; Reading recent changes and file-only activity") &&
                  startupTrace.ToString().Contains("retained-history; Reading the pair IDs with blocked"),
                "Startup logs each required summary/recovery read without eagerly loading the hidden Activity table.");
            var deferredActivityVersion = Field<int>("_activityQueryVersion");
            typeof(MainWindow).GetMethod("NavigateTo", flags)!.Invoke(window, ["Activity", false]);
            Check(Field<TextBlock>("ActivityPageSummary").Text.StartsWith("Loading retained activity", StringComparison.Ordinal),
                "Opening the deferred page immediately shows loading, not a falsely empty completed result.");
            Until(() => Field<int>("_activityQueryVersion") > deferredActivityVersion && Field<int>("_activityPageLoads") == 0,
                "The actual navigation handler loads the deferred Activity page when opened.");
            Check(!Field<bool>("_activityLoadFailed"), "The on-demand Activity read reports genuine success, not a presumed empty result.");
            Check(!Field<TextBlock>("ActivityPageSummary").Text.StartsWith("Loading retained activity", StringComparison.Ordinal),
                "The actual read completion replaces loading feedback with its retained result.");
            typeof(MainWindow).GetMethod("NavigateTo", flags)!.Invoke(window, ["Dashboard", false]);
            Inspect("empty content after retry");
            var saved = Task.Run(() => window.AuthorityHost.LoadPairsAsync());
            Until(() => saved.IsCompleted, "The saved-state read finishes without blocking the dispatcher.");
            Check(saved.GetAwaiter().GetResult().Pairs.Count == 0,
                "The empty startup neither creates a folder pair nor starts a sync.");
            var left = Directory.CreateDirectory(Path.Combine(root, "local-left")).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "local-right")).FullName;
            File.WriteAllText(Path.Combine(left, "not-approved.txt"), "Do not transfer during startup recovery.");
            SyncEndpoint Endpoint(string path)
            {
                using var lease = PilotDirectoryLease.Open(path);
                return SyncEndpoint.Local(lease.Path,
                    new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
            }
            var bind = Task.Run(() => window.AuthorityHost.BindEndpointPairAsync(SyncEndpointPairDraft.Create(
                new SyncPairEndpoints(Endpoint(left), Endpoint(right), SyncMode.TwoWay), "Accountless recovery")));
            Until(() => bind.IsCompleted, "The real host binds the isolated local/local pair without a cloud account.");
            var localPair = bind.GetAwaiter().GetResult().Pair;
            var recovery = (Task<IReadOnlySet<string>>)typeof(MainWindow)
                .GetMethod("RecoverOnStartupAsync", flags)!.Invoke(window, null)!;
            Until(() => recovery.IsCompleted, "The actual startup recovery handler finishes for an accountless pair.");
            Check(recovery.GetAwaiter().GetResult().Contains(localPair.Id) &&
                  !File.Exists(Path.Combine(right, "not-approved.txt")),
                "Accountless local/local startup reaches real recovery without admitting unreviewed new file work.");
            // Exercise the production branch too: local-fixture completion is not full readiness.
            var disableAutomation = Task.Run(() => window.AuthorityHost.SaveAutomationAsync(new(false, 5, false)));
            Until(() => disableAutomation.IsCompleted, "The isolated fixture disables automatic work before production startup.");
            disableAutomation.GetAwaiter().GetResult();
            MainWindow.StartupLocalOnlyForTest = false;
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow.StartupCheckpointForTest = step => step == "recovery results" ? gate.Task : Task.CompletedTask;
            var activityVersion = Field<int>("_activityQueryVersion");
            var productionTraceStart = startupTrace.GetStringBuilder().Length;
            loaded.Invoke(window, new object[] { window, new RoutedEventArgs() });
            Until(() => Field<string>("_startupPhase") == "recovery-results" && Field<bool>("_startupLoading"),
                "Production startup reaches recovery before loading its history projection.");
            Check(Field<Border>("StartupLoadingHost").IsVisible && !RuntimeStatus().InitializationComplete &&
                  !Field<bool>("_explorerStartupReady") && Field<int>("_activityQueryVersion") == activityVersion,
                "Loading feedback stays visible and history is not redundantly loaded before real recovery completes.");
            gate.SetException(new IOException("Fixture recovery-result admission unavailable"));
            Until(() => !Field<bool>("_startupLoading"), "A late startup failure releases the loading guard.");
            Check(RuntimeStatus() is { State: "error", InitializationComplete: false } && retry.IsVisible &&
                  Field<Border>("StartupLoadingHost").IsVisible,
                "A post-recovery failure retains its exact cause and Retry instead of revealing an empty ready shell.");
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindow.StartupCheckpointForTest = step => step == "recovery results" ? gate.Task : Task.CompletedTask;
            retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Until(() => Field<string>("_startupPhase") == "recovery-results" && Field<bool>("_startupLoading"),
                "Production retry reaches the actual pending recovery-result phase.");
            var lateProjectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateProjection = (Task)typeof(MainWindow).GetMethod("RunStartupProjectionWorkAsync", flags)!
                .Invoke(window, ["Reading a concurrent retained CLI refresh", (Func<Task>)(() => lateProjectionGate.Task)])!;
            Check(!lateProjection.IsCompleted && RuntimeStatus() is { State: "loading", InitializationComplete: false },
                "A real concurrent projection joins awaited startup without prematurely claiming readiness.");
            gate.SetResult();
            Until(() => !Field<bool>("_startupLoading"), "Production retry completes native initialization and retained recovery.");
            Check(RuntimeStatus() is { State: "ready", InitializationComplete: true, CompletedUtc: not null } &&
                  Field<Border>("StartupLoadingHost").Visibility == Visibility.Collapsed &&
                  Field<int>("_activityQueryVersion") == activityVersion,
                "Normal Home startup retains required recovery and hides loading at full readiness without reading the hidden Activity page.");
            var readyBeforeLateRefresh = RuntimeStatus();
            lateProjectionGate.SetResult();
            Until(() => lateProjection.IsCompleted, "The concurrent projection actually finishes after startup readiness.");
            lateProjection.GetAwaiter().GetResult();
            var readyAfterLateRefresh = RuntimeStatus();
            Check(readyAfterLateRefresh is { State: "ready", InitializationComplete: true } &&
                  readyAfterLateRefresh.Phase == readyBeforeLateRefresh.Phase &&
                  readyAfterLateRefresh.CompletedUtc == readyBeforeLateRefresh.CompletedUtc &&
                  readyAfterLateRefresh.LastProgressUtc == readyBeforeLateRefresh.LastProgressUtc,
                "A late CLI history refresh cannot regress completed startup to loading or invent startup advancement.");
            var productionTrace = startupTrace.ToString()[productionTraceStart..];
            Check(!productionTrace.Contains("Reading the first retained Activity page") &&
                  productionTrace.Contains("Completed: Reconciling retained interrupted work") &&
                  productionTrace.Contains("Elapsed:") && !File.Exists(Path.Combine(right, "not-approved.txt")),
                "The actual production startup trace records measured recovery completions without a hidden Activity read or unreviewed file transfers.");
            var repair = Field<Button>("StartupRepairButton");
            Check(repair.Visibility == Visibility.Collapsed &&
                  Diagnostic()?.StartsWith("detected") != true,
                "root729 an unrelated startup failure never presents a repair; diagnostic=" + (Diagnostic() ?? "<null>"));
            var repairAuthority = window.AuthorityHost.Authority;
            var localRoot = Directory.CreateDirectory(Path.Combine(root, "repair-local")).FullName;
            async Task SeedIncidentAsync()
            {
                await window.AuthorityHost.LoadPairsAsync();
                var install = await repairAuthority.LoadInstallationIdAsync() ?? "";
                const string serial = "00a1b2c3";
                const string fileId = "00000000000000f1";
                const string repairPairId = "pair-startup-repair";
                var volumeId = await repairAuthority.ResolveVolumeIdAsync(install, serial, "NTFS", null,
                    supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true, default);
                var configuration = new SyncAuthorityPairConfiguration("recoverable", 0, 1,
                    "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                    AutomaticPruning: false, RecoveryRetentionDays: 30);
                await repairAuthority.RegisterProviderAccountAsync("acct-startup-repair", install, "dbid-repair",
                    "namespace-repair", "namespace-repair", "Repair Dropbox", 1);
                await repairAuthority.RegisterPairAsync(repairPairId, 1, configuration);
                await repairAuthority.RegisterLocalRootBindingAsync("root-repair-local", repairPairId, 1, install,
                    volumeId, serial, fileId, localRoot);
                await repairAuthority.RegisterDropboxRootBindingAsync("root-repair-cloud", repairPairId, 1, install,
                    "acct-startup-repair", "namespace-repair", "id:folder-repair", "/Documents/Repair");
                var published = new SyncPair(repairPairId, "Repair pair", localRoot, 0x00a1b2c3u, 0xf1UL,
                    "/Documents/Repair", "id:folder-repair", Enabled: false, Direction: SyncMode.UploadOnly,
                    InitialReviewApproved: false, Provider: SyncProviders.Dropbox, ConnectionId: "connection-repair")
                    { PolicyRevision = 1 };
                await repairAuthority.RetainPairDefinitionAsync(published, select: true);
                await repairAuthority.RegisterPairAsync(repairPairId, 2, configuration);
            }
            Pump(SeedIncidentAsync());
            Pump((Task)typeof(MainWindow).GetMethod("OfferStartupRepairAsync", flags)!.Invoke(window, null)!);
            Check(repair.Visibility == Visibility.Visible &&
                  Field<TextBlock>("StartupRepairStatus").Text.Contains("Repair pair") &&
                  Diagnostic()?.Contains("pair-startup-repair") == true,
                "root729 a recoverable incident presents the repair and NAMES the affected pair from retained authority");
            var confirmations = 0;
            MainWindow.StartupRepairConfirmationForTest = () => { confirmations++; return true; };
            try { repair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            finally { MainWindow.StartupRepairConfirmationForTest = null; }
            Until(() => Diagnostic()?.StartsWith("repaired") == true, "the real guarded Host repair completes");
            Check(confirmations == 1 && Diagnostic()?.StartsWith("repaired") == true,
                "root729 the confirmation ran once and the real guarded repair restored the published revision");
            Until(() => !Field<bool>("_startupLoading"), "the repair reload finishes its ordinary startup");
            Pump((Task)typeof(MainWindow).GetMethod("OfferStartupRepairAsync", flags)!.Invoke(window, null)!);
            Check(repair.Visibility == Visibility.Collapsed,
                "root729 the repeat offer is a no-op: the repaired pair no longer reports an incident");
            // root731: the repair diagnostics are RETAINED in the durable activity history, not in-memory -
            // read back through a FRESH read-only connection to the same authority database.
            string Scalar(string sql, string parameter, string value)
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    "Data Source=" + repairAuthority.DatabasePath + ";Mode=ReadOnly;Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue(parameter, value);
                return Convert.ToString(command.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
            string Persisted(string token) => Scalar(
                "SELECT count(*) FROM history_event WHERE event_kind='presentation' AND detail_json LIKE $token;",
                "$token", "%" + token + "%");
            Check(Persisted("startup repair offered") != "0" && Persisted("pair-startup-repair") != "0" &&
                  Persisted("startup repair repaired") != "0",
                "root731 the repair offer and result are persisted in the durable activity history and read back fresh");
            var repairConfig = new SyncAuthorityPairConfiguration("recoverable", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false, RecoveryRetentionDays: 30);
            Pump(window.AuthorityHost.Authority.RegisterPairAsync("pair-startup-repair", 3, repairConfig));
            Pump((Task)typeof(MainWindow).GetMethod("OfferStartupRepairAsync", flags)!.Invoke(window, null)!);
            MainWindow.StartupRepairConfirmationForTest = () => false;
            try { repair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            finally { MainWindow.StartupRepairConfirmationForTest = null; }
            Until(() => Diagnostic()?.StartsWith("cancelled") == true, "the repair cancel path reports");
            Check(Persisted("startup repair cancelled") != "0",
                "root731 a cancelled repair is persisted with its safe pair identity and no change");
            var profileId = Scalar("SELECT capability_profile_id FROM capability_profile LIMIT 1;", "$p", "");
            Pump(window.AuthorityHost.Authority.RegisterPairAsync("pair-startup-repair", 4, repairConfig));
            Pump((Task)typeof(MainWindow).GetMethod("OfferStartupRepairAsync", flags)!.Invoke(window, null)!);
            Pump(window.AuthorityHost.Authority.RegisterObjectAsync("object-repair-pending", "directory"));
            var fixtureAdmission = window.AuthorityHost.Authority.CreateAcceptedCommandAdmissionAsync(null, "reconcile", "pair-startup-repair", null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect).GetAwaiter().GetResult();
            Pump(window.AuthorityHost.Authority.CreateOperationAsync(new SyncAuthorityOperationPlan(
                "operation-repair-pending", "idempotency-repair-pending", SyncAuthorityOperationKind.Reconcile,
                "pair-startup-repair", "object-repair-pending", 2, profileId, "{}", 1,
                [new SyncAuthorityMember("member-repair-pending", "pair-startup-repair", "object-repair-pending",
                    "affected", "root-repair-local", null, null, null, "repair.txt", "{}")], [], []), fixtureAdmission));
            MainWindow.StartupRepairConfirmationForTest = () => true;
            try { repair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            finally { MainWindow.StartupRepairConfirmationForTest = null; }
            Until(() => Diagnostic()?.StartsWith("refused") == true, "the repair refusal path reports");
            Check(Persisted("startup repair refused") != "0" && repair.Visibility == Visibility.Visible,
                "root731 a refused repair is persisted with its exact failure reason and the action is restored");
            return 0;
        }
        finally
        {
            Console.SetError(originalErrorWriter);
            MainWindow.StartupCheckpointForTest = null;
            MainWindow.StartupLocalOnlyForTest = false;
            MainWindow.SuppressLoadedStartupForTest = false;
            window.RequestProductExit(); app.Shutdown();
        }
    }
}
