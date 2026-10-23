using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// UIV1 source-only checks over the real WPF Storage path and the existing detail-header constructors.
//
// Everything asserted here runs through production members: the real Storage page controls
// (_storageAccount/_storagePair/_storageFilter/_storageSearch and their real event handlers),
// the real LoadStoragePageAsync/RenderStorageInventory/PresentStorageSummary/ShowStorageDetails
// path, the real authority storage-inventory read, the real DetailsHost expand controls, the real
// MainWindow.InspectActivity/RefreshStructuredInspector presentation and the real SharingEditor
// detail-header constructor.
//
// Fixture input is the retained authority the window itself reads. The account display labels are
// seeded directly so the account filter runs without a saved provider connection, which keeps this
// check free of any credential, provider, network, signing, install or user-file effect.
internal static class SourceUiCorrectionChecks
{
    private const string Selector = "--source-ui-correction-checks";
    private const string AccountA = "ui-correction-account-a";
    private const string AccountB = "ui-correction-account-b";
    private const string AccountC = "ui-correction-account-c";
    private const string AccountLabelA = "Example Personal";
    private const string AccountLabelB = "Example Work";
    private const string AccountLabelC = "Example Archive";
    private const string PairA = "ui-correction-pair-a";
    private const string PairB = "ui-correction-pair-b";
    private const string PairC = "ui-correction-pair-c";
    private const string ExcludedA = "Hold-A";
    private const string ExcludedB = "Hold-B";
    private const string OnlineOnlyFolder = "Archive-Holds";
    private const int PartialRuleCount = 99;
    private const string ExpandTooltip = "Open these details in a larger window";
    private const string SavedIndexProvenance = "Saved mappings are not a fresh disk scan.";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        // Each run gets its own bounded state directory under the requested allocation, so a repeat
        // run cannot inherit the previous run's retained fixture pairs.
        var runRoot = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(runRoot, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var pairA = Pair(PairA, AccountA, 11, 101, "/First", "id:correction-a", [ExcludedA]);
        var pairB = Pair(PairB, AccountB, 12, 102, "/Second", "id:correction-b", [ExcludedB]);
        var pairC = Pair(PairC, AccountC, 13, 103, "/Third", "id:correction-c",
            [.. Enumerable.Range(0, PartialRuleCount).Select(index => "Hold-C-" + index.ToString("000"))],
            directoryRules: [new SyncDirectoryStorageRule(OnlineOnlyFolder, SyncDirectoryStorage.CloudOnly),
                new SyncDirectoryStorageRule(OnlineOnlyFolder + "-B", SyncDirectoryStorage.CloudOnly),
                new SyncDirectoryStorageRule(OnlineOnlyFolder + "-C", SyncDirectoryStorage.CloudOnly)]);
        using var retained = new RetainedAuthority(instance.StateAuthority, pairA, pairB);
        var window = new MainWindow(instance.StateAuthority, theme)
        {
            Width = 1240, Height = 780, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            Drain();
            SeedStorageScope(window, pairA, pairB);
            Call(window, "NavigateTo", "Storage", true);
            Drain();
            Reload(window);

            CheckCurrentSummaryFollowsTheVisibleRows(window);
            CheckSelectedRowDetailsAndExpandAction(window);
            CheckAccountAndPairFiltering(window);
            CheckPartialCoverageStaysDistinct(window, retained, pairA, pairB, pairC, runRoot);
            CheckDetailHeaderActionsAndFieldConvention(window);

            Console.WriteLine("PASS " + Selector +
                "; state " + runRoot + "; real Storage filter/search/account/pair render path and real detail-header constructors only," +
                " no credential, provider, network, signing, install or user-file effect");
            return 0;
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = false;
            window.RequestProductExit();
            app.Shutdown();
        }
    }

    // ---- UIV1-1: the footer describes the current filter and the rows actually shown -------------

