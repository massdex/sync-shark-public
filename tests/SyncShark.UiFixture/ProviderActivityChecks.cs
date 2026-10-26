using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// The dedicated P37/P38 activity-provider check. It builds real activity rows against the real
// stored account and pair metadata and inspects them through the production details sidebar,
// copy details and cloud-location seams, so a Google account, a Dropbox account and an
// unrecorded historical row are proved by the labels this product actually renders - not by
// asserting a helper constant. Missing legacy connection evidence remains unknown; new rows carry
// the immutable ordered context rather than borrowing the pair's current account.
internal static class ProviderActivityChecks
{
    internal static int Run(string root)
    {
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        var passed = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS " + label); }

        var stateRoot = Path.Combine(Path.GetFullPath(root), "provider-activity");
        Directory.CreateDirectory(stateRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var state = instance.StateAuthority;
        var dropbox = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "pa-dropbox-account", "pa-dropbox-root", "pa-dropbox-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Personal Holder", Email: "personal@example.test").Normalize();
        Pump(new DropboxConnectionStore(state).SaveNewAsync(dropbox));
        const string permissionId = "pa-google-account";
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId,
            "google@example.test", "Google Holder", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Pump(new GoogleDriveConnectionStore(state).SaveAsync(google));
        using var theme = new ThemeManager(new UiPreferencesStore(state));
        theme.Initialize();
        var window = new MainWindow(state, theme) { Width = 1240, Height = 790 };
        window.StartupRegistrationForTest = _ => throw new InvalidOperationException("This check must not modify Windows startup.");
        window.Show(); Drain(); window.UpdateLayout();
        Pump(window.AuthorityHost.LoadPairsAsync());
        var authority = window.AuthorityHost.Authority;
        var googlePair = RetainedPair(authority, stateRoot, "pa-google-pair", "Drive Pair", "google-local",
            "/Google/Paired", google.ConnectionId, SyncProviders.GoogleDrive);
        var dropboxPair = RetainedPair(authority, stateRoot, "pa-dropbox-pair", "Dropbox Pair", "dropbox-local",
            "/Dropbox/Paired", dropbox.ConnectionId, SyncProviders.Dropbox);
        SetField(window, "_pairSet", Pump(window.AuthorityHost.LoadPairsAsync()));
        try
        {
            Check(Field<bool>(window, "_workspaceReady"), "The production workspace and its details sidebar are ready for inspection");

            // Installed pre-endpoint history has a typed transfer but no captured endpoint/name/account.
            // Exercise the same retained-row factory used by Home, Activity and Tray, not a hand-built row.
            foreach (var kind in new[] { SyncAuthorityOperationKind.CreateLocal, SyncAuthorityOperationKind.CreateRemote,
                         SyncAuthorityOperationKind.UpdateLocal, SyncAuthorityOperationKind.UpdateRemote })
            {
                var legacy = MainWindow.RetainedActivityRow(new SyncAuthorityActivityProjection(
                    DateTimeOffset.UtcNow, googlePair.Id, "sync-check.txt", "Completed", "Retained legacy event",
                    "legacy-operation", ItemKind: "File", FileType: "TXT", OperationKind: kind,
                    Phase: SyncActivityPhase.Completed));
                Check(legacy.HumanAction == "Transfer · Completed" && legacy.PairName == "Folder pair" &&
                      legacy.PairId == googlePair.Id && legacy.Connection == "Not recorded" &&
                      legacy.ConnectionId is null && legacy.RecordedCloudProvider is null &&
                      legacy.EndpointPresentation is { IsNeutral: true },
                    "Legacy " + kind + " keeps its known transfer and exact identity without inventing historical metadata");
                Call(window, "InspectActivity", legacy); Drain(); window.UpdateLayout();
                Check(Field<TextBlock>(window, "_detailSubtitle").Text == "Transfer · Completed",
                    "The production details sidebar renders the neutral retained transfer");
            }

            // Recorded legacy connection identities remain supported, but missing historical account
            // evidence must never borrow the current pair's account.
            var googleRecorded = Row("docs/report.pdf", "Uploaded", google.ConnectionId, googlePair.Id, "/Google/Paired");
            var googlePairBound = Row("docs/report.pdf", "Uploaded", google.ConnectionId, googlePair.Id, "/Google/Paired");
            var dropboxPairBound = Row("manual.pdf", "Uploaded", dropbox.ConnectionId, dropboxPair.Id, "/Dropbox/Paired");
            var unrecorded = Row("archive/legacy.pdf", "Uploaded", null, "pa-missing-history-pair", "/Legacy/Retained");

            Check(window.ActivityCloudName(googleRecorded) == "Google Drive",
                "A row with a recorded Google connection id is named Google Drive");
            Check(window.ActivityCloudName(googlePairBound) == "Google Drive",
                "A Google activity row keeps its recorded connection identity");
            Check(window.ActivityCloudName(dropboxPairBound) == "Dropbox",
                "A Dropbox activity row keeps its recorded connection identity");
            Check(window.ActivityCloudName(googlePairBound with { ConnectionId = null }) == "Cloud" &&
                  window.ActivityCloudName(dropboxPairBound with { ConnectionId = null }) == "Cloud",
                "Missing historical account evidence never borrows a current pair binding");
            Check(window.ActivityCloudName(unrecorded) == "Cloud",
                "A row with no recorded provider evidence keeps the neutral Cloud label and is never labelled Dropbox");
            Check(window.ActivityCloudName(Row("odd.pdf", "Uploaded", "gdrive-connection-NOPE", "pa-missing-history-pair")) == "Cloud" &&
                  window.ActivityCloudName(Row("odd.pdf", "Uploaded", "connection-" + new string('z', 24), "pa-missing-history-pair")) == "Cloud",
                "A connection id outside the exact provider identity rule is not accepted as Google Drive or Dropbox");
            Check(window.ActivityCloudName(Row("odd.pdf", "Uploaded", "connection-" + new string('a', 24), "pa-missing-history-pair")) == "Dropbox",
                "An exact Dropbox connection id still resolves to Dropbox without a stored binding");

            // The real details sidebar: the inspected Google row must name Google Drive everywhere,
            // including the recorded cloud location field and the described action.
            Call(window, "InspectActivity", googlePairBound); Drain(); window.UpdateLayout();
            Check(Field<TextBlock>(window, "_detailSubtitle").Text == "Uploaded to Google Drive",
                "The inspected Google row describes the upload with the resolved provider");
            Check(FieldHeadings(window).Contains("Recorded Google Drive Location:") &&
                  FieldHeadings(window).All(heading => !heading.Contains("Dropbox", StringComparison.Ordinal)),
                "The details sidebar records the Google Drive location and says nothing about Dropbox: " +
                string.Join(" | ", FieldHeadings(window)));
            Check(Field<Button>(window, "_inspectorCloud").ToolTip?.ToString() == "Open in Google Drive" &&
                  AutomationProperties.GetName(Field<Button>(window, "_inspectorCloud")) == "Open in Google Drive" &&
                  Field<Button>(window, "_inspectorLink").ToolTip?.ToString() == "Copy Google Drive Location Link" &&
                  AutomationProperties.GetName(Field<Button>(window, "_inspectorLink")) == "Copy Google Drive Location Link",
                "The inspector tooltips and accessibility names carry the resolved provider");
            var copied = Call(window, "ActivityDetails", googlePairBound)?.ToString() ?? string.Empty;
            Check(copied.Contains("Recorded Google Drive Location:", StringComparison.Ordinal) &&
                  !copied.Contains("Dropbox", StringComparison.Ordinal),
                "The copied details carry the resolved provider and never a fabricated Dropbox location");
            Check(Call(window, "CloudItemUrl", googlePairBound) is null &&
                  Field<Button>(window, "_inspectorCloud").IsEnabled &&
                  Field<Button>(window, "_inspectorLink").IsEnabled,
                "A Google Drive row uses its exact current account/root resolver, never a fabricated Dropbox URL");

            // The provider must follow the selected row: an unreleased provider label from a previous
            // selection is the exact stale-provider defect this correction removes.
            Call(window, "InspectActivity", dropboxPairBound); Drain(); window.UpdateLayout();
            Check(FieldHeadings(window).Contains("Recorded Dropbox Location:") &&
                  FieldHeadings(window).All(heading => !heading.Contains("Google", StringComparison.Ordinal)) &&
                  AutomationProperties.GetName(Field<Button>(window, "_inspectorCloud")) == "Open in Dropbox",
                "Selecting the Dropbox row replaces every Google Drive label: " + string.Join(" | ", FieldHeadings(window)));
            Check(Call(window, "CloudItemUrl", dropboxPairBound) is Uri uri &&
                  uri.AbsoluteUri.StartsWith("https://", StringComparison.Ordinal) &&
                  Field<Button>(window, "_inspectorCloud").IsEnabled,
                "The Dropbox row keeps its retained web location available");
            Call(window, "InspectActivity", googleRecorded); Drain(); window.UpdateLayout();
            Check(FieldHeadings(window).Contains("Recorded Google Drive Location:") &&
                  FieldHeadings(window).All(heading => !heading.Contains("Dropbox", StringComparison.Ordinal)) &&
                  AutomationProperties.GetName(Field<Button>(window, "_inspectorCloud")) == "Open in Google Drive",
                "Selecting the Google row again leaves no Dropbox label behind");

            // A missing historical account: the row keeps a neutral label and is never handed a
            // Dropbox location, while the retained path stays copyable.
            Call(window, "InspectActivity", unrecorded); Drain(); window.UpdateLayout();
            Check(FieldHeadings(window).Contains("Recorded Cloud Location:") &&
                  FieldHeadings(window).All(heading => !heading.Contains("Dropbox", StringComparison.Ordinal)) &&
                  Call(window, "CloudItemUrl", unrecorded) is null,
                "An unrecorded historical row keeps the neutral Cloud location and is never given a Dropbox location");

            Check(window.WithExactProviderBinding(googlePairBound).EventKey == googlePairBound.EventKey &&
                  window.ExactActivityConnectionId(null, googlePair.Id) is null &&
                  window.ExactActivityConnectionId(dropbox.ConnectionId, googlePair.Id) == dropbox.ConnectionId &&
                  window.ExactActivityConnectionId(null, "pa-missing-history-pair") is null,
                "Only recorded event account evidence is used and retained event identity stays unchanged");

            Check(window.WithExactProviderBinding(googlePairBound).CloudName == "Google Drive" &&
                  unrecorded.CloudName == "Cloud" && unrecorded.HumanAction == "Uploaded to Cloud" &&
                  unrecorded.Subtitle == "Uploaded to Cloud · Pair",
                "The activity record keeps exact provider wording and a neutral label for unrecorded history");

            var context = new SyncEndpointContext("historical-cloud-pair", "Historical pair",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.GoogleDrive, "local",
                    SyncProviders.GoogleDrive, google.ConnectionId, google.AccountKey, "history-left", "/Original left"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, dropbox.ConnectionId, dropbox.RootNamespaceId, "id:history-right", "/Original right"));
            var historical = Row("docs/report.pdf", "Copied", null, context.PairId) with
            {
                EndpointContext = context, PairName = context.CapturedName,
                OperationKind = SyncAuthorityOperationKind.CreateRemote, Phase = SyncActivityPhase.Completed
            };
            Check(historical.MatchesAccount(google.ConnectionId) && historical.MatchesAccount(dropbox.ConnectionId) &&
                  !historical.MatchesAccount("unrelated-account"), "Both recorded endpoint accounts can filter one historical event");
            Check(MainWindow.ActivityEndpointLocation(historical, context.Left) == "/Original left/docs/report.pdf" &&
                  MainWindow.ActivityEndpointLocation(historical, context.Right) == "/Original right/docs/report.pdf",
                "Each recorded endpoint retains its own location in left/right order");
            SetField(window, "_pairSet", new SyncPairSet([], null));
            Call(window, "InspectActivity", historical); Drain(); window.UpdateLayout();
            var headings = FieldHeadings(window);
            Check(headings.Contains("Left endpoint:") && headings.Contains("Right endpoint:") &&
                  headings.IndexOf("Left endpoint:") < headings.IndexOf("Right endpoint:"),
                "Real inspector renders both ordered endpoints after the current pair is gone");
            var endpointDetails = Call(window, "ActivityDetails", historical)?.ToString() ?? "";
            Check(endpointDetails.Contains("Left endpoint: Google Drive") && endpointDetails.Contains("Right endpoint: Dropbox") &&
                  endpointDetails.Contains(google.Email) && endpointDetails.Contains(dropbox.Email) &&
                  !endpointDetails.Contains(google.ConnectionId) && !endpointDetails.Contains(dropbox.ConnectionId),
                "Copied details retain both original providers and their exact recorded accounts, never raw connection ids, without consulting current pair state");
        }
        finally { window.Close(); }
        Console.WriteLine(passed + " dedicated activity provider checks passed; no provider call, credential, package " +
            "registration or product-state effect was performed.");
        return 0;
    }

    private static ActivityRow Row(string path, string outcome, string? connectionId, string? pairId, string? cloudRoot = null) =>
        new(DateTimeOffset.Now.ToString("O"), path, outcome, "Uploaded and verified.", "Account label",
            pairId, "Pair", "pa-operation", "File", "PDF", DropboxRoot: cloudRoot, ConnectionId: connectionId);

    private static SyncPair RetainedPair(SyncAuthorityDatabase authority, string stateRoot, string id, string name,
        string localFolder, string cloudRoot, string connectionId, string provider)
    {
        var localRoot = Path.Combine(stateRoot, localFolder);
        Directory.CreateDirectory(localRoot);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
        var pair = new SyncPair(id, name, localRoot, identity.VolumeSerialNumber, identity.FileId, cloudRoot,
            (provider == SyncProviders.Dropbox ? "id:" : "") + id + "-folder-id",
            ConnectionId: connectionId, Provider: provider);
        Pump(authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
        Pump(authority.RetainPairDefinitionAsync(pair, true));
        return pair;
    }

    // The production details sidebar builds one heading/value panel per recorded field, so the
    // headings are read from the panel the product rebuilt rather than from a rendered snapshot.
    private static List<string> FieldHeadings(MainWindow window) =>
        Field<StackPanel>(window, "_detailFields").Children.OfType<StackPanel>()
            .Select(panel => panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? string.Empty)
            .Where(text => text.Length > 0).ToList();

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task) { while (!task.IsCompleted) { Drain(); Thread.Sleep(1); } task.GetAwaiter().GetResult(); }

    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
}
