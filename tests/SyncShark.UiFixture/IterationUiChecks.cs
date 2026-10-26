using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class IterationUiChecks
{
    internal static int Run(string root)
    {
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(appLock.StateAuthority);
        using var theme = new ThemeManager(preferences); theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1100, Height = 760 };
        window.StartupRegistrationForTest = _ => throw new InvalidOperationException("This test must not modify Windows startup.");
        try
        {
            window.Show(); Drain();
            // UI20: the Connections account identity and its one clickable linked-pair count.
            CheckConnectionsAccountIdentity(root);
            CheckAccountRefreshAfterClose(root);
            var version = typeof(App).Assembly.GetName().Version!;
            var productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(App).Assembly.Location).ProductVersion!;
            Require(version == typeof(SyncPair).Assembly.GetName().Version && productVersion.StartsWith($"{version.Major}.{version.Minor}.{version.Build}+", StringComparison.Ordinal),
                "UI, Core and informational release versions agree");
            Call(window, "NavigateTo", "Settings", true);
            var original = preferences.Load();
            var appearance = Field<ComboBox>(window, "_settingsAppearance");
            appearance.SelectedItem = "Light"; Drain();
            Require(preferences.Load().Theme == original.Theme, "Theme preview does not persist an unsaved draft");
            Require(Field<Button>(window, "_settingsSave").Visibility == Visibility.Visible, "Dirty category exposes header Save");
            var toggles = Field<Dictionary<string, CheckBox>>(window, "_settingsToggles");
            toggles["problems"].IsChecked = !original.NotifyProblems;
            Call(window, "SettingsDraftChanged");
            Require(Pump((Task<bool>)Call(window, "SaveSettingsCategoryAsync", 2)!), "Notification category saves");
            Require(preferences.Load().Theme == original.Theme && preferences.Load().NotifyProblems != original.NotifyProblems,
                "Saving Notifications does not commit the General draft");
            Pump(window.SetSyncPauseAsync(TimeSpan.FromMinutes(5), false));
            var pause = preferences.Load().PauseUntilUtc;
            Require(Pump((Task<bool>)Call(window, "SaveSettingsCategoryAsync", 0)!), "General category saves");
            Require(preferences.Load().Theme == "Light" && preferences.Load().PauseUntilUtc == pause &&
                preferences.Load().NotifyProblems != original.NotifyProblems, "General save preserves timed pause and notification settings");
            appearance.SelectedItem = "Dark"; Drain();
            Require(Field<TextBlock>(window, "_settingsFeedback").Visibility == Visibility.Hidden,
                "A new edit clears the previous Changes Saved message");
            Call(window, "DiscardSettingsCategory", 0);
            Require(appearance.SelectedItem?.ToString() == "Light" && theme.Preference == "Light", "Discard restores the saved theme and preview");
            // UI21: walk the real Settings tabs through the selection the product itself
            // uses and observe the real Save and Discard affordances, rather than an
            // internal dirty predicate. Browsing Explorer, About & Updates or Diagnostics must neither
            // offer nor perform another category's save and discard.
            var settingsTabs = Field<TabControl>(window, "_settingsTabs");
            var settingsTabNames = new[] { "General", "Sync & Performance", "Notifications", "Explorer", "About & Updates", "Diagnostics" };
            Require(settingsTabs.Items.Count == settingsTabNames.Length, "Settings exposes exactly its six tabs");
            for (var index = 0; index < settingsTabNames.Length; index++)
            {
                ((TabItem)settingsTabs.Items[index]).IsSelected = true; Drain();
                Require(settingsTabs.SelectedIndex == index && ((TabItem)settingsTabs.Items[index]).IsSelected &&
                    ((TabItem)settingsTabs.Items[index]).Header.ToString() == settingsTabNames[index],
                    "Settings navigation reaches " + settingsTabNames[index]);
            }
            ((TabItem)settingsTabs.Items[0]).IsSelected = true; Drain();
            appearance.SelectedItem = "Dark"; Drain();
            var savedTheme = preferences.Load().Theme;
            Require(Field<Button>(window, "_settingsSave").Visibility == Visibility.Visible &&
                Field<Button>(window, "_settingsDiscard").Visibility == Visibility.Visible,
                "A dirty General tab offers its real Save and Discard affordances");
            foreach (var index in new[] { 3, 4, 5 })
            {
                ((TabItem)settingsTabs.Items[0]).IsSelected = true; Drain();
                ((TabItem)settingsTabs.Items[index]).IsSelected = true; Drain();
                Require(Field<Button>(window, "_settingsSave").Visibility != Visibility.Visible &&
                    Field<Button>(window, "_settingsDiscard").Visibility != Visibility.Visible,
                    settingsTabNames[index] + " collapses Save and Discard for another category's draft");
                Field<Button>(window, "_settingsSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Field<Button>(window, "_settingsDiscard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(Task.Delay(5)); Drain();
                Require(preferences.Load().Theme == savedTheme && appearance.SelectedItem?.ToString() == "Dark",
                    "Browsing " + settingsTabNames[index] + " commits nothing and keeps the draft");
            }
            ((TabItem)settingsTabs.Items[0]).IsSelected = true; Drain();
            Require(Field<Button>(window, "_settingsSave").Visibility == Visibility.Visible &&
                appearance.SelectedItem?.ToString() == "Dark" && preferences.Load().Theme == savedTheme &&
                ((TabItem)settingsTabs.Items[0]).ToolTip?.ToString() == "General: Unsaved Changes",
                "Returning to General finds the same unsaved draft and its warning intact");
            Field<Button>(window, "_settingsDiscard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(5)); Drain();
            Require(appearance.SelectedItem?.ToString() == "Light" &&
                Field<Button>(window, "_settingsSave").Visibility != Visibility.Visible,
                "The real Discard affordance returns General to its saved value");
            var interval = Field<TextBox>(window, "AutomationIntervalBox");
            interval.Text = "0";
            Require(!Pump((Task<bool>)Call(window, "SaveSettingsCategoryAsync", 1)!), "Invalid settings fail without clearing the draft");
            Require((bool)Call(window, "SettingsCategoryDirty", 1)!, "Rejected draft remains editable");
            var failedFeedback = Field<TextBlock>(window, "_settingsFeedback").Text;
            Call(window, "SettingsDraftChanged");
            Require(Field<TextBlock>(window, "_settingsFeedback").Visibility == Visibility.Visible &&
                Field<TextBlock>(window, "_settingsFeedback").Text == failedFeedback,
                "Draft refresh does not erase a real failed-save message");
            Call(window, "DiscardSettingsCategory", 1);
            appearance.SelectedItem = "Dark"; Drain();
            window.UnsavedNavigationChoiceForTest = () => AppDialogChoice.Cancel;
            Call(window, "NavigateTo", "Dashboard", true);
            Require(Field<string>(window, "_workspacePage") == "Settings", "Stay Here prevents navigation with a draft");
            window.UnsavedNavigationChoiceForTest = () => AppDialogChoice.Secondary;
            Call(window, "NavigateTo", "Dashboard", true);
            Require(Field<string>(window, "_workspacePage") == "Dashboard" && appearance.SelectedItem?.ToString() == "Light",
                "Discard navigation restores the baseline and leaves");
            Call(window, "SidebarToggle_Click", window, new RoutedEventArgs()); Drain();
            var navigation = Field<ListBox>(window, "Navigation");
            Require(navigation.Items.OfType<ListBoxItem>().All(item => item.HorizontalContentAlignment == HorizontalAlignment.Center),
                "Collapsed navigation uses a common centered icon cell");
            Require(Field<StackPanel>(window, "HeaderBrandPanel").HorizontalAlignment == HorizontalAlignment.Center,
                "Collapsed logo is centered in its allocated width");
            var sidebarToggle = Field<Button>(window, "SidebarToggle");
            Require(sidebarToggle.Width == 40 && sidebarToggle.MinWidth == 0 && sidebarToggle.HorizontalAlignment == HorizontalAlignment.Center,
                "Collapse control fits and centers inside the compact sidebar");
            Require(Call(window, "PathLink", new ActivityRow("", "sample.txt", "Uploaded", ""), "Not recorded for this event", "local") is TextBlock,
                "Missing locations are plain text, not misleading action links");
            var lookup = typeof(ShellTypeImage).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!;
            var iconHashes = new Dictionary<string, string>();
            foreach (var extension in new[] { ".txt", ".pdf", ".jpg", ".m4a", "<folder>", ".syncsharkunknown" })
            {
                var image = Pump(Task.Run(() => lookup.Invoke(null, [extension]))) as BitmapSource;
                Require(image is { PixelWidth: > 0, IsFrozen: true }, "Windows association lookup returns a frozen native image: " + extension);
                var converted = new FormatConvertedBitmap(image!, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[converted.PixelHeight * converted.PixelWidth * 4];
                converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
                iconHashes[extension] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
            }
            Require(iconHashes[".txt"] != iconHashes[".syncsharkunknown"], "Text file uses the associated icon, not the Windows unknown-file icon");
            Require(Massdex.SyncShark.V2.AutoSyncProcess.IsProductStartupCommand(
                Massdex.SyncShark.V2.AutoSyncProcess.StartupCommand(@"C:\Builds\Current\SyncShark.exe")),
                "Startup ownership recognizes build-folder supervisor commands");
            Require(!Massdex.SyncShark.V2.AutoSyncProcess.IsProductStartupCommand(@"""C:\foreign\SyncShark.exe"" --background"),
                "A legacy background command is not the supervisor owner");
            Pump(CheckUpdateBridgeAsync(root));
            Pump(CheckRollbackGuardAsync(root));
            var explorer = ExplorerRequest.Parse(["--explorer", "open", Path.Combine(root, "sample.txt")]);
            Require(explorer?.Operation == "open" && ExplorerRequest.Deserialize(explorer.Serialize()) == explorer,
                "Explorer action preserves one exact local item through the IPC payload");
            foreach (var operation in new[] { "open", "web", "share" })
                Require(!ExplorerRequest.Parse(["--explorer", operation, Path.Combine(root, "sample.txt")])!.RequiresCompletedStartup,
                    operation + " is local navigation and does not depend on network verification or recovery completion");
            foreach (var operation in new[] { "sync", "offline", "online-only" })
                Require(ExplorerRequest.Parse(["--explorer", operation, Path.Combine(root, "sample.txt")])!.RequiresCompletedStartup,
                    operation + " cannot race startup recovery");
            CheckExplorerStartupDispatch(window, root);
            foreach (var arguments in new[] {
                new[] { "--explorer", "open", "C:\\one.txt", "C:\\two.txt" },
                new[] { "--explorer", "delete", "C:\\one.txt" },
                new[] { "--explorer", "share", "\\\\server\\private.txt" },
                new[] { "--explorer", "sync", "C:\\one.txt:stream" } })
            {
                try { ExplorerRequest.Parse(arguments); throw new IOException("An unsafe Explorer request was accepted."); }
                catch (InvalidDataException) { }
            }
            Require(true, "Explorer rejects multiple items, unsupported effects, network paths and alternate streams");
            Console.WriteLine("PASS Isolated UI draft, navigation, icon and presentation checks");
            return 0;
        }
        finally { window.RequestProductExit(); MainWindow.SuppressLoadedStartupForTest = false; app.Shutdown(); }
    }
    private static void CheckExplorerStartupDispatch(MainWindow window, string root)
    {
        // Exercise the real dispatch entry point. This fixture is not installed,
        // so the existing installed-process guard must stop every admitted request
        // before pair/provider effects. It is intentionally not bypassed for tests.
        Require(!ExplorerIntegration.IsInstalledProcess, "Startup dispatch fixture cannot perform installed Explorer effects");
        var console = Field<TextBox>(window, "ConsoleText");
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Dispatch() => Pump((Task)Call(window, "DispatchExplorerRequestAsync")!);
        foreach (var operation in new[] { "open", "web", "share", "sync", "offline", "online-only" })
        {
            Set("_explorerLocalReady", false); Set("_explorerStartupReady", false); Set("_explorerStartupFailed", false);
            var request = ExplorerRequest.Parse(["--explorer", operation, Path.Combine(root, "sample.txt")])!;
            console.Clear(); window.QueueExplorerRequest(request); Dispatch();
            Require(Field<ExplorerRequest?>(window, "_pendingExplorerRequest") == request && console.Text.Length == 0,
                operation + " remains queued before local state is ready");
            Set("_explorerLocalReady", true); Dispatch();
            if (request.RequiresCompletedStartup)
            {
                Require(Field<ExplorerRequest?>(window, "_pendingExplorerRequest") == request && console.Text.Length == 0,
                    operation + " remains queued while startup recovery is pending");
                Set("_explorerStartupReady", true); Dispatch();
            }
            Require(Field<ExplorerRequest?>(window, "_pendingExplorerRequest") is null &&
                console.Text.Contains("require the installed Sync Shark application", StringComparison.Ordinal),
                operation + " passes only its required startup stage and still reaches the real installed-process guard");
        }
        foreach (var localReady in new[] { false, true })
        {
            Set("_explorerLocalReady", localReady); Set("_explorerStartupReady", false); Set("_explorerStartupFailed", false);
            console.Clear();
            var request = ExplorerRequest.Parse(["--explorer", "online-only", Path.Combine(root, "sample.txt")])!;
            window.QueueExplorerRequest(request);
            Set("_explorerStartupFailed", true); Dispatch();
            Require(Field<ExplorerRequest?>(window, "_pendingExplorerRequest") is null &&
                console.Text.Contains("Startup did not finish", StringComparison.Ordinal) &&
                !Field<bool>(window, "_explorerDispatching"),
                "Startup failure clears queued file-changing requests with a visible error (local ready=" + localReady + ")");
        }
        Set("_explorerLocalReady", true); Set("_explorerStartupFailed", true); console.Clear();
        window.QueueExplorerRequest(ExplorerRequest.Parse(["--explorer", "open", Path.Combine(root, "sample.txt")])!); Dispatch();
        Require(Field<ExplorerRequest?>(window, "_pendingExplorerRequest") is null &&
            console.Text.Contains("require the installed Sync Shark application", StringComparison.Ordinal),
            "Loaded local navigation remains available after later startup failure without admitting file changes");
        Set("_explorerLocalReady", false); Set("_explorerStartupReady", false); Set("_explorerStartupFailed", false);
    }
    private static async Task CheckRollbackGuardAsync(string root)
    {
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "rollback-check", "state", "instance.lock"));
        var local = Path.Combine(root, "rollback-local"); Directory.CreateDirectory(local);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
        var pair = new SyncPair("rollback-fixture", "Rollback Fixture", local, identity.VolumeSerialNumber, identity.FileId, "/Fixture", "id:fixture");
        var store = new SyncPairStore(instance.StateAuthority);
        await store.SaveAsync(new SyncPairSet([pair], pair.Id));
        var database = new SyncAuthorityDatabase(Path.Combine(root, "rollback-check", "vNext", "state", "syncshark.db"));
        await database.InitializeAsync("rollback-fixture", "fixture", "UNBOUND", "{}");
        var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(database.DatabasePath));
        // This fixture also runs beside an installed release. Never unregister the user's
        // shell integration to manufacture a clean-machine precondition for a read-only check.
        using var fileMenu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\Massdex.SyncShark");
        using var folderMenu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\Massdex.SyncShark");
        using var registeredRoots = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
        var productPrefix = "Massdex.SyncShark!" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + "!";
        var hostHasRegistration = fileMenu is not null || folderMenu is not null ||
            registeredRoots?.GetSubKeyNames().Any(name => name.StartsWith(productPrefix, StringComparison.Ordinal)) == true;
        Require(await RollbackReadiness.CheckAsync(instance.StateAuthority) == !hostHasRegistration,
            hostHasRegistration
                ? "Read-only rollback check conservatively refuses this host's existing Windows registration"
                : "Read-only rollback check accepts ordinary local state without Windows registrations");
        Require(before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(database.DatabasePath))),
            "Rollback readiness reads SQLite without modifying its bytes");
        await store.SaveAsync(new SyncPairSet([pair with { FilesOnDemandEnabled = true }], pair.Id));
        Require(!await RollbackReadiness.CheckAsync(instance.StateAuthority), "Rollback refuses retained managed-cloud configuration before changing any state");
    }
    private static async Task CheckUpdateBridgeAsync(string root)
    {
        // root736: the tiny inert unsigned-update fixture must be on an ordinary FIXED NTFS local volume, because
        // the production update-input lease correctly rejects a REMOVABLE volume and F: is removable. So the real
        // signature-guard leg runs from C: local temp, and the F: placement is retained as its OWN expected
        // volume refusal - never presented as a signature verdict. Logs and captures stay on F.
        string PreparePackage(string parent)
        {
            var folder = Path.Combine(parent, "update-package", "deploy-scripts");
            Directory.CreateDirectory(folder);
            var installer = Path.Combine(folder, "Install-SyncShark.ps1");
            var marker = Path.Combine(folder, "must-not-execute.txt");
            File.WriteAllText(installer, "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value executed");
            File.WriteAllText(Path.Combine(folder, "SyncShark.Release.psm1"), "# Inert fixture module; must never be imported.");
            var package = Path.Combine(parent, "update-package", "package");
            Directory.CreateDirectory(Path.Combine(package, "signed-candidate"));
            File.WriteAllText(Path.Combine(package, "unsigned-candidate-framework-manifest.json"), "{}");
            return installer;
        }
        async Task<(int Exit, string Output, string Error, bool Marker)> RunAsync(string packageParent)
        {
            var installer = PreparePackage(packageParent);
            var marker = Path.Combine(Path.GetDirectoryName(installer)!, "must-not-execute.txt");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(installer))).ToLowerInvariant();
            var start = typeof(MainWindow).GetMethod("StartUpdateProcessAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            using var process = await (Task<System.Diagnostics.Process>)start.Invoke(null, [installer, hash, "0.3.17", false])!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            return (process.ExitCode, await output, await error, File.Exists(marker));
        }
        // Only a removable fixture root can exercise the volume-refusal leg. On a fixed root (the CI
        // runner's C: scratch root) the package reaches the signature guard instead, so that leg is
        // skipped there and the ordinary fixed-NTFS signature contract below still runs unchanged.
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).DriveType == DriveType.Removable)
        {
            var removable = await RunAsync(root);
            Require(removable.Exit != 0 && !removable.Marker,
                "The update bridge refuses an unsigned package placed on the removable F: volume without executing it");
            Require(removable.Error.Contains("NTFS", StringComparison.OrdinalIgnoreCase) ||
                    removable.Error.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
                    removable.Error.Contains("local", StringComparison.OrdinalIgnoreCase) ||
                    removable.Error.Contains("volume", StringComparison.OrdinalIgnoreCase),
                "The F: placement refusal is the volume input-lease refusal, not a signature verdict: " + removable.Error.Trim());
        }
        var tempParent = Path.Combine(Path.GetTempPath(), "syncshark-update-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempParent);
            var drive = new DriveInfo(Path.GetPathRoot(tempParent)!);
            Require(drive.DriveType == DriveType.Fixed &&
                    drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) &&
                    (new DirectoryInfo(tempParent).Attributes & FileAttributes.ReparsePoint) == 0,
                "the unsigned-update fixture lives on an ordinary fixed NTFS local volume without a reparse point");
            var fixedDrive = await RunAsync(tempParent);
            Require(fixedDrive.Exit != 0 && !fixedDrive.Marker && !fixedDrive.Output.Contains("\"ok\":true"),
                "The real embedded update bridge rejects unsigned packages without executing them");
            Require(fixedDrive.Error.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                    fixedDrive.Error.Contains("signed", StringComparison.OrdinalIgnoreCase) ||
                    fixedDrive.Error.Contains("signer", StringComparison.OrdinalIgnoreCase),
                "Update rejection reaches the signature guard, not a missing runtime or parser failure: " + fixedDrive.Error.Trim());
        }
        finally
        {
            try { if (Directory.Exists(tempParent)) Directory.Delete(tempParent, recursive: true); } catch { }
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    // UI20 consumer checks on a real window: the Connections account label is the real provider mark
    // plus the exact email with the holder in brackets, the linked-pair count is the only control for
    // that link, and the link filters Folder Pairs by the exact connection id rather than a display
    // name or a row index. Source-only: never executed under the resource hold, so UNVERIFIED.
    private static void CheckConnectionsAccountIdentity(string root)
    {
        var stateRoot = Path.Combine(root, "ui20-connections");
        Directory.CreateDirectory(stateRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var personal = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "ui20-personal", "ui20-personal-root", "ui20-personal-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Shared Holder", Email: "personal@example.test").Normalize();
        Pump(connections.SaveNewAsync(personal));
        var work = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "ui20-work", "ui20-work-root", "ui20-work-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Shared Holder", Email: "work@example.test").Normalize();
        Pump(connections.SaveAsync(work with
        {
            CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(work.ConnectionId)
        }));
        var noMail = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "ui20-nomail", "ui20-nomail-root", "ui20-nomail-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "No Mail Holder").Normalize();
        Pump(connections.SaveAsync(noMail with
        {
            CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(noMail.ConnectionId)
        }));
        const string permissionId = "ui20-google";
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId,
            "google@example.test", "Google Holder", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Pump(new GoogleDriveConnectionStore(instance.StateAuthority).SaveAsync(google));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1240, Height = 790 };
        window.StartupRegistrationForTest = _ => throw new InvalidOperationException("This test must not modify Windows startup.");
        window.Show(); Drain();
        try
        {
            Call(window, "NavigateTo", "Connections", true); Drain(); window.UpdateLayout();
            var picker = Field<ComboBox>(window, "_accountPicker");
            var label = Field<TextBlock>(window, "ConnectionNameText");
            var email = Field<TextBlock>(window, "ConnectionEmailText");
            var mark = Field<ContentControl>(window, "_connectionProviderIcon");
            var pairsLink = Field<Button>(window, "_connectionPairsLink");
            var choices = picker.Items.Cast<StorageAccountChoice>().ToArray();
            Require(choices.Length == 4,
                "Two Dropbox accounts, the missing-email account and the second provider are selectable identities");
            // U1 removed the repeated visible provider prefix: the mark carries the provider and the
            // accessible name keeps it explicit.
            Require(choices.Single(item => item.Id == work.ConnectionId).Label == "work@example.test (Shared Holder)" &&
                    choices.Single(item => item.Id == work.ConnectionId).AccessibleName == "Dropbox work@example.test (Shared Holder)" &&
                    choices.Single(item => item.Id == work.ConnectionId).ProviderKey == SyncProviders.Dropbox,
                "The account selector uses the exact email with the holder in brackets, with the provider carried by the mark and the accessible name");
            Require(MainWindow.AccountLabel("Dropbox", "Holder", "mail@example.test") == "mail@example.test (Holder)" &&
                MainWindow.AccountLabel("Dropbox", "", "mail@example.test") == "mail@example.test" &&
                MainWindow.AccountLabel("Dropbox", "Holder", "") == "Holder" &&
                MainWindow.AccountLabel("Dropbox", "", "") == "Dropbox account",
                "The account label fallback is deterministic for a missing name or a missing email");

            SelectAccount(window, picker, personal.ConnectionId);
            Require(mark.ContentTemplate is DataTemplate personalMark &&
                ReferenceEquals(personalMark, window.FindResource("DropboxIconTemplate")),
                "A Dropbox account shows the real Dropbox provider mark");
            Require(label.Text == "personal@example.test (Shared Holder)",
                "The account label is the exact email with the holder in brackets: " + label.Text);
            Require(email.Visibility == Visibility.Collapsed, "The email is carried once, by the account label");
            var personalLabel = label.Text;

            SelectAccount(window, picker, work.ConnectionId);
            Require(label.Text == "work@example.test (Shared Holder)" && !string.Equals(label.Text, personalLabel, StringComparison.Ordinal),
                "Two accounts that share a display name remain visually distinct by their exact email");
            Require(label.ToolTip?.ToString()?.Contains(work.ConnectionId, StringComparison.Ordinal) == true &&
                label.ToolTip?.ToString()?.Contains("Dropbox account", StringComparison.Ordinal) == true,
                "The account label tooltip keeps the provider and the stable connection id");

            SelectAccount(window, picker, noMail.ConnectionId);
            Require(label.Text == "No Mail Holder", "A provider account without an email falls back to its holder name");
            Require(label.ToolTip?.ToString()?.Contains(noMail.ConnectionId, StringComparison.Ordinal) == true,
                "An account without an email keeps a stable identity in its tooltip");

            SelectAccount(window, picker, google.ConnectionId);
            Require(mark.ContentTemplate is DataTemplate googleMark &&
                ReferenceEquals(googleMark, window.FindResource("GoogleDriveIconTemplate")),
                "The second provider shows its own real provider mark");
            Require(label.Text == "google@example.test (Google Holder)",
                "The Google Drive account label uses its own exact email and holder");
            // P1: the Add Account choices carry the same official provider mark the Connections
            // selectors use, rather than a generic glyph inferred from the provider name.
            var storageAccountMenuItem = typeof(MainWindow).GetMethod("StorageAccountMenuItem",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            MenuItem AddAccountChoice(string name, string provider, string templateKey)
            {
                var clickCount = 0;
                var produced = (MenuItem)storageAccountMenuItem.Invoke(window,
                    [name, provider, new RoutedEventHandler((_, _) => clickCount++)])!;
                Require(produced.Header is StackPanel { Children.Count: 2 } choiceHeader &&
                        choiceHeader.Children[0] is ContentControl { ContentTemplate: DataTemplate choiceMark } &&
                        ReferenceEquals(choiceMark, window.FindResource(templateKey)) &&
                        choiceHeader.Children[1] is TextBlock { Text: var choiceText } && choiceText == name &&
                        System.Windows.Automation.AutomationProperties.GetName(produced) == name,
                    "The Add Account " + name + " choice carries the official provider mark with its unchanged label");
                produced.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(clickCount == 1, "The Add Account " + name + " choice retains its supplied click handler");
                return produced;
            }
            var dropboxChoice = AddAccountChoice("Dropbox", SyncProviders.Dropbox, "DropboxIconTemplate");
            var googleChoice = AddAccountChoice("Google Drive", SyncProviders.GoogleDrive, "GoogleDriveIconTemplate");
            Require(dropboxChoice.IsEnabled && googleChoice.IsEnabled,
                "Both Add Account choices stay enabled with their own handlers");
            Require(pairsLink.Content is StackPanel { Children.Count: 2 } pairsContent &&
                pairsContent.Children[0] is Viewbox &&
                ReferenceEquals(pairsContent.Children[1], window.FindName("ConnectionPairsText")) &&
                pairsLink.Focusable && pairsLink.IsEnabled,
                "The linked-pair count is one clickable control carrying its own icon");
            Require(!Visuals<Button>(window.ConnectionsPage).Any(button =>
                    string.Equals(System.Windows.Automation.AutomationProperties.GetName(button), "View Pairs", StringComparison.Ordinal) ||
                    string.Equals(button.ToolTip?.ToString(), "View Pairs", StringComparison.Ordinal)),
                "The duplicate View Pairs action is removed from Connections");

            // The final Connections decision removed the Account Details Expander: the useful account
            // fields are flat aligned SettingRows in the product, so these assertions read the real
            // production rows - the exact label each row carries, the exact value it holds and the real
            // two-column row layout the value sits alone in.
            // The detail rows describe the account the page has selected, so the check selects the exact
            // Dropbox account whose stable connection id it asserts and drives the real production status
            // path through the existing test seam rather than inheriting the last provider switch above.
            MainWindow.DropboxConnectionStatusLoaderForTest = _ => Task.FromResult(
                new DropboxConnectionStatus(work, true, false, "Connected; live verification pending.", null, 0, null));
            try
            {
                SelectAccount(window, picker, work.ConnectionId);
                Require(!Visuals<Expander>(window.ConnectionsPage).Any(expander =>
                        expander.Header is StackPanel header &&
                        header.Children.OfType<TextBlock>().Any(text => text.Text == "Account Details")),
                    "The removed Account Details Expander is not restored on Connections");
                Require(new[] { "ConnectionProviderText", "ConnectionTypeText", "ConnectionAccessText",
                        "ConnectionLastSyncText", "ConnectionIdText" }.All(name => IsAloneSettingRowValue(window, name)),
                    "Every account field is a flat SettingRow value alone in its own value column");
                var providerValue = Field<TextBlock>(window, "ConnectionProviderText");
                Require(SettingRowLabel(window, "ConnectionProviderText") == "Provider" && providerValue.Text == "Dropbox" &&
                    providerValue.IsVisible,
                    "The Provider row carries the exact label and the selected account's real provider value");
                Require(SettingRowLabel(window, "ConnectionTypeText") == "Plan" &&
                    Field<TextBlock>(window, "ConnectionTypeText").Text == "—",
                    "The Plan row carries the exact label and the account's own plan value");
                Require(SettingRowLabel(window, "ConnectionAccessText") == "Access" &&
                    Field<TextBlock>(window, "ConnectionAccessText").Text == "Full Dropbox",
                    "The Access row carries the exact label and the account's real access value");
                Require(SettingRowLabel(window, "ConnectionLastSyncText") == "Last Successful Sync" &&
                    Field<TextBlock>(window, "ConnectionLastSyncText").Text == "—",
                    "The Last Successful Sync row carries the exact label and the reported sync value");
                Require(SettingRowLabel(window, "ConnectionIdText") == "Connection ID" &&
                    Field<TextBlock>(window, "ConnectionIdText").Text == work.ConnectionId,
                    "The Connection ID row carries the exact label and the account's stable connection id");
                Require(IsAloneSettingRowValue(window, "ConnectionHealthText") &&
                    IsAloneSettingRowValue(window, "ConnectionVerifiedText"),
                    "The check status and verification timestamp keep their own value column, so an action cannot overlap them");
            }
            finally { MainWindow.DropboxConnectionStatusLoaderForTest = null; }

            // Two pairs on one Dropbox account, one on the other, none on the second provider.
            window.LoadPairSetForTest(new SyncPairSet([
                new SyncPair("ui20-personal-a", "Documents Personal", @"C:\ui20\personal-a", 1, 1, "/PersonalA", "id:personal-a",
                    ConnectionId: personal.ConnectionId),
                new SyncPair("ui20-personal-b", "Documents Backup", @"C:\ui20\personal-b", 1, 1, "/PersonalB", "id:personal-b",
                    ConnectionId: personal.ConnectionId),
                new SyncPair("ui20-work", "Documents Work", @"C:\ui20\work", 1, 1, "/Work", "id:work",
                    ConnectionId: work.ConnectionId)
            ], "ui20-personal-a")); Drain();
            var search = Field<TextBox>(window, "PairSearchBox");
            search.Text = "Documents"; Drain();
            var pairList = Field<DataGrid>(window, "PairList");
            var selectedIdBefore = (pairList.SelectedItem as PairRow)?.Id;
            var editedBefore = Field<string>(window, "_editingPairRootId");
            Require(VisiblePairIds(window).Length == 3 && editedBefore == "ui20-personal-a",
                "Every seeded pair is visible before the link is used and its editor draft is open");

            SelectAccount(window, picker, work.ConnectionId);
            pairsLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain(); window.UpdateLayout();
            Require(Field<string>(window, "_workspacePage") == "Pairs", "The linked-pair count opens Folder Pairs");
            Require(Field<string>(window, "_selectedConnectionId") == work.ConnectionId,
                "Using the link preserves the selected account and its action scope");
            Require(Field<string>(window, "_workspaceAccountId") == work.ConnectionId,
                "The link filters by the exact connection id, never a display name or a row index");
            Require(VisiblePairIds(window).SequenceEqual(["ui20-work"]),
                "Only the exact account's pairs remain visible: " + string.Join(",", VisiblePairIds(window)));
            // Refreshing account status rebuilds row projections; filtering out the original pair
            // may clear the visible selection. Neither is permission to retarget its open draft.
            Require(pairList.SelectedItem is null || (pairList.SelectedItem as PairRow)?.Id == selectedIdBefore,
                "Using the account link does not select a different pair");
            Require(search.Text == "Documents", "Using the link preserves the pair search");
            Require(Field<string>(window, "_editingPairRootId") == editedBefore,
                "Using the link keeps the open pair draft bound to its own pair");
            search.Clear(); Drain();
            Require(VisiblePairIds(window).SequenceEqual(["ui20-work"]),
                "Changing the pair search keeps the exact-account scope");
            SelectAccount(window, picker, google.ConnectionId);
            pairsLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
            Require(VisiblePairIds(window).Length == 0,
                "An account with no pairs returns an empty scoped result instead of every account's pairs");
            Console.WriteLine("PASS Connections account identity and exact-account pair scope");
        }
        finally { window.RequestProductExit(); }
    }

    private static void CheckAccountRefreshAfterClose(string root)
    {
        foreach (var fail in new[] { false, true })
        {
            using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "closed-refresh-" + fail, "instance.lock"));
            using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
            theme.Initialize();
            var closed = new MainWindow(instance.StateAuthority, theme);
            closed.Show(); Drain();
            var pending = new TaskCompletionSource<DropboxConnectionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loads = 0;
            MainWindow.DropboxConnectionStatusLoaderForTest = _ => { loads++; return pending.Task; };
            try
            {
                var before = Field<TextBlock>(closed, "HeaderState").Text;
                var refresh = closed.RefreshConnectionStateForTestAsync(false);
                Require(!refresh.IsCompleted, "Account refresh waits for its actual async status response");
                closed.RequestProductExit();
                instance.Dispose();
                if (fail) pending.SetException(new IOException("Controlled late status failure"));
                else pending.SetResult(new DropboxConnectionStatus(null, false, false, "Disconnected", null, 0));
                Pump(refresh); Drain();
                Require(Field<TextBlock>(closed, "HeaderState").Text == before,
                    "A late account " + (fail ? "failure" : "response") + " cannot update a closed window or read disposed state");
                Pump(closed.RefreshConnectionStateForTestAsync(false));
                Require(loads == 1, "A closed window does not start another account refresh");
            }
            finally
            {
                MainWindow.DropboxConnectionStatusLoaderForTest = null;
                if (closed.IsVisible) closed.RequestProductExit();
            }
        }
    }

    private static void SelectAccount(MainWindow window, ComboBox picker, string connectionId)
    {
        picker.SelectedValue = connectionId; Drain();
        Call(window, "RefreshAccountPickers");
        window.UpdateLayout();
    }

    private static string[] VisiblePairIds(MainWindow window) =>
        System.Windows.Data.CollectionViewSource.GetDefaultView(window.PairRows).Cast<PairRow>()
            .Select(row => row.Id).ToArray();

    // A SettingRow value sits alone in column 1 of that row's two-column grid, so no action control
    // shares the column the verification timestamp occupies.
    private static bool IsAloneSettingRowValue(MainWindow window, string name) =>
        window.FindName(name) is FrameworkElement value && value.Parent is Grid { ColumnDefinitions.Count: 2 } grid &&
        Grid.GetColumn(value) == 1 &&
        grid.Children.OfType<UIElement>().Count(child => !ReferenceEquals(child, value)) == 1;

    // A flat SettingRow prints its own label as the first line of the words column beside the value,
    // so this reads the label the real production row carries instead of an assumed row shape.
    private static string SettingRowLabel(MainWindow window, string name) =>
        window.FindName(name) is FrameworkElement value && value.Parent is Grid { ColumnDefinitions.Count: 2 } grid &&
        grid.Children.OfType<UIElement>().FirstOrDefault(child => !ReferenceEquals(child, value)) is StackPanel words &&
        words.Children.OfType<TextBlock>().FirstOrDefault() is { } label ? label.Text : string.Empty;

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }

    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Pump(Task task) { while (!task.IsCompleted) { Drain(); Thread.Sleep(1); } task.GetAwaiter().GetResult(); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
