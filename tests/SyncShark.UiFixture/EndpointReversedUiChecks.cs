using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// EUI-P1..EUI-P3: the reversed endpoint UI proof - Google Drive on the left, this computer on the right.
//
// This is a real WPF proof of the production endpoint authoring surface. The actual kind, account and
// direction controls drive the one production editor session; both folder choices go through the
// production per-side pickers; Save and Cancel are the window's own routed Click handlers executed on
// the window dispatcher. The fixture never calls the endpoint binding host itself and never builds an
// expected pair of its own: every assertion reads what the product persisted through its real Save.
internal static partial class Program
{
    /// <summary>
    /// The instance locks this group holds for its whole duration. A direction's state authority stays
    /// alive until the group is finished, because the routed Save keeps working on the dispatcher after
    /// it has displayed its pair row: tearing the authority down while that completion pipeline is still
    /// running makes the pipeline's own continuation observe a disposed authority.
    /// </summary>
    private static readonly List<PilotInstanceLock> ReversedUiInstanceLocks = [];

    private static int RunEndpointReversedUiChecks(string root)
    {
        Directory.CreateDirectory(root);
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        var priorClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        App? app = null;
        try
        {
            // One fixed registered-client override for the whole group, restored in the finally below.
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "fixture-client";
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            MainWindow.SuppressLoadedStartupForTest = true;
            foreach (var direction in Enum.GetValues<SyncMode>())
                RunReversedEndpointDirection(root, direction);
            RunEndpointBrowserLabelCase(Path.Combine(root, "browser-label"));
            Console.WriteLine("PASS reversed Google-left/local-right endpoint UI in all three directions through the " +
                "real controls, the production pickers and the actual routed Save and Cancel clicks, including " +
                "the fresh-host readback of each persisted pair.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = priorClient;
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            // Let every routed Save's completion pipeline finish before the state authorities are released.
            DrainDispatcher();
            app?.Shutdown();
            foreach (var held in ReversedUiInstanceLocks) held.Dispose();
            ReversedUiInstanceLocks.Clear();
        }
    }

    /// <summary>One isolated state root, local folder and authority per direction.</summary>
    private static void RunReversedEndpointDirection(string root, SyncMode direction)
    {
        var fixture = Path.Combine(root, direction.ToString());
        var localPath = Directory.CreateDirectory(Path.Combine(fixture, "local")).FullName;
        File.WriteAllText(Path.Combine(localPath, "local.txt"), "local source");
        var savedPair = RunReversedEndpointEditor(fixture, localPath, direction);
        RunReversedEndpointRestartReadback(fixture, localPath, direction, savedPair);
    }

    /// <summary>
    /// The synthetic Google Drive one reversed direction binds to. The account-bound recovery container
    /// is present because a real Save verifies it rather than inventing it.
    /// </summary>
    private static (string LeftFolderId, string ChangedFolderId) CreateReversedDrive(SyntheticDriveServer server)
    {
        server.AddFolder(server.MyDriveRootIdForTest ?? throw new InvalidDataException(
            "The full-host reversed fixture requires the provider's opaque My Drive identity."),
            "My Drive", null);
        server.AddFolder("apps", "Apps", "root");
        server.AddFolder("product", "Sync Shark", "apps");
        server.AddFolder("recovery", "Recovery", "product");
        var leftFolderId = server.AddFolder("reversed-folder", "Reversed", "root").Id;
        var changedFolderId = server.AddFolder("reversed-folder-two", "Reversed Two", "root").Id;
        server.AddFile("reversed-cloud-file", "cloud.txt", leftFolderId, Encoding.UTF8.GetBytes("cloud source"));
        return (leftFolderId, changedFolderId);
    }

    /// <summary>
    /// Drive the production editor to one persisted reversed pair, then reopen it and cancel a changed
    /// draft. The window and its fixture authority host own this method's lifetime; the state authority
    /// is held by the group so the routed Save's completion pipeline can still finish after this returns.
    /// </summary>
    private static SyncPair RunReversedEndpointEditor(string fixture, string localPath, SyncMode direction)
    {
        var pairName = "Reversed UI pair " + direction;
        SyncPair? persisted = null;
        MainWindow? window = null;
        try
        {
            var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            ReversedUiInstanceLocks.Add(instance);
            // The real retained Google binding, written through the production connection store.
            var binding = new GoogleDriveConnectionBinding("fixture-client",
                GoogleDriveConnectionBinding.CreateAccountKey("perm-account-one"), "perm-account-one",
                "reversed@example.invalid", "Reversed Fixture",
                [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
            Task.Run(() => new GoogleDriveConnectionStore(instance.StateAuthority).SaveAsync(binding))
                .GetAwaiter().GetResult();
            using var server = new SyntheticDriveServer { MyDriveRootIdForTest = "reversed-my-drive-root" };
            var (leftFolderId, changedFolderId) = CreateReversedDrive(server);
            using var http = new HttpClient(server);
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
                new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db")),
                googleDriveCredentialStore: new EndpointCloudCredentials(),
                googleDriveTokenEndpoint: new EndpointCloudTokens());
            Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
            using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
            theme.Initialize();
            MainWindow.SuppressLoadedStartupForTest = true;
            window = new MainWindow(instance.StateAuthority, theme)
            { Width = 1240, Height = 790, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            BindAuthorityHost(window, host);
            window.Show();

            // EUI-P2: the real editor controls, not helper constants, choose both endpoints and direction.
            EndpointUiCall(window, "BeginNewPairEditor");
            var session = EndpointUiField<SyncPairEditorSession>(window, "_endpointSession");
            EndpointUiRequire(session.KindOf(SyncPairSide.Left) == SyncEndpointKind.Local &&
                session.KindOf(SyncPairSide.Right) == SyncEndpointKind.Dropbox,
                "Reversed " + direction + ": a new pair opens the production default order with both sides independent");
            // The name first, so while the left endpoint is missing nothing else keeps Save disabled.
            EndpointUiField<TextBox>(window, "PairNameBox").Text = pairName;
            EndpointUiField<ComboBox>(window, "LeftEndpointKindBox").SelectedValue = SyncEndpointKind.GoogleDrive;
            EndpointUiRequire(session.KindOf(SyncPairSide.Left) == SyncEndpointKind.GoogleDrive &&
                session.KindOf(SyncPairSide.Right) == SyncEndpointKind.Dropbox,
                "Reversed " + direction + ": the real left kind control switches only the left side to Google Drive");
            EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.Local;
            EndpointUiRequire(session.KindOf(SyncPairSide.Left) == SyncEndpointKind.GoogleDrive &&
                session.KindOf(SyncPairSide.Right) == SyncEndpointKind.Local,
                "Reversed " + direction + ": the real right kind control switches only the right side to this computer");
            EndpointUiRequire(EndpointUiField<ComboBox>(window, "LeftEndpointAccountBox").Visibility == Visibility.Visible &&
                EndpointUiField<ComboBox>(window, "RightEndpointAccountBox").Visibility == Visibility.Collapsed,
                "Reversed " + direction + ": only the cloud side is offered an account");
            EndpointUiField<ComboBox>(window, "LeftEndpointAccountBox").SelectedValue = binding.ConnectionId;
            EndpointUiRequire(string.Equals(session.Draft(SyncPairSide.Left).ConnectionId, binding.ConnectionId,
                    StringComparison.Ordinal),
                "Reversed " + direction + ": the real left account control binds the retained Google account into the draft");

            // EUI-P2: one missing endpoint keeps the actual Save control disabled.
            EndpointUiCall(window, "UpdatePairSaveAvailability");
            EndpointUiRequire(window.EndpointDraft() is { CanSave: false } &&
                !EndpointUiField<Button>(window, "SavePairButton").IsEnabled,
                "Reversed " + direction + ": the missing Google folder keeps the actual Save control disabled");

            // EUI-P2: both folder identities arrive through the production per-side pickers.
            var leftPicker = session.PickerFor(SyncPairSide.Left);
            EndpointUiRequire(leftPicker.ApplyCloudFolder(leftPicker.BeginPick(),
                    new DropboxFolderEntry("Reversed", "/Reversed", leftFolderId)).Applied &&
                string.Equals(session.Draft(SyncPairSide.Left).FolderId, leftFolderId, StringComparison.Ordinal),
                "Reversed " + direction + ": the production left picker applies the exact Google folder the synthetic drive issues");
            var rightPicker = session.PickerFor(SyncPairSide.Right);
            EndpointUiRequire(rightPicker.ApplyLocalFolder(rightPicker.BeginPick(), localPath).Applied,
                "Reversed " + direction + ": the production right picker binds this computer's exact folder identity");
            EndpointUiField<ComboBox>(window, "PairDirectionBox").SelectedValue = direction;
            EndpointUiCall(window, "UpdatePairSaveAvailability");
            EndpointUiRequire(session.Direction == direction,
                "Reversed " + direction + ": the real direction control updates the same draft Save reads");
            EndpointUiRequire(window.EndpointDraft() is { CanSave: true } &&
                EndpointUiField<Button>(window, "SavePairButton").IsEnabled,
                "Reversed " + direction + ": a complete reversed draft enables the actual Save control");

            // EUI-P2: the real routed Save click, dispatched like a user input event.
            SaveEndpointPolicyDraft(window, pairName);
            var savedPair = Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult()
                .Pairs.Single(item => string.Equals(item.Name, pairName, StringComparison.Ordinal));

            // EUI-P1: the persisted ordered endpoint identity and the logical direction.
            EndpointUiRequire(savedPair.LeftEndpoint is { IsLocal: false, Kind: SyncEndpointKind.GoogleDrive } &&
                savedPair.RightEndpoint is { IsLocal: true } && SyncEndpointEngineLayout.IsReversed(savedPair) &&
                string.Equals(savedPair.LeftEndpoint!.ConnectionId, binding.ConnectionId, StringComparison.Ordinal) &&
                string.Equals(savedPair.LeftEndpoint!.FolderId, leftFolderId, StringComparison.Ordinal) &&
                string.Equals(savedPair.LeftEndpoint!.FolderPath, "/Reversed", StringComparison.Ordinal) &&
                string.Equals(savedPair.RightEndpoint!.LocalPath, localPath, StringComparison.OrdinalIgnoreCase) &&
                savedPair.Direction == direction,
                "Reversed " + direction + ": Save retains the ordered Google/left and this-computer/right endpoints " +
                "and the logical direction");
            uint localVolume;
            ulong localFileId;
            using (var lease = PilotDirectoryLease.Open(localPath))
            {
                localVolume = lease.Identity.VolumeSerialNumber;
                localFileId = lease.Identity.FileId;
            }
            EndpointUiRequire(savedPair.RightEndpoint!.LocalVolumeSerialNumber == localVolume &&
                savedPair.RightEndpoint!.LocalRootFileId == localFileId,
                "Reversed " + direction + ": the persisted local endpoint carries this NTFS root's exact volume and file identity");
            // EUI-P1: Save copies no payload in either direction before the first review.
            EndpointUiRequire(savedPair.InitialReviewApproved == false &&
                !File.Exists(Path.Combine(localPath, "cloud.txt")) &&
                !server.Names(leftFolderId).Contains("local.txt"),
                "Reversed " + direction + ": Save copies no payload either way and the reversed pair still " +
                "requires its first-sync review");

            // EUI-P2: reopen the saved pair through its own pair row and read the same two endpoints back.
            var row = window.PairRows.Single(item => string.Equals(item.Id, savedPair.Id, StringComparison.Ordinal));
            window.PairList.ScrollIntoView(row);
            window.PairList.UpdateLayout();
            window.PairList.SelectedItem = row;
            PumpUntil(window, () => EndpointUiNullableField<SyncPairEditorSession>(window, "_endpointSession") is { } opened &&
                    string.Equals(opened.EditingPairId, savedPair.Id, StringComparison.Ordinal),
                "Reversed " + direction + ": selecting the saved pair row reopens its own editor draft");
            var reopened = EndpointUiField<SyncPairEditorSession>(window, "_endpointSession");
            EndpointUiRequire(reopened.KindOf(SyncPairSide.Left) == SyncEndpointKind.GoogleDrive &&
                reopened.KindOf(SyncPairSide.Right) == SyncEndpointKind.Local && reopened.Direction == direction &&
                string.Equals(reopened.Draft(SyncPairSide.Left).FolderId, leftFolderId, StringComparison.Ordinal) &&
                string.Equals(reopened.Draft(SyncPairSide.Left).ConnectionId, binding.ConnectionId, StringComparison.Ordinal) &&
                string.Equals(reopened.Draft(SyncPairSide.Right).LocalPath, localPath, StringComparison.OrdinalIgnoreCase) &&
                EndpointUiField<ComboBox>(window, "LeftEndpointKindBox").SelectedValue is SyncEndpointKind.GoogleDrive &&
                EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue is SyncEndpointKind.Local &&
                EndpointUiField<ComboBox>(window, "PairDirectionBox").SelectedValue is SyncMode shownDirection &&
                shownDirection == direction,
                "Reversed " + direction + ": reopening the saved pair restores both endpoints and the direction in the real controls");

            // EUI-P2: a changed draft is discarded by the actual Cancel click and persists nothing.
            var changedDirection = direction == SyncMode.TwoWay ? SyncMode.UploadOnly : SyncMode.TwoWay;
            var reopenedPicker = reopened.PickerFor(SyncPairSide.Left);
            EndpointUiRequire(reopenedPicker.ApplyCloudFolder(reopenedPicker.BeginPick(),
                    new DropboxFolderEntry("Reversed Two", "/Reversed Two", changedFolderId)).Applied,
                "Reversed " + direction + ": the reopened draft accepts a different Google folder as a changed draft");
            EndpointUiField<ComboBox>(window, "PairDirectionBox").SelectedValue = changedDirection;
            EndpointUiRequire(window.EndpointDraft() is { Left.FolderId: var draftedFolder } &&
                string.Equals(draftedFolder, changedFolderId, StringComparison.Ordinal) &&
                reopened.Direction == changedDirection,
                "Reversed " + direction + ": the changed draft really differs from the saved pair before Cancel");
            _ = window.Dispatcher.InvokeAsync(() => EndpointUiField<Button>(window, "CancelPairButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            PumpUntil(window, () => EndpointUiNullableField<SyncPairEditorSession>(window, "_endpointSession") is null,
                "Reversed " + direction + ": the actual routed Cancel click ends the changed draft");
            var afterCancel = Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult()
                .Pairs.Single(item => string.Equals(item.Id, savedPair.Id, StringComparison.Ordinal));
            EndpointUiRequire(UnchangedReversedPair(afterCancel, savedPair),
                "Reversed " + direction + ": cancelling the changed draft leaves the persisted pair exactly as Save retained it");
            persisted = savedPair;
        }
        finally { window?.RequestProductExit(); }
        return persisted ?? throw new InvalidOperationException(
            "The reversed endpoint editor produced no persisted pair.");
    }

    /// <summary>
    /// A fresh authority host over the same isolated state root, so the reversed pair's persistence is
    /// read back by a second process-wide runtime rather than by the window that wrote it.
    /// </summary>
    private static void RunReversedEndpointRestartReadback(string fixture, string localPath, SyncMode direction,
        SyncPair savedPair)
    {
        // A second runtime over the same isolated state root. It takes its own instance lock file inside
        // that same state directory, so the editor's authority can stay alive for the whole group while
        // this readback still proves that a fresh host reads the persisted pair from the same state root.
        using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "readback-instance.lock"));
        using var server = new SyntheticDriveServer { MyDriveRootIdForTest = "reversed-my-drive-root" };
        _ = CreateReversedDrive(server);
        using var http = new HttpClient(server);
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
            new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db")),
            googleDriveCredentialStore: new EndpointCloudCredentials(),
            googleDriveTokenEndpoint: new EndpointCloudTokens());
        var restarted = Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult()
            .Pairs.Single(item => string.Equals(item.Id, savedPair.Id, StringComparison.Ordinal));
        EndpointUiRequire(restarted.LeftEndpoint == savedPair.LeftEndpoint &&
            restarted.RightEndpoint == savedPair.RightEndpoint && restarted.Direction == direction &&
            restarted.InitialReviewApproved == false && SyncEndpointEngineLayout.IsReversed(restarted) &&
            !File.Exists(Path.Combine(localPath, "cloud.txt")),
            "Reversed " + direction + ": a fresh authority host over the same isolated state root reopens the " +
            "reversed pair with both endpoints, its direction and its review state unchanged");
    }

    /// <summary>Every persisted member the cancelled draft could have changed, compared exactly.</summary>
    private static bool UnchangedReversedPair(SyncPair after, SyncPair before) =>
        string.Equals(after.Name, before.Name, StringComparison.Ordinal) &&
        after.LeftEndpoint == before.LeftEndpoint && after.RightEndpoint == before.RightEndpoint &&
        after.Direction == before.Direction && after.InitialReviewApproved == before.InitialReviewApproved &&
        string.Equals(after.LocalRoot, before.LocalRoot, StringComparison.OrdinalIgnoreCase) &&
        after.LocalVolumeSerialNumber == before.LocalVolumeSerialNumber &&
        after.LocalRootFileId == before.LocalRootFileId &&
        string.Equals(after.DropboxRoot, before.DropboxRoot, StringComparison.Ordinal) &&
        string.Equals(after.DropboxFolderId, before.DropboxFolderId, StringComparison.Ordinal) &&
        string.Equals(after.Provider, before.Provider, StringComparison.Ordinal) &&
        string.Equals(after.ConnectionId, before.ConnectionId, StringComparison.Ordinal);

    /// <summary>
    /// Pumps the dispatcher to idle, so work a routed Save has already started on the dispatcher runs to
    /// completion before this fixture releases the state authority that work still depends on.
    /// </summary>
    private static void DrainDispatcher() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    /// <summary>
    /// Reads a field whose value is legitimately null. The shared EndpointUiField helper refuses a null
    /// value because it cannot tell it from a missing field; this reader checks the field's existence on
    /// its own and returns a genuine null as null, so the assertion that the routed Cancel click ended
    /// the changed draft can actually observe that null. The shared helper's non-null semantics and the
    /// persisted-unchanged assertion are unchanged.
    /// </summary>
    private static T? EndpointUiNullableField<T>(MainWindow window, string name) where T : class
    {
        var field = typeof(MainWindow).GetField(name, EndpointUiFlags) ??
                    throw new MissingFieldException(typeof(MainWindow).FullName, name);
        return field.GetValue(window) as T;
    }

    /// <summary>
    /// EUI-S2/EUI-S3/EUI-S4: the endpoint Save policy proofs the three-direction reversed group does not
    /// cover. Both endpoints and the direction are authored through the real controls and the production
    /// pickers, and the binding, planning and Save decisions under proof are the product's own. The retained
    /// tree plan and any explicitly selected Recovery folder enter exactly as the folder-tree selection and
    /// the Recovery browse enter them.
    /// </summary>
    private static int RunEndpointPolicyUiChecks(string root)
    {
        Directory.CreateDirectory(root);
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        var priorClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        App? app = null;
        try
        {
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "fixture-client";
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            MainWindow.SuppressLoadedStartupForTest = true;
            RunEndpointPolicyCases(root);
            Console.WriteLine("PASS endpoint Save policy: the direct whole-root default, a still-matching tree " +
                "plan's own rules and default, a stale plan visibly refused with the persisted set unchanged, an " +
                "unchanged retained editor that is not dirty, and the explicitly selected Recovery held only for " +
                "the matching selected cloud account.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            MainWindow.PairFolderBrowserFactoryForTest = null;
            MainWindow.RecoveryFolderDialogForTest = null;
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = priorClient;
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            DrainDispatcher();
            app?.Shutdown();
            foreach (var held in ReversedUiInstanceLocks) held.Dispose();
            ReversedUiInstanceLocks.Clear();
        }
    }
    /// <summary>
    /// The endpoint Save policy proofs the three-direction reversed group does not cover. Each case runs in
    /// its own isolated state root with its own window, so an earlier case's persisted pairs can never make
    /// a later one be refused by an unrelated ancestry, duplicate or overlap guard.
    /// </summary>
    private static void RunEndpointPolicyCases(string root)
    {
        RunEndpointPolicyWholeRootCase(Path.Combine(root, "whole"));
        RunEndpointPolicyMatchingPlanCase(Path.Combine(root, "matching-plan"));
        RunEndpointPolicyStalePlanCase(Path.Combine(root, "stale-plan"));
        RunEndpointPolicyRecoveryCase(Path.Combine(root, "recovery"));
        RunEndpointPolicyOtherAccountRecoveryCase(Path.Combine(root, "other-account"));
    }

    /// <summary>One isolated policy fixture: a state root, an authority, a synthetic cloud and a window.</summary>
    private sealed class EndpointPolicyFixture : IDisposable
    {
        internal EndpointPolicyFixture(MainWindow window, SyncAuthorityRuntimeHost host,
            SyntheticDriveServer server, HttpClient http, ThemeManager theme, GoogleDriveConnectionBinding binding)
        {
            Window = window; Host = host; Server = server; Http = http; Theme = theme; Binding = binding;
        }

        internal MainWindow Window { get; }
        internal SyncAuthorityRuntimeHost Host { get; }
        internal SyntheticDriveServer Server { get; }
        internal GoogleDriveConnectionBinding Binding { get; }
        private HttpClient Http { get; }
        private ThemeManager Theme { get; }

        public void Dispose()
        {
            Window.RequestProductExit();
            Theme.Dispose();
            Host.Dispose();
            Http.Dispose();
            Server.Dispose();
        }
    }

    private static EndpointPolicyFixture OpenEndpointPolicyFixture(string fixture, bool withForeignAccount,
        Action<SyntheticDriveServer> populateDrive, out string? foreignAccountId)
    {
        Directory.CreateDirectory(fixture);
        var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
        ReversedUiInstanceLocks.Add(instance);
        // The retained Google binding the production connection store verifies; this fixture's synthetic
        // identity endpoint reports the same account, exactly as the real provider would.
        var binding = new GoogleDriveConnectionBinding("fixture-client",
            GoogleDriveConnectionBinding.CreateAccountKey("perm-account-one"), "perm-account-one",
            "policy@example.invalid", "Policy Fixture", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Task.Run(() => new GoogleDriveConnectionStore(instance.StateAuthority).SaveAsync(binding))
            .GetAwaiter().GetResult();
        foreignAccountId = null;
        if (withForeignAccount)
        {
            // A registered account of the other provider that this case's own selected account never names.
            var foreign = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:policy-other", "root-policy-other", "home-policy-other",
                DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                DisplayName: "Policy Other Account", Email: "policy-other@example.invalid", AccountType: "basic")
                .Normalize();
            Task.Run(() => new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(foreign))
                .GetAwaiter().GetResult();
            foreignAccountId = foreign.ConnectionId;
        }
        var server = new SyntheticDriveServer { MyDriveRootIdForTest = "policy-my-drive-root" };
        server.AddFolder(server.MyDriveRootIdForTest, "My Drive", null);
        server.AddFolder("apps", "Apps", "root");
        server.AddFolder("product", "Sync Shark", "apps");
        server.AddFolder("recovery", "Recovery", "product");
        populateDrive(server);
        var http = new HttpClient(server);
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
            new SyncAuthorityDatabase(Path.Combine(fixture, "authority.db")),
            googleDriveCredentialStore: new EndpointCloudCredentials(),
            googleDriveTokenEndpoint: new EndpointCloudTokens());
        Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
        var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.RecoveryFolderDialogForTest = null;
        var window = new MainWindow(instance.StateAuthority, theme)
        { Width = 1240, Height = 790, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        BindAuthorityHost(window, host);
        // The production folder browser an in-memory fixture supplies, so Recovery selection never needs a
        // live provider request and the case still selects through the real Recovery browse command.
        MainWindow.PairFolderBrowserFactoryForTest = _ =>
            Task.FromResult<IDropboxFolderBrowserService>(new EmbeddedDropboxPagingFixture());
        window.Show();
        // The production account surface owns the pair account picker a new draft is seeded from.
        EndpointUiCall(window, "BuildAccountPicker");
        DrainDispatcher();
        EndpointUiRequire(EndpointUiNullableField<ComboBox>(window, "_pairAccountPicker") is not null,
            "Endpoint policy: the production pair account picker exists before any draft is authored");
        return new EndpointPolicyFixture(window, host, server, http, theme, binding);
    }

    /// <summary>One Google-left/local-right draft authored through the real controls and the pickers.</summary>
    private static void AuthorEndpointPolicyDraft(MainWindow window, GoogleDriveConnectionBinding binding,
        string pairName, string localPath, string cloudFolderId, string cloudFolderPath)
    {
        EndpointUiCall(window, "BeginNewPairEditor");
        var session = EndpointUiField<SyncPairEditorSession>(window, "_endpointSession");
        EndpointUiField<TextBox>(window, "PairNameBox").Text = pairName;
        EndpointUiField<ComboBox>(window, "LeftEndpointKindBox").SelectedValue = SyncEndpointKind.GoogleDrive;
        EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.Local;
        EndpointUiField<ComboBox>(window, "LeftEndpointAccountBox").SelectedValue = binding.ConnectionId;
        var leftPicker = session.PickerFor(SyncPairSide.Left);
        if (!leftPicker.ApplyCloudFolder(leftPicker.BeginPick(),
                new DropboxFolderEntry("Cloud", cloudFolderPath, cloudFolderId)).Applied)
            throw new InvalidDataException("The production left picker refused " + cloudFolderPath + ".");
        var rightPicker = session.PickerFor(SyncPairSide.Right);
        if (!rightPicker.ApplyLocalFolder(rightPicker.BeginPick(), localPath).Applied)
            throw new InvalidDataException("The production right picker refused " + localPath + ".");
        EndpointUiField<ComboBox>(window, "PairDirectionBox").SelectedValue = SyncMode.TwoWay;
        EndpointUiCall(window, "UpdatePairSaveAvailability");
        var authored = window.EndpointDraft();
        EndpointUiRequire(session.Direction == SyncMode.TwoWay && authored is { CanSave: true },
            "Endpoint policy: the authored draft " + pairName + " is complete and saveable" +
            (authored is null ? " (no draft)" : " (canSave=" + authored.CanSave + "; validation=" +
                authored.Validation.Describe() + "; unsupported=" +
                (authored.UnsupportedBindingReason ?? "none") + ")"));
    }

    /// <summary>One local/local draft authored the same way, for the pair a case must already hold.</summary>
    private static void AuthorEndpointPolicyLocalDraft(MainWindow window, string pairName, string leftLocal,
        string rightLocal)
    {
        EndpointUiCall(window, "BeginNewPairEditor");
        var session = EndpointUiField<SyncPairEditorSession>(window, "_endpointSession");
        EndpointUiField<TextBox>(window, "PairNameBox").Text = pairName;
        EndpointUiField<ComboBox>(window, "RightEndpointKindBox").SelectedValue = SyncEndpointKind.Local;
        foreach (var side in new[] { (Side: SyncPairSide.Left, Path: leftLocal),
                                     (Side: SyncPairSide.Right, Path: rightLocal) })
        {
            var picker = session.PickerFor(side.Side);
            if (!picker.ApplyLocalFolder(picker.BeginPick(), side.Path).Applied)
                throw new InvalidDataException("The production picker refused " + side.Path + ".");
        }
        EndpointUiField<ComboBox>(window, "PairDirectionBox").SelectedValue = SyncMode.TwoWay;
        EndpointUiCall(window, "UpdatePairSaveAvailability");
    }

    /// <summary>Makes the draft's own selected cloud account the exact production picker value.</summary>
    private static void SelectEndpointPolicyAccount(MainWindow window, string connectionId)
    {
        var picker = EndpointUiNullableField<ComboBox>(window, "_pairAccountPicker") ??
            throw new MissingFieldException(typeof(MainWindow).FullName, "_pairAccountPicker");
        picker.SelectedValue = connectionId;
        PumpUntil(window, () => string.Equals(
                EndpointUiNullableField<string>(window, "_pairDraftConnectionId"), connectionId,
                StringComparison.Ordinal),
            "Endpoint policy: the draft's own selected account becomes " + connectionId);
        EndpointUiCall(window, "UpdatePairSaveAvailability");
    }

    private static void RaiseEndpointPolicyButton(MainWindow window, string name) =>
        window.Dispatcher.Invoke(() => EndpointUiField<Button>(window, name)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

    private static void SaveEndpointPolicyDraft(MainWindow window, string pairName)
    {
        var status = EndpointUiField<TextBlock>(window, "PairEditorStatusText");
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty, typeof(TextBlock));
        var traceCount = 0;
        void TraceSaveState(object? sender, EventArgs args)
        {
            if (++traceCount > 64) return;
            var session = EndpointUiNullableField<SyncPairEditorSession>(window, "_endpointSession");
            var draft = window.EndpointDraft();
            Console.Error.WriteLine("SAVE_TRACE " + System.Text.Json.JsonSerializer.Serialize(new
            {
                Pair = pairName, Transition = traceCount, Status = status.Text,
                SessionActive = session?.IsActive, SessionIdentity = session is null ? 0 :
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(session),
                HasEndpoints = draft?.Endpoints is not null, CanSave = draft?.CanSave,
                Validation = draft?.Validation.Describe(),
                HasPlan = EndpointUiNullableField<PairDraftBindingPlan>(window, "_globalDraftPlan") is not null,
                SaveEnabled = EndpointUiField<Button>(window, "SavePairButton").IsEnabled,
                SaveInFlight = EndpointSaveInFlight(window)
            }));
        }
        descriptor.AddValueChanged(status, TraceSaveState);
        TraceSaveState(null, EventArgs.Empty);
        _ = window.Dispatcher.InvokeAsync(() => EndpointUiField<Button>(window, "SavePairButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        try
        {
            // Publishing the row is not the end of the routed handler: monitor setup still runs, and
            // the one shared feedback scope intentionally ignores repeated clicks until it completes.
            // Observe that actual lifetime before the next case authors or saves another draft.
            PumpUntil(window, () => window.PairRows.Any(row =>
                    string.Equals(row.Name, pairName, StringComparison.Ordinal)) && !EndpointSaveInFlight(window),
                "Endpoint policy: the routed Save click saves " + pairName);
        }
        catch (TimeoutException)
        {
            TraceSaveState(null, EventArgs.Empty);
            throw new InvalidDataException("Endpoint policy: the routed Save click did not save " + pairName +
                "; the editor reported: " + EndpointUiField<TextBlock>(window, "PairEditorStatusText").Text);
        }
        finally { descriptor.RemoveValueChanged(status, TraceSaveState); }
    }

    private static bool EndpointSaveInFlight(MainWindow window)
    {
        var feedback = typeof(MainWindow).Assembly.GetType("Massdex.SyncShark.ActionFeedback") ??
            throw new TypeLoadException("The actual action feedback lifetime is unavailable.");
        var active = feedback.GetField("Active", EndpointUiFlags | System.Reflection.BindingFlags.Static)?.GetValue(null) ??
            throw new MissingFieldException(feedback.FullName, "Active");
        var tryGet = active.GetType().GetMethod("TryGetValue") ??
            throw new MissingMethodException(active.GetType().FullName, "TryGetValue");
        return (bool)tryGet.Invoke(active, [EndpointUiField<Button>(window, "SavePairButton"), null])!;
    }

    private static SyncPair LoadEndpointPolicyPair(SyncAuthorityRuntimeHost host, string pairName) =>
        Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult().Pairs.Single(item =>
            string.Equals(item.Name, pairName, StringComparison.Ordinal));

    private static void SetEndpointUiField(MainWindow window, string name, object? value)
    {
        var field = typeof(MainWindow).GetField(name, EndpointUiFlags) ??
                    throw new MissingFieldException(typeof(MainWindow).FullName, name);
        field.SetValue(window, value);
    }

    private static string PolicyLocalRoot(string fixture, string name)
    {
        var local = Directory.CreateDirectory(Path.Combine(fixture, name)).FullName;
        File.WriteAllText(Path.Combine(local, "local.txt"), "local source");
        return local;
    }

    /// <summary>EUI-S2: a direct whole-root selection is saved as the whole parent folders.</summary>
    private static void RunEndpointPolicyWholeRootCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: false,
            server => server.AddFolder("policy-whole", "Whole", "root"), out _);
        var local = PolicyLocalRoot(fixture, "local");
        AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Policy whole root", local,
            harness.Server.IdByName("root", "Whole"), "/Whole");
        SaveEndpointPolicyDraft(harness.Window, "Policy whole root");
        var saved = LoadEndpointPolicyPair(harness.Host, "Policy whole root");
        EndpointUiRequire(saved.LeftEndpoint is { IsLocal: false, Kind: SyncEndpointKind.GoogleDrive } &&
            saved.SelectionDefaultIncluded && (saved.SelectionRules is null || saved.SelectionRules.Count == 0),
            "Endpoint policy: a direct whole-root selection persists remaining included with no rules");
    }

    /// <summary>
    /// EUI-S2/EUI-S4: a tree plan whose physical roots still match the explicit endpoints supplies its own
    /// rules and default, and reopening that retained pair is not spuriously dirty.
    /// </summary>
    private static void RunEndpointPolicyMatchingPlanCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: false,
            server => server.AddFolder("policy-plan", "Plan", "root"), out _);
        var local = PolicyLocalRoot(fixture, "local");
        var folderId = harness.Server.IdByName("root", "Plan");
        AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Policy matching plan", local, folderId, "/Plan");
        SetEndpointUiField(harness.Window, "_globalDraftPlan", new PairDraftBindingPlan(local,
            new DropboxFolderEntry("Plan", "/Plan", folderId), false,
            [new SyncSelectionRule("Sub", true, "cloud:policy-plan-sub")]));
        SaveEndpointPolicyDraft(harness.Window, "Policy matching plan");
        var planned = LoadEndpointPolicyPair(harness.Host, "Policy matching plan");
        EndpointUiRequire(!planned.SelectionDefaultIncluded &&
            planned.SelectionRules is [{ FolderPath: "Sub", Included: true }],
            "Endpoint policy: a still-matching tree plan keeps its own rules and default");

