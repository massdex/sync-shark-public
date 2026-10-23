using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// UI17 real-consumer checks on the actual MainWindow pair-details Storage tab.
//
// The Storage tab, its single provider-neutral policy editor, the global Storage savings mappings,
// the exact pair and relative-path hand-off, the selection cap, the confirmation and the scope
// revalidation are all driven through the production members the running application uses:
// MainWindow.PairEditorTabs, MainWindow.PairStorageTabHost, the real PairList selection and
// ExpandPairRow, the real global "Manage Storage" button raising its real Click, the real
// OpenPairStorageAsync entry point, the real ApplyPairStorageChoiceAsync confirmation and dispatch
// path, the real authority overview contract and the real EnsureStorageOwnershipAvailable guard.
//
// Source-only packet: execution belongs to the parent fixture lane. No
// restore, provider call, credential read, signing, install or user-file effect is performed. The
// pair-scoped overview read, the authority mutation and the modal confirmation are replaced by the
// harness ONLY so the delayed-pair, cap, refusal and no-dispatch orderings can be completed
// deliberately; every decision those orderings depend on still runs in production code. The fixture
// pairs are written to this fixture's own retained authority and no other
// machine or product state is touched.
internal static class PairStorageUiChecks
{
    private const string Selector = "--pair-storage-ui-checks";
    private const string PairA = "pair-storage-a";
    private const string PairB = "pair-storage-b";
    private const string PairGoogle = "pair-storage-google";
    private const string PairForeign = "pair-storage-foreign";
    private const string AccountA = "fixture-account-a";
    private const string AccountB = "fixture-account-b";
    private const string GooglePermissionId = "fixture-storage-google";
    private static readonly string AccountGoogle = GoogleDriveConnectionBinding.CreateConnectionId(
        GoogleDriveConnectionBinding.CreateAccountKey(GooglePermissionId));
    // Disconnected accounts must remain distinguishable without inventing retained names/emails.
    private const string AccountLabelA = "Account Unavailable (" + AccountA + ")";
    private const string AccountLabelB = "Account Unavailable (" + AccountB + ")";
    private const string PairName = "Documents";
    private const string PathAOnly = "Plans/A-Only-Hold";
    private const string PathBOnly = "Plans/B-Only-Hold";
    private const string PathFilter = "Plans/Design";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(appLock.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        var harness = new Harness();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.PairStorageOverviewLoaderForTest = harness.Load;
        MainWindow.PairStorageConfirmationForTest = harness.Confirm;
        MainWindow.PairStorageChangeForTest = harness.Change;
        MainWindow.PairStorageFileResetForTest = harness.ResetFile;
        // The real unsaved-pair-draft prompt is a modal dialog; the fixture answers it so the existing
        // draft guard still runs for real instead of waiting on a user that is not there.
        var pairA = Pair(PairA, AccountA, 1, 1, "/Documents", "id:storage-a");
        var pairB = Pair(PairB, AccountB, 2, 2, "/Documents", "id:storage-b");
        var pairGoogle = Pair(PairGoogle, AccountGoogle, 3, 3, "/Shared", "google-folder-id-1");
        var pairForeign = Pair(PairForeign, AccountA, 4, 4, "/Foreign", "id:storage-foreign");
        harness.Pairs = new Dictionary<string, SyncPair>(StringComparer.Ordinal)
        {
            [PairA] = pairA, [PairB] = pairB, [PairGoogle] = pairGoogle, [PairForeign] = pairForeign
        };
        harness.Overviews[PairA] = Overview(pairA, [PathAOnly]);
        harness.Overviews[PairB] = Overview(pairB, [PathBOnly]);
        harness.Overviews[PairGoogle] = Overview(pairGoogle, [PathBOnly]);
        harness.Overviews[PairForeign] = Overview(pairForeign, [PathBOnly]);
        SavePairs(appLock.StateAuthority, pairA, pairB, pairGoogle, pairForeign);
        var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1240, Height = 790, Left = -32000, Top = -32000 };
        window.UnsavedNavigationChoiceForTest = () => AppDialogChoice.Secondary;
        harness.Window = window;
        try
        {
            Seed(window, pairA, pairB, pairGoogle, pairForeign);
            window.Show();
            Drain();

            CheckEditorPlacement(window);
            CheckGlobalHandOff(window, harness, pairB);
            CheckExactPairAndPathFilter(window, harness, pairA, pairB);
            CheckDisconnectedAccountLabels(window, pairA, pairB);
            CheckDelayedPairSwitch(window, harness);
            CheckUnsavedPairNoActions(window);
            CheckSupportedProviders(window, harness, pairB, pairGoogle);
            CheckProviderRefusal(window, harness, pairGoogle);
            CheckReadOnlyBrowsing(window, harness);
            CheckExcludedAndCap(window, harness, pairB);
            CheckConfirmationAndScopeChange(window, harness, pairA, pairB, pairGoogle);
            CheckGoogleConfirmationScope(window, harness, pairGoogle);
            CheckRunActiveRefusal(window, harness, pairB);
            CheckRunActiveRefusal(window, harness, pairGoogle);
            CheckFileOverrideReset(window, harness, pairB);
            CheckFileOverrideReset(window, harness, pairGoogle);
            CheckDetailedStorageFailures(window, harness, pairB);
            CheckOwnershipRefusal(window, harness, pairForeign, root);
            CheckFailureRetry(window, harness, pairB);
            // Last: retain a non-secret Google identity only after UI navigation has finished, so
            // capacity rendering cannot start a live/credential account-status read for this fixture.
            CheckRealGoogleOverviewLookup(window, harness, appLock.StateAuthority, pairGoogle);

            Console.WriteLine("PASS " + Selector +
                "; real-consumer pair Storage checks only, no provider, credential, signing, install or user-file" +
                " effect");
            return 0;
        }
        finally
        {
            MainWindow.PairStorageOverviewLoaderForTest = null;
            MainWindow.PairStorageConfirmationForTest = null;
            MainWindow.PairStorageChangeForTest = null;
            MainWindow.PairStorageFileResetForTest = null;
            window.UnsavedNavigationChoiceForTest = null;
            ReleasePending(harness);
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    // ---- checks ---------------------------------------------------------------------------------

    private static void CheckEditorPlacement(MainWindow window)
    {
        Require(window.PairEditorTabs.Items.Count == MainWindow.PairStorageTabIndex + 1,
            "Storage is the last pair-details tab, so the existing Folder/Settings/Sync status tab indexes are unchanged");
        var last = window.PairEditorTabs.Items[MainWindow.PairStorageTabIndex] as TabItem;
        Require(last is not null && string.Equals(last.Header?.ToString(), "Storage", StringComparison.Ordinal),
            "the last pair-details tab is the Storage tab");
        Require(window.PairStorageTabHost.Children.Count == 1,
            "the single relocated policy editor is mounted in the pair Storage tab host");
        var editor = window.PairStorageTabHost.Children[0] as Border;
        Require(editor is not null && FindDescendants<Button>(editor).Count() > 0,
            "the pair Storage editor carries its own refresh, filter and change controls");
        Require(editor is not null && FindDescendants<ComboBox>(editor).Count() == 1,
            "the pair Storage editor offers only its folder filter, with no account, drive or pair selector");
        Require(editor is not null && FindDescendants<CheckBox>(editor).Count() == 0,
            "the pair Storage editor shows no folder rows before a pair is selected");
        Require(Field<TextBlock>(window, "_pairStorageMessage").Text.Length == 0,
            "the pair Storage editor shows no stale message before a pair is selected");
    }

    private static void CheckGlobalHandOff(MainWindow window, Harness harness, SyncPair pairB)
    {
        harness.DelayLoads = false;
        Field<TextBox>(window, "_storageSearch").Text = "";
        Call(window, "NavigateTo", "Storage", true);
        Drain();
        PumpTask(CallTask(window, "LoadStoragePageAsync", true));
        var rows = Field<DataGrid>(window, "_storageRows");
        Require(FindDescendants<CheckBox>(rows).Count() == 0,
            "the global inventory has no policy editor");
        var loaded = rows.Items.OfType<StorageInventoryDisplayRow>().ToArray();
        Console.WriteLine("Storage fixture rows=" + loaded.Length + "; " + Field<TextBlock>(window, "_storageMessage").Text);
        var state = Field<ComboBox>(window, "_storageFilter");
        Require(Name(state) == "Storage State" && state.SelectedIndex == 0 &&
                state.Items.Cast<string>().SequenceEqual(new[] { "Online-only folders", "Selective sync" }),
            "global Storage defaults to online-only mappings with selective sync as its only alternate category");
        Require(loaded.Length == 3 && loaded.Single(row => row.Item.RelativePath == ".").Item.PairId == PairA &&
                loaded.Any(row => row.Item.PairId == PairB && row.Item.RelativePath == "Reports") &&
                loaded.Any(row => row.Item.PairId == PairGoogle && row.Item.RelativePath == "Reports"),
            "global Storage shows the online-only root and explicit Dropbox/Google mappings, not every saved root");
        Require(loaded.Any(row => row.AccountLabel == AccountLabelA) && loaded.Any(row => row.AccountLabel == AccountLabelB),
            "duplicate-named pairs retain exact account labels");
        var capacity = Text(Field<StackPanel>(window, "_storageDeviceSpace"));
        Require(capacity.Contains("available of", StringComparison.Ordinal) ||
                capacity.Contains("Device capacity unavailable", StringComparison.Ordinal),
            "global Storage renders device free/capacity metadata or an explicit unavailable result");
        Require(Field<TextBlock>(window, "_storageMessage").Text.Contains("not a fresh disk scan", StringComparison.Ordinal),
            "saved mappings are not misrepresented as a new filesystem scan");
        state.SelectedIndex = 1;
        Drain();
        var selective = rows.Items.OfType<StorageInventoryDisplayRow>().ToArray();
        Require(selective.Length == 3 && selective.All(row => row.Item.RelativePath == "Secret") &&
                selective.Select(row => row.Item.PairId).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(new[] { PairB, PairGoogle, PairForeign }),
            "the real category switch renders exact excluded-folder mappings across both providers");
        Require(harness.Changes.Count == 0 && harness.FileResets.Count == 0,
            "switching global savings categories performs no storage changes");
        state.SelectedIndex = 0;
        Drain();
        loaded = rows.Items.OfType<StorageInventoryDisplayRow>().ToArray();
        rows.SelectedItem = loaded.Single(row => row.Item.PairId == pairB.Id && row.Item.RelativePath == "Reports");
        Drain();
        var details = Field<StackPanel>(window, "_storageDetailFields");
        var button = FindDescendants<Button>(details).Single(item => Name(item) == "Manage Storage");
        button!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        PumpUntil(() => window.PairEditorTabs.SelectedIndex == MainWindow.PairStorageTabIndex,
            "the global Manage Storage hand-off to open the pair Storage tab");
        Require(ReferenceEquals(window.PairList.SelectedItem, window.PairRows.Single(item => item.Id == pairB.Id)),
            "opening storage from global Storage selects the exact pair, not its duplicate-named twin");
        Require(harness.Loads.Count > 0 && harness.Loads[^1].PairId == pairB.Id &&
            harness.Loads[^1].ConnectionId == AccountB && harness.Loads[^1].Search == "Reports",
            "the real global child-row hand-off reads the exact account, pair and relative folder path");
    }

    private static void CheckExactPairAndPathFilter(MainWindow window, Harness harness, SyncPair pairA, SyncPair pairB)
    {
        harness.DelayLoads = false;
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, PathFilter));
        Require(window.PairEditorTabs.SelectedIndex == MainWindow.PairStorageTabIndex,
            "the pair Storage tab is selected after the hand-off");
        Require(ReferenceEquals(window.PairList.SelectedItem, window.PairRows.Single(item => item.Id == pairB.Id)),
            "the exact saved pair is selected");
        Require(string.Equals(Field<string>(window, "_expandedPairId"), PairB, StringComparison.Ordinal),
            "the exact saved pair is the expanded row");
        Require(Field<TextBox>(window, "_pairStorageSearch").Text == PathFilter,
            "the relative path is applied as the pair Storage filter");
        Require(harness.Loads[^1].PairId == pairB.Id && harness.Loads[^1].Search == PathFilter,
            "the pair-scoped storage read carried the exact pair and relative path");
        var scope = Field<TextBlock>(window, "_pairStorageScope").Text;
        Require(scope.Contains(PairName, StringComparison.Ordinal) && scope.Contains(AccountLabelB, StringComparison.Ordinal),
            "the pair Storage editor names the exact pair and account it is editing");
        PumpTask(CallTask(window, "OpenPairStorageAsync", pairA.Id, @"..\..\Windows"));
        Require(Field<TextBox>(window, "_pairStorageSearch").Text.Length == 0,
            "an unsafe traversal path is ignored as a filter instead of being resolved");
        Require(harness.Loads[^1].Search.Length == 0, "the ignored traversal path never reaches the storage read");
        PumpTask(CallTask(window, "OpenPairStorageAsync", pairB.Id, PathFilter));
    }

    private static void CheckDelayedPairSwitch(MainWindow window, Harness harness)
    {
        harness.DelayLoads = true;
        harness.Pending.Clear();
        CallTask(window, "OpenPairStorageAsync", PairB, "");
        PumpUntil(() => harness.Pending.Count == 1, "the pair B storage read to start");
        harness.Pending[0].TrySetResult(harness.Overviews[PairB]);
        PumpUntil(() => Field<SyncStorageOverview?>(window, "_pairStorageOverview")?.Pair.Id == PairB, "pair B storage rows to render");
        Require(RowText(window).Contains(PathBOnly, StringComparison.Ordinal), "pair B's own folder path is rendered for pair B");

        CallTask(window, "OpenPairStorageAsync", PairA, "");
        PumpUntil(() => harness.Pending.Count == 2, "the pair A storage read to start");
        Require(Field<StackPanel>(window, "_pairStorageRows").Children.Count == 0,
            "the previous pair's rows, selection and actions are cleared immediately when the pair changes");
        Require(Field<SyncStorageOverview?>(window, "_pairStorageOverview") is null,
            "the previous pair's overview is dropped when the pair changes");

        CallTask(window, "OpenPairStorageAsync", PairB, "");
        PumpUntil(() => harness.Pending.Count == 3, "the second pair B storage read to start");
        harness.Pending[2].TrySetResult(harness.Overviews[PairB]);
        PumpUntil(() => RowText(window).Contains(PathBOnly, StringComparison.Ordinal), "pair B storage rows to render again");

        harness.Pending[1].TrySetResult(harness.Overviews[PairA]);
        Drain();
        Require(Field<SyncStorageOverview?>(window, "_pairStorageOverview")?.Pair.Id == PairB,
            "a late completion for the previous pair cannot replace the displayed pair");
        Require(!RowText(window).Contains(PathAOnly, StringComparison.Ordinal),
            "a late completion for the previous pair cannot display its rows or enable its actions");
        harness.DelayLoads = false;
        harness.Pending.Clear();
    }

    private static void CheckUnsavedPairNoActions(MainWindow window)
    {
        Set(window, "_editingNewPair", true);
        try
        {
            PumpTask(CallTask(window, "RefreshPairStorageTabAsync"));
            Require(Field<TextBlock>(window, "_pairStorageMessage").Text.Contains("Save this pair", StringComparison.Ordinal),
                "a new, unsaved pair is told to be saved before its storage can be managed");
            Require(Field<StackPanel>(window, "_pairStorageRows").Children.Count == 0,
                "an unsaved pair shows no actionable storage rows");
            Require(Field<SyncStorageOverview?>(window, "_pairStorageOverview") is null,
                "an unsaved pair never loads a storage policy");
        }
        finally { Set(window, "_editingNewPair", false); }
    }

    private static void CheckSupportedProviders(MainWindow window, Harness harness, SyncPair pairB, SyncPair pairGoogle)
    {
        var editor = window.PairStorageTabHost.Children[0];
        foreach (var pair in new[] { pairB, pairGoogle })
        {
            PumpTask(CallTask(window, "OpenPairStorageAsync", pair.Id, ""));
            Require(window.PairStorageTabHost.Children.Count == 1 &&
                    ReferenceEquals(editor, window.PairStorageTabHost.Children[0]),
                pair.Provider + " uses the same per-pair Storage editor instance");
            Require(harness.Loads[^1].PairId == pair.Id && harness.Loads[^1].ConnectionId == pair.ConnectionId &&
                    Field<SyncStorageOverview?>(window, "_pairStorageOverview")?.Pair == pair &&
                    RowText(window).Contains(PathBOnly, StringComparison.Ordinal),
                pair.Provider + " loads and renders the exact pair/account without a Dropbox-only capability refusal");

            harness.Changes.Clear();
            harness.FileResets.Clear();
            harness.Confirmations = 0;
            harness.ConfirmationResult = false;
            PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pair, new[] { PathFilter }, SyncDirectoryStorage.CloudOnly));
            PumpTask(CallTask(window, "ResetPairStorageFileChoiceAsync", pair, pair.FileStorageRules!.Single()));
            Require(harness.Confirmations == 2 && harness.Changes.Count == 0 && harness.FileResets.Count == 0,
                pair.Provider + " cancellation refuses both directory and file-override mutations");
            harness.ConfirmationResult = true;

            foreach (var storage in new[] { SyncDirectoryStorage.CloudOnly, SyncDirectoryStorage.AlwaysAvailable })
            {
                harness.Changes.Clear();
                PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pair, new[] { PathFilter }, storage));
                Require(harness.Changes.Count == 1 && harness.Changes[0].PairId == pair.Id &&
                        harness.Changes[0].ConnectionId == pair.ConnectionId && harness.Changes[0].Revision == pair.PolicyRevision &&
                        harness.Changes[0].Storage == storage && harness.Changes[0].Folders is { Count: 1 } paths && paths[0] == PathFilter,
                    pair.Provider + " dispatches the chosen availability once to the exact account/pair/path/policy revision");
            }
        }
        harness.Changes.Clear();
        harness.FileResets.Clear();
    }

    private static void CheckProviderRefusal(MainWindow window, Harness harness, SyncPair pairGoogle)
    {
        var unsupported = pairGoogle with { Provider = "fixture-unknown-provider" };
        var readsBefore = harness.Loads.Count;
        // Input at the real loader boundary: an unknown provider must fail before account/index
        // lookup, regardless of which supported pair is currently selected. Do not persist an
        // invalid provider definition just to reach that guard.
        PumpTask(CallTask(window, "LoadPairStorageAsync", unsupported, true));
        var message = Field<TextBlock>(window, "_pairStorageMessage").Text;
        Require(message.Contains(unsupported.Provider, StringComparison.Ordinal) && message.Contains("supported cloud pair", StringComparison.Ordinal),
            "unknown provider storage is refused explicitly instead of falling back to Dropbox");
        Require(!message.Contains("Reconnect", StringComparison.OrdinalIgnoreCase),
            "the unsupported-provider refusal does not imply that reconnecting an account would solve it");
        Require(harness.Loads.Count == readsBefore, "an unsupported provider never reaches the storage read");
        Require(Field<StackPanel>(window, "_pairStorageRows").Children.Count == 0,
            "an unsupported provider shows no actionable storage rows");

        harness.Changes.Clear();
        harness.Confirmations = 0;
        PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", unsupported, new[] { "Plans" }, SyncDirectoryStorage.CloudOnly));
        harness.FileResets.Clear();
        PumpTask(CallTask(window, "ResetPairStorageFileChoiceAsync", unsupported, pairGoogle.FileStorageRules!.Single()));
        Require(harness.Changes.Count == 0, "an unsupported provider cannot dispatch a storage change");
        Require(harness.FileResets.Count == 0, "an unsupported provider cannot dispatch a file override reset");
        Require(harness.Confirmations == 0, "an unsupported provider is refused before any confirmation is shown");
        Require(LastStatus(window).Contains(unsupported.Provider, StringComparison.Ordinal),
            "the refused write names the unsupported provider");
        Require(!LastStatus(window).Contains("Reconnect", StringComparison.OrdinalIgnoreCase),
            "the refused write does not imply reconnecting an unrelated Dropbox account would help");
        harness.Confirmations = 0;
        window.LoadPairSetForTest(new SyncPairSet(harness.Pairs.Values.ToArray(), PairB));
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
    }

    private static void CheckReadOnlyBrowsing(MainWindow window, Harness harness)
    {
        harness.Changes.Clear();
        harness.FileResets.Clear();
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
        PumpTask(CallTask(window, "ReloadCurrentPairStorageAsync"));
        Field<TextBox>(window, "_pairStorageSearch").Text = "Plans";
        PumpTask(CallTask(window, "ReloadCurrentPairStorageAsync"));
        Field<ComboBox>(window, "_pairStorageFilter").SelectedIndex = 1;
        Drain();
        PumpTask(CallTask(window, "LoadMorePairStorageAsync"));
        Field<ComboBox>(window, "_pairStorageFilter").SelectedIndex = 0;
        Drain();
        PumpTask(CallTask(window, "ReloadCurrentPairStorageAsync"));
        Require(harness.Changes.Count == 0 && harness.FileResets.Count == 0,
            "opening, refreshing, searching, filtering and loading more storage invokes zero storage writes");
        Require(!Field<bool>(window, "_changingPairStorage"),
            "read-only storage browsing never enters the changing-storage state");
        Require(Field<Button>(window, "_pairStorageRefresh").IsEnabled,
            "the pair Storage refresh control stays usable after browsing");
        Field<TextBox>(window, "_pairStorageSearch").Text = "";
    }

    private static void CheckExcludedAndCap(MainWindow window, Harness harness, SyncPair pairB)
    {
        var many = new List<SyncFolderSelectionNode> { Node("Secret"), Node("Ignored") };
        for (var index = 1; index <= 101; index++) many.Add(Node("F" + index.ToString("D3")));
        harness.Overviews[PairB] = Overview(pairB, many.Select(node => node.RelativePath).ToArray());
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
        var rows = Field<StackPanel>(window, "_pairStorageRows");
        var selectors = FindDescendants<CheckBox>(rows).Where(item => Name(item).StartsWith("Select ", StringComparison.Ordinal)).ToArray();
        Require(selectors.Length == many.Count, "every indexed folder is offered a storage selector");
        var excluded = selectors.SingleOrDefault(item => Name(item) == "Select Secret");
        var ignored = selectors.SingleOrDefault(item => Name(item) == "Select Ignored");
        Require(excluded is not null && !excluded.IsEnabled,
            "a folder excluded from sync cannot be selected for a storage change");
        Require(ignored is not null && !ignored.IsEnabled,
            "an ignored folder cannot be selected for a storage change");
        var eligible = selectors.Where(item => item.IsEnabled).ToArray();
        Require(eligible.Length == 101, "only participating folders remain selectable");
        foreach (var selector in eligible) selector.IsChecked = true;
        Drain();
        Require(Field<HashSet<string>>(window, "_pairStorageSelected").Count == 100,
            "one storage change is capped at 100 folders");
        Require(Field<TextBlock>(window, "_pairStorageMessage").Text.Contains("up to 100", StringComparison.Ordinal),
            "the 100-folder cap is explained instead of silently dropping the extra folder");
        Require(selectors.Count(item => item.IsChecked == true) == 100, "the refused extra selector is not left checked");
        harness.Overviews[PairB] = Overview(pairB, [PathBOnly]);
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
    }

    private static void CheckConfirmationAndScopeChange(MainWindow window, Harness harness, SyncPair pairA, SyncPair pairB,
        SyncPair pairGoogle)
    {
        harness.Changes.Clear();
        harness.Confirmations = 0;
        harness.ChangeScopeDuringConfirmation = true;
        PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pairB, new[] { PathFilter }, SyncDirectoryStorage.CloudOnly));
        Require(harness.LastConfirmation is not null, "a storage change is confirmed before it is despatched");
        var confirmation = harness.LastConfirmation!;
        Require(confirmation.Details.Contains(AccountLabelB, StringComparison.Ordinal) &&
                confirmation.Details.Contains(PairName, StringComparison.Ordinal),
            "the confirmation names the exact account and folder pair");
        Require(confirmation.Details.Contains("Storage: Online Only", StringComparison.Ordinal),
            "the confirmation names the exact storage choice being applied");
        Require(harness.Changes.Count == 0,
            "a confirmed choice is not despatched when the pair changed while the confirmation was open");
        Require(LastStatus(window).Contains("changed while the storage choice was open", StringComparison.Ordinal),
            "the stale confirmed choice is reported instead of being applied to another pair or revision");

        harness.ChangeScopeDuringConfirmation = false;
        harness.ChangeAccountDuringConfirmation = true;
        window.LoadPairSetForTest(new SyncPairSet([pairA, pairB, pairGoogle], PairB));
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
        PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pairB, new[] { PathFilter }, SyncDirectoryStorage.CloudOnly));
        Require(harness.Changes.Count == 0, "an account rebind during confirmation is refused even when pair id and revision match");
        harness.ChangeAccountDuringConfirmation = false;
        window.LoadPairSetForTest(new SyncPairSet([pairA, pairB, pairGoogle], PairB));
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
        PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pairB, new[] { PathFilter }, SyncDirectoryStorage.CloudOnly));
        Require(harness.Changes.Count == 1, "an unchanged scope despatches the confirmed choice exactly once");
        Require(harness.Changes[0].PairId == PairB && harness.Changes[0].ConnectionId == AccountB && harness.Changes[0].Revision == pairB.PolicyRevision,
            "the despatched change carries the exact account, pair and confirmed policy revision");
        Require(harness.Changes[0].Folders is { Count: 1 } folders && folders[0] == PathFilter,
            "the despatched change carries the exact confirmed folder target");
        Require(LastStatus(window).Contains("Storage preference saved", StringComparison.Ordinal),
            "a successful storage change reports the applied result");
        Require(harness.DispatchStatus.Contains("Applying the selected storage preference", StringComparison.Ordinal),
            "storage provides immediate operation-specific feedback before its effect dispatch");
        harness.Confirmations = 0;
    }

    private static void CheckDetailedStorageFailures(MainWindow window, Harness harness, SyncPair pair)
    {
        window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
        PumpTask(CallTask(window, "OpenPairStorageAsync", pair.Id, ""));
        harness.ChangeFailures = ["Plans/first.txt: local changes preserved.", "Plans/second.txt: hydration failed; previous pin restored."];
        try
        {
            PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pair, new[] { PathFilter }, SyncDirectoryStorage.AlwaysAvailable));
            Require(harness.ChangeFailures.All(failure => LastStatus(window).Contains(failure, StringComparison.Ordinal)),
                "storage retains and presents every failed path and exact reason, not just the first failure");
        }
        finally { harness.ChangeFailures = []; }
    }

    private static void CheckRunActiveRefusal(MainWindow window, Harness harness, SyncPair pairB)
    {
        harness.Changes.Clear();
        harness.Confirmations = 0;
        Set(window, "_uiRunActive", true);
        try
        {
            PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pairB, new[] { PathFilter }, SyncDirectoryStorage.CloudOnly));
            Require(harness.Changes.Count == 0, "a storage change is refused while a sync run is active");
            Require(harness.Confirmations == 0, "an active run refuses the change before any confirmation is shown");
            Require(LastStatus(window).Contains("Wait for the current operation before changing storage", StringComparison.Ordinal),
                "the active-run refusal gives the exact wait reason");
        }
        finally { Set(window, "_uiRunActive", false); }
        harness.Confirmations = 0;
    }

    private static void CheckGoogleConfirmationScope(MainWindow window, Harness harness, SyncPair pair)
    {
        harness.ConfirmationPairId = pair.Id;
        try
        {
            foreach (var change in new[] { "revision", "account", "selection" })
            {
                window.LoadPairSetForTest(new SyncPairSet(harness.Pairs.Values.ToArray(), pair.Id));
                PumpTask(CallTask(window, "OpenPairStorageAsync", pair.Id, ""));
                harness.Changes.Clear();
                harness.Confirmations = 0;
                harness.ChangeScopeDuringConfirmation = change == "revision";
                harness.ChangeAccountDuringConfirmation = change == "account";
                harness.ChangeSelectionDuringConfirmation = change == "selection";
                PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pair, new[] { PathFilter }, SyncDirectoryStorage.CloudOnly));
                Require(harness.Confirmations == 1 && harness.Changes.Count == 0 &&
                        LastStatus(window).Contains("changed while the storage choice was open", StringComparison.Ordinal),
                    "Google storage refuses a stale " + change + " after confirmation instead of dispatching");
            }
        }
        finally
        {
            harness.ChangeScopeDuringConfirmation = false;
            harness.ChangeAccountDuringConfirmation = false;
            harness.ChangeSelectionDuringConfirmation = false;
            harness.ConfirmationPairId = PairB;
            window.LoadPairSetForTest(new SyncPairSet(harness.Pairs.Values.ToArray(), PairB));
            PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
        }
    }

    private static void CheckFileOverrideReset(MainWindow window, Harness harness, SyncPair pairB)
    {
        PumpTask(CallTask(window, "OpenPairStorageAsync", pairB.Id, ""));
        var rule = pairB.FileStorageRules!.Single();
        harness.FileResets.Clear();
        harness.Confirmations = 0;
        PumpTask(CallTask(window, "ResetPairStorageFileChoiceAsync", pairB, rule));
        Require(harness.FileResets.Count == 1 && harness.FileResets[0].RelativePath == rule.RelativePath,
            "a file override reset despatches exactly once for the exact file path");
        Require(harness.FileResets[0].Revision == pairB.PolicyRevision &&
                harness.FileResets[0].PairId == pairB.Id && harness.FileResets[0].ConnectionId == pairB.ConnectionId &&
                harness.FileResets[0].Rule == rule,
            pairB.Provider + " file override reset carries the exact account, pair, file identity and confirmed policy revision");
        Require(harness.LastConfirmation is not null &&
                harness.LastConfirmation!.Details.Contains("stays selected for sync", StringComparison.Ordinal),
            "the file override reset keeps its sync-participation explanation");
        harness.Confirmations = 0;
    }

    private static void CheckOwnershipRefusal(MainWindow window, Harness harness, SyncPair pairForeign, string root)
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(root));
        var foreign = string.IsNullOrEmpty(drive) ? null : ExplorerIntegration.CompetingRoots(drive).FirstOrDefault();
        if (foreign is null)
        {
            Console.WriteLine("UNVERIFIED " + Selector +
                ": the wrong-owner refusal needs a real foreign sync root on the fixture drive and none exists here, so it stays unverified");
            return;
        }
        var pair = pairForeign with { LocalRoot = foreign.Path };
        harness.Pairs[PairForeign] = pair;
        harness.Overviews[PairForeign] = Overview(pair, [PathBOnly]);
        window.LoadPairSetForTest(new SyncPairSet([harness.Pairs[PairA], harness.Pairs[PairB], harness.Pairs[PairGoogle], pair], PairForeign));
        SelectPairRow(window, pair);
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairForeign, ""));
        Require(Field<SyncStorageOverview?>(window, "_pairStorageOverview")?.Pair.Id == PairForeign,
            "the foreign-ownership fixture pair loads through the real pair Storage read");
        harness.Changes.Clear();
        harness.Confirmations = 0;
        PumpTask(CallTask(window, "ApplyPairStorageChoiceAsync", pair, new[] { PathBOnly }, SyncDirectoryStorage.CloudOnly));
        Require(harness.Changes.Count == 0, "a location owned by another provider refuses the storage change");
        Require(LastStatus(window).Contains("owns the parent location", StringComparison.Ordinal),
            "the wrong-owner refusal names the competing provider and the parent location");
        Require(LastStatus(window).Contains(foreign.Provider, StringComparison.Ordinal),
            "the wrong-owner refusal names the exact competing provider");
        harness.Confirmations = 0;
        window.LoadPairSetForTest(new SyncPairSet([harness.Pairs[PairA], harness.Pairs[PairB], harness.Pairs[PairGoogle], pairForeign], PairB));
    }

    private static void CheckFailureRetry(MainWindow window, Harness harness, SyncPair pairB)
    {
        harness.DelayLoads = false;
        harness.LoadFailure = "The saved storage index could not be read.";
        PumpTask(CallTask(window, "OpenPairStorageAsync", PairB, ""));
        Require(Field<StackPanel>(window, "_pairStorageRows").Children.Count == 0,
            "a failed storage read clears the stale actionable rows");
        Require(Field<SyncStorageOverview?>(window, "_pairStorageOverview") is null,
            "a failed storage read drops the stale overview");
        Require(Field<TextBlock>(window, "_pairStorageMessage").Text.Contains("could not be loaded", StringComparison.Ordinal),
            "a failed storage read explains the failure");
        Require(Field<TextBlock>(window, "_pairStorageMessage").Text.Contains("Refresh", StringComparison.Ordinal),
            "a failed storage read offers the retry affordance");
        harness.LoadFailure = null;
        PumpTask(CallTask(window, "ReloadCurrentPairStorageAsync"));
        Require(RowText(window).Contains(PathBOnly, StringComparison.Ordinal), "the retry renders the pair's storage rows again");
    }

    private static void CheckRealGoogleOverviewLookup(MainWindow window, Harness harness,
        PilotStateAuthority authority, SyncPair pair)
    {
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(GooglePermissionId), GooglePermissionId,
            "storage-google@example.test", "Fixture Google", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Require(google.ConnectionId == pair.ConnectionId, "the test Google binding belongs to the exact saved Google pair");
        Pump(new GoogleDriveConnectionStore(authority).SaveAsync(google));
        Require(new DropboxConnectionStore(authority).Load() is null,
            "the real Google overview check has no Dropbox account to fall back to");
        var readsBefore = harness.Loads.Count;
        MainWindow.PairStorageOverviewLoaderForTest = null;
        try
        {
            // Unlike the rendering checks this invokes the real UI account lookup, then the real
            // authority pair/account check and retained-index read. No token/status/media path runs.
            var read = (Task<SyncStorageOverview>)Call(window, "LoadPairStorageOverviewAsync",
                pair, 0, "", "All folders", CancellationToken.None)!;
            Pump(read);
            var overview = read.GetAwaiter().GetResult();
            Require(overview.Pair.Id == pair.Id && overview.Pair.ConnectionId == google.ConnectionId &&
                    overview.Pair.Provider == SyncProviders.GoogleDrive && harness.Loads.Count == readsBefore,
                "real Google storage overview lookup succeeds without a Dropbox binding or test overview seam");
            var wrongAccount = window.AuthorityHost.LoadStorageOverviewAsync(pair.Id, AccountA);
            var rejected = false;
            try { Pump(wrongAccount); }
            catch (InvalidOperationException error) when (error.Message.Contains("does not own", StringComparison.Ordinal))
            { rejected = true; }
            Require(rejected, "real authority rejects a different account for the exact Google pair before reading its overview");
        }
        finally { MainWindow.PairStorageOverviewLoaderForTest = harness.Load; }
    }

    // ---- harness --------------------------------------------------------------------------------

    // R13-B/R15-A: a disconnected account is named by its exact connection id, so two pairs that share the
    // same display name never collapse into one indistinguishable label. This exercises the real window label
    // path through the same internal surface the tray uses, with no provider call and no invented identity.
    private static void CheckDisconnectedAccountLabels(MainWindow window, SyncPair pairA, SyncPair pairB)
    {
        var labelA = window.TrayLabelForPair(pairA.Id);
        var labelB = window.TrayLabelForPair(pairB.Id);
        Require(labelA == AccountLabelA && labelB == AccountLabelB && labelA != labelB,
            "Two disconnected accounts under the same folder-pair name stay distinct by exact connection id: " + labelA + " / " + labelB);
        Require(pairA.Name == pairB.Name,
            "The disconnected-label check really does use duplicate folder-pair names");
        Require(window.TrayLabelForPair("pair-not-retained") == "Account Not Retained",
            "An unknown pair id is reported as not retained rather than attributed to any account");

        var left = new SyncEndpointRecord(SyncEndpointKind.Dropbox, Provider: SyncProviders.Dropbox,
            ConnectionId: AccountA, NamespaceId: "namespace-a", FolderId: "id:folder-a", FolderPath: "/a");
        var right = new SyncEndpointRecord(SyncEndpointKind.GoogleDrive, Provider: SyncProviders.GoogleDrive,
            ConnectionId: AccountB, NamespaceId: "namespace-b", FolderId: "drive-folder-b", FolderPath: "/b");
        var explicitPair = pairA with
        {
            EndpointSchemaVersion = SyncEndpointSchema.Current,
            LeftEndpoint = left,
            RightEndpoint = right
        };
        Require((string?)Call(window, "LabelForPair", explicitPair) == AccountLabelA + " ↔ " + AccountLabelB,
            "An explicit two-cloud pair retains both disconnected account identities in endpoint order");
        Require((string?)Call(window, "LabelForPair", explicitPair with
            { LeftEndpoint = right, RightEndpoint = left }) == AccountLabelB + " ↔ " + AccountLabelA,
            "Swapping endpoints swaps their account labels without falling back to the legacy account");
        var local = new SyncEndpointRecord(SyncEndpointKind.Local, pairA.LocalRoot,
            pairA.LocalVolumeSerialNumber, pairA.LocalRootFileId);
        Require((string?)Call(window, "LabelForPair", explicitPair with
            { LeftEndpoint = local }) == "This computer ↔ " + AccountLabelB,
            "A local endpoint stays This computer while its cloud side retains its own account identity");
        Require((string?)Call(window, "LabelForPair", explicitPair with
            { RightEndpoint = local }) == AccountLabelA + " ↔ This computer",
            "The local endpoint label is independent of which side it occupies");
    }

    private sealed class Harness
    {
        internal MainWindow? Window;
        internal Dictionary<string, SyncPair> Pairs = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, SyncStorageOverview> Overviews = new(StringComparer.Ordinal);
        internal readonly List<(string PairId, string ConnectionId, int Offset, string Search, string? Filter)> Loads = [];
        internal readonly List<TaskCompletionSource<SyncStorageOverview>> Pending = [];
        internal readonly List<(string PairId, string ConnectionId, IReadOnlyList<string>? Folders, SyncDirectoryStorage? Storage, long Revision)> Changes = [];
        internal readonly List<(string PairId, string ConnectionId, string RelativePath, long Revision, SyncFileStorageRule Rule)> FileResets = [];
        internal bool DelayLoads;
        internal string? LoadFailure;
        internal IReadOnlyList<string> ChangeFailures = [];
        internal string DispatchStatus = string.Empty;
        internal bool ChangeScopeDuringConfirmation;
        internal bool ChangeAccountDuringConfirmation;
        internal bool ChangeSelectionDuringConfirmation;
        internal bool ConfirmationResult = true;
        internal string ConfirmationPairId = PairB;
        internal int Confirmations;
        internal MainWindow.PairStorageConfirmation? LastConfirmation;

        internal Task<SyncStorageOverview> Load(string pairId, string connectionId, int offset, string? search,
            string? filter, CancellationToken token)
        {
            Loads.Add((pairId, connectionId, offset, search ?? "", filter));
            if (LoadFailure is not null)
                return Task.FromException<SyncStorageOverview>(new InvalidOperationException(LoadFailure));
            if (DelayLoads)
            {
                var source = new TaskCompletionSource<SyncStorageOverview>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending.Add(source);
                return source.Task;
            }
            return Overviews.TryGetValue(pairId, out var overview)
                ? Task.FromResult(overview)
                : Task.FromException<SyncStorageOverview>(new InvalidOperationException("No fixture storage overview exists for " + pairId));
        }

        internal Task<SyncCloudAvailabilityResult> Change(string pairId, string connectionId,
            IReadOnlyList<string>? folders, SyncDirectoryStorage? storage, long revision)
        {
            Changes.Add((pairId, connectionId, folders, storage, revision));
            DispatchStatus = Window is { } window ? LastStatus(window) : string.Empty;
            return Task.FromResult(new SyncCloudAvailabilityResult(folders?.FirstOrDefault() ?? "",
                storage != SyncDirectoryStorage.CloudOnly, 3, 1, ChangeFailures));
        }

        internal Task<SyncCloudAvailabilityResult> ResetFile(string pairId, string connectionId, SyncFileStorageRule rule, long revision)
        {
            FileResets.Add((pairId, connectionId, rule.RelativePath, revision, rule));
            return Task.FromResult(new SyncCloudAvailabilityResult(rule.RelativePath, true, 1, 0, []));
        }

        internal bool Confirm(MainWindow.PairStorageConfirmation confirmation)
        {
            LastConfirmation = confirmation;
            Confirmations++;
            if (!ConfirmationResult) return false;
            if (Window is not { } target) return true;
            if (ChangeSelectionDuringConfirmation)
            {
                Set(target, "_suppressPairSelection", true);
                try { target.PairList.SelectedItem = target.PairRows.Single(row => row.Id == PairA); }
                finally { Set(target, "_suppressPairSelection", false); }
                return true;
            }
            if (ChangeAccountDuringConfirmation)
            {
                var rebound = Pairs[ConfirmationPairId] with { ConnectionId = AccountA };
                target.LoadPairSetForTest(new SyncPairSet(Pairs.Values
                    .Select(pair => pair.Id == rebound.Id ? rebound : pair).ToArray(), rebound.Id));
                return true;
            }
            if (!ChangeScopeDuringConfirmation) return true;
            // The exact pair is re-saved at a new policy revision while the confirmation is open.
            var bumped = Pairs[ConfirmationPairId] with { PolicyRevision = Pairs[ConfirmationPairId].PolicyRevision + 1 };
            target.LoadPairSetForTest(new SyncPairSet(Pairs.Values
                .Select(pair => pair.Id == bumped.Id ? bumped : pair).ToArray(), bumped.Id));
            return true;
        }
    }

    private static void Seed(MainWindow window, params SyncPair[] pairs) =>
        window.LoadPairSetForTest(new SyncPairSet(pairs, pairs[0].Id));

    private static void SavePairs(PilotStateAuthority authority, params SyncPair[] pairs)
    {
        // Seed the existing retained-authority boundary, not the single-provider legacy migration.
        using var http = new System.Net.Http.HttpClient();
        using var host = new SyncAuthorityRuntimeHost(authority, http);
        var installationId = new SyncPairStore(authority).GetOrCreateInstallationIdAsync().GetAwaiter().GetResult();
        Pump(host.Authority.InitializeAsync(installationId, "fixture-profile", "fixture-source", "{}"));
        foreach (var pair in pairs)
        {
            Pump(host.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
            Pump(host.Authority.RetainPairDefinitionAsync(pair, pair.Id == pairs[0].Id));
        }
    }

    private static SyncPair Pair(string id, string account, uint serial, ulong fileId, string dropboxRoot, string dropboxFolderId) =>
        new(id, PairName, @"C:\fixture\" + id, serial, fileId, dropboxRoot, dropboxFolderId, ConnectionId: account,
            Provider: id == PairGoogle ? SyncProviders.GoogleDrive : SyncProviders.Dropbox,
            DefaultStorage: id == PairA ? SyncDirectoryStorage.CloudOnly : null,
            SelectionRules: SelectionRules(id),
            IgnoreRules: IgnoreRules(id),
            FileStorageRules: (id is PairB or PairGoogle)
                ? [new SyncFileStorageRule("Plans/Design/spec.txt", SyncDirectoryStorage.CloudOnly, 0,
                    id == PairGoogle ? "google-fixture-file-1" : "id:fixture-file-1")]
                : null,
            DirectoryStorageRules: (id is PairB or PairGoogle) ? [new SyncDirectoryStorageRule("Reports", SyncDirectoryStorage.CloudOnly)] : null,
            DirectoryStorageApplyPaths: (id is PairB or PairGoogle) ? ["Plans"] : null);

    private static IReadOnlyList<SyncSelectionRule>? SelectionRules(string id) =>
        (id is PairB or PairGoogle or PairForeign) ? [new SyncSelectionRule("Secret", false)] : null;

    private static IReadOnlyList<SyncIgnoreRule>? IgnoreRules(string id) =>
        (id is PairB or PairGoogle or PairForeign) ? [new SyncIgnoreRule(SyncIgnoreKind.FolderName, "Ignored")] : null;

    private static SyncStorageOverview Overview(SyncPair pair, IReadOnlyList<string> folders) => new(pair,
        new SyncFolderSelectionPage(pair.Id, [.. folders.Select(Node)], 0, folders.Count, folders.Count, false, 0, 0, 0, 0,
            new SyncFolderRollup(folders.Count, 0, 0, 0, 0, 0)), 4096, 8, DateTimeOffset.UnixEpoch);

    private static SyncFolderSelectionNode Node(string relativePath) => new(relativePath,
        relativePath.Split('/')[^1], relativePath.Count(character => character == '/'), true, SyncFolderPresence.Mixed, 0);

    private static void SelectPairRow(MainWindow window, SyncPair pair)
    {
        var row = window.PairRows.FirstOrDefault(item => item.Id == pair.Id);
        if (row is null) return;
        window.PairList.SelectedItem = row;
        Call(window, "ExpandPairRow", row);
        Drain();
    }

    private static string Name(DependencyObject element) => AutomationProperties.GetName(element) ?? "";

    private static string RowText(MainWindow window) => Text(Field<StackPanel>(window, "_pairStorageRows"));

    private static string Text(DependencyObject root) =>
        string.Join("\n", FindDescendants<TextBlock>(root).Select(item => item.Text));

    private static string LastStatus(MainWindow window) => Field<TextBox>(window, "ConsoleText").Text;

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private static void ReleasePending(Harness harness)
    {
        var started = Environment.TickCount64;
        while (harness.Pending.Count > 0)
        {
            foreach (var source in harness.Pending) source.TrySetResult(harness.Overviews[PairB]);
            harness.Pending.Clear();
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out releasing outstanding pair Storage reads");
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
                throw new InvalidOperationException("Timed out waiting for a pair Storage task to complete");
        }
        task.GetAwaiter().GetResult();
    }

    private static void PumpTask(Task task)
    {
        try { Pump(task); }
        catch (Exception error) when (error is OperationCanceledException or TaskCanceledException) { }
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
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    private static Task CallTask(object target, string name, params object?[] args) =>
        Call(target, name, args) as Task ?? throw new InvalidOperationException(name + " did not return a task");

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
