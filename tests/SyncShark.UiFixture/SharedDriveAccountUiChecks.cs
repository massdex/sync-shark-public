using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// SUI-1..SUI-5: the Connections shared-drive account UI driven by the real WPF toggle event.
//
// Evidence boundary: only provider HTTP transport and the two credential stores are replaced.
// Every assertion reads persisted Google Drive connection state through a fresh
// GoogleDriveConnectionStore over the same state root, the real host capability projection, or the
// real MainWindow control state the actual CheckBox.Click handler produced. No product action is
// invoked directly and no fake boolean stands in for the stored preference. This fixture does not build,
// install, sign, call a provider or touch real credentials.
internal static partial class Program
{
    private static class SharedDriveAccountUiChecks
    {
    private const BindingFlags UiFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string SharedDrivesToggleName = "Enable shared drives for this Google account";
    private const string FixtureClientId = "fixture-shared-drive-account-ui-client";

    internal static int Run(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var previousStartup = MainWindow.SuppressLoadedStartupForTest;
        var previousClientId = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = FixtureClientId;
        var fixtureRoot = Path.Combine(Path.GetFullPath(root), "shared-drive-account-ui");
        Directory.CreateDirectory(fixtureRoot);
        var network = new CountingNetworkBoundary(new InspectionNetworkBoundary());
        var googleCredentials = new GoogleCredentialBoundary();
        var remoteFactoryCalls = 0;
        App? app = null;
        MainWindow? window = null;
        SyncAuthorityRuntimeHost? ownedHost = null;
        SyncAuthorityRuntimeHost? ownedRestartHost = null;
        try
        {
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixtureRoot, "state", "instance.lock"));
            var authority = instance.StateAuthority;
            using var theme = new ThemeManager(new UiPreferencesStore(authority));
            theme.Initialize();
            using var http = new HttpClient(network);
            var dropboxConnections = new DropboxConnectionStore(authority);
            var database = new SyncAuthorityDatabase(Path.Combine(fixtureRoot, "vNext", "state", "syncshark.db"));
            var dropbox = InspectionAccount("shared-drive-ui", "Shared Drive Holder");
            // Both Google accounts carry one identical label; only the exact permission id,
            // account key and connection id differ, so a label-based route would drive the
            // wrong account and the assertion below would catch it.
            var accountA = GoogleAccount("perm-shared-drive-ui-a");
            var accountB = GoogleAccount("perm-shared-drive-ui-b");
            var accountAId = accountA.ConnectionId;
            var accountBId = accountB.ConnectionId;
            Task.Run(async () =>
            {
                await dropboxConnections.SaveNewAsync(dropbox);
                await new GoogleDriveConnectionStore(authority).SaveAsync(accountA);
                await new GoogleDriveConnectionStore(authority).SaveAsync(accountB);
                var installationId = await new SyncPairStore(authority).GetOrCreateInstallationIdAsync();
                await database.InitializeAsync(installationId, "inspection-profile", "fixture", "{}");
            }).GetAwaiter().GetResult();
            Require(accountAId != accountBId && accountA.PermissionId != accountB.PermissionId &&
                    string.Equals(accountA.Email, accountB.Email, StringComparison.Ordinal) &&
                    string.Equals(accountA.DisplayName, accountB.DisplayName, StringComparison.Ordinal),
                "SUI-2 the two Google accounts share one label and differ only by exact id");

