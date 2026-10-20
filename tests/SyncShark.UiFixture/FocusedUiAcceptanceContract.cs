using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class FocusedUiAcceptanceContract
{
    private static readonly List<string> Passed = [];

    public static int Run(string rawOutputRoot, string expectedSourceRevision, bool activityKindOnly = false,
        bool isolationOnly = false)
    {
        Passed.Clear();
        var outputRoot = Path.GetFullPath(rawOutputRoot);
        Directory.CreateDirectory(outputRoot);
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-focused-ui", Guid.NewGuid().ToString("N"));
        var localRoot = Path.Combine(stateRoot, "LocalName");
        CreateLocalTree(localRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        var priorSynchronizationContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        MainWindow? window = null;
        try
        {
            if (MainWindow.PairHealthLoaderForTest is not null || MainWindow.MonitorProjectionLoaderForTest is not null ||
                MainWindow.PairRunDispatcherForTest is not null || MainWindow.PairBindingForTest is not null ||
                MainWindow.DropboxConnectionCheckForTest is not null ||
                MainWindow.DropboxConnectionStatusLoaderForTest is not null ||
                MainWindow.DropboxReconnectForTest is not null ||
                MainWindow.DropboxDisconnectForTest is not null ||
                MainWindow.BootstrapApprovalConfirmationForTest is not null ||
                MainWindow.BootstrapApprovalForTest is not null ||
                MainWindow.BootstrapActionPlanForTest is not null ||
                MainWindow.BootstrapReviewLoaderForTest is not null ||
                MainWindow.BootstrapReviewPageLoaderForTest is not null ||
                MainWindow.ActivityProjectionLoaderForTest is not null ||
                MainWindow.PendingQueueCountLoaderForTest is not null ||
                MainWindow.MonitorConfiguratorForTest is not null ||
                MainWindow.MonitorStopperForTest is not null ||
                MainWindow.UnsavedDraftRunChoiceForTest is not null ||
                MainWindow.PairFolderBrowserFactoryForTest is not null ||
                MainWindow.RecommendedRecoveryFolderForTest is not null ||
                MainWindow.RecoveryFolderDialogForTest is not null ||
                MainWindow.LocalRootNodeFactoryForTest is not null || MainWindow.PairHealthTimeoutForTest is not null ||
                MainWindow.ClipboardTextSetterForTest is not null || MainWindow.SuppressLoadedStartupForTest)
                throw new InvalidDataException("A production test override was active before the isolated fixture installed it.");
            Passed.Add("TC-UI-10.b production test overrides default inactive");
            theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
            using var lease = PilotDirectoryLease.Open(localRoot);
            var pair = new SyncPair("a123456789abcdefabcd", "Project files", localRoot,
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/RemoteName", "id:remote-name",
                Enabled: true, SelectionRules: [], SelectionDefaultIncluded: true, InitialReviewApproved: true,
                RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery", "id:recovery", "root"));
            var pairStore = new SyncPairStore(appLock.StateAuthority);
            pairStore.SaveAsync(new SyncPairSet([pair], pair.Id)).GetAwaiter().GetResult();
            var connection = new DropboxConnectionBinding(
                DropboxProviderConfiguration.RegisteredClientId, "dbid:focused-ui", "root-focused-ui",
                "home-focused-ui", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes, DisplayName: "Focused UI",
                Email: "focused-ui@example.invalid", AccountType: "basic");
            new DropboxConnectionStore(appLock.StateAuthority).SaveNewAsync(connection).GetAwaiter().GetResult();
            using (var preflight = new IsolatedRuntime(appLock.StateAuthority))
                _ = preflight.Host.LoadPairsAsync().GetAwaiter().GetResult();
            new SyncAutomationStore(appLock.StateAuthority)
                .SaveAsync(new SyncAutomationSettings(true, 15, false)).GetAwaiter().GetResult();

            MainWindow.SuppressLoadedStartupForTest = true;
            var isolation = AssertFixtureIsolation(appLock.StateAuthority, theme, pair);
            if (isolationOnly)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    result = "PASS",
                    selector = "--focused-ui-isolation-contract",
                    cases = Passed,
                    isolation,
                    versions = AssertVersionIdentity(expectedSourceRevision)
                }));
                return 0;
            }

            var browser = new HierarchyBrowser();
            var dispatches = new List<string>();
            MainWindow.ExitCheckpointWaitForTest = (_, _) => Task.FromResult(true);
            MainWindow.SuppressLoadedStartupForTest = true;
            MainWindow.DropboxConnectionCheckForTest = () => Task.FromResult(true);
            MainWindow.DropboxConnectionStatusLoaderForTest = verifyLive => Task.FromResult(
                new DropboxConnectionStatus(connection, true, verifyLive,
                    verifyLive ? "Connected and verified." : "Connected; live verification pending.",
                    null, 1, null));
            MainWindow.PairFolderBrowserFactoryForTest = _ => Task.FromResult<IDropboxFolderBrowserService>(browser);
            MainWindow.RecommendedRecoveryFolderForTest = _ => Task.FromResult(
                new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
                    "id:recommended-recovery"));
            MainWindow.LocalRootNodeFactoryForTest = requestedPath =>
            {
                if (requestedPath?.Contains("surviving", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var survivingRoot = requestedPath[..requestedPath.IndexOf("surviving",
                        StringComparison.OrdinalIgnoreCase)] + "surviving";
                    return [LocalFolderNode.CreateForTest(survivingRoot, [])];
                }
                return CompactLocalTree(localRoot);
            };
            MainWindow.PairRunDispatcherForTest = (pairs, trigger) =>
            {
                dispatches.Add(trigger + ":" + string.Join(',', pairs.Select(item => item.Id)));
                return Task.CompletedTask;
            };
            MainWindow.UnsavedDraftRunChoiceForTest = () => AppDialogChoice.Cancel;
            MainWindow.PairHealthLoaderForTest = (_, _) => Task.FromResult(Health(pair, true, true));
            Step(outputRoot, "resize");
            AssertResizeSequence(appLock.StateAuthority, theme, pair, browser, localRoot);
            window = CreateIsolatedWindow(appLock.StateAuthority, theme);
            window.Show();
            if (activityKindOnly)
            {
                window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
                foreach (var accepted in GuidedSyncCorrectionContract.RunActivityKindDelta(window, pair, outputRoot))
                    Passed.Add(accepted);
                var focusedVersions = AssertVersionIdentity(expectedSourceRevision);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    result = "PASS",
                    selector = "--activity-kind-ui-contract",
                    cases = Passed,
                    versions = focusedVersions
                }));
                return 0;
            }
            AssertConnectionStartupAndConnectAction(window);
            PumpTask(window, GoogleMonitorScopeChecks.RunAsync(window, pair, pairStore));
            NavigateToPairs(window);
            AssertRecommendedRecoveryPresentation(window, pair, browser);
            window.PrepareExistingPairEditorForTest(pair);
            PumpUntil(window, () => window.PairRows.Any(item => item.Id == pair.Id),
                "The saved pair did not load.");
            UseCompactLocalTree(window, localRoot);
            PumpUntil(window, () => FindDescendants<ContentControl>(window).Any(item =>
                    item.Name == "PairRowDetailsHost" && ReferenceEquals(item.Content,
                        window.FindName("PairEditorPanel"))),
                "The saved editor did not open inside its list row.");
            PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName"));
            window.ExpandLocalTreeForTest(localRoot);
            PumpUntil(window, () => FindLocal(window, Path.Combine(localRoot, "Match")) is not null &&
                                    FindDropbox(window, "/RemoteName/Match") is not null,
                "The paired local and Dropbox descendants were not loaded.");

            Step(outputRoot, "shared-editor");
            AssertSharedEditorAndStatus(window, pair);
            UseCompactLocalTree(window, localRoot);
            window.ExpandLocalTreeForTest(localRoot);
            PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName"));
            Step(outputRoot, "search-and-filters");
            AssertSearchAndFilters(window, browser, localRoot, outputRoot);
            Step(outputRoot, "linked-selection");
            AssertLinkedSelection(window, pair, pairStore, browser, dispatches, localRoot);
            Step(outputRoot, "health-and-dispatch");
            AssertHealthAndDispatch(window, pair, pairStore, dispatches, localRoot, outputRoot);
            Step(outputRoot, "final-review-manual-dispatch");
            AssertFinalReviewManualDispatch(window, pair, pairStore, dispatches, localRoot);
            Step(outputRoot, "unsaved-draft-manual-admission");
            AssertUnsavedDraftManualAdmission(window, pair, pairStore, browser, dispatches, localRoot);
            Step(outputRoot, "folder-accessibility");
            AssertFolderAccessibility(window, pair, browser, localRoot, outputRoot);
            Step(outputRoot, "folder-picker-path-and-clicks");
            AssertFolderPickerPathAndClicks(window, pair, browser, stateRoot);
            Step(outputRoot, "theme-and-language");
            AssertThemeAndLanguage(window);
            AssertFirstSyncChoiceSurface(window);
            Capture(window, Path.Combine(outputRoot, "focused-default-editor.png"));
            Step(outputRoot, "clean-add-cancel");
            var recommendedRecoveryCalls = 0;
            MainWindow.RecommendedRecoveryFolderForTest = _ =>
            {
                recommendedRecoveryCalls++;
                return Task.FromResult(new DropboxFolderEntry("Recovery",
                    SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recommended-recovery"));
            };
            AssertCleanAddCancel(window, pair, pairStore, browser, localRoot,
                () => recommendedRecoveryCalls);
            Step(outputRoot, "stale-repair");
            AssertStaleRepair(window, pair, browser, stateRoot);
            Step(outputRoot, "final-review-dropbox-replacement");
            AssertFinalReviewDropboxReplacement(window, pair, pairStore, browser, localRoot);
            Capture(window, Path.Combine(outputRoot, "focused-stale-repair.png"));

            Step(outputRoot, "guided-sync-correction");
            foreach (var accepted in GuidedSyncCorrectionContract.Run(app, window, pair, pairStore, browser,
                         dispatches, localRoot, stateRoot, outputRoot))
                Passed.Add(accepted);

            Step(outputRoot, "external-cli-saved-editors");
            AssertExternalCliSavedEditors(app, pair, connection, browser, stateRoot);

            Step(outputRoot, "version-identity");
            var versions = AssertVersionIdentity(expectedSourceRevision);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--focused-ui-acceptance-contract",
                cases = Passed,
                versions,
                isolation,
                renders = Directory.GetFiles(outputRoot, "focused-*.png").Select(Path.GetFileName).Order().ToArray(),
                dispatchCountAfterBlockedEntryPoints = dispatches.Count
            }));
            return 0;
        }
        finally
        {
            MainWindow.PairHealthLoaderForTest = null;
            MainWindow.MonitorProjectionLoaderForTest = null;
            MainWindow.PairRunDispatcherForTest = null;
            MainWindow.PairBindingForTest = null;
            MainWindow.DropboxConnectionCheckForTest = null;
            MainWindow.DropboxConnectionStatusLoaderForTest = null;
            MainWindow.DropboxReconnectForTest = null;
            MainWindow.DropboxDisconnectForTest = null;
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            MainWindow.BootstrapApprovalForTest = null;
            MainWindow.BootstrapActionPlanForTest = null;
            MainWindow.BootstrapReviewLoaderForTest = null;
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            MainWindow.ActivityProjectionLoaderForTest = null;
            MainWindow.PendingQueueCountLoaderForTest = null;
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.MonitorStopperForTest = null;
            MainWindow.UnsavedDraftRunChoiceForTest = null;
            MainWindow.PairFolderBrowserFactoryForTest = null;
            MainWindow.RecommendedRecoveryFolderForTest = null;
            MainWindow.RecoveryFolderDialogForTest = null;
            MainWindow.LocalRootNodeFactoryForTest = null;
            MainWindow.PairHealthTimeoutForTest = null;
            MainWindow.ExitCheckpointWaitForTest = null;
            MainWindow.SuppressLoadedStartupForTest = false;
            window?.RequestProductExit();
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(priorSynchronizationContext);
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    internal static MainWindow CreateIsolatedWindow(PilotStateAuthority authority, ThemeManager theme)
        => CreateIsolatedWindow(authority, theme,
            new IsolatedRuntime(authority, allowControlledCredentialInput: true));

    private static MainWindow CreateIsolatedWindow(PilotStateAuthority authority, ThemeManager theme,
        IsolatedRuntime runtime)
    {
        try
        {
            return new MainWindow(authority, theme, runtime.HttpClient, runtime.Host)
            {
                Width = 1240,
                Height = 790,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    private static void AssertExternalCliSavedEditors(App app, SyncPair source,
        DropboxConnectionBinding connection, IDropboxFolderBrowserService browser, string stateRoot)
    {
        // SaveNewAsync normalizes its persisted copy; it cannot replace this immutable input.
        // Bind both editors to the same normalized account identity that the real store retains.
        connection = connection.Normalize();
        var failures = new List<Exception>();
        foreach (var legacySave in new[] { true, false })
        foreach (var dirty in new[] { false, true })
        {
            var label = "M23 " + (legacySave ? "legacy" : "endpoint") + (dirty ? " dirty" : " clean");
            try
            {
                var caseRoot = Path.Combine(stateRoot, "external-cli-editors",
                    (legacySave ? "legacy" : "endpoint") + (dirty ? "-dirty" : "-clean"));
                using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
                using var caseTheme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
                caseTheme.Initialize();
                new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(connection).GetAwaiter().GetResult();
                var retained = source with { ConnectionId = connection.ConnectionId, PolicyRevision = 1 };
                if (!legacySave)
                {
                    var session = SyncPairEditorSession.ForExistingPair(retained, 1, out _,
                        [new SyncVerifiedAccountBinding(SyncProviders.Dropbox, connection.ConnectionId,
                            connection.RootNamespaceId, false)]);
                    var endpoints = session.ExtractDraft(retained).Endpoints ??
                        throw new InvalidDataException("The explicit endpoint input is incomplete.");
                    retained = SyncEndpointPairDraft.Create(endpoints, retained.Name, retained) with { Id = retained.Id };
                }
                new SyncPairStore(instance.StateAuthority).SaveAsync(new SyncPairSet([retained], retained.Id))
                    .GetAwaiter().GetResult();
                using var runtime = new IsolatedRuntime(instance.StateAuthority, allowControlledCredentialInput: true);
                retained = runtime.Host.LoadPairsAsync().GetAwaiter().GetResult().Pairs.Single();
                var editor = CreateIsolatedWindow(instance.StateAuthority, caseTheme, runtime);
                try
                {
                    editor.Show();
                    NavigateToPairs(editor);
                    editor.PrepareExistingPairEditorForTest(retained);
                    UseCompactLocalTree(editor, retained.LocalRoot);
                    PumpTask(editor, editor.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
                    PumpTask(editor, editor.ExpandDropboxTreeForTestAsync(retained.DropboxRoot));
                    editor.ExpandLocalTreeForTest(retained.LocalRoot);
                    Pump(editor);
                    if (legacySave)
                    {
                        // Controlled input to the existing compatibility fallback: the retained
                        // two-tree draft still names the physical roots, while the explicit strip
                        // cannot name both sides. Keep the real opening session/revision intact.
                        var opening = (SyncPairEditorSession)typeof(MainWindow).GetField("_endpointSession",
                            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                        opening.SetCloudFolder(SyncPairSide.Right, null, null);
                    }
                    if ((editor.EndpointDraft()?.Endpoints is null) != legacySave ||
                        Invoke(editor, "CurrentPairEditorHasUnsavedChanges") as bool? != false)
                        throw new InvalidDataException(label + ": the opening input is not the exact clean save path.");
                    if (dirty)
                    {
                        ((TextBox)Required(editor, "PairNameBox")).Text = "Keep this unsaved draft";
                        Pump(editor);
                        if (Invoke(editor, "CurrentPairEditorHasUnsavedChanges") as bool? != true)
                            throw new InvalidDataException(label + ": the deliberate name edit is not dirty.");
                    }
                    var draftBefore = SavedEditorSnapshot(editor);
                    var openingRevision = editor.EndpointDraft()?.ExpectedPolicyRevision;
                    using (var bridge = new CliSettingsChecks.RunningAppBridge(app, editor, instance, caseTheme))
                    {
                        bridge.Invoke("pairs", "disable", "--pair", retained.Id, "--yes");
                        PumpUntil(editor, () => editor.PairRows.Single(row => row.Id == retained.Id).Enabled == false,
                            label + ": the actual bridge post-command readback did not refresh its row.");
                        var newer = PumpTask(editor, runtime.Host.LoadPairsAsync()).Pairs.Single();
                        if (newer.Enabled || newer.PolicyRevision <= retained.PolicyRevision)
                            throw new InvalidDataException(label + ": CLI did not commit a genuinely newer policy.");
                        var persistedAfterCli = JsonSerializer.Serialize(newer);
                        if (!dirty)
                        {
                            if (((CheckBox)Required(editor, "PairEnabledBox")).IsChecked != false ||
                                Invoke(editor, "CurrentPairEditorHasUnsavedChanges") as bool? != false ||
                                editor.EndpointDraft()?.ExpectedPolicyRevision != newer.PolicyRevision)
                                throw new InvalidDataException(label +
                                    ": clean editor did not reload the new CLI policy/baseline without false dirty state.");
                            Passed.Add(label + " actual external CLI policy reload remains clean at the new revision");
                        }
                        else
                        {
                            if (SavedEditorSnapshot(editor) != draftBefore ||
                                editor.EndpointDraft()?.ExpectedPolicyRevision != openingRevision ||
                                Invoke(editor, "CurrentPairEditorHasUnsavedChanges") as bool? != true)
                                throw new InvalidDataException(label + ": external CLI change replaced the dirty opening draft.");
                            if ((editor.EndpointDraft()?.Endpoints is null) != legacySave)
                                throw new InvalidDataException(label + ": stale save would take the wrong production path.");
                            var saved = PumpTask(editor, (Task<bool>)Invoke(editor, "SavePairAsync")!);
                            if (saved || !((TextBlock)Required(editor, "PairEditorStatusText")).Text
                                    .Contains("changed", StringComparison.OrdinalIgnoreCase) ||
                                SavedEditorSnapshot(editor) != draftBefore ||
                                JsonSerializer.Serialize(PumpTask(editor, runtime.Host.LoadPairsAsync()).Pairs.Single()) != persistedAfterCli)
                                throw new InvalidDataException(label + ": stale save was not refused without overwriting the newer policy/draft.");
                            Passed.Add(label + " actual stale Save refuses and preserves both draft and newer CLI policy");
                        }
                    }
                    if (runtime.Transport.DeniedRequests != 0 || runtime.Credentials.Writes != 0 ||
                        runtime.Credentials.Deletes != 0 || runtime.DeniedAuthorizationAttempts != 0)
                        throw new InvalidDataException(label + ": this no-provider policy proof crossed an isolated effect boundary.");
                }
                finally { editor.RequestProductExit(); }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(label + ": " + error.GetType().Name + ": " + error.Message);
                failures.Add(new InvalidDataException(label + " original sequence failed.", error));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("M23 original saved-editor sequences remain unverified/failed.", failures);
    }

    private static string SavedEditorSnapshot(MainWindow window) => JsonSerializer.Serialize(new
    {
        PhysicalDraft = CompleteDraftSnapshot(window),
        Name = ((TextBox)Required(window, "PairNameBox")).Text,
        Enabled = ((CheckBox)Required(window, "PairEnabledBox")).IsChecked,
        Priority = ((TextBox)Required(window, "PairPriorityBox")).Text,
        Direction = ((ComboBox)Required(window, "SyncTypeBox")).SelectedIndex,
        Revision = window.EndpointDraft()?.ExpectedPolicyRevision
    });

    private static object AssertFixtureIsolation(PilotStateAuthority authority, ThemeManager theme, SyncPair pair)
    {
        using var runtime = new IsolatedRuntime(authority);
        var window = CreateIsolatedWindow(authority, theme, runtime);
        try
        {
            if (!ReferenceEquals(window.AuthorityHost, runtime.Host))
                throw new InvalidDataException("The isolated window did not retain its supplied Host.");
            try
            {
                _ = new MainWindow(authority, theme, null!, runtime.Host);
                throw new InvalidDataException("The isolated constructor accepted a missing transport.");
            }
            catch (ArgumentNullException error) when (error.ParamName == "httpClient") { }
            try
            {
                _ = new MainWindow(authority, theme, runtime.HttpClient, null!);
                throw new InvalidDataException("The isolated constructor accepted a missing Host.");
            }
            catch (ArgumentNullException error) when (error.ParamName == "authorityHost") { }
            Passed.Add("Q2 explicit constructor rejects missing transport or Host without production fallback");
            // No connection-status or browser action hook is installed. Exercise the actual window
            // and Host paths that would otherwise reach Windows Credential Manager and the provider.
            PumpTask(window, window.RefreshConnectionStateForTestAsync(verifyLive: true));
            var status = PumpTask(window, runtime.Host.GetDropboxConnectionStatusAsync(verifyLive: true));
            if (status.HasProtectedCredential || status.LiveVerified || runtime.Credentials.DropboxReads == 0 ||
                runtime.Transport.DeniedRequests != 0)
                throw new InvalidDataException("The unhooked account request escaped fixture credential denial.");
            try
            {
                _ = runtime.Host.CreateFolderBrowserAsync().GetAwaiter().GetResult();
                throw new InvalidDataException("The unhooked folder request accepted unavailable fixture credentials.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("credential", StringComparison.OrdinalIgnoreCase))
            {
                Passed.Add("Q2 unhooked window/Host provider request denied before network or protected credentials");
            }
            var googleStore = new GoogleDriveConnectionStore(authority);
            if (googleStore.Exists)
                throw new InvalidDataException("The isolated Google credential proof requires fresh local metadata.");
            var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
                "", "focused-ui-google-permission", "focused-ui-google@example.invalid", "Focused UI Google",
                GoogleDriveProviderConfiguration.RequiredScopes).Normalize();
            try
            {
                PumpTask(window, googleStore.SaveAsync(google));
                var googleStatus = PumpTask(window,
                    window.AuthorityHost.GetAccountStatusAsync(verifyLive: true, connectionId: google.ConnectionId));
                if (googleStatus.HasProtectedCredential || googleStatus.LiveVerified ||
                    runtime.Credentials.GoogleReads != 1 || runtime.Transport.DeniedRequests != 0 ||
                    runtime.GoogleEndpoint.Calls != 0)
                    throw new InvalidDataException("The unhooked Google account request escaped fixture credential denial.");
                Passed.Add("Q2 unhooked retained Google account reads only the injected denying credential provider");
            }
            finally
            {
                // Restore the isolated input through its existing metadata store; no credential or
                // provider deletion is involved, and the later full UI keeps its original account set.
                PumpTask(window, googleStore.DeleteAsync());
            }
            if (googleStore.Exists)
                throw new InvalidDataException("The isolated Google account metadata was not restored to absent.");
            try
            {
                _ = runtime.Host.ConnectGoogleDriveAsync(_ =>
                    throw new InvalidDataException("The isolated Host opened a real authorization page."))
                    .GetAwaiter().GetResult();
                throw new InvalidDataException("The isolated Host accepted a production Google authorization endpoint.");
            }
            catch (InvalidOperationException error) when (error.Message == "Google authorization is not available on this runtime.")
            {
                Passed.Add("Q2 injected Google credential/authorization boundary has no production fallback");
            }
            var browserOpens = 0;
            Task DenyBrowserOpen(Uri _) { browserOpens++; throw new InvalidDataException("The fixture opened authorization."); }
            foreach (var authorization in new Func<Task>[]
                     {
                         async () => _ = await window.AuthorityHost.ConnectDropboxAsync(DenyBrowserOpen),
                         async () => _ = await window.AuthorityHost.ReauthorizeDropboxAsync(DenyBrowserOpen)
                     })
            {
                try
                {
                    PumpTask(window, authorization());
                    throw new InvalidDataException("An isolated Dropbox authorization was not cancelled.");
                }
                catch (OperationCanceledException) { }
            }
            if (browserOpens != 0 || runtime.DeniedAuthorizationAttempts != 2 ||
                runtime.Host.PendingAuthorization is not null)
                throw new InvalidDataException("Authorization isolation escaped the actual Host cancellation boundary.");
            Passed.Add("Q2 actual Host connect and reauthorization cancel before browser, listener, credentials or HTTP");
            try
            {
                _ = DropboxOAuth.GetCurrentAccountAsync(runtime.HttpClient, "fixture-access-input",
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
                throw new InvalidDataException("An unexpected provider request escaped the isolated transport.");
            }
            catch (InvalidOperationException error) when (error.Message == DenyingTransport.Denial)
            {
                Passed.Add("Q2 unexpected production-adapter HTTP request denied by injected transport");
            }
            var retained = runtime.Host.LoadPairsAsync().GetAwaiter().GetResult();
            if (!retained.Pairs.Any(item => item.Id == pair.Id) || runtime.Transport.DeniedRequests != 1 ||
                runtime.Credentials.GoogleReads != 1 ||
                runtime.Credentials.Writes != 0 || runtime.Credentials.Deletes != 0 ||
                runtime.GoogleEndpoint.Calls != 0)
                throw new InvalidDataException("Fixture isolation changed local Host success or performed an unexpected effect.");
            window.LoadPairSetForTest(retained);
            window.Show();
            window.PrepareExistingPairEditorForTest(pair);
            var browser = new HierarchyBrowser();
            PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            PumpTask(window, window.ExpandDropboxTreeForTestAsync(pair.DropboxRoot));
            if (!DropboxDescendants(window.DropboxRootNodes).Any(item => item.Path == "/RemoteName/Match"))
                throw new InvalidDataException("The controlled folder action failed under the isolated constructor.");
            Passed.Add("Q2 legitimate retained-pair Host read and controlled window folder action succeed");
            using var connectedRuntime = new IsolatedRuntime(authority, allowControlledCredentialInput: true);
            var connectedWindow = CreateIsolatedWindow(authority, theme, connectedRuntime);
            try
            {
                if (!PumpTask(connectedWindow, connectedWindow.AuthorityHost.IsDropboxConnectedAsync()))
                    throw new InvalidDataException("The controlled retained account lost its actual Host connected state.");
                PumpTask(connectedWindow, connectedWindow.RefreshConnectionStateForTestAsync(verifyLive: false));
                var localStatus = PumpTask(connectedWindow,
                    connectedRuntime.Host.GetDropboxConnectionStatusAsync(verifyLive: false));
                if (((TextBlock)Required(connectedWindow, "HeaderState")).Text != "Dropbox connected" ||
                    !localStatus.HasProtectedCredential || localStatus.Binding?.AccountId != "dbid:focused-ui" ||
                    connectedRuntime.Transport.DeniedRequests != 0)
                    throw new InvalidDataException("The real local credential checkpoint was not connected before live verification.");
                PumpTask(connectedWindow, connectedWindow.ConnectDropboxFromUiForTestAsync());
                var duplicateConnect = (Button)Required(connectedWindow, "ConnectionConnectButton");
                var header = (TextBlock)Required(connectedWindow, "HeaderState");
                string ConnectedDiagnostic() => $"header={header.Text}; health=" +
                    ((TextBlock)Required(connectedWindow, "ConnectionHealthText")).Text +
                    $"; duplicateEnabled={duplicateConnect.IsEnabled}; visibility={duplicateConnect.Visibility}; " +
                    $"help={AutomationProperties.GetHelpText(duplicateConnect)}; " +
                    $"deniedHttp={connectedRuntime.Transport.DeniedRequests}; " +
                    $"authorization={connectedRuntime.DeniedAuthorizationAttempts}; " +
                    $"writes={connectedRuntime.Credentials.Writes}; deletes={connectedRuntime.Credentials.Deletes}";
                try
                {
                    PumpUntil(connectedWindow, () => header.Text == "Dropbox needs attention" &&
                        connectedRuntime.Transport.DeniedRequests > 0 && !duplicateConnect.IsEnabled &&
                        duplicateConnect.Visibility == Visibility.Collapsed,
                        "The deliberately denied live verification did not reach its retained-credential terminal state.");
                }
                catch (TimeoutException error)
                { throw new TimeoutException(error.Message + " " + ConnectedDiagnostic(), error); }
                var retainedStatus = PumpTask(connectedWindow,
                    connectedRuntime.Host.GetDropboxConnectionStatusAsync(verifyLive: false));
                if (!retainedStatus.HasProtectedCredential || retainedStatus.Binding?.AccountId != "dbid:focused-ui" ||
                    connectedWindow.AuthorityHost.PendingAuthorization is not null ||
                    connectedRuntime.DeniedAuthorizationAttempts != 0 || connectedRuntime.Credentials.Writes != 0 ||
                    connectedRuntime.Credentials.Deletes != 0 ||
                    ((Button)Required(connectedWindow, "ConnectButton")).Content?.ToString() != "Connection" ||
                    !AutomationProperties.GetHelpText(duplicateConnect).Contains("already connected", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The controlled retained account lost the duplicate-authorization guard. " +
                        ConnectedDiagnostic());
                Passed.Add("Q2 controlled account input preserves the actual window connected/duplicate-authorization guard");
            }
            finally { connectedWindow.RequestProductExit(); }
            return new
            {
                runtime.Credentials.DropboxReads,
                runtime.Credentials.GoogleReads,
                runtime.Credentials.Writes,
                runtime.Credentials.Deletes,
                deniedHttpRequests = runtime.Transport.DeniedRequests,
                googleEndpointCalls = runtime.GoogleEndpoint.Calls,
                deniedAuthorizationAttempts = runtime.DeniedAuthorizationAttempts,
                browserOpens,
                credentialProvider = runtime.Credentials.GetType().Name,
                transportHandler = runtime.Transport.GetType().Name,
                suppliedHostRetained = true
            };
        }
        finally
        {
            window.RequestProductExit();
        }
    }

    private sealed class IsolatedRuntime : IDisposable
    {
        internal readonly DenyingCredentials Credentials;
        internal readonly DenyingTransport Transport = new();
        internal readonly DenyingGoogleEndpoint GoogleEndpoint = new();
        internal HttpClient HttpClient { get; }
        internal SyncAuthorityRuntimeHost Host { get; }
        private int _deniedAuthorizationAttempts;
        internal int DeniedAuthorizationAttempts => Volatile.Read(ref _deniedAuthorizationAttempts);

        internal IsolatedRuntime(PilotStateAuthority authority, bool allowControlledCredentialInput = false)
        {
            Credentials = new DenyingCredentials(allowControlledCredentialInput);
            HttpClient = new HttpClient(Transport);
            Host = new SyncAuthorityRuntimeHost(authority, HttpClient, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority), Credentials,
                new SyncAuthorityDatabase(Path.Combine(Path.GetDirectoryName(authority.StateRoot)!,
                    "vNext", "state", "syncshark.db")), googleDriveCredentialStore: Credentials,
                googleDriveTokenEndpoint: GoogleEndpoint);
            // OAuth opens a browser independently of HttpClient. Cancel the exact attempt through the
            // real Host controller synchronously, before its coordinator enters the authorization gate.
            Host.AuthorizationAttemptChanged += () =>
            {
                if (Host.PendingAuthorization is { Status: SyncPendingAuthorizationSnapshot.AwaitingAuthorizationPhase } pending &&
                    Host.RequestAuthorizationAttemptCancellation(pending.AttemptId))
                    Interlocked.Increment(ref _deniedAuthorizationAttempts);
            };
        }

        public void Dispose()
        {
            Host.Dispose();
            HttpClient.Dispose();
        }
    }

    private sealed class DenyingTransport : HttpMessageHandler
    {
        internal const string Denial = "The focused fixture denied an unexpected provider HTTP request.";
        private int _deniedRequests;
        internal int DeniedRequests => Volatile.Read(ref _deniedRequests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deniedRequests);
            throw new InvalidOperationException(Denial);
        }
    }

    private sealed class DenyingCredentials(bool allowControlledCredentialInput)
        : IDropboxRefreshTokenStore, IGoogleDriveRefreshTokenStore
    {
        private int _dropboxReads;
        private int _googleReads;
        private int _writes;
        private int _deletes;
        internal int DropboxReads => Volatile.Read(ref _dropboxReads);
        internal int GoogleReads => Volatile.Read(ref _googleReads);
        internal int Writes => Volatile.Read(ref _writes);
        internal int Deletes => Volatile.Read(ref _deletes);
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) =>
            allowControlledCredentialInput && binding.AccountId == "dbid:focused-ui" &&
            binding.RootNamespaceId == "root-focused-ui" && binding.HomeNamespaceId == "home-focused-ui"
                ? new ControlledCredentialInput(this) : this;
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _dropboxReads);
            return Task.FromResult<string?>(null);
        }
        public Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _googleReads);
            return Task.FromResult<string?>(null);
        }
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) => DenyWrite();
        public Task WriteAsync(string connectionId, string refreshToken,
            CancellationToken cancellationToken = default) => DenyWrite();
        private Task DenyWrite()
        {
            Interlocked.Increment(ref _writes);
            throw new InvalidOperationException("The focused fixture denied a credential write.");
        }
        public Task DeleteAsync(CancellationToken cancellationToken = default) => DenyDelete();
        public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default) => DenyDelete();
        private Task DenyDelete()
        {
            Interlocked.Increment(ref _deletes);
            throw new InvalidOperationException("The focused fixture denied a credential deletion.");
        }

        // This is explicit synthetic input for the one retained fixture account, never an OS store.
        // Unknown accounts stay denied; every write/delete remains denied; transport and authorization
        // are independently closed even when the controlled account is presented as connected.
        private sealed class ControlledCredentialInput(DenyingCredentials owner) : IDropboxRefreshTokenStore
        {
            public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._dropboxReads);
                return Task.FromResult<string?>("focused-ui-controlled-refresh-input");
            }
            public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
                owner.DenyWrite();
            public Task DeleteAsync(CancellationToken cancellationToken = default) => owner.DenyDelete();
        }
    }

    private sealed class DenyingGoogleEndpoint : IGoogleDriveTokenEndpoint
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<GoogleDriveTokenResult> RefreshAsync(string clientId, string refreshToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The focused fixture denied a Google token request.");
        }
        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The focused fixture denied a Google account request.");
        }
    }

    private static void AssertFirstSyncChoiceSurface(MainWindow window)
    {
        _ = Required(window, "BootstrapItemFilter");
        _ = Required(window, "BootstrapTypeFilter");
        _ = Required(window, "BootstrapChoiceSummary");
        var row = new BootstrapRow("pair", "folder/report.pdf", "Local only", "Upload",
            "Copy to Dropbox", "1 KiB", "—", "2026-09-12", "Unavailable", "Pair", "file", "PDF");
        if (!row.CanChooseAction || !row.AvailableActions.SequenceEqual(["Upload", "Skip for now"]))
            throw new InvalidDataException("The first-sync Upload row does not expose the exact safe choices.");
        row.SelectedAction = "Skip for now";
        if (row.Effect != "Leave unchanged and keep waiting")
            throw new InvalidDataException("Skip for now does not explain that the item remains unchanged and pending.");
        var fixedRow = new BootstrapRow("pair", "same.txt", "Identical", "No change",
            "Record as already in sync", "1 KiB", "1 KiB", "2026-09-12", "2026-09-12");
        if (fixedRow.CanChooseAction || fixedRow.AvailableActions.Count != 1)
            throw new InvalidDataException("An identical first-sync row exposed an inapplicable action choice.");
        Passed.Add("TC-UI-14 first-sync row actions and item/type filters");
    }

    private static void AssertRecommendedRecoveryPresentation(MainWindow window, SyncPair pair,
        HierarchyBrowser folderBrowser)
    {
        PumpTask(window, window.UseRecommendedRecoveryForTestAsync());
        if (((TextBox)Required(window, "RecoveryRootBox")).Text !=
            SyncDropboxArtifactPaths.RecoveryContainerPath ||
            !((TextBlock)Required(window, "PairEditorStatusText")).Text.Contains(
                SyncDropboxArtifactPaths.RecoveryContainerPath, StringComparison.Ordinal))
            throw new InvalidDataException("The recommended Recovery path was not presented from the real window action.");

        MainWindow.RecommendedRecoveryFolderForTest = _ => Task.FromResult(
            new DropboxFolderEntry("Elsewhere", "/Elsewhere", "id:elsewhere"));
        window.PrepareExistingPairEditorForTest(pair);
        var bindDispatches = 0;
        MainWindow.PairBindingForTest = _ =>
        {
            bindDispatches++;
            throw new InvalidOperationException("The invalid Recovery selection reached pair binding.");
        };
        PumpTask(window, window.UseRecommendedRecoveryForTestAsync());
        if (((TextBox)Required(window, "RecoveryRootBox")).Text.Length != 0 ||
            ((TextBlock)Required(window, "RecoveryFolderIdText")).Text != "Not selected" ||
            window.HasSelectedRecoveryForTest ||
            ((Button)Required(window, "SavePairButton")).IsEnabled ||
            !((TextBlock)Required(window, "PairEditorStatusText")).Text.StartsWith(
                "Recommended Recovery could not be prepared:", StringComparison.Ordinal))
            throw new InvalidDataException("A noncontained recommended Recovery path remained in hidden or save-ready UI state.");
        PumpTask(window, window.SavePairForTestAsync());
        if (bindDispatches != 0 || !((TextBlock)Required(window, "PairEditorStatusText")).Text.Contains(
                "Recovery", StringComparison.Ordinal))
            throw new InvalidDataException("The invalid Recovery state reached pair binding through the real Save path.");

        MainWindow.RecoveryFolderDialogForTest = dialog =>
        {
            if (dialog.AllowsFolderCreationForTest ||
                dialog.CanSelectFolderForTest(new DropboxFolderEntry("Elsewhere", "/Elsewhere", "id:elsewhere")) ||
                !dialog.CanSelectFolderForTest(new DropboxFolderEntry("Recovery",
                    SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery")))
                throw new InvalidDataException("The production Recovery chooser was not exact-container selection-only.");
            ((TextBox)Required(dialog, "NewFolderName")).Text = "Must not be created";
            ((Button)Required(dialog, "CreateFolderButton"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (folderBrowser.CreateCalls != 0)
                throw new InvalidDataException("The restricted Recovery chooser reached provider folder creation.");
            dialog.Close();
            return new DropboxFolderEntry("Elsewhere", "/Elsewhere", "id:elsewhere");
        };
        ((Button)Required(window, "BrowseRecoveryButton"))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => ((TextBlock)Required(window, "PairEditorStatusText")).Text.StartsWith(
                "The recovery folder could not be selected:", StringComparison.Ordinal),
            "the noncanonical Recovery chooser rejection");
        if (window.HasSelectedRecoveryForTest || ((TextBox)Required(window, "RecoveryRootBox")).Text.Length != 0 ||
            ((Button)Required(window, "SavePairButton")).IsEnabled || folderBrowser.CreateCalls != 0)
            throw new InvalidDataException("The noncanonical Recovery chooser changed hidden/save/provider state.");

        MainWindow.RecoveryFolderDialogForTest = dialog =>
        {
            Exception? dialogFailure = null;
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(15)
            };
            timer.Tick += (_, _) =>
            {
                try
                {
                    var root = dialog.Roots.Single();
                    if (!root.IsLoaded || root.IsLoading) return;
                    var recovery = root.Children.FirstOrDefault(node =>
                        node.Entry?.Path == SyncDropboxArtifactPaths.RecoveryContainerPath);
                    if (recovery is null)
                        throw new InvalidDataException("The canonical Recovery folder was absent from the real browser result.");
                    var tree = (TreeView)Required(dialog, "FolderTree");
                    dialog.UpdateLayout();
                    var item = FindDescendants<TreeViewItem>(tree).FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.DataContext, recovery));
                    if (item is null) return;
                    item.IsSelected = true;
                    item.BringIntoView();
                    dialog.UpdateLayout();
                    ((Button)Required(dialog, "SelectFolderButton"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    timer.Stop();
                }
                catch (Exception exception)
                {
                    dialogFailure = exception;
                    timer.Stop();
                    dialog.Close();
                }
            };
            timer.Start();
            var dialogResult = dialog.ShowDialog();
            timer.Stop();
            if (dialogFailure is not null)
                throw new InvalidDataException("The real restricted Recovery browser failed.", dialogFailure);
            if (dialogResult != true || dialog.SelectedFolder?.Path !=
                SyncDropboxArtifactPaths.RecoveryContainerPath)
                throw new InvalidDataException("The real restricted Recovery browser did not commit its canonical selection.");
            return dialog.SelectedFolder;
        };
        ((Button)Required(window, "BrowseRecoveryButton"))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => ((TextBox)Required(window, "RecoveryRootBox")).Text ==
                                SyncDropboxArtifactPaths.RecoveryContainerPath,
            "the canonical Recovery chooser selection");
        SyncPairBindingRequest? selectedRequest = null;
        MainWindow.PairBindingForTest = request =>
        {
            selectedRequest = request;
            throw new InvalidOperationException("Expected fixture stop after observing the exact binding request.");
        };
        PumpTask(window, window.SavePairForTestAsync());
        if (selectedRequest?.RecoveryFolder is not { } selectedRecovery ||
            selectedRecovery.Path != SyncDropboxArtifactPaths.RecoveryContainerPath ||
            selectedRecovery.FolderId != "id:recovery-from-chooser")
            throw new InvalidDataException("The canonical Recovery chooser selection did not reach the real Save path.");
        MainWindow.PairBindingForTest = null;
        MainWindow.RecoveryFolderDialogForTest = null;
        MainWindow.RecommendedRecoveryFolderForTest = _ => Task.FromResult(
            new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
                "id:recommended-recovery"));
        Passed.Add("SS-CONTAIN-003 Recovery UI recommendation and chooser constrain create/select/save state");
    }

    private static void AssertConnectionStartupAndConnectAction(MainWindow window)
    {
        var connect = (Button)Required(window, "ConnectButton");
        var connectOnPage = (Button)Required(window, "ConnectionConnectButton");
        if (connect.Content?.ToString() != "Checking connection…" || connect.IsEnabled ||
            connectOnPage.Visibility != Visibility.Collapsed)
            throw new InvalidDataException("The initial connection UI claimed an unverified disconnected state.");

        PumpTask(window, window.RefreshConnectionStateForTestAsync(verifyLive: false));
        if (connect.Content?.ToString() != "Connection" ||
            ((TextBlock)Required(window, "HeaderState")).Text != "Dropbox connected" ||
            connectOnPage.Visibility != Visibility.Collapsed)
            throw new InvalidDataException(
                "A trusted local Dropbox account was presented as unhealthy while live verification was pending.");

        PumpTask(window, window.ConnectDropboxFromUiForTestAsync());
        if (connect.Content?.ToString() != "Connection" ||
            ((TextBlock)Required(window, "HeaderState")).Text != "Dropbox connected" ||
            ((TextBlock)Required(window, "PairConnectionSummary")).Text.Contains("disconnected",
                StringComparison.OrdinalIgnoreCase) ||
            ((Border)Required(window, "ConnectionsPage")).Visibility != Visibility.Visible)
            throw new InvalidDataException(
                "An already-connected account was not resolved from authoritative local state before the Connect action.");
        Passed.Add("TC-UI-10.c connected startup never presents duplicate Dropbox authorization");
    }

    private static void AssertSharedEditorAndStatus(MainWindow window, SyncPair pair)
    {
        var editor = (Border)Required(window, "PairEditorPanel");
        var savedHost = FindDescendants<ContentControl>(window).Single(item =>
            item.Name == "PairRowDetailsHost" && ReferenceEquals(item.Content, editor));
        var row = window.PairRows.Single(item => item.Id == pair.Id);
        row.UpdateHealth(true, "Saved local folder no longer exists. Choose a replacement.");
        var toggle = FindDescendants<Button>(window).First(item => ReferenceEquals(item.DataContext, row) &&
            AutomationProperties.GetName(item).Contains("Expand or collapse", StringComparison.Ordinal));
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (savedHost.Content is not null) throw new InvalidDataException("Collapse retained the editor in the row.");
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!FindDescendants<ContentControl>(window).Any(item =>
                item.Name == "PairRowDetailsHost" && ReferenceEquals(item.Content, editor)))
            throw new InvalidDataException("Re-expanding the saved pair did not restore the shared editor.");
        Pump(window);
        var pairList = (DataGrid)Required(window, "PairList");
        var statusColumn = pairList.Columns.Single(column =>
            string.Equals(column.Header as string, "Sync Status", StringComparison.OrdinalIgnoreCase));
        pairList.ScrollIntoView(row, statusColumn);
        Pump(window);
        var status = FindDescendants<Button>(window).Single(item => ReferenceEquals(item.DataContext, row) &&
            AutomationProperties.GetName(item) == "Open pair sync status");
        var text = FindDescendants<TextBlock>(status).Select(item => item.Text).ToArray();
        if (!text.Contains(row.Status) || !text.Contains(row.StatusSummary))
            throw new InvalidDataException("Status and concise reason did not survive collapse and re-expansion.");
        // Saved clean pairs deliberately hide Save/Discard. Exercise their real dirty state.
        ((System.Windows.Controls.TextBox)Required(window, "PairNameBox")).Text = pair.Name + " edited";
        Pump(window);
        foreach (var name in new[] { "SavePairButton", "CancelPairButton", "EditorSyncNowButton" })
        {
            var action = Required(window, name);
            var bounds = Bounds(action, editor);
            if (!action.IsVisible || bounds.Left < -1 || bounds.Top < -1 ||
                bounds.Right > editor.ActualWidth + 1 || bounds.Bottom > editor.ActualHeight + 1)
                throw new InvalidDataException($"{name} is not reachable inside the saved-pair editor: " +
                    $"visible={action.IsVisible}; bounds={bounds}; editor={editor.ActualWidth}x{editor.ActualHeight}.");
        }
        ((TextBox)Required(window, "PairNameBox")).Text = pair.Name;
        Pump(window);
        ((Button)Required(window, "AddPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var newPairHost = (ContentControl)Required(window, "NewPairEditorHost");
        if (!ReferenceEquals(newPairHost.Content, editor))
            throw new InvalidDataException("Add pair did not use the same in-list editor control.");
        var pairsScroller = (ScrollViewer)Required(window, "PairsScroller");
        PumpUntil(window, () => editor.IsDescendantOf(pairsScroller) && newPairHost.IsVisible,
            "The shared editor did not attach to the Add-pair host.");
        foreach (var name in new[] { "PairEditorTabs", "LocalRootPanel", "DropboxRootPanel", "SavePairButton",
                     "CancelPairButton" })
        {
            var control = Required(window, name);
            control.BringIntoView();
            // Scrolling and the editor's queued viewport reset complete below Render priority.
            // One Render frame is not an observation of the final user-visible position.
            PumpUntil(window, () => IsReachableInViewport(control, pairsScroller),
                name + " did not become reachable after the new-pair viewport settled.");
            var bounds = Bounds(control, editor);
            if (control.Visibility != Visibility.Visible || control.ActualWidth <= 0 || control.ActualHeight <= 0 ||
                bounds.Left < -1 || bounds.Top < -1 || bounds.Right > editor.ActualWidth + 1 ||
                bounds.Bottom > editor.ActualHeight + 1)
                throw new InvalidDataException($"{name} is not reachable inside the new-pair host: " +
                    $"visibility={control.Visibility}; size={control.ActualWidth:0.0}x{control.ActualHeight:0.0}; " +
                    $"bounds={bounds}; editor={editor.ActualWidth:0.0}x{editor.ActualHeight:0.0}.");
            AssertReachableInViewport(control, pairsScroller,
                name + " in new-pair host");
        }
        if (((Button)Required(window, "EditorSyncNowButton")).Visibility != Visibility.Collapsed)
            throw new InvalidDataException("The unsaved Add-pair host exposed Sync now.");
        ((Button)Required(window, "CancelPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.PrepareExistingPairEditorForTest(pair);
        Passed.Add("TC-UI-01.a-b shared editor and status lifecycle");
    }

    private static void AssertResizeSequence(PilotStateAuthority authority, ThemeManager theme, SyncPair pair,
        IDropboxFolderBrowserService browser, string localRoot)
    {
        var window = CreateIsolatedWindow(authority, theme);
        window.Show();
        NavigateToPairs(window);
        window.PrepareExistingPairEditorForTest(pair);
        PumpUntil(window, () => window.PairRows.Any(item => item.Id == pair.Id),
            "The saved pair was not available for resize testing.");
        UseCompactLocalTree(window, localRoot);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(pair.DropboxRoot));
        window.ExpandLocalTreeForTest(localRoot);
        window.UpdateLayout();
        ((System.Windows.Controls.TextBox)Required(window, "PairNameBox")).Text = pair.Name + " resize draft";
        Pump(window);
        var steps = new (WindowState State, double Width, double Height)[]
        {
            (WindowState.Normal, 1240, 790), (WindowState.Maximized, 1240, 790),
            (WindowState.Normal, 1240, 790), (WindowState.Normal, 760, 540),
            (WindowState.Maximized, 760, 540), (WindowState.Normal, 980, 650)
        };
        foreach (var step in steps)
        {
            window.WindowState = WindowState.Normal;
            window.Width = step.Width;
            window.Height = step.Height;
            window.UpdateLayout();
            window.WindowState = step.State;
            window.UpdateLayout();
            // Responsive layout is deliberately queued at Background priority.
            // A synchronous UpdateLayout alone does not execute that callback.
            PumpFor(window, TimeSpan.FromMilliseconds(75));
            window.UpdateLayout();
            var editor = (Border)Required(window, "PairEditorPanel");
            var local = (Border)Required(window, "LocalRootPanel");
            var dropbox = (Border)Required(window, "DropboxRootPanel");
            // WPF reattaches row details and the selected TabItem content on the
            // dispatcher after a window-state transition. Measure the live panes,
            // not the temporarily detached namescope objects retained by XAML.
            try
            {
                PumpUntil(window, () => editor.IsVisible && local.IsDescendantOf(editor) &&
                        dropbox.IsDescendantOf(editor) && local.IsVisible && dropbox.IsVisible,
                    $"The folder panes did not reattach after {step.State} {step.Width}x{step.Height}.");
            }
            catch (TimeoutException exception)
            {
                throw new InvalidDataException(exception.Message +
                    $" Editor visible={editor.IsVisible}; local descendant={local.IsDescendantOf(editor)};" +
                    $" local visible={local.IsVisible}; cloud descendant={dropbox.IsDescendantOf(editor)};" +
                    $" tab={((TabControl)Required(window, "PairEditorTabs")).SelectedIndex};" +
                    $" pairs visible={Required(window, "PairsPage").IsVisible};" +
                    $" editor host={((FrameworkElement?)VisualTreeHelper.GetParent(editor))?.Name}.", exception);
            }
            var left = Bounds(local, editor);
            var right = Bounds(dropbox, editor);
            if (left.IntersectsWith(right))
                throw new InvalidDataException($"Folder panes overlap after {step.State} {step.Width}x{step.Height}.");
            foreach (var name in new[] { "SavePairButton", "CancelPairButton", "FolderSelectionSearchBox",
                         "FolderSelectionFilterBox", "LocalRootTree", "DropboxRootTree" })
            {
                var control = Required(window, name);
                control.BringIntoView();
                window.UpdateLayout();
                if (!control.IsVisible || control.ActualWidth <= 0 || control.ActualHeight <= 0)
                    throw new InvalidDataException($"{name} became unreachable after a window transition.");
                AssertReachableInViewport(control, (ScrollViewer)Required(window, "PairsScroller"),
                    name + " after " + step.State + " " + step.Width + "x" + step.Height);
            }
            var root = (Grid)Required(window, "PairRootGrid");
            var scroller = (ScrollViewer)Required(window, "PairsScroller");
            if (root.ActualWidth > scroller.ViewportWidth + 2)
                throw new InvalidDataException($"The editor retained a stale width after {step.State} " +
                    $"{step.Width}x{step.Height}: editor={root.ActualWidth:0.0}, viewport={scroller.ViewportWidth:0.0}.");
            var savedHost = FindDescendants<ContentControl>(window).Single(item =>
                item.Name == "PairRowDetailsHost" && ReferenceEquals(item.Content, Required(window, "PairEditorPanel")));
            if (savedHost.Visibility != Visibility.Visible)
                throw new InvalidDataException("The saved-row editor stopped being the active host during resize.");
        }
        window.RequestProductExit();
        Passed.Add("TC-UI-02.a-b real maximize/restore/narrow sequence");
    }

    private static void AssertSearchAndFilters(MainWindow window, IDropboxFolderBrowserService browser,
        string localRoot, string outputRoot)
    {
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName"));
        var before = PlanSnapshot(window);
        var search = (TextBox)Required(window, "FolderSelectionSearchBox");
        search.Text = "Match";
        var searchTimer = (DispatcherTimer)(typeof(MainWindow).GetField("_folderSelectionSearchTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) ??
            throw new MissingFieldException(nameof(MainWindow), "_folderSelectionSearchTimer"));
        if (!searchTimer.IsEnabled)
            throw new InvalidDataException("Typing in folder search did not schedule a UI refresh.");
        window.ApplyFolderTreeFilterForTest("Match", 0);
        RequireVisible(FindLocal(window, Path.Combine(localRoot, "Match")), true, "local search match");
        RequireVisible(FindDropbox(window, "/RemoteName/Match"), true, "Dropbox search match");
        RequireVisible(FindLocal(window, Path.Combine(localRoot, "LocalOnly")), false, "local search nonmatch");
        RequireVisibleSet(LocalDescendants(window.LocalRootNodes).Where(item => !item.IsPlaceholder),
            [localRoot, Path.Combine(localRoot, "Match")], item => item.Path,
            "local matches plus ancestors");
        RequireVisibleSet(DropboxDescendants(window.DropboxRootNodes).Where(item => !item.IsPlaceholder),
            ["/", "/RemoteName", "/RemoteName/Match", "/Match"], item => item.Path,
            "Dropbox matches plus ancestors");
        if (!((TextBlock)Required(window, "FolderSearchScopeText")).Text.Contains("loaded folders", StringComparison.Ordinal))
            throw new InvalidDataException("Positive search did not disclose its loaded-tree scope.");
        search.Text = "no-such-loaded-folder";
        window.ApplyFolderTreeFilterForTest("no-such-loaded-folder", 0);
        if (!((TextBlock)Required(window, "FolderSearchScopeText")).Text.Contains("No loaded folders match", StringComparison.Ordinal))
            throw new InvalidDataException("No-match search did not show the agreed empty-state copy.");
        Capture(window, Path.Combine(outputRoot, "focused-no-results.png"));
        search.Clear();
        window.ApplyFolderTreeFilterForTest(string.Empty, 0);
        RequireVisible(FindLocal(window, Path.Combine(localRoot, "LocalOnly")), true, "restored local folder");
        if (PlanSnapshot(window) != before) throw new InvalidDataException("Search changed the selection plan.");
        PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName"));

        if (LocalFolderNode.AvailabilityStatusForAttributes(FileAttributes.Normal).Length != 0 ||
            LocalFolderNode.AvailabilityStatusForAttributes(FileAttributes.Offline) != "Online-only" ||
            LocalFolderNode.AvailabilityStatusForAttributes((FileAttributes)0x00040000) != "Online-only" ||
            LocalFolderNode.AvailabilityStatusForAttributes((FileAttributes)0x00400000) != "Online-only")
            throw new InvalidDataException("The local Files On-Demand attribute derivation is incorrect.");
        FindLocal(window, Path.Combine(localRoot, "OnlineOnly"))!.SetStatus("Online-only");
        var derivedUnavailable = FindLocal(window, Path.Combine(localRoot, "DerivedUnavailable"))!;
        if (derivedUnavailable.Status != "Unavailable")
            throw new InvalidDataException("A missing local folder did not derive the Unavailable status.");
        FindDropbox(window, "/RemoteName/Attention")!.SetStatus("Unavailable");
        window.SetGlobalLocalSelectionForTest(Path.Combine(localRoot, "Excluded"), false);
        var filter = (ComboBox)Required(window, "FolderSelectionFilterBox");
        var checks = new (int Index, object[] Positive, object[] Negative, string Name)[]
        {
            (1, [FindLocal(window, Path.Combine(localRoot, "LocalOnly"))!],
                [FindLocal(window, Path.Combine(localRoot, "Match"))!, FindDropbox(window, "/RemoteName/DropboxOnly")!], "local-only"),
            (2, [FindDropbox(window, "/RemoteName/DropboxOnly")!],
                [FindDropbox(window, "/RemoteName/Match")!, FindLocal(window, Path.Combine(localRoot, "LocalOnly"))!], "Dropbox-only"),
            (3, [FindLocal(window, Path.Combine(localRoot, "OnlineOnly"))!],
                [FindLocal(window, Path.Combine(localRoot, "LocalOnly"))!, FindDropbox(window, "/RemoteName/Match")!], "online-only"),
            (4, [FindLocal(window, Path.Combine(localRoot, "Match"))!, FindDropbox(window, "/RemoteName/Match")!],
                [FindLocal(window, Path.Combine(localRoot, "LocalOnly"))!, FindDropbox(window, "/RemoteName/DropboxOnly")!], "mixed"),
            (5, [FindLocal(window, Path.Combine(localRoot, "Excluded"))!, FindDropbox(window, "/RemoteName/Excluded")!],
                [FindLocal(window, Path.Combine(localRoot, "Match"))!, FindDropbox(window, "/RemoteName/Match")!], "excluded"),
            (6, [derivedUnavailable, FindDropbox(window, "/RemoteName/Attention")!],
                [FindLocal(window, Path.Combine(localRoot, "Match"))!, FindDropbox(window, "/RemoteName/Match")!], "needs-attention")
        };
        var selectedPlan = PlanSnapshot(window);
        foreach (var check in checks)
        {
            window.ApplyFolderTreeFilterForTest(string.Empty, check.Index);
            foreach (var positive in check.Positive) RequireVisible(positive, true, check.Name + " positive");
            foreach (var negative in check.Negative) RequireVisible(negative, false, check.Name + " negative");
            if (PlanSnapshot(window) != selectedPlan)
                throw new InvalidDataException($"The {check.Name} filter changed the selection plan.");
        }
        window.ApplyFolderTreeFilterForTest(string.Empty, 0);
        Passed.Add("TC-UI-03.a-c positive/no-result search and seven filters");
    }

    private static void AssertLinkedSelection(MainWindow window, SyncPair pair, SyncPairStore pairStore,
        IDropboxFolderBrowserService browser, List<string> dispatches, string localRoot)
    {
        window.PrepareExistingPairEditorForTest(pair);
        UseCompactLocalTree(window, localRoot);
        var siblingOutside = Path.Combine(Path.GetDirectoryName(localRoot)!, "SiblingOutside");
        var siblingNode = LocalFolderNode.CreateForTest(siblingOutside, []);
        window.LocalRootNodes.Add(siblingNode);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(pair.DropboxRoot));
        window.ExpandLocalTreeForTest(localRoot);
        Pump(window);
        var before = PlanSnapshot(window);
        var storedBefore = JsonSerializer.Serialize(pairStore.Load());
        var dispatchCountBefore = dispatches.Count;
        var localTree = (TreeView)Required(window, "LocalRootTree");
        localTree.BringIntoView();
        Pump(window);
        FindDescendants<ScrollViewer>(localTree).FirstOrDefault()?.ScrollToEnd();
        PumpUntil(window, () => localTree.ItemContainerGenerator.ContainerFromItem(siblingNode) is TreeViewItem,
            "The real local sibling row was not generated after scrolling the hierarchy.");
        var siblingItem = localTree.ItemContainerGenerator.ContainerFromItem(siblingNode) as TreeViewItem ??
            throw new InvalidDataException("The real local sibling row was not generated.");
        siblingItem.BringIntoView();
        Pump(window);
        var siblingCheck = RealizedCheckbox(localTree, () => FindLocal(window, siblingOutside), "local outside sibling");
        siblingCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        var afterSiblingClick = PlanSnapshot(window);
        var siblingStatus = ((TextBlock)Required(window, "FolderSelectionSummary")).Text;
        if (afterSiblingClick != before || !siblingStatus.Contains("different names", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The real local checkbox stranded an unrepresentable sibling promotion. " +
                                           $"before={before}; after={afterSiblingClick}; status={siblingStatus}");
        var dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        var remoteOutside = FindDropbox(window, "/Match")!;
        var dropboxRootItem = dropboxTree.ItemContainerGenerator.ContainerFromItem(window.DropboxRootNodes.Single())
            as TreeViewItem ?? throw new InvalidDataException("The Dropbox hierarchy root was not generated.");
        dropboxRootItem.IsExpanded = true;
        dropboxRootItem.UpdateLayout();
        dropboxTree.BringIntoView();
        Pump(window);
        // The browser sorts Match before the navigation-padding siblings. Scrolling to
        // the end virtualizes that exact target away; reveal its real row at the top.
        FindDescendants<ScrollViewer>(dropboxTree).FirstOrDefault()?.ScrollToTop();
        PumpUntil(window, () => dropboxRootItem.ItemContainerGenerator.ContainerFromItem(remoteOutside) is TreeViewItem,
            "The real Dropbox sibling row was not generated after scrolling the hierarchy.");
        var remoteOutsideItem = dropboxRootItem.ItemContainerGenerator.ContainerFromItem(remoteOutside) as TreeViewItem ??
            throw new InvalidDataException("The real Dropbox sibling row was not generated.");
        remoteOutsideItem.BringIntoView();
        Pump(window);
        var remoteOutsideCheck = RealizedCheckbox(dropboxTree, () => FindDropbox(window, "/Match"), "Dropbox outside sibling");
        remoteOutsideCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        if (PlanSnapshot(window) != before || JsonSerializer.Serialize(pairStore.Load()) != storedBefore ||
            dispatches.Count != dispatchCountBefore)
            throw new InvalidDataException("The real Dropbox checkbox changed persistence, dispatch or a valid plan after rejection.");

        ((Button)Required(window, "AddPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        UseCompactLocalTree(window, localRoot);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        window.ExpandLocalTreeForTest(localRoot);
        PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName"));
        Pump(window);
        localTree = (TreeView)Required(window, "LocalRootTree");
        dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        var localMatch = CurrentLocal(Path.Combine(localRoot, "Match"));
        var dropboxMatch = CurrentDropbox("/RemoteName/Match");
        RealizedCheckbox(localTree, () => FindLocal(window, Path.Combine(localRoot, "Match")), "local Match inclusion")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The linked selection refresh may recycle the cloud containers. Reveal the actual
        // child again before interacting; an offscreen container is not a missing checkbox.
        PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName/Match"));
        Pump(window);
        dropboxMatch = CurrentDropbox("/RemoteName/Match");
        RealizedCheckbox(dropboxTree, () => FindDropbox(window, "/RemoteName/Match"), "refreshed Dropbox Match inclusion")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        if (!FolderTreeSelectionDraft.Equivalent(window.LocalGlobalPlanForTest, window.DropboxGlobalPlanForTest))
            throw new InvalidDataException("The real controls did not establish an equal paired child selection.");

        var localExcluded = CurrentLocal(Path.Combine(localRoot, "Excluded"));
        var dropboxExcluded = CurrentDropbox("/RemoteName/Excluded");
        RealizedCheckbox(localTree, () => FindLocal(window, Path.Combine(localRoot, "Excluded")), "local Excluded sibling inclusion")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        localExcluded = CurrentLocal(Path.Combine(localRoot, "Excluded"));
        dropboxExcluded = CurrentDropbox("/RemoteName/Excluded");
        if (!FolderTreeSelectionDraft.Equivalent(window.LocalGlobalPlanForTest, window.DropboxGlobalPlanForTest) ||
            !string.Equals(window.LocalGlobalPlanForTest?.RootPath, localRoot, StringComparison.OrdinalIgnoreCase) ||
            window.LocalGlobalPlanForTest?.DefaultIncluded != false ||
            localExcluded.IsChecked != true || dropboxExcluded.IsChecked != true)
            throw new InvalidDataException("A real sibling click did not derive a coupled selective LCA.");

        localMatch = CurrentLocal(Path.Combine(localRoot, "Match"));
        RealizedCheckbox(localTree, () => FindLocal(window, Path.Combine(localRoot, "Match")), "local Match exclusion")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        localMatch = CurrentLocal(Path.Combine(localRoot, "Match"));
        dropboxMatch = CurrentDropbox("/RemoteName/Match");
        if (localMatch.IsChecked != false || dropboxMatch.IsChecked != false ||
            FindLocal(window, localRoot)!.IsChecked is not null || FindDropbox(window, "/RemoteName")!.IsChecked is not null)
            throw new InvalidDataException("A real child exclusion did not remain coupled and dashed.");
        dropboxMatch = CurrentDropbox("/RemoteName/Match");
        RealizedCheckbox(dropboxTree, () => FindDropbox(window, "/RemoteName/Match"), "refreshed Dropbox Match restoration")
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        localMatch = CurrentLocal(Path.Combine(localRoot, "Match"));
        dropboxMatch = CurrentDropbox("/RemoteName/Match");
        if (localMatch.IsChecked != true || dropboxMatch.IsChecked != true ||
            !FolderTreeSelectionDraft.Equivalent(window.LocalGlobalPlanForTest, window.DropboxGlobalPlanForTest))
            throw new InvalidDataException("A real opposite-side restoration diverged between the pair trees.");
        ((Button)Required(window, "CancelPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.PrepareExistingPairEditorForTest(pair);
        Passed.Add("TC-UI-05.a-b linked rollback and selection algebra");

        // Every interaction uses a current model and its actual realized template. A linked
        // selection may replace cloud nodes or recycle containers, so neither a cached visual
        // nor a descendant search of the current viewport is authority to skip a real click.
        LocalFolderNode CurrentLocal(string path) => FindLocal(window, path) ??
            throw new InvalidDataException("The current linked local node is absent: " + path);

        DropboxFolderNode CurrentDropbox(string path) => FindDropbox(window, path) ??
            throw new InvalidDataException("The current linked Dropbox node is absent: " + path);

        CheckBox RealizedCheckbox(TreeView tree, Func<object?> currentNode, string description)
        {
            CheckBox? checkbox = null;
            PumpUntil(window, () =>
            {
                var node = currentNode();
                if (node is null) return false;
                Invoke(window, "BringTreeNodeIntoView", tree, node);
                tree.UpdateLayout();
                checkbox = FindDescendants<CheckBox>(tree).FirstOrDefault(item =>
                    ReferenceEquals(item.DataContext, node) && item.IsLoaded && item.IsVisible);
                return checkbox is not null;
            }, "The real linked-selection checkbox was not realized: " + description);
            return checkbox!;
        }
    }

    private static void AssertHealthAndDispatch(MainWindow window, SyncPair pair, SyncPairStore pairStore,
        List<string> dispatches, string localRoot, string outputRoot)
    {
        window.PrepareExistingPairEditorForTest(pair);
        UseCompactLocalTree(window, localRoot);
        window.ExpandLocalTreeForTest(localRoot);
        ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 3;
        Pump(window);
        var checkAgain = FindDescendants<Button>(window).First(item => string.Equals(
            System.Windows.Automation.AutomationProperties.GetName(item), "Check again", StringComparison.OrdinalIgnoreCase));

        void CheckTerminalState(SyncFolderSelectionTree state, string localPrefix, string dropboxPrefix,
            string? reasonFragment, bool blocked)
        {
            var loaderCalls = 0;
            string? loadedPairId = null;
            MainWindow.PairHealthLoaderForTest = (pairId, _) =>
            {
                loaderCalls++;
                loadedPairId = pairId;
                return Task.FromResult(state);
            };
            checkAgain.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            try
            {
                PumpUntil(window, () => ((TextBlock)Required(window, "PairHealthLocalText")).Text.StartsWith(
                    localPrefix, StringComparison.Ordinal) &&
                ((TextBlock)Required(window, "PairHealthDropboxText")).Text.StartsWith(
                    dropboxPrefix, StringComparison.Ordinal) &&
                window.PairKnownUnhealthyForTest(pair.Id) == blocked &&
                ((Button)Required(window, "EditorSyncNowButton")).IsEnabled == !blocked &&
                (reasonFragment is null || ((TextBlock)Required(window, "PairHealthReasonText")).Text.Contains(
                    reasonFragment, StringComparison.OrdinalIgnoreCase)),
                "Check again did not reach the expected terminal health state.");
            }
            catch (TimeoutException exception)
            {
                var localText = ((TextBlock)Required(window, "PairHealthLocalText")).Text;
                var dropboxText = ((TextBlock)Required(window, "PairHealthDropboxText")).Text;
                var reasonText = ((TextBlock)Required(window, "PairHealthReasonText")).Text;
                var knownUnhealthy = window.PairKnownUnhealthyForTest(pair.Id);
                var syncNow = (Button)Required(window, "EditorSyncNowButton");
                var currentState = JsonSerializer.Serialize(new
                {
                    expected = new { pair.Id, localPrefix, dropboxPrefix, reasonFragment, blocked },
                    predicates = new
                    {
                        localPrefixMatches = localText.StartsWith(localPrefix, StringComparison.Ordinal),
                        dropboxPrefixMatches = dropboxText.StartsWith(dropboxPrefix, StringComparison.Ordinal),
                        blockedMatches = knownUnhealthy == blocked,
                        syncNowEnabledMatches = syncNow.IsEnabled == !blocked,
                        reasonMatches = reasonFragment is null || reasonText.Contains(reasonFragment,
                            StringComparison.OrdinalIgnoreCase)
                    },
                    actual = new
                    {
                        localText, dropboxText, reasonText, knownUnhealthy,
                        syncNowEnabled = syncNow.IsEnabled,
                        syncNowVisible = syncNow.IsVisible,
                        overall = ((TextBlock)Required(window, "PairHealthOverallText")).Text,
                        liveSync = ((TextBlock)Required(window, "PairHealthLiveSyncText")).Text,
                        selectedPairId = ((DataGrid)Required(window, "PairList")).SelectedItem is PairRow selected
                            ? selected.Id : null,
                        selectedTab = ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex,
                        editorEnabled = ((FrameworkElement)Required(window, "PairEditorPanel")).IsEnabled,
                        editorHasUnsavedChanges = Invoke(window, "CurrentPairEditorHasUnsavedChanges"),
                        checkAgainEnabled = checkAgain.IsEnabled,
                        checkAgainVisible = checkAgain.IsVisible,
                        checkAgainLoaded = checkAgain.IsLoaded,
                        loaderCalls, loadedPairId
                    }
                });
                // Keep the original five predicates and timeout intact. The snapshot distinguishes
                // stale text expectations from a missing click, unfinished load or actual admission block.
                throw new TimeoutException(exception.Message + " Current UI state: " + currentState, exception);
            }
            var local = ((TextBlock)Required(window, "PairHealthLocalText")).Text;
            var dropbox = ((TextBlock)Required(window, "PairHealthDropboxText")).Text;
            var reason = ((TextBlock)Required(window, "PairHealthReasonText")).Text;
            if (local.Contains("Checking", StringComparison.Ordinal) ||
                dropbox.Contains("Checking", StringComparison.Ordinal) ||
                blocked != window.PairKnownUnhealthyForTest(pair.Id) ||
                blocked == ((Button)Required(window, "EditorSyncNowButton")).IsEnabled ||
                reasonFragment is not null && !reason.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Terminal health state is not actionable: local={local}; " +
                    $"Dropbox={dropbox}; reason={reason}; blocked={blocked}.");
            if (blocked) ExerciseBlockedLaunchSurfaces(window, pair, pairStore, dispatches);
        }

        CheckTerminalState(Health(pair, true, true), "This computer: available",
            "Cloud: available", null, blocked: false);
        CheckTerminalState(Health(pair, false, true), "This computer: unavailable\n", "Cloud: available",
            "Saved local folder no longer exists", blocked: true);
        ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 1;
        Pump(window);
        if (((Border)Required(window, "PairPreviewUnavailable")).Visibility != Visibility.Visible ||
            !((TextBlock)Required(window, "PairPreviewUnavailableReason")).Text.Contains(
                "Saved local folder no longer exists", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A blocked pair did not show its preview-unavailable reason.");
        Capture(window, Path.Combine(outputRoot, "focused-preview-unavailable.png"));
        ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 3;
        CheckTerminalState(Health(pair, true, false), "This computer: available", "Cloud: unreachable\n",
            "Saved Dropbox folder is unavailable", blocked: true);

        MainWindow.PairHealthTimeoutForTest = TimeSpan.FromMilliseconds(60);
        MainWindow.PairHealthLoaderForTest = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new UnreachableException();
        };
        checkAgain.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => ((TextBlock)Required(window, "PairHealthReasonText")).Text.Contains(
                "did not finish in time", StringComparison.Ordinal) &&
            ((TextBlock)Required(window, "PairHealthReasonText")).Text.Contains("Last confirmed problem:", StringComparison.Ordinal),
            "Timeout health did not reach its terminal state.");
        var reason = ((TextBlock)Required(window, "PairHealthReasonText")).Text;
        if (!window.PairKnownUnhealthyForTest(pair.Id) ||
            !((TextBlock)Required(window, "PairHealthLocalText")).Text.StartsWith("Not checked", StringComparison.Ordinal) ||
            !reason.Contains("did not finish in time", StringComparison.Ordinal) ||
            ((Button)Required(window, "EditorSyncNowButton")).IsEnabled)
            throw new InvalidDataException("Timeout health did not terminate in an actionable blocked state.");
        ExerciseBlockedLaunchSurfaces(window, pair, pairStore, dispatches);
        Capture(window, Path.Combine(outputRoot, "focused-blocked-health.png"));

        MainWindow.PairHealthTimeoutForTest = null;
        MainWindow.PairHealthLoaderForTest = (_, _) => Task.FromResult(Health(pair, true, true));
        checkAgain.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => ((TextBlock)Required(window, "PairHealthLocalText")).Text.StartsWith("This computer: available", StringComparison.Ordinal),
            "Check again did not refresh the tab directly.");
        if (window.PairKnownUnhealthyForTest(pair.Id))
            throw new InvalidDataException("A healthy recheck retained a stale blocking reason.");

        var row = window.PairRows.Single(item => item.Id == pair.Id);
        var pairToggle = FindDescendants<CheckBox>((DataGrid)Required(window, "PairList"))
            .First(item => ReferenceEquals(item.Tag, row));
        pairToggle.IsChecked = false;
        pairToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => window.PairRows.Single(item => item.Id == pair.Id).Enabled == false,
            "The real pair-row switch did not pause the pair.");
        var paused = window.PairRows.Single(item => item.Id == pair.Id);
        var pausedPair = pairStore.Load().Pairs.Single(item => item.Id == pair.Id);
        window.PrepareExistingPairEditorForTest(pausedPair);
        ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 3;
        PumpTask(window, window.RefreshPairSyncStatusForTestAsync());
        if (paused.LiveSyncLabel != "Pair Paused" || paused.Status != "Pair paused" ||
            ((TextBlock)Required(window, "PairHealthLiveSyncText")).Text != "Pair paused")
            throw new InvalidDataException($"The real pair row and Sync status tab contradicted the paused state: " +
                $"enabled={paused.Enabled}; label={paused.LiveSyncLabel}; status={paused.Status}; tab=" +
                ((TextBlock)Required(window, "PairHealthLiveSyncText")).Text + '.');
        var resumedToggle = FindDescendants<CheckBox>((DataGrid)Required(window, "PairList"))
            .First(item => item.Tag is PairRow candidate && candidate.Id == pair.Id);
        resumedToggle.IsChecked = true;
        resumedToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => window.PairRows.Single(item => item.Id == pair.Id).Enabled,
            "The real pair-row switch did not resume the pair.");

        row = window.PairRows.Single(item => item.Id == pair.Id);
        PumpTask(window, window.ToggleAutomaticSyncFromBackgroundAsync());
        PumpTask(window, window.RefreshPairSyncStatusForTestAsync());
        if (row.Status != "Automatic Sync Off" || row.StatusSummary != "Enable in Settings" ||
            ((TextBlock)Required(window, "PairHealthLiveSyncText")).Text != "Live sync is off in Settings")
            throw new InvalidDataException($"The real window contradicted the global Live sync setting: " +
                $"status={row.Status}; summary={row.StatusSummary}; detail={row.StatusDetail}; " +
                $"sources={window.PairAttentionSourcesForTest(pair.Id)}; tab=" +
                ((TextBlock)Required(window, "PairHealthLiveSyncText")).Text + '.');
        PumpTask(window, window.ToggleAutomaticSyncFromBackgroundAsync());
        Passed.Add("TC-UI-04.a-c terminal health, zero dispatch, first-merge non-mutation and Live sync states");
    }

    private static void AssertCleanAddCancel(MainWindow window, SyncPair pair, SyncPairStore store,
        IDropboxFolderBrowserService browser, string localRoot, Func<int> recommendedRecoveryCallCount)
    {
        window.PrepareExistingPairEditorForTest(pair);
        ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 4;
        ((TextBox)Required(window, "FolderSelectionSearchBox")).Text = "stale";
        ((ComboBox)Required(window, "FolderSelectionFilterBox")).SelectedIndex = 6;
        window.SetExistingFolderSelectionForTest("Match", false, "dropbox:id:match");
        ((TextBox)Required(window, "PairPriorityBox")).Text = "7";
        ((CheckBox)Required(window, "PairEnabledBox")).IsChecked = false;
        ((CheckBox)Required(window, "PairDeletionBox")).IsChecked = true;
        ((CheckBox)Required(window, "PairFilesOnDemandBox")).IsChecked = true;
        ((TextBox)Required(window, "AvailabilityPathBox")).Text = "stale/path";
        ((TextBox)Required(window, "RecoveryRootBox")).Text = "/stale-recovery";
        ((TextBlock)Required(window, "RecoveryFolderIdText")).Text = "id:stale-recovery";
        ((ComboBox)Required(window, "RecoveryRetentionBox")).SelectedIndex = 0;
        ((ComboBox)Required(window, "PairStorageBox")).SelectedIndex = 1;
        ((ComboBox)Required(window, "PairPriorityChoice")).SelectedIndex = 2;
        var before = JsonSerializer.Serialize(store.Load());
        var recoveryCallsBeforeAdd = recommendedRecoveryCallCount();
        // Leave the deliberately dirty existing-pair draft explicitly. The user-facing Add action
        // correctly asks before discarding an unsaved draft, which cannot be accepted by this fixture.
        ((Button)Required(window, "CancelPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        ((Button)Required(window, "AddPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (((TabControl)Required(window, "PairEditorTabs")).SelectedIndex != 0 ||
            ((TextBox)Required(window, "FolderSelectionSearchBox")).Text.Length != 0 ||
            ((ComboBox)Required(window, "FolderSelectionFilterBox")).SelectedIndex != 0 ||
            ((TextBox)Required(window, "PairPriorityBox")).Text != "0" ||
            ((CheckBox)Required(window, "PairEnabledBox")).IsChecked != true ||
            ((CheckBox)Required(window, "PairDeletionBox")).IsChecked != false ||
            ((CheckBox)Required(window, "PairFilesOnDemandBox")).IsChecked != false ||
            ((TextBox)Required(window, "AvailabilityPathBox")).Text.Length != 0 ||
            ((TextBox)Required(window, "LocalRootBox")).Text.Length != 0 ||
            ((TextBox)Required(window, "DropboxRootBox")).Text.Length != 0 ||
            ((TextBlock)Required(window, "DropboxFolderIdText")).Text != "Not selected" ||
            ((TextBox)Required(window, "RecoveryRootBox")).Text != SyncDropboxArtifactPaths.RecoveryContainerPath ||
            !((TextBlock)Required(window, "RecoveryFolderIdText")).Text.Contains("Created and verified when you save",
                StringComparison.Ordinal) ||
            ((ComboBox)Required(window, "RecoveryRetentionBox")).SelectedIndex != 1 ||
            ((ComboBox)Required(window, "PairStorageBox")).SelectedIndex != 0 ||
            ((ComboBox)Required(window, "PairPriorityChoice")).SelectedIndex != 1 ||
            ((CheckBox)Required(window, "WholeRootSelectionBox")).IsChecked != false ||
            window.PendingSelectionDefaultIncludedForTest || window.PendingSelectionRulesForTest.Count != 0 ||
            window.LocalGlobalPlanForTest is not null || window.DropboxGlobalPlanForTest is not null ||
            window.GlobalDraftPlanForTest is not null ||
            ((TextBox)Required(window, "PairNameBox")).Text.Length != 0 ||
            recommendedRecoveryCallCount() != recoveryCallsBeforeAdd)
            throw new InvalidDataException("Add pair retained unrelated draft state: " +
                $"tab={((TabControl)Required(window, "PairEditorTabs")).SelectedIndex}; " +
                $"search='{((TextBox)Required(window, "FolderSelectionSearchBox")).Text}'; " +
                $"filter={((ComboBox)Required(window, "FolderSelectionFilterBox")).SelectedIndex}; " +
                $"priority='{((TextBox)Required(window, "PairPriorityBox")).Text}'/" +
                $"{((ComboBox)Required(window, "PairPriorityChoice")).SelectedIndex}; " +
                $"enabled={((CheckBox)Required(window, "PairEnabledBox")).IsChecked}; " +
                $"deletions={((CheckBox)Required(window, "PairDeletionBox")).IsChecked}; " +
                $"demand={((CheckBox)Required(window, "PairFilesOnDemandBox")).IsChecked}; " +
                $"availability='{((TextBox)Required(window, "AvailabilityPathBox")).Text}'; " +
                $"local='{((TextBox)Required(window, "LocalRootBox")).Text}'; " +
                $"dropbox='{((TextBox)Required(window, "DropboxRootBox")).Text}'; " +
                $"dropboxId='{((TextBlock)Required(window, "DropboxFolderIdText")).Text}'; " +
                $"recovery='{((TextBox)Required(window, "RecoveryRootBox")).Text}'/" +
                $"'{((TextBlock)Required(window, "RecoveryFolderIdText")).Text}'; " +
                $"retention={((ComboBox)Required(window, "RecoveryRetentionBox")).SelectedIndex}; " +
                $"storage={((ComboBox)Required(window, "PairStorageBox")).SelectedIndex}; " +
                $"whole={((CheckBox)Required(window, "WholeRootSelectionBox")).IsChecked}; " +
                $"default={window.PendingSelectionDefaultIncludedForTest}; " +
                $"rules={window.PendingSelectionRulesForTest.Count}; " +
                $"localPlan={window.LocalGlobalPlanForTest is not null}; " +
                $"dropboxPlan={window.DropboxGlobalPlanForTest is not null}; " +
                $"draftPlan={window.GlobalDraftPlanForTest is not null}; " +
                $"name='{((TextBox)Required(window, "PairNameBox")).Text}'; " +
                $"recoveryCalls={recommendedRecoveryCallCount()}/{recoveryCallsBeforeAdd}.");
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        if (((TextBox)Required(window, "LocalRootBox")).Text.Length != 0 ||
            ((TextBox)Required(window, "DropboxRootBox")).Text.Length != 0 ||
            window.LocalGlobalPlanForTest is not null || window.DropboxGlobalPlanForTest is not null ||
            recommendedRecoveryCallCount() != recoveryCallsBeforeAdd)
            throw new InvalidDataException("Loading the new-pair browsers restored stale saved roots or selections.");
        UseCompactLocalTree(window, localRoot);
        window.ExpandLocalTreeForTest(localRoot);
        window.SetGlobalLocalSelectionForTest(Path.Combine(localRoot, "Match"), true);
        window.SetGlobalDropboxSelectionForTest("/Match", true);
        if (!((TextBox)Required(window, "PairNameBox")).Text.Contains("Match", StringComparison.Ordinal))
            throw new InvalidDataException("Automatic pair-name suggestion was lost.");
        ((TextBox)Required(window, "PairNameBox")).Text = "My editable name";
        ((Button)Required(window, "CancelPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        if (((ContentControl)Required(window, "NewPairEditorHost")).Visibility != Visibility.Collapsed ||
            ((DataGrid)Required(window, "PairList")).SelectedItem is not null ||
            JsonSerializer.Serialize(store.Load()) != before)
            throw new InvalidDataException("Cancel changed persistence or opened an unrelated row.");
        Passed.Add("TC-UI-06.a-b clean add, editable suggestion and cancel isolation");
    }

    private static void AssertStaleRepair(MainWindow window, SyncPair source,
        IDropboxFolderBrowserService browser, string stateRoot)
    {
        window.PrepareExistingPairEditorForTest(source);
        UseScrollChallengingLocalTree(window, source.LocalRoot, stateRoot);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        var localTree = (TreeView)Required(window, "LocalRootTree");
        var dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        ScrollTreeToStartAndRequireTargetOutside<LocalFolderNode>(window, localTree,
            item => string.Equals(item.Path, source.LocalRoot, StringComparison.OrdinalIgnoreCase),
            "saved local folder");
        ScrollTreeToStartAndRequireTargetOutside<DropboxFolderNode>(window, dropboxTree,
            item => string.Equals(item.Path, source.DropboxRoot, StringComparison.OrdinalIgnoreCase),
            "saved Dropbox folder");
        window.ExpandLocalTreeForTest(source.LocalRoot);
        var loadMore = (Button)Required(window, "LoadMoreDropboxRootButton");
        var openDropbox = ((Panel)Required(window, "SavedDropboxActions")).Children.OfType<Button>().Single();
        if (FindDropbox(window, "/PagedRoot") is null || loadMore.Visibility != Visibility.Collapsed)
            throw new InvalidDataException("The ordinary paged Dropbox browser must automatically finish its next page without hiding folders behind Load more.");
        var folderStatus = Required(window, "DropboxRootBrowserStatus");
        AssertFooterSeparation(window, folderStatus, openDropbox, 1240);
        AssertFooterSeparation(window, folderStatus, openDropbox, 760);
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(source.DropboxRoot));
        PumpFor(window, TimeSpan.FromMilliseconds(150));
        var validLocal = SelectedTreeItem<LocalFolderNode>((TreeView)Required(window, "LocalRootTree"),
            item => string.Equals(item.Path, source.LocalRoot, StringComparison.OrdinalIgnoreCase));
        var validDropbox = SelectedTreeItem<DropboxFolderNode>((TreeView)Required(window, "DropboxRootTree"),
            item => string.Equals(item.Path, source.DropboxRoot, StringComparison.OrdinalIgnoreCase));
        AssertTreeItemInViewport(validLocal, localTree,
            "existing saved local folder");
        AssertTreeItemInViewport(validDropbox, dropboxTree,
            "existing saved Dropbox folder");
        window.WindowState = WindowState.Normal;
        window.Width = 1240;
        window.Height = 790;
        window.UpdateLayout();

        var staleDropboxOnly = source with
        {
            Id = "c123456789abcdefabcd", Name = "Dropbox repair pair",
            DropboxRoot = "/RemoteName/Missing", DropboxFolderId = "id:missing"
        };
        window.PrepareExistingPairEditorForTest(staleDropboxOnly);
        UseScrollChallengingLocalTree(window, source.LocalRoot, stateRoot);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        localTree = (TreeView)Required(window, "LocalRootTree");
        dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        ScrollTreeToStartAndRequireTargetOutside<LocalFolderNode>(window, localTree,
            item => string.Equals(item.Path, source.LocalRoot, StringComparison.OrdinalIgnoreCase),
            "healthy local folder beside a missing Dropbox folder");
        ScrollTreeToStartAndRequireTargetOutside<DropboxFolderNode>(window, dropboxTree,
            item => string.Equals(item.Path, "/RemoteName", StringComparison.OrdinalIgnoreCase),
            "nearest surviving Dropbox ancestor");
        window.ExpandLocalTreeForTest(source.LocalRoot);
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(staleDropboxOnly.DropboxRoot));
        PumpFor(window, TimeSpan.FromMilliseconds(150));
        if (((Border)Required(window, "LocalHierarchyNotice")).Visibility != Visibility.Collapsed ||
            ((Border)Required(window, "DropboxHierarchyNotice")).Visibility != Visibility.Visible)
            throw new InvalidDataException("A missing Dropbox folder damaged the healthy local-side presentation.");
        AssertTreeItemInViewport(SelectedTreeItem<LocalFolderNode>((TreeView)Required(window, "LocalRootTree"),
                item => string.Equals(item.Path, source.LocalRoot, StringComparison.OrdinalIgnoreCase)),
            localTree, "healthy local folder beside a missing Dropbox folder");

        var surviving = Path.Combine(stateRoot, "surviving");
        Directory.CreateDirectory(surviving);
        var missing = Path.Combine(surviving, "missing", "child");
        var stale = source with { Id = "b123456789abcdefabcd", Name = "Stale pair", LocalRoot = missing,
            DropboxRoot = "/RemoteName/Missing", DropboxFolderId = "id:missing" };
        window.PrepareExistingPairEditorForTest(stale);
        UseScrollChallengingLocalTree(window, surviving, stateRoot);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        localTree = (TreeView)Required(window, "LocalRootTree");
        dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        ScrollTreeToStartAndRequireTargetOutside<LocalFolderNode>(window, localTree,
            item => string.Equals(item.Path, surviving, StringComparison.OrdinalIgnoreCase),
            "deepest surviving local ancestor");
        ScrollTreeToStartAndRequireTargetOutside<DropboxFolderNode>(window, dropboxTree,
            item => string.Equals(item.Path, "/RemoteName", StringComparison.OrdinalIgnoreCase),
            "deepest surviving Dropbox ancestor");
        window.ExpandLocalTreeForTest(missing);
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(stale.DropboxRoot));
        PumpFor(window, TimeSpan.FromMilliseconds(200));
        var dataGridScroller = FindDescendants<ScrollViewer>((DataGrid)Required(window, "PairList"))
            .FirstOrDefault(item => item.Name == "DG_ScrollViewer");
        if (((ScrollViewer)Required(window, "PairsScroller")).HorizontalOffset > 1 ||
            dataGridScroller?.HorizontalOffset > 1)
            throw new InvalidDataException("Opening the stale pair retained a horizontal scroll offset.");
        var localNotice = (Border)Required(window, "LocalHierarchyNotice");
        var dropboxNotice = (Border)Required(window, "DropboxHierarchyNotice");
        if (localNotice.Visibility != Visibility.Visible || dropboxNotice.Visibility != Visibility.Visible ||
            ((TextBlock)Required(window, "LocalHierarchyNoticeText")).Text !=
            "Saved folder no longer exists. Choose a replacement." || !Equals(localNotice.ToolTip, missing) ||
            !Equals(dropboxNotice.ToolTip, stale.DropboxRoot))
            throw new InvalidDataException("Stale repair copy or full-path details are incomplete.");
        var nearest = FindDescendants<TreeViewItem>(localTree).FirstOrDefault(item =>
            item.IsSelected && item.DataContext is LocalFolderNode node &&
            string.Equals(node.Path, surviving, StringComparison.OrdinalIgnoreCase));
        if (nearest is null) throw new InvalidDataException("The deepest surviving local ancestor was not selected.");
        AssertTreeItemInViewport(nearest, localTree, "deepest surviving local ancestor");
        var nearestDropbox = FindDescendants<TreeViewItem>(dropboxTree).FirstOrDefault(item => item.IsSelected &&
            item.DataContext is DropboxFolderNode node && node.Path == "/RemoteName");
        if (nearestDropbox is null) throw new InvalidDataException("The deepest surviving Dropbox ancestor was not selected.");
        AssertTreeItemInViewport(nearestDropbox, dropboxTree, "deepest surviving Dropbox ancestor");
        var open = (Button)Required(window, "OpenLocalFolderButton");
        var openLabel = open.Content as string ?? (open.Content as StackPanel)?.Children.OfType<TextBlock>().SingleOrDefault()?.Text;
        if (!string.Equals(openLabel, "Choose replacement…", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The stale local action is not named for its repair effect: " + openLabel);
        Passed.Add("TC-UI-07.a-c stale local/Dropbox navigation and repair layout");
    }

    private static void AssertFinalReviewManualDispatch(MainWindow window, SyncPair pair,
        SyncPairStore pairStore, List<string> dispatches, string localRoot)
    {
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<SyncAuthorityMonitorProjection> monitorUnavailable =
        [
            new(pair.Id, "local", "Monitor unavailable", now, 0),
            new(pair.Id, "dropbox", "Monitoring", now, 0)
        ];
        MainWindow.MonitorProjectionLoaderForTest = id => Task.FromResult<IReadOnlyList<SyncAuthorityMonitorProjection>>(
            monitorUnavailable.Where(item => string.Equals(item.PairId, id, StringComparison.Ordinal)).ToArray());
        window.PrepareExistingPairEditorForTest(pair);
        PumpTask(window, window.RefreshMonitorHealthForTestAsync(pair.Id));
        Pump(window);
        var editorButton = (Button)Required(window, "EditorSyncNowButton");
        var row = window.PairRows.Single(item => item.Id == pair.Id);
        if (!row.NeedsAttention || window.PairKnownUnhealthyForTest(pair.Id) || !editorButton.IsEnabled ||
            !row.StatusDetail.Contains("use Sync now", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Monitor-only attention did not retain actionable manual-sync guidance.");
        var before = dispatches.Count;
        editorButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => dispatches.Count == before + 1,
            "The actual editor Sync now button did not dispatch the monitor-unavailable pair.");
        if (!dispatches[^1].EndsWith(':' + pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The editor Sync now button dispatched the wrong pair.");
        PumpUntil(window, () => !(bool)typeof(MainWindow).GetProperty("_runActive",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! &&
            AutomationProperties.GetItemStatus(editorButton) != "In progress",
            "The editor run did not finish before the next independent user action.");

        var pairList = (DataGrid)Required(window, "PairList");
        pairList.SelectedItem = window.PairRows.Single(item => item.Id == pair.Id);
        var contextSync = (MenuItem)Required(window, "PairSyncNowMenuItem");
        before = dispatches.Count;
        contextSync.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpUntil(window, () => dispatches.Count == before + 1,
            "The real pair-row context Sync now command did not dispatch.");
        if (!dispatches[^1].EndsWith(':' + pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The context Sync now command dispatched the wrong selected pair.");
        PumpUntil(window, () => !(bool)typeof(MainWindow).GetProperty("_runActive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
            "The previous manual run did not finish before the next user action.");

        window.ApplyFolderHierarchyPresentationForTest(Health(pair, false, true));
        Pump(window);
        if (editorButton.IsEnabled || !window.PairKnownUnhealthyForTest(pair.Id))
            throw new InvalidDataException("An endpoint-unhealthy pair left the editor manual action enabled.");
        before = dispatches.Count;
        var consoleBefore = ((TextBox)Required(window, "ConsoleText")).Text.Length;
        contextSync.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpUntil(window, () => ((TextBox)Required(window, "ConsoleText")).Text[consoleBefore..].Contains("Repair this pair", StringComparison.Ordinal),
            "The blocked context action did not report its repair guidance.");
        if (dispatches.Count != before ||
            ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex != 3 ||
            string.IsNullOrWhiteSpace(((TextBlock)Required(window, "PairHealthReasonText")).Text))
            throw new InvalidDataException("The endpoint-unhealthy context action did not fail closed with repair guidance.");

        var fixtureParent = Path.GetDirectoryName(localRoot)!;
        var monitorLocal = Path.Combine(fixtureParent, "MonitorPair");
        var blockedLocal = Path.Combine(fixtureParent, "BlockedPair");
        Directory.CreateDirectory(monitorLocal);
        Directory.CreateDirectory(blockedLocal);
        using var monitorLease = PilotDirectoryLease.Open(monitorLocal);
        using var blockedLease = PilotDirectoryLease.Open(blockedLocal);
        var monitorPair = pair with
        {
            Id = "d123456789abcdefabcd", Name = "Monitor-only pair", LocalRoot = monitorLocal,
            LocalVolumeSerialNumber = monitorLease.Identity.VolumeSerialNumber,
            LocalRootFileId = monitorLease.Identity.FileId, DropboxRoot = "/MonitorPair",
            DropboxFolderId = "id:monitor-pair"
        };
        var blockedPair = pair with
        {
            Id = "e123456789abcdefabcd", Name = "Broken endpoint pair", LocalRoot = blockedLocal,
            LocalVolumeSerialNumber = blockedLease.Identity.VolumeSerialNumber,
            LocalRootFileId = blockedLease.Identity.FileId, DropboxRoot = "/BlockedPair",
            DropboxFolderId = "id:blocked-pair"
        };
        var mixed = new SyncPairSet([pair, monitorPair, blockedPair], pair.Id);
        pairStore.SaveAsync(mixed).GetAwaiter().GetResult();
        window.LoadPairSetForTest(mixed);
        window.ApplyFolderHierarchyPresentationForTest(Health(pair, true, true));
        monitorUnavailable =
        [
            new(monitorPair.Id, "local", "Monitor unavailable", now, 0),
            new(monitorPair.Id, "dropbox", "Monitoring", now, 0)
        ];
        PumpTask(window, window.RefreshMonitorHealthForTestAsync(monitorPair.Id));
        window.ApplyFolderHierarchyPresentationForTest(Health(blockedPair, false, true));

        before = dispatches.Count;
        ((Button)Required(window, "SyncAllNowButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => dispatches.Count == before + 1,
            "The actual global Sync all now wrapper did not complete.");
        AssertMixedDispatch(dispatches[^1], pair.Id, monitorPair.Id, blockedPair.Id, "global");
        PumpUntil(window, () => !(bool)typeof(MainWindow).GetProperty("_runActive",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! &&
            AutomationProperties.GetItemStatus((Button)Required(window, "SyncAllNowButton")) != "In progress",
            "The global run did not finish before the independent tray command.");
        before = dispatches.Count;
        PumpTask(window, window.SyncNowFromBackgroundAsync());
        if (dispatches.Count != before + 1)
            throw new InvalidDataException("The public tray Sync now route did not dispatch once.");
        AssertMixedDispatch(dispatches[^1], pair.Id, monitorPair.Id, blockedPair.Id, "tray");

        pairStore.SaveAsync(new SyncPairSet([pair], pair.Id)).GetAwaiter().GetResult();
        window.PrepareExistingPairEditorForTest(pair);
        window.ApplyFolderHierarchyPresentationForTest(Health(pair, true, true));
        MainWindow.MonitorProjectionLoaderForTest = null;
        Passed.Add("TCFINAL-001-A editor button monitor-only dispatch and endpoint rejection");
        Passed.Add("TCFINAL-001-B pair-row context command exact selection and endpoint rejection");
        Passed.Add("TCFINAL-001-C global and tray mixed-health dispatch admission");
    }

    private static void AssertUnsavedDraftManualAdmission(MainWindow window, SyncPair pair,
        SyncPairStore pairStore, IDropboxFolderBrowserService browser, List<string> dispatches,
        string localRoot)
    {
        var otherRoot = Path.Combine(Path.GetDirectoryName(localRoot)!, "unaffected-pair");
        Directory.CreateDirectory(otherRoot);
        using var otherLease = PilotDirectoryLease.Open(otherRoot);
        var otherPair = pair with
        {
            Id = "b123456789abcdefabcd", Name = "Unaffected pair", LocalRoot = otherRoot,
            LocalVolumeSerialNumber = otherLease.Identity.VolumeSerialNumber,
            LocalRootFileId = otherLease.Identity.FileId, DropboxRoot = "/Unaffected",
            DropboxFolderId = "id:unaffected"
        };

        static void InvokeManualEntryPoints(MainWindow target, SyncPair selected)
        {
            var pairList = (DataGrid)Required(target, "PairList");
            pairList.SelectedItem = target.PairRows.Single(item => item.Id == selected.Id);
            Invoke(target, "Run_Click", target, new RoutedEventArgs());
            Invoke(target, "RunPairNow_Click", Required(target, "EditorSyncNowButton"), new RoutedEventArgs());
            ((MenuItem)Required(target, "PairSyncNowMenuItem"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Invoke(target, "RunAllPairsNow_Click", Required(target, "SyncAllNowButton"), new RoutedEventArgs());
        }

        void PrepareExistingDraft(string changedName)
        {
            PumpUntil(window, () => !window.RunActiveForTest,
                "The prior actual run did not finish before constructing the next independent draft.");
            var set = new SyncPairSet([pair, otherPair], pair.Id);
            pairStore.SaveAsync(set).GetAwaiter().GetResult();
            window.LoadPairSetForTest(set);
            window.PrepareExistingPairEditorForTest(pair);
            window.LoadPairSetForTest(set);
            UseCompactLocalTree(window, localRoot);
            window.ExpandLocalTreeForTest(localRoot);
            PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            PumpTask(window, window.ExpandDropboxTreeForTestAsync(pair.DropboxRoot));
            ((TextBox)Required(window, "PairNameBox")).Text = changedName;
            Pump(window);
        }

        MainWindow.UnsavedDraftRunChoiceForTest = () => AppDialogChoice.Cancel;
        PrepareExistingDraft("Unsaved existing pair");
        var wasDirty = Invoke(window, "CurrentPairEditorHasUnsavedChanges") as bool? == true;
        var before = dispatches.Count;
        var pairList = (DataGrid)Required(window, "PairList");
        var suppressSelection = typeof(MainWindow).GetField("_suppressPairSelection",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
                                throw new MissingFieldException(nameof(MainWindow), "_suppressPairSelection");
        suppressSelection.SetValue(window, true);
        pairList.SelectedItem = window.PairRows.Single(item => item.Id == otherPair.Id);
        suppressSelection.SetValue(window, false);
        ((MenuItem)Required(window, "PairSyncNowMenuItem"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        InvokeManualEntryPoints(window, pair);
        PumpTask(window, window.SyncNowFromBackgroundAsync());
        PumpFor(window, TimeSpan.FromMilliseconds(150));
        if (dispatches.Count != before ||
            Invoke(window, "CurrentPairEditorHasUnsavedChanges") as bool? != true ||
            ((TextBox)Required(window, "PairNameBox")).Text != "Unsaved existing pair")
            throw new InvalidDataException("Cancel admitted a manual sync or discarded the dirty existing-pair draft: " +
                $"dirtyBefore={wasDirty}; dispatches={before}->{dispatches.Count}; " +
                $"name={((TextBox)Required(window, "PairNameBox")).Text}.");

        Invoke(window, "BeginNewPairEditor");
        ((TextBox)Required(window, "PairNameBox")).Text = "Unsaved new pair";
        before = dispatches.Count;
        Invoke(window, "Run_Click", window, new RoutedEventArgs());
        Invoke(window, "RunAllPairsNow_Click", Required(window, "SyncAllNowButton"), new RoutedEventArgs());
        PumpTask(window, window.SyncNowFromBackgroundAsync());
        PumpFor(window, TimeSpan.FromMilliseconds(150));
        if (dispatches.Count != before ||
            ((ContentControl)Required(window, "NewPairEditorHost")).Visibility != Visibility.Visible ||
            ((TextBox)Required(window, "PairNameBox")).Text != "Unsaved new pair")
            throw new InvalidDataException("Cancel admitted a manual sync or changed the dirty new-pair draft.");

        MainWindow.UnsavedDraftRunChoiceForTest = () => AppDialogChoice.Secondary;
        PrepareExistingDraft("Discard this edit");
        before = dispatches.Count;
        Invoke(window, "Run_Click", window, new RoutedEventArgs());
        PumpUntil(window, () => dispatches.Count == before + 1,
            "Discard changes did not continue the dashboard sync with saved settings.");
        if (pairStore.Load().Pairs.Single(item => item.Id == pair.Id).Name != pair.Name)
            throw new InvalidDataException("Discard changes modified the saved pair.");

        MainWindow.UnsavedDraftRunChoiceForTest = () => AppDialogChoice.Primary;
        PrepareExistingDraft("Saved before sync");
        MainWindow.PairBindingForTest = request =>
        {
            var saved = pair with { Name = request.Name };
            var set = new SyncPairSet([saved, otherPair], saved.Id);
            pairStore.SaveAsync(set).GetAwaiter().GetResult();
            return Task.FromResult(new SyncPairBindingResult(set, saved,
                new PilotSettings(saved.LocalRoot, saved.LocalVolumeSerialNumber, saved.LocalRootFileId,
                    saved.DropboxRoot, saved.DropboxFolderId), IdentityChanged: false));
        };
        before = dispatches.Count;
        PumpTask(window, window.SyncNowFromBackgroundAsync());
        PumpUntil(window, () => dispatches.Count == before + 1,
            "Save changes did not complete before the tray sync dispatch.");
        if (pairStore.Load().Pairs.Single(item => item.Id == pair.Id).Name != "Saved before sync")
            throw new InvalidDataException("Save changes dispatched without persisting the visible draft.");

        MainWindow.PairBindingForTest = null;
        MainWindow.UnsavedDraftRunChoiceForTest = () => AppDialogChoice.Cancel;
        var restored = new SyncPairSet([pair], pair.Id);
        pairStore.SaveAsync(restored).GetAwaiter().GetResult();
        window.LoadPairSetForTest(restored);
        window.PrepareExistingPairEditorForTest(pair);
        Passed.Add("TC-UI-10.d every manual sync entry honors save, discard and cancel for dirty existing/new drafts");
        Passed.Add("TC-UI-10.e unaffected-pair context is deliberately guarded while any visible draft is dirty");
    }

    private static void AssertMixedDispatch(string observed, string healthyId, string monitorId, string blockedId,
        string surface)
    {
        var ids = observed[(observed.IndexOf(':') + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!ids.Contains(healthyId, StringComparer.Ordinal) || !ids.Contains(monitorId, StringComparer.Ordinal) ||
            ids.Contains(blockedId, StringComparer.Ordinal) || ids.Length != 2)
            throw new InvalidDataException($"The {surface} manual route admitted the wrong pair set: {observed}");
    }

    private static void AssertFinalReviewDropboxReplacement(MainWindow window, SyncPair source,
        SyncPairStore pairStore, IDropboxFolderBrowserService browser, string localRoot)
    {
        var stale = source with
        {
            Id = "f123456789abcdefabcd", Name = "Replace stale Dropbox",
            DropboxRoot = "/RemoteName/Missing", DropboxFolderId = "id:missing"
        };
        pairStore.SaveAsync(new SyncPairSet([stale], stale.Id)).GetAwaiter().GetResult();
        window.PrepareExistingPairEditorForTest(stale);
        PumpFor(window, TimeSpan.FromMilliseconds(250));
        UseCompactLocalTree(window, localRoot);
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        PumpTask(window, window.LoadMoreDropboxRootForTestAsync());
        window.ExpandLocalTreeForTest(localRoot);
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(stale.DropboxRoot));
        Pump(window);
        var beforeLocal = JsonSerializer.Serialize(window.LocalGlobalPlanForTest);
        var beforeLocalRules = JsonSerializer.Serialize(window.PendingSelectionRulesForTest);
        var staleDropbox = JsonSerializer.Serialize(window.DropboxGlobalPlanForTest);
        if (!staleDropbox.Contains("/RemoteName/Missing", StringComparison.Ordinal) ||
            !window.ReplaceUnavailableDropboxOnNextCheckForTest ||
            ((Border)Required(window, "DropboxHierarchyNotice")).Visibility != Visibility.Visible)
            throw new InvalidDataException("The stale Dropbox branch was not retained and surfaced before replacement.");

        var pictures = FindDropbox(window, "/Pictures") ??
                       throw new InvalidDataException("The unrelated /Pictures replacement was not visible.");
        var dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        Invoke(window, "BringTreeNodeIntoView", dropboxTree, pictures);
        Pump(window);
        var picturesCheck = FindDescendants<CheckBox>(dropboxTree)
            .Single(item => ReferenceEquals(item.DataContext, pictures));
        Invoke(window, "DropboxGlobalFolderCheck_Click", picturesCheck,
            new RoutedEventArgs(ButtonBase.ClickEvent, picturesCheck));
        Pump(window);
        var observedFolderId = ((TextBlock)Required(window, "DropboxFolderIdText")).Text;
        if (!string.Equals(window.DropboxGlobalPlanForTest?.RootPath, "/Pictures", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observedFolderId, "id:pictures", StringComparison.Ordinal) ||
            JsonSerializer.Serialize(window.LocalGlobalPlanForTest) != beforeLocal ||
            JsonSerializer.Serialize(window.PendingSelectionRulesForTest) != beforeLocalRules ||
            JsonSerializer.Serialize(window.DropboxGlobalPlanForTest).Contains("/RemoteName/Missing", StringComparison.Ordinal))
            throw new InvalidDataException("The first unrelated Dropbox selection did not atomically replace only the stale side. " +
                $"Dropbox={JsonSerializer.Serialize(window.DropboxGlobalPlanForTest)}; folderId={observedFolderId}; " +
                $"localBefore={beforeLocal}; localAfter={JsonSerializer.Serialize(window.LocalGlobalPlanForTest)}; " +
                $"rulesBefore={beforeLocalRules}; rulesAfter={JsonSerializer.Serialize(window.PendingSelectionRulesForTest)}; " +
                $"replacement={window.ReplaceUnavailableDropboxOnNextCheckForTest}; run={window.RunActiveForTest}; updating={window.UpdatingGlobalTreeSelectionForTest}; checked={pictures.IsChecked}");

        var validDraft = CompleteDraftSnapshot(window);
        var persistedBeforeInvalid = JsonSerializer.Serialize(pairStore.Load());
        var unrelated = FindDropbox(window, "/Match") ?? throw new InvalidDataException("The invalid sibling was not visible.");
        Invoke(window, "BringTreeNodeIntoView", dropboxTree, unrelated);
        Pump(window);
        var unrelatedCheck = FindDescendants<CheckBox>(dropboxTree)
            .Single(item => ReferenceEquals(item.DataContext, unrelated));
        Invoke(window, "DropboxGlobalFolderCheck_Click", unrelatedCheck,
            new RoutedEventArgs(ButtonBase.ClickEvent, unrelatedCheck));
        Pump(window);
        if (CompleteDraftSnapshot(window) != validDraft || JsonSerializer.Serialize(pairStore.Load()) != persistedBeforeInvalid)
            throw new InvalidDataException("An unrepresentable coupled click damaged the last valid replacement draft.");

        SyncPairBindingRequest? observedRequest = null;
        MainWindow.PairBindingForTest = request =>
        {
            observedRequest = request;
            var successor = stale with
            {
                Id = "f00d0000000000000001", LocalRoot = request.LocalRoot,
                DropboxRoot = request.DropboxFolder.Path, DropboxFolderId = request.DropboxFolder.FolderId,
                SelectionRules = request.SelectionRules, SelectionDefaultIncluded = request.SelectionDefaultIncluded ?? true,
                Name = request.Name
            };
            var set = new SyncPairSet([successor], successor.Id);
            pairStore.SaveAsync(set).GetAwaiter().GetResult();
            return Task.FromResult(new SyncPairBindingResult(set, successor,
                new PilotSettings(successor.LocalRoot, successor.LocalVolumeSerialNumber,
                    successor.LocalRootFileId, successor.DropboxRoot, successor.DropboxFolderId), IdentityChanged: true));
        };
        ((Button)Required(window, "SavePairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(window, () => pairStore.Load().Pairs.SingleOrDefault()?.DropboxRoot == "/Pictures",
            "The actual Save pair path did not persist the Dropbox replacement.");
        var saved = pairStore.Load().Pairs.Single();
        if (observedRequest is null || observedRequest.DropboxFolder.Path != "/Pictures" ||
            observedRequest.DropboxFolder.FolderId != "id:pictures" || saved.DropboxFolderId != "id:pictures" ||
            !string.Equals(saved.LocalRoot, source.LocalRoot, StringComparison.OrdinalIgnoreCase) ||
            saved.SelectionDefaultIncluded != source.SelectionDefaultIncluded ||
            JsonSerializer.Serialize(saved.SelectionRules) != JsonSerializer.Serialize(source.SelectionRules))
            throw new InvalidDataException("Save pair did not retain the healthy local binding and exact replacement Dropbox identity.");
        MainWindow.PairBindingForTest = null;
        pairStore.SaveAsync(new SyncPairSet([source], source.Id)).GetAwaiter().GetResult();
        window.PrepareExistingPairEditorForTest(source);
        Passed.Add("TCFINAL-002-A stale Dropbox root replacement clears only stale rules");
        Passed.Add("TCFINAL-002-B actual Save pair persists exact replacement identity");
        Passed.Add("TCFINAL-002-C invalid coupled clicks preserve the valid draft and store atomically");
    }

    private static string CompleteDraftSnapshot(MainWindow window) => JsonSerializer.Serialize(new
    {
        Local = window.LocalGlobalPlanForTest,
        Dropbox = window.DropboxGlobalPlanForTest,
        window.PendingSelectionDefaultIncludedForTest,
        Rules = window.PendingSelectionRulesForTest,
        DropboxFolderId = ((TextBlock)Required(window, "DropboxFolderIdText")).Text,
        LocalRoot = ((TextBox)Required(window, "LocalRootBox")).Text,
        DropboxRoot = ((TextBox)Required(window, "DropboxRootBox")).Text
    });

    private static void AssertFolderAccessibility(MainWindow window, SyncPair pair,
        IDropboxFolderBrowserService browser, string localRoot, string outputRoot)
    {
        NavigateToPairs(window);
        window.PrepareExistingPairEditorForTest(pair);
        ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 0;
        PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
        PumpTask(window, window.ExpandDropboxTreeForTestAsync("/RemoteName"));
        UseCompactLocalTree(window, localRoot);
        window.ExpandLocalTreeForTest(localRoot);
        Pump(window);
        var node = FindLocal(window, Path.Combine(localRoot, "Very long folder name that must not displace the right chevron 0123456789"))!;
        node.IsExpanded = true;
        var tree = (TreeView)Required(window, "LocalRootTree");
        Invoke(window, "BringTreeNodeIntoView", tree, node);
        Pump(window);
        var checkbox = FindDescendants<CheckBox>(tree).FirstOrDefault(item => ReferenceEquals(item.DataContext, node))
            ?? throw new InvalidDataException($"The long-name folder checkbox was not realized: treeVisible={tree.IsVisible}; " +
                $"roots={window.LocalRootNodes.Count}; nodeFound={node is not null}; " +
                $"checkboxes={FindDescendants<CheckBox>(tree).Count()}; " +
                $"editorVisible={((FrameworkElement)Required(window, "PairEditorPanel")).IsVisible}.");
        var chevron = FindDescendants<ToggleButton>(tree).First(item => item is not CheckBox &&
            ReferenceEquals(item.DataContext, node));
        var checkboxBounds = Bounds(checkbox, tree);
        var chevronBounds = Bounds(chevron, tree);
        if (checkbox.ActualWidth < 28 || checkbox.ActualHeight < 28 ||
            checkboxBounds.Right >= chevronBounds.Left || chevronBounds.Right > tree.ActualWidth + 1)
            throw new InvalidDataException($"Folder checkbox/chevron geometry is unsafe for a long label: " +
                $"check={checkbox.ActualWidth:0.0}x{checkbox.ActualHeight:0.0} {checkboxBounds}; " +
                $"chevron={chevronBounds}; tree={tree.ActualWidth:0.0}.");
        var localMatch = FindLocal(window, Path.Combine(localRoot, "Match"))!;
        var dropboxMatch = FindDropbox(window, "/RemoteName/Match")!;
        Invoke(window, "BringTreeNodeIntoView", tree, localMatch);
        Pump(window);
        var localMatchCheck = FindDescendants<CheckBox>(tree)
            .First(item => ReferenceEquals(item.DataContext, localMatch));
        var dropboxTree = (TreeView)Required(window, "DropboxRootTree");
        Invoke(window, "BringTreeNodeIntoView", dropboxTree, dropboxMatch);
        Pump(window);
        var dropboxMatchCheck = FindDescendants<CheckBox>(dropboxTree)
            .First(item => ReferenceEquals(item.DataContext, dropboxMatch));
        localMatchCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        var localPlanRoot = FindLocal(window, localRoot)!;
        var dropboxPlanRoot = FindDropbox(window, "/RemoteName")!;
        if (localMatch.IsChecked != false || dropboxMatch.IsChecked != false ||
            localPlanRoot.IsChecked is not null || dropboxPlanRoot.IsChecked is not null ||
            FolderMark(localMatchCheck) != string.Empty || FolderMark(dropboxMatchCheck) != string.Empty ||
            FolderMark(FindDescendants<CheckBox>(tree).First(item => ReferenceEquals(item.DataContext, localPlanRoot))) != "—" ||
            FolderMark(FindDescendants<CheckBox>(dropboxTree).First(item => ReferenceEquals(item.DataContext, dropboxPlanRoot))) != "—")
            throw new InvalidDataException("The real local checkbox did not produce a coupled empty/dash state.");
        FocusAndAssertFolderControl(localMatchCheck, "local folder checkbox");
        Capture(window, Path.Combine(outputRoot, "focused-selected-tristate.png"));
        dropboxMatchCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        if (localMatch.IsChecked != true || dropboxMatch.IsChecked != true ||
            localPlanRoot.IsChecked != true || dropboxPlanRoot.IsChecked != true ||
            FolderMark(localMatchCheck) != "✓" || FolderMark(dropboxMatchCheck) != "✓")
            throw new InvalidDataException("The real Dropbox checkbox did not restore the coupled tick state.");

        Invoke(window, "BringTreeNodeIntoView", tree, node);
        Pump(window);
        checkbox = FindDescendants<CheckBox>(tree).First(item => ReferenceEquals(item.DataContext, node));
        chevron = FindDescendants<ToggleButton>(tree).First(item => item is not CheckBox &&
            ReferenceEquals(item.DataContext, node));
        FocusAndAssertFolderControl(checkbox, "long local folder checkbox");
        Pump(window);
        var name = AutomationProperties.GetName(checkbox);
        if (!name.Contains(node.Name, StringComparison.Ordinal) ||
            !name.Contains("computer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The checkbox accessible name omits its side or folder.");
        node.IsExpanded = false;
        Pump(window);
        if (!AutomationProperties.GetName(chevron).StartsWith("Expand ", StringComparison.Ordinal))
            throw new InvalidDataException("Collapsed chevron does not expose its action.");
        FocusAndAssertChevron(chevron, "local folder chevron");
        node.IsExpanded = true;
        Pump(window);
        if (!AutomationProperties.GetName(chevron).StartsWith("Collapse ", StringComparison.Ordinal))
            throw new InvalidDataException("Expanded chevron does not expose its action.");

        var dropboxLong = FindDropbox(window,
            "/Very long Dropbox folder name that must keep its chevron visible 0123456789")!;
        dropboxLong.IsExpanded = true;
        PumpTask(window, window.ExpandDropboxTreeForTestAsync(dropboxLong.Path +
            "/Deep child folder with a long name 0123456789"));
        Invoke(window, "BringTreeNodeIntoView", dropboxTree, dropboxLong);
        Pump(window);
        var dropboxCheck = FindDescendants<CheckBox>(dropboxTree)
            .First(item => ReferenceEquals(item.DataContext, dropboxLong));
        if (!AutomationProperties.GetName(dropboxCheck).Contains("Dropbox", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dropbox checkbox accessible name omits its side.");
        if (dropboxCheck.ActualWidth < 28 || dropboxCheck.ActualHeight < 28)
            throw new InvalidDataException("Dropbox folder checkbox does not retain its hit target.");
        FocusAndAssertFolderControl(dropboxCheck, "Dropbox folder checkbox");
        var dropboxChevron = FindDescendants<ToggleButton>(dropboxTree).First(item => item is not CheckBox &&
            ReferenceEquals(item.DataContext, dropboxLong));
        dropboxLong.IsExpanded = false;
        Pump(window);
        if (!AutomationProperties.GetName(dropboxChevron).StartsWith("Expand ", StringComparison.Ordinal))
            throw new InvalidDataException("Collapsed Dropbox chevron does not expose its action.");
        FocusAndAssertChevron(dropboxChevron, "Dropbox folder chevron");
        dropboxLong.IsExpanded = true;
        Pump(window);
        if (!AutomationProperties.GetName(dropboxChevron).StartsWith("Collapse ", StringComparison.Ordinal))
            throw new InvalidDataException("Expanded Dropbox chevron does not expose its action.");
        var dropboxDeep = FindDropbox(window, dropboxLong.Path + "/Deep child folder with a long name 0123456789")!;
        var deepCheck = FindDescendants<CheckBox>(dropboxTree)
            .First(item => ReferenceEquals(item.DataContext, dropboxDeep));
        var deepChevron = FindDescendants<ToggleButton>(dropboxTree).First(item => item is not CheckBox &&
            ReferenceEquals(item.DataContext, dropboxDeep));
        if (Bounds(deepCheck, dropboxTree).Right >= Bounds(deepChevron, dropboxTree).Left ||
            Bounds(deepChevron, dropboxTree).Right > dropboxTree.ActualWidth + 1)
            throw new InvalidDataException("Deep Dropbox folder geometry displaced its right-side chevron.");

        var ordinaryItem = FindDescendants<TreeViewItem>(tree).First(item => ReferenceEquals(item.DataContext, localMatch));
        var ordinaryLabel = FindDescendants<TextBlock>(ordinaryItem).First(item => item.Text == localMatch.Name);
        var ordinaryInk = ((SolidColorBrush)Application.Current.Resources["Ink"]).Color;
        var raisedSurface = ((SolidColorBrush)Application.Current.Resources["RaisedSurface"]).Color;
        if (ordinaryItem.IsSelected || ordinaryLabel.Foreground is not SolidColorBrush actualOrdinaryInk ||
            actualOrdinaryInk.Color != ordinaryInk || Contrast(actualOrdinaryInk.Color, raisedSurface) < 4.5)
            throw new InvalidDataException("An unselected folder label does not use readable application ink.");
        var selectedItem = FindDescendants<TreeViewItem>(tree).First(item => ReferenceEquals(item.DataContext, node));
        selectedItem.IsSelected = true;
        Pump(window);
        var selectedLabel = FindDescendants<TextBlock>(selectedItem).First(item => item.Text == node.Name);
        var selectedInk = ((SolidColorBrush)Application.Current.Resources["SelectedInk"]).Color;
        var selectedSurface = ((SolidColorBrush)Application.Current.Resources["SelectedSurface"]).Color;
        if (selectedLabel.Foreground is not SolidColorBrush actualInk || actualInk.Color != selectedInk ||
            Contrast(actualInk.Color, selectedSurface) < 4.5)
            throw new InvalidDataException("The actual selected folder label does not use readable SelectedInk.");
        Passed.Add("TC-UI-08.a-b themed tri-state and accessible folder controls");
    }

    private static void AssertFolderPickerPathAndClicks(MainWindow window, SyncPair pair,
        IDropboxFolderBrowserService browser, string stateRoot)
    {
        var pickerRoot = Path.Combine(stateRoot, "picker-root");
        var deepPath = Path.Combine(pickerRoot, "Alpha", "Bravo", "Charlie", "Delta");
        Directory.CreateDirectory(deepPath);
        Directory.CreateDirectory(Path.Combine(deepPath, "Leaf"));
        Directory.CreateDirectory(Path.Combine(pickerRoot, "Sibling", "Nested", "Deeper"));

        var previousFactory = MainWindow.LocalRootNodeFactoryForTest;
        MainWindow.LocalRootNodeFactoryForTest = _ =>
        {
            return [LocalFolderNode.CreateUnloadedForTest(pickerRoot)];
        };
        try
        {
            window.PrepareExistingPairEditorForTest(pair);
            ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 0;
            PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            PumpUntil(window, () => window.LocalRootNodes.Count == 1 &&
                                    string.Equals(Path.GetFullPath(window.LocalRootNodes[0].Path),
                                        Path.GetFullPath(pickerRoot), StringComparison.OrdinalIgnoreCase),
                "The folder picker did not expose the unloaded drive-root fixture for path entry.");
            PumpFor(window, TimeSpan.FromMilliseconds(150));

            var pathBox = (TextBox)Required(window, "LocalPathEntryBox");
            var goButton = (Button)Required(window, "GoToLocalPathButton");
            var pairList = (DataGrid)Required(window, "PairList");
            if (!pathBox.IsVisible || !pathBox.IsEnabled || !goButton.IsVisible ||
                AutomationProperties.GetName(pathBox).Length == 0 || AutomationProperties.GetName(goButton).Length == 0)
                throw new InvalidDataException("The folder picker has no reachable direct local path entry.");
            if (string.IsNullOrWhiteSpace(pathBox.Text) && (goButton.IsEnabled ||
                goButton.ToolTip?.ToString()?.Contains("Enter a full local folder path first", StringComparison.Ordinal) != true ||
                !ToolTipService.GetShowOnDisabled(goButton)))
                throw new InvalidDataException("The empty path action must be disabled with its exact required-input explanation.");
            pathBox.Focus();
            Keyboard.Focus(pathBox);
            if (!pathBox.IsKeyboardFocused)
                throw new InvalidDataException("The folder picker path entry cannot take keyboard focus.");
            var source = PresentationSource.FromVisual(window) ??
                         throw new InvalidDataException("The fixture window has no presentation source.");
            var planBefore = PlanSnapshot(window);
            var selectedPairBefore = pairList.SelectedItem;

            foreach (var rejected in new[] { "relative" + Path.DirectorySeparatorChar + "folder", "C:relative", "\\relative",
                         Path.Combine(pickerRoot, "not-there") })
            {
                pathBox.Text = rejected;
                if (!goButton.IsEnabled) throw new InvalidDataException("Nonempty typed path cannot reach its real validation action.");
                goButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpFor(window, TimeSpan.FromMilliseconds(120));
                if (window.LocalRootNodes.Single().IsLoaded)
                    throw new InvalidDataException(
                        $"The folder picker read the folder list for the rejected path '{rejected}'.");
            }
            pathBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(window);
            if (pathBox.Text.Length != 0 || window.LocalRootNodes.Single().IsLoaded)
                throw new InvalidDataException("Cancelling a typed path kept the text or still read folders.");

            pathBox.Text = deepPath;
            pathBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            PumpUntil(window, () => FindLocal(window, deepPath) is not null,
                "The typed deep local path was not opened from the keyboard.");
            Pump(window);
            var sibling = FindLocal(window, Path.Combine(pickerRoot, "Sibling"));
            if (!window.LocalRootNodes.Single().IsLoaded ||
                FindLocal(window, Path.Combine(pickerRoot, "Alpha")) is null || sibling is null || sibling.IsLoaded)
                throw new InvalidDataException(
                    "A typed path did not open exactly its own ancestors, or it read an unrelated sibling subtree.");
            if (FindLocal(window, deepPath)!.IsChecked == true || PlanSnapshot(window) != planBefore ||
                !ReferenceEquals(pairList.SelectedItem, selectedPairBefore))
                throw new InvalidDataException("Typing a path ticked a folder or changed the selected pair row.");

            var tree = (TreeView)Required(window, "LocalRootTree");
            tree.UpdateLayout();
            Pump(window);
            var deepItem = FindDescendants<TreeViewItem>(tree).FirstOrDefault(item =>
                item.DataContext is LocalFolderNode node &&
                string.Equals(Path.GetFullPath(node.Path), Path.GetFullPath(deepPath), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The typed deep folder row was never realized in the picker tree.");
            AssertTreeItemInViewport(deepItem, tree, "typed deep local folder");
            foreach (var ancestor in new[]
                     {
                         pickerRoot, Path.Combine(pickerRoot, "Alpha"), Path.Combine(pickerRoot, "Alpha", "Bravo")
                     })
                FindLocal(window, ancestor)!.IsExpanded = true;
            Pump(window);
            deepItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent });
            Pump(window);
            if (!FindLocal(window, pickerRoot)!.IsExpanded ||
                !FindLocal(window, Path.Combine(pickerRoot, "Alpha"))!.IsExpanded ||
                FindLocal(window, deepPath)!.IsExpanded)
                throw new InvalidDataException("A double click on a deep folder collapsed its expanded ancestor chain.");
            deepItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent });
            Pump(window);
            if (!FindLocal(window, deepPath)!.IsExpanded)
                throw new InvalidDataException("A second double click did not reopen the deep folder row.");
            if (FindLocal(window, deepPath)!.IsChecked == true || PlanSnapshot(window) != planBefore ||
                !ReferenceEquals(pairList.SelectedItem, selectedPairBefore))
                throw new InvalidDataException("Row clicks changed the folder tick state or the selected pair row.");

            var createOrSelect = (Button)Required(window, "CreateOrSelectDropboxFolderButton");
            var openLocal = (Button)Required(window, "OpenLocalFolderButton");
            if (!createOrSelect.IsVisible || !createOrSelect.IsEnabled ||
                AutomationProperties.GetName(createOrSelect).Length == 0 || !openLocal.IsVisible || !openLocal.IsEnabled)
                throw new InvalidDataException(
                    "The pair folder picker lost its cloud create/select or local folder actions.");
            var unrestricted = new DropboxFolderBrowserWindow(browser, "Dropbox · fixture browser");
            if (!unrestricted.AllowsFolderCreationForTest ||
                ((FrameworkElement)Required(unrestricted, "CreateFolderPanel")).Visibility != Visibility.Visible ||
                ((Button)Required(unrestricted, "CreateFolderButton")).Visibility != Visibility.Visible ||
                !((Button)Required(unrestricted, "CreateFolderButton")).IsEnabled ||
                ((Button)Required(unrestricted, "SelectFolderButton")).Visibility != Visibility.Visible)
                throw new InvalidDataException(
                    "The supported Dropbox folder browser no longer reaches Create Folder and Select folder.");
            Passed.Add("TC-UI-29.a-c typed local path, bounded deep tree and real click handling");
        }
        finally
        {
            MainWindow.LocalRootNodeFactoryForTest = previousFactory;
        }
    }

    private static void AssertThemeAndLanguage(MainWindow window)
    {
        var resources = Application.Current.Resources;
        var blue = ((SolidColorBrush)resources["DropboxBlue"]).Color.ToString();
        var cloud = ((SolidColorBrush)resources["DropboxCloud"]).Color.ToString();
        var selected = ((SolidColorBrush)resources["SelectedSurface"]).Color;
        var ink = ((SolidColorBrush)resources["SelectedInk"]).Color;
        if (blue != "#FF0061FE" || cloud != "#FFB4C8E1" || selected.ToString() != "#FF202D41" ||
            Contrast(selected, ink) < 4.5)
            throw new InvalidDataException("Dropbox colour tokens or AMOLED selected-state contrast are wrong.");
        var allText = FindDescendants<TextBlock>(window).Select(item => item.Text).ToArray();
        foreach (var text in new[] { "Choose the folders you want to keep in sync.",
                     "Paired folder on this computer", "Paired folder in Dropbox" })
            if (!allText.Contains(text)) throw new InvalidDataException("Agreed UI language is missing: " + text);
        Passed.Add("TC-UI-09.a exact language, Dropbox tokens and AMOLED contrast");
    }

    private static object AssertVersionIdentity(string expectedSourceRevision)
    {
        if (expectedSourceRevision.Length != 40 ||
            expectedSourceRevision.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The focused UI selector requires one exact 40-character source revision.");
        var appVersion = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        var coreVersion = typeof(SyncPair).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;
        var folder = Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!;
        var launcher = Path.Combine(folder, "SyncShark.exe");
        var launcherVersion = FileVersionInfo.GetVersionInfo(launcher).ProductVersion ?? string.Empty;
        var fixtureVersion = typeof(FocusedUiAcceptanceContract).Assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidDataException("The current fixture has no build version.");
        var exactVersion = fixtureVersion + "+" + expectedSourceRevision.ToLowerInvariant();
        if (!VersionIdentityMatches(exactVersion, appVersion, coreVersion, launcherVersion))
            throw new InvalidDataException(
                $"Launcher/app/core must all equal the exact candidate identity {exactVersion}. " +
                $"Observed app={appVersion}; core={coreVersion}; launcher={launcherVersion}.");
        var otherRevision = new string(expectedSourceRevision[0] == '0' ? '1' : '0', 40);
        var otherVersion = "0.3.12+" + otherRevision;
        if (VersionIdentityMatches(exactVersion, "0.3.12+UNBOUND", "0.3.12+UNBOUND", "0.3.12+UNBOUND") ||
            VersionIdentityMatches(exactVersion, otherVersion, otherVersion, otherVersion) ||
            VersionIdentityMatches(exactVersion, "0.3.12+", "0.3.12+", "0.3.12+") ||
            VersionIdentityMatches(exactVersion, exactVersion, exactVersion, otherVersion))
            throw new InvalidDataException("The exact-version rejection controls admitted an unbound, empty, mixed or different revision.");
        Passed.Add("TC-UI-10.a launcher/app/core exact source-revision alignment and rejection controls");
        return new { appVersion, coreVersion, launcherVersion };
    }

    internal static bool VersionIdentityMatches(string expectedVersion, string appVersion,
        string coreVersion, string launcherVersion) =>
        string.Equals(appVersion, expectedVersion, StringComparison.Ordinal) &&
        string.Equals(coreVersion, expectedVersion, StringComparison.Ordinal) &&
        string.Equals(launcherVersion, expectedVersion, StringComparison.Ordinal);

    private static SyncFolderSelectionTree Health(SyncPair pair, bool local, bool dropbox) =>
        new(pair.Id, [], 0, 0, 0, 0, new SyncFolderRollup(0, 0, 0, 0, 0, 0),
            LocalHierarchyAvailable: local,
            LocalHierarchyMessage: local ? null : "Saved local folder no longer exists. Choose a replacement.",
            DropboxHierarchyAvailable: dropbox,
            DropboxHierarchyMessage: dropbox ? null : "Saved Dropbox folder is unavailable. Choose a replacement.",
            LocalRootFiles: 0, DropboxRootFiles: 0);

    private static void CreateLocalTree(string root)
    {
        foreach (var relative in new[] { "Match", "LocalOnly", "OnlineOnly", "Attention", "Excluded",
                     "Very long folder name that must not displace the right chevron 0123456789/Child" })
            Directory.CreateDirectory(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(root)!, "SiblingOutside"));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(root)!, "A", "Child"));
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(root)!, "B"));
    }

    private static void UseCompactLocalTree(MainWindow window, string root)
    {
        window.LocalRootNodes.Clear();
        foreach (var node in CompactLocalTree(root)) window.LocalRootNodes.Add(node);
    }

    private static IReadOnlyList<LocalFolderNode> CompactLocalTree(string root) =>
    [
        LocalFolderNode.CreateForTest(root,
        [
            "Match", "LocalOnly", "OnlineOnly", "Attention", "Excluded", "DerivedUnavailable",
            "Very long folder name that must not displace the right chevron 0123456789/Child"
        ])
    ];

    private static void UseScrollChallengingLocalTree(MainWindow window, string targetRoot, string stateRoot)
    {
        window.LocalRootNodes.Clear();
        foreach (var index in Enumerable.Range(0, 24))
            window.LocalRootNodes.Add(LocalFolderNode.CreateForTest(
                Path.Combine(stateRoot, $"navigation-padding-{index:D2}"), []));
        window.LocalRootNodes.Add(LocalFolderNode.CreateForTest(targetRoot, []));
    }

    private static string PlanSnapshot(MainWindow window) => JsonSerializer.Serialize(new
    {
        Local = window.LocalGlobalPlanForTest,
        Dropbox = window.DropboxGlobalPlanForTest,
        window.PendingSelectionDefaultIncludedForTest,
        Rules = window.PendingSelectionRulesForTest
    });

    private static LocalFolderNode? FindLocal(MainWindow window, string path) =>
        LocalDescendants(window.LocalRootNodes).FirstOrDefault(node => !node.IsPlaceholder &&
            string.Equals(Path.GetFullPath(node.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    private static DropboxFolderNode? FindDropbox(MainWindow window, string path) =>
        DropboxDescendants(window.DropboxRootNodes).FirstOrDefault(node => !node.IsPlaceholder &&
            string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<LocalFolderNode> LocalDescendants(IEnumerable<LocalFolderNode> roots)
    {
        foreach (var root in roots) { yield return root; foreach (var child in LocalDescendants(root.Children)) yield return child; }
    }

    private static IEnumerable<DropboxFolderNode> DropboxDescendants(IEnumerable<DropboxFolderNode> roots)
    {
        foreach (var root in roots) { yield return root; foreach (var child in DropboxDescendants(root.Children)) yield return child; }
    }

    private static void RequireVisible(object? value, bool visible, string description)
    {
        var actual = value switch { LocalFolderNode local => local.IsFilterVisible,
            DropboxFolderNode dropbox => dropbox.IsFilterVisible, _ => throw new InvalidDataException(description + " is absent.") };
        if (actual != visible) throw new InvalidDataException(description + " has the wrong filter visibility.");
    }

    private static void RequireVisibleSet<T>(IEnumerable<T> values, IEnumerable<string> expected,
        Func<T, string> path, string description)
    {
        static bool Visible(object value) => value switch
        {
            LocalFolderNode local => local.IsFilterVisible,
            DropboxFolderNode dropbox => dropbox.IsFilterVisible,
            _ => throw new InvalidDataException("Unsupported folder-node type in visible-set assertion.")
        };
        var actual = values.Where(item => Visible(item!)).Select(path)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        var wanted = expected.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description} differed. Expected [{string.Join(", ", wanted)}]; " +
                $"actual [{string.Join(", ", actual)}].");
    }

    private static void ExerciseBlockedLaunchSurfaces(MainWindow window, SyncPair pair, SyncPairStore pairStore,
        List<string> dispatches)
    {
        var before = dispatches.Count;
        Snapshot("initial");
        PumpTask(window, window.DispatchPairsForTestAsync([pair], "shared wrapper"));
        Snapshot("shared wrapper completed");
        Launch("Run_Click", "RunButton");
        Launch("RunPairNow_Click", "EditorSyncNowButton");
        Launch("RunAllPairsNow_Click", "SyncAllNowButton");
        PumpTask(window, window.SyncNowFromBackgroundAsync());
        Snapshot("tray completed");
        var scheduled = Invoke(window, "RunScheduledAsync", "scheduled",
            new HashSet<string>([pair.Id], StringComparer.Ordinal)) as Task<bool> ??
            throw new InvalidDataException("The scheduled sync entry point did not return its task.");
        PumpTask(window, scheduled);
        Snapshot("scheduled completed");
        var approvalCandidate = pair with { InitialReviewApproved = false };
        pairStore.SaveAsync(new SyncPairSet([approvalCandidate], approvalCandidate.Id)).GetAwaiter().GetResult();
        window.PrepareExistingPairEditorForTest(approvalCandidate);
        var stateBeforeApproval = JsonSerializer.Serialize(pairStore.Load());
        var approvalCalls = 0;
        MainWindow.BootstrapApprovalConfirmationForTest = _ => true;
        MainWindow.BootstrapApprovalForTest = (_, _) =>
        {
            approvalCalls++;
            return Task.FromResult(approvalCandidate with { InitialReviewApproved = true });
        };
        try
        {
            Invoke(window, "ApproveBootstrap_Click", Required(window, "ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(window, TimeSpan.FromMilliseconds(100));
        }
        finally
        {
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            MainWindow.BootstrapApprovalForTest = null;
        }
        if (approvalCalls != 0 || JsonSerializer.Serialize(pairStore.Load()) != stateBeforeApproval ||
            pairStore.Load().Pairs.Single().InitialReviewApproved != false)
            throw new InvalidDataException("A blocked pair reached first-merge approval or changed bootstrap state.");
        if (dispatches.Count != before)
            throw new InvalidDataException("A blocked pair reached a sync dispatcher through a real launch surface: " +
                JsonSerializer.Serialize(dispatches.Skip(before).ToArray()));

        void Launch(string method, string controlName)
        {
            var control = (Button)Required(window, controlName);
            Snapshot(method + " before");
            Invoke(window, method, control, new RoutedEventArgs(ButtonBase.ClickEvent));
            // Observe the actual command lifetime rather than treating 100 ms as
            // evidence that an async-void launch finished its awaited pair reload.
            PumpUntil(window, () => AutomationProperties.GetItemStatus(control) != "In progress",
                method + " did not finish its action-feedback lifetime.");
            Snapshot(method + " completed");
        }

        void Snapshot(string route)
        {
            var hostPairs = PumpTask(window, window.AuthorityHost.LoadPairsAsync());
            var legacy = pairStore.Load();
            Console.WriteLine("BLOCKED-LAUNCH " + JsonSerializer.Serialize(new
            {
                route, originalPairId = pair.Id,
                dispatchedIds = dispatches.Skip(before).ToArray(),
                hostIds = hostPairs.Pairs.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
                hostSelected = hostPairs.SelectedPairId,
                legacyIds = legacy.Pairs.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
                legacySelected = legacy.SelectedPairId,
                selectedRow = ((DataGrid)Required(window, "PairList")).SelectedItem is PairRow row ? row.Id : null,
                knownUnhealthy = window.PairKnownUnhealthyForTest(pair.Id),
                attentionSources = window.PairAttentionSourcesForTest(pair.Id)
            }));
        }
    }

    private static void AssertReachableInViewport(FrameworkElement control, ScrollViewer scroller,
        string description)
    {
        Rect bounds;
        try { bounds = Bounds(control, scroller); }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException($"{description} became detached from the visible pair editor.", exception);
        }
        var viewport = new Rect(0, 0, scroller.ViewportWidth, scroller.ViewportHeight);
        var visible = Rect.Intersect(bounds, viewport);
        if (visible.IsEmpty || visible.Width < Math.Min(28, control.ActualWidth) ||
            visible.Height < Math.Min(28, control.ActualHeight))
            throw new InvalidDataException($"{description} is not reachable in the visible editor viewport: " +
                $"bounds={bounds}; viewport={viewport}; intersection={visible}; " +
                $"control={control.ActualWidth:0.0}x{control.ActualHeight:0.0}; " +
                $"offset={scroller.HorizontalOffset:0.0},{scroller.VerticalOffset:0.0}; " +
                $"extent={scroller.ExtentWidth:0.0}x{scroller.ExtentHeight:0.0}.");
    }

    private static bool IsReachableInViewport(FrameworkElement control, ScrollViewer scroller)
    {
        if (!control.IsVisible || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
        try
        {
            var visible = Rect.Intersect(Bounds(control, scroller),
                new Rect(0, 0, scroller.ViewportWidth, scroller.ViewportHeight));
            return !visible.IsEmpty && visible.Width >= Math.Min(28, control.ActualWidth) &&
                   visible.Height >= Math.Min(28, control.ActualHeight);
        }
        catch (InvalidOperationException) { return false; }
    }

    private static TreeViewItem SelectedTreeItem<T>(TreeView tree, Func<T, bool> predicate)
    {
        var item = FindDescendants<TreeViewItem>(tree).FirstOrDefault(candidate =>
            candidate.IsSelected && candidate.DataContext is T value && predicate(value));
        return item ?? throw new InvalidDataException("The requested saved folder was not selected in its hierarchy.");
    }

    private static void AssertTreeItemInViewport(TreeViewItem item, TreeView tree, string description)
    {
        tree.UpdateLayout();
        var bounds = Bounds(item, tree);
        var viewport = new Rect(0, 0, tree.ActualWidth, tree.ActualHeight);
        var visible = Rect.Intersect(bounds, viewport);
        if (visible.IsEmpty || visible.Height < Math.Min(28, item.ActualHeight) || visible.Width < 28)
            throw new InvalidDataException($"The {description} was selected but remained outside the visible hierarchy.");
    }

    private static void ScrollTreeToStartAndRequireTargetOutside<T>(MainWindow window, TreeView tree,
        Func<T, bool> predicate, string description)
    {
        var scroller = FindDescendants<ScrollViewer>(tree).FirstOrDefault() ??
                       throw new InvalidDataException($"The {description} hierarchy has no scroll viewer.");
        scroller.ScrollToTop();
        window.UpdateLayout();
        Pump(window);
        var item = FindDescendants<TreeViewItem>(tree).FirstOrDefault(candidate =>
            candidate.DataContext is T value && predicate(value));
        if (item is null) return;
        var bounds = Bounds(item, tree);
        var viewport = new Rect(0, 0, tree.ActualWidth, tree.ActualHeight);
        var visible = Rect.Intersect(bounds, viewport);
        if (!visible.IsEmpty && visible.Height >= Math.Min(28, item.ActualHeight) && visible.Width >= 28)
            throw new InvalidDataException($"The {description} was already visible before production auto-navigation.");
    }

    private static void AssertFooterSeparation(MainWindow window, FrameworkElement loadMore, Button openDropbox,
        double width)
    {
        window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = width < 900 ? 650 : 790;
        loadMore.BringIntoView();
        window.UpdateLayout();
        Pump(window);
        var panel = Required(window, "DropboxRootPanel");
        var loadBounds = Bounds(loadMore, panel);
        var openBounds = Bounds(openDropbox, panel);
        var panelBounds = new Rect(0, 0, panel.ActualWidth, panel.ActualHeight);
        if (!loadMore.IsVisible || !openDropbox.IsVisible || loadBounds.IntersectsWith(openBounds) ||
            !panelBounds.Contains(loadBounds.TopLeft) || !panelBounds.Contains(loadBounds.BottomRight) ||
            !panelBounds.Contains(openBounds.TopLeft) || !panelBounds.Contains(openBounds.BottomRight))
            throw new InvalidDataException($"Dropbox footer actions overlap or clip at {width:0}px width.");
    }

    private static string FolderMark(CheckBox checkbox)
    {
        checkbox.ApplyTemplate();
        return (checkbox.Template.FindName("Mark", checkbox) as TextBlock)?.Text ??
            throw new InvalidDataException("The folder checkbox template did not expose its visible mark.");
    }

    private static void FocusAndAssertFolderControl(CheckBox checkbox, string description)
    {
        checkbox.Focus();
        Keyboard.Focus(checkbox);
        checkbox.UpdateLayout();
        if (!checkbox.IsKeyboardFocused ||
            checkbox.Template.FindName("FocusRing", checkbox) is not Border ring ||
            ring.BorderBrush is not SolidColorBrush actual ||
            Application.Current.Resources["FocusBrush"] is not SolidColorBrush expected ||
            actual.Color != expected.Color)
            throw new InvalidDataException($"The {description} has no visible keyboard focus state.");
    }

    private static void FocusAndAssertChevron(ToggleButton chevron, string description)
    {
        chevron.Focus();
        Keyboard.Focus(chevron);
        chevron.UpdateLayout();
        if (!chevron.IsKeyboardFocused ||
            chevron.Template.FindName("ChevronFocus", chevron) is not Border ring ||
            ring.BorderBrush is not SolidColorBrush actual ||
            Application.Current.Resources["FocusBrush"] is not SolidColorBrush expected ||
            actual.Color != expected.Color)
            throw new InvalidDataException($"The {description} has no visible keyboard focus state.");
    }

    private static FrameworkElement Required(FrameworkElement root, string name) =>
        root.FindName(name) as FrameworkElement ?? throw new InvalidDataException("Missing UI element " + name + '.');

    private static void NavigateToPairs(MainWindow window)
    {
        var navigation = (ListBox)Required(window, "Navigation");
        navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item =>
            string.Equals(item.Tag?.ToString(), "Pairs", StringComparison.Ordinal));
        Pump(window);
    }

    private static object? Invoke(MainWindow window, string name, params object?[] args)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
                     throw new MissingMethodException(nameof(MainWindow), name);
        return method.Invoke(window, args);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static void Pump(MainWindow window)
    {
        var frame = new DispatcherFrame();
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Render,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpFor(MainWindow window, TimeSpan duration)
    {
        var until = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (until.Elapsed >= duration) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void PumpUntil(MainWindow window, Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(15)) PumpFor(window, TimeSpan.FromMilliseconds(25));
        if (!condition()) throw new TimeoutException(failure);
    }

    private static void PumpTask(MainWindow window, Task task)
    {
        PumpUntil(window, () => task.IsCompleted, "A focused UI task did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static T PumpTask<T>(MainWindow window, Task<T> task)
    {
        PumpUntil(window, () => task.IsCompleted, "A focused UI task did not complete.");
        return task.GetAwaiter().GetResult();
    }

    internal static void Capture(MainWindow window, string path)
    {
        Pump(window);
        CaptureVisual((FrameworkElement)window.Content, path);
    }

    internal static void CaptureVisual(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Round(visual.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(visual.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX,
            dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Step(string outputRoot, string name)
    {
        File.AppendAllText(Path.Combine(outputRoot, "progress.log"), name + Environment.NewLine);
        Console.Error.WriteLine("STEP " + name);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var sample = value / 255d;
            return sample <= 0.03928 ? sample / 12.92 : Math.Pow((sample + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private sealed class HierarchyBrowser : IDropboxFolderBrowserService
    {
        internal int CreateCalls { get; private set; }
        private static readonly IReadOnlyDictionary<string, DropboxFolderPage> Pages =
            new Dictionary<string, DropboxFolderPage>(StringComparer.Ordinal)
            {
                ["root"] = BuildRootPage(),
                ["id:remote-name"] = new([
                    new("Match", "/RemoteName/Match", "id:match"),
                    new("DropboxOnly", "/RemoteName/DropboxOnly", "id:dropbox-only"),
                    new("Attention", "/RemoteName/Attention", "id:attention"),
                    new("Excluded", "/RemoteName/Excluded", "id:excluded")
                ], null),
                ["id:long"] = new([
                    new("Deep child folder with a long name 0123456789",
                        "/Very long Dropbox folder name that must keep its chevron visible 0123456789/Deep child folder with a long name 0123456789",
                        "id:long-child")
                ], null)
            };

        private static DropboxFolderPage BuildRootPage()
        {
            var folders = Enumerable.Range(0, 24)
                .Select(index => new DropboxFolderEntry($"Navigation padding {index:D2}",
                    $"/Navigation padding {index:D2}", $"id:navigation-padding-{index:D2}"))
                .Append(new DropboxFolderEntry("RemoteName", "/RemoteName", "id:remote-name"))
                .Append(new DropboxFolderEntry("Match", "/Match", "id:top-match"))
                .Append(new DropboxFolderEntry(
                    "Very long Dropbox folder name that must keep its chevron visible 0123456789",
                    "/Very long Dropbox folder name that must keep its chevron visible 0123456789", "id:long"))
                .Append(new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
                    "id:recovery-from-chooser"))
                .ToArray();
            return new DropboxFolderPage(folders, "root-page-2");
        }

        public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Pages.GetValueOrDefault(folderId ?? "root") ?? new DropboxFolderPage([], null));
        public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult(cursor == "root-page-2"
                ? new DropboxFolderPage([
                    new("PagedRoot", "/PagedRoot", "id:paged-root"),
                    new("Pictures", "/Pictures", "id:pictures")
                ], null)
                : new DropboxFolderPage([], null));
        public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
            CancellationToken cancellationToken = default) => ListFoldersAsync(folderId, cancellationToken);
        public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
            CancellationToken cancellationToken = default) => ContinueFoldersAsync(cursor, cancellationToken);
        public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var folder in (await ListFoldersAsync(folderId, cancellationToken)).Folders) yield return folder;
        }
        public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            throw new InvalidOperationException("The focused UI fixture does not permit provider folder creation.");
        }
    }
}
