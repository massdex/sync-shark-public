using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// UI16 (P-REC) real-consumer checks on the actual MainWindow recovery surface.
//
// The recovery page, its rows, its projection, its details host, its view switch, its row menu and
// its refusal paths are driven through the production members the running application uses: the real
// bound RecoveryRows collection, the real RecoveryGrid selection handler, the real ApplyRecoveryFilter
// population path, the real RefreshRecoveryAsync page load, the real SetDetailedViewAsync preference
// write and the real RestoreSelectedRecoveryAsync guard.
//
// This file was written under the UI16 source-only hold and has NOT been built, run or rendered.
// No restore, remove, provider call, credential read or user-file effect is performed: the page fetch
// is replaced by RecoveryCatalogLoaderForTest only for the controlled completion-order cases, and
// every fixture record is constructed in memory.
internal static class RecoveryUiChecks
{
    private const string Selector = "--recovery-ui-checks";
    private const string GuardRestorable =
        "Restore original retains an occupied destination first; Restore as copy never replaces it.";
    private const string GuardDeleted =
        "Load revisions for this path to choose the exact version to restore.";
    private const string GuardBlocked = "This item cannot be restored until its health issue is resolved.";
    private const string RefusalDuringRun =
        "Wait for the current sync or recovery action to reach a durable boundary.";

    private const string PairA = "pair-recovery-a";
    private const string PairB = "pair-recovery-b";
    private const string AccountA = "alex@example.invalid";
    private const string AccountB = "blair@example.invalid";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(appLock.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1240, Height = 790,
            Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
        var pending = new List<TaskCompletionSource<SyncRecoveryCatalogPage>>();
        var requests = new List<(string PairId, bool IncludeDeleted)>();
        try
        {
            SeedPair(window, root, PairA, "Fixture Pair A", "/FixtureA", "fixture-account-a", select: true);
            SeedPair(window, root, PairB, "Fixture Pair B", "/FixtureB", "fixture-account-b", select: false);
            MainWindow.RecoveryCatalogLoaderForTest = (pairId, _, _, includeDeleted, _, _, _) =>
            {
                var source = new TaskCompletionSource<SyncRecoveryCatalogPage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(source);
                requests.Add((pairId, includeDeleted));
                return source.Task;
            };
            window.Show();
            Drain();
            Call(window, "NavigateTo", "Recovery", true);
            Drain();

            CheckEmptyState(window, pending, requests);
            CheckForeignPageRejected(window, pending);
            CheckPopulatedRows(window, pending, requests);
            CheckSelectionDetails(window);
            CheckContextualGuards(window, pending, requests);
            CheckSimpleDetailedSwitch(window, preferences, pending, requests);
            CheckExpandedDetails(window);
            CheckSupersededPage(window, pending);
            CheckAccountSwitch(window, pending, requests);
            CheckRestoreRefusedDuringRun(window);
            RewindHandoffChecks.Run(Path.Combine(root, "rewind-handoff"), theme);
            Console.WriteLine("PASS " + Selector +
                "; real-consumer recovery presentation checks only, no restore, remove, provider, credential" +
                " or user-file effect");
            return 0;
        }
        finally
        {
            MainWindow.RecoveryCatalogLoaderForTest = null;
            ReleasePending(window, pending);
            Set(window, "_uiRunActive", false);
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    private static void SeedPair(MainWindow window, string root, string id, string name, string dropboxRoot,
        string connectionId, bool select)
    {
        var local = Path.Combine(root, "local-" + id);
        Directory.CreateDirectory(local);
        Pump(window.AuthorityHost.LoadPairsAsync());
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair(id, name, local, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            dropboxRoot, "id:" + id, ConnectionId: connectionId);
        Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
        Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, select));
    }

    // --- UI16 empty state -------------------------------------------------------------------------

    private static void CheckEmptyState(MainWindow window, List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending,
        List<(string PairId, bool IncludeDeleted)> requests)
    {
        var request = NextRequest(pending, 0, "the first recovery page load");
        Require(requests[^1].PairId is PairA or PairB,
            "UI16 empty state: the first recovery load asks for one exact seeded pair (" + requests[^1].PairId + ")");
        request.SetResult(new SyncRecoveryCatalogPage([], 0, false, null, false));
        Drain();
        PumpUntil(() =>
        {
            // Initial navigation may coalesce a selector refresh. Supply the same
            // empty input to each initial request without altering the UI's gates.
            foreach (var initial in pending) initial.TrySetResult(new SyncRecoveryCatalogPage([], 0, false, null, false));
            return !Field<bool>(window, "_loadingRecovery");
        }, "the first recovery page to settle");
        Require(window.RecoveryRows.Count == 0, "UI16 empty state: a pair with no retained copy presents no rows");
        Require(window._recoveryEmptyPanel?.Visibility == Visibility.Visible,
            "UI16 empty state: the explanation panel is visible instead of an unexplained blank table");
        Require(Field<TextBlock>(window, "RecoveryDetails").Text == "No recovery item selected.",
            "UI16 empty state: the details host states that nothing is selected");
        Require(!Field<Button>(window, "RestoreOriginalButton").IsEnabled &&
                !Field<Button>(window, "RestoreCopyButton").IsEnabled &&
                !Field<Button>(window, "RemoveRecoveryButton").IsEnabled,
            "UI16 empty state: restore and remove are unavailable with no selection");
        Require(Field<ComboBox>(window, "RecoveryPairBox").SelectedItem is SyncRecoveryPairChoice,
            "UI16 empty state: the recovery pair selector is bound to a real retained pair choice");
    }

