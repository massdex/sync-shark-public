using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.V2;
using Microsoft.Win32;

namespace SyncShark.UiFixture;

// Real running-app settings adapter and controls over isolated stores. Only the Windows
// startup side effect is replaced; no installed state, provider or registry is modified.
internal static class CliSettingsChecks
{
    private static async Task<bool> CheckHostStartupAsync()
    {
        using var product = SyncSharkRuntimePaths.OverrideProductRootForTest(
            Path.Combine(Path.GetTempPath(), "startup-host-" + Guid.NewGuid().ToString("N")));
        await using var host = AutoSyncBackgroundHost.Start(
            AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile),
            () => new MemoryStream(), () => new StartupEngine());
        // Same-process peers are deliberately untrusted. Even this rejected start must not
        // change logon registration before peer validation; do not bypass the production gate.
        try { await BackgroundHostEntry.RunAsync(); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed class StartupEngine : IAutoSyncHostEngine
    {
        public Task<object> ExecuteAsync(AutoSyncHostRequest request, CancellationToken token) => Task.FromResult<object>(new { running = true });
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static int Run(string root)
    {
        // The same existing selector also admits its own bounded request file as the external
        // observer role. It never opens App, a state authority or the production CLI fallback.
        if (File.Exists(root)) return RunningAppBridge.RunBoundClient(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(instance.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = FocusedUiAcceptanceContract.CreateIsolatedWindow(instance.StateAuthority, theme);
        var startup = new List<bool>();
        window.StartupRegistrationForTest = value => startup.Add(value);
        var runtime = new CliAppRuntime(window.AuthorityHost, instance.StateAuthority, () => false,
            () => false, () => { }, () => Task.CompletedTask,
            beforeSettingsSave: window.BeforeCliSettingsSaveAsync,
            applySettings: window.ApplyCliSavedSettingsAsync,
            completeSettingsSave: window.CompleteCliSettingsSaveAsync,
            appAction: window.ExecuteCliAppActionAsync);
        var count = 0;
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("CLI settings: " + label);
            count++;
            Console.WriteLine("PASS " + label);
        }
        try
        {
            Call(window, "NavigateTo", "Settings", true);
            var initial = Pump(runtime.LoadSettingsAsync(default));
            var registeredExecutable = Path.Combine(root, "installed", "SyncShark.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(registeredExecutable)!);
            File.WriteAllText(registeredExecutable, "isolated installed fixture");
            AutoSyncProcess.RegisterStartup(registeredExecutable, true);
            using (var startupKey = Registry.CurrentUser.OpenSubKey(AutoSyncProcess.StartupKeyPathForTest!, writable: true)!)
            {
                Call(window, "LoadAutomation");
                Require((string?)startupKey.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(registeredExecutable),
                    "GUI launch from another executable preserves the working startup registration");
                Require(Pump(CheckHostStartupAsync()), "host entry preserves the production same-process peer refusal");
                Require((string?)startupKey.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(registeredExecutable),
                    "a rejected autosync start from another executable preserves the working registration");
                AutoSyncProcess.RegisterStartup(registeredExecutable, false);
                var legacy = $"\"{registeredExecutable}\" {AutoSyncProcess.LegacyArguments}";
                startupKey.SetValue(AutoSyncProcess.LegacyValueName, legacy);
                AutoSyncProcess.BeforeStartupWriteForTest = () => throw new IOException("Injected migration write failure");
                try
                {
                    Call(window, "LoadAutomation");
                    Require((string?)startupKey.GetValue(AutoSyncProcess.LegacyValueName) == legacy &&
                        startupKey.GetValue(AutoSyncProcess.ValueName) is null,
                        "GUI legacy migration preserves the original launcher when the supervisor write fails");
                }
                finally { AutoSyncProcess.BeforeStartupWriteForTest = null; }
                Call(window, "LoadAutomation");
                // RepairStartup selects the running build for an owned legacy launcher. This fixture host
                // is not a product executable (SyncShark.exe), so the shared owner refuses to register
                // it and leaves the owned launcher untouched; the successful repoint is proved by
                // AutoSyncStartupChecks.CheckOldGuiMigration.
                Require(startupKey.GetValue(AutoSyncProcess.ValueName) is null &&
                    (string?)startupKey.GetValue(AutoSyncProcess.LegacyValueName) == legacy &&
                    Field<TextBox>(window, "ConsoleText").Text.Contains(
                        "Engine logon startup could not be updated", StringComparison.Ordinal),
                    "GUI launch from a non-product test build reports the refusal and leaves the owned legacy launcher unchanged");
                AutoSyncProcess.RegisterStartup(registeredExecutable, false);
                startupKey.SetValue(AutoSyncProcess.ValueName, "foreign.exe");
                Call(window, "LoadAutomation");
                window.StartupRegistrationForTest = _ => throw new InvalidOperationException("Interval-only save touched startup");
                Pump(runtime.SaveSettingsAsync("{\"automation\":{\"intervalMinutes\":7}}", default));
                Require((string?)startupKey.GetValue(AutoSyncProcess.ValueName) == "foreign.exe",
                    "interval-only CLI save never touches a foreign startup value");
                window.StartupRegistrationForTest = null;
                Call(window, "SetStartupRegistration", false);
                Require((string?)startupKey.GetValue(AutoSyncProcess.ValueName) == "foreign.exe",
                    "GUI uninstall/rollback disable leaves foreign startup alone without throwing");
                startupKey.DeleteValue(AutoSyncProcess.ValueName);
            }
            // Substitute only the executable identity; the shared owner writes an isolated registry key.
            window.StartupRegistrationForTest = value => Massdex.SyncShark.V2.AutoSyncProcess.RegisterStartup(
                @"C:\Builds\Current\SyncShark.exe", value);
            var enabledStartup = Pump(runtime.SaveSettingsAsync("{\"automation\":{\"startWithWindows\":true}}", default));
            Require(enabledStartup.Automation.StartWithWindows && Field<CheckBox>(window, "StartWithWindowsBox").IsChecked == true,
                "CLI enable applies supervisor registration and refreshes the GUI from registry fact");
            Massdex.SyncShark.V2.AutoSyncProcess.RegisterStartup(@"C:\Builds\Current\SyncShark.exe", false);
            Require(!Pump(runtime.LoadSettingsAsync(default)).Automation.StartWithWindows,
                "external removal reports disabled without a persisted startup flag");
            Pump(runtime.SaveSettingsAsync("{\"automation\":{\"startWithWindows\":true}}", default));
            Require(Massdex.SyncShark.V2.AutoSyncProcess.ReadStartup(), "enable registers startup after external removal");
            Pump(runtime.SaveSettingsAsync("{\"automation\":{\"startWithWindows\":false}}", default));
            Require(!Massdex.SyncShark.V2.AutoSyncProcess.ReadStartup(), "CLI disable removes the same supervisor registration");
            window.StartupRegistrationForTest = value => startup.Add(value);
            var saved = Pump(runtime.SaveSettingsAsync("{\"automation\":{\"enabled\":false},\"interface\":{\"theme\":\"Light\",\"compactRows\":true,\"notificationSound\":true}}", default));
            Require(saved.EffectsApplied && preferences.Load().Theme == "Light" && theme.Preference == "Light",
                "saved theme applies immediately to the running application");
            Require(Field<ComboBox>(window, "_settingsAppearance").SelectedItem?.ToString() == "Light" &&
                Field<ComboBox>(window, "_settingsDensity").SelectedIndex == 1 &&
                Field<DataGrid>(window, "ActivityGrid").RowHeight == 60,
                "settings controls and activity density match retained settings");
            Require(Field<Dictionary<string, CheckBox>>(window, "_settingsToggles")["sound"].IsChecked == true &&
                !Enumerable.Range(0, 3).Any(index => (bool)Call(window, "SettingsCategoryDirty", index)!),
                "notification sound and all saved draft baselines refresh together");
            Require(!Field<bool>(window, "_cliSettingsGateHeld"), "successful transaction releases preference gate");
            foreach (var style in new[] { "Colour", "Symbol", "Combined" })
            {
                var changed = Pump(runtime.SaveSettingsAsync("{\"interface\":{\"statusIconStyle\":\"" + style + "\"}}", default));
                Require(changed.EffectsApplied && changed.Interface.StatusIconStyle == style &&
                    preferences.Load().StatusIconStyle == style &&
                    Field<ComboBox>(window, "_settingsIconStyle").SelectedItem?.ToString() == style &&
                    Equals(app.Resources[BrandedItemImages.StyleResource], style), "icon style persists and applies to the running UI: " + style);
            }
            var iconChoice = Field<ComboBox>(window, "_settingsIconStyle");
            iconChoice.SelectedItem = "Colour";
            Require(Equals(app.Resources[BrandedItemImages.StyleResource], "Colour") && preferences.Load().StatusIconStyle == "Combined",
                "icon draft previews without silently saving");
            Call(window, "DiscardSettingsCategory", 0);
            Require(Equals(app.Resources[BrandedItemImages.StyleResource], "Combined") && iconChoice.SelectedItem?.ToString() == "Combined",
                "discard restores the saved icon style");
            var invalidIcons = false;
            try { Pump(runtime.SaveSettingsAsync("{\"interface\":{\"theme\":\"AMOLED\",\"statusIconStyle\":\"unknown\"}}", default)); }
            catch (SyncCliUsageException) { invalidIcons = true; }
            Require(invalidIcons && preferences.Load().Theme == "Light" && preferences.Load().StatusIconStyle == "Combined",
                "invalid icon styles reject the complete settings update without side effects");

            var appearance = Field<ComboBox>(window, "_settingsAppearance");
            appearance.SelectedItem = "Dark";
            var draftRefused = false;
            try { Pump(runtime.SaveSettingsAsync("{\"interface\":{\"theme\":\"AMOLED\"}}", default)); }
            catch (InvalidOperationException) { draftRefused = true; }
            Require(draftRefused && preferences.Load().Theme == "Light" && appearance.SelectedItem?.ToString() == "Dark",
                "an unsaved UI draft is preserved and CLI write refused");
            Call(window, "DiscardSettingsCategory", 0);

            var beforeInvalid = Pump(runtime.LoadSettingsAsync(default));
            var invalidRefused = false;
            try { Pump(runtime.SaveSettingsAsync("{\"automation\":{\"intervalMinutes\":11},\"interface\":{\"theme\":\"unknown\"}}", default)); }
            catch (SyncCliUsageException) { invalidRefused = true; }
            Require(invalidRefused && Pump(runtime.LoadSettingsAsync(default)) == beforeInvalid &&
                !Field<bool>(window, "_cliSettingsGateHeld"), "invalid later section changes no earlier stores and releases the gate");

            window.StartupRegistrationForTest = value => {
                startup.Add(value);
                if (value != initial.Automation.StartWithWindows) throw new IOException("Fixture startup refusal");
            };
            var requested = !initial.Automation.StartWithWindows;
            var failed = false;
            try { Pump(runtime.SaveSettingsAsync("{\"automation\":{\"startWithWindows\":" + requested.ToString().ToLowerInvariant() +
                "},\"interface\":{\"theme\":\"AMOLED\"}}", default)); }
            catch (SyncCliOperationException) { failed = true; }
            Require(failed && Pump(runtime.LoadSettingsAsync(default)) == beforeInvalid && theme.Preference == "Light",
                "startup effect failure rolls back all stores and the running UI");
            Require(startup.Contains(requested) && startup.Last() == initial.Automation.StartWithWindows &&
                !Field<bool>(window, "_cliSettingsGateHeld"), "startup rollback is attempted and gate is released after failure");
            window.StartupRegistrationForTest = value => startup.Add(value);
            var recovered = Pump(runtime.SaveSettingsAsync("{\"interface\":{\"theme\":\"Dark\",\"compactRows\":false}}", default));
            Require(recovered.EffectsApplied && theme.Preference == "Dark" && Field<DataGrid>(window, "ActivityGrid").RowHeight == 76,
                "the next settings command succeeds after failure without restarting the app");
            var local = Path.Combine(root, "local-location"); Directory.CreateDirectory(local);
            Pump(window.AuthorityHost.LoadPairsAsync());
            using (var lease = PilotDirectoryLease.Open(local))
            {
                var pair = new SyncPair("pair-cli-app", "CLI App", local, lease.Identity.VolumeSerialNumber,
                    lease.Identity.FileId, "/Fixture", "id:fixture", ConnectionId: "fixture-account");
                Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
                Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, true));
            }
            var launches = new List<System.Diagnostics.ProcessStartInfo>();
            Require(window.PairRows.All(pair => pair.Id != "pair-cli-app"),
                "new authority pair is not yet in the GUI's cached rows");
            Pump(window.RefreshAfterCliCommandAsync());
            Require(window.PairRows.Any(pair => pair.Id == "pair-cli-app"),
                "post-command readback displays the new pair without restart");
            AssertRunningBridgeSequence(app, window, instance, theme, preferences, startup, Require);
            Pump(runtime.SetPairEnabledAsync("pair-cli-app", false, default));
            Pump(window.RefreshAfterCliCommandAsync());
            Require(!window.PairRows.Single(pair => pair.Id == "pair-cli-app").Enabled,
                "real CLI pair pause is reflected in the running GUI");
            Pump(runtime.SetPairEnabledAsync("pair-cli-app", true, default));
            Pump(window.RefreshAfterCliCommandAsync());
            Require(window.PairRows.Single(pair => pair.Id == "pair-cli-app").Enabled,
                "real CLI pair resume updates the same GUI row");
            window.CliLocationLauncherForTest = launches.Add;
            object Dispatch(params string[] arguments) => Pump(SyncCliRegistry.DispatchAsync(runtime,
                SyncCliRegistry.Parse(arguments), TextWriter.Null, default));
            Dispatch("--cli", "locations", "open", "--pair", "pair-cli-app", "--path", ".", "--choice", "local", "--yes");
            Require(launches.Count == 1 && launches[0].FileName == "explorer.exe" && launches[0].Arguments.Contains(local, StringComparison.Ordinal),
                "actual registry and app adapter request the exact local folder in Explorer");
            var escaped = false;
            try { Dispatch("--cli", "locations", "open", "--pair", "pair-cli-app", "--path", "../outside", "--choice", "local", "--yes"); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { escaped = true; }
            Require(escaped && launches.Count == 1, "relative path escape never reaches the OS launcher");
            var wrongAccount = false;
            try { Dispatch("--cli", "locations", "open", "--pair", "pair-cli-app", "--account", "wrong", "--path", ".", "--choice", "local", "--yes"); }
            catch (SyncCliTargetException) { wrongAccount = true; }
            Require(wrongAccount && launches.Count == 1, "another account cannot open this pair's target");
            var unsignedRefused = false;
            try { Dispatch("--cli", "explorer", "inspect", "--pair", "pair-cli-app", "--key", "fixture", "--path", local, "--yes"); }
            catch (SyncCliUnsupportedException) { unsignedRefused = true; }
            Require(unsignedRefused && launches.Count == 1, "development app refuses ownership inspection before any native registration effect");
            var pairForStorage = Pump(window.AuthorityHost.LoadPairsAsync()).Pairs.Single(item => item.Id == "pair-cli-app");
            var generation = pairForStorage.PolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var noStorageChange = Dispatch("--cli", "storage", "disable", "--pair", pairForStorage.Id, "--account", "fixture-account", "--generation", generation, "--yes");
            Require(System.Text.Json.JsonSerializer.Serialize(noStorageChange).Contains("\"changed\":false", StringComparison.Ordinal),
                "storage disable is an honest no-op for an already ordinary pair");
            var staleStorage = false;
            try { Dispatch("--cli", "storage", "disable", "--pair", pairForStorage.Id, "--account", "fixture-account", "--generation", "99999", "--yes"); }
            catch (SyncCliTargetException) { staleStorage = true; }
            Require(staleStorage, "storage disable rejects a stale pair policy before native work");
            var missingStorageAccount = false;
            try { Dispatch("--cli", "storage", "disable", "--pair", pairForStorage.Id, "--generation", generation, "--yes"); }
            catch (SyncCliUsageException) { missingStorageAccount = true; }
            Require(missingStorageAccount, "storage disable requires the exact account even for an ordinary pair");
            var hostStorageGuard = false;
            try { Pump(window.AuthorityHost.DisableFilesOnDemandAsync(pairForStorage.Id, expectedConnectionId: "wrong-account", expectedPolicyRevision: pairForStorage.PolicyRevision)); }
            catch (InvalidOperationException) { hostStorageGuard = true; }
            Require(hostStorageGuard, "shared storage authority rechecks the expected account before its no-op or provider effect");
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pairForStorage with { FilesOnDemandEnabled = true }, true));
            var managedStorageRefused = false;
            try { Dispatch("--cli", "storage", "disable", "--pair", pairForStorage.Id, "--account", "fixture-account", "--generation", generation, "--yes"); }
            catch (SyncCliUnsupportedException) { managedStorageRefused = true; }
            Require(managedStorageRefused && Pump(window.AuthorityHost.LoadPairsAsync()).Pairs.Single().FilesOnDemandEnabled,
                "development app cannot disable a managed root or claim its files became local");
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pairForStorage, true));
            foreach (var action in new[] { new[] { "explorer", "restore" }, new[] { "app", "uninstall" } })
            {
                var refused = false;
                try { Dispatch("--cli", action[0], action[1], "--yes"); }
                catch (SyncCliUnsupportedException) { refused = true; }
                Require(refused && !window.CliUpdateExitPrepared,
                    string.Join(" ", action) + " refuses unsigned development identity without arming exit or native work");
            }
            foreach (var action in new[] { "verify", "apply" })
            {
                var refused = false;
                var arguments = new List<string> { "--cli", "updates", action, "--path", Path.Combine(root, "Install-SyncShark.ps1") };
                if (action == "apply") arguments.AddRange(["--update", "0.3.35", "--yes"]);
                try { Dispatch(arguments.ToArray()); }
                catch (SyncCliUnsupportedException) { refused = true; }
                Require(refused && !window.CliUpdateExitPrepared,
                    "local update " + action + " refuses unsigned identity before reading or executing the supplied package");
            }
            CheckStartupRegistrationDestination(Require);
            CheckLegacyFieldLocalSave(runtime, window, preferences, theme, Require);
            CheckLegacyRollbackPreservesConcurrentUpdate(runtime, window, preferences, Require);
            CheckEnginePreferenceRoute(runtime, preferences, Require);
            Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + count + ",\"boundary\":\"real app adapters; isolated stores; startup and OS launch effects substituted\"}");
            return 0;
        }
        finally
        {
            Pump(window.PrepareProductExitAsync());
            window.Close();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    private static void AssertRunningBridgeSequence(App app, MainWindow window, PilotInstanceLock instance,
        ThemeManager theme, UiPreferencesStore preferences, List<bool> startup, Action<bool, string> require)
    {
        using var bridge = new RunningAppBridge(app, window, instance, theme);
        bridge.Invoke("settings", "set", "--settings",
            "{\"interface\":{\"theme\":\"Light\",\"compactRows\":true}}", "--yes");
        require(preferences.Load().Theme == "Light" && theme.Preference == "Light" &&
            !Field<bool>(window, "_cliSettingsGateHeld"),
            "M22 first external bridge save commits, updates the UI and releases its gate");
        bridge.Invoke("pause", "--yes");
        require(preferences.Load().PauseUntilResumed && !Field<bool>(window, "_cliPauseGateHeld") &&
            !Field<bool>(window, "_cliSettingsGateHeld"),
            "M22 external bridge pause releases the same preference gate");
        bridge.Invoke("resume", "--yes");
        bridge.Invoke("settings", "set", "--settings",
            "{\"interface\":{\"theme\":\"Dark\",\"compactRows\":false}}", "--yes");
        require(preferences.Load().Theme == "Dark" && theme.Preference == "Dark" &&
            Field<DataGrid>(window, "ActivityGrid").RowHeight == 76 &&
            !preferences.Load().PauseUntilResumed &&
            !Field<bool>(window, "_cliPauseGateHeld") && !Field<bool>(window, "_cliSettingsGateHeld"),
            "M22 second external bridge save succeeds after pause/resume without restarting");

        var before = window.AuthorityHost.LoadAutomation();
        var beforePreferences = preferences.Load();
        var startupBefore = startup.Count;
        var requested = !before.StartWithWindows;
        window.StartupRegistrationForTest = value =>
        {
            startup.Add(value);
            if (value == requested) throw new IOException("Fixture bridge startup refusal");
        };
        try
        {
            var refused = bridge.Invoke(false, "settings", "set", "--settings",
                "{\"automation\":{\"startWithWindows\":" + requested.ToString().ToLowerInvariant() +
                "},\"interface\":{\"theme\":\"AMOLED\"}}", "--yes");
            var error = refused.GetProperty("error");
            var afterAutomation = window.AuthorityHost.LoadAutomation();
            var afterPreferences = preferences.Load();
            var codeSame = error.GetProperty("code").GetString() == "operation_failed";
            var automationSame = afterAutomation == before;
            var preferencesRecordSame = afterPreferences == beforePreferences;
            var preferencesValuesSame = PreferencesValuesEqual(beforePreferences, afterPreferences);
            var themeSame = theme.Preference == "Dark";
            var startupSegment = startup.Skip(startupBefore).ToArray();
            var startupRestored = startupSegment.Length > 0 && startupSegment[^1] == before.StartWithWindows;
            var gateReleased = !Field<bool>(window, "_cliSettingsGateHeld");
            Console.WriteLine("M22 rollback diagnostic " + JsonSerializer.Serialize(new
            {
                codeSame, automationSame, preferencesRecordSame, preferencesValuesSame, themeSame,
                startupRestored, gateReleased, requested, startupSegment,
                errorCode = error.GetProperty("code").GetString(),
                errorMessage = error.TryGetProperty("message", out var message) ? message.GetString() : null,
                beforeAutomation = before, afterAutomation, beforePreferences, afterPreferences,
                samePauseCollection = ReferenceEquals(beforePreferences.AccountPauses, afterPreferences.AccountPauses),
                sameViewCollection = ReferenceEquals(beforePreferences.PageViewModes, afterPreferences.PageViewModes)
            }));
            require(codeSame && automationSame && preferencesRecordSame && themeSame &&
                startupRestored && startupSegment.Contains(requested) && gateReleased,
                "M22 actual bridge startup failure rolls back stores/UI and releases its gate");
        }
        finally { window.StartupRegistrationForTest = value => startup.Add(value); }
        bridge.Invoke("settings", "set", "--settings", "{\"interface\":{\"theme\":\"Light\"}}", "--yes");
        require(preferences.Load().Theme == "Light" && theme.Preference == "Light" &&
            !Field<bool>(window, "_cliSettingsGateHeld"),
            "M22 the next actual bridge save succeeds after the failed transaction");
        // Preserve the later existing assertions' running-UI state.
        bridge.Invoke("settings", "set", "--settings", "{\"interface\":{\"theme\":\"Dark\"}}", "--yes");
    }

    private static bool PreferencesValuesEqual(UiPreferences before, UiPreferences after)
    {
        // Compare every scalar via record equality, then the complete retained collections by value.
        // Null is distinct from an empty collection; no preference member is omitted from the proof.
        if ((before with { AccountPauses = null, PageViewModes = null }) !=
            (after with { AccountPauses = null, PageViewModes = null })) return false;
        if ((before.AccountPauses is null) != (after.AccountPauses is null) ||
            (before.PageViewModes is null) != (after.PageViewModes is null)) return false;
        if (before.AccountPauses is not null && !before.AccountPauses.SequenceEqual(after.AccountPauses!))
            return false;
        return before.PageViewModes is null || before.PageViewModes.Count == after.PageViewModes!.Count &&
            before.PageViewModes.All(item => after.PageViewModes.TryGetValue(item.Key, out var value) &&
                string.Equals(item.Value, value, StringComparison.Ordinal));
    }

    // Starts App's actual bridge and callbacks, not a parallel test dispatcher. Each command is
    // issued by the same fixture selector's bound observer role, in the exact fixture executable.
    internal sealed class RunningAppBridge : IDisposable
    {
        private readonly App _app;
        private readonly MainWindow _window;
        private readonly Dictionary<string, object?> _previous = [];
        private readonly SyncCliBridgeServer _server;
        private readonly string _requestRoot;

        internal RunningAppBridge(App app, MainWindow window, PilotInstanceLock instance, ThemeManager theme)
        {
            _app = app;
            _window = window;
            _requestRoot = Path.Combine(instance.StateAuthority.StateRoot, "bridge-requests");
            RequireNoOtherFixtureOwner();
            foreach (var name in new[] { "_singleInstance", "_themeManager", "_mainWindow", "_cliBridge" })
                _previous[name] = AppField(name).GetValue(app);
            if (_previous["_cliBridge"] is not null)
                throw new InvalidOperationException("The isolated app already owns a bridge.");
            try
            {
                AppField("_singleInstance").SetValue(app, instance);
                AppField("_themeManager").SetValue(app, theme);
                AppField("_mainWindow").SetValue(app, window);
                typeof(App).GetMethod("StartCliBridge", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(app, null);
                _server = (SyncCliBridgeServer?)AppField("_cliBridge").GetValue(app) ??
                    throw new InvalidOperationException("The real app bridge did not start.");
                var target = Field<IntPtr>(_server, "_window");
                SyncCliBridgeTransport.GetWindowThreadProcessId(target, out var owner);
                if (target == IntPtr.Zero || owner != (uint)Environment.ProcessId)
                    throw new InvalidOperationException("The bridge is not owned by this exact fixture process.");
            }
            catch
            {
                (AppField("_cliBridge").GetValue(app) as SyncCliBridgeServer)?.Dispose();
                RestoreAppFields();
                throw;
            }
        }

        internal JsonElement Invoke(params string[] arguments) => Invoke(true, arguments);

        internal JsonElement Invoke(bool expectedSuccess, params string[] arguments)
        {
            RequireNoOtherFixtureOwner();
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SyncShark.UiFixture.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            Directory.CreateDirectory(_requestRoot);
            var requestPath = Path.Combine(_requestRoot, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(new BoundClientRequest(Environment.ProcessId,
                Field<IntPtr>(_server, "_window").ToInt64(), ["--cli", .. arguments, "--json"])));
            start.ArgumentList.Add("--cli-settings-checks");
            start.ArgumentList.Add(requestPath);
            using var client = Process.Start(start) ?? throw new InvalidOperationException("CLI client did not start.");
            var output = client.StandardOutput.ReadToEndAsync();
            var diagnostic = client.StandardError.ReadToEndAsync();
            try { Pump(client.WaitForExitAsync()); }
            catch
            {
                // This exact newly-created process only waits for a reply. Stop that observer, never
                // the app or accepted job; fail without retrying an uncertain mutation.
                if (!client.HasExited) { client.Kill(entireProcessTree: false); client.WaitForExit(5000); }
                throw;
            }
            var text = Pump(output);
            var diagnosticText = Pump(diagnostic);
            File.Delete(requestPath);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("External app-bridge client returned no command envelope; exit=" +
                    client.ExitCode + "; diagnostic=" + diagnosticText.Trim());
            using var document = JsonDocument.Parse(text);
            var envelope = document.RootElement.Clone();
            if (envelope.GetProperty("ok").GetBoolean() != expectedSuccess ||
                (client.ExitCode == 0) != expectedSuccess)
                throw new InvalidDataException("External app-bridge command returned the wrong outcome: " +
                    string.Join(' ', arguments.Take(2)) + "; exit=" + client.ExitCode);
            return envelope;
        }

        private sealed record BoundClientRequest(int OwnerProcessId, long ReceiverWindow, string[] Arguments);

        internal static int RunBoundClient(string requestPath)
        {
            try
            {
                var file = new FileInfo(requestPath);
                if (file.Length is <= 0 or > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The bounded client request is unavailable or unsafe.");
                var request = JsonSerializer.Deserialize<BoundClientRequest>(File.ReadAllText(requestPath)) ??
                    throw new InvalidDataException("The bounded client request is missing.");
                var target = new IntPtr(request.ReceiverWindow);
                SyncCliBridgeTransport.GetWindowThreadProcessId(target, out var owner);
                if (target == IntPtr.Zero || owner != (uint)request.OwnerProcessId ||
                    !SyncCliBridgeTransport.IsTrustedPeer(owner, out _) ||
                    SyncCliBridgeTransport.FindReceiver(includeCurrentProcess: false, out _) != target)
                    throw new InvalidOperationException("The exact isolated owner is not the eligible bridge; no request sent.");
                using var client = new SyncCliBridgeClient();
                if (!client.TryInvoke(request.Arguments, TextWriter.Null, TimeSpan.FromSeconds(25),
                        out var exit, out var text, out var error))
                    throw new InvalidOperationException("The isolated bridge disappeared; production fallback is forbidden.");
                if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(error))
                    Console.Error.WriteLine(error);
                Console.Write(text);
                return exit;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Bound fixture client refused: " + error.GetType().Name +
                    (error is SyncCliUsageException ? ": " + error.Message : string.Empty));
                return (int)SyncCliExitCode.Failed;
            }
        }

        private static void RequireNoOtherFixtureOwner()
        {
            if (SyncCliBridgeTransport.FindReceiver(includeCurrentProcess: false, out _) != IntPtr.Zero)
                throw new InvalidOperationException("Another eligible fixture bridge is active; no client was launched.");
        }

        private static FieldInfo AppField(string name) => typeof(App).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(nameof(App), name);

        private void RestoreAppFields()
        {
            foreach (var saved in _previous) AppField(saved.Key).SetValue(_app, saved.Value);
        }

        public void Dispose()
        {
            // Presentation is asynchronous after the result. Drain its actual readback before teardown.
            try { Pump(_window.RefreshAfterCliCommandAsync()); }
            finally
            {
                _server.Dispose();
                RestoreAppFields();
            }
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            // Inline Invoke on this STA does not dispatch incoming WM_COPYDATA or queued async
            // continuations. Run the real dispatcher while the bound external client waits.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            timer.Tick += (_, _) =>
            {
                if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        if (!task.IsCompleted) throw new TimeoutException("App settings fixture did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }

    // The canonical CLI startup route starts the supervisor and waits for readiness.
    private static void CheckStartupRegistrationDestination(Action<bool, string> require)
    {
        var executable = Path.Combine(Path.GetTempPath(), "sync-shark-startup-fixture", "SyncShark.exe");
        var canonical = AutoSyncProcess.StartupCommand(executable);
        require(canonical.EndsWith(" --cli engine host start --yes", StringComparison.Ordinal) &&
                AutoSyncProcess.IsProductStartupCommand(canonical) &&
                !canonical.Contains(AutoSyncProcess.LegacyArguments, StringComparison.Ordinal),
            "Start With Windows registers the readiness-gated Engine start command, not the legacy host");
    }

    // RC05-V5-S/RC05-T4: a one-field legacy CLI save must merge only that field, persist, refresh the
    // running controls and leave every unrelated saved preference and control untouched.
    private static void CheckLegacyFieldLocalSave(CliAppRuntime runtime, MainWindow window,
        UiPreferencesStore preferences, ThemeManager theme, Action<bool, string> require)
    {
        var before = preferences.Load();
        var target = before.Theme == UiPreferencesStore.DarkTheme
            ? UiPreferencesStore.LightTheme : UiPreferencesStore.DarkTheme;
        var iconBefore = Field<ComboBox>(window, "_settingsIconStyle").SelectedItem?.ToString();
        var densityBefore = Field<ComboBox>(window, "_settingsDensity").SelectedIndex;
        var soundBefore = Field<Dictionary<string, CheckBox>>(window, "_settingsToggles")["sound"].IsChecked;
        var saved = Pump(runtime.SaveSettingsAsync("{\"interface\":{\"theme\":\"" + target + "\"}}", default));
        var after = preferences.Load();
        require(saved.Interface.Theme == target && after.Theme == target && theme.Preference == target,
            "a one-field legacy CLI save persists and applies only the supplied theme");
        require(SamePreferencesExceptTheme(before, after),
            "the one-field legacy save preserves every unrelated preference member");
        require(Field<ComboBox>(window, "_settingsAppearance").SelectedItem?.ToString() == target &&
                Field<ComboBox>(window, "_settingsIconStyle").SelectedItem?.ToString() == iconBefore &&
                Field<ComboBox>(window, "_settingsDensity").SelectedIndex == densityBefore &&
                Field<Dictionary<string, CheckBox>>(window, "_settingsToggles")["sound"].IsChecked == soundBefore,
            "the running application shows the saved theme without changing unrelated controls");
        require(!Field<bool>(window, "_cliSettingsGateHeld"),
            "the one-field legacy save releases the shared preference gate");
    }

    // RC05-T4: a failed legacy transaction restores only the fields that call wrote. A concurrent disjoint
    // write survives; a newer same-field write is retained and the incomplete restoration is reported.
    private static void CheckLegacyRollbackPreservesConcurrentUpdate(CliAppRuntime runtime, MainWindow window,
        UiPreferencesStore preferences, Action<bool, string> require)
    {
        var baseline = preferences.Load();
        var requested = !window.AuthorityHost.LoadAutomation().StartWithWindows;
        var updates = new List<bool>();
        var inject = true;
        window.StartupRegistrationForTest = value =>
        {
            updates.Add(value);
            if (!inject) return;
            inject = false;
            Pump(preferences.UpdateAsync(new UiPreferencesUpdate
                { AutomaticUpdateChecks = !baseline.AutomaticUpdateChecks }));
            throw new IOException("Fixture startup refusal");
        };
        try
        {
            var patchTheme = baseline.Theme == UiPreferencesStore.AmoledTheme
                ? UiPreferencesStore.DarkTheme : UiPreferencesStore.AmoledTheme;
            var rolledBack = false;
            try
            {
                Pump(runtime.SaveSettingsAsync("{\"automation\":{\"startWithWindows\":" +
                    requested.ToString().ToLowerInvariant() + "},\"interface\":{\"theme\":\"" + patchTheme + "\"}}",
                    default));
            }
            catch (SyncCliOperationException error)
            {
                rolledBack = error.Message.Contains("Previous retained settings were restored", StringComparison.Ordinal);
            }
            var restored = preferences.Load();
            require(rolledBack && restored.Theme == baseline.Theme,
                "a failed legacy save restores only its own theme field");
            require(restored.AutomaticUpdateChecks == !baseline.AutomaticUpdateChecks &&
                    restored.StatusIconStyle == baseline.StatusIconStyle &&
                    restored.CompactRows == baseline.CompactRows,
                "a concurrent disjoint preference update survives the failed save's rollback");
            require(!Field<bool>(window, "_cliSettingsGateHeld"),
                "the failed legacy save releases the shared preference gate");
        }
        finally
        {
            window.StartupRegistrationForTest = value => updates.Add(value);
        }

        inject = true;
        var newerTheme = new[] { UiPreferencesStore.DarkTheme, UiPreferencesStore.AmoledTheme,
            UiPreferencesStore.LightTheme }.First(value => value != baseline.Theme);
        window.StartupRegistrationForTest = value =>
        {
            updates.Add(value);
            if (!inject) return;
            inject = false;
            Pump(preferences.UpdateAsync(new UiPreferencesUpdate { Theme = newerTheme }));
            throw new IOException("Fixture startup refusal");
        };
        try
        {
            var patchTheme = newerTheme == UiPreferencesStore.DarkTheme
                ? UiPreferencesStore.LightTheme : UiPreferencesStore.DarkTheme;
            var incomplete = false;
            try
            {
                Pump(runtime.SaveSettingsAsync("{\"automation\":{\"startWithWindows\":" +
                    requested.ToString().ToLowerInvariant() + "},\"interface\":{\"theme\":\"" + patchTheme + "\"}}",
                    default));
            }
            catch (SyncCliOperationException error)
            {
                incomplete = error.Message.Contains("Restoration incomplete", StringComparison.Ordinal) &&
                    error.Message.Contains("theme", StringComparison.Ordinal);
            }
            require(incomplete && preferences.Load().Theme == newerTheme,
                "a newer same-field write is retained and the incomplete restoration is reported honestly");
            require(!Field<bool>(window, "_cliSettingsGateHeld"),
                "the incomplete-restoration save still releases the shared preference gate");
        }
        finally
        {
            window.StartupRegistrationForTest = value => updates.Add(value);
        }
    }

    // RC05-T7/T8 and RC05-C: the shared registry, parser and dispatcher route the typed Engine preference
    // command and result through the Engine gateway. A standalone Engine runtime and this running legacy
    // application both refuse it instead of falling back to old settings or a direct local store write.
    private static void CheckEnginePreferenceRoute(CliAppRuntime runningApplication,
        UiPreferencesStore preferences, Action<bool, string> require)
    {
        var before = preferences.Load();
        var show = SyncCliRegistry.Parse(["--cli", "engine", "preferences", "show"]);
        require(show.Command == "engine preferences show" &&
                SyncCliRegistry.Rows.Single(row => row.Command == show.Command).Mode == "existing-host-only",
            "the shared registry publishes engine preferences show as existing-host-only");
        var recording = new RecordingPreferenceEngine(before);
        var jsonOutput = new StringWriter();
        require(Pump(SyncCli.ExecuteAsync(["--cli", "engine", "preferences", "show", "--json"], jsonOutput,
                new StringWriter(), () => recording)) == 0 &&
                recording.EngineCalls == 1 && recording.SettingsLoads == 0,
            "the --cli parser and dispatcher route engine preferences show to the Engine gateway, not the legacy settings surface");
        var json = JsonDocument.Parse(jsonOutput.ToString().Trim()).RootElement;
        require(json.GetProperty("command").GetString() == "engine preferences show" &&
                json.GetProperty("ok").GetBoolean() &&
                json.GetProperty("data").GetProperty("preferences").GetProperty("theme").GetString() ==
                before.Theme,
            "the JSON preference result is the typed saved value");
        var humanOutput = new StringWriter();
        require(Pump(SyncCli.ExecuteAsync(["--cli", "engine", "preferences", "show"], humanOutput,
                new StringWriter(), () => recording)) == 0 &&
                humanOutput.ToString().Contains("theme: " + before.Theme, StringComparison.Ordinal),
            "the human preference result lists the same typed saved value");
        var resultTheme = before.Theme == UiPreferencesStore.DarkTheme
            ? UiPreferencesStore.LightTheme : UiPreferencesStore.DarkTheme;
        recording.Result = new UiPreferencesResult(before with { Theme = resultTheme });
        var setOutput = new StringWriter();
        require(Pump(SyncCli.ExecuteAsync(["--cli", "engine", "preferences", "set", "--settings",
                    "{\"theme\":\"" + resultTheme + "\"}", "--yes", "--json"], setOutput,
                new StringWriter(), () => recording)) == 0 &&
                recording.LastInvocation!.Value("--settings") == "{\"theme\":\"" + resultTheme + "\"}" &&
                recording.SettingsLoads == 0,
            "engine preferences set hands the exact changed-field patch to the Engine gateway");
        var setJson = JsonDocument.Parse(setOutput.ToString().Trim()).RootElement;
        require(setJson.GetProperty("data").GetProperty("preferences").GetProperty("theme").GetString() ==
                resultTheme,
            "engine preferences set returns the typed committed result");

        var engineCalls = recording.EngineCalls;
        foreach (var rejected in new[]
                 {
                     new[] { "--cli", "engine", "preferences", "set", "--settings", "{\"theme\":\"Dark\"}" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings", "{}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings", "{\"bogus\":true}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings", "{\"theme\":\"Blue\"}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings",
                         "{\"theme\":\"Dark\",\"Theme\":\"Light\"}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings",
                         "{\"keepRunningWhenClosed\":null}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings",
                         "{\"pageViewModes\":{\"Bad Key\":\"Detailed\"}}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings",
                         "{\"pauseUntilResumed\":true}", "--yes" },
                     new[] { "--cli", "engine", "preferences", "set", "--settings", "{\"theme\":\"Dark\"}",
                         "--pair", "p", "--yes" }
                 })
        {
            var refused = false;
            try { SyncCliRegistry.Parse(rejected); }
            catch (SyncCliUsageException) { refused = true; }
            require(refused,
                "the parser refuses an invalid Engine preference request before any runtime: " +
                string.Join(' ', rejected.Skip(3).Take(2)));
        }
        require(recording.EngineCalls == engineCalls,
            "rejected Engine preference requests never reach the Engine gateway");

        var unchanged = preferences.Load();
        var standalone = new EngineCliRuntime();
        var standaloneRefused = false;
        try { _ = Pump(SyncCliRegistry.DispatchAsync(standalone, show, TextWriter.Null, default)); }
        catch (SyncCliUnsupportedException) { standaloneRefused = true; }
        finally { Pump(standalone.DisposeAsync().AsTask()); }
        require(standaloneRefused && PreferencesValuesEqual(unchanged, preferences.Load()),
            "a standalone Engine runtime refuses the preference command instead of taking local authority");

        var bridgeRefused = false;
        try { _ = Pump(SyncCliRegistry.DispatchAsync(runningApplication, show, TextWriter.Null, default)); }
        catch (SyncCliUnsupportedException) { bridgeRefused = true; }
        require(bridgeRefused && PreferencesValuesEqual(unchanged, preferences.Load()),
            "the legacy running application refuses the Engine preference command instead of its old bridge or direct store");

        var preMerge = preferences.Load();
        var committed = Pump(preferences.UpdateAsync(UiPreferencesUpdate.Parse(
            "{\"theme\":\"" + resultTheme + "\"}")));
        require(committed.Theme == resultTheme && SamePreferencesExceptTheme(preMerge, committed) &&
                preferences.Load().Theme == resultTheme,
            "the shared changed-field merge commits exactly the named field and preserves every unrelated member");
    }

    private static bool SamePreferencesExceptTheme(UiPreferences before, UiPreferences after) =>
        PreferencesValuesEqual(before with { Theme = after.Theme }, after);

    // Records the exact Engine invocation and returns the typed result. Every legacy runtime member refuses,
    // so a silently routed old settings call fails instead of passing.
    private sealed class RecordingPreferenceEngine(UiPreferences shown) : ISyncCliRuntime, IEngineCliAccess
    {
        internal UiPreferencesResult Result { get; set; } = new(shown);
        internal int EngineCalls { get; private set; }
        internal int SettingsLoads { get; private set; }
        internal SyncCliInvocation? LastInvocation { get; private set; }

        public Task<object> RunEngineCommandAsync(SyncCliInvocation invocation, Action<string> progress,
            CancellationToken ct)
        {
            EngineCalls++;
            LastInvocation = invocation;
            return Task.FromResult<object>(Result);
        }

        public Task<SyncCliStatusData> LoadStatusAsync(CancellationToken cancellationToken) => Refuse<SyncCliStatusData>();
        public Task<SyncCliAccountListData> LoadAccountsAsync(CancellationToken cancellationToken) =>
            Refuse<SyncCliAccountListData>();
        public Task<SyncCliPairListData> LoadPairsAsync(CancellationToken cancellationToken) =>
            Refuse<SyncCliPairListData>();
        public Task<SyncCliPairData> LoadPairAsync(string pairId, CancellationToken cancellationToken) =>
            Refuse<SyncCliPairData>();
        public Task<SyncCliActivityListData> LoadActivityAsync(string? accountId, int limit, int offset,
            CancellationToken cancellationToken, SyncActivityQuery? query = null, string? search = null,
            string? filter = null, string? pairId = null, string? itemType = null, DateTimeOffset? sinceUtc = null) =>
            Refuse<SyncCliActivityListData>();
        public Task<SyncCliSettingsData> LoadSettingsAsync(CancellationToken cancellationToken)
        {
            SettingsLoads++;
            return Refuse<SyncCliSettingsData>();
        }
        public Task<SyncCliPlanData> LoadPlanAsync(string pairId, CancellationToken cancellationToken,
            int limit = SyncCli.ActivityDefaultLimit, int offset = 0) => Refuse<SyncCliPlanData>();
        public Task<SyncCliRunData> RunPairAsync(string pairId, Action<string> progress,
            CancellationToken cancellationToken) => Refuse<SyncCliRunData>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<T> Refuse<T>() => Task.FromException<T>(new InvalidOperationException(
            "The Engine preference route must not use the legacy runtime surface."));
    }
}
