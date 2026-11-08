using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// Original AUD15: actual Host producers, retained command readback and actual Home decisions.
// The existing Core remote supplies INPUT only. No seeded command/status, replacement query,
// result dispatcher, private Host call, live credential, provider or product database is used.
// This runs inside WorkflowSurfacingChecks' ONE Application; it owns a separate fixture authority.
internal static class HomeProducerProjectionChecks
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        var observations = new List<object>();
        var checks = new List<string>();
        var transport = new DenyNetwork();
        using var http = new HttpClient(transport);
        var a = Binding("aud15-account-a");
        var b = Binding("aud15-account-b");
        b = b with { CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(b.ConnectionId) };
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        Pump(connections.SaveNewAsync(a, deadline.Token));
        Pump(connections.SaveAsync(b, deadline.Token));
        var credentials = new ControlledCredentials([a, b]);
        var inputs = new Dictionary<string, RemoteInput>(StringComparer.Ordinal);
        var pairs = new SyncPairStore(instance.StateAuthority);
        var installation = Pump(pairs.GetOrCreateInstallationIdAsync(deadline.Token));
        var first = CloudPair(root, "61700000000000000000000000000151", "AUD15 First Account", a, installation);
        var other = CloudPair(root, "61700000000000000000000000000152", "AUD15 Other Account", b, installation);
        var firstInput = RemoteInput.Create();
        var otherInput = RemoteInput.Create();
        inputs.Add(first.Id, firstInput);
        inputs.Add(other.Id, otherInput);
        firstInput.Put("first-download.txt", "AUD15 actual reviewed download");
        File.WriteAllText(Path.Combine(first.LocalRoot, "first-upload.txt"), "AUD15 actual reviewed upload");
        // Current multi-account definitions belong to the authority database below, not the legacy
        // single-connection import store. Leave that store empty so its migration guard is not bypassed.
        Pump(new SyncAutomationStore(instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false), deadline.Token));
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairs,
            new SyncRuntimeStateStore(instance.StateAuthority), connections, credentials, database,
            (pair, _) => inputs[pair.Id].Store, googleDriveCredentialStore: credentials);
        Pump(host.LoadPairsAsync(deadline.Token));
        Pump(host.SaveTransferSettingsAsync(host.LoadTransferSettings() with { ResourceMode = "Performance" }, deadline.Token));
        foreach (var pair in new[] { first, other })
        {
            Pump(database.RegisterPairAsync(pair.Id, pair.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(pair), deadline.Token));
            Pump(database.RetainPairDefinitionAsync(pair, pair.Id == first.Id, deadline.Token));
        }
        // Both raw retained definitions must obtain their OWN root/account authority through the public
        // inventory producer before a cross-pair policy/approval comparison. A manually seeded definition
        // alone is not a verified cloud identity, even when its display path or connection label looks valid.
        // This exercises real preparation for both accounts; it does not seed root rows or bypass refusal.
        Pump(host.RefreshBootstrapInventoryAsync(other.Id, cancellationToken: deadline.Token));
        Pump(host.RefreshBootstrapInventoryAsync(first.Id, cancellationToken: deadline.Token));
        other = Pump(host.ApproveBootstrapMergeAsync(other.Id, deadline.Token));
        var priorBrowser = MainWindow.PairFolderBrowserFactoryForTest;
        var priorRoots = MainWindow.LocalRootNodeFactoryForTest;
        var priorAccountStatus = MainWindow.DropboxConnectionStatusLoaderForTest;
        var browser = new ScriptedFolderBrowserService
        { ListHandler = (_, _) => Task.FromResult(new DropboxFolderPage([], null)) };
        MainWindow? window = null;
        Task? runningUi = null;
        var downloadEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDownload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? failure = null;
        try
        {
            // Only navigation INPUT is isolated. Every execution/projection seam must remain unhooked.
            Check(MainWindow.PairRunDispatcherForTest is null && MainWindow.BootstrapActionPlanForTest is null &&
                  MainWindow.BootstrapReviewPageLoaderForTest is null && MainWindow.BootstrapReviewLoaderForTest is null &&
                  MainWindow.SummaryHistoryLoaderForTest is null && MainWindow.ActivityProjectionLoaderForTest is null &&
                  MainWindow.RunHistoryLoaderForTest is null,
                "AUD15/PREP Host execution and retained Home query seams are not replaced");
            MainWindow.PairFolderBrowserFactoryForTest = _ => Task.FromResult<IDropboxFolderBrowserService>(browser);
            MainWindow.LocalRootNodeFactoryForTest = path => [LocalFolderNode.CreateForTest(path ?? first.LocalRoot, [])];
            // Account-health input is local retained metadata; no synthetic credential is sent to OAuth.
            // Actual sync producers, retained outcomes and Home projection decisions remain unhooked.
            MainWindow.DropboxConnectionStatusLoaderForTest = _ =>
                host.GetDropboxConnectionStatusAsync(verifyLive: false,
                    connectionId: window is null ? null : Field<string?>(window, "_selectedConnectionId"));
            window = new MainWindow(instance.StateAuthority, theme, http, host)
            { Width = 1240, Height = 790, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            window.LoadPairSetForTest(Pump(host.LoadPairsAsync(deadline.Token)));
            Call(window, "RefreshAccountPickers");
            Pump((Task)Call(window, "RefreshConnectionStateAsync", false)!);
            Select(window, null);

            // AUD15/first-sync: capture the real complete retained inventory and submit its own
            // recommendations through the public Host consent path. The UI then reads the actual command.
            Pump(host.RefreshBootstrapInventoryAsync(first.Id, cancellationToken: deadline.Token));
            var page = Pump(host.LoadBootstrapReviewPageAsync(first.Id, cancellationToken: deadline.Token));
            Check(page.InventoryComplete && !page.HasMore && page.LocalScanGenerationId is not null &&
                  page.DropboxScanGenerationId is not null && page.Items.Any(item => item.RelativePath == "first-download.txt") &&
                  page.Items.Any(item => item.RelativePath == "first-upload.txt"),
                "AUD15/first-sync actual complete inventory contains both real input files before any transfer");
            var plan = new SyncBootstrapActionPlan(first.Id, page.LocalScanGenerationId!, page.DropboxScanGenerationId!,
                page.Items.Where(item => SyncBootstrapActions.IsActionable(item.Group))
                    .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group, item.Recommendation,
                        SyncBootstrapActions.IsMove(item.Recommendation) ? item.Move : null)).ToArray());
            var beforeFirst = RunIds(host);
            var applied = Pump(host.RunBootstrapActionPlanAsync(plan, cancellationToken: deadline.Token));
            var firstRun = NewRun(host, beforeFirst);
            Check(applied.FirstSyncComplete && applied.Result is { Uploaded: 1, Downloaded: 1, Blocked: 0, Conflicts: 0, Stopped: false } &&
                  File.ReadAllText(Path.Combine(first.LocalRoot, "first-download.txt")) == "AUD15 actual reviewed download" &&
                  firstInput.ReadText("first-upload.txt") == "AUD15 actual reviewed upload",
                "AUD15/first-sync public Host actually completes reviewed upload/download and exact byte readback");
            using (var result = JsonDocument.Parse(firstRun.ResultJson!))
                Check(firstRun.Status == "completed" && result.RootElement.GetProperty("firstSyncComplete").GetBoolean() &&
                      result.RootElement.GetProperty("reviewedWorkComplete").GetBoolean(),
                    "AUD15/first-sync actual retained command binds completed reviewed-work consent, not an ordinary clean run");
            var consent = Pump(host.Authority.LoadConsentBindingForCommandAsync(firstRun.Id, deadline.Token))
                ?? throw new InvalidDataException("The actual reviewed Host command lost its original consent.");
            var consentPins = Pump(host.Authority.LoadConsentItemPinsAsync(consent.ConsentId,
                Enum.GetValues<SyncAuthorityConsentItemLifecycle>(), 16, deadline.Token));
            Check(consentPins.Count == 2 && consentPins.All(item =>
                      item.Lifecycle == SyncAuthorityConsentItemLifecycle.Completed &&
                      item.IssuedUtc is not null && item.CompletedUtc is not null &&
                      item.OperationCount == 1 && item.OperationStatus == "completed"),
                "I11 actual reviewed Host operations bind, issue and complete both original consent items");
            var reissueRefused = false;
            try
            {
                Pump(host.Authority.LoadConsentItemsAsync(consent.ConsentId,
                    consentPins.Select(item => item.Ordinal).ToArray(),
                    SyncAuthorityConsentSelectionMode.ReissueSelection, deadline.Token));
            }
            catch (InvalidDataException error) when (error.Message.Contains("completed") &&
                                                    error.Message.Contains("cannot authorize a new attempt"))
            { reissueRefused = true; }
            Check(reissueRefused, "I11 completed original consent items cannot authorize another attempt");
            Pump(window.RefreshAfterCliCommandAsync());
            Select(window, a.ConnectionId);
            CheckSummary(window, firstRun, first.Name, applied.Result.Uploaded, applied.Result.Downloaded, "Finished");
            CheckSuccess(window, host, null, [first.Id], "AUD15/first-sync reviewed completion is visible but does not claim ordinary last-success");
            Observe("first-sync", firstRun, first.Id);
            first = Pump(host.LoadPairsAsync(deadline.Token)).Pairs.Single(pair => pair.Id == first.Id);

            // AUD15/scoped: actual shared UI run entry calls public Host.RunPairsAsync, and its
            // real action callback drives Home's pair/file/timing/activity actions while input is held.
            const string activeFile = "scoped-progress.txt";
            firstInput.Put(activeFile, "AUD15 scoped real transfer");
            firstInput.BeforeDownload(async token =>
            {
                downloadEntered.TrySetResult(true);
                await releaseDownload.Task.WaitAsync(token);
            });
            var beforeScoped = RunIds(host);
            runningUi = (Task)Call(window, "RunPairsAsync", new[] { first }, "AUD15 actual scoped producer")!;
            try
            {
                Until(() => downloadEntered.Task.IsCompleted &&
                    Element<TextBlock>(window, "PhaseText").Text.Contains(activeFile, StringComparison.Ordinal),
                    "real scoped download/current-file callback");
                var active = host.ActiveRun ?? throw new InvalidDataException("AUD15 held effect has no actual retained active run.");
                observations.Add(new { subcase = "scoped-active", runId = active.Id, pairId = first.Id,
                    file = activeFile, phase = Element<TextBlock>(window, "PhaseText").Text,
                    timing = Field<TextBlock>(window, "_homeTiming").Text });
                Check(window.RunActiveForTest && Field<string>(window, "_workspaceRunningPairId") == first.Id &&
                      Field<TextBlock>(window, "_homeTiming").Text.StartsWith("Run Started:", StringComparison.Ordinal) &&
                      Field<Button>(window, "_homePairLink").Visibility == Visibility.Visible &&
                      Field<Button>(window, "_homePending").Visibility == Visibility.Visible,
                    "AUD15/scoped actual Home shows the producing pair, current file, active timing and current-sync actions");
                Field<Button>(window, "_homePending").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(Field<TextBox>(window, "_operationLogRun").Text == active.Id &&
                      Field<TabControl>(window, "_activityTabs").SelectedIndex == 3,
                    "AUD15/scoped Home current-sync action routes to that actual active retained run, not an older command");
                Call(window, "NavigateTo", "Dashboard", true);
                Field<Button>(window, "_homePairLink").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(Element<DataGrid>(window, "PairList").SelectedItem is PairRow selected && selected.Id == first.Id,
                    "AUD15/scoped Home pair link opens the actual producing folder pair");
                Select(window, b.ConnectionId);
                Check(!Field<TextBlock>(window, "_homeTiming").Text.StartsWith("Run Started:", StringComparison.Ordinal) &&
                      Field<Button>(window, "_homePairLink").Visibility == Visibility.Collapsed &&
                      Element<TextBlock>(window, "PhaseText").Text.Contains("Another account is syncing", StringComparison.Ordinal),
                    "AUD15/scoped another account does not borrow the active pair/file or active timing");
                Select(window, a.ConnectionId);
            }
            finally { releaseDownload.TrySetResult(true); firstInput.BeforeDownload(null); Pump(runningUi); runningUi = null; }
            var scoped = NewRun(host, beforeScoped);
            CheckClean(scoped, [first.Id]);
            var aSuccess = Pump(database.LoadLastSuccessfulSyncUtcAsync(allowedPairIds: [first.Id]));
            Check(aSuccess == scoped.CompletedUtc && aSuccess is not null &&
                  File.ReadAllText(Path.Combine(first.LocalRoot, activeFile)) == "AUD15 scoped real transfer",
                "AUD15/scoped real ordinary producer establishes the retained successful timestamp and file bytes");
            Select(window, a.ConnectionId);
            CheckSuccess(window, host, aSuccess, [first.Id], "AUD15/scoped Home consumes account A's real success");
            CheckSummary(window, scoped, first.Name, 0, 1, "Finished");
            Select(window, b.ConnectionId);
            CheckSuccess(window, host, null, [other.Id], "AUD15/scoped unrelated account B cannot borrow account A's success");
            Observe("scoped-success", scoped, first.Id);

            // Establish B's own legitimate success, then run ONE actual mixed command. One pair
            // succeeds; the other receives a controlled metadata error. Neither aggregate nor
            // selected-account timestamps may advance to the failed mixed command.
            Select(window, b.ConnectionId);
            var beforeOther = RunIds(host);
            Pump((Task)Call(window, "RunPairsAsync", new[] { other }, "AUD15 other-account clean producer")!);
            var otherRun = NewRun(host, beforeOther);
            CheckClean(otherRun, [other.Id]);
            var bSuccess = otherRun.CompletedUtc;
            CheckSuccess(window, host, bSuccess, [other.Id], "AUD15/scoped B's own successful producer is consumed without A attribution");
            var allBeforeMixed = Pump(database.LoadLastSuccessfulSyncUtcAsync());
            var downloadsBeforeMixed = otherInput.Count("DownloadCount");
            firstInput.Put("mixed-success.txt", "AUD15 independent completed mixed file");
            const string refusal = "AUD15 controlled B metadata refusal";
            otherInput.BeforeList(_ => Task.FromException(new IOException(refusal)));
            Select(window, null);
            var beforeMixed = RunIds(host);
            try { Pump((Task)Call(window, "RunPairsAsync", new[] { first, other }, "AUD15 actual mixed producer")!); }
            finally { otherInput.BeforeList(null); }
            var mixed = NewRun(host, beforeMixed);
            using (var result = JsonDocument.Parse(mixed.ResultJson!))
                Check(result.RootElement.GetProperty("completedPairs").GetInt32() == 1 &&
                      result.RootElement.GetProperty("failedPairs").GetInt32() == 1 &&
                      mixed.RequestJson.Contains(first.Id, StringComparison.Ordinal) && mixed.RequestJson.Contains(other.Id, StringComparison.Ordinal) &&
                      mixed.ResultJson!.Contains(refusal, StringComparison.Ordinal),
                    "AUD15/mixed actual retained batch records one independent completion and one exact failure with both pair identities");
            Check(File.ReadAllText(Path.Combine(first.LocalRoot, "mixed-success.txt")) == "AUD15 independent completed mixed file" &&
                  otherInput.Count("DownloadCount") == downloadsBeforeMixed,
                "AUD15/mixed successful file completes independently while rejected account B issues no download");
            CheckSuccess(window, host, allBeforeMixed, null, "AUD15/mixed All Accounts does not promote partial batch completion to a clean success");
            Check(Field<TextBlock>(window, "_homeTitle").Text == "Needs Attention",
                "AUD15/mixed All Accounts Home exposes retained attention instead of declaring clean success");
            Observe("mixed-all", mixed, null);
            Select(window, a.ConnectionId);
            CheckSuccess(window, host, aSuccess, [first.Id], "AUD15/mixed scoped A keeps its earlier clean date despite its file finishing in the mixed command");
            CheckSummary(window, mixed, first.Name, 0, 1, "Finished");
            Observe("mixed-account-a", mixed, first.Id);
            Select(window, b.ConnectionId);
            CheckSuccess(window, host, bSuccess, [other.Id], "AUD15/mixed scoped B retains only its own earlier clean date");
            Check(Field<TextBlock>(window, "_homeTitle").Text == "Needs Attention" &&
                  Field<Button>(window, "_homeStateAction").Visibility == Visibility.Visible &&
                  Element<TextBlock>(window, "PhaseText").Text.Contains(refusal, StringComparison.Ordinal),
                "AUD15/mixed scoped failure preserves its exact retained reason and actionable Home attention");
            Field<Button>(window, "_homeStateAction").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Field<TextBox>(window, "_operationLogRun").Text == mixed.Id,
                "AUD15/mixed scoped Home failure action opens the real mixed command's retained run");
            Observe("mixed-account-b", mixed, other.Id);

            // AUD15/accountless: actual public endpoint bind + observation + approval + Host run.
            // The resulting real local-to-local payload participates in All Accounts only.
            var left = Directory.CreateDirectory(Path.Combine(root, "accountless-left")).FullName;
            var right = Directory.CreateDirectory(Path.Combine(root, "accountless-right")).FullName;
            File.WriteAllText(Path.Combine(left, "accountless.txt"), "AUD15 actual accountless payload");
            var endpointDraft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(Local(left), Local(right), SyncMode.TwoWay),
                "AUD15 Accountless Local Pair");
            var bound = Pump(host.BindEndpointPairAsync(endpointDraft, cancellationToken: deadline.Token));
            Pump(host.RefreshBootstrapInventoryAsync(bound.Pair.Id, cancellationToken: deadline.Token));
            var local = Pump(host.ApproveBootstrapMergeAsync(bound.Pair.Id, deadline.Token));
            Check(host.PairConnectionIds(local).Count == 0, "AUD15/accountless actual bound local pair has no provider account attribution");
            window.LoadPairSetForTest(Pump(host.LoadPairsAsync(deadline.Token)));
            Select(window, null);
            var beforeLocal = RunIds(host);
            Pump((Task)Call(window, "RunPairsAsync", new[] { local }, "AUD15 actual accountless producer")!);
            var localRun = NewRun(host, beforeLocal);
            CheckClean(localRun, [local.Id]);
            Check(File.ReadAllText(Path.Combine(right, "accountless.txt")) == "AUD15 actual accountless payload" &&
                  localRun.CompletedUtc > allBeforeMixed,
                "AUD15/accountless public Host performs the real later local/local copy, not a seeded successful command");
            CheckSuccess(window, host, localRun.CompletedUtc, null, "AUD15/accountless All Accounts consumes the real local/local latest success");
            CheckSummary(window, localRun, local.Name, 1, 0, "Finished");
            Observe("accountless-all", localRun, local.Id);
            Select(window, a.ConnectionId);
            CheckSuccess(window, host, aSuccess, [first.Id], "AUD15/accountless selected A excludes the accountless success");
            CheckSummary(window, mixed, first.Name, 0, 1, "Finished");
            Select(window, b.ConnectionId);
            CheckSuccess(window, host, bSuccess, [other.Id], "AUD15/accountless selected B excludes both A and accountless success");
            CheckSummary(window, otherRun, other.Name, 0, 0, "Finished");
            Check(transport.Calls == 0 && credentials.Mutations == 0 &&
                  firstInput.Count("DeleteCount") == 0 && otherInput.Count("DeleteCount") == 0 &&
                  firstInput.Count("ArchiveCount") == 0 && otherInput.Count("ArchiveCount") == 0,
                "AUD15/safety no HTTP, protected credential mutation, deletion or archive was issued");
            Console.WriteLine("PASS original AUD15 actual Host producer -> Home projection; state " + root);
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            releaseDownload.TrySetResult(true);
            firstInput.BeforeDownload(null);
            otherInput.BeforeList(null);
            Exception? drainFailure = null;
            try { if (runningUi is not null) Pump(runningUi); }
            catch (Exception error) { drainFailure = error; }
            MainWindow.PairFolderBrowserFactoryForTest = priorBrowser;
            MainWindow.LocalRootNodeFactoryForTest = priorRoots;
            MainWindow.DropboxConnectionStatusLoaderForTest = priorAccountStatus;
            File.WriteAllText(Path.Combine(root, "home-producer-projection.json"), JsonSerializer.Serialize(new
            {
                original = "AUD15", entrypoints = new[] { "Host.RunBootstrapActionPlanAsync", "Host.RunPairsAsync",
                    "Host.BindEndpointPairAsync", "MainWindow.RunPairsAsync (real shared entry)", "MainWindow.RefreshAfterCliCommandAsync",
                    "Home account picker / current-sync / pair / attention actions" },
                observations, assertions = checks, failure, drainFailure = drainFailure?.ToString(),
                networkRequests = transport.Calls, credentialMutations = credentials.Mutations,
                firstInput = firstInput.Identity, otherInput = otherInput.Identity,
                testedBinaries = new[] { Binary(typeof(HomeProducerProjectionChecks).Assembly), Binary(typeof(MainWindow).Assembly),
                    Binary(typeof(SyncAuthorityRuntimeHost).Assembly) }
            }, new JsonSerializerOptions { WriteIndented = true }));
            window?.RequestProductExit();
            if (failure is null && drainFailure is not null)
                throw new InvalidDataException("AUD15 owned UI run did not drain after fixture input release; raw report retained.", drainFailure);
        }

        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        void CheckSuccess(MainWindow owner, SyncAuthorityRuntimeHost producer, DateTimeOffset? expected,
            IReadOnlyCollection<string>? scope, string label)
        {
            var retained = Pump(producer.Authority.LoadLastSuccessfulSyncUtcAsync(allowedPairIds: scope));
            Pump((Task)Call(owner, "RefreshWorkspaceScopeDataAsync")!);
            Check(retained == expected && Field<DateTimeOffset?>(owner, "_scopeLastSuccessfulUtc") == expected &&
                  Field<TextBlock>(owner, "_homeTiming").Text == (expected is { } utc
                    ? "Last successful sync: " + utc.ToLocalTime().ToString("g") : "Last successful sync: Not recorded"), label);
        }
        void CheckSummary(MainWindow owner, SyncAuthorityRunHistory producer, string pairName, int uploaded, int downloaded, string status)
        {
            var values = Summary(owner);
            using var json = JsonDocument.Parse(producer.ResultJson!);
            var retainedPair = json.RootElement.GetProperty("pairs").EnumerateArray()
                .Single(pair => pair.GetProperty("pairName").GetString() == pairName);
            var unchanged = retainedPair.GetProperty("unchanged").GetInt32();
            var needsAttention = retainedPair.GetProperty("blocked").GetInt32() + retainedPair.GetProperty("conflicts").GetInt32();
            Check(values.GetValueOrDefault("Pair:") == pairName && values.GetValueOrDefault("Status:") == status &&
                  retainedPair.GetProperty("uploaded").GetInt32() == uploaded && retainedPair.GetProperty("downloaded").GetInt32() == downloaded &&
                  values.GetValueOrDefault("Uploaded:") == uploaded.ToString("N0") &&
                  values.GetValueOrDefault("Downloaded:") == downloaded.ToString("N0") &&
                  values.GetValueOrDefault("Unchanged:") == unchanged.ToString("N0") &&
                  (values.GetValueOrDefault("Needs attention:") ?? values.GetValueOrDefault("Needs Attention:")) == needsAttention.ToString("N0"),
                "AUD15/counts Home's actual summary preserves producing pair and recorded transfer counts for " + pairName);
        }
        void Observe(string subcase, SyncAuthorityRunHistory run, string? pairId)
        {
            observations.Add(new { subcase, run, pairId, scope = Field<string?>(window!, "_workspaceAccountId"),
                title = Field<TextBlock>(window!, "_homeTitle").Text, timing = Field<TextBlock>(window!, "_homeTiming").Text,
                phase = Element<TextBlock>(window!, "PhaseText").Text, summary = Summary(window!),
                lastSuccess = Field<DateTimeOffset?>(window!, "_scopeLastSuccessfulUtc") });
            Console.WriteLine("AUD15 witness " + subcase + " command=" + run.Id + " pair=" + (pairId ?? "all"));
        }
    }

    private static DropboxConnectionBinding Binding(string account) => new DropboxConnectionBinding(
        DropboxProviderConfiguration.RegisteredClientId, account, account + "-root", account + "-home",
        DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
        DisplayName: account, Email: account + "@example.test").Normalize();

    private static object Binary(Assembly assembly) => new { path = assembly.Location,
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() };

    private static SyncPair CloudPair(string root, string id, string name, DropboxConnectionBinding account, string installation)
    {
        var localRoot = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        using var local = PilotDirectoryLease.Open(localRoot);
        // root736: the private staging ownership is NOT forged here. The definition is retained WITHOUT a
        // control namespace, so the ordinary Host authoring path prepares and JOURNALS the allocation (real
        // retained creation authority) when the pair is approved. A pre-created staging root carrying no
        // journalled allocation must be refused - that guard is exactly what this fixture used to trip by
        // calling SyncControlNamespace.BindLocalForTest, which materialised the directory without authority.
        return new SyncPair(id, name, localRoot, local.Identity.VolumeSerialNumber, local.Identity.FileId,
            "/" + id, "id:root-" + id, ConnectionId: account.ConnectionId, InitialReviewApproved: false,
            FilesOnDemandEnabled: false, RecoveryRoot: new SyncRemoteRecoveryRootBinding(
                SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery-" + id, account.RootNamespaceId));
    }
    private static SyncEndpoint Local(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(lease.Path, new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
    }
    private static HashSet<string> RunIds(SyncAuthorityRuntimeHost host) =>
        Pump(host.Authority.LoadRunHistoryAsync()).Select(run => run.Id).ToHashSet(StringComparer.Ordinal);
    private static SyncAuthorityRunHistory NewRun(SyncAuthorityRuntimeHost host, HashSet<string> before)
    {
        var candidates = Pump(host.Authority.LoadRunHistoryAsync()).Where(run => !before.Contains(run.Id) &&
            run.Kind is "run_pair" or "run_all").ToArray();
        if (candidates.Length != 1) throw new InvalidDataException("AUD15 expected one actual newly retained sync command; observed " + candidates.Length);
        return candidates[0];
    }
    private static void CheckClean(SyncAuthorityRunHistory run, string[] pairIds)
    {
        using var result = JsonDocument.Parse(run.ResultJson ?? throw new InvalidDataException("AUD15 missing retained result."));
        using var request = JsonDocument.Parse(run.RequestJson);
        if (run.Status != "completed" || run.CompletedUtc is null ||
            result.RootElement.GetProperty("failedPairs").GetInt32() != 0 ||
            result.RootElement.GetProperty("completedPairs").GetInt32() != pairIds.Length ||
            !request.RootElement.GetProperty("pairIds").EnumerateArray().Select(item => item.GetString())
                .OrderBy(id => id).SequenceEqual(pairIds.OrderBy(id => id)))
            throw new InvalidDataException("AUD15 expected actual clean retained producer with exact requested pair identities: " + run.ResultJson);
        Console.WriteLine("PASS AUD15 clean producer command=" + run.Id + " pairs=" + string.Join(",", pairIds));
    }
    private static Dictionary<string, string> Summary(MainWindow owner)
    {
        var grid = Field<ContentPresenter>(owner, "_homeLastSync").Content as Grid
            ?? throw new InvalidDataException("AUD15 actual Home labelled summary is unavailable.");
        return grid.Children.OfType<TextBlock>().Where(text => Grid.GetColumn(text) == 0)
            .ToDictionary(label => label.Text, label => grid.Children.OfType<TextBlock>()
                .FirstOrDefault(value => Grid.GetRow(value) == Grid.GetRow(label) && Grid.GetColumn(value) == 1)?.Text ?? "",
                StringComparer.Ordinal);
    }
    private static void Select(MainWindow owner, string? connection)
    {
        var picker = Field<List<ComboBox>>(owner, "_workspaceAccountPickers")[0];
        if (connection is null) picker.SelectedIndex = 0; else picker.SelectedValue = connection;
        Pump(Field<Task>(owner, "_workspaceScopeRefresh"));
        if (Field<string?>(owner, "_workspaceAccountId") != connection)
            throw new InvalidDataException("AUD15 actual account picker did not select the requested scope.");
        Call(owner, "NavigateTo", "Dashboard", true);
    }
    private static T Element<T>(FrameworkElement owner, string name) => (T)(owner.FindName(name) ?? throw new MissingMemberException(name));
    private static T Field<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().Name, name);
        return (T)field.GetValue(owner)!; // Nullable fields legitimately remain null; do not replace them with fixture status.
    }
    private static object? Call(object owner, string name, params object?[] arguments) =>
        (owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(owner.GetType().Name, name)).Invoke(owner, arguments);
    private static void Until(Func<bool> ready, string label)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("AUD15 timed out: " + label);
            Thread.Sleep(1);
        }
    }
    private static void Pump(Task task) { Until(() => task.IsCompleted, "actual Host/UI task"); task.GetAwaiter().GetResult(); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }

    private sealed class RemoteInput(ISyncRemoteStore store, object instance, Type type)
    {
        internal ISyncRemoteStore Store => store;
        internal object Identity => new { assembly = type.Assembly.Location,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location))).ToLowerInvariant(), inputType = type.FullName };
        internal static RemoteInput Create()
        {
            var factory = typeof(Program).GetMethod("CreateCoreTestRemote", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Existing Core provider-input factory is unavailable.");
            var value = ((ISyncRemoteStore Store, object Instance, Type Type))(factory.Invoke(null, [null]) ??
                throw new InvalidDataException("Existing Core provider-input factory returned no input."));
            Console.WriteLine("AUD15 controlled input binary " + value.Type.Assembly.Location + " sha256=" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(value.Type.Assembly.Location))).ToLowerInvariant());
            return new(value.Store, value.Instance, value.Type);
        }
        internal void Put(string path, string bytes) => Invoke("Put", path, bytes);
        internal string ReadText(string path) => (string)Invoke("ReadText", path)!;
        internal int Count(string name) => (int)(type.GetProperty(name)?.GetValue(instance) ?? throw new MissingMemberException(name));
        internal void BeforeDownload(Func<CancellationToken, Task>? input) => Set("BeforeDownloadForTest", input);
        internal void BeforeList(Func<CancellationToken, Task>? input) => Set("BeforeListEntriesForTest", input);
        private void Set(string name, object? value) => (type.GetProperty(name) ?? throw new MissingMemberException(name)).SetValue(instance, value);
        private object? Invoke(string name, params object[] values) => (type.GetMethod(name) ?? throw new MissingMethodException(name)).Invoke(instance, values);
    }
    private sealed class DenyNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("AUD15 controlled fixture denies every HTTP request."); }
    }
    private sealed class ControlledCredentials(DropboxConnectionBinding[] allowed) : IDropboxRefreshTokenStore, IGoogleDriveRefreshTokenStore
    {
        internal int Mutations;
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding selected) =>
            allowed.Any(binding => binding.ConnectionId == selected.ConnectionId && binding.AccountId == selected.AccountId) ? this :
                throw new InvalidOperationException("AUD15 denies unrelated credential route.");
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>("aud15-synthetic-input-not-a-credential");
        public Task<string?> ReadAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("AUD15 denies Google credential access.");
        public Task WriteAsync(string value, CancellationToken token = default) => RefuseMutation();
        public Task WriteAsync(string id, string value, CancellationToken token = default) => RefuseMutation();
        public Task DeleteAsync(CancellationToken token = default) => RefuseMutation();
        public Task DeleteAsync(string id, CancellationToken token = default) => RefuseMutation();
        private Task RefuseMutation() { Interlocked.Increment(ref Mutations); throw new InvalidOperationException("AUD15 denies protected credential mutation."); }
    }
}