    private static void CheckForeignPageRejected(MainWindow window,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending)
    {
        var before = pending.Count;
        StartRefresh(window, pending, before, "foreign-page rejection load");
        var foreignPage = new SyncRecoveryCatalogPage(
            [CatalogItem("foreign", PairB, "Fixture Pair B", SyncRecoverySource.LocalArchive,
                "Docs/report.txt", AccountB, "/foreign", 1, Stamp(1), "retention", "Available", null, null, null, true)],
            1, false, null, false);
        PumpUntil(() =>
        {
            foreach (var request in pending.Skip(before)) request.TrySetResult(foreignPage);
            return !Field<bool>(window, "_loadingRecovery");
        }, "foreign-page rejection");
        Require(window.RecoveryRows.Count == 0 &&
            Field<TextBlock>(window, "RecoverySummary").Text.Contains("does not belong", StringComparison.Ordinal),
            "UI16 identity rejection: a different pair's catalog cannot populate this view");
    }

    // --- UI16 populated compact rows ---------------------------------------------------------------

    private static void CheckPopulatedRows(MainWindow window, List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending,
        List<(string PairId, bool IncludeDeleted)> requests)
    {
        var before = pending.Count;
        StartRefresh(window, pending, before, "the populated recovery page load");
        Require(requests[^1].PairId is PairA or PairB,
            "UI16 populated: the page is requested for the selected pair context (" + requests[^1].PairId + ")");
        pending[^1].SetResult(CatalogPage());
        Drain();
        try
        {
            PumpUntil(() =>
            {
                // Navigation can enqueue a coalesced refresh after the first page.
                // Complete that same-context input too; never leave the fake server
                // hanging while asserting the real consumer's loading state.
                for (var index = before; index < pending.Count; index++)
                {
                    if (requests[index].PairId != PairA)
                        throw new InvalidOperationException("Populated recovery requested a foreign pair.");
                    pending[index].TrySetResult(CatalogPage());
                }
                return window.RecoveryRows.Count == 4 && !Field<bool>(window, "_loadingRecovery");
            },
                "the populated recovery page to settle");
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"{exception.Message}; pair={CurrentPairId(window)}; " +
                $"rows={window.RecoveryRows.Count}; loading={Field<bool>(window, "_loadingRecovery")}; " +
                $"requests={requests.Count}; unfinished={pending.Count(item => !item.Task.IsCompleted)}; " +
                $"summary={Field<TextBlock>(window, "RecoverySummary").Text}", exception);
        }
        Require(window.RecoveryRows.Count == 4,
            "UI16 populated: the saved-copy tab presents the two archives, the missing-source copy and the" +
            " stale-target copy for the selected pair");
        Require(window._recoveryEmptyPanel?.Visibility != Visibility.Visible,
            "UI16 populated: the empty explanation is hidden while rows are presented");

