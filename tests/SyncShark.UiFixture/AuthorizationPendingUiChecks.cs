using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Massdex.SyncShark;

// AUTH2 and RCOPY-1: the real Connections pending panel driven through the production members the
// running application uses, and the four exact Recovery generic texts measured on the realised
// MainWindow surface. Every provider input is synthetic: the attempt is held at the product's own
// loopback callback, no browser is opened, no credential is read or written and no provider is called.
internal static class AuthorizationPendingUiChecks
{
    private static readonly TaskCompletionSource<Uri> Opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(appLock.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.AuthorizationPageOpenerForTest = uri => { Opened.TrySetResult(uri); return Task.CompletedTask; };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var window = new MainWindow(appLock.StateAuthority, theme)
        {
            Width = 1240, Height = 790, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            window.Show();
            Drain();
            RecoveryVisibleProofs(window, root);
            CommandLineAttemptObservedAndCancelledFromPanel(window);
            GuiStartedAttemptCancelledFromCommandLine(window, appLock.StateAuthority);
            NoDuplicateCancellationAuthority();
        }
        finally
        {
            MainWindow.AuthorizationPageOpenerForTest = null;
            MainWindow.SuppressLoadedStartupForTest = false;
            window.Close();
        }
        Console.WriteLine("PASS AUTH2 the Connections pending panel observes the shared authorization lifecycle" +
                          " for both start paths with one exact-target cancellation authority");
        return 0;
    }

    // ---- RCOPY-1: visible Recovery proofs on the realised layout, with hidden identity labelled ----
    private static void RecoveryVisibleProofs(MainWindow window, string root)
    {
        const string Subtitle = "Browse protected recovery copies, revisions and deleted items supported by" +
                                " the selected pair. Replacements preview and retain the current destination first.";
        const string Refresh = "Refresh retained Sync Shark archives and available deleted-item metadata for" +
                               " this pair's provider.";
        const string Filter = "Filter the recovery items already loaded; no provider refresh.";
        const string RevisionPath = "Enter a path from this pair to load its provider's retained revision history.";
        const string LoadRevisions = "Load this path's retained revision history from its own provider.";
        // The fixture serves the recovery catalog itself: the visible action is exercised with no
        // credential read and no provider call.
        var requests = new List<(string PairId, bool IncludeDeleted)>();
        MainWindow.RecoveryCatalogLoaderForTest = (pairId, _, _, includeDeleted, _, _, _) =>
        {
            lock (requests) requests.Add((pairId, includeDeleted));
            return Task.FromResult(new SyncRecoveryCatalogPage([], 0, false, null, false));
        };
        SeedPair(window, root, "pair-ui-refresh", "Fixture Recovery Pair", "/FixtureRecovery",
            "fixture-account-refresh", true);
        Call(window, "NavigateTo", "Recovery", true);
        Drain();
        PumpUntil(() => RequestCount(requests) > 0, "the recovery page load after the real navigation");
        var tabs = Field<TabControl>(window, "_recoveryTabs")!;
        var browser = Field<Grid>(window, "_recoveryBrowser")!;
        var toolbar = (WrapPanel)browser.Children.Cast<FrameworkElement>()
            .First(child => Grid.GetRow(child) == 1);
        tabs.SelectedIndex = 0;
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRecovery"), "the saved-copies page to settle");

        // The visible replacement action is the only refresh affordance the user can actually see.
        var visible = Descendants<Button>(toolbar).Where(button => button.IsVisible).ToArray();
        Require(visible.Length == 1,
            "exactly one action is visible in the realised Recovery toolbar after the legacy commands are collapsed");
        var refresh = visible[0];
        Require(refresh.IsEnabled,
            "the visible Recovery refresh action is enabled for the loaded layout");
        Require(AutomationProperties.GetName(refresh) == "Refresh archives and deleted",
            "the visible Recovery refresh action carries an unambiguous accessible name consistent with what it does");
        Require(refresh.ToolTip as string == Refresh,
            "the visible Recovery refresh action carries the exact approved provider-neutral tooltip");
        var before = RequestCount(requests);
        refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => RequestCount(requests) > before, "the refreshed recovery page request");
        var last = LastRequest(requests);
        Require(last.PairId == "pair-ui-refresh" && !last.IncludeDeleted,
            "clicking the visible refresh action runs the existing pair-scoped refresh with deleted metadata off on this tab");

        // Visible controls on the Cloud History tab: the revision path identity and the neutral action.
        tabs.SelectedIndex = 1;
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRecovery"), "the cloud history page to settle");
        var pathBox = (TextBox)window.FindName("RecoveryPathBox")!;
        Require(pathBox.IsVisible && pathBox.ToolTip as string == RevisionPath &&
              AutomationProperties.GetName(pathBox) == "Revision path",
            "the visible revision path box carries the exact sentence and the Revision path accessible name");
        var loadRevisions = Descendants<Button>(toolbar)
            .FirstOrDefault(button => AutomationProperties.GetName(button) == "Load revisions");
        Require(loadRevisions is not null && loadRevisions.IsVisible &&
              loadRevisions.ToolTip as string == LoadRevisions,
            "the visible Load revisions action keeps its exact provider-neutral tooltip");
        var searchBox = (TextBox)window.FindName("RecoverySearchBox")!;
        Require(searchBox.IsVisible && searchBox.ToolTip as string == Filter,
            "the visible recovery search field states that it filters the items already loaded");