    private static void CheckCurrentSummaryFollowsTheVisibleRows(MainWindow window)
    {
        var rows = Field<DataGrid>(window, "_storageRows");
        var filter = Field<ComboBox>(window, "_storageFilter");
        var search = Field<TextBox>(window, "_storageSearch");
        search.Text = "";
        filter.SelectedIndex = 0;
        Drain();
        Require(rows.Items.Count == 0, "UIV1-1 online-only is genuinely empty before any online-only mapping exists");
        Require(Summary(window).StartsWith("No online-only folders are recorded for this selection.", StringComparison.Ordinal),
            "UIV1-1 the empty footer names the current online-only filter");
        Require(Summary(window).Contains(SavedIndexProvenance, StringComparison.Ordinal),
            "UIV1-1 the footer keeps its retained saved-index provenance");

        filter.SelectedIndex = 1;
        Drain();
        Require(rows.Items.Count == 2 && VisibleRows(window).All(row => row.Item.Participation == "Excluded from sync"),
            "UIV1-1 selective sync shows the two real excluded mappings for the current scope");
        Require(VisibleRows(window).All(row => row.ShortAction == "Excluded from sync"),
            "UIV1-1 an exclusion row action names the exclusion instead of echoing its retained availability");
        Require(!Summary(window).Contains("No online-only folders", StringComparison.Ordinal) &&
                Summary(window).Contains("Selective sync: 2 shown for this selection.", StringComparison.Ordinal),
            "UIV1-1 the footer follows the current selective-sync filter and the exact visible row count");

        search.Text = "no-such-saved-mapping";
        Drain();
        Require(rows.Items.Count == 0 &&
                Summary(window).Contains("No selective-sync exclusions are recorded for this selection.", StringComparison.Ordinal) &&
                !Summary(window).Contains("Selective sync: 2", StringComparison.Ordinal),
            "UIV1-1 a no-match search derives its own empty sentence from the current filter and rows");

        search.Text = "";
        Drain();
        Require(rows.Items.Count == 2 && Summary(window).Contains("Selective sync: 2 shown for this selection.", StringComparison.Ordinal),
            "UIV1-1 clearing the search restores the derived count");

        filter.SelectedIndex = 0;
        Drain();
        Require(rows.Items.Count == 0 &&
                Summary(window).StartsWith("No online-only folders are recorded for this selection.", StringComparison.Ordinal) &&
                !Summary(window).Contains("Selective sync:", StringComparison.Ordinal),
            "UIV1-1 returning to online-only never retains the previous selective-sync sentence");
    }

    private static void CheckSelectedRowDetailsAndExpandAction(MainWindow window)
    {
        var rows = Field<DataGrid>(window, "_storageRows");
        var filter = Field<ComboBox>(window, "_storageFilter");
        Field<TextBox>(window, "_storageSearch").Text = "";
        filter.SelectedIndex = 1;
        Drain();
        var selected = VisibleRows(window).First(row => row.Item.PairId == PairA);
        rows.SelectedItem = selected;
        Drain();
        var details = Field<StackPanel>(window, "_storageDetailFields");
        var labels = Descendants<TextBlock>(details).Select(item => item.Text).ToArray();
        Require(Follows(labels, "Observed availability:", "Not observed; saved policy only"),
            "UIV1-1 the retained availability stays in the details of an exclusion row");
        Require(Follows(labels, "Sync participation:", "Excluded from sync"),
            "UIV1-1 the details state the exclusion explicitly");
        Require(Follows(labels, "Desired policy:", selected.Item.DesiredPolicy),
            "UIV1-1 the details keep the saved policy separate from the observed state");
        var expand = Descendants<Button>(details).Single(button => Name(button) == "Expand Storage Details");
        Require(ExpandTooltip == expand.ToolTip as string,
            "UIV1-2 the Storage expand action explains itself instead of repeating its own action name");
        Require(SameGlyph(expand.Content as FrameworkElement, "expand"),
            "UIV1-2 the Storage expand action keeps the existing four-corner expand icon");
    }

