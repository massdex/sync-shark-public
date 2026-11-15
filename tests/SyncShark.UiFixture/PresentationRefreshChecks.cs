using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// SS-D12: real-consumer checks on the synchronous presentation scope and its one validated snapshot.
// Every count is the production store-load report of the real loader against real disposable state, so a
// hidden read is counted rather than assumed, and the out-of-scope control proves the counter measures
// actual I/O rather than an expected number. No provider request, credential read, signing, install or
// original user file is touched; every pair and account here is an isolated fixture identity.
internal static class PresentationRefreshChecks
{
    private const string RunLinkId = "command-presentation-run-link";
    private const string BrokenReason = "The paired folder is unavailable.";
    private const string MonitorReason = "The account needs reconnecting.";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var state = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = state.StateAuthority;
        var stateRoot = authority.StateRoot;
        var dropboxStore = new DropboxConnectionStore(authority);
        var googleStore = new GoogleDriveConnectionStore(authority);
        var preferencesStore = new UiPreferencesStore(authority);
        var dropbox = DropboxAccount("presentation-ui-primary", primary: true);
        var google = GoogleAccount("presentation-ui-google");
        Pump(dropboxStore.SaveNewAsync(dropbox));
        Pump(googleStore.SaveAsync(google));
        Pump(preferencesStore.SaveAsync(new UiPreferences(UiPreferencesStore.SystemTheme)));
        using var theme = new ThemeManager(new UiPreferencesStore(authority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        Action<bool, string> check = Check;
        var window = new MainWindow(authority, theme)
        {
            Width = 1240, Height = 790, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
        };
        TrayFlyoutWindow? tray = null;
        var report = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            window.Show();
            Drain();
            CheckStoresPerRefresh(window, dropbox.ConnectionId, google.ConnectionId, check, report);
            CheckNestedScopeReuse(window, dropbox.ConnectionId, dropbox.Email, google.ConnectionId, check);
            CheckRealTrayRenderEntry(window, check, report, value => tray = value);
            CheckPairCountIndependence(window, dropbox.ConnectionId, google.ConnectionId, dropbox.Email, check, report);
            CheckFreshStateBetweenRefreshes(window, dropboxStore, preferencesStore, dropbox, google, check);
            CheckRejectedAndRecoveredRefresh(window, dropboxStore, stateRoot, dropbox, google, check);
            CheckAttentionSurfacesRetained(window, dropbox.ConnectionId, check, report);
            CheckTimerCadenceRetained(window, check);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS", selector = "--presentation-refresh-checks", checks = passed,
                storeReadsPerRefresh = report.GetValueOrDefault("readsPerRefresh"),
                storeReadsWithOneHundredPairs = report.GetValueOrDefault("readsWithHundredPairs"),
                trayRenderStoreReads = report.GetValueOrDefault("trayRenderReads"),
                warmMs = report.GetValueOrDefault("warmMs"), refreshMs = report.GetValueOrDefault("refreshMs"),
                boundary = "real window, real tray flyout, real isolated stores, counted store loads; " +
                    "no provider, network, credential, signing, install or user-folder effect"
            }));
            return 0;
        }
        finally
        {
            SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null;
            MainWindow.QueuedInvocationHoldForTest = null;
            MainWindow.SuppressLoadedStartupForTest = false;
            tray?.Close();
            window.RequestProductExit();
            app.Shutdown();
        }
    }

    // ---- one projection per refresh, independent of the number of pairs --------------------------

    private static void CheckStoresPerRefresh(MainWindow window, string dropboxId, string googleId,
        Action<bool, string> check, Dictionary<string, object> report)
    {
        var pair = Pair("presentation-ui-pair", dropboxId);
        window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
        var reads = Measure(() => Call(window, "RefreshWorkspace"));
        report["readsPerRefresh"] = reads.ToArray();
        check(reads.Count == 3 && reads[0] == "dropbox-connections:1" &&
              reads[1] == "google-drive-connections:1" && reads[2] == "ui-preferences:1",
            "one real refresh performs exactly one validated projection of the three stores: " + Describe(reads));
        check(window.CurrentPresentationSnapshot is null,
            "no snapshot is retained after the refresh ends");
        check(window.PresentationAccountBinding(dropboxId)?.Email is not null,
            "the display lookup outside a refresh still resolves the real stored account");
        check(window.TrayScope(dropboxId).Label.Length > 0 && window.TrayScope(googleId).Label.Length > 0,
            "the tray scope names both real accounts from the same projection");

        // The guard that decides not to render at all must still run before any store read.
        var afterGuard = Measure(() => Call(window, "RefreshExplorerRootStates"));
        check(afterGuard.Count == 0,
            "the Explorer refresh keeps its not-installed guard before the scope, so it reads no store: " +
            Describe(afterGuard));
    }

    // ---- nested rendering inside one outer scope reuses the one immutable snapshot ---------------

    private static void CheckNestedScopeReuse(MainWindow window, string dropboxId, string dropboxEmail,
        string googleId, Action<bool, string> check)
    {
        var nested = new List<string>();
        SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, count) => nested.Add(store + ":" + count);
        try
        {
            using (var scope = window.OpenPresentationScope())
            {
                var captured = window.CurrentPresentationSnapshot;
                check(captured is not null && captured.AccountsByConnectionId.Count == 2 &&
                      nested.Count == 3 && nested.Select(value => value.Split(':')[0]).SequenceEqual(
                          new[] { "dropbox-connections", "google-drive-connections", "ui-preferences" }),
                    "the outer entry establishes one snapshot from exactly three store loads");
                nested.Clear(); // Measure additional reads by the nested calls, excluding the checked outer acquisition.
                Call(window, "RefreshFullApp");
                Call(window, "RefreshAccountPickers");
                Call(window, "RefreshWorkspaceAccountPickers");
                var accounts = window.TrayAccounts;
                var scopePresentation = window.TrayScope(dropboxId);
                var cases = window.TrayAttentionCases;
                var attention = window.TrayAttention;
                var cli = window.CliAttentionItems;
                var label = window.TrayLabelForPair("presentation-ui-pair");
                check(ReferenceEquals(captured, window.CurrentPresentationSnapshot),
                    "every nested render entry keeps the one immutable snapshot of the pass");
                check(nested.Count == 0,
                    "nested Home, account, attention, CLI and tray rendering re-reads no store: " + Describe(nested));
                check(accounts.Count == 2 && accounts.Any(account => account.Id == dropboxId) &&
                      scopePresentation.Label.Length > 0 && label.Length > 0 &&
                      cases.Count == attention.Count && cli.Count == cases.Count,
                    "the tray list, tray scope, pair label, attention list and CLI project the same shared snapshot");
            }
            check(window.CurrentPresentationSnapshot is null && nested.Count == 0,
                "the outer scope releases its snapshot and its attention result when the pass ends");
        }
        finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }
    }

    // ---- the real tray flyout render entry performs one projection -------------------------------

    private static void CheckRealTrayRenderEntry(MainWindow window, Action<bool, string> check,
        Dictionary<string, object> report, Action<TrayFlyoutWindow> created)
    {
        var tray = new TrayFlyoutWindow(window);
        created(tray);
        var reads = Measure(() => Call(tray, "Refresh"));
        report["trayRenderReads"] = reads.ToArray();
        check(reads.Count == 3,
            "one real tray flyout render performs exactly one validated projection: " + Describe(reads));
        var second = Measure(() => Call(tray, "Refresh"));
        check(second.Count == 3,
            "a second tray render reads again instead of reusing the previous pass: " + Describe(second));
    }

    // ---- one hundred pairs cost the same as one, in and out of a refresh -------------------------

    private static void CheckPairCountIndependence(MainWindow window, string dropboxId, string googleId,
        string dropboxEmail, Action<bool, string> check, Dictionary<string, object> report)
    {
        var many = Enumerable.Range(0, 100)
            .Select(index => Pair("presentation-ui-scale-" + index, index % 2 == 0 ? dropboxId : googleId))
            .ToArray();
        window.LoadPairSetForTest(new SyncPairSet(many, many[0].Id));
        var reads = Measure(() => Call(window, "RefreshWorkspace"));
        report["readsWithHundredPairs"] = reads.ToArray();
        check(reads.Count == 3 && reads[0] == "dropbox-connections:1" &&
              reads[1] == "google-drive-connections:1" && reads[2] == "ui-preferences:1",
            "one hundred folder pairs still cost exactly one projection: " + Describe(reads));

        // Warmup is separated from the measured repetitions.
        Call(window, "RefreshWorkspace");
        var warm = Stopwatch.StartNew();
        Call(window, "RefreshWorkspace");
        warm.Stop();
        var elapsed = new List<double>();
        var repeated = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var readsForPass = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, count) => readsForPass.Add(store + ":" + count);
            try { Call(window, "RefreshWorkspace"); }
            finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }
            stopwatch.Stop();
            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
            repeated.Add(readsForPass.Count.ToString());
        }
        report["warmMs"] = Math.Round(warm.Elapsed.TotalMilliseconds, 3);
        report["refreshMs"] = elapsed.Select(value => Math.Round(value, 3)).ToArray();
        check(repeated.All(count => count == "3"),
            "every repeated refresh keeps the fixed three-store projection: " + string.Join(", ", repeated));
        check(elapsed.All(value => value < 2000),
            "a refresh over one hundred pairs completed on this machine within two seconds: " +
            string.Join(", ", elapsed.Select(value => value.ToString("N1") + "ms")));

        // Inside one pass, one hundred per-pair display lookups add no read at all.
        var inner = new List<string>();
        SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, count) => inner.Add(store + ":" + count);
        try
        {
            using (window.OpenPresentationScope())
            {
                check(inner.Count == 3 && inner.Select(value => value.Split(':')[0]).SequenceEqual(
                        new[] { "dropbox-connections", "google-drive-connections", "ui-preferences" }),
                    "the scaled outer scope acquires exactly one three-store projection");
                inner.Clear();
                for (var index = 0; index < many.Length; index++) _ = window.PresentationPairAccountPaused(many[index]);
                check(window.PresentationAccountBinding(many[0].ConnectionId)?.Email == dropboxEmail &&
                      window.PresentationAccountBinding(many[1].ConnectionId)?.Email is not null,
                    "a scaled pair resolves its own account from the shared projection");
            }
        }
        finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }
        check(inner.Count == 0,
            "one hundred per-pair account and pause lookups inside one pass add no store read: " + Describe(inner));

        // Outside a pass the same lookup opens exactly one bounded fresh projection and caches nothing,
        // so the counted number is a real measurement rather than an expected constant.
        var outside = Measure(() => { _ = window.PresentationAccountBinding(dropboxId); });
        check(outside.Count == 3,
            "the same display lookup outside a pass opens one bounded fresh projection: " + Describe(outside));
        var outsideAgain = Measure(() => { _ = window.PresentationAccountBinding(dropboxId); });
        check(outsideAgain.Count == 3,
            "nothing is cached between passes: the next lookup reads the stores again: " + Describe(outsideAgain));
    }

    // ---- the next refresh reports what is actually stored ----------------------------------------

    private static void CheckFreshStateBetweenRefreshes(MainWindow window, DropboxConnectionStore dropboxStore,
        UiPreferencesStore preferencesStore, DropboxConnectionBinding dropbox, GoogleDriveConnectionBinding google,
        Action<bool, string> check)
    {
        // A rebind of the stored account collection is observed at the next refresh.
        var added = DropboxAccount("presentation-ui-added", primary: false);
        Pump(dropboxStore.SaveAsync(added));
        Call(window, "RefreshAccountPickers");
        check(window.TrayAccounts.Any(account => account.Id == added.ConnectionId) &&
              window.PresentationAccounts.Count == 3,
            "a rebound account is observed at the next refresh without a restart");

        // An account pause saved now is the displayed pause truth of the next refresh.
        Pump(preferencesStore.SaveAsync(new UiPreferences(UiPreferencesStore.SystemTheme,
            AccountPauses: [new SyncAccountPause(google.ConnectionId, true, null)])));
        check(window.TrayScope(google.ConnectionId).OwnPause && !window.TrayScope(dropbox.ConnectionId).OwnPause,
            "the next refresh displays the newly saved account pause and no other account's pause");

        // A timed pause that has already expired is never displayed as active.
        Pump(preferencesStore.SaveAsync(new UiPreferences(UiPreferencesStore.SystemTheme,
            AccountPauses: [new SyncAccountPause(google.ConnectionId, false, DateTimeOffset.UtcNow.AddMinutes(-5))])));
        Call(window, "RefreshAccountPickers");
        check(!window.TrayScope(google.ConnectionId).OwnPause,
            "an expired timed pause is not displayed as active on the next refresh");

        // A global pause saved now is projected by the next pass from the same preferences.
        Pump(preferencesStore.SaveAsync(new UiPreferences(UiPreferencesStore.SystemTheme, PauseUntilResumed: true)));
        using (window.OpenPresentationScope())
            check(window.CurrentPresentationSnapshot!.GlobalPaused(DateTimeOffset.UtcNow),
                "a global pause saved now is projected by the next pass");
        check(!window.GlobalPauseActive,
            "the pass projects the stored global pause without rewriting the window's own cached pause authority");

        // Restore the display baseline: no pause, one rebound account.
        Pump(preferencesStore.SaveAsync(new UiPreferences(UiPreferencesStore.SystemTheme)));
        Call(window, "RefreshAccountPickers");
        check(!window.TrayScope(google.ConnectionId).Paused,
            "clearing the pause is observed at the next refresh as well");

        // Accountless local/local, explicit local/cloud and cloud/cloud keep their exact scope semantics.
        var accountless = Pair("presentation-ui-accountless", "") with { Provider = SyncProviders.Local };
        var localToCloud = Pair("presentation-ui-local-cloud", google.ConnectionId);
        var cloudToCloud = Pair("presentation-ui-cloud-cloud", dropbox.ConnectionId);
        window.LoadPairSetForTest(new SyncPairSet([accountless, localToCloud, cloudToCloud], cloudToCloud.Id));
        Call(window, "RefreshWorkspace");
        check(window.TrayMatchesAccount(accountless.Id, null) &&
              !window.TrayMatchesAccount(accountless.Id, google.ConnectionId) &&
              window.TrayMatchesAccount(localToCloud.Id, google.ConnectionId) &&
              window.TrayMatchesAccount(cloudToCloud.Id, dropbox.ConnectionId),
            "accountless local/local, explicit local/cloud and cloud/cloud keep their exact account scope");
    }

    // ---- a rejected projection supplies nothing, and valid state recovers ------------------------

    private static void CheckRejectedAndRecoveredRefresh(MainWindow window, DropboxConnectionStore dropboxStore,
        string stateRoot, DropboxConnectionBinding dropbox, GoogleDriveConnectionBinding google,
        Action<bool, string> check)
    {
        Call(window, "RefreshWorkspace");
        var previousTitle = Field<TextBlock>(window, "_homeTitle").Text;
        var retainedSlots = new[] { "a", "b" }.Select(slot =>
            SlotPath(stateRoot, "dropbox-connection", slot)).Where(File.Exists)
            .ToDictionary(path => path, File.ReadAllBytes);
        CorruptSlots(stateRoot, "dropbox-connection");
        var corruptReads = new List<string>();
        Exception? failure = null;
        SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, count) => corruptReads.Add(store + ":" + count);
        try { Call(window, "RefreshWorkspace"); }
        catch (Exception error) { failure = error is TargetInvocationException { InnerException: { } inner } ? inner : error; }
        finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }
        check(failure is InvalidDataException,
            "corrupt current slots reject the next refresh's projection instead of presenting stale data: " +
            (failure?.GetType().Name ?? "no failure"));
        check(corruptReads.Count == 0,
            "the rejected refresh supplies no partially loaded account or preference data: " + Describe(corruptReads));
        check(Field<object?>(window, "_presentationSnapshot") is null,
            "the rejected refresh publishes no snapshot and retains nothing");
        var stale = false;
        try { stale = window.PresentationAccountBinding(dropbox.ConnectionId) is not null; }
        catch (InvalidDataException) { }
        check(!stale, "after corruption no display lookup supplies the previous binding or preference");
        check(Field<TextBlock>(window, "_homeTitle").Text == previousTitle,
            "the failed refresh changes no visible title, so it cannot present a fresh success state");

        // Restoring valid state recovers on the next refresh without a restart.
        // Preserve the authority's bound NTFS objects while restoring this synthetic corruption.
        foreach (var slot in retainedSlots) File.WriteAllBytes(slot.Key, slot.Value);
        Pump(dropboxStore.DeleteAsync());
        var recovered = DropboxAccount("presentation-ui-recovered", primary: true);
        Pump(dropboxStore.SaveNewAsync(recovered));
        var recoveredReads = Measure(() => Call(window, "RefreshWorkspace"));
        check(recoveredReads.Count == 3 &&
              window.PresentationAccountBinding(recovered.ConnectionId)?.Email == recovered.Email,
            "restoring valid state recovers the next refresh without a restart: " + Describe(recoveredReads));
        check(window.PresentationAccountBinding(google.ConnectionId)?.Email == google.Email &&
              !window.TrayAccounts.Any(account => account.Id == dropbox.ConnectionId),
            "the recovered refresh supplies the restored account and never the replaced one");
    }

    // ---- case counts, reasons and exact run links survive the shared projection -------------------

    private static void CheckAttentionSurfacesRetained(MainWindow window, string dropboxId,
        Action<bool, string> check, Dictionary<string, object> report)
    {
        var reviewPair = Pair("presentation-ui-review", dropboxId) with { InitialReviewApproved = false };
        var brokenPair = Pair("presentation-ui-broken", dropboxId) with { InitialReviewApproved = false };
        var conflictPair = Pair("presentation-ui-conflict", dropboxId) with { InitialReviewApproved = true };
        window.LoadPairSetForTest(new SyncPairSet([reviewPair, brokenPair, conflictPair], reviewPair.Id));
        window.SetPairAttentionSourcesForTest(brokenPair.Id, BrokenReason, MonitorReason, null, false);
        window.ConflictRows.Add(new DecisionRow(conflictPair.Id, conflictPair.Name, "report.pdf", "Conflict",
            "Both versions changed."));
        Set(window, "_commandProblemsByPair", new Dictionary<string, SyncCommandProblem>(StringComparer.Ordinal)
        {
            [conflictPair.Id] = new SyncCommandProblem("pair_run_failed", "Transferring",
                "Two files could not be uploaded.", "Check the account connection.", RunLinkId, conflictPair.Id,
                DateTimeOffset.UtcNow)
        });
        Set(window, "_attentionFingerprint", string.Empty);
        Call(window, "RefreshFullApp");
        Call(window, "RefreshWorkspace");

        var cases = window.TrayAttentionCases;
        var cli = window.CliAttentionItems;
        var trayPairs = window.TrayAttention;
        report["caseCount"] = cases.Count;
        check(cases.Any(item => item.Pair.Id == conflictPair.Id && item.RunId == RunLinkId && item.IsProblem),
            "the exact retained run link remains on the shared attention case");
        check(cli.Any(item => item.PairId == conflictPair.Id && item.RunId == RunLinkId && item.Title == "Sync needs attention"),
            "the same exact run link and reason reach the CLI attention projection");
        check(cases.Any(item => item.Pair.Id == reviewPair.Id && !item.IsProblem) &&
              cases.Any(item => item.Pair.Id == brokenPair.Id && item.IsProblem &&
                  item.Description.Length > 0),
            "first-sync review and confirmed folder problem cases keep their reasons");
        check(!cases.Any(item => item.Pair.Id == brokenPair.Id && !item.IsProblem),
            "the blocked folder problem still suppresses this pair's first-sync review row");
        check(trayPairs.Count == cases.Select(item => item.Pair.Id).Distinct(StringComparer.Ordinal).Count(),
            "the tray pair projection stays the distinct-pair projection of the same cases");
        check(window.TrayAttentionCases.Count == cases.Count && Field<StackPanel>(window, "_homeAttention").Children.Count > 1,
            "Home and the Attention list render the same shared case set");

        // The once-per-pass result is one immutable list reused by every consumer in the pass.
        using (window.OpenPresentationScope())
        {
            var first = window.PresentationAttentionCases;
            check(ReferenceEquals(first, window.PresentationAttentionCases) && first.Count == cases.Count,
                "the pass evaluates its attention set once and every consumer reuses it");
        }
        check(Field<object?>(window, "_presentationAttentionCases") is null,
            "no attention result is retained outside a pass, so a later change can never reuse it");
        check(window.PresentationAttentionCases.Count == cases.Count &&
              Field<object?>(window, "_presentationAttentionCases") is null,
            "outside a pass the attention set is rebuilt for that read and caching nothing");
    }

    // ---- the one-second cadence and its duties are unchanged -------------------------------------

    private static void CheckTimerCadenceRetained(MainWindow window, Action<bool, string> check)
    {
        var timer = Field<DispatcherTimer>(window, "_workspaceTimer");
        check(timer.Interval == TimeSpan.FromSeconds(1) && timer.IsEnabled,
            "the one-second workspace cadence that drives pause deadlines, progress and local-root health is unchanged");
        check(window.CurrentPresentationSnapshot is null,
            "the cadence leaves no presentation state behind between ticks");
    }

    // ---- shared fixture helpers ------------------------------------------------------------------

    private static List<string> Measure(Action action)
    {
        var reads = new List<string>();
        // One callback is one completed store load. Its integer payload is the number of parsed records
        // (zero account pauses is legitimate), not the number of I/O calls.
        SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, _) => reads.Add(store + ":1");
        try { action(); }
        finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }
        return reads;
    }

    private static string Describe(IReadOnlyList<string> reads) =>
        reads.Count == 0 ? "no store read" : string.Join(", ", reads);

    private static string SlotPath(string stateRoot, string baseName, string slot) =>
        Path.Combine(stateRoot, baseName + "-" + slot + ".json");

    private static void CorruptSlots(string stateRoot, string baseName)
    {
        foreach (var slot in new[] { "a", "b" })
            File.WriteAllBytes(SlotPath(stateRoot, baseName, slot), System.Text.Encoding.UTF8.GetBytes("corrupt-slot"));
    }

    private static SyncPair Pair(string id, string connectionId) => new(id, id,
        Path.Combine(Path.GetTempPath(), "sync-shark-presentation-ui", id), 1, 1, "/Presentation", "id:presentation",
        ConnectionId: connectionId);

    private static DropboxConnectionBinding DropboxAccount(string account, bool primary)
    {
        var connectionId = DropboxConnectionBinding.CreateConnectionId(account);
        return new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, account,
            "root-" + account, "home-" + account, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, ConnectionId: connectionId, DisplayName: account,
            Email: account + "@example.test",
            CredentialReference: primary ? WindowsDropboxRefreshTokenStore.RegisteredTarget
                : WindowsDropboxRefreshTokenStore.AccountTarget(connectionId)).Normalize();
    }

    private static GoogleDriveConnectionBinding GoogleAccount(string permissionId)
    {
        var accountKey = GoogleDriveConnectionBinding.CreateAccountKey(permissionId);
        return new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId, accountKey,
            permissionId, permissionId + "@example.test", permissionId,
            GoogleDriveProviderConfiguration.RequiredScopes,
            ConnectionId: GoogleDriveConnectionBinding.CreateConnectionId(accountKey)).Normalize();
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } instance
            ? instance.Invoke(target, arguments)
            : target.GetType().GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) is { } statics
                ? statics.Invoke(null, arguments)
                : throw new MissingMethodException(target.GetType().Name, name);

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Drain(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
    }
}

