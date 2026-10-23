using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// EPS3B endpoint location actions: staged source check, not yet built or run.
//
// The actions are measured through the production seams (the real details sidebar and the real row
// menu), so a left and a right action for a two-cloud pair, a local-only pair, a same-provider
// two-account pair and a missing historical identity are proved by what the product builds - not by a
// helper constant. Nothing here performs a provider call, opens a real location or changes state.
internal static class EndpointLocationActionChecks
{
    internal static int Run(string root)
    {
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        var passed = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS " + label); }

        var stateRoot = Path.Combine(Path.GetFullPath(root), "endpoint-location-action");
        Directory.CreateDirectory(stateRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var state = instance.StateAuthority;
        var googleA = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("el-google-a"), "el-google-a",
            "a@example.test", "Holder A", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Pump(new GoogleDriveConnectionStore(state).SaveAsync(googleA));
        var dropboxA = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "el-dropbox-a", "el-dropbox-root-a", "el-dropbox-home-a",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Dropbox Holder A", Email: "dropbox-a@example.test").Normalize();
        Pump(new DropboxConnectionStore(state).SaveNewAsync(dropboxA));
        var dropboxB = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "el-dropbox-b", "el-dropbox-root-b", "el-dropbox-home-b",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Dropbox Holder B", Email: "dropbox-b@example.test",
            CredentialReference: WindowsDropboxRefreshTokenStore.AccountTarget(
                DropboxConnectionBinding.CreateConnectionId("el-dropbox-b"))).Normalize();
        // The first account is the store's primary (SaveNewAsync); a second account of the same provider is
        // appended through the store's own additional-account path, which requires that account's own
        // credential target. The fixture still registers exactly the two accounts the cases measure.
        Pump(new DropboxConnectionStore(state).SaveAsync(dropboxB));
        using var theme = new ThemeManager(new UiPreferencesStore(state));
        theme.Initialize();
        var localRoot = Path.Combine(stateRoot, "local");
        Directory.CreateDirectory(localRoot);
        var window = new MainWindow(state, theme) { Width = 1240, Height = 790 };
        window.StartupRegistrationForTest = _ => throw new InvalidOperationException("This check must not modify Windows startup.");
        window.Show(); Drain(); window.UpdateLayout();
        Pump(window.AuthorityHost.LoadPairsAsync());
        try
        {
            // --- Two cloud endpoints of different providers: one ordered action each -------------------
            var twoCloud = new SyncEndpointContext("el-two-cloud", "Two cloud",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.GoogleDrive, "local",
                    SyncProviders.GoogleDrive, googleA.ConnectionId, googleA.AccountKey, "el-left-folder", "/Recorded left"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, dropboxA.ConnectionId, dropboxA.RootNamespaceId, "id:el-right", "/Recorded right"));
            var cloudRow = Row("docs/report.pdf", () => twoCloud);
            var actions = window.ActivityEndpointLocationActions(cloudRow);
            Check(actions.Count == 2 && actions[0].Side == SyncActivitySide.Left &&
                  actions[1].Side == SyncActivitySide.Right,
                "A two-cloud event offers exactly one ordered Left and Right location action");
            Check(actions[0].EndpointLabel == "Google Drive" && actions[1].EndpointLabel == "Dropbox" &&
                  actions[0].Account == googleA.ConnectionId && actions[1].Account == dropboxA.ConnectionId,
                "Each action carries its own endpoint's exact recorded account and provider");
            Check(actions[0].Location == "/Recorded left/docs/report.pdf" &&
                  actions[1].Location == "/Recorded right/docs/report.pdf",
                "Each action carries its own endpoint's recorded root in left/right order");
            Check(actions[1].Enabled && actions[1].WebLocation is { } rightUrl &&
                  rightUrl.AbsoluteUri.StartsWith("https://", StringComparison.Ordinal),
                "The recorded Dropbox endpoint reuses the existing provider web-location rule");

            // --- Same provider, two accounts: the actions stay distinguishable -------------------------
            var sameProvider = new SyncEndpointContext("el-two-dropbox", "Two Dropbox accounts",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Dropbox, "local",
                    SyncProviders.Dropbox, dropboxA.ConnectionId, dropboxA.RootNamespaceId, "id:el-a", "/Account A"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, dropboxB.ConnectionId, dropboxB.RootNamespaceId, "id:el-b", "/Account B"));
            var sameRow = Row("docs/report.pdf", () => sameProvider);
            var sameActions = window.ActivityEndpointLocationActions(sameRow);
            Check(sameActions.Count == 2 && sameActions[0].Account == dropboxA.ConnectionId &&
                  sameActions[1].Account == dropboxB.ConnectionId &&
                  sameActions[0].Account != sameActions[1].Account,
                "Two accounts of one provider keep their own recorded account instead of one shared label");
            Check(sameActions[0].Location == "/Account A/docs/report.pdf" &&
                  sameActions[1].Location == "/Account B/docs/report.pdf" &&
                  sameActions[0].WebLocation?.AbsoluteUri != sameActions[1].WebLocation?.AbsoluteUri,
                "The two same-provider actions keep their own recorded root and web location");