    private static void CheckAccountAndPairFiltering(MainWindow window)
    {
        var rows = Field<DataGrid>(window, "_storageRows");
        var filter = Field<ComboBox>(window, "_storageFilter");
        var search = Field<TextBox>(window, "_storageSearch");
        search.Text = "";
        SeedAccountLabels(window);
        filter.SelectedIndex = 1;
        Drain();

        var account = Field<ComboBox>(window, "_storageAccount");
        account.SelectedValue = AccountA;
        PumpUntil(() => Field<object?>(window, "_storageLoad") is null && rows.Items.Count == 1,
            "the account-scoped Storage read");
        Require(VisibleRows(window).Single().Item.PairId == PairA,
            "UIV1-1 the account filter renders only that account's mapping");
        Require(Summary(window).Contains("Selective sync: 1 shown for this selection.", StringComparison.Ordinal),
            "UIV1-1 the account transition re-derives the footer for the new visible row count");

        account.SelectedValue = "";
        PumpUntil(() => Field<object?>(window, "_storageLoad") is null && rows.Items.Count == 2,
            "the all-account Storage read");
        var pairPicker = Field<ComboBox>(window, "_storagePair");
        pairPicker.SelectedValue = PairB;
        PumpUntil(() => Field<object?>(window, "_storageLoad") is null && rows.Items.Count == 1,
            "the pair-scoped Storage read");
        Require(VisibleRows(window).Single().Item.PairId == PairB,
            "UIV1-1 the pair filter renders only that pair's mapping");
        pairPicker.SelectedValue = "";
        PumpUntil(() => Field<object?>(window, "_storageLoad") is null && rows.Items.Count == 2,
            "the all-pair Storage read");
    }

    // ---- UIV1-1: partial loaded coverage and the inspection notice stay distinct ------------------

    private static void CheckPartialCoverageStaysDistinct(MainWindow window, RetainedAuthority retained,
        SyncPair pairA, SyncPair pairB, SyncPair pairC, string root)
    {
        retained.Add(pairC);
        SeedStorageScope(window, pairA, pairB, pairC);
        var filter = Field<ComboBox>(window, "_storageFilter");
        Field<TextBox>(window, "_storageSearch").Text = "";
        filter.SelectedIndex = 1;
        Drain();
        Reload(window);

        var rows = Field<DataGrid>(window, "_storageRows");
        var shown = rows.Items.Count;
        Require(shown > 100 &&
                Summary(window).Contains("Selective sync: " + shown + " shown for this selection.", StringComparison.Ordinal),
            "UIV1-1 a bounded page reports its exact current visible row count");
        Require(Field<Button>(window, "_storageMore").Visibility == Visibility.Visible &&
                Summary(window).Contains("More saved records remain to load.", StringComparison.Ordinal),
            "UIV1-1 partial loaded coverage stays a distinct retained footer fact");

        filter.SelectedIndex = 0;
        Drain();
        var onlineOnly = VisibleRows(window);
        var onlineOnlyDetail = onlineOnly.Length + " row(s); first=" + (onlineOnly.Length == 0 ? "none"
            : onlineOnly[0].Item.PairId + ":" + onlineOnly[0].Item.RelativePath + ":action=" + onlineOnly[0].ShortAction);
        Require(onlineOnly.Length == 1 && onlineOnly[0].Item.RelativePath == OnlineOnlyFolder &&
                onlineOnly[0].ShortAction == "Online only (saved policy)",
            "UIV1-1 an online-only row names the saved policy instead of claiming an observed dehydration; " + onlineOnlyDetail);
        Require(Summary(window).Contains("Online-only folders: 1 shown for this selection.", StringComparison.Ordinal) &&
                Summary(window).Contains("More saved records remain to load.", StringComparison.Ordinal),
            "UIV1-1 an ordinary category render re-derives the summary and keeps the retained coverage");
        Require(!Summary(window).Contains("Selective sync:", StringComparison.Ordinal),
            "UIV1-1 the footer carries no sentence from the previous category");

        var target = Path.Combine(root, "inspection-target");
        Directory.CreateDirectory(Path.Combine(target, "Inner"));
        File.WriteAllText(Path.Combine(target, "note.txt"), "Isolated Sync Shark storage inspection example.\n");
        PumpTask((Task)Call(window, "InspectStorageAsync", target, null, null)!);
        Require(Summary(window).Contains("Bounded on-disk inspection finished.", StringComparison.Ordinal),
            "UIV1-1 the explicit inspection outcome is retained in the footer");
        filter.SelectedIndex = 1;
        Drain();
        Require(Summary(window).Contains("Bounded on-disk inspection finished.", StringComparison.Ordinal),
            "UIV1-1 an ordinary render does not erase the retained inspection notice");

        // A file root is a bounded refusal the production inspection already reports as incomplete.
        PumpTask((Task)Call(window, "InspectStorageAsync", Path.Combine(target, "note.txt"), null, null)!);
        Require(Summary(window).Contains("Partial inspection:", StringComparison.Ordinal),
            "UIV1-1 an incomplete on-disk inspection keeps its distinct partial-coverage provenance");
        filter.SelectedIndex = 0;
        Drain();
        Require(Summary(window).Contains("Partial inspection:", StringComparison.Ordinal),
            "UIV1-1 an ordinary category render keeps the partial-coverage provenance");

        // A drive root is refused by the real validation before any inspection starts.
        PumpTask((Task)Call(window, "InspectStorageAsync", Path.GetPathRoot(root)!, null, null)!);
        Require(Summary(window).Contains("Inspection did not finish:", StringComparison.Ordinal) &&
                Summary(window).Contains("not an entire drive", StringComparison.Ordinal),
            "UIV1-1 a refused inspection keeps its distinct error provenance");
        Field<TextBox>(window, "_storageSearch").Text = "no-such-saved-mapping";
        Drain();
        Require(Summary(window).Contains("Inspection did not finish:", StringComparison.Ordinal),
            "UIV1-1 an ordinary search render does not erase the retained inspection error");
        Field<TextBox>(window, "_storageSearch").Text = "";
        Drain();
    }

