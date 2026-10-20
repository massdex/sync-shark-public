using Massdex.SyncShark;
using Massdex.SyncShark.V2;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// Focused offline check for the changed conflict "Options" column: a DecisionRow carrying the production
// three-choice notice options must render three real buttons with their labels and the row carried through
// the Tag binding, while an ordinary notice with no choices renders an empty cell.
//
// Window initialization follows ConflictPresentationPathChecks: product startup is suppressed, the state
// authority is an isolated temporary root, and no host, provider or credential operation is invoked. The
// window is shown off-screen only because the real DataGrid cell templates are instantiated by layout.
internal static class NoticeOptionsUiChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var priorStartupSuppression = MainWindow.SuppressLoadedStartupForTest;
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        using var instance = PilotInstanceLock.Acquire(Path.Combine(Path.GetFullPath(root), "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        var window = new MainWindow(instance.StateAuthority, theme)
        {
            Width = 1100,
            Height = 740,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }

        try
        {
            // The Conflicts page lives in the third Needs Attention tab. Selecting it puts the real
            // ConflictGrid into the layout tree so its DataGridTemplateColumn cell templates run.
            window.Show();
            Drain();
            // Use the real navigation path so both the outer page and its content ancestors
            // participate in layout; toggling the outer Border alone leaves workspace state stale.
            typeof(MainWindow).GetMethod("NavigateTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, ["Conflicts", true]);
            window.UpdateLayout();
            Drain();

            var grid = (DataGrid)window.FindName("ConflictGrid");

            // (1) The changed XAML column is the first DataGridTemplateColumn and owns a cell template.
            var optionsColumn = grid.Columns.Count > 0 ? grid.Columns[0] as DataGridTemplateColumn : null;
            Check(optionsColumn is not null &&
                  optionsColumn.Header as string == "Options" &&
                  optionsColumn.CellTemplate is not null,
                "The real ConflictGrid renders the changed Options template column first");

            // (2) The option vocabulary comes from the production mapping, not a test-only copy.
            var bindingNotice = new Notice("pair-notice", 38, NoticeKind.AutoConflict,
                "Reports/2026 Q3.pdf", PlanNotices.CloudFilesBindingNeedsReview.Detail,
                PlanNotices.CloudFilesBindingNeedsReview.Key);
            var options = NoticeChoices.Options(bindingNotice);
            Check(options.Count == 3 &&
                  options.Select(option => option.Choice).SequenceEqual(
                      [NoticeChoices.ReviewRebind, NoticeChoices.PausePair, NoticeChoices.LeaveAsIs]),
                "The production notice mapping supplies the three named choices the row renders");

            // (3) A regular notice maps to no choices, exactly as NoticeMapping attaches an empty list.
            var regularNotice = new Notice("pair-regular", 4, NoticeKind.AutoConflict,
                "Docs/ordinary.txt", "Both versions are retained.", "auto-conflict");
            var regularOptions = NoticeChoices.Options(regularNotice);
            Check(regularOptions.Count == 0, "A regular notice maps to no choices");

            var noticeRow = new DecisionRow("pair-notice", "Notice pair", "Reports/2026 Q3.pdf",
                SyncDecisionKind.Conflict.ToString(), "Cloud Files binding needs review.",
                ConflictId: "conflict-notice", ConflictGeneration: 7, NoticeOptions: options);
            var regularRow = new DecisionRow("pair-regular", "Regular pair", "Docs/ordinary.txt",
                SyncDecisionKind.Conflict.ToString(), "Both versions are retained.",
                ConflictId: "conflict-regular", ConflictGeneration: 1, NoticeOptions: regularOptions);

            // (4) The rows go through the real displayed conflict collection, so the real grid's
            // ItemsSource and templates produce the cells; no product test hook is added for this.
            window.ConflictRows.Clear();
            window.ConflictRows.Add(noticeRow);
            window.ConflictRows.Add(regularRow);
            window.UpdateLayout();
            Drain();

            // (5) The three-choice notice row: the real buttons, their order and labels, the ItemsControl
            // Tag that is the DecisionRow, and the Button Tag that is the same row through the binding.
            var noticeContainer = RequireRow(grid, noticeRow);
            var noticeHost = Descendants(noticeContainer).OfType<ItemsControl>()
                .FirstOrDefault(control => ReferenceEquals(control.Tag, noticeRow));
            Check(noticeHost is not null && noticeHost.Items.Count == 3,
                "The notice row's real Options cell hosts all three choice items");
            Check(noticeHost!.ItemsPanel.LoadContent() is WrapPanel,
                "The notice options use the wrap panel the changed column declares");
            var optionButtons = Descendants(noticeHost).OfType<Button>().ToArray();
            Check(optionButtons.Length == 3,
                "The notice row renders exactly three real option buttons");
            Check(optionButtons.Select(ButtonText).SequenceEqual(
                    new[]
                    {
                        "Review / rebind to verified new path",
                        "Pause the pair",
                        "Leave as is"
                    }, StringComparer.Ordinal),
                "The three rendered option buttons carry their production labels in order: " +
                string.Join(" | ", optionButtons.Select(ButtonText)));
            Check(optionButtons.All(button => ReferenceEquals(button.Tag, noticeRow)),
                "Each rendered option button carries its own DecisionRow through the Tag binding");

            // (6) A regular notice has an empty Options cell and no buttons for that row.
            var regularContainer = RequireRow(grid, regularRow);
            var regularHost = Descendants(regularContainer).OfType<ItemsControl>()
                .FirstOrDefault(control => ReferenceEquals(control.Tag, regularRow));
            Check(regularHost is not null && regularHost.Items.Count == 0,
                "A regular notice renders an empty Options cell");
            Check(!Descendants(regularContainer).OfType<Button>()
                    .Any(button => ReferenceEquals(button.Tag, regularRow)),
                "A regular notice renders no option buttons");
        }
        finally
        {
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = priorStartupSuppression;
            app.Shutdown();
        }
        Console.WriteLine(passed +
            " notice option UI checks passed; offline rendering only, no host, provider or credential call was performed.");
        return 0;
    }

    private static T Field<T>(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);
        return (T)(field.GetValue(window)
            ?? throw new InvalidDataException("The window field was not initialized: " + name));
    }

    private static DataGridRow RequireRow(DataGrid grid, DecisionRow row)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            grid.ScrollIntoView(row);
            grid.UpdateLayout();
            Drain();
            if (grid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow container)
            {
                container.UpdateLayout();
                Drain();
                return container;
            }
        }
        throw new InvalidDataException("The conflict row was not realized in the real grid: " + row.Path);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string ButtonText(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(child => child.Text)),
        _ => string.Empty
    };

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
