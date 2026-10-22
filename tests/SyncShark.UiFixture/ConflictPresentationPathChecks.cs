using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// W2-A/B: the ACTUAL changed GUI paths, not the shared helper alone and not the CLI text.
//
// Every assertion here reads the real control, the real DecisionRow surface or the real production
// summary builder, so the ordered presentation, the neutral filter category and the removed provider
// ternary are proved on the surfaces a user actually sees.
internal static class ConflictPresentationPathChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        using var instance = PilotInstanceLock.Acquire(Path.Combine(Path.GetFullPath(root), "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1100, Height = 740 };
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }

        try
        {
            window.Show();
            Drain();
            CheckComparisonInteractions(window, Check);

            // A normal local-LEFT / cloud-RIGHT pair and a REVERSED cloud-LEFT / local-RIGHT pair, so the
            // compatibility port and the ordered side can be shown to disagree exactly as a reversed pair
            // makes them disagree.
            var normal = new SyncEndpointContext("pair-normal", "Normal",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Local,
                    SyncAuthorityCloudSides.LogicalLeft, LocalPath: @"C:\W2C\left"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.GoogleDrive,
                    SyncAuthorityCloudSides.LogicalRight, Provider: SyncProviders.GoogleDrive,
                    ConnectionId: "conn-w2c", NamespaceId: "ns-w2c", FolderId: "gd-w2c", FolderPath: "/DriveRight"));
            var reversed = new SyncEndpointContext("pair-reversed", "Reversed",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.GoogleDrive,
                    SyncAuthorityCloudSides.LogicalRight, Provider: SyncProviders.GoogleDrive,
                    ConnectionId: "conn-w2c", NamespaceId: "ns-w2c", FolderId: "gd-w2c", FolderPath: "/DriveLeft"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Local,
                    SyncAuthorityCloudSides.LogicalLeft, LocalPath: @"C:\W2C\right"));
            var normalPresentation = new EndpointReviewPresentation(normal);
            var reversedPresentation = new EndpointReviewPresentation(reversed);

            // (1) The conflict ROW summary through the real production builder: ordered names, ordered
            // sizes via LeftValue/RightValue, and no 'This PC' or provider ternary.
            var normalSummary = MainWindow.ConflictSummaryReason(normalPresentation, 11, 22);
            Check(normalSummary.Contains("Left — This computer 11 bytes", StringComparison.Ordinal) &&
                  normalSummary.Contains("Right — Google Drive 22 bytes", StringComparison.Ordinal),
                "The conflict row summary shows the ordered endpoints with each side's own size: " + normalSummary);
            var reversedSummary = MainWindow.ConflictSummaryReason(reversedPresentation, 11, 22);
            Check(reversedSummary.Contains("Left — Google Drive 22 bytes", StringComparison.Ordinal) &&
                  reversedSummary.Contains("Right — This computer 11 bytes", StringComparison.Ordinal),
                "For a reversed pair the sizes follow the compatibility ports, not an assumed local left: " + reversedSummary);
            Check(!normalSummary.Contains("This PC", StringComparison.Ordinal) &&
                  !reversedSummary.Contains("This PC", StringComparison.Ordinal),
                "The old 'This PC' plus provider ternary summary is gone from the production builder");
            var neutralSummary = MainWindow.ConflictSummaryReason(null, 11, 22);
            Check(neutralSummary.StartsWith("Left endpoint ", StringComparison.Ordinal) &&
                  !neutralSummary.Contains("Dropbox", StringComparison.Ordinal),
                "An undescribable pair falls back to neutral ordered wording rather than inventing a provider: " + neutralSummary);

            // (2) The conflict ROW presentation through the real DecisionRow surface: the raw compatibility
            // key is displayed against the ordered side that actually carries that physical port.
            var leftPortNewer = new DecisionRow("pair-normal", "Normal", "docs/report.txt", "Conflict", "r",
                Group: "Local newer", EndpointContext: normal);
            Check(leftPortNewer.GroupDisplay == "Newer on Left — This computer",
                "The raw 'Local newer' key is displayed on the ordered LEFT side when the left endpoint carries the local port: " + leftPortNewer.GroupDisplay);
            var rightPortNewer = new DecisionRow("pair-reversed", "Reversed", "docs/report.txt", "Conflict", "r",
                Group: "Local newer", EndpointContext: reversed);
            Check(rightPortNewer.GroupDisplay == "Newer on Right — This computer",
                "The SAME raw key is displayed on the ordered RIGHT side for a reversed pair, proving the port mapping: " + rightPortNewer.GroupDisplay);
            var noContext = new DecisionRow("pair-mixed", "Mixed", "docs/report.txt", "Conflict", "r",
                Group: "Dropbox newer");
            Check(noContext.GroupDisplay == SyncAuthorityRuntimeHost.NewerVersionGroup &&
                  noContext.Group == "Dropbox newer",
                "With no single pair context the display uses the truthful neutral category while the raw key stays unchanged and addressable");

            // (3) The group-FILTER display through the real control.
            var filter = (ComboBox)window.FindName("ConflictGroupFilter");
            var items = filter.Items.OfType<ComboBoxItem>()
                .Select(item => (Content: item.Content?.ToString() ?? string.Empty, Tag: item.Tag?.ToString() ?? string.Empty))
                .ToArray();
            Check(items.Any(item => item.Content == "Newer version" && item.Tag == "Newer version"),
                "The REAL group filter offers the neutral 'Newer version' category: " + string.Join(" | ", items.Select(item => item.Content)));
            Check(items.All(item => item.Content != "Local newer" && item.Content != "Cloud newer" &&
                                    item.Tag != "Local newer" && item.Tag != "Dropbox newer"),
                "The group filter no longer offers the lying local or cloud newer options");
            filter.SelectedItem = filter.Items.OfType<ComboBoxItem>().Single(item => item.Content as string == "Newer version");
            var selectedKey = (string?)typeof(MainWindow)
                .GetMethod("SelectedGroupKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [filter]);
            Check(selectedKey == SyncAuthorityRuntimeHost.NewerVersionGroup,
                "The real filter selection key is the neutral category the Core query matches against BOTH raw keys: " + selectedKey);

            // (4) W1's captions and selection behaviour still hold on the real controls.
            window.ApplyConflictActionPresentation((EndpointReviewPresentation?)null);
            Drain();
            var leftButton = (Button)window.FindName("KeepLocalConflictButton");
            var rightButton = (Button)window.FindName("KeepDropboxConflictButton");
            var leftName = System.Windows.Automation.AutomationProperties.GetName(leftButton);
            Check(ButtonText(leftButton) == "Use left" && ButtonText(rightButton) == "Use right" &&
                  leftName.Contains("right endpoint's version", StringComparison.Ordinal) &&
                  System.Windows.Automation.AutomationProperties.GetName(rightButton)
                      .Contains("left endpoint's version", StringComparison.Ordinal),
                "W1's ordered captions and the opposite-retention accessible names still hold on the real controls");
        }
        finally
        {
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
        Console.WriteLine(passed + " conflict presentation path checks passed; no provider call, credential or product-state effect was performed.");
        return 0;
    }

    private static void CheckComparisonInteractions(Window owner, Action<bool, string> check)
    {
        var left = System.Text.Encoding.UTF8.GetBytes("alpha  beta\nreturn 1;\n");
        var right = System.Text.Encoding.UTF8.GetBytes("alpha beta\nreturn 2;\n");
        var content = new SyncConflictContent("fixture-conflict", "fixture-pair", "sample.cs",
            left.Length, right.Length, null, null, null, null, left, right, "Bounded read-only comparison.");
        foreach (var choice in new[] { "left", "right", "both", "close" })
        {
            var dialog = new ConflictComparisonWindow(content)
            { Owner = owner, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
            dialog.Show(); Drain();
            try
            {
                var granularity = (ComboBox)dialog.FindName("DiffGranularityBox");
                var whitespace = (CheckBox)dialog.FindName("IgnoreWhitespaceBox");
                var text = (TextBox)dialog.FindName("UnifiedText");
                foreach (var mode in new[] { 0, 1, 2 })
                {
                    granularity.SelectedIndex = mode; whitespace.IsChecked = false; Drain();
                    var original = text.Text;
                    whitespace.IsChecked = true; Drain();
                    check(original.Length != 0 && text.Text.Length != 0 &&
                        ((TextBox)dialog.FindName("LocalText")).Text.Contains("alpha  beta") &&
                        ((TextBox)dialog.FindName("DropboxText")).Text.Contains("alpha beta"),
                        "Actual comparison mode " + mode + " and whitespace toggle preserve both source views");
                }
                var tabs = (TabControl)dialog.FindName("ComparisonTabs");
                foreach (TabItem tab in tabs.Items)
                    if (tab.Visibility == Visibility.Visible)
                    { tab.IsSelected = true; Drain(); check(tabs.SelectedItem == tab, "Actual comparison tab selects " + tab.Header); }
                Button button = choice switch
                {
                    "left" => (Button)dialog.FindName("ChooseLeftButton"),
                    "right" => (Button)dialog.FindName("ChooseRightButton"),
                    _ => Descendants(dialog).OfType<Button>().Single(b => ButtonText(b) == (choice == "both" ? "Keep Both" : "Close"))
                };
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
                var expected = choice switch
                {
                    "left" => (SyncAuthorityConflictResolutionKind?)SyncAuthorityConflictResolutionKind.KeepLocal,
                    "right" => SyncAuthorityConflictResolutionKind.KeepDropbox,
                    "both" => SyncAuthorityConflictResolutionKind.PreserveBoth,
                    _ => null
                };
                check(!dialog.IsVisible && dialog.RequestedResolution == expected,
                    "Actual comparison " + choice + " button closes with exactly its preview selection");
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
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