    // ---- UIV1-2: detail-header actions and the Field label convention -----------------------------

    private static void CheckDetailHeaderActionsAndFieldConvention(MainWindow window)
    {
        Call(window, "NavigateTo", "Activity", true);
        Drain();
        // The real inspection shows the details sidebar and realises its ContentPresenter-hosted
        // content, exactly as selecting a row does in the running application.
        var row = new ActivityRow(DateTimeOffset.Now.ToString("O"), "Work/Invoices/manual.pdf", "Completed upload",
            "Uploaded and verified.", PairId: PairA, PairName: "Correction " + PairA,
            OperationId: "ui-correction-format", ItemKind: "File", FileType: "PDF", SizeBytes: 1024);
        Call(window, "InspectActivity", row);
        Drain();
        window.UpdateLayout();
        Drain();
        var activityExpand = Descendants<Button>(window).SingleOrDefault(button => button.Name == "DetailsExpandButton");
        Require(activityExpand is not null, "UIV1-2 the Activity details header still builds its expand action");
        Require(ExpandTooltip == activityExpand!.ToolTip as string && Name(activityExpand) == "Expand Details" &&
                SameGlyph(activityExpand.Content as FrameworkElement, "expand"),
            "UIV1-2 the Activity expand action explains itself, keeps its accessible name and keeps the four-corner expand icon");

        var sharingExpand = SharingChrome(window).Children.OfType<Button>().Single(button => Name(button) == "Expand Details");
        Require(ExpandTooltip == sharingExpand.ToolTip as string && Name(sharingExpand) == "Expand Details" &&
                SameGlyph(sharingExpand.Content as FrameworkElement, "expand"),
            "UIV1-2 the Sharing expand action explains itself, keeps its accessible name and keeps the four-corner expand icon");

        var labels = Descendants<TextBlock>(Field<StackPanel>(window, "_detailFields")).Select(item => item.Text).ToArray();
        Require(Follows(labels, "File Format:", "PDF"),
            "UIV1-2 the Activity File Format detail label carries the Field convention colon");
        Require(Follows(labels, "File Type:", "Document") && Follows(labels, "Type:", "File"),
            "UIV1-2 the neighbouring Activity detail labels keep the same existing convention");
    }

    // ---- fixture input ----------------------------------------------------------------------------

    private static SyncPair Pair(string id, string account, uint serial, ulong fileId, string dropboxRoot,
        string folderId, IReadOnlyList<string> excluded, SyncDirectoryStorage? defaultStorage = null,
        IReadOnlyList<SyncDirectoryStorageRule>? directoryRules = null) =>
        new(id, "Correction " + id, @"C:\fixture\" + id, serial, fileId, dropboxRoot, folderId,
            ConnectionId: account, Provider: SyncProviders.Dropbox, DefaultStorage: defaultStorage,
            SelectionRules: [.. excluded.Select(path => new SyncSelectionRule(path, false))],
            DirectoryStorageRules: directoryRules);