        var duplicates = window.RecoveryRows.Where(row => row.DisplayName == "report.txt").ToArray();
        Require(duplicates.Length == 2,
            "UI16 populated: two retained versions of an identical file name present separate saved copies");
        Require(duplicates.All(row => !row.CompactSubtitle.Contains('\n') && row.Subtitle.Contains('\n') &&
                row.CompactSubtitle.Contains(row.AccountLabel) && row.CompactSubtitle.Contains(row.PairLabel) &&
                row.CompactSubtitle.Contains(row.Source) && row.CompactSubtitle.Contains(row.Health)),
            "Compact recovery rows keep all source facts on one line; full details retain their separate lines");
        var savedColumn = (DataGridTextColumn)Field<DataGrid>(window, "RecoveryGrid").Columns.Single(c => (string?)c.Header == "Saved");
        Require(((System.Windows.Data.Binding)savedColumn.Binding).Path.Path == nameof(RecoveryRow.RetainedDisplay) &&
            savedColumn.ElementStyle.Setters.OfType<Setter>().Any(s => s.Property == FrameworkElement.ToolTipProperty &&
                s.Value is System.Windows.Data.Binding binding && binding.Path.Path == nameof(RecoveryRow.Retained)),
            "Saved column binds the short local timestamp and keeps the full timestamp in its tooltip");
        Require(duplicates.Select(row => row.Connection).Distinct(StringComparer.Ordinal).Count() == 1 &&
                duplicates.All(row => row.Subtitle.Contains("Account: ", StringComparison.Ordinal)),
            "UI16 populated: both retained versions preserve the exact source account");
        Require(duplicates.All(row => row.Item.PairId == CurrentPairId(window)),
            "UI16 populated: no other pair's copy is shown inside this pair's recovery catalog");
        Require(window.RecoveryRows.Any(row => row.Source == "Local archive · File") &&
                window.RecoveryRows.Any(row => row.Source == "Dropbox Recovery · File"),
            "UI16 populated: local archive and canonical Dropbox Recovery copies both project their source");
        Require(window.RecoveryRows.Single(row => row.Item.Key == "local-a").Size
                    .EndsWith("KiB", StringComparison.Ordinal) &&
                window.RecoveryRows.Single(row => row.Item.Key == "missing-a").Size == "\u2014",
            "UI16 populated: a recorded size is formatted and a genuinely absent one keeps the existing" +
            " compact placeholder rather than an invented value");
    }

    // --- UI16 contextual details for the selected saved copy ---------------------------------------

    private static void CheckSelectionDetails(MainWindow window)
    {
        var grid = Field<DataGrid>(window, "RecoveryGrid");
        Require(ReferenceEquals(grid.ItemsSource, window.RecoveryRows),
            "UI16 details: the real recovery grid is bound to the presented rows collection");
        var local = window.RecoveryRows.Single(row => row.Item.Key == "local-a");
        grid.SelectedItem = local;
        Drain();
        var fields = RenderedFields(window);
        Require(fields.GetValueOrDefault("Source Account") == AccountA,
            "UI16 details: the selected saved copy states its exact source account");
        Require(fields.GetValueOrDefault("Folder Pair") == "Fixture Pair A",
            "UI16 details: the selected saved copy states its exact folder pair");
        Require(fields.GetValueOrDefault("Original Path") == "Docs/report.txt",
            "UI16 details: the selected saved copy states its exact pair-relative original path");
        Require(fields.GetValueOrDefault("Restore Destination") == "Docs/report.txt (pair-relative)",
            "UI16 details: the restore destination is stated as the exact pair-relative target");
        Require(fields.GetValueOrDefault("Saved Location") == "/local/archive/report.txt.1",
            "UI16 details: the saved copy states exactly where it is retained");
        Require(fields.GetValueOrDefault("Recovery Copy") == "local-a" &&
                fields.GetValueOrDefault("Saved Copy or Revision") == "Local archive · File",
            "UI16 details: the exact saved-copy identity and its kind are stated");
        Require(fields.GetValueOrDefault("Restore As Copy Destination")?.Contains("Not recorded",
                StringComparison.Ordinal) == true,
            "UI16 details: the as-copy destination is explicitly not recorded until preview proves it");
        Require(fields.GetValueOrDefault("Content Hash") == "sha256-local-a",
            "UI16 details: the recorded content hash is projected exactly");
        Require(fields.GetValueOrDefault(RecoveryRow.GuardLabel) == GuardRestorable,
            "UI16 details: the restorable guard sentence is unchanged");
        Require(Field<Button>(window, "RestoreOriginalButton").IsEnabled &&
                Field<Button>(window, "RestoreCopyButton").IsEnabled &&
                !Field<Button>(window, "RemoveRecoveryButton").IsEnabled,
            "UI16 details: the restore actions follow CanRestore and removal follows CanRemove");
        Require(Field<TextBox>(window, "RecoveryPathBox").Text == "Docs/report.txt",
            "UI16 details: selecting a saved copy still offers its exact path for revision lookup");
        Require(Field<TextBlock>(window, "RecoveryDetails").Text.Contains("Source Account: " + AccountA,
                StringComparison.Ordinal),
            "UI16 details: the plain-text record carries the same contextual values");
        var menu = window.RecoveryRowMenu(local);
        Require(menu.Items.Count == 8 && menu.Items[6] is Separator,
            "UI16 details: the recovery row menu keeps its exact six actions and separator");
        Require(Action(menu, "Restore as Copy").IsEnabled && Action(menu, "Restore Original").IsEnabled &&
                !Action(menu, "Remove Saved Copy").IsEnabled &&
                Action(menu, "Load Dropbox Revisions").IsEnabled,
            "UI16 details: the row menu enablement follows CanRestore and CanRemove while idle");
        // The saved-copy row menu still copies exactly what the details state.
        var copied = new List<string>();
        MainWindow.ClipboardTextSetterForTest = copied.Add;
        try
        {
            Action(menu, "Copy Saved Location").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Action(menu, "Copy Details").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally { MainWindow.ClipboardTextSetterForTest = null; }
        Require(copied.Count == 2 && copied[0] == "/local/archive/report.txt.1" &&
                copied[1].Contains("Version Identity: ", StringComparison.Ordinal) &&
                copied[1].Contains("Restore Guard: " + GuardRestorable, StringComparison.Ordinal),
            "UI16 details: copying from the row menu uses the same projected saved location and details");
    }

    // --- UI16 missing source, stale target and the history tab ------------------------------------

    private static void CheckContextualGuards(MainWindow window,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending, List<(string PairId, bool IncludeDeleted)> requests)
    {
        var grid = Field<DataGrid>(window, "RecoveryGrid");
        var missing = window.RecoveryRows.Single(row => row.Item.Key == "missing-a");
        grid.SelectedItem = missing;
        Drain();
        var fields = RenderedFields(window);
        Require(fields.GetValueOrDefault("Version Identity") == RecoveryRow.NotRecorded &&
                fields.GetValueOrDefault("Content Hash") == RecoveryRow.NotRecorded &&
                fields.GetValueOrDefault("Saved") == RecoveryRow.NotRecorded &&
                fields.GetValueOrDefault("Size") == RecoveryRow.NotRecorded &&
                fields.GetValueOrDefault("Saved Location") == RecoveryRow.NotRecorded,
            "UI16 missing source: every value the model genuinely lacks keeps an explicit Not recorded marker");
        Require(fields.GetValueOrDefault("Health") == "Missing: the retained source copy is no longer present",
            "UI16 missing source: the recorded health is projected exactly and not softened");
        Require(fields.GetValueOrDefault(RecoveryRow.GuardLabel) == GuardBlocked,
            "UI16 missing source: the unresolvable guard sentence is unchanged");
        Require(!Field<Button>(window, "RestoreOriginalButton").IsEnabled &&
                !Field<Button>(window, "RestoreCopyButton").IsEnabled &&
                Field<Button>(window, "RemoveRecoveryButton").IsEnabled,
            "UI16 missing source: restore is refused while removal of the unusable copy stays possible");

        var stale = window.RecoveryRows.Single(row => row.Item.Key == "stale-a");
        grid.SelectedItem = stale;
        Drain();
        fields = RenderedFields(window);
        Require(fields.GetValueOrDefault("Restore Destination") == "Docs/moved.bin (pair-relative)" &&
                fields.GetValueOrDefault("Health") ==
                "Blocked: this pair no longer has a bound Dropbox Recovery folder",
            "UI16 stale target: the recorded destination and the exact blocking reason are both stated");
        Require(fields.GetValueOrDefault(RecoveryRow.GuardLabel) == GuardBlocked,
            "UI16 stale target: the blocked guard sentence is unchanged");
        Require(!Field<Button>(window, "RestoreOriginalButton").IsEnabled &&
                !Field<Button>(window, "RestoreCopyButton").IsEnabled &&
                !Field<Button>(window, "RemoveRecoveryButton").IsEnabled,
            "UI16 stale target: a saved copy whose target is gone cannot be restored or removed from here");

        var revisions = Field<TabControl>(window, "_recoveryTabs");
        var before = pending.Count;
        Console.WriteLine("UI16 history ordering before tab switch: " +
            RecoveryHistoryDiagnostics(window, pending, requests));
        revisions.SelectionChanged += ObserveHistorySelection;
        try
        {
            revisions.SelectedIndex = 1;
            Drain();
            Console.WriteLine("UI16 history ordering after tab switch: " +
                RecoveryHistoryDiagnostics(window, pending, requests));
            try { NextRequest(pending, before, "the Dropbox History page load"); }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(exception.Message + "; " +
                    RecoveryHistoryDiagnostics(window, pending, requests), exception);
            }
        }
        finally { revisions.SelectionChanged -= ObserveHistorySelection; }

        void ObserveHistorySelection(object sender, SelectionChangedEventArgs e) =>
            Console.WriteLine("UI16 history selection event: source=" + e.Source.GetType().Name +
                " original=" + e.OriginalSource.GetType().Name + " sameTabSource=" +
                ReferenceEquals(e.Source, revisions) + "; " +
                RecoveryHistoryDiagnostics(window, pending, requests));
        Require(requests[^1].IncludeDeleted,
            "UI16 history tab: opening Dropbox History asks for the deleted-item metadata as well");
        pending[^1].SetResult(CatalogPage());
        Drain();
        PumpUntil(() => window.RecoveryRows.Count == 2 && !Field<bool>(window, "_loadingRecovery"),
            "the Dropbox History page to settle");
        Require(window.RecoveryRows.All(row => row.Item.Source is SyncRecoverySource.DropboxRevision
                or SyncRecoverySource.DropboxDeleted),
            "UI16 history tab: the Dropbox History tab presents only revisions and deleted items");
        var revision = window.RecoveryRows.Single(row => row.Item.Key == "rev-a");
        grid.SelectedItem = revision;
        Drain();
        fields = RenderedFields(window);
        Require(fields.GetValueOrDefault("Version Identity") == "Revision rev-exact-1 · Identity id-file-a",
            "UI16 version identity: the exact Dropbox revision and file identity are both stated");
        Require(fields.GetValueOrDefault("Restore Destination") == "Docs/report.txt (pair-relative)" &&
                Field<Button>(window, "RestoreOriginalButton").IsEnabled,
            "UI16 version identity: an exact revision keeps its destination and stays restorable");
        var deleted = window.RecoveryRows.Single(row => row.Item.Key == "deleted-a");
        grid.SelectedItem = deleted;
        Drain();
        fields = RenderedFields(window);
        Require(fields.GetValueOrDefault(RecoveryRow.GuardLabel) == GuardDeleted,
            "UI16 deleted item: the load-revisions guard sentence is unchanged");
        Require(!Field<Button>(window, "RestoreOriginalButton").IsEnabled,
            "UI16 deleted item: an item without an exact revision cannot be restored yet");

        before = pending.Count;
        revisions.SelectedIndex = 0;
        Drain();
        NextRequest(pending, before, "the return to the saved-copy page");
        pending[^1].SetResult(CatalogPage());
        Drain();
        PumpUntil(() => window.RecoveryRows.Count == 4 && !Field<bool>(window, "_loadingRecovery"),
            "the saved-copy page to return");
        Require(window.RecoveryRows.Select(row => row.Item.Key).Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "cloud-b", "local-a", "missing-a", "stale-a" }),
            "UI16 history tab: returning to Saved Copies restores the archive rows in the same filtered view");

        var restorable = CatalogItem("guard-ok", PairA, "Fixture Pair A", SyncRecoverySource.LocalArchive,
            "Docs/ok.txt", AccountA, "/local/ok.txt", 1, Stamp(1), "retention", "Available", "hash", null, null, true);
        var deletedShape = CatalogItem("guard-deleted", PairA, "Fixture Pair A", SyncRecoverySource.DropboxDeleted,
            "Docs/gone.txt", AccountA, "/FixtureA", null, null, "retention", "health", null, null, null, false);
        var blockedShape = CatalogItem("guard-blocked", PairA, "Fixture Pair A", SyncRecoverySource.LocalArchive,
            "Docs/gone.txt", AccountA, "/local/gone.txt", 1, Stamp(2), "retention", "Missing source", "hash", null,
            null, false);
        Require(RecoveryRow.Guard(restorable) == GuardRestorable &&
                RecoveryRow.Guard(deletedShape) == GuardDeleted &&
                RecoveryRow.Guard(blockedShape) == GuardBlocked,
            "UI16 guards: all three preview, restore and uncertainty guards keep their exact previous wording");
    }

    // --- UI16 shared Simple/Detailed switch --------------------------------------------------------

    private static void CheckSimpleDetailedSwitch(MainWindow window, UiPreferencesStore preferences,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending, List<(string PairId, bool IncludeDeleted)> requests)
    {
        var grid = Field<DataGrid>(window, "RecoveryGrid");
        var search = Field<TextBox>(window, "RecoverySearchBox");
        var selected = window.RecoveryRows.Single(row => row.Item.Key == "cloud-b");
        grid.SelectedItem = selected;
        Drain();
        var compactColumns = grid.Columns.Count;
        Require(window._recoveryViewSwitch is { PageKey: "recovery" },
            "UI16 view switch: the recovery page carries the shared switch for its own persisted page key");
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.None &&
                grid.RowHeight == SharedPresentation.CompactRowHeight,
            "UI16 view switch: the compact presentation hides headings and uses the shared compact density");

        window._recoveryViewSwitch!.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        PumpUntil(() => grid.Columns.Count == compactColumns + 6,
            "the detailed recovery columns to appear");
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.Column &&
                grid.RowHeight == SharedPresentation.DetailedRowHeight,
            "UI16 view switch: Detailed shows the headings and the shared detailed density");
        var headers = grid.Columns.Select(column => column.Header?.ToString()).ToArray();
        Require(headers.Contains("Version") && headers.Contains("Account") && headers.Contains("Saved Location") &&
                headers.Contains("Retention") && headers.Contains("Health"),
            "UI16 view switch: Detailed adds the account, saved-location, version, retention and health columns");
        Require(ReferenceEquals(grid.SelectedItem, selected) && window.RecoveryRows.Count == 4,
            "UI16 view switch: switching to Detailed keeps the same rows and the same selected saved copy");
        PumpUntil(() => preferences.Load().PageViewModes?.GetValueOrDefault("recovery") ==
                UiPreferencesStore.DetailedView, "the recovery view mode to be persisted");
        Require(preferences.Load().PageViewModes?.GetValueOrDefault("recovery") == UiPreferencesStore.DetailedView,
            "UI16 view switch: the recovery mode is remembered per page in the existing preference store");

        // UI398-1: the typed search arms the real 350 ms production debounce, and that debounce stops its
        // own timer when it ticks, so the baseline for the arriving request must be taken BEFORE the text
        // is written. Taken after the filter assertion and the two presentation toggles below, the baseline
        // could already contain the debounced request and the wait would then have nothing left to observe.
        // The production path, the real 350 ms interval, the next-request wait and every assertion are
        // unchanged; the two bounded captures record the ordering instead of inferring it from wall time.
        var before = pending.Count;
        var beforeAction = RecoverySearchDiagnostics(window, pending, "before the search write");
        search.Text = "report.txt";
        Drain();
        Require(window.RecoveryRows.Count == 2,
            "UI16 view switch: the typed filter narrows the detailed rows without changing the source data");
        var beforeWait = RecoverySearchDiagnostics(window, pending, "before the next-request wait");
        Require(beforeAction.Pending == before &&
                (beforeWait.TimerEnabled || beforeWait.Pending > beforeAction.Pending),
            "UI398-1 debounce ordering: the baseline precedes the search write, and that write reaches the " +
            "production filter path that arms the real debounce or its already-fired request, so no earlier " +
            "request can be mistaken for the reload this check waits for (" + beforeAction.Describe() +
            " -> " + beforeWait.Describe() + ")");
        window._recoveryViewSwitch.SimpleSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        PumpUntil(() => grid.Columns.Count == compactColumns, "the detailed recovery columns to be removed");
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.None &&
                grid.RowHeight == SharedPresentation.CompactRowHeight,
            "UI16 view switch: returning to Simple removes the detailed headings and density");
        Require(!grid.Columns.Select(column => column.Header?.ToString()).Contains("Version"),
            "UI16 view switch: returning to Simple removes the detailed columns without replacing the rows");
        Require(ReferenceEquals(grid.SelectedItem, selected) && search.Text == "report.txt",
            "UI16 view switch: the selected saved copy and the typed filter both survive the switch back");

        // The debounced recovery search reload is drained so the next checks start from one known page.
        NextRequest(pending, before, "the debounced recovery search reload (" + beforeAction.Describe() + ")");
        pending[^1].SetResult(CatalogPage());
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRecovery"), "the debounced recovery search reload to settle");
        Require(window.RecoveryRows.Count == 2,
            "UI16 view switch: the debounced reload re-applies the same typed filter to the reloaded page");
        before = pending.Count;
        search.Clear();
        Drain();
        NextRequest(pending, before, "the cleared recovery search reload");
        pending[^1].SetResult(CatalogPage());
        Drain();
        PumpUntil(() => window.RecoveryRows.Count == 4 && !Field<bool>(window, "_loadingRecovery"),
            "the cleared recovery search reload to settle");
        Require(window.RecoveryRows.Count == 4,
            "UI16 view switch: clearing the filter restores every retained copy without a stale page");
    }

    // --- UI16 shared details host and expanded dialog ----------------------------------------------

    private static void CheckExpandedDetails(MainWindow window)
    {
        var host = window._recoveryDetailsHost;
        Require(host is not null, "UI16 expanded dialog: the recovery page owns the shared details host");
        var details = host!;
        var grid = Field<DataGrid>(window, "RecoveryGrid");
        var local = window.RecoveryRows.Single(row => row.Item.Key == "local-a");
        grid.SelectedItem = local;
        Drain();
        var content = details.Content;
        Require(RenderedFields(window).GetValueOrDefault("Source Account") == AccountA,
            "UI16 expanded dialog: the compact sidebar shows the selected copy before expanding");
        details.Expand(window);
        Drain();
        Require(details.Expanded && details.Dialog is { IsVisible: true },
            "UI16 expanded dialog: expanding opens exactly one details dialog for the selected copy");
        Require(ReferenceEquals(details.Dialog!.HostedContent, content),
            "UI16 expanded dialog: the dialog hosts the same details element as the sidebar, not a copy");
        Require(ReferenceEquals(grid.SelectedItem, local),
            "UI16 expanded dialog: expanding keeps the selected saved copy");
        var cloud = window.RecoveryRows.Single(row => row.Item.Key == "cloud-b");
        grid.SelectedItem = cloud;
        Drain();
        Require(RenderedFields(window).GetValueOrDefault("Recovery Copy") == "cloud-b" &&
                RenderedFields(window).GetValueOrDefault("Source Account") == AccountA,
            "UI16 expanded dialog: selecting another copy updates the same hosted details element");
        details.Collapse();
        Drain();
        Require(!details.Expanded && details.Dialog is null,
            "UI16 expanded dialog: closing returns the details element to the compact sidebar");
        Require(ReferenceEquals(grid.SelectedItem, cloud),
            "UI16 expanded dialog: returning from the dialog keeps the selected saved copy");
        Require(RenderedFields(window).GetValueOrDefault("Recovery Copy") == "cloud-b",
            "UI16 expanded dialog: the returned sidebar still shows the selected copy's exact values");
    }

    // --- UI16 superseded page (cancellation) -------------------------------------------------------

    private static void CheckSupersededPage(MainWindow window,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending)
    {
        var pairId = CurrentPairId(window);
        var before = pending.Count;
        var first = (Task)Call(window, "LoadRecoveryPageAsync", true, false)!;
        PumpUntil(() => pending.Count == before + 1, "the first controlled recovery load to start");
        var firstPage = pending[^1];
        // Exercise the real serial refresh queue without clearing its production guard.
        var versionBefore = Field<int>(window, "_recoveryQueryVersion");
        var second = (Task)Call(window, "RefreshRecoveryAsync", false)!;
        Require(Field<int>(window, "_recoveryQueryVersion") == versionBefore + 1,
            "UI16 cancellation: a queued refresh invalidates the in-flight query generation");
        Require(pending.Count == before + 1, "UI16 cancellation: queued refresh does not start a parallel page");
        firstPage.SetResult(new SyncRecoveryCatalogPage(
            [CatalogItem("stale-superseded", pairId, "Fixture Pair A", SyncRecoverySource.LocalArchive,
                "Docs/superseded.txt", AccountA, "/local/superseded.txt", 22, Stamp(2), "retention", "Available",
                "sha256-stale", null, null, true)], 1, false, null, false));
        PumpUntil(() => pending.Count == before + 2, "the queued recovery load to start");
        Require(window.RecoveryRows.All(row => row.Item.Key != "stale-superseded"),
            "UI16 cancellation: superseded output is never shown");
        var secondPage = pending[^1];

        secondPage.SetResult(new SyncRecoveryCatalogPage(
            [CatalogItem("current-a", pairId, "Fixture Pair A", SyncRecoverySource.LocalArchive, "Docs/current.txt",
                AccountA, "/local/current.txt", 11, Stamp(1), "retention", "Available", "sha256-current", null, null,
                true)], 1, false, null, false));
        Drain();
        PumpUntil(() => window.RecoveryRows.Count == 1, "the superseding recovery page to be applied");
        Require(window.RecoveryRows.Single().Item.Key == "current-a",
            "UI16 cancellation: the newest recovery page is the one presented");

        Pump(first);
        Pump(second);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRecovery"), "the superseded load to finish");
        Require(window.RecoveryRows.Count == 1 && window.RecoveryRows.Single().Item.Key == "current-a",
            "UI16 cancellation: the superseded page never reaches the list after it completes out of order");
        Require(Field<TextBlock>(window, "RecoverySummary").Text.Contains("Local Copies: 1", StringComparison.Ordinal),
            "UI16 cancellation: the summary still describes the page that was actually applied");
        Require(!Field<bool>(window, "_recoveryReloadPending"),
            "UI16 cancellation: the superseded request leaves no pending reload behind");
    }

    // --- UI16 account/pair switching ---------------------------------------------------------------

    private static void CheckAccountSwitch(MainWindow window,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending, List<(string PairId, bool IncludeDeleted)> requests)
    {
        var grid = Field<DataGrid>(window, "RecoveryGrid");
        var selector = Field<ComboBox>(window, "RecoveryPairBox");
        var selection = window.RecoveryRows.Single();
        grid.SelectedItem = selection;
        Drain();
        Require(Field<TextBox>(window, "RecoveryPathBox").Text == "Docs/current.txt",
            "UI16 account switching: the first pair's selected copy is present before the switch");

        var before = pending.Count;
        var requestForA = (Task)Call(window, "LoadRecoveryPageAsync", true, false)!;
        PumpUntil(() => pending.Count == before + 1, "the pair A recovery load to start");
        var pageForA = pending[^1];
        selector.SelectedValue = PairB;
        Drain();
        Require(Field<bool>(window, "_recoveryReloadPending"),
            "UI16 account switching: a pair change during a load is remembered instead of dropped");
        Require(grid.SelectedItem is null && Field<TextBox>(window, "RecoveryPathBox").Text.Length == 0,
            "UI16 account switching: changing the pair discards the stale selected copy and its path");
        Require(Field<TextBlock>(window, "RecoveryDetails").Text == "No recovery item selected.",
            "UI16 account switching: the details host no longer describes the previous pair's copy");

        pageForA.SetResult(CatalogPage());
        Drain();
        PumpUntil(() => pending.Count == before + 2, "the pair B page request to start");
        var pageForB = pending[^1];
        Require(requests[^1] == (PairB, false),
            "UI16 account switching: the follow-up load runs for the exact new pair");
        pageForB.SetResult(new SyncRecoveryCatalogPage(
            [CatalogItem("cloud-b-only", PairB, "Fixture Pair B", SyncRecoverySource.DropboxArchive, "Docs/report.txt",
                AccountB, "/Apps/Sync Shark/Recovery/report.txt", 2048, Stamp(3), "retention",
                "Recorded; stable Dropbox identity is verified again before restore", "sha256-cloud-b", "id:cloud-b",
                "rev-cloud-b", true)], 1, false, null, false));
        Drain();
        PumpUntil(() => window.RecoveryRows.Count == 1 && !Field<bool>(window, "_loadingRecovery"),
            "the pair B page to be applied");
        Pump(requestForA);
        Require(window.RecoveryRows.Single().Item.Key == "cloud-b-only" &&
                window.RecoveryRows.All(row => row.Item.PairId == PairB),
            "UI16 account switching: the previous pair's page never populated the new pair's list");
        Require(grid.SelectedItem is null,
            "UI16 account switching: no stale row remains selected after the switch");
        Require(RenderedFields(window).GetValueOrDefault("Recovery Copy") == RecoveryRow.NotRecorded ||
                RenderedFields(window).Count == 0,
            "UI16 account switching: the details host carries no previous copy after the switch");
    }

    // --- UI16 refusal while a run is active --------------------------------------------------------

    private static void CheckRestoreRefusedDuringRun(MainWindow window)
    {
        var grid = Field<DataGrid>(window, "RecoveryGrid");
        var row = window.RecoveryRows.Single(item => item.Item.CanRestore);
        grid.SelectedItem = row;
        Drain();
        var menu = window.RecoveryRowMenu(row);
        Require(menu.Items.Count == 8 && menu.Items[6] is Separator,
            "UI16 run refusal: the recovery row menu keeps its six actions and its separator");
        Set(window, "_uiRunActive", true);
        menu = window.RecoveryRowMenu(row);
        Require(!Action(menu, "Restore as Copy").IsEnabled && !Action(menu, "Restore Original").IsEnabled &&
                !Action(menu, "Remove Saved Copy").IsEnabled && !Action(menu, "Load Dropbox Revisions").IsEnabled,
            "UI16 run refusal: every changing action is refused in the context menu while a run is active");
        Require(Action(menu, "Copy Relative Path").IsEnabled && Action(menu, "Copy Details").IsEnabled,
            "UI16 run refusal: read-only row actions stay available while a run is active");
        Call(window, "RestoreCopy_Click", window, new RoutedEventArgs());
        Drain();
        Require(Field<TextBlock>(window, "RecoverySummary").Text == RefusalDuringRun,
            "UI16 run refusal: invoking restore during a run reports the existing durable-boundary refusal");
        Require(ReferenceEquals(grid.SelectedItem, row) && window.RecoveryRows.Count == 1,
            "UI16 run refusal: the refused restore changed no row and no selection");
        Call(window, "RemoveRecovery_Click", window, new RoutedEventArgs());
        Drain();
        Require(window.RecoveryRows.Count == 1 &&
                Field<TextBlock>(window, "RecoverySummary").Text == RefusalDuringRun,
            "UI16 run refusal: removal during a run leaves every row untouched and opens no confirmation");
        Set(window, "_uiRunActive", false);
        menu = window.RecoveryRowMenu(row);
        Require(Action(menu, "Restore as Copy").IsEnabled && Action(menu, "Restore Original").IsEnabled &&
                !Action(menu, "Remove Saved Copy").IsEnabled,
            "UI16 run refusal: the same copy is restorable again once the run reaches its boundary");
    }

    // --- fixture records ---------------------------------------------------------------------------

    private static SyncRecoveryCatalogPage CatalogPage() => new(
        [
            CatalogItem("local-a", PairA, "Fixture Pair A", SyncRecoverySource.LocalArchive, "Docs/report.txt",
                AccountA, "/local/archive/report.txt.1", 2048, Stamp(4), "Protected until 2026-12-09; cleanup is manual",
                "Available from the local archive", "sha256-local-a", null, null, true),
            CatalogItem("cloud-b", PairA, "Fixture Pair A", SyncRecoverySource.DropboxArchive, "Docs/report.txt",
                AccountA, "/Apps/Sync Shark/Recovery/report.txt", 2048, Stamp(5),
                "Protected until 2026-12-09; cleanup is manual",
                "Recorded; stable Dropbox identity is verified again before restore", "sha256-cloud-b", "id:cloud-b",
                "rev-cloud-b", true),
            CatalogItem("missing-a", PairA, "Fixture Pair A", SyncRecoverySource.LocalArchive, "Docs/gone.bin",
                AccountA, string.Empty, null, null, "Kept until you remove it",
                "Missing: the retained source copy is no longer present", null, null, null, false, canRemove: true),
            CatalogItem("stale-a", PairA, "Fixture Pair A", SyncRecoverySource.DropboxArchive, "Docs/moved.bin",
                AccountA, "/Apps/Sync Shark/Recovery/moved.bin", 1024, Stamp(6), "Kept until you remove it",
                "Blocked: this pair no longer has a bound Dropbox Recovery folder", null, null, null, false),
            CatalogItem("rev-a", PairA, "Fixture Pair A", SyncRecoverySource.DropboxRevision, "Docs/report.txt",
                AccountA, "Dropbox revision rev-exact-1", 2048, Stamp(7),
                "Subject to the connected Dropbox account's revision history", "Available from Dropbox",
                "sha256-rev", "id-file-a", "rev-exact-1", true),
            CatalogItem("deleted-a", PairA, "Fixture Pair A", SyncRecoverySource.DropboxDeleted, "Docs/old.txt",
                AccountA, "/FixtureA", null, null, "Subject to the connected Dropbox account's revision history",
                "Load revisions to select an exact recoverable version", null, null, null, false)
        ],
        6, false, null, false);

    private static SyncRecoveryCatalogItem CatalogItem(string key, string pairId, string pairName,
        SyncRecoverySource source, string path, string connection, string location, long? size,
        DateTimeOffset? retained, string retention, string health, string? contentHash, string? identity,
        string? revision, bool canRestore, string objectKind = "file", bool canRemove = false) =>
        new(key, pairId, pairName, source, path, connection, location, size, retained, retention, health,
            contentHash, identity, revision, "{}", canRestore, objectKind, canRemove);

    private static DateTimeOffset Stamp(int days) => new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero).AddDays(days);

    // --- helpers -----------------------------------------------------------------------------------

    private static string CurrentPairId(MainWindow window) =>
        (Field<ComboBox>(window, "RecoveryPairBox").SelectedItem as SyncRecoveryPairChoice)?.Id ?? PairA;

    // Observe the actual tab handler and async-load state; do not retime, complete, or replace
    // any production decision while diagnosing a missing history request.
    private static string RecoveryHistoryDiagnostics(MainWindow window,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending,
        List<(string PairId, bool IncludeDeleted)> requests) =>
        "tab=" + Field<TabControl>(window, "_recoveryTabs").SelectedIndex +
        " ready=" + Field<bool>(window, "_fullAppReady") +
        " loading=" + Field<bool>(window, "_loadingRecovery") +
        " reload=" + Field<bool>(window, "_recoveryReloadPending") +
        " deletedRequested=" + Field<bool>(window, "_recoveryDeletedRequested") +
        " pending=" + pending.Count + " unfinished=" + pending.Count(item => !item.Task.IsCompleted) +
        " requests=" + requests.Count + " pair=" + CurrentPairId(window) +
        " page=" + Field<Border>(window, "RecoveryPage").Visibility +
        " summary=" + Field<TextBlock>(window, "RecoverySummary").Text.Replace('\n', ' ');

    private static void StartRefresh(MainWindow window, List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending,
        int before, string label)
    {
        Call(window, "RefreshRecoveryAsync", false);
        PumpUntil(() => pending.Count > before, label);
    }

    private static TaskCompletionSource<SyncRecoveryCatalogPage> NextRequest(
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending, int before, string label)
    {
        PumpUntil(() => pending.Count > before, label);
        return pending[^1];
    }

    // UI398-1: the bounded ordering evidence for the debounced recovery search. It reads only real
    // production state - the harness request list, the production 350 ms timer, the production loading
    // flag and the real recovery page's visibility - and prints it, so the ordering is recorded rather
    // than inferred. Nothing here sleeps, retimes the debounce or alters product logic.
    private sealed record RecoverySearchOrdering(int Pending, int Unfinished, bool TimerEnabled, bool Loading,
        string PageVisibility)
    {
        internal string Describe() => "pending=" + Pending + " unfinished=" + Unfinished +
            " timerEnabled=" + TimerEnabled + " loading=" + Loading + " recoveryPage=" + PageVisibility;
    }

    private static RecoverySearchOrdering RecoverySearchDiagnostics(MainWindow window,
        List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending, string point)
    {
        var observed = new RecoverySearchOrdering(pending.Count, pending.Count(item => !item.Task.IsCompleted),
            Field<DispatcherTimer>(window, "_recoverySearchTimer").IsEnabled,
            Field<bool>(window, "_loadingRecovery"),
            Field<Border>(window, "RecoveryPage").Visibility.ToString());
        Console.WriteLine("UI398-1 debounce ordering " + point + ": " + observed.Describe());
        return observed;
    }

    // The row menu labels its items with the shared command-label element, so the stable identity of
    // an action is the accessible name the production helper sets on the MenuItem itself.
    private static MenuItem Action(ContextMenu menu, string header) => menu.Items.OfType<MenuItem>()
        .Single(item => string.Equals(System.Windows.Automation.AutomationProperties.GetName(item), header,
            StringComparison.Ordinal));

    private static Dictionary<string, string> RenderedFields(MainWindow window)
    {
        var panel = Field<StackPanel>(window, "_recoveryDetailFields");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in panel.Children.OfType<StackPanel>())
        {
            var texts = field.Children.OfType<TextBlock>().ToArray();
            if (texts.Length >= 2) fields[texts[0].Text.TrimEnd(':')] = texts[1].Text;
        }
        return fields;
    }

    private static void ReleasePending(MainWindow window, List<TaskCompletionSource<SyncRecoveryCatalogPage>> pending)
    {
        var started = Environment.TickCount64;
        while (Field<bool>(window, "_loadingRecovery"))
        {
            foreach (var source in pending)
                source.TrySetResult(new SyncRecoveryCatalogPage([], 0, false, null, false));
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out releasing outstanding recovery loads");
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(1);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for a recovery task to complete");
        }
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, string label)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for " + label);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
