using Massdex.SyncShark;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.V2;
using Side = Massdex.SyncShark.Engine.Side;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// T318: the Engine issue/notice presentation on the ACTUAL window refresh route, plus the bounded
// startup/isolation slices that can run in this process under the existing test root override.
//
// Every expected sentence comes from the same data owners the product uses (Text.Get and
// EngineCliCatalog.Message), so a copied or enum-identity label fails here. The refresh path is the
// real MainWindow.ApplyEngineIssues projection, not a re-implementation.
internal static class EngineIssuePresentationUiChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);

        // AUDIT-D1-rev5 section N: no exact-child process redirect exists, so the isolation and
        // startup primitives run IN this fixture process under the existing root override.
        CheckIsolatedRuntimeRoot();
        CheckBusyExitWritesAndClearsOwnerNotice();
        CheckNoticeWriteFailureLogsOneIdentityOnlyLine();
        CheckStartupCommandIsCanonicalEngineStart();
        CheckReadinessVariableIsClearedOnClaim();

        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(instance.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1180, Height = 800 };
        // Preference-only presentation must never touch the live registry during this fixture.
        window.StartupRegistrationForTest = _ => { };
        var pairRoot = Path.Combine(root, "pair-root");
        Directory.CreateDirectory(pairRoot);
        using var lease = PilotDirectoryLease.Open(pairRoot);
        try
        {
            window.Show();
            Drain();
            var pair = new SyncPair("t318-pair", "Pair T318", pairRoot,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/t318", "id:t318");
            window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));

            CheckRetryStatesRenderDataOwnedGuidance(window);
            CheckSharedRendererIsSingleTextSource(window);
            CheckIgnoredFolderKeptOffersDataOwnedChoices(window);
            foreach (var kind in Enum.GetValues<IssueKind>())
            {
                var issue = new Issue(900, kind, TextKey.FailChanged, "file.txt", new IssueSubject([], null), DateTimeOffset.UnixEpoch, false);
                window.ApplyEngineIssues([issue], new Dictionary<long, IssueRetryView>());
                Require(Row(window, issue.Id).NoticeOptions!.Any(option => option.Choice == nameof(ResolutionChoice.Ignore)), "every kind offers Ignore");
                window.ApplyEngineIssues([issue with { Ignored = true }], new Dictionary<long, IssueRetryView>());
                Require(Row(window, issue.Id).NoticeOptions!.Any(option => option.Choice == nameof(ResolutionChoice.Unignore) && option.Label == Text.Get(TextKey.RestoreLabel)), "ignored offers data-owned Restore");
                window.ApplyEngineIssues([issue], new Dictionary<long, IssueRetryView>());
                Require(!Row(window, issue.Id).NoticeOptions!.Any(option => option.Choice == nameof(ResolutionChoice.Unignore)), "restored view offers Ignore again");
            }
            CheckRecoveryLabelsAreDataOwned();
            CheckOnlyMatchingCloseRemovesRows(window);
            CheckUnrelatedLegacyRowsSurvive(window);
            CheckReviewedNoticeChoice(window, root);
            CheckRefreshDuringDestinationDialogStopsTheChoice(window);
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = false;
            window.RequestProductExit();
            app.Shutdown();
        }

        Console.WriteLine("T318 Engine issue presentation checks passed; isolated product root, no provider call, "
            + "no live registration or product-state effect.");
        return 0;
    }

    // Freeze 3.3 / CV-RETRYVIEW: the in-use family renders the shared cause plus the guidance for its
    // current state from engine-strings.json. A missing entry is Unavailable, never an active retry.
    private static void CheckRetryStatesRenderDataOwnedGuidance(MainWindow window)
    {
        var nextId = 100L;
        foreach (var state in Enum.GetValues<RetryPresentationState>())
        {
            var id = nextId++;
            var path = "state-" + state + ".txt";
            var issue = InUseIssue(id, path);
            window.ApplyEngineIssues([issue],
                new Dictionary<long, IssueRetryView> { [id] = new(state) });
            var row = Row(window, id);
            var expected = Text.Get(TextKey.FailLocalInUse, (TextArg.Path, path), (TextArg.Message, ""))
                + Environment.NewLine + Text.Get(ExpectedRetryKey(state));
            Require(row.Reason == expected,
                "Retry state " + state + " renders the data-owned cause and guidance with no other text");
            Require(row.NoticeOptions!.Any(option => option.Choice == nameof(ResolutionChoice.Ignore)),
                "Retry state " + state + " offers Ignore without changing retry guidance");
        }

        var transition = InUseIssue(150, "transition.txt");
        window.ApplyEngineIssues([transition],
            new Dictionary<long, IssueRetryView> { [150] = new(RetryPresentationState.Running) });
        var running = Row(window, 150).Reason;
        window.ApplyEngineIssues([transition],
            new Dictionary<long, IssueRetryView> { [150] = new(RetryPresentationState.Waiting) });
        var waiting = Row(window, 150).Reason;
        Require(running != waiting && waiting == Text.Get(TextKey.FailLocalInUse, (TextArg.Path, "transition.txt"),
                (TextArg.Message, "")) + Environment.NewLine + Text.Get(TextKey.RetryWaiting),
            "A status-only mode transition updates the same row and removes nothing");

        var skip = new Issue(151, IssueKind.Skipped, TextKey.SkipReservedName, "invalid:name",
            new IssueSubject([new IssueItem(Side.Dropbox, "invalid:name", "id:151")], null),
            DateTimeOffset.UnixEpoch, false);
        window.ApplyEngineIssues([skip],
            new Dictionary<long, IssueRetryView> { [151] = new(RetryPresentationState.Running) });
        Require(Row(window, 151).Reason == Text.Get(TextKey.SkipReservedName,
                (TextArg.Path, "invalid:name"), (TextArg.Message, "")),
            "A cause outside the in-use family renders only its own sentence, never retry guidance");

        var missing = InUseIssue(152, "missing-status.txt");
        window.ApplyEngineIssues([missing], new Dictionary<long, IssueRetryView>());
        Require(Row(window, 152).Reason.EndsWith(Environment.NewLine + Text.Get(TextKey.RetryUnavailable),
                StringComparison.Ordinal),
            "A missing status entry is Unavailable authority, never a fabricated active retry");
    }

    // The window row text is the one shared Edge renderer the CLI also uses, never a second wording.
    private static void CheckSharedRendererIsSingleTextSource(MainWindow window)
    {
        var id = 500L;
        var path = "shared-renderer.txt";
        var issue = InUseIssue(id, path);
        var view = new IssueRetryView(RetryPresentationState.Waiting);
        window.ApplyEngineIssues([issue], new Dictionary<long, IssueRetryView> { [id] = view });
        Require(Row(window, id).Reason == IssuePresentation.Render(issue, view),
            "The UI row text is the one shared Edge renderer the CLI also uses");
    }

    // CV-D09 / F014.fix3: the ignored-folder-kept notice offers the one-off "Keep this time" and the
    // always choice, both with the exact shared resolution identities and the supplied data-owned labels.
    private static void CheckIgnoredFolderKeptOffersDataOwnedChoices(MainWindow window)
    {
        var path = "kept-folder";
        var issue = new Issue(200, IssueKind.Info, TextKey.InfoIgnoredFolderKept, path,
            new IssueSubject([new IssueItem(Side.Dropbox, path, "id:200")], null),
            DateTimeOffset.UnixEpoch, false);
        window.ApplyEngineIssues([issue], new Dictionary<long, IssueRetryView>());
        var row = Row(window, 200);
        Require(row.Reason == Text.Get(TextKey.InfoIgnoredFolderKept, (TextArg.Path, path), (TextArg.Message, "")),
            "The ignored-folder-kept notice renders its data-owned sentence");
        var options = row.NoticeOptions ?? throw new InvalidDataException(
            "The ignored-folder-kept notice offered no choices on the real refresh route.");
        Require(options.Count == 3 && options.Any(option => option.Choice == nameof(ResolutionChoice.Ignore)), "The notice offers one-off, always and Ignore choices");
        Require(options[0].Choice == nameof(ResolutionChoice.Dismiss) &&
                options[0].Label == EngineCliCatalog.Message(EngineMessageKey.KeepThisTimeLabel),
            "The one-off choice keeps the shared Dismiss identity and the data-owned 'Keep this time' label");
        Require(options[1].Choice == nameof(ResolutionChoice.KeepFolderAlways) &&
                options[1].Label == EngineCliCatalog.Message(EngineMessageKey.KeepFolderAlwaysLabel),
            "The always choice keeps the shared KeepFolderAlways identity and the data-owned label");
        Require(options[0].Label != nameof(ResolutionChoice.Dismiss) &&
                options[1].Label != nameof(ResolutionChoice.KeepFolderAlways),
            "Neither choice renders a bare enum identity as its label");
    }

    // F014.fix3: the recovery choices are supplied as data-owned sentences, never the enum identities
    // the shared command uses internally. The real recovery resolve surface is exercised by T206/T241.
    private static void CheckRecoveryLabelsAreDataOwned()
    {
        var keys = new[]
        {
            EngineMessageKey.RecoveryCheckLabel,
            EngineMessageKey.RecoveryAcceptCurrentLabel,
            EngineMessageKey.RecoveryAbandonIncomingLabel
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var label = EngineCliCatalog.Message(key);
            Require(!string.IsNullOrWhiteSpace(label) && label != key.ToString() && seen.Add(label),
                "Recovery label " + key + " is a supplied data-owned sentence, not an enum identity");
        }
    }

    // LOCAL-IN-USE F4 / section 12.1: only an issue absent from the authoritative list (a matching
    // successful closure) removes its row. A status-only change or another issue's presence removes nothing.
    private static void CheckOnlyMatchingCloseRemovesRows(MainWindow window)
    {
        var first = InUseIssue(300, "a.txt");
        var second = InUseIssue(301, "b.txt");
        var views = new Dictionary<long, IssueRetryView>
        {
            [300] = new(RetryPresentationState.Waiting),
            [301] = new(RetryPresentationState.Waiting)
        };
        window.ApplyEngineIssues([first, second], views);
        Require(HasRow(window, 300) && HasRow(window, 301), "Both current engine issues have a row");
        window.ApplyEngineIssues([first], views);
        Require(HasRow(window, 300), "The still-current issue keeps its row");
        Require(!HasRow(window, 301), "Only the issue missing from the authoritative list removes its row");
    }

    // The projection owns only rows it created (the exact issue id). A legacy notice is never swept.
    private static void CheckUnrelatedLegacyRowsSurvive(MainWindow window)
    {
        var legacy = new DecisionRow("legacy-pair", "Legacy", "legacy.txt", "Conflict", "legacy reason",
            ConflictId: "legacy-1");
        window.ConflictRows.Add(legacy);
        try
        {
            window.ApplyEngineIssues([InUseIssue(400, "current.txt")],
                new Dictionary<long, IssueRetryView> { [400] = new(RetryPresentationState.Waiting) });
            Require(window.ConflictRows.Any(row => row.ConflictId == "legacy-1"),
                "A legacy row with no engine issue id is untouched by the engine projection");
        }
        finally
        {
            window.ConflictRows.Remove(legacy);
        }
    }

    private static void CheckReviewedNoticeChoice(MainWindow window, string root)
    {
        var product = TempRoot("t318-reviewed-notice-");
        using var paths = SyncSharkRuntimePaths.OverrideProductRootForTest(product);
        var operations = new Massdex.SyncShark.Engine.Run.EngineOperations(product, _ => { });
        var pair = new PairConfig("reviewed-ui", "Pair A", Path.Combine(root, "local"), "/Remote", "account", [], [],
            FirstRunPolicy.NewestWins, false, null);
        void Raise(string identity)
        {
            using var store = Massdex.SyncShark.Engine.Storage.StateStore.Open(
                Massdex.SyncShark.Engine.Storage.StateStore.DatabasePath(product), (_, _, _, _, _) => false);
            store.SavePair(pair);
            store.Commit(pair.PairId, StateChange.Empty with { Raise = [(IssueKind.Info,
                TextKey.InfoIgnoredFolderKept, "folder", new IssueSubject([new(Side.Dropbox, "folder", identity)], null))] });
        }
        Raise("original");
        var reviewed = operations.Issues(pair.PairId, null).Single();
        var commandField = typeof(MainWindow).GetField("_engineCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalCommand = commandField.GetValue(window);
        var dispatches = 0;
        Func<string[], TextWriter, TextWriter, CancellationToken, Task<int?>> command = async (args, output, error, token) =>
        {
            var invocation = SyncCliRegistry.Parse(args);
            if (EngineCliCatalog.CommandKey(invocation.Command) != EngineCommandKey.Resolve)
                return (int)SyncCliExitCode.TransientUnavailable;
            dispatches++;
            return await SyncCli.ExecuteAsync(args, output, error, () => new EngineCliRuntime());
        };
        commandField.SetValue(window, command);
        try
        {
            foreach (var stale in new[] { true, false })
            {
                var shown = stale ? reviewed : operations.Issues(pair.PairId, null).Single();
                window.ApplyEngineIssues(pair.PairId, pair.Name, [shown], new Dictionary<long, IssueRetryView>());
                var row = Row(window, shown.Id);
                typeof(MainWindow).GetField("_engineSelectedPair", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, new Massdex.SyncShark.Engine.Edge.Results.PairView(pair.PairId, pair.Name,
                        pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId, pair.FirstRun, false, [], RootsBound: false));
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (stale) Raise("replacement");
                    Application.Current.Windows.OfType<AppDialogWindow>().Single(dialog => dialog.IsVisible).DialogResult = true;
                }));
                var option = row.NoticeOptions!.Single(value => value.Choice == nameof(ResolutionChoice.Ignore));
                var action = typeof(MainWindow).GetMethod("ApplyEngineNoticeOptionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var task = (Task)action.Invoke(window, [row, option])!;
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame);
                try { task.GetAwaiter().GetResult(); Require(!stale, "stale UI choice must refuse"); }
                catch (IOException error)
                {
                    Require(stale && error.Message.Contains(Text.Get(TextKey.ResolveStale,
                        (TextArg.Id, shown.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))), StringComparison.Ordinal),
                        "UI reports the stale-notice refusal");
                }
                using var stored = Massdex.SyncShark.Engine.Storage.StateStore.OpenReadOnly(
                    Massdex.SyncShark.Engine.Storage.StateStore.DatabasePath(product))!;
                Require(stored.IssueById(pair.PairId, shown.Id)!.Ignored == !stale,
                    "the real UI choice applies only to the reviewed subject");
            }
            Require(dispatches == 2, "both UI choices traverse the real CLI dispatcher");
        }
        finally { commandField.SetValue(window, originalCommand); }
    }

    // F4: the destination dialog pumps messages, so a refresh can remove the exact row while it is open.
    // The real action must revalidate before composing the confirmation, never index the cleared issue map.
    private static void CheckRefreshDuringDestinationDialogStopsTheChoice(MainWindow window)
    {
        const long id = 600;
        var issue = new Issue(id, IssueKind.Info, TextKey.InfoIgnoredFolderKept, "kept-folder",
            new IssueSubject([new IssueItem(Side.Dropbox, "kept-folder", "id:600")], null),
            DateTimeOffset.UnixEpoch, false) { Reviewed = "reviewed" };
        window.ApplyEngineIssues([issue], new Dictionary<long, IssueRetryView>());
        var row = Row(window, id);
        var option = row.NoticeOptions!.Single(item => item.Choice == nameof(ResolutionChoice.KeepFolderAlways));
        typeof(MainWindow).GetField("_engineSelectedPair", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, new Massdex.SyncShark.Engine.Edge.Results.PairView(row.PairId, "Pair " + row.PairId,
                "local", "dropbox", "account", FirstRunPolicy.NewestWins, true, [], RootsBound: true));

        // Runs inside the dialog's nested message loop: refresh removes the row and its issue, then confirm.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            window.ApplyEngineIssues([], new Dictionary<long, IssueRetryView>());
            var dialog = Application.Current.Windows.OfType<Window>()
                .First(candidate => candidate is not MainWindow && candidate.IsVisible);
            var panel = (StackPanel)dialog.Content;
            ((TextBox)panel.Children[1]).Text = "FileExtension:tmp";
            ((Button)panel.Children[2]).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }));

        var action = typeof(MainWindow).GetMethod("ApplyEngineNoticeOptionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Task)action.Invoke(window, [row, option])!).GetAwaiter().GetResult();
        Require(!HasRow(window, id), "A refresh during the destination dialog leaves the removed notice unapplied");
    }

    // AUDIT-D1-rev5 section N: the effective product root drives the lock, the supervisor notice and the
    // lifecycle log together, so every tested effect stays inside the fixture's owned directory.
    private static void CheckIsolatedRuntimeRoot()
    {
        var root = TempRoot("t318-isolate-");
        using (SyncSharkRuntimePaths.OverrideProductRootForTest(root))
        {
            Require(SyncSharkRuntimePaths.ProductRoot == Path.GetFullPath(root),
                "The test override is the one effective product root");
            Require(Under(root, SyncSharkRuntimePaths.InstanceLockFile),
                "The product lock path lies inside the owned root");
            Require(Under(root, AutoSyncProcess.ExistingOwnerNoticePath),
                "The supervisor notice path follows the same owned root");
            Require(Under(root, AutoSyncProcess.ExistingOwnerNoticePathFor(root)),
                "The notice-for-root helper agrees with the owned root");
            Require(Under(root, AutoSyncLog.FilePath(root)),
                "The lifecycle log path lies inside the owned root");
        }
        Require(SyncSharkRuntimePaths.ProductRoot != Path.GetFullPath(root),
            "Disposing the override restores normal root selection");
    }

    // A genuine exclusive-owner (Busy) child exit raises the existing-owner notice; a successful
    // acquisition clears that notice to empty rather than deleting or forgetting it.
    private static void CheckBusyExitWritesAndClearsOwnerNotice()
    {
        var root = TempRoot("t318-busy-");
        using (SyncSharkRuntimePaths.OverrideProductRootForTest(root))
        {
            var stops = 0;
            var supervisor = new AutoSyncSupervisor(root,
                () => new FixedChild(7, (int)SyncCliExitCode.Busy),
                _ => ++stops > 1,
                TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5), TimeSpan.Zero);
            Require(supervisor.Run() == 0,
                "The supervisor stops cooperatively after the ownership refusal");
            var notice = AutoSyncProcess.ExistingOwnerNoticePathFor(root);
            Require(File.Exists(notice) && new FileInfo(notice).Length > 0,
                "An exclusive-owner (Busy) child exit writes the existing-owner notice under the owned root");
            AutoSyncProcess.ClearExistingOwnerNotice(notice);
            Require(File.Exists(notice) && new FileInfo(notice).Length == 0,
                "Successful acquisition clears only the notice and leaves it empty");
        }
    }

    // AUDIT-D1-rev5 section N: a failed notice clear is surfaced as ONE identity-only lifecycle-failure
    // line, with no path, message or other free-form detail, and it does not stop a running host.
    private static void CheckNoticeWriteFailureLogsOneIdentityOnlyLine()
    {
        var root = TempRoot("t318-noticelock-");
        using (SyncSharkRuntimePaths.OverrideProductRootForTest(root))
        {
            var notice = AutoSyncProcess.ExistingOwnerNoticePathFor(root);
            Directory.CreateDirectory(Path.GetDirectoryName(notice)!);
            File.WriteAllText(notice, "owned");
            var log = AutoSyncLog.FilePath(root);
            var before = File.Exists(log) ? File.ReadAllLines(log).Length : 0;
            using (new FileStream(notice, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    AutoSyncProcess.ClearExistingOwnerNotice(notice);
                    throw new InvalidDataException("A notice locked against writes was cleared.");
                }
                catch (IOException error)
                {
                    AutoSyncLog.Failure(root, error);
                }
            }
            var lines = File.ReadAllLines(log);
            Require(lines.Length == before + 1, "A failed notice clear appends exactly one line");
            var last = lines[^1];
            Require(last.Contains("System.IO.IOException", StringComparison.Ordinal),
                "The line records the exception identity");
            Require(!last.Contains(notice, StringComparison.Ordinal) && !last.Contains(root, StringComparison.Ordinal),
                "The line contains no path or free-form message detail");
        }
    }

    // Startup uses the readiness-gated CLI route; old owned supervisor registrations remain recognizable.
    private static void CheckStartupCommandIsCanonicalEngineStart()
    {
        var executable = Path.Combine(Path.GetTempPath(), "SyncShark.exe");
        var command = AutoSyncProcess.StartupCommand(executable);
        Require(command == "\"" + Path.GetFullPath(executable) + "\" --cli engine host start --yes",
            "Start with Windows targets the readiness-gated Engine host start command");
        Require(AutoSyncProcess.IsProductStartupCommand(command),
            "The canonical command is recognized as an owned product startup");
        Require(AutoSyncProcess.IsProductStartupCommand(
                "\"" + executable + "\" --core v2 --autosync-supervisor"),
            "The old supervisor spelling remains a recognized owned command");
        Require(!AutoSyncProcess.IsProductStartupCommand("\"" + executable + "\" --core v2 --engine-host"),
            "The private host spelling is not a startup registration");
        Require(!AutoSyncProcess.IsProductStartupCommand("\"" + executable + "\" --background"),
            "The legacy GUI value is not accepted as the supervisor command");
        try
        {
            _ = AutoSyncProcess.StartupCommand("SyncShark.exe");
            throw new InvalidDataException("A relative executable path was accepted for startup.");
        }
        catch (ArgumentException)
        {
        }
        Require(true, "A relative executable path is refused for startup");
    }

    // RC05-V5-S: a supervisor's relaunched child must not inherit a stale readiness handle number, so
    // claiming it clears the variable even when the handle itself is unusable.
    private static void CheckReadinessVariableIsClearedOnClaim()
    {
        const string variable = "SYNCSHARK_ENGINE_READINESS_HANDLE";
        Environment.SetEnvironmentVariable(variable, null);
        Require(AutoSyncProcess.TakeReadinessPipe() is null,
            "A launch without the readiness variable claims no pipe");
        Environment.SetEnvironmentVariable(variable, "0");
        try
        {
            using var pipe = AutoSyncProcess.TakeReadinessPipe();
        }
        catch (Exception)
        {
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
        Require(Environment.GetEnvironmentVariable(variable) is null,
            "Claiming the readiness variable clears it so a supervisor's later child cannot reuse it");
    }

    private static DecisionRow Row(MainWindow window, long issueId) =>
        window.ConflictRows.Single(row => MainWindow.EngineIssueId(row) == issueId);

    private static bool HasRow(MainWindow window, long issueId) =>
        window.ConflictRows.Any(row => MainWindow.EngineIssueId(row) == issueId);

    private static Issue InUseIssue(long id, string path) =>
        new(id, IssueKind.Failure, TextKey.FailLocalInUse, path,
            new IssueSubject([new IssueItem(Side.Local, path, "id:" + id.ToString())], null),
            DateTimeOffset.UnixEpoch, false);

    // The declared state-to-guidance map is the oracle; the expected text still comes from the data file.
    private static TextKey ExpectedRetryKey(RetryPresentationState state) => state switch
    {
        RetryPresentationState.Running => TextKey.RetryRunning,
        RetryPresentationState.Waiting => TextKey.RetryWaiting,
        RetryPresentationState.Offline => TextKey.RetryOffline,
        RetryPresentationState.Paused => TextKey.RetryPaused,
        RetryPresentationState.Disabled or RetryPresentationState.RepairOnly => TextKey.RetryRepairOnly,
        RetryPresentationState.RecoveryRequired => TextKey.RetryRecoveryRequired,
        _ => TextKey.RetryUnavailable
    };

    private static string TempRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool Under(string root, string path)
    {
        var owned = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(owned, StringComparison.OrdinalIgnoreCase);
    }

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidDataException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class FixedChild : AutoSyncSupervisor.IChild
    {
        private readonly int _processId;
        private readonly int _exitCode;
        internal FixedChild(int processId, int exitCode) { _processId = processId; _exitCode = exitCode; }
        public int ProcessId => _processId;
        public void WaitForExit() { }
        public int ExitCode => _exitCode;
        public void Dispose() { }
    }
}
