using Massdex.SyncShark;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// P-PAUSE consumer checks on the real window: the account-scope refusal while a run is active, the
// split between the global pause and the run pause, the identity rules on the app path and the
// truthful reporting of a failed preference write. Uses isolated fixture state only.
internal static class PauseScopeChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var dropbox = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:pause-fixture", "pause-fixture-root", "pause-fixture-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        Pump(new DropboxConnectionStore(appLock.StateAuthority).SaveNewAsync(dropbox));
        const string permissionId = "pause-fixture-google";
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId,
            "pause@example.invalid", "Pause fixture", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Pump(new GoogleDriveConnectionStore(appLock.StateAuthority).SaveAsync(google));
        using var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1100, Height = 740 };
        try
        {
            window.Show(); Drain();
            CheckSettingsHeaderConsumer(window);
            Call(window, "NavigateTo", "Dashboard", true); Drain();
            var store = new UiPreferencesStore(appLock.StateAuthority);
            CheckOptionLabels();
            CheckAccountRefusalDuringRun(window, store, dropbox.ConnectionId);
            CheckRunPauseSplit(window, store, dropbox.ConnectionId);
            CheckIdentityRules(window, store, google.ConnectionId);
            CheckTrayScope(window, store, dropbox.ConnectionId, google.ConnectionId, root);
            CheckTrayIcons();
            using var stop = App.CreateNativePauseIcon("stop");
            Require(stop.GetPixel(8, 8).A == 255 && stop.GetPixel(0, 0).A == 0,
                "Native stop icon has visible square pixels on a transparent canvas");
            Console.WriteLine("PASS pause scope consumer checks");
            return 0;
        }
        finally
        {
            Set(window, "_uiRunActive", false);
            Set(window, "_workspacePaused", false);
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    private static void CheckSettingsHeaderConsumer(MainWindow window)
    {
        Call(window, "NavigateTo", "Settings", true); Drain();
        var tabs = Field<TabControl>(window, "_settingsTabs");
        Require(tabs.Items.Count == 6, "The actual Settings page constructs all six tabs with the compact header");
        var page = (Grid)Field<Border>(window, "SettingsPage").Child;
        Require(page.Children[0] is PageHeaderPanel,
            "Settings uses the shared header without relying on its former StackPanel type");
        var save = Field<Button>(window, "_settingsSave");
        var discard = Field<Button>(window, "_settingsDiscard");
        var header = (PageHeaderPanel)page.Children[0];
        Require(header.IsAncestorOf(save) && header.IsAncestorOf(discard),
            "Save and Discard are real header descendants rather than overlapping grid overlays");
        var expected = new[] { "General", "Sync & Performance", "Notifications", "Explorer", "About & Updates", "Diagnostics" };
        for (var i = 0; i < expected.Length; i++)
        {
            tabs.SelectedIndex = i; window.UpdateLayout(); Drain();
            Require(((TabItem)tabs.SelectedItem).Header.ToString() == expected[i],
                "Actual Settings tab opens: " + expected[i]);
        }
        tabs.SelectedIndex = 0; Drain();
    }

    private static void CheckOptionLabels()
    {
        var labels = PauseOption.All.Select(option => option.Label).ToArray();
        Require(labels.SequenceEqual(["5 Minutes", "15 Minutes", "30 Minutes", "1 Hour", "Until Tomorrow",
                "Indefinitely"]),
            "The shared option set renders exactly the six approved labels");
        Require(PauseOption.Indefinitely.StopSquare && PauseOption.All.Take(5).All(option => !option.StopSquare),
            "Only Indefinitely carries the stop square");
    }

    private static void CheckAccountRefusalDuringRun(MainWindow window, UiPreferencesStore store, string id)
    {
        var before = store.Load();
        Set(window, "_uiRunActive", true);
        Pump(window.SetPausedAsync(PauseOption.Minutes5, id));
        var phase = Field<TextBlock>(window, "PhaseText").Text;
        Require(phase.Contains("refused", StringComparison.OrdinalIgnoreCase),
            "An account pause during an active run is refused with an explanation");
        Call(window, "RefreshWorkspace");
        Require(Field<TextBlock>(window, "PhaseText").Text == phase,
            "A normal presentation refresh preserves the account-pause refusal long enough to read it");
        var after = store.Load();
        Require(after.AccountPauses?.Any(item => item.ConnectionId == id) != true,
            "The refused account pause stored nothing for that account");
        Require(after.PauseUntilResumed == before.PauseUntilResumed && after.PauseUntilUtc == before.PauseUntilUtc,
            "The refused account pause did not change the global pause");
        Set(window, "_uiRunActive", false);
    }

    private static void CheckRunPauseSplit(MainWindow window, UiPreferencesStore store, string id)
    {
        Pump(window.SetPausedAsync(PauseOption.Indefinitely, id));
        Require(store.Load().AccountPauses?.Any(item => item.ConnectionId == id) == true,
            "An exact account pause is stored on the app path");
        Require(!Field<UiPreferences>(window, "_workspacePreferences").PauseUntilResumed,
            "An account pause does not set the global pause");
        Set(window, "_uiRunActive", true);
        Set(window, "_workspacePaused", true);
        Pump(window.ResumePausedAsync(id));
        Require(store.Load().AccountPauses?.Any(item => item.ConnectionId == id) == true,
            "Account resume during an active run is refused without clearing the pause");
        Pump(window.ResumePausedAsync(null));
        Require(Field<bool>(window, "_workspacePaused"),
            "A global resume does not release the run while an account pause is still in place");
        Require(Field<TextBlock>(window, "PhaseText").Text.Contains("account pause", StringComparison.OrdinalIgnoreCase),
            "The global resume reports that the run stays paused for the account pause");
        Set(window, "_workspacePaused", false);
        Set(window, "_uiRunActive", false);
        Pump(window.ResumePausedAsync(id));
    }

    private static void CheckIdentityRules(MainWindow window, UiPreferencesStore store, string google)
    {
        Pump(window.SetPausedAsync(PauseOption.Minutes15, google));
        Require(store.Load().AccountPauses?.Any(item => item.ConnectionId == google) == true,
            "An approved Google Drive identity is accepted by the app path");
        Pump(window.SetPausedAsync(PauseOption.Minutes15, "gdrive-connection-NOPE"));
        Require(store.Load().AccountPauses?.Any(item => item.ConnectionId == "gdrive-connection-NOPE") != true,
            "A malformed identity is refused by the app path");
        Require(Field<TextBlock>(window, "PhaseText").Text.Contains("not an approved connection identifier",
                StringComparison.Ordinal),
            "The refused identity is explained rather than silently ignored");
        var unknown = "gdrive-connection-" + new string('e', 24);
        Pump(window.SetPausedAsync(PauseOption.Indefinitely, unknown));
        Require(store.Load().AccountPauses?.Any(item => item.ConnectionId == unknown) != true,
            "A well-formed but nonexistent account is refused");
        Require(Field<TextBlock>(window, "PhaseText").Text.Contains("no longer connected", StringComparison.Ordinal),
            "The nonexistent account is explained");
        Pump(window.ResumePausedAsync(google));
    }

    private static void CheckTrayScope(MainWindow window, UiPreferencesStore store, string dropbox, string google, string root)
    {
        var first = new SyncPair("tray-pair-a", "Same name", root, 0, 1, "/a", "id:a", ConnectionId: dropbox);
        var second = first with { Id = "tray-pair-b", ConnectionId = google, DropboxRoot = "/b", DropboxFolderId = "id:b" };
        window.LoadPairSetForTest(new SyncPairSet([first, second], first.Id));
        Set(window, "_workspaceReady", true);
        // Each synthetic event carries the exact account it was recorded under. The tray must filter on that
        // recorded identity rather than deriving historical ownership from today's mutable pair definitions.
        var a = new ActivityRow("2026-09-18T01:00:00Z", "a.txt", "Completed upload", "", PairId: first.Id,
            OperationId: "tray-a", ConnectionId: dropbox, ItemKind: "File", Entity: "File",
            Categories: new[] { SyncActivityCategories.Files });
        var b = a with { PairId = second.Id, Path = "b.txt", OperationId = "tray-b", ConnectionId = google };
        // One deliberately unscoped record: it carries no account identity at all, so an exact-account view
        // must never attribute it to the selected account. It deliberately names the SELECTED Google pair so
        // the case is adversarial: the pair matches, yet absent recorded account identity must still exclude it.
        var unscoped = new ActivityRow("2026-09-18T01:00:00Z", "c.txt", "Completed upload", "", PairId: second.Id,
            OperationId: "tray-unscoped", ItemKind: "File", Entity: "File",
            Categories: new[] { SyncActivityCategories.Files });
        Call(window, "TrackWorkspaceAction", a); Call(window, "TrackWorkspaceAction", b);
        Call(window, "TrackWorkspaceAction", unscoped);
        Require(a.IsFileActivity && b.IsFileActivity && unscoped.IsFileActivity,
            "All three synthetic tray events are File activity so the tray Files summary can present them");
        Require(b.MatchesAccount(google) && !unscoped.MatchesAccount(google),
            "b matches the selected Google account while the unscoped pair-matching row does not");
        var tray = new TrayFlyoutWindow(window);
        try
        {
            var before = store.Load();
            Pump(tray.SelectAccountAsync(google));
            Require(System.Text.Json.JsonSerializer.Serialize(before) == System.Text.Json.JsonSerializer.Serialize(store.Load()),
                "Changing tray account is read-only and does not alter sync preferences");
            var googleRows = Field<DataGrid>(tray, "_feed").Items.Cast<ActivityRow>().ToArray();
            Require(googleRows.Length == 1 && ReferenceEquals(googleRows[0], b),
                "The selected Google tray shows exactly its own recorded event and never the unscoped pair-matching row; actual count " +
                googleRows.Length + " ids " + string.Join(",", googleRows.Select(row => row.OperationId ?? "<none>")));
            Pump(window.SetPausedAsync(PauseOption.Minutes30, google));
            Call(tray, "Refresh");
            var pause = Field<Button>(tray, "_pause");
            Require(System.Windows.Automation.AutomationProperties.GetName(pause) == "Resume Sync" && pause.IsEnabled,
                "The actual tray control offers Resume with the selected account paused");
            Pump(window.HandleTrayPauseAsync(pause, google));
            Require(!window.TrayScope(google).Paused && !window.TrayScope(dropbox).Paused,
                "Tray resume clears only its own selected account pause");
            Pump(window.SetPausedAsync(PauseOption.Indefinitely, dropbox));
            Require(window.TrayScope(dropbox).OwnPause && !window.TrayScope(google).Paused,
                "The other account's deliberate pause does not masquerade as the selected account's pause");
            Pump(window.SetPausedAsync(PauseOption.Minutes30, null)); Call(tray, "Refresh");
            Require(window.TrayScope(google).Paused && !pause.IsEnabled &&
                    pause.ToolTip!.ToString()!.Contains("All Accounts"),
                "An inherited global pause is explained and cannot be resumed by a scoped account command");
            Pump(tray.SelectAccountAsync(null));
            var allRows = Field<DataGrid>(tray, "_feed").Items.Cast<ActivityRow>().ToArray();
            Require(allRows.Select(row => row.OperationId).OrderBy(id => id, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "tray-a", "tray-b", "tray-unscoped" }) &&
                    allRows.Any(row => ReferenceEquals(row, unscoped)),
                "All Accounts shows both account-bound events and the unscoped event; actual count " +
                allRows.Length + " ids " + string.Join(",", allRows.Select(row => row.OperationId ?? "<none>")));
            Pump(window.HandleTrayPauseAsync(pause, null));
            Require(window.TrayScope(dropbox).OwnPause && !window.TrayScope(google).Paused,
                "Resuming All Accounts leaves the separate account pause intact");
            Pump(window.ResumePausedAsync(dropbox));
            Pump(tray.SelectAccountAsync("removed-account"));
            Require(Field<string?>(tray, "_accountFilter") is null,
                "A removed account falls back to All Accounts without changing settings");
            Require(Field<DataGrid>(tray, "_feed").RowHeight >= 84,
                "Tray activity rows retain the roomier approved spacing");
        }
        finally { tray.Close(); }
    }

    private static void CheckTrayIcons()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/SyncShark;component/Assets/SyncShark.ico"))!.Stream;
        using var original = new System.Drawing.Icon(stream);
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var baseline = TrayStatusIcons.Render(original, "idle", size);
            foreach (var state in new[] { "starting", "paused", "attention", "working", "working-light" })
            {
                using var colored = TrayStatusIcons.Render(original, state, size);
                var visible = 0;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    if (baseline.GetPixel(x, y).A != colored.GetPixel(x, y).A)
                        throw new InvalidOperationException("A tray tint changed the fin silhouette or added a background/overlay.");
                    if (colored.GetPixel(x, y).A > 128) visible++;
                }
                Require(visible > size, $"Tray {state} retains the original transparent fin at {size}px");
            }
        }
    }

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        while (!task.IsCompleted) { Drain(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(target)!;

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