        var row = harness.Window.PairRows.Single(item =>
            string.Equals(item.Id, planned.Id, StringComparison.Ordinal));
        harness.Window.PairList.ScrollIntoView(row);
        harness.Window.PairList.UpdateLayout();
        harness.Window.PairList.SelectedItem = row;
        PumpUntil(harness.Window, () =>
                EndpointUiNullableField<SyncPairEditorSession>(harness.Window, "_endpointSession") is { } opened &&
                string.Equals(opened.EditingPairId, planned.Id, StringComparison.Ordinal),
            "Endpoint policy: the retained pair's own row reopens its editor draft");
        EndpointUiCall(harness.Window, "UpdatePairSaveAvailability");
        EndpointUiRequire(EndpointUiField<Button>(harness.Window, "SavePairButton").Visibility ==
                Visibility.Collapsed &&
            EndpointUiField<Button>(harness.Window, "CancelPairButton").Visibility == Visibility.Collapsed,
            "Endpoint policy: an unchanged retained endpoint pair is not spuriously dirty");
        EndpointUiRequire(
            EndpointUiNullableField<SyncPairEditorSession>(harness.Window, "_endpointSession") is { } retained &&
            retained.KindOf(SyncPairSide.Left) == SyncEndpointKind.GoogleDrive &&
            retained.KindOf(SyncPairSide.Right) == SyncEndpointKind.Local,
            "Endpoint policy: the unchanged retained editor still holds both ordered endpoints");
    }

    /// <summary>
    /// EUI-S2: a tree plan whose roots no longer match the explicit endpoints is refused visibly, and the
    /// persisted pair set is left exactly as it was.
    /// </summary>
    private static void RunEndpointPolicyStalePlanCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: false,
            server => server.AddFolder("policy-stale", "Stale", "root"), out _);
        AuthorEndpointPolicyLocalDraft(harness.Window, "Policy retained local pair",
            PolicyLocalRoot(fixture, "retained"), PolicyLocalRoot(fixture, "retained-copy"));
        SaveEndpointPolicyDraft(harness.Window, "Policy retained local pair");
        var before = Task.Run(() => harness.Host.LoadPairsAsync()).GetAwaiter().GetResult();
        EndpointUiRequire(before.Pairs.Count == 1,
            "Endpoint policy: this case's own retained pair is persisted before the stale attempt");
        // PairRows is refreshed before Save finishes configuring its monitors. A second routed
        // click during that same handler is intentionally rejected by the single-flight guard.
        // Wait for the real handler to release it, not an arbitrary delay or another Save call.
        var feedbackType = typeof(MainWindow).Assembly.GetType("Massdex.SyncShark.ActionFeedback")!;
        var activeFeedback = feedbackType.GetField("Active", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        var tryGetFeedback = activeFeedback.GetType().GetMethod("TryGetValue")!;
        bool SaveStillRunning() => (bool)tryGetFeedback.Invoke(activeFeedback,
            [EndpointUiField<Button>(harness.Window, "SavePairButton"), null])!;
        Console.WriteLine("OBSERVED previous Save still running at PairRows readback: " + SaveStillRunning());
        PumpUntil(harness.Window, () => !SaveStillRunning(),
            "Endpoint policy: the previous real Save handler releases its single-flight guard");

        AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Policy stale plan",
            PolicyLocalRoot(fixture, "local"), harness.Server.IdByName("root", "Stale"), "/Stale");
        SetEndpointUiField(harness.Window, "_globalDraftPlan", new PairDraftBindingPlan(
            Path.Combine(fixture, "not-the-chosen-folder"),
            new DropboxFolderEntry("Elsewhere", "/Elsewhere", "policy-elsewhere"), false, []));
        _ = harness.Window.Dispatcher.InvokeAsync(() =>
            EndpointUiField<Button>(harness.Window, "SavePairButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        PumpUntil(harness.Window, () =>
                EndpointUiField<TextBlock>(harness.Window, "PairEditorStatusText").Text
                    .Contains("no longer describes the chosen endpoints", StringComparison.Ordinal),
            "Endpoint policy: the stale tree plan is visibly refused instead of bound");
        var after = Task.Run(() => harness.Host.LoadPairsAsync()).GetAwaiter().GetResult();
        EndpointUiRequire(after.Pairs.Count == before.Pairs.Count &&
            !after.Pairs.Any(item => string.Equals(item.Name, "Policy stale plan", StringComparison.Ordinal)),
            "Endpoint policy: the refused stale plan leaves the persisted pair set unchanged");
    }

    /// <summary>EUI-S3: the explicitly selected Recovery is retained for the matching selected account.</summary>
    private static void RunEndpointPolicyRecoveryCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: false,
            server => server.AddFolder("policy-recovery", "Recovery Case", "root"), out _);
        var local = PolicyLocalRoot(fixture, "local");
        AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Policy recovery", local,
            harness.Server.IdByName("root", "Recovery Case"), "/Recovery Case");
        SelectEndpointPolicyAccount(harness.Window, harness.Binding.ConnectionId);
        var chosen = new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath, "recovery");
        MainWindow.RecoveryFolderDialogForTest = _ => chosen;
        RaiseEndpointPolicyButton(harness.Window, "BrowseRecoveryButton");
        PumpUntil(harness.Window, () =>
                EndpointUiNullableField<DropboxFolderEntry>(harness.Window, "_selectedRecoveryFolder") is not null,
            "Endpoint policy: the real Recovery browse records the chosen container");
        SaveEndpointPolicyDraft(harness.Window, "Policy recovery");
        var saved = LoadEndpointPolicyPair(harness.Host, "Policy recovery");
        EndpointUiRequire(saved.RecoveryRoot is { } boundRecovery &&
            string.Equals(boundRecovery.RootFolderId, "recovery", StringComparison.Ordinal) &&
            string.Equals(PilotPath.NormalizeDropboxRoot(boundRecovery.RootPath),
                SyncDropboxArtifactPaths.RecoveryContainerPath, StringComparison.OrdinalIgnoreCase),
            "Endpoint policy: the explicitly selected Recovery is retained for the matching selected account");
    }

    /// <summary>
    /// EUI-S3: the same explicitly selected Recovery is never reused once this draft's own selected account
    /// is not the account its cloud endpoint names.
    /// </summary>
    private static void RunEndpointPolicyOtherAccountRecoveryCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: true,
            server => server.AddFolder("policy-other", "Other Account", "root"), out var foreignAccountId);
        var local = PolicyLocalRoot(fixture, "local");
        AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Policy other account", local,
            harness.Server.IdByName("root", "Other Account"), "/Other Account");
        SelectEndpointPolicyAccount(harness.Window, foreignAccountId ??
            throw new InvalidDataException("The foreign account identity was not registered."));
        var foreign = new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
            "policy-foreign-recovery");
        MainWindow.RecoveryFolderDialogForTest = _ => foreign;
        RaiseEndpointPolicyButton(harness.Window, "BrowseRecoveryButton");
        PumpUntil(harness.Window, () =>
                EndpointUiNullableField<DropboxFolderEntry>(harness.Window, "_selectedRecoveryFolder") is not null,
            "Endpoint policy: the Recovery browse records the other account's selection too");
        SaveEndpointPolicyDraft(harness.Window, "Policy other account");
        var saved = LoadEndpointPolicyPair(harness.Host, "Policy other account");
        EndpointUiRequire(saved.RecoveryRoot is { } boundRecovery &&
            !string.Equals(boundRecovery.RootFolderId, "policy-foreign-recovery", StringComparison.Ordinal),
            "Endpoint policy: a Recovery chosen while a different account is selected is not reused by this pair");
    }

    /// <summary>
    /// UI397-A: the endpoint folder-browser subtitle names the exact account its picker browses, and no
    /// internal connection identity reaches the subtitle or its tooltip. Both accounts are real registered
    /// connections with distinct emails and holders, the draft's own account is chosen through the real
    /// account control, and the modal under assertion is the real production window the real Browse click
    /// constructs. Only the modal's own result is supplied, as the recovery browse case already does.
    /// </summary>
    private static void RunEndpointBrowserLabelCase(string fixture)
    {
        using var harness = OpenEndpointPolicyFixture(fixture, withForeignAccount: true,
            server => server.AddFolder("policy-other", "Other Account", "root"), out var foreignAccountId);
        var local = PolicyLocalRoot(fixture, "local");
        var captured = new List<DropboxFolderBrowserWindow>();
        MainWindow.PairEndpointFolderBrowserDialogForTest = window => { captured.Add(window); return null; };
        try
        {
            AuthorEndpointPolicyDraft(harness.Window, harness.Binding, "Label pair", local,
                harness.Server.IdByName("root", "Apps"), "/Apps");
            var session = EndpointUiField<SyncPairEditorSession>(harness.Window, "_endpointSession");
            RaiseEndpointPolicyButton(harness.Window, "LeftEndpointBrowseButton");
            PumpUntil(harness.Window, () => captured.Count == 1,
                "Endpoint label: the real Browse click opens the folder browser once");
            var selected = AccountSubtitle(captured[0]);
            var selectedLabel = SyncEndpointPresentation.AccountLabel(
                SyncEndpointPresentation.ShortLabel(SyncEndpointKind.GoogleDrive),
                harness.Binding.DisplayName, harness.Binding.Email);
            EndpointUiRequire(selected.Text == selectedLabel &&
                selected.Text.Contains(harness.Binding.Email, StringComparison.Ordinal) &&
                selected.Text.Contains(harness.Binding.DisplayName, StringComparison.Ordinal) &&
                !selected.Text.Contains(harness.Binding.ConnectionId, StringComparison.Ordinal) &&
                selected.ToolTip is string selectedTooltip &&
                !selectedTooltip.Contains(harness.Binding.ConnectionId, StringComparison.Ordinal),
                "Endpoint label: the selected account's readable email and holder name the folder-browser " +
                "subtitle and no connection identity reaches the subtitle or its tooltip ('" + selected.Text +
                "' / '" + selected.ToolTip + "')");

            var foreign = foreignAccountId ??
                throw new InvalidDataException("Endpoint label: the other account identity was not registered.");
            EndpointUiField<ComboBox>(harness.Window, "LeftEndpointKindBox").SelectedValue = SyncEndpointKind.Dropbox;
            EndpointUiField<ComboBox>(harness.Window, "LeftEndpointAccountBox").SelectedValue = foreign;
            PumpUntil(harness.Window, () => string.Equals(session.PickerFor(SyncPairSide.Left).ConnectionId,
                    foreign, StringComparison.Ordinal),
                "Endpoint label: the left picker names the other account");
            RaiseEndpointPolicyButton(harness.Window, "LeftEndpointBrowseButton");
            PumpUntil(harness.Window, () => captured.Count == 2,
                "Endpoint label: the second Browse click opens the folder browser once");
            var other = AccountSubtitle(captured[1]);
            var otherLabel = SyncEndpointPresentation.AccountLabel(
                SyncEndpointPresentation.ShortLabel(SyncEndpointKind.Dropbox),
                "Policy Other Account", "policy-other@example.invalid");
            EndpointUiRequire(other.Text == otherLabel &&
                other.Text.Contains("policy-other@example.invalid", StringComparison.Ordinal) &&
                !other.Text.Contains(harness.Binding.Email, StringComparison.Ordinal) &&
                !other.Text.Contains(foreign, StringComparison.Ordinal) &&
                other.ToolTip is string otherTooltip &&
                !otherTooltip.Contains(foreign, StringComparison.Ordinal) &&
                !otherTooltip.Contains(harness.Binding.Email, StringComparison.Ordinal),
                "Endpoint label: the other account's own label is used, never the first account's, and neither " +
                "subtitle nor tooltip carries a connection identity ('" + other.Text + "' / '" + other.ToolTip + "')");

            var fallback = (string)typeof(MainWindow).GetMethod("EndpointBrowserAccountLabel", EndpointUiFlags)!
                .Invoke(harness.Window, [SyncEndpointKind.Dropbox, "connection-not-registered"])!;
            EndpointUiRequire(string.Equals(fallback,
                    SyncEndpointPresentation.AccountLabel(
                        SyncEndpointPresentation.ShortLabel(SyncEndpointKind.Dropbox), null, null),
                    StringComparison.Ordinal) &&
                !fallback.Contains("connection", StringComparison.Ordinal),
                "Endpoint label: an account with no retained identity falls back to the shared provider form " +
                "with no identifier ('" + fallback + "')");

            // UI398-3: the ONE shared display-only resolver behind the Activity endpoint surfaces states a
            // missing or unregistered identity itself. It must never reach the host, because a host lookup
            // with a null id answers with the primary account and would present that account as this
            // endpoint's own; this window holds a real registered foreign account the lookup could return.
            var resolver = typeof(MainWindow).GetMethod("EndpointAccountDisplayLabel", EndpointUiFlags)!;
            var noIdentity = (string)resolver.Invoke(harness.Window, [null])!;
            var blankIdentity = (string)resolver.Invoke(harness.Window, ["   "])!;
            var unknownIdentity = (string)resolver.Invoke(harness.Window, ["connection-not-registered"])!;
            EndpointUiRequire(noIdentity == "Account not recorded" && blankIdentity == "Account not recorded" &&
                unknownIdentity == "Account unavailable" &&
                !noIdentity.Contains("connection", StringComparison.Ordinal) &&
                !unknownIdentity.Contains("connection-", StringComparison.Ordinal),
                "Endpoint label: the shared resolver states a missing or unregistered endpoint account instead " +
                "of resolving the current one ('" + noIdentity + "' / '" + blankIdentity + "' / '" +
                unknownIdentity + "')");
        }
        finally { MainWindow.PairEndpointFolderBrowserDialogForTest = null; }
    }

    private static TextBlock AccountSubtitle(DropboxFolderBrowserWindow window) =>
        window.FindName("AccountText") as TextBlock ??
        throw new InvalidDataException("Endpoint label: the folder browser lost its account subtitle.");

}