            var host = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), dropboxConnections, new InspectionCredentialBoundary(), database,
                (_, _) =>
                {
                    Interlocked.Increment(ref remoteFactoryCalls);
                    throw new InvalidOperationException("No provider remote store in the shared-drive account UI fixture.");
                },
                googleDriveCredentialStore: googleCredentials);
            ownedHost = host;
            Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
            Require(!host.HasActiveRun, "SUI-1 the fixture host starts with no active run");

            MainWindow.SuppressLoadedStartupForTest = true;
            var surface = NewOffscreenWindow(authority, theme);
            window = surface;
            BindAuthorityHost(surface, host);
            surface.Show();
            Call(surface, "RefreshAccountPickers");
            var toggle = FieldOf<CheckBox?>(surface, "_sharedDrivesToggle")
                ?? throw new InvalidDataException("The Connections page exposes no shared-drive toggle.");
            var row = FieldOf<FrameworkElement?>(surface, "_sharedDrivesRow")
                ?? throw new InvalidDataException("The Connections page exposes no shared-drive row.");
            Require(AutomationProperties.GetName(toggle) == SharedDrivesToggleName,
                "SUI-1 the fixture drives the product's own Connections shared-drive toggle control");

            var picker = FieldOf<ComboBox>(surface, "_accountPicker");
            var listed = picker.Items.OfType<StorageAccountChoice>().ToArray();
            Require(listed.Any(item => item.Id == accountAId) && listed.Any(item => item.Id == accountBId) &&
                    listed.Any(item => item.Id == dropbox.ConnectionId),
                "SUI-2 the Connections account picker lists both Google accounts and the Dropbox account");
            Require(listed.Where(item => item.Id == accountAId || item.Id == accountBId)
                    .Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() == 1,
                "SUI-2 the two Google accounts are distinguishable by exact id, not by label");

            // SUI-2: a Dropbox account holds no shared-drive row at all.
            SelectAccount(surface, dropbox.ConnectionId);
            PumpUntil(surface, () => row.Visibility == Visibility.Collapsed && !toggle.IsEnabled && toggle.IsChecked != true,
                "SUI-2 selecting the Dropbox account hides the shared-drive row and leaves its toggle off");

            // SUI-1: the actual toggle event for the exact selected Google account.
            SelectAccount(surface, accountAId);
            PumpUntil(surface, () => row.Visibility == Visibility.Visible && toggle.IsEnabled && toggle.IsChecked != true,
                "SUI-1 selecting the Google account shows an enabled shared-drive toggle at its stored value");
            Require(!StoredSharedDrives(authority, accountAId) && !StoredSharedDrives(authority, accountBId),
                "SUI-1 both Google accounts start with shared drives off in persisted state");
            var requestsBefore = network.Requests;
            ClickSharedDrivesToggle(surface, true);
            PumpUntil(surface, () => !FieldOf<bool>(surface, "_savingSharedDrives"),
                "SUI-1 the enable click settles");
            Require(StoredSharedDrives(authority, accountAId),
                "SUI-1 the actual toggle click persists shared drives for the exact selected Google account");
            Require(!StoredSharedDrives(authority, accountBId),
                "SUI-1 the click left the other Google account unchanged");
            Require(toggle.IsChecked == true && toggle.IsEnabled,
                "SUI-1 the toggle shows the persisted enabled value after the click");
            Require(host.LoadGoogleDriveConnection(accountAId)!.SharedDrivesEnabled,
                "SUI-1 the host reads the enabled value back from the persisted store");
            Require(SharedDrivesCapability(host, accountAId),
                "SUI-1 the exact account capability reports shared drives after the click");
            Require(!SharedDrivesCapability(host, accountBId),
                "SUI-1 the other account capability is unchanged");
            Require(network.Requests == requestsBefore,
                "SUI-1 the shared-drive setting issued no provider HTTP request");
            Require(googleCredentials.Writes == 0 && googleCredentials.Deletes == 0,
                "SUI-1 the shared-drive setting wrote and deleted no Google credential");
            Require(StatusText(surface).Contains("Shared drives enabled for this Google account", StringComparison.Ordinal),
                "SUI-1 the real handler reported the enabled result for this exact account");
            ClickSharedDrivesToggle(surface, false);
            PumpUntil(surface, () => !FieldOf<bool>(surface, "_savingSharedDrives"),
                "SUI-1 the disable click settles");
            Require(!StoredSharedDrives(authority, accountAId) && !SharedDrivesCapability(host, accountAId),
                "SUI-1 the same real click path persists the disabled value and clears the capability");
            Require(network.Requests == requestsBefore && googleCredentials.Writes == 0,
                "SUI-1 enabling and disabling shared drives had no provider or credential effect");
            Require(StatusText(surface).Contains("Shared drives disabled for this Google account", StringComparison.Ordinal),
                "SUI-1 the real handler reported the disabled result for this exact account");

            // SUI-3: the real host refuses the setting while its run gate is held.
            var runGate = HostFieldOf<SemaphoreSlim>(host, "_runGate");
            Require(runGate.Wait(TimeSpan.FromSeconds(10)), "SUI-3 the fixture holds the real host run gate");
            try
            {
                ClickSharedDrivesToggle(surface, true);
                Settle(surface);
                Require(!StoredSharedDrives(authority, accountAId) && !SharedDrivesCapability(host, accountAId),
                    "SUI-3 a busy host refuses the shared-drive change without any stored change");
                Require(toggle.IsChecked != true && toggle.IsEnabled,
                    "SUI-3 the toggle resets to the persisted value and is usable again after the refusal");
                Require(StatusText(surface).Contains("Shared-drive setting was not changed", StringComparison.Ordinal),
                    "SUI-3 the window reported the refusal instead of a success");
                Require(network.Requests == requestsBefore && googleCredentials.Writes == 0,
                    "SUI-3 the refusal had no provider or credential effect");
            }
            finally { runGate.Release(); }

            // SUI-4: account A's save is in flight on the real per-state store write gate while the
            // user selects account B; only A may change and B's UI must keep B's own value.
            SelectAccount(surface, accountAId);
            PumpUntil(surface, () => toggle.IsChecked != true && toggle.IsEnabled && row.Visibility == Visibility.Visible,
                "SUI-4 account A is selected at its own stored value before the race");
            var storeWriteGate = StoreWriteGate(HostFieldOf<GoogleDriveConnectionStore>(host, "_googleDriveConnections"));
            Require(ReferenceEquals(storeWriteGate, StoreWriteGate(new GoogleDriveConnectionStore(authority))),
                "SUI-4 the held gate is the same per-state Google connection store write gate the host uses");
            Require(storeWriteGate.Wait(TimeSpan.FromSeconds(10)), "SUI-4 the fixture holds the real store write gate");
            try
            {
                ClickSharedDrivesToggle(surface, true);
                PumpUntil(surface, () => FieldOf<bool>(surface, "_savingSharedDrives") && runGate.CurrentCount == 0,
                    "SUI-4 account A's save is in flight inside the real host run gate");
                Settle(surface);
                Require(!StoredSharedDrives(authority, accountAId),
                    "SUI-4 account A's save has not landed while the store write gate is held");
                SelectAccount(surface, accountBId);
                PumpUntil(surface, () => toggle.IsChecked != true,
                    "SUI-4 account B is selected and shows B's own stored value while A's save is still held");
                Require(!StoredSharedDrives(authority, accountAId) && !StoredSharedDrives(authority, accountBId),
                    "SUI-4 neither account changed while account A's save was held");
            }
            finally { storeWriteGate.Release(); }
            PumpUntil(surface, () => !FieldOf<bool>(surface, "_savingSharedDrives") && runGate.CurrentCount == 1,
                "SUI-4 account A's save completed and the host released its run gate");
            Require(StoredSharedDrives(authority, accountAId) && !StoredSharedDrives(authority, accountBId),
                "SUI-4 only account A changed once the released save completed");
            Require(FieldOf<string?>(surface, "_selectedConnectionId") == accountBId && toggle.IsChecked != true && toggle.IsEnabled,
                "SUI-4 account B keeps its own value in the UI instead of inheriting account A's enabled value");
            Require(SharedDrivesCapability(host, accountAId) && !SharedDrivesCapability(host, accountBId),
                "SUI-4 only account A's capability changed");
            Require(network.Requests == requestsBefore && googleCredentials.Writes == 0,
                "SUI-4 the selection race had no provider or credential effect");

            // SUI-2: the visible toggle follows the selected account, and a re-authorization
            // snapshot of one account cannot revert that account's own option.
            SelectAccount(surface, accountAId);
            PumpUntil(surface, () => toggle.IsChecked == true && row.Visibility == Visibility.Visible && toggle.IsEnabled,
                "SUI-2 the visible toggle follows the selected account and shows account A enabled");
            SelectAccount(surface, accountBId);
            PumpUntil(surface, () => toggle.IsChecked != true,
                "SUI-2 selecting account B shows B's own disabled value");
            Task.Run(() => new GoogleDriveConnectionStore(authority)
                .SaveAsync(accountA with { DisplayName = "Shared Drive Holder (reverified)" })).GetAwaiter().GetResult();
            Require(StoredSharedDrives(authority, accountAId) && !StoredSharedDrives(authority, accountBId),
                "SUI-2 a re-authorization snapshot preserves account A's own option and leaves B unchanged");
            Require(FieldOf<string?>(surface, "_selectedConnectionId") == accountBId && toggle.IsChecked != true,
                "SUI-2 the re-authorization of account A did not change the visible account B value");
            Require(remoteFactoryCalls == 0,
                "SUI-1 no provider remote store was created for any account-UI operation");

            // SUI-2: restart readback through a fresh host and a fresh window over the same state.
            surface.RequestProductExit();
            window = null;
            MainWindow.SuppressLoadedStartupForTest = true;
            var restarted = new SyncAuthorityRuntimeHost(authority, http, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority),
                new InspectionCredentialBoundary(), database, googleDriveCredentialStore: googleCredentials);
            ownedRestartHost = restarted;
            Task.Run(() => restarted.LoadPairsAsync()).GetAwaiter().GetResult();
            Require(restarted.LoadGoogleDriveConnection(accountAId)!.SharedDrivesEnabled &&
                    !restarted.LoadGoogleDriveConnection(accountBId)!.SharedDrivesEnabled,
                "SUI-2 a restarted host reads account A enabled and account B disabled from persisted state");
            Require(SharedDrivesCapability(restarted, accountAId) && !SharedDrivesCapability(restarted, accountBId),
                "SUI-2 the restarted capability projection keeps each account's own option");
            var restartSurface = NewOffscreenWindow(authority, theme);
            window = restartSurface;
            BindAuthorityHost(restartSurface, restarted);
            restartSurface.Show();
            Call(restartSurface, "RefreshAccountPickers");
            var restartToggle = FieldOf<CheckBox?>(restartSurface, "_sharedDrivesToggle")
                ?? throw new InvalidDataException("The restarted Connections page exposes no shared-drive toggle.");
            var restartRow = FieldOf<FrameworkElement?>(restartSurface, "_sharedDrivesRow")
                ?? throw new InvalidDataException("The restarted Connections page exposes no shared-drive row.");
            SelectAccount(restartSurface, accountAId);
            PumpUntil(restartSurface,
                () => restartToggle.IsChecked == true && restartRow.Visibility == Visibility.Visible && restartToggle.IsEnabled,
                "SUI-2 the restarted window shows account A's retained shared-drive preference");
            SelectAccount(restartSurface, accountBId);
            PumpUntil(restartSurface, () => restartToggle.IsChecked != true,
                "SUI-2 the restarted window shows account B's own disabled preference");
            Require(network.Requests == requestsBefore,
                "SUI-5 the restart readback and every check above issued no provider request");

            Console.WriteLine("PASS real shared-drive account UI checks; source-only, no provider, credential, build or installed effect");
            return 0;
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = previousStartup;
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = previousClientId;
            try { window?.RequestProductExit(); }
            finally
            {
                ownedHost?.Dispose();
                ownedRestartHost?.Dispose();
                app?.Shutdown();
            }
        }
    }

    private static GoogleDriveConnectionBinding GoogleAccount(string permissionId) => new GoogleDriveConnectionBinding(FixtureClientId,
        GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId,
        "shared-drive-holder@example.invalid", "Shared Drive Holder",
        [GoogleDriveProviderConfiguration.DriveScope]).Normalize();

    private static bool StoredSharedDrives(PilotStateAuthority authority, string connectionId) =>
        new GoogleDriveConnectionStore(authority).Load(connectionId) is { } binding && binding.SharedDrivesEnabled;

    private static bool SharedDrivesCapability(SyncAuthorityRuntimeHost host, string connectionId) =>
        host.LoadProviderConnections()
            .Single(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
            .Capabilities.SharedDrives;

    private static MainWindow NewOffscreenWindow(PilotStateAuthority authority, ThemeManager theme) =>
        new(authority, theme)
        {
            Width = 1200, Height = 780, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false
        };

    private static void SelectAccount(MainWindow window, string connectionId)
    {
        FieldOf<ComboBox>(window, "_accountPicker").SelectedValue = connectionId;
        PumpUntil(window, () => string.Equals(FieldOf<string?>(window, "_selectedConnectionId"), connectionId,
                StringComparison.Ordinal),
            "the Connections account picker routed the exact connection id " + connectionId);
    }

    // The real WPF toggle handler reads IsChecked, so the fixture sets the requested state and then
    // raises the genuine Click routed event instead of calling the product action directly.
    private static void ClickSharedDrivesToggle(MainWindow window, bool requested)
    {
        var toggle = FieldOf<CheckBox>(window, "_sharedDrivesToggle");
        toggle.IsChecked = requested;
        toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
    }

    private static void PumpUntil(MainWindow window, Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15))
            window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        if (!condition()) throw new TimeoutException("Shared-drive account UI fixture deadline reached: " + description);
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void Settle(MainWindow window)
    {
        for (var round = 0; round < 8; round++)
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static string StatusText(MainWindow window) => FieldOf<TextBox?>(window, "ConsoleText")?.Text ?? string.Empty;

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }

    private static object? Call(MainWindow window, string name, params object?[] values) =>
        (typeof(MainWindow).GetMethod(name, UiFlags) ?? throw new MissingMethodException(nameof(MainWindow), name))
            .Invoke(window, values);

    private static T FieldOf<T>(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, UiFlags)
            ?? throw new MissingFieldException(nameof(MainWindow), name);
        return (T)field.GetValue(window)!;
    }

    private static T HostFieldOf<T>(SyncAuthorityRuntimeHost host, string name)
    {
        var field = typeof(SyncAuthorityRuntimeHost).GetField(name, UiFlags)
            ?? throw new MissingFieldException(nameof(SyncAuthorityRuntimeHost), name);
        return (T)field.GetValue(host)!;
    }

    private static SemaphoreSlim StoreWriteGate(GoogleDriveConnectionStore store)
    {
        var field = typeof(GoogleDriveConnectionStore).GetField("_writeGate", UiFlags)
            ?? throw new MissingFieldException(nameof(GoogleDriveConnectionStore), "_writeGate");
        return (SemaphoreSlim)field.GetValue(store)!;
    }

    private sealed class CountingNetworkBoundary(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(inner, disposeHandler: true);
        internal int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return _inner.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class GoogleCredentialBoundary : IGoogleDriveRefreshTokenStore
    {
        internal int Reads;
        internal int Writes;
        internal int Deletes;

        public Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult<string?>("synthetic-google-refresh-" + connectionId);
        }

        public Task WriteAsync(string connectionId, string refreshToken, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            throw new InvalidOperationException(
                "The shared-drive account UI fixture must not write a Google credential.");
        }

        public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Deletes);
            throw new InvalidOperationException(
                "The shared-drive account UI fixture must not delete a Google credential.");
        }
    }
}
    }