            // --- Local plus cloud: one local action and one cloud action --------------------------------
            var localLeft = new SyncEndpointContext("el-local-left", "Local left",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Local, "local",
                    LocalPath: localRoot),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, dropboxA.ConnectionId, dropboxA.RootNamespaceId, "id:el-lr", "/Recorded right"));
            var localRow = Row("docs/report.pdf", () => localLeft);
            var localActions = window.ActivityEndpointLocationActions(localRow);
            Check(localActions.Count == 2 && localActions[0].Account is null &&
                  localActions[0].WebLocation is null && localActions[0].Enabled &&
                  localActions[0].Location?.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase) == true,
                "The recorded local endpoint offers its own local folder action and claims no cloud account");
            Check(localActions[1].Enabled && localActions[1].WebLocation is not null,
                "The recorded cloud endpoint keeps its own cloud location action beside the local one");

            // --- Local to local: local actions only ------------------------------------------------------
            var localLocal = new SyncEndpointContext("el-local-local", "Local to local",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Local, "local", LocalPath: localRoot),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Local, "dropbox",
                    LocalPath: Path.Combine(stateRoot, "local-two")));
            Directory.CreateDirectory(Path.Combine(stateRoot, "local-two"));
            var localLocalRow = Row("docs/report.pdf", () => localLocal);
            var localLocalActions = window.ActivityEndpointLocationActions(localLocalRow);
            Check(localLocalActions.Count == 2 && localLocalActions.All(action => action.Account is null) &&
                  localLocalActions.All(action => action.WebLocation is null) &&
                  localLocalActions.All(action => action.EndpointLabel == "This computer"),
                "A local-to-local event offers local actions only and never invents a cloud endpoint");

            // --- Missing historical identity: present, disabled, and never the current account ----------
            SetField(window, "_pairSet", new SyncPairSet([], null));
            var noAccountLeft = new SyncEndpointContext("el-no-account", "No recorded account",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.GoogleDrive, "local",
                    SyncProviders.GoogleDrive, NamespaceId: "el-ns", FolderPath: "/Recorded left"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, dropboxA.ConnectionId, dropboxA.RootNamespaceId, "id:el-r2", "/Recorded right"));
            var noAccountRow = Row("docs/report.pdf", () => noAccountLeft);
            var noAccountActions = window.ActivityEndpointLocationActions(noAccountRow);
            Check(noAccountActions[0].Account is null && !noAccountActions[0].Enabled &&
                  noAccountActions[0].Location == "/Recorded left/docs/report.pdf" &&
                  noAccountActions[0].Reason.Contains("did not record", StringComparison.Ordinal),
                "A cloud endpoint with no recorded account stays visible with its location and a disabled action carrying its reason");
            Check(!noAccountActions[0].Enabled && noAccountActions[1].Enabled,
                "The missing-account action is disabled while the endpoint that did record its account still works");
            var noRootLeft = new SyncEndpointContext("el-no-root", "No recorded root",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Dropbox, "local",
                    SyncProviders.Dropbox, dropboxA.ConnectionId),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, dropboxB.ConnectionId, dropboxB.RootNamespaceId, "id:el-r3", "/Recorded right"));
            var noRootActions = window.ActivityEndpointLocationActions(Row("docs/report.pdf", () => noRootLeft));
            Check(!noRootActions[0].Enabled && noRootActions[0].Location is null &&
                  noRootActions[0].Reason.Contains("root", StringComparison.Ordinal),
                "A cloud endpoint with no recorded root is disabled with a root reason rather than borrowing a root");

            // --- The recorded context stays historical -------------------------------------------------
            SetField(window, "_pairSet", new SyncPairSet([], null));
            var historical = Row("docs/report.pdf", () => twoCloud);
            var historicalActions = window.ActivityEndpointLocationActions(historical);
            Check(!historicalActions[0].Enabled &&
                  historicalActions[0].Reason.Contains("cannot be resolved", StringComparison.Ordinal) &&
                  historicalActions[0].Location == "/Recorded left/docs/report.pdf" &&
                  historicalActions[1].Enabled && historicalActions[1].WebLocation is not null,
                "With no matching current pair the recorded Drive endpoint stays unavailable while the recorded Dropbox root still resolves");

            // --- EL-1: a NON-PRIMARY Google endpoint on the same pair is eligible ------------------------
            var cloudLocal = Path.Combine(stateRoot, "el-non-primary-local");
            Directory.CreateDirectory(cloudLocal);
            using (var lease = PilotDirectoryLease.Open(cloudLocal))
            {
                var nonPrimary = new SyncPair("el-non-primary", "Two cloud", cloudLocal,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/Recorded right", "id:el-right",
                    ConnectionId: dropboxA.ConnectionId, Provider: SyncProviders.Dropbox,
                    InitialReviewApproved: false) with
                {
                    EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
                    LeftEndpoint = new SyncEndpointRecord(SyncEndpointKind.GoogleDrive,
                        Provider: SyncProviders.GoogleDrive, ConnectionId: googleA.ConnectionId,
                        NamespaceId: googleA.AccountKey, FolderId: "el-left-folder", FolderPath: "/Recorded left"),
                    RightEndpoint = new SyncEndpointRecord(SyncEndpointKind.Dropbox,
                        Provider: SyncProviders.Dropbox, ConnectionId: dropboxA.ConnectionId,
                        NamespaceId: dropboxA.RootNamespaceId, FolderId: "id:el-right", FolderPath: "/Recorded right")
                };
                // A cloud/cloud pair's retained identity IS the derivation over its ordered document, so the
                // fixture derives it exactly as the binding host does instead of asserting a hand-made id.
                nonPrimary = nonPrimary with
                {
                    Id = SyncCloudPairConfiguration.DeriveId(nonPrimary.LeftEndpoint!, nonPrimary.RightEndpoint!),
                    // A two-cloud document carries NO compatibility local root: the pair's ordered document is
                    // its only endpoint source, exactly as the binding host retains such a pair.
                    LocalRoot = string.Empty,
                    LocalVolumeSerialNumber = 0,
                    LocalRootFileId = 0
                };
                Pump(window.AuthorityHost.Authority.RegisterPairAsync(nonPrimary.Id, nonPrimary.PolicyRevision));
                Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(nonPrimary, true));
                SetField(window, "_pairSet", Pump(window.AuthorityHost.LoadPairsAsync()));
                var nonPrimaryContext = new SyncEndpointContext(nonPrimary.Id, "Two cloud",
                    new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.GoogleDrive, "local",
                        SyncProviders.GoogleDrive, googleA.ConnectionId, googleA.AccountKey, "el-left-folder", "/Recorded left"),
                    new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                        SyncProviders.Dropbox, dropboxA.ConnectionId, dropboxA.RootNamespaceId, "id:el-right", "/Recorded right"));
                var nonPrimaryRow = Row("docs/report.pdf", () => nonPrimaryContext);
                var nonPrimaryActions = window.ActivityEndpointLocationActions(nonPrimaryRow);
                Check(nonPrimaryActions[0].EndpointLabel == "Google Drive" && nonPrimaryActions[0].Enabled &&
                      nonPrimaryActions[0].Account == googleA.ConnectionId && nonPrimaryActions[1].Enabled,
                    "A Google endpoint held on a NON-PRIMARY account is eligible and keeps its own recorded account");
                var leftRequest = MainWindow.EndpointDriveRequest(nonPrimaryRow, nonPrimaryContext.Left);
                var rightRequest = MainWindow.EndpointDriveRequest(nonPrimaryRow, nonPrimaryContext.Right);
                Check(leftRequest.PairId == nonPrimaryRow.PairId && leftRequest.Side == SyncPairSide.Left &&
                      Equals(leftRequest.Context, nonPrimaryRow.EndpointContext) &&
                      leftRequest.RelativePath == "docs/report.pdf" &&
                      rightRequest.Side == SyncPairSide.Right,
                    "The caller passes the recorded pair, the SELECTED logical side, the recorded context and the recorded path");
                var reboundContext = nonPrimaryContext with
                {
                    Left = nonPrimaryContext.Left with { ConnectionId = "el-google-not-current" }
                };
                var reboundActions = window.ActivityEndpointLocationActions(Row("docs/report.pdf", () => reboundContext));
                Check(!reboundActions[0].Enabled &&
                      reboundActions[0].Reason.Contains("not the pair's current", StringComparison.Ordinal) &&
                      reboundActions[0].Location == "/Recorded left/docs/report.pdf",
                    "A rebound Google endpoint stays visible with its location and unavailable, never the current account");

                // EL-1: a GENUINE legacy no-document pair must keep a working Google Drive endpoint action.
                // Capture resolves it as left-local and right-cloud, so the right endpoint is the Google one.
                var legacy = new SyncPair("el-legacy", "Legacy Google", cloudLocal,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/LegacyRoot", "el-legacy-folder",
                    ConnectionId: googleA.ConnectionId, Provider: SyncProviders.GoogleDrive,
                    InitialReviewApproved: false);
                Pump(window.AuthorityHost.Authority.RegisterPairAsync(legacy.Id, legacy.PolicyRevision));
                Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(legacy, true));
                SetField(window, "_pairSet", Pump(window.AuthorityHost.LoadPairsAsync()));
                var legacyContext = SyncEndpointContext.Capture(legacy);
                var legacyRow = Row("docs/report.pdf", () => legacyContext);
                var legacyActions = window.ActivityEndpointLocationActions(legacyRow);
                Check(legacyActions[1].EndpointLabel == "Google Drive" && legacyActions[1].Enabled &&
                      legacyActions[1].Account == googleA.ConnectionId,
                    "A genuine legacy no-document pair keeps a successful Google Drive endpoint action");
                Call(window, "InspectActivity", legacyRow); Drain(); window.UpdateLayout();
                var legacyDriveAction = EndpointActionButtons(window)
                    .SingleOrDefault(button => AutomationProperties.GetName(button)
                        .Contains("Google Drive", StringComparison.Ordinal));
                Check(legacyDriveAction is { IsEnabled: true },
                    "The UI action the user clicks for the legacy Drive endpoint is enabled");
                var legacyRequest = MainWindow.EndpointDriveRequest(legacyRow, legacyContext.Right);
                Check(legacyRequest.Side == SyncPairSide.Right && Equals(legacyRequest.Context, legacyContext) &&
                      legacyRequest.PairId == legacy.Id,
                    "The legacy Drive action passes the resolved context and the selected logical side");

                // Missing, wrong and rebound account or root all refuse with a reason.
                var refusals = new (SyncEndpointContext Context, string Description)[]
                {
                    (legacyContext with { Right = legacyContext.Right with { ConnectionId = "el-other-account" } },
                        "A wrong recorded Google account refuses instead of using the pair's account"),
                    (legacyContext with { Right = legacyContext.Right with { ConnectionId = null } },
                        "A missing recorded Google account refuses rather than borrowing one"),
                    (legacyContext with { Right = legacyContext.Right with { FolderPath = "/OtherRoot" } },
                        "A wrong recorded Google root refuses instead of using the pair's root")
                };
                foreach (var (refusedContext, description) in refusals)
                {
                    var refusedAction = window.ActivityEndpointLocationActions(
                        Row("docs/report.pdf", () => refusedContext))[1];
                    Check(!refusedAction.Enabled && refusedAction.Location is not null && refusedAction.Reason.Length > 0,
                        description);
                }

                // A malformed explicit document with plausible legacy fields must still refuse.
                var malformed = new SyncPair("el-malformed", "Malformed document", cloudLocal,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/LegacyRoot", "el-legacy-folder",
                    ConnectionId: googleA.ConnectionId, Provider: SyncProviders.GoogleDrive,
                    InitialReviewApproved: false) with
                {
                    EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
                    LeftEndpoint = new SyncEndpointRecord(SyncEndpointKind.Local, LocalPath: cloudLocal,
                        LocalVolumeSerialNumber: lease.Identity.VolumeSerialNumber,
                        LocalRootFileId: lease.Identity.FileId),
                    RightEndpoint = new SyncEndpointRecord(SyncEndpointKind.GoogleDrive,
                        Provider: SyncProviders.GoogleDrive, ConnectionId: googleA.ConnectionId)
                };
                Pump(window.AuthorityHost.Authority.RegisterPairAsync(malformed.Id, malformed.PolicyRevision));
                // A malformed explicit endpoint document is refused by the retained-authority validation
                // itself, so this retain is EXPECTED to be refused and the pair stays unretained; the case's
                // own assertions below are unchanged and prove the refusal reaches the rendered action.
                try
                {
                    Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(malformed, true));
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
                {
                    Check(MainWindow.TryResolveCurrentEndpoints(malformed, out _) == false,
                        "The malformed document is refused at retention and also never resolves: " +
                        error.GetType().Name);
                }
                SetField(window, "_pairSet", Pump(window.AuthorityHost.LoadPairsAsync()));
                var malformedResolved = MainWindow.TryResolveCurrentEndpoints(malformed, out _);
                var malformedActions = window.ActivityEndpointLocationActions(
                    Row("docs/report.pdf", () => legacyContext with { PairId = malformed.Id }));
                Check(!malformedResolved && !malformedActions[1].Enabled &&
                      malformedActions[1].Reason.Contains("cannot be resolved", StringComparison.Ordinal),
                    "A malformed explicit endpoint document with plausible legacy fields still refuses and never falls back");
            }
            // --- The real details sidebar renders one action per recorded endpoint ----------------------
            Call(window, "InspectActivity", cloudRow); Drain(); window.UpdateLayout();
            var strips = EndpointStrips(window);
            Check(strips.Count == 2 && strips[0].Contains("Left endpoint") && strips[1].Contains("Right endpoint"),
                "The real inspector renders one endpoint action for the left and one for the right: " +
                string.Join(" | ", strips));
            Call(window, "InspectActivity", historical); Drain(); window.UpdateLayout();
            var unavailable = UnavailableEndpointActions(window);
            // The historical row HAS a recorded Google account but no retained current pair carries its pair
            // id, so its correct disabled branch is the cannot-be-resolved one the action model above already
            // asserted; the inspector must render exactly that single disabled action with that same non-empty
            // reason. The missing-account wording belongs to the no-account row and is proved separately below.
            Check(historicalActions[0].Reason.Length > 0 && unavailable.Count == 1 &&
                  unavailable[0].Contains(historicalActions[0].Reason, StringComparison.Ordinal) &&
                  unavailable[0].Contains("cannot be resolved", StringComparison.Ordinal),
                "The real inspector renders the single disabled historical endpoint action with its own cannot-be-resolved reason: " +
                string.Join(" | ", unavailable));
            // The missing-account branch keeps its own distinct reason in the real inspector, and the other
            // recorded endpoint of that same row stays enabled beside it.
            Call(window, "InspectActivity", noAccountRow); Drain(); window.UpdateLayout();
            var noAccountUnavailable = UnavailableEndpointActions(window);
            var noAccountButtons = EndpointActionButtons(window);
            Check(noAccountActions[0].Reason.Contains("did not record", StringComparison.Ordinal) &&
                  noAccountActions[1].Enabled && noAccountUnavailable.Count == 1 &&
                  noAccountUnavailable[0].Contains(noAccountActions[0].Reason, StringComparison.Ordinal) &&
                  noAccountButtons.Count == 2 && noAccountButtons.Count(button => button.IsEnabled) == 1,
                "The real inspector renders the missing-account action with its own recorded-account reason while the recorded cloud endpoint stays enabled: " +
                string.Join(" | ", noAccountUnavailable));

            // --- UI398-3: the real sidebar and captions name each endpoint's OWN exact account ------------
            Call(window, "InspectActivity", cloudRow); Drain(); window.UpdateLayout();
            var cloudAccounts = EndpointAccountLines(window);
            Check(cloudAccounts.Count == 2 &&
                  cloudAccounts[0].Contains("a@example.test", StringComparison.Ordinal) &&
                  cloudAccounts[1].Contains("dropbox-a@example.test", StringComparison.Ordinal) &&
                  cloudAccounts[0] != cloudAccounts[1] &&
                  !cloudAccounts[0].Contains(googleA.ConnectionId, StringComparison.Ordinal) &&
                  !cloudAccounts[1].Contains(dropboxA.ConnectionId, StringComparison.Ordinal),
                "The details sidebar names each recorded endpoint's own exact account, never the selected or " +
                "primary one and never a connection identity: " + string.Join(" | ", cloudAccounts));
            var cloudCaptions = EndpointActionButtons(window).Select(AutomationProperties.GetName).ToList();
            Check(cloudCaptions.Count == 2 &&
                  cloudCaptions[0].Contains("a@example.test", StringComparison.Ordinal) &&
                  cloudCaptions[1].Contains("dropbox-a@example.test", StringComparison.Ordinal) &&
                  cloudCaptions.All(caption => !caption.Contains(googleA.ConnectionId, StringComparison.Ordinal) &&
                      !caption.Contains(dropboxA.ConnectionId, StringComparison.Ordinal)),
                "The endpoint action accessibility captions name the same exact accounts with no connection " +
                "identity: " + string.Join(" | ", cloudCaptions));
            Call(window, "InspectActivity", sameRow); Drain(); window.UpdateLayout();
            var sameAccounts = EndpointAccountLines(window);
            var sameCaptions = EndpointActionButtons(window).Select(AutomationProperties.GetName).ToList();
            Check(sameAccounts.Count == 2 &&
                  sameAccounts[0].Contains("dropbox-a@example.test", StringComparison.Ordinal) &&
                  sameAccounts[1].Contains("dropbox-b@example.test", StringComparison.Ordinal) &&
                  sameAccounts[0] != sameAccounts[1] &&
                  sameCaptions.Count == 2 &&
                  sameCaptions[0].Contains("dropbox-a@example.test", StringComparison.Ordinal) &&
                  sameCaptions[1].Contains("dropbox-b@example.test", StringComparison.Ordinal),
                "Two accounts of one provider keep their own label in the sidebar and in the captions: " +
                string.Join(" | ", sameAccounts) + " || " + string.Join(" | ", sameCaptions));
            Call(window, "InspectActivity", noAccountRow); Drain(); window.UpdateLayout();
            var unrecordedLines = EndpointAccountLines(window);
            var primary = window.AuthorityHost.LoadAccountBinding();
            var primaryLabel = primary is null ? null
                : MainWindow.AccountLabel(primary.ProviderName, primary.DisplayName, primary.Email);
            Check(unrecordedLines.Count == 2 && unrecordedLines[0] == "Account not recorded" &&
                  unrecordedLines[1] == "Account: " +
                      MainWindow.AccountLabel(SyncProviders.Dropbox, dropboxA.DisplayName, dropboxA.Email) &&
                  unrecordedLines.All(line => !line.Contains("connection-", StringComparison.Ordinal)) &&
                  (primary is null || (!unrecordedLines[0].Contains(primaryLabel!, StringComparison.Ordinal) &&
                      !unrecordedLines[0].Contains(primary.ConnectionId, StringComparison.Ordinal))),
                "An endpoint that recorded no account states the absence instead of resolving the current or " +
                "primary account: " + string.Join(" | ", unrecordedLines) + " (primary '" + (primaryLabel ?? "none") + "')");
            var unknownAccountId = "connection-" + new string('c', 24);
            var unknownAccountRow = Row("docs/report.pdf", () => twoCloud with
                { Right = twoCloud.Right with { ConnectionId = unknownAccountId } });
            Call(window, "InspectActivity", unknownAccountRow); Drain(); window.UpdateLayout();
            var unknownAccountLines = EndpointAccountLines(window);
            Check(unknownAccountLines.Count == 2 && unknownAccountLines[1] == "Account unavailable" &&
                  !unknownAccountLines[1].Contains(unknownAccountId, StringComparison.Ordinal) &&
                  EndpointActionButtons(window).Select(AutomationProperties.GetName)
                      .Any(caption => caption.Contains("Account unavailable", StringComparison.Ordinal)),
                "An endpoint whose recorded account no longer resolves says so rather than showing its " +
                "identity: " + string.Join(" | ", unknownAccountLines));

            // --- UI398-4: a recorded location keeps its own casing, punctuation and characters -------------
            var mixedCaseRoot = "/Résumé d'Été/Ärger";
            var mixedRow = Row("Docs/Report'.PDF", () => twoCloud with
                { Right = twoCloud.Right with { FolderPath = mixedCaseRoot } });
            Call(window, "InspectActivity", mixedRow); Drain(); window.UpdateLayout();
            var literal = MainWindow.ActivityEndpointLocation(mixedRow, mixedRow.EndpointContext!.Right)!;
            var copyButton = CopyButton(window);
            var copied = new List<string>();
            MainWindow.ClipboardTextSetterForTest = copied.Add;
            try
            {
                copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, copyButton));
                Drain();
            }
            finally { MainWindow.ClipboardTextSetterForTest = null; }
            Check(literal.Contains("Résumé", StringComparison.Ordinal) && literal.Contains('É') &&
                  literal.Contains('\'') && literal != literal.ToLowerInvariant(),
                "The recorded location this check copies is mixed case with Unicode and an apostrophe: " + literal);
            Check(DisplayedCaption(copyButton) == literal && AutomationProperties.GetName(copyButton) == literal &&
                  copied.Count == 1 && copied[0] == literal,
                "The recorded endpoint location is shown and copied exactly as recorded, without a caption pass " +
                "or a path rewrite: '" + DisplayedCaption(copyButton) + "' / '" + string.Join(" | ", copied) + "'");

            // --- The row menu consumes exactly this action list, so the two surfaces cannot disagree ---
            var menuEntries = window.ActivityEndpointLocationActions(cloudRow);
            Check(menuEntries.Select(action => action.Label + "|" + action.IsLocal + "|" + action.Enabled)
                      .SequenceEqual(["Left endpoint: Open in Google Drive|False|False",
                                      "Right endpoint: Open in Dropbox|False|True"]) &&
                  menuEntries[0].Reason.Contains("cannot be resolved", StringComparison.Ordinal),
                "The row menu entries carry each endpoint's own label, side, enablement and disabled reason");
        }
        finally { window.Close(); }
        Console.WriteLine(passed + " endpoint location action checks passed; no provider call, credential or " +
            "product-state effect was performed.");
        return 0;
    }

    private static List<string> EndpointStrips(MainWindow window) =>
        Field<StackPanel>(window, "_detailFields").Children.OfType<StackPanel>()
            .Where(panel => (panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty)
                .StartsWith("Left endpoint:", StringComparison.Ordinal) ||
                (panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty)
                .StartsWith("Right endpoint:", StringComparison.Ordinal))
            .Select(panel => (panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty) + " " +
                string.Join(" | ", Buttons(panel).Select(button => button.ToolTip?.ToString() ?? string.Empty)))
            .ToList();

    private static List<string> UnavailableEndpointActions(MainWindow window) =>
        Field<StackPanel>(window, "_detailFields").Children.OfType<StackPanel>()
            .SelectMany(Buttons)
            .Where(button => !button.IsEnabled &&
                AutomationProperties.GetName(button).Contains("location action", StringComparison.Ordinal))
            .Select(button => AutomationProperties.GetName(button))
            .ToList();

    private static List<Button> EndpointActionButtons(MainWindow window) =>
        Field<StackPanel>(window, "_detailFields").Children.OfType<StackPanel>()
            .SelectMany(Buttons)
            .Where(button => AutomationProperties.GetName(button).Contains("location action", StringComparison.Ordinal))
            .ToList();

    // UI398-3: the endpoint detail blocks of the real details sidebar, in the order the product rendered
    // them. The sidebar wraps each block in the shared field label, so the block itself is the wrapper's
    // nested panel and the account, location and action controls all live inside that block.
    private static List<StackPanel> EndpointDetails(MainWindow window) =>
        Field<StackPanel>(window, "_detailFields").Children.OfType<StackPanel>()
            .Where(wrapper => EndpointHeading(wrapper).Length > 0)
            .SelectMany(wrapper => wrapper.Children.OfType<StackPanel>())
            .ToList();

    private static string EndpointHeading(StackPanel wrapper) =>
        wrapper.Children.OfType<TextBlock>().FirstOrDefault()?.Text is { } heading &&
        (heading.StartsWith("Left endpoint:", StringComparison.Ordinal) ||
         heading.StartsWith("Right endpoint:", StringComparison.Ordinal))
            ? heading : string.Empty;

    // The account line each endpoint block shows: the block's own heading is its first text, so a local
    // endpoint contributes none and a cloud endpoint contributes exactly the one account line.
    private static List<string> EndpointAccountLines(MainWindow window) => EndpointDetails(window)
        .Select(detail => string.Join(" ", detail.Children.OfType<TextBlock>().Skip(1).Select(text => text.Text)))
        .ToList();

    // The recorded-location copy control of the last endpoint block, identified by its own tooltip.
    private static Button CopyButton(MainWindow window) =>
        Buttons(EndpointDetails(window)[^1]).FirstOrDefault(button => button.ToolTip is string tooltip &&
            tooltip.StartsWith("Copy this endpoint's recorded location", StringComparison.Ordinal)) ??
        throw new InvalidDataException("UI398-4: the recorded endpoint location copy control is absent.");

    // The visible caption of a shared command button, read from the element the production helper built.
    private static string DisplayedCaption(Button button) =>
        button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().SingleOrDefault() is { } caption
            ? caption.Text : string.Empty;

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button button) yield return button;
            foreach (var nested in Buttons(child)) yield return nested;
        }
    }

    private static ActivityRow Row(string path, Func<SyncEndpointContext> context) =>
        new ActivityRow(DateTimeOffset.Now.ToString("O"), path, "Copied", "Recorded and verified.", "Account label",
            context().PairId, "Pair", "el-operation", "File", "PDF") with
        {
            EndpointContext = context(),
            OperationKind = SyncAuthorityOperationKind.CreateRemote,
            Phase = SyncActivityPhase.Completed
        };

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task) { while (!task.IsCompleted) { Drain(); Thread.Sleep(1); } task.GetAwaiter().GetResult(); }

    // The result-returning form is needed where a pumped task's value is assigned or stored; the void form
    // above stays for the fire-and-check call sites. Both pump the same way.
    private static T Pump<T>(Task<T> task)
    { while (!task.IsCompleted) { Drain(); Thread.Sleep(1); } return task.GetAwaiter().GetResult(); }
}