    private static void SeedStorageScope(MainWindow window, params SyncPair[] pairs)
    {
        window.LoadPairSetForTest(new SyncPairSet(pairs, pairs[0].Id));
        SeedAccountLabels(window);
    }

    private static void SeedAccountLabels(MainWindow window)
    {
        // The provider connections themselves are never saved, so no credential or provider read can
        // start; the display labels only feed the real account filter that the Storage page applies.
        Set(window, "_accountLabels", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountA] = AccountLabelA, [AccountB] = AccountLabelB, [AccountC] = AccountLabelC
        });
        Call(window, "RefreshStorageChoices", true);
    }

    private static void Reload(MainWindow window) => PumpTask(CallTask(window, "LoadStoragePageAsync", true));

    private static StackPanel SharingChrome(MainWindow window)
    {
        var constructor = typeof(MainWindow).GetMethod("SharingEditor", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("The Sharing detail-header constructor is absent.");
        var arguments = new object?[] { "Manage Shared Link", null, null, null, null };
        var session = constructor.Invoke(window, arguments)
            ?? throw new InvalidDataException("The Sharing detail header was not built.");
        return session.GetType()
            .GetField("SidebarChrome", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(session) as StackPanel
            ?? throw new InvalidDataException("The Sharing detail header has no sidebar chrome.");
    }

    private sealed class RetainedAuthority : IDisposable
    {
        private readonly System.Net.Http.HttpClient _http = new();
        private readonly SyncAuthorityRuntimeHost _host;

        internal RetainedAuthority(PilotStateAuthority authority, params SyncPair[] pairs)
        {
            _host = new SyncAuthorityRuntimeHost(authority, _http);
            Pump(_host.Authority.InitializeAsync("ui-correction-fixture", "fixture-profile", "fixture-source", "{}"));
            Add(pairs);
        }

        internal void Add(params SyncPair[] pairs)
        {
            foreach (var pair in pairs)
            {
                Pump(_host.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
                Pump(_host.Authority.RetainPairDefinitionAsync(pair, select: true));
            }
        }

        public void Dispose()
        {
            _host.Dispose();
            _http.Dispose();
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static StorageInventoryDisplayRow[] VisibleRows(MainWindow window) =>
        Field<DataGrid>(window, "_storageRows").Items.OfType<StorageInventoryDisplayRow>().ToArray();

    private static string Summary(MainWindow window) => Field<TextBlock>(window, "_storageMessage").Text;

    private static string Name(DependencyObject element) => AutomationProperties.GetName(element) ?? "";

    private static bool SameGlyph(FrameworkElement? content, string name)
    {
        if (content is null) return false;
        var observed = Descendants<System.Windows.Shapes.Path>(content)
            .Select(item => item.Data?.ToString() ?? "").FirstOrDefault();
        var reference = Descendants<System.Windows.Shapes.Path>(SharedPresentation.Glyph(name))
            .Select(item => item.Data?.ToString() ?? "").FirstOrDefault();
        return !string.IsNullOrEmpty(observed) && observed == reference;
    }

    private static bool Follows(IReadOnlyList<string> labels, string label, string value)
    {
        for (var index = 0; index + 1 < labels.Count; index++)
            if (labels[index] == label && labels[index + 1] == value) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        // Both trees are walked (deduplicated by reference) so template-generated and collapsed
        // containers are reached as well as the plain logical children.
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()
                         .Concat(VisualChildren(current)))
            {
                if (!seen.Add(child)) continue;
                if (child is T match) yield return match;
                pending.Enqueue(child);
            }
        }
    }

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject current)
    {
        if (current is not Visual) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            yield return VisualTreeHelper.GetChild(current, index);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);

    private static Task CallTask(object target, string name, params object?[] args) =>
        Call(target, name, args) as Task ?? throw new InvalidOperationException(name + " did not return a task");

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(1);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for a retained-authority write");
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

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