        // Hidden identity is labelled for what it proves: the source text survives, and nothing is
        // claimed about what a user sees on the collapsed legacy commands.
        var page = (FrameworkElement)window.FindName("RecoveryPage")!;
        var blocks = Descendants<TextBlock>(page).ToArray();
        var legacyRefresh = Descendants<Button>(page)
            .FirstOrDefault(button => AutomationProperties.GetName(button) == "Refresh archives + deleted");
        Require(blocks.Any(block => block.Text == Subtitle) &&
              !blocks.Any(block => block.IsVisible && block.Text == Subtitle),
            "the preserved generic subtitle identity exists in source and is not visible, so it proves identity only");
        Require(!blocks.Any(block => block.IsVisible &&
                  block.Text.Contains("canonical Dropbox Recovery copies", StringComparison.Ordinal)),
            "no visible Recovery text claims Dropbox-only recovery copies");
        Require(legacyRefresh is not null && !legacyRefresh.IsVisible && legacyRefresh.ToolTip as string == Refresh,
            "the collapsed legacy refresh command keeps the same approved tooltip identity and is not the visible UI");
        var rewind = Descendants<Button>(page)
            .FirstOrDefault(button => AutomationProperties.GetName(button) == "Dropbox Rewind");
        Require(rewind is not null && !rewind.IsVisible,
            "the collapsed Dropbox Rewind command identity is preserved and is not the visible UI");
        tabs.SelectedIndex = 2;
        Drain();
        Require(Descendants<TextBlock>(window).Any(block => block.IsVisible && block.Text == "Dropbox Rewind"),
            "the visible Rewind surface still names Dropbox Rewind explicitly");
        tabs.SelectedIndex = 0;
        Drain();
    }

    private static int RequestCount(List<(string PairId, bool IncludeDeleted)> requests)
    {
        lock (requests) return requests.Count;
    }

    private static (string PairId, bool IncludeDeleted) LastRequest(
        List<(string PairId, bool IncludeDeleted)> requests)
    {
        lock (requests) return requests[^1];
    }

    // The isolated fixture registers one pair through the real authority so the Recovery page has a
    // selected pair to load; nothing outside this fixture's own state root is touched.
    private static void SeedPair(MainWindow window, string root, string id, string name, string dropboxRoot,
        string connectionId, bool select)
    {
        var local = Path.Combine(root, "local-" + id);
        Directory.CreateDirectory(local);
        Pump(window.AuthorityHost.LoadPairsAsync());
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair(id, name, local, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            dropboxRoot, "id:" + id, ConnectionId: connectionId);
        Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
        Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, select));
    }

    private static void Pump(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(1);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for a fixture pair registration");
        }
        task.GetAwaiter().GetResult();
    }

    // ---- AUTH2: a command-line-started attempt and the panel's own exact-target cancellation ------
    private static void CommandLineAttemptObservedAndCancelledFromPanel(MainWindow window)
    {
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = window.AuthorityHost.ConnectDropboxAsync(
            uri => { opened.TrySetResult(uri); return Task.CompletedTask; }, addAccount: true);
        PumpUntil(() => window.AuthorityHost.PendingAuthorization is not null, "the shared attempt to appear");
        Drain();
        var attemptId = window.AuthorityHost.PendingAuthorization!.AttemptId;
        var panel = Field<UIElement>(window, "_authorizationPanel");
        var message = Field<TextBlock>(window, "_authorizationMessage");
        var cancel = Field<Button>(window, "_authorizationCancelButton");
        Require(panel.Visibility == Visibility.Visible,
            "the Connections pending panel observes a command-line-started authorization");
        Require(message.Text.Contains(attemptId, StringComparison.Ordinal) &&
              message.Text.Contains("command line", StringComparison.Ordinal),
            "the panel displays the exact attempt identity of the command-line authorization");
        Require(cancel.IsEnabled && AutomationProperties.GetName(cancel) == "Cancel Connection",
            "the panel offers its cancel action for exactly that attempt");
        Require(!Field<bool>(window, "_observedAuthorizationStartedByThisWindow"),
            "the panel records truthfully that this window did not start the command-line attempt");
        Require(!window.ConnectionConnectButton.IsEnabled && !window.ConnectionReauthorizeButton.IsEnabled &&
              !window.ConnectionDisconnectButton.IsEnabled,
            "the connection controls stay unavailable while an authorization is waiting");
        var responsive = false;
        window.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Normal);
        Drain();
        Require(responsive && window.AuthorityHost.PendingAuthorization is not null,
            "the UI thread stays responsive while the provider response is still outstanding");

        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(window.AuthorityHost.PendingAuthorization?.Status ==
              SyncPendingAuthorizationSnapshot.CancellationRequestedPhase,
            "the panel cancel action targets the exact shared attempt and requests its cancellation");
        Require(message.Text.Contains("Cancelling", StringComparison.Ordinal) &&
              message.Text.Contains(attemptId, StringComparison.Ordinal),
            "the panel states the cancellation request for that exact attempt");
        Require(!cancel.IsEnabled, "the panel cannot request the same cancellation twice");
        PumpUntil(() => window.AuthorityHost.PendingAuthorization is null, "the cancelled attempt to end");
        Drain();
        Require(panel.Visibility == Visibility.Collapsed && message.Text.Length == 0 && !cancel.IsEnabled,
            "terminal completion clears only the matching pending presentation");
        Require(window.ConnectionConnectButton.IsEnabled,
            "terminal completion re-enables the connection controls");
        Require(connect.IsCanceled || connect.IsFaulted,
            "the cancelled command-line authorization did not report a connection");
        _ = connect.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        Require(Field<TextBox>(window, "ConsoleText").Text.Contains(
                  "started from the command line was cancelled", StringComparison.Ordinal),
            "the panel reports a command-line cancellation truthfully even though it was not requested here");
        _ = opened;
    }

    // ---- AUTH2: a window-started attempt cancelled by the real command-line operation -------------
    private static void GuiStartedAttemptCancelledFromCommandLine(MainWindow window, PilotStateAuthority state)
    {
        var authorize = Call(window, "AuthorizeDropboxFromUiAsync",
            (Func<CancellationToken, Task<DropboxConnectionBinding>>)(token =>
                window.AuthorityHost.ConnectDropboxAsync(MainWindow.OpenAuthorizationPageForUiAsync, token,
                    addAccount: true)))!;
        PumpUntil(() => window.AuthorityHost.PendingAuthorization is not null, "the window-started attempt to appear");
        Drain();
        var attemptId = window.AuthorityHost.PendingAuthorization!.AttemptId;
        var panel = Field<UIElement>(window, "_authorizationPanel");
        var message = Field<TextBlock>(window, "_authorizationMessage");
        Require(Opened.Task.IsCompleted,
            "the window start path opens the registered provider authorization page through its own opener");
        Require(Field<bool>(window, "_observedAuthorizationStartedByThisWindow"),
            "the panel records that this window started the attempt");
        Require(panel.Visibility == Visibility.Visible &&
              message.Text.Contains("Finish connecting Dropbox in your browser", StringComparison.Ordinal) &&
              message.Text.Contains(attemptId, StringComparison.Ordinal),
            "the established pending panel layout and message are preserved for a window-started attempt");

        var runtime = new UiCliRuntime(window.AuthorityHost, state);
        var command = SyncCliRegistry.DispatchAsync(runtime,
            SyncCliRegistry.Parse(["--cli", "accounts", "cancel", "--key", attemptId, "--yes"]),
            TextWriter.Null, CancellationToken.None);
        PumpUntil(() => command.IsCompleted, "the command-line cancellation to complete");
        var report = (SyncCliControlData)command.GetAwaiter().GetResult();
        Require(report.Accepted && report.Result.Contains("was cancelled", StringComparison.Ordinal),
            "the command-line exact-target cancellation reported terminal cancellation");
        var authorizeTask = (Task<DropboxConnectionBinding>)authorize;
        PumpUntil(() => authorizeTask.IsCompleted, "the window-started attempt to end");
        Drain();
        Require(panel.Visibility == Visibility.Collapsed,
            "the panel cleared the window-started attempt once it reached its terminal result");
        Require(authorizeTask.IsFaulted || authorizeTask.IsCanceled,
            "the window start path reports the cancelled attempt instead of a connection");
        _ = authorizeTask.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ---- AUTH2: the window holds no duplicate cancellation authority of its own -------------------
    private static void NoDuplicateCancellationAuthority()
    {
        var fields = typeof(MainWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Require(!fields.Any(field => typeof(CancellationTokenSource).IsAssignableFrom(field.FieldType) &&
              field.Name.Contains("authorization", StringComparison.OrdinalIgnoreCase)),
            "the window keeps no per-attempt authorization cancellation source of its own");
        Require(fields.Any(field => field.Name == "_observedAuthorizationAttemptId"),
            "the window keys its pending presentation on the exact shared attempt identity");
        Require(typeof(MainWindow).GetMethod("AuthorizationAttemptChanged",
                  BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "the panel is driven by the shared attempt change notification");
        Require(typeof(MainWindow).GetMethod("CancelPendingAuthorizationFromUi",
                  BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "the panel cancel action is the shared exact-target host operation");
    }

    private sealed class UiCliRuntime(SyncAuthorityRuntimeHost host, PilotStateAuthority state) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => state;
        public override string Mode => "running-application";

        // The application owns the host and its product lock, exactly as the production bridge runtime does.
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (var nested in Descendants<T>(node)) yield return nested;
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Func<bool> condition, string label)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for " + label);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Authorization pending UI: " + label);
        Console.WriteLine("PASS AUTH2 " + label);
    }
}
