using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class GuidedSyncCorrectionContract
{
    private const int PageSize = 200;

    internal static IReadOnlyList<string> Run(App app, MainWindow window, SyncPair pair, SyncPairStore pairStore,
        IDropboxFolderBrowserService browser, List<string> dispatches, string localRoot, string stateRoot,
        string outputRoot)
    {
        var passed = new List<string>();
        AssertFirstSyncSurface(window, pair, pairStore, outputRoot, passed);
        AssertActivitySurface(window, pair, outputRoot, passed);
        AssertInFlightResponsiveness(window, pair, browser, dispatches, localRoot, outputRoot, passed);
        AssertMonitorGenerationAndHealthSources(window, pair, pairStore, passed);
        AssertConnectionLifecycle(window, pair, pairStore, passed);
        AssertPairLifecycleAndDialog(app, window, pair, pairStore, browser, stateRoot, outputRoot, passed);
        AssertRenderedControlStates(window, outputRoot, passed);
        return passed;
    }

    internal static IReadOnlyList<string> RunActivityKindDelta(MainWindow window, SyncPair pair, string outputRoot)
    {
        var passed = new List<string>();
        AssertActivitySurface(window, pair, outputRoot, passed);
        return passed;
    }

    private static void AssertFirstSyncSurface(MainWindow window, SyncPair original, SyncPairStore store,
        string outputRoot, ICollection<string> passed)
    {
        CancelDraft(window);
        var pair = original with { InitialReviewApproved = false };
        var set = new SyncPairSet([pair], pair.Id);
        store.SaveAsync(set).GetAwaiter().GetResult();
        window.LoadPairSetForTest(set);
        window.ApplyFolderHierarchyPresentationForTest(Health(pair, true, true));

        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<SyncBootstrapReviewItem> inventory =
        [
            Item(pair, "docs/report.pdf", "Local only", "file", "PDF", "Upload", now),
            Item(pair, "notes.txt", "Dropbox only", "file", "TXT", "Download", now.AddMinutes(-1)),
            Item(pair, "Folder A", "Local only", "folder", "Folder", "Upload", now.AddMinutes(-2)),
            Item(pair, "same.docx", "Identical", "file", "DOCX", "No change", now.AddMinutes(-3)),
            Item(pair, "conflict.csv", "Local newer", "file", "CSV", "Review conflict", now.AddMinutes(-4))
        ];
        var excluded = Item(pair, "excluded/never-sync.txt", "Local only", "file", "TXT", "Upload",
            now.AddMinutes(-5));
        IReadOnlyList<SyncBootstrapReviewItem> Filter(string? search, string? group, string? kind, string? type)
        {
            IEnumerable<SyncBootstrapReviewItem> rows = inventory;
            if (!string.IsNullOrWhiteSpace(search)) rows = rows.Where(item =>
                item.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(group) && group != "All groups")
                rows = rows.Where(item => item.Group == group);
            if (!string.IsNullOrWhiteSpace(kind) && kind != "All items")
                rows = rows.Where(item => item.ItemKind.Equals(kind.TrimEnd('s'), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(type) && type != "All types")
                rows = rows.Where(item => item.FileType.Equals(type.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
            return rows.ToArray();
        }

        MainWindow.BootstrapReviewLoaderForTest = (id, search, group, kind, type) =>
            Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>(Filter(search, group, kind, type));
        MainWindow.BootstrapReviewPageLoaderForTest = (id, take, offset, search, group, kind, type) =>
        {
            var filtered = Filter(search, group, kind, type);
            return Task.FromResult(new SyncBootstrapReviewPage(filtered.Skip(offset).Take(take).ToArray(), offset,
                filtered.Count, inventory.Count, 2, 1, 1, offset + take < filtered.Count, true,
                "local-generation-1", "dropbox-generation-1"));
        };
        try
        {
            Navigate(window, "Bootstrap");
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            var pairSelector = (ComboBox)Required(window, "BootstrapPairBox");
            pairSelector.SelectedItem = window.PairRows.Single(item => item.Id == pair.Id);
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            if (!Equals(pairSelector.SelectedValue, pair.Id))
                throw new InvalidDataException("The First sync pair filter did not retain the selected pair.");
            RequirePaths(window, "docs/report.pdf", "notes.txt", "Folder A", "same.docx", "conflict.csv");
            if (window.BootstrapRows.Any(item => item.Path == excluded.RelativePath))
                throw new InvalidDataException("An excluded folder item reached First sync review.");

            SelectContent<ComboBoxItem>((ComboBox)Required(window, "BootstrapGroupFilter"), "Local only");
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            RequirePaths(window, "docs/report.pdf", "Folder A");
            SelectContent<ComboBoxItem>((ComboBox)Required(window, "BootstrapGroupFilter"), "All groups");
            SelectContent<ComboBoxItem>((ComboBox)Required(window, "BootstrapItemFilter"), "Folders");
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            RequirePaths(window, "Folder A");
            SelectContent<ComboBoxItem>((ComboBox)Required(window, "BootstrapItemFilter"), "All items");
            ((ComboBox)Required(window, "BootstrapTypeFilter")).Text = "PDF";
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            RequirePaths(window, "docs/report.pdf");
            SelectContent<ComboBoxItem>((ComboBox)Required(window, "BootstrapTypeFilter"), "All types");
            ((TextBox)Required(window, "BootstrapSearchBox")).Text = "notes";
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            RequirePaths(window, "notes.txt");
            ((TextBox)Required(window, "BootstrapSearchBox")).Clear();
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());

            var localRow = window.BootstrapRows.Single(item => item.Path == "docs/report.pdf");
            var cloudRow = window.BootstrapRows.Single(item => item.Path == "notes.txt");
            var grid = (DataGrid)Required(window, "BootstrapGrid");
            grid.ScrollIntoView(localRow);
            grid.UpdateLayout();
            SetRenderedBootstrapAction(window, grid, localRow, "Skip for now");
            SetRenderedBootstrapAction(window, grid, localRow, "Upload");
            SetRenderedBootstrapAction(window, grid, localRow, "Skip for now");
            SetRenderedBootstrapAction(window, grid, cloudRow, "Skip for now");
            SetRenderedBootstrapAction(window, grid, cloudRow, "Download");
            SetRenderedBootstrapAction(window, grid, cloudRow, "Skip for now");
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            if (window.BootstrapRows.Single(item => item.Path == "docs/report.pdf").SelectedAction != "Skip for now" ||
                window.BootstrapRows.Single(item => item.Path == "notes.txt").SelectedAction != "Skip for now")
                throw new InvalidDataException("Skipped first-sync choices did not remain pending after refresh.");

            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(window.BootstrapRows.Single(item => item.Path == "docs/report.pdf"));
            grid.SelectedItems.Add(window.BootstrapRows.Single(item => item.Path == "notes.txt"));
            var selectedAction = (ComboBox)Required(window, "BootstrapSelectedActionBox");
            for (DependencyObject? ancestor = selectedAction; ancestor is not null;
                 ancestor = LogicalTreeHelper.GetParent(ancestor))
                if (ancestor is Expander bulk) bulk.IsExpanded = true;
            // The bulk-action panel is wrapped in a real Expander, so its buttons reach the VISUAL tree only
            // after that expander is expanded AND the window has measured and arranged the new content. The
            // logical chain alone leaves the visual lookup empty, so also expand every rendered ancestor
            // expander, force a layout pass and wait for the real button before asserting it exists.
            for (DependencyObject? rendered = selectedAction; rendered is not null;
                 rendered = VisualTreeHelper.GetParent(rendered))
                if (rendered is Expander visualBulk) visualBulk.IsExpanded = true;
            window.UpdateLayout();
            for (var attempt = 0; attempt < 50 && !FindDescendants<Button>(window)
                     .Any(item => LabelEquals(item, "Apply to selected")); attempt++)
            {
                Pump(window);
                window.UpdateLayout();
            }
            var applySelectedButtons = FindDescendants<Button>(window)
                .Where(item => LabelEquals(item, "Apply to selected")).ToArray();
            if (applySelectedButtons.Length != 1)
                throw new InvalidDataException("The rendered Filters and Bulk Actions expander exposed " +
                    applySelectedButtons.Length + " 'Apply to selected' buttons after expansion and layout; " +
                    "expanders=[" + string.Join(",", FindDescendants<Expander>(window).Select(item =>
                        (item.Header?.ToString() ?? "null") + "=" + item.IsExpanded)) +
                    "]; matchingButtons=" + FindDescendants<Button>(window)
                        .Count(item => LabelEquals(item, "Apply to all matching")));
            var applySelected = applySelectedButtons[0];
            SelectContent<ComboBoxItem>(selectedAction, "Upload");
            applySelected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (window.BootstrapRows.Single(item => item.Path == "docs/report.pdf").SelectedAction != "Upload")
                throw new InvalidDataException("Bulk Upload did not reach the selected local-only row.");
            if (window.BootstrapRows.Single(item => item.Path == "notes.txt").SelectedAction != "Skip for now")
                throw new InvalidDataException("Bulk Upload crossed direction into a Dropbox-only row.");
            SelectContent<ComboBoxItem>(selectedAction, "Download");
            applySelected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (window.BootstrapRows.Single(item => item.Path == "notes.txt").SelectedAction != "Download")
                throw new InvalidDataException("Bulk Download did not reach the selected Dropbox-only row.");
            SelectContent<ComboBoxItem>(selectedAction, "Skip for now");
            applySelected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (window.BootstrapRows.Where(item => item.Path is "docs/report.pdf" or "notes.txt")
                .Any(item => item.SelectedAction != "Skip for now"))
                throw new InvalidDataException("Bulk Skip did not reach both selected one-sided rows.");
            var matchingAction = (ComboBox)Required(window, "BootstrapMatchingActionBox");
            window.UpdateLayout();
            for (var attempt = 0; attempt < 50 && !FindDescendants<Button>(window)
                     .Any(item => LabelEquals(item, "Apply to all matching")); attempt++)
            {
                Pump(window);
                window.UpdateLayout();
            }
            var applyMatchingButtons = FindDescendants<Button>(window)
                .Where(item => LabelEquals(item, "Apply to all matching")).ToArray();
            if (applyMatchingButtons.Length != 1)
                throw new InvalidDataException("The rendered Filters and Bulk Actions expander exposed " +
                    applyMatchingButtons.Length + " 'Apply to all matching' buttons after expansion and layout");
            var applyMatching = applyMatchingButtons[0];
            SelectContent<ComboBoxItem>(matchingAction, "Upload");
            applyMatching.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            SelectContent<ComboBoxItem>(matchingAction, "Download");
            applyMatching.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (window.BootstrapRows.Single(item => item.Path == "docs/report.pdf").SelectedAction != "Upload" ||
                window.BootstrapRows.Single(item => item.Path == "Folder A").SelectedAction != "Upload" ||
                window.BootstrapRows.Single(item => item.Path == "notes.txt").SelectedAction != "Download")
                throw new InvalidDataException("Bulk all-matching Upload/Download did not preserve direction fences.");
            // Column redistribution is queued below Render priority. Drain that layout work before
            // measuring/capturing, otherwise a valid table can be captured at its transient minimums.
            grid.UpdateLayout();
            var layoutFrame = new DispatcherFrame();
            _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => layoutFrame.Continue = false);
            Dispatcher.PushFrame(layoutFrame);
            grid.UpdateLayout();
            Console.WriteLine($"First-sync measured viewport: visible={grid.IsVisible}, grid={grid.ActualWidth:0.0}x{grid.ActualHeight:0.0}, window={window.ActualWidth:0.0}x{window.ActualHeight:0.0}");
            Console.WriteLine("First-sync measured columns: " + string.Join("; ", grid.Columns.Select(column =>
                $"{column.Header}: actual={column.ActualWidth:0.0}, requested={column.Width}, min={column.MinWidth:0.0}, max={column.MaxWidth:0.0}, display={column.DisplayIndex}")));
            if (!grid.IsVisible || grid.ActualWidth < 600 || grid.Columns[0].ActualWidth < 240 ||
                grid.Columns[1].ActualWidth < 60 || grid.Columns[2].ActualWidth < 70 || grid.Columns[3].ActualWidth > 240 ||
                !FindDescendants<TextBlock>(grid).Any(text => text.Text == localRow.Path && text.IsVisible && text.ActualWidth >= 240))
                throw new InvalidDataException("The rendered first-sync table does not expose readable item/type/format/action columns and file paths.");
            FocusedUiAcceptanceContract.Capture(window, Path.Combine(outputRoot,
                "focused-first-sync-bulk-actions.png"));
            grid.SelectedItems.Clear();
            grid.SelectedItems.Add(window.BootstrapRows.Single(item => item.Path == "notes.txt"));
            SelectContent<ComboBoxItem>(selectedAction, "Skip for now");
            applySelected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            SyncBootstrapActionPlan? captured = null;
            MainWindow.BootstrapApprovalConfirmationForTest = preview =>
                preview.Contains("Copy 2 new item(s) from Left", StringComparison.Ordinal) &&
                preview.Contains("Copy 0 new item(s) from Right", StringComparison.Ordinal) &&
                preview.Contains("Skip 1 item(s)", StringComparison.Ordinal);
            MainWindow.BootstrapActionPlanForTest = (plan, _) =>
            {
                captured = plan;
                return Task.FromResult(new SyncBootstrapActionResult(pair, new SyncRunResult(pair.Id, []),
                    AppliedItems: 2, SkippedItems: 1, FirstSyncComplete: false));
            };
            Invoke(window, "ApproveBootstrap_Click", Required(window, "ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => captured is not null && !window.RunActiveForTest,
                "The actual first-sync approval did not complete.");
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["docs/report.pdf"] = "Upload", ["notes.txt"] = "Skip for now", ["Folder A"] = "Upload"
            };
            if (captured is null || captured.LocalScanGenerationId != "local-generation-1" ||
                captured.DropboxScanGenerationId != "dropbox-generation-1" ||
                captured.Choices.Count != expected.Count || captured.Choices.Any(choice =>
                    !expected.TryGetValue(choice.RelativePath, out var action) || choice.Action != action) ||
                captured.Choices.Any(choice => choice.RelativePath.StartsWith("excluded", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The actual first-sync surface did not bind the exact reviewed plan.");

            var actionApplied = false;
            var failRefresh = false;
            MainWindow.BootstrapActionPlanForTest = (plan, _) =>
            {
                actionApplied = true;
                failRefresh = true;
                return Task.FromResult(new SyncBootstrapActionResult(pair,
                    new SyncRunResult(pair.Id, [new SyncDecision("docs/report.pdf", SyncDecisionKind.Uploaded,
                        "Created in Dropbox")]), 1, 1, false));
            };
            MainWindow.BootstrapReviewPageLoaderForTest = (id, take, offset, search, group, kind, type) =>
            {
                if (failRefresh) throw new InvalidOperationException("post-effect-refresh-probe");
                var filtered = Filter(search, group, kind, type);
                return Task.FromResult(new SyncBootstrapReviewPage(filtered.Take(take).ToArray(), 0,
                    filtered.Count, inventory.Count, 2, 1, 1, false, true,
                    "local-generation-1", "dropbox-generation-1"));
            };
            MainWindow.ActivityProjectionLoaderForTest = (take, offset, search, filter, itemType, id, sinceUtc, accountId) =>
                Task.FromResult<IReadOnlyList<SyncAuthorityActivityProjection>>([
                    new(DateTimeOffset.UtcNow, pair.Id, "docs/report.pdf", "Uploaded",
                        "Created in Dropbox", "first-sync-effect", "File", "PDF")
                ]);
            MainWindow.BootstrapApprovalConfirmationForTest = _ => true;
            Invoke(window, "ApproveBootstrap_Click", Required(window, "ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => actionApplied && !window.RunActiveForTest,
                "The post-effect refresh probe did not complete.");
            Invoke(window, "RefreshWorkspace");
            if (!((TextBlock)Required(window, "PhaseText")).Text.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
                ((TextBlock)Required(window, "PhaseText")).Text.Contains("provider failure", StringComparison.OrdinalIgnoreCase) ||
                !window.ActivityRows.Any(item => item.Path == "docs/report.pdf" && item.Outcome == "Uploaded") ||
                window.BootstrapRows.Count != 0 || ((Button)Required(window, "ApproveBootstrapButton")).IsEnabled ||
                !((TextBlock)Required(window, "BootstrapSummary")).Text.Contains("could not be loaded", StringComparison.Ordinal))
                throw new InvalidDataException("A completed first-sync effect was relabelled as a provider failure. " +
                    $"phase={((TextBlock)Required(window, "PhaseText")).Text}; " +
                    $"activity={string.Join(',', window.ActivityRows.Select(item => item.Path + ':' + item.Outcome))}; " +
                    $"remainingRows={window.BootstrapRows.Count}");
            // The current review intentionally clears stale rows after a failed reload. The saved
            // skip choice must survive, but only become actionable after the same plan reloads.
            failRefresh = false;
            PumpTask(window, window.RefreshBootstrapReviewForTestAsync());
            if (window.BootstrapRows.Single(item => item.Path == "notes.txt").SelectedAction != "Skip for now")
                throw new InvalidDataException("Retrying the same first-sync inventory lost its saved Skip choice.");
            passed.Add("TC-SS-004.a-c actual first-sync filters, choices, plan binding and post-effect refresh semantics");
        }
        finally
        {
            MainWindow.BootstrapReviewLoaderForTest = null;
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            MainWindow.ActivityProjectionLoaderForTest = null;
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            MainWindow.BootstrapActionPlanForTest = null;
            store.SaveAsync(new SyncPairSet([original], original.Id)).GetAwaiter().GetResult();
            window.LoadPairSetForTest(new SyncPairSet([original], original.Id));
            window.ApplyFolderHierarchyPresentationForTest(Health(original, true, true));
        }
    }

    private static void AssertActivitySurface(MainWindow window, SyncPair pair, string outputRoot,
        ICollection<string> passed)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 225).Select(index => new SyncAuthorityActivityProjection(
                now.AddMinutes(-index), pair.Id, index % 11 == 0 ? $"folders/folder-{index:D3}" :
                    index % 2 == 0 ? $"reports/item-{index:D3}.pdf" : $"notes/item-{index:D3}.txt",
                (index % 5) switch { 0 => "Uploaded", 1 => "Downloaded", 2 => "Moved", 3 => "Conflict", _ => "Recovery copy saved" },
                index == 18 ? "needle-detail" : "retained action", $"run-{index % 7}",
                index % 11 == 0 ? "Folder" : "File",
                index % 11 == 0 ? "Folder" : index % 2 == 0 ? "PDF" : "TXT"))
            .Append(new SyncAuthorityActivityProjection(now.AddSeconds(2), pair.Id, "LICENSE",
                "Uploaded", "retained extensionless file", "activity-extensionless", "File", "File"))
            .Append(new SyncAuthorityActivityProjection(now.AddSeconds(1), pair.Id, "archive.v1",
                "Uploaded", "retained dotted folder", "activity-dotted-folder", "Folder", "Folder"))
            .Append(new SyncAuthorityActivityProjection(now.AddDays(-45), "other-pair", "old/archive.zip",
                "Deleted", "outside date range", "run-old", "File", "ZIP")).ToArray();
        MainWindow.ActivityProjectionLoaderForTest = (take, offset, search, filter, itemType, pairId, sinceUtc, accountId) =>
        {
            IEnumerable<SyncAuthorityActivityProjection> filtered = rows;
            if (!string.IsNullOrWhiteSpace(pairId)) filtered = filtered.Where(item => item.PairId == pairId);
            if (sinceUtc is not null) filtered = filtered.Where(item => item.OccurredUtc >= sinceUtc);
            if (!string.IsNullOrWhiteSpace(search)) filtered = filtered.Where(item =>
                new[] { item.RelativePath, item.Outcome, item.Reason, item.OperationId ?? string.Empty }
                    .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)));
            filtered = filter switch
            {
                "Transfers" => filtered.Where(item => item.Outcome is "Uploaded" or "Downloaded"),
                "Moves" => filtered.Where(item => item.Outcome == "Moved"),
                "Conflicts" => filtered.Where(item => item.Outcome == "Conflict"),
                "Recovery / deletions" => filtered.Where(item => item.Outcome.Contains("Recovery", StringComparison.Ordinal) || item.Outcome == "Deleted"),
                "Needs attention" => filtered.Where(item => item.Outcome == "Conflict"),
                _ => filtered
            };
            filtered = itemType switch
            {
                "Folders" => filtered.Where(item => item.ItemKind == "Folder"),
                "Files" => filtered.Where(item => item.ItemKind == "File"),
                not null when itemType != "All types" => filtered.Where(item =>
                    string.Equals(item.FileType, itemType.TrimStart('.'), StringComparison.OrdinalIgnoreCase)),
                _ => filtered
            };
            return Task.FromResult<IReadOnlyList<SyncAuthorityActivityProjection>>(filtered.Skip(offset).Take(take).ToArray());
        };
        MainWindow.PendingQueueCountLoaderForTest = () => Task.FromResult(321);
        var copiedText = string.Empty;
        MainWindow.ClipboardTextSetterForTest = value => copiedText = value;
        try
        {
            Navigate(window, "Activity");
            PumpTask(window, window.LoadQueuePageForTestAsync());
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count != PageSize || !((Button)Required(window, "LoadMoreActivityButton")).IsEnabled ||
                ((TextBlock)Required(window, "QueueCount")).Text != "321")
                throw new InvalidDataException("Activity paging or the unfiltered queue badge is incorrect.");
            PumpTask(window, window.LoadActivityPageForTestAsync(reset: false));
            if (window.ActivityRows.Count != rows.Length || ((TextBlock)Required(window, "QueueCount")).Text != "321")
                throw new InvalidDataException("Activity Load more changed the global queue count or omitted retained rows.");

            var pairFilter = (ComboBox)Required(window, "ActivityPairBox");
            var actionFilter = (ComboBox)Required(window, "ActivityFilterBox");
            var typeFilter = (ComboBox)Required(window, "ActivityTypeFilter");
            var dateFilter = (ComboBox)Required(window, "ActivityDateBox");
            var searchBox = (TextBox)Required(window, "ActivitySearchBox");

            pairFilter.SelectedItem = window.ActivityPairChoices.Single(item => item.Id == pair.Id);
            SelectContent<ComboBoxItem>(actionFilter, "All actions");
            SelectContent<ComboBoxItem>(typeFilter, "Files");
            SelectContent<ComboBoxItem>(dateFilter, "All time");
            searchBox.Text = string.Empty;
            PumpTask(window, window.LoadActivityPageForTestAsync());
            window.ActivityRows.Clear();
            PresentLive("activity-extensionless", "LICENSE", "Upload", "Starting", "File", "File");
            PresentLive("activity-extensionless", "LICENSE", "Upload", "Completed", "File", "File");
            if (window.ActivityRows.Count != 2 || window.ActivityRows.Any(item => item.Path != "LICENSE" ||
                    item.OperationId != "activity-extensionless" || item.ItemKind != "File" ||
                    item.FileType != "File") || window.ActivityRows[0].Outcome != "Completed upload" ||
                window.ActivityRows[1].Outcome != "Starting upload")
                throw new InvalidDataException("The live Activity consumer misclassified an extensionless file.");

            SelectContent<ComboBoxItem>(typeFilter, "Folders");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            window.ActivityRows.Clear();
            PresentLive("activity-extensionless", "LICENSE", "Upload", "Starting", "File", "File");
            PresentLive("activity-extensionless", "LICENSE", "Upload", "Completed", "File", "File");
            if (window.ActivityRows.Count != 0)
                throw new InvalidDataException("The live Activity consumer admitted an extensionless file under Folders.");

            PresentLive("activity-dotted-folder", "archive.v1", "Upload", "Starting", "Folder", "Folder");
            PresentLive("activity-dotted-folder", "archive.v1", "Upload", "Completed", "Folder", "Folder");
            if (window.ActivityRows.Count != 2 || window.ActivityRows.Any(item => item.Path != "archive.v1" ||
                    item.OperationId != "activity-dotted-folder" || item.ItemKind != "Folder" ||
                    item.FileType != "Folder") || window.ActivityRows[0].Outcome != "Completed upload" ||
                window.ActivityRows[1].Outcome != "Starting upload")
                throw new InvalidDataException("The live Activity consumer misclassified a dotted folder.");

            SelectContent<ComboBoxItem>(typeFilter, "Files");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            window.ActivityRows.Clear();
            PresentLive("activity-dotted-folder", "archive.v1", "Upload", "Starting", "Folder", "Folder");
            PresentLive("activity-dotted-folder", "archive.v1", "Upload", "Completed", "Folder", "Folder");
            if (window.ActivityRows.Count != 0)
                throw new InvalidDataException("The live Activity consumer admitted a dotted folder under Files.");

            searchBox.Text = "LICENSE";
            PumpTask(window, window.LoadActivityPageForTestAsync());
            // This synthetic retained summary is a distinct observation from the
            // two live phase events above. A read must not erase those live logs.
            if (window.ActivityRows.Count != 3 || window.ActivityRows.Count(item =>
                    item.Reason == "retained extensionless file") != 1 ||
                window.ActivityRows.Any(item => item.OperationId != "activity-extensionless" ||
                    item.Path != "LICENSE" || item.ItemKind != "File"))
                throw new InvalidDataException("The retained Activity consumer did not return the exact extensionless-file operation.");
            SelectContent<ComboBoxItem>(typeFilter, "Folders");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count != 0)
                throw new InvalidDataException("The retained Activity consumer admitted the extensionless file under Folders.");

            searchBox.Text = "archive.v1";
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count != 3 || window.ActivityRows.Count(item =>
                    item.Reason == "retained dotted folder") != 1 ||
                window.ActivityRows.Any(item => item.OperationId != "activity-dotted-folder" ||
                    item.Path != "archive.v1" || item.ItemKind != "Folder"))
                throw new InvalidDataException("The retained Activity consumer did not return the exact dotted-folder operation.");
            SelectContent<ComboBoxItem>(typeFilter, "Files");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count != 0)
                throw new InvalidDataException("The retained Activity consumer admitted the dotted folder under Files.");

            searchBox.Text = string.Empty;
            SelectContent<ComboBoxItem>(typeFilter, "All types");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Any(item => item.PairId != pair.Id))
                throw new InvalidDataException("The Activity pair filter admitted another pair.");
            pairFilter.SelectedIndex = 0;
            SelectContent<ComboBoxItem>(actionFilter, "Moves");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count == 0 || window.ActivityRows.Any(item => item.Outcome != "Moved"))
                throw new InvalidDataException("The Activity action filter did not isolate moves.");
            SelectContent<ComboBoxItem>(actionFilter, "All actions");
            SelectContent<ComboBoxItem>(dateFilter, "Last 7 days");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Any(item => item.Path == "old/archive.zip"))
                throw new InvalidDataException("The Activity date filter admitted an older retained event.");
            SelectContent<ComboBoxItem>(dateFilter, "All time");
            SelectContent<ComboBoxItem>(typeFilter, "Folders");
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count == 0 || window.ActivityRows.Any(item => item.ItemKind != "Folder"))
                throw new InvalidDataException("The Activity item filter did not isolate folders.");
            typeFilter.Text = "PDF";
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count == 0 || window.ActivityRows.Any(item => item.FileType != "PDF"))
                throw new InvalidDataException("The Activity file-type filter did not isolate PDFs.");
            SelectContent<ComboBoxItem>(typeFilter, "All types");
            searchBox.Text = "run-6";
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count == 0 || window.ActivityRows.Any(item => item.OperationId != "run-6"))
                throw new InvalidDataException("The Activity run-ID search did not isolate its retained run.");
            FocusedUiAcceptanceContract.Capture(window, Path.Combine(outputRoot,
                "focused-activity-type-and-run-filter.png"));

            void PresentLive(string operationId, string path, string action, string stage,
                string itemKind, string fileType)
            {
                var task = Invoke(window, "PresentBatchProgressAsync",
                    new SyncAuthorityBatchProgress(pair, "Running", null, null, null,
                        new SyncAuthorityLiveAction(operationId, path, action, stage,
                            stage + " " + path, now, itemKind, fileType)), "Manual", false) as Task
                    ?? throw new InvalidDataException("The live Activity consumer did not return a task.");
                PumpTask(window, task);
            }

            searchBox.Text = "needle-detail";
            SelectContent<ComboBoxItem>(actionFilter, "Conflicts");
            SelectContent<ComboBoxItem>(dateFilter, "Last 7 days");
            pairFilter.SelectedItem = window.ActivityPairChoices.Single(item => item.Id == pair.Id);
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count != 1 || window.ActivityRows[0].Path != "reports/item-018.pdf" ||
                ((TextBlock)Required(window, "QueueCount")).Text != "321")
                throw new InvalidDataException("Combined Activity pair/date/action/text/run filters did not bind exactly.");

            var grid = (DataGrid)Required(window, "ActivityGrid");
            grid.SelectedItem = window.ActivityRows[0];
            Invoke(window, "CopyActivityPath_Click", grid, new RoutedEventArgs());
            if (copiedText != "reports/item-018.pdf") throw new InvalidDataException("Copy path copied the wrong activity row.");
            Invoke(window, "CopyActivityDetails_Click", grid, new RoutedEventArgs());
            if (!copiedText.Contains("run-4", StringComparison.Ordinal))
                throw new InvalidDataException("Copy details omitted the retained run ID.");
            Invoke(window, "OpenActivityPair_Click", grid, new RoutedEventArgs());
            if (((Border)Required(window, "PairsPage")).Visibility != Visibility.Visible)
                throw new InvalidDataException("Open folder pair did not navigate from retained Activity.");
            Navigate(window, "Activity");
            grid.SelectedItem = window.ActivityRows[0];
            Invoke(window, "ActivityContextMenu_Opening", grid.ContextMenu!, new RoutedEventArgs());
            if (((MenuItem)Required(window, "ActivityOpenRecoveryMenuItem")).IsEnabled)
                throw new InvalidDataException("An ineligible conflict row enabled Open Recovery.");
            Invoke(window, "OpenActivityRecovery_Click", grid, new RoutedEventArgs());
            if (((Border)Required(window, "ActivityPage")).Visibility != Visibility.Visible)
                throw new InvalidDataException("An ineligible activity action escaped its safe rejection.");

            searchBox.Clear();
            SelectContent<ComboBoxItem>(actionFilter, "Recovery / deletions");
            SelectContent<ComboBoxItem>(dateFilter, "All time");
            pairFilter.SelectedItem = window.ActivityPairChoices.Single(item => item.Id == pair.Id);
            PumpTask(window, window.LoadActivityPageForTestAsync());
            grid.SelectedItem = window.ActivityRows.First(item => item.Outcome.Contains("Recovery", StringComparison.Ordinal));
            Invoke(window, "ActivityContextMenu_Opening", grid.ContextMenu!, new RoutedEventArgs());
            if (!((MenuItem)Required(window, "ActivityOpenRecoveryMenuItem")).IsEnabled)
                throw new InvalidDataException("An eligible recovery row did not enable Open Recovery.");
            Invoke(window, "OpenActivityRecovery_Click", grid, new RoutedEventArgs());
            if (((Border)Required(window, "RecoveryPage")).Visibility != Visibility.Visible)
                throw new InvalidDataException("Open Recovery did not route from retained Activity.");

            Navigate(window, "Activity");
            grid.SelectedItem = null;
            copiedText = "activity-no-selection-sentinel";
            Invoke(window, "ActivityContextMenu_Opening", grid.ContextMenu!, new RoutedEventArgs());
            if (new[] { "ActivityOpenPairMenuItem", "ActivityOpenRecoveryMenuItem", "ActivityCopyPathMenuItem",
                    "ActivityCopyDetailsMenuItem" }.Select(name => (MenuItem)Required(window, name)).Any(item => item.IsEnabled))
                throw new InvalidDataException("Activity context actions were enabled without a selected row.");
            Invoke(window, "OpenActivityPair_Click", grid, new RoutedEventArgs());
            Invoke(window, "OpenActivityRecovery_Click", grid, new RoutedEventArgs());
            Invoke(window, "CopyActivityPath_Click", grid, new RoutedEventArgs());
            Invoke(window, "CopyActivityDetails_Click", grid, new RoutedEventArgs());
            if (((Border)Required(window, "ActivityPage")).Visibility != Visibility.Visible ||
                copiedText != "activity-no-selection-sentinel")
                throw new InvalidDataException("An Activity action escaped its no-selection rejection.");

            window.ActivityRows.Clear();
            var missingPair = new ActivityRow(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
                "missing-pair.txt", "Uploaded", "Retained after pair deletion", "Dropbox",
                "missing-pair", "Deleted pair", "retained-operation", "File", "TXT");
            window.ActivityRows.Add(missingPair);
            PumpUntil(window, () => grid.Items.Contains(missingPair),
                "The retained deleted-pair row did not reach the displayed activity projection.");
            grid.SelectedItem = missingPair;
            Invoke(window, "ActivityContextMenu_Opening", grid.ContextMenu!, new RoutedEventArgs());
            if (((MenuItem)Required(window, "ActivityOpenPairMenuItem")).IsEnabled ||
                ((MenuItem)Required(window, "ActivityOpenRecoveryMenuItem")).IsEnabled ||
                !((MenuItem)Required(window, "ActivityCopyPathMenuItem")).IsEnabled ||
                !((MenuItem)Required(window, "ActivityCopyDetailsMenuItem")).IsEnabled)
                throw new InvalidDataException("Activity actions did not distinguish retained history for a deleted pair.");
            Invoke(window, "OpenActivityPair_Click", grid, new RoutedEventArgs());
            Invoke(window, "OpenActivityRecovery_Click", grid, new RoutedEventArgs());
            Invoke(window, "CopyActivityPath_Click", grid, new RoutedEventArgs());
            if (((Border)Required(window, "ActivityPage")).Visibility != Visibility.Visible ||
                copiedText != "missing-pair.txt")
                throw new InvalidDataException("Retained history for a deleted pair enabled navigation or blocked Copy path.");
            Invoke(window, "CopyActivityDetails_Click", grid, new RoutedEventArgs());
            if (!copiedText.Contains("retained-operation", StringComparison.Ordinal))
                throw new InvalidDataException("Copy details omitted the retained operation for a deleted pair.");

            var synthetic = new ActivityRow(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
                "<run>", "Blocked", "Synthetic run summary", "Dropbox", pair.Id, pair.Name,
                "run-summary", "System", "System");
            window.ActivityRows.Clear();
            window.ActivityRows.Add(synthetic);
            PumpUntil(window, () => grid.Items.Contains(synthetic),
                "The system row did not reach the displayed activity projection.");
            grid.SelectedItem = synthetic;
            copiedText = "activity-synthetic-path-sentinel";
            Invoke(window, "ActivityContextMenu_Opening", grid.ContextMenu!, new RoutedEventArgs());
            if (((MenuItem)Required(window, "ActivityCopyPathMenuItem")).IsEnabled ||
                !((MenuItem)Required(window, "ActivityCopyDetailsMenuItem")).IsEnabled)
                throw new InvalidDataException("Activity synthetic-row action eligibility is incorrect.");
            Invoke(window, "CopyActivityPath_Click", grid, new RoutedEventArgs());
            if (copiedText != "activity-synthetic-path-sentinel")
                throw new InvalidDataException("Copy path escaped its synthetic-row rejection.");
            Invoke(window, "CopyActivityDetails_Click", grid, new RoutedEventArgs());
            if (!copiedText.Contains("run-summary", StringComparison.Ordinal))
                throw new InvalidDataException("Copy details rejected an eligible synthetic run summary.");

            Navigate(window, "Activity");
            searchBox.Text = "no-results-probe";
            SelectContent<ComboBoxItem>(actionFilter, "All actions");
            SelectContent<ComboBoxItem>(typeFilter, "All types");
            SelectContent<ComboBoxItem>(dateFilter, "All time");
            pairFilter.SelectedIndex = 0;
            PumpTask(window, window.LoadActivityPageForTestAsync());
            if (window.ActivityRows.Count != 0 || ((TextBlock)Required(window, "QueueCount")).Text != "321")
                throw new InvalidDataException("Activity no-results/reset state changed the global queue count.");
            searchBox.Clear();
            PumpTask(window, window.LoadActivityPageForTestAsync());
            var expectedLiveReasons = new[]
            {
                "Starting LICENSE", "Completed LICENSE", "Starting archive.v1", "Completed archive.v1"
            };
            if (window.ActivityRows.Count != PageSize + expectedLiveReasons.Length ||
                expectedLiveReasons.Any(reason => window.ActivityRows.Count(row => row.Reason == reason) != 1))
                throw new InvalidDataException("Clearing Activity filters did not restore the first retained page with each distinct live operation phase.");
            passed.Add("TC-SS-005.a-c retained Activity filters, paging, context actions and global queue count");
        }
        finally
        {
            MainWindow.ActivityProjectionLoaderForTest = null;
            MainWindow.PendingQueueCountLoaderForTest = null;
            MainWindow.ClipboardTextSetterForTest = null;
        }
    }

    private static void AssertInFlightResponsiveness(MainWindow window, SyncPair pair,
        IDropboxFolderBrowserService browser, List<string> dispatches, string localRoot, string outputRoot,
        ICollection<string> passed)
    {
        CancelDraft(window);
        Navigate(window, "Pairs");
        window.PrepareExistingPairEditorForTest(pair);
        window.ApplyFolderHierarchyPresentationForTest(Health(pair, true, true));
        var pairList = (DataGrid)Required(window, "PairList");
        var row = window.PairRows.Single(item => item.Id == pair.Id);
        pairList.ScrollIntoView(row);
        pairList.UpdateLayout();
        var rowContainer = pairList.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow ??
                           throw new InvalidDataException("The pair row was not rendered for active expansion proof.");
        var pairToggle = FindDescendants<Button>(rowContainer).Single(item => ReferenceEquals(item.Tag, row));
        pairToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        if (rowContainer.DetailsVisibility != Visibility.Collapsed)
            throw new InvalidDataException("The pair did not collapse before the active-sync expansion proof.");
        window.WindowState = WindowState.Normal;
        Pump(window);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        MainWindow.PairRunDispatcherForTest = (_, _) => { calls++; return gate.Task; };
        try
        {
            Invoke(window, "Run_Click", window, new RoutedEventArgs());
            PumpUntil(window, () => window.RunActiveForTest && calls == 1, "The in-flight UI run did not start.");
            pairToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => rowContainer.DetailsVisibility == Visibility.Visible &&
                                    ((FrameworkElement)Required(window, "PairEditorPanel")).Visibility == Visibility.Visible,
                "The pair row did not expand while sync was active.");
            var beforeLeft = window.Left;
            var beforeWidth = window.Width;
            window.Left = beforeLeft + 9;
            window.Width = beforeWidth + 11;
            Pump(window);
            if (Math.Abs(window.Left - (beforeLeft + 9)) > 0.5 || Math.Abs(window.Width - (beforeWidth + 11)) > 0.5)
                throw new InvalidDataException("The WPF dispatcher stopped processing window movement during sync.");
            PumpTask(window, window.PrepareDropboxBrowserForCurrentDraftForTestAsync(browser));
            PumpTask(window, window.ExpandDropboxTreeForTestAsync(pair.DropboxRoot));
            window.ExpandLocalTreeForTest(localRoot);
            Pump(window);
            var pairMenuItems = pairList.ContextMenu.Items.OfType<MenuItem>().ToArray();
            if (((TreeView)Required(window, "LocalRootTree")).IsEnabled == false ||
                ((TreeView)Required(window, "DropboxRootTree")).IsEnabled == false ||
                ((TextBox)Required(window, "FolderSelectionSearchBox")).IsEnabled == false ||
                ((CheckBox)Required(window, "PairEnabledBox")).IsEnabled ||
                ((CheckBox)Required(window, "PairDeletionBox")).IsEnabled ||
                ((ComboBox)Required(window, "PairStorageBox")).IsEnabled ||
                ((ComboBox)Required(window, "PairPriorityChoice")).IsEnabled ||
                ((ComboBox)Required(window, "RecoveryRetentionBox")).IsEnabled ||
                ((CheckBox)Required(window, "WholeRootSelectionBox")).IsEnabled ||
                ((Button)Required(window, "SavePairButton")).IsEnabled ||
                ((Button)Required(window, "AddPairButton")).IsEnabled ||
                ((Button)Required(window, "SyncAllNowButton")).IsEnabled ||
                ((Button)Required(window, "ConnectionReauthorizeButton")).IsEnabled ||
                ((Button)Required(window, "ConnectionDisconnectButton")).IsEnabled ||
                window.PairRows.Single(item => item.Id == pair.Id).CanChangeLiveSync ||
                ((MenuItem)Required(window, "PairDuplicateMenuItem")).IsEnabled ||
                ((MenuItem)Required(window, "PairDeleteMenuItem")).IsEnabled ||
                ((MenuItem)Required(window, "PairSyncNowMenuItem")).IsEnabled)
                throw new InvalidDataException("Active sync did not separate safe browsing from pair mutations.");
            pairToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => rowContainer.DetailsVisibility == Visibility.Collapsed,
                "The pair row did not collapse while sync was active.");
            pairToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => rowContainer.DetailsVisibility == Visibility.Visible,
                "The pair row did not reopen while sync was active.");
            FocusedUiAcceptanceContract.Capture(window, Path.Combine(outputRoot,
                "focused-active-sync-mutation-lock.png"));
            Invoke(window, "Run_Click", window, new RoutedEventArgs());
            Pump(window);
            if (calls != 1) throw new InvalidDataException("An active UI run admitted a second sync dispatch.");
            gate.SetResult();
            PumpUntil(window, () => !window.RunActiveForTest, "The UI did not restore after the in-flight run completed.");
            if (!((CheckBox)Required(window, "PairEnabledBox")).IsEnabled ||
                !((ComboBox)Required(window, "PairStorageBox")).IsEnabled ||
                !((Button)Required(window, "AddPairButton")).IsEnabled ||
                !window.PairRows.Single(item => item.Id == pair.Id).CanChangeLiveSync)
                throw new InvalidDataException("Pair controls did not restore after sync completion.");
            passed.Add("TC-SS-006.a-b responsive in-flight browsing, mutation lock and no reentrancy");
        }
        finally
        {
            gate.TrySetResult();
            MainWindow.PairRunDispatcherForTest = (pairs, trigger) =>
            {
                dispatches.Add(trigger + ":" + string.Join(',', pairs.Select(item => item.Id)));
                return Task.CompletedTask;
            };
        }
    }

    private static void AssertMonitorGenerationAndHealthSources(MainWindow window, SyncPair original,
        SyncPairStore store, ICollection<string> passed)
    {
        CancelDraft(window);
        var pair = original with { InitialReviewApproved = false, RecoveryRoot = null };
        var set = new SyncPairSet([pair], pair.Id);
        store.SaveAsync(set).GetAwaiter().GetResult();
        window.LoadPairSetForTest(set);
        window.PrepareExistingPairEditorForTest(pair);
        window.SetPairAttentionSourcesForTest(pair.Id, "endpoint-definitive", "monitor-old",
            "definitive-run-problem", projection: true);
        IReadOnlyList<SyncAuthorityMonitorProjection> projections =
            [new(pair.Id, "local", "Monitor unavailable", DateTimeOffset.UtcNow, 0)];
        MainWindow.MonitorProjectionLoaderForTest = _ => Task.FromResult(projections);
        var callbacks = new List<Func<string, Task>>();
        MainWindow.MonitorConfiguratorForTest = (pairs, run, health) =>
        {
            callbacks.Add(health);
            return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>(
                [new(pair.Id, "Monitoring", null, null)]);
        };
        MainWindow.MonitorStopperForTest = () => Task.CompletedTask;
        try
        {
            PumpTask(window, window.ConfigureLocalMonitorsForTestAsync(new SyncAutomationSettings(true, 15, false)));
            if (callbacks.Count != 1) throw new InvalidDataException("Live sync did not bind a current health callback.");
            PumpTask(window, callbacks[0](pair.Id));
            if (!window.PairAttentionSourcesForTest(pair.Id).Contains("monitor=Live monitoring", StringComparison.Ordinal))
                throw new InvalidDataException("The current monitor callback did not project its own attention source.");

            PumpTask(window, window.ToggleAutomaticSyncFromBackgroundAsync());
            var afterOff = window.PairAttentionSourcesForTest(pair.Id);
            if (!afterOff.Contains("monitor=<none>", StringComparison.Ordinal) ||
                !afterOff.Contains("endpoint=endpoint-definitive", StringComparison.Ordinal) ||
                !afterOff.Contains("run=definitive-run-problem", StringComparison.Ordinal))
                throw new InvalidDataException("Turning Live sync off cleared unrelated health sources.");
            PumpTask(window, callbacks[0](pair.Id));
            if (!window.PairAttentionSourcesForTest(pair.Id).Contains("monitor=<none>", StringComparison.Ordinal))
                throw new InvalidDataException("A late monitor callback repopulated attention after Live sync was off.");

            projections = [new(pair.Id, "local", "Monitoring", DateTimeOffset.UtcNow, 0)];
            PumpTask(window, window.ToggleAutomaticSyncFromBackgroundAsync());
            if (callbacks.Count != 2) throw new InvalidDataException("Live sync restart reused a stale callback generation.");
            PumpTask(window, callbacks[1](pair.Id));
            var restarted = window.PairAttentionSourcesForTest(pair.Id);
            if (!restarted.Contains("monitor=<none>", StringComparison.Ordinal) ||
                !restarted.Contains("endpoint=endpoint-definitive", StringComparison.Ordinal) ||
                !restarted.Contains("run=definitive-run-problem", StringComparison.Ordinal))
                throw new InvalidDataException("Healthy monitoring erased an endpoint or run attention source.");

            window.SetPairAttentionSourcesForTest(pair.Id, "transient-endpoint", "monitor-still-present",
                "temporarily unavailable during earlier check", projection: true);
            window.ApplyFolderHierarchyPresentationForTest(Health(pair, true, true));
            var healthy = window.PairAttentionSourcesForTest(pair.Id);
            if (!healthy.Contains("endpoint=<none>", StringComparison.Ordinal) ||
                !healthy.Contains("monitor=monitor-still-present", StringComparison.Ordinal) ||
                !healthy.Contains("run=<none>", StringComparison.Ordinal) ||
                !healthy.Contains("projection=True", StringComparison.Ordinal))
                throw new InvalidDataException("Healthy endpoint recheck did not retire only matching transient sources.");
            window.PrepareExistingPairEditorForTest(pair);
            ((TabControl)Required(window, "PairEditorTabs")).SelectedIndex = 3;
            PumpTask(window, window.RefreshPairSyncStatusForTestAsync());
            var firstSyncState = ((TextBlock)Required(window, "PairHealthFirstSyncText")).Text;
            var recoveryState = ((TextBlock)Required(window, "PairHealthRecoveryText")).Text;
            if (!firstSyncState.Contains("Review required", StringComparison.Ordinal) ||
                recoveryState != "Not configured")
                throw new InvalidDataException("Sync status conflated first-sync or Recovery state with monitor health. " +
                                               $"First sync={firstSyncState}; Recovery={recoveryState}.");
            passed.Add("TC-SS-008.a-c health-source separation and stale monitor callback rejection");
        }
        finally
        {
            MainWindow.MonitorProjectionLoaderForTest = null;
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.MonitorStopperForTest = null;
            if (!window.AutomaticSyncEnabled) PumpTask(window, window.ToggleAutomaticSyncFromBackgroundAsync());
            store.SaveAsync(new SyncPairSet([original], original.Id)).GetAwaiter().GetResult();
            window.LoadPairSetForTest(new SyncPairSet([original], original.Id));
            window.SetPairAttentionSourcesForTest(original.Id, null, null, null, false);
            window.ApplyFolderHierarchyPresentationForTest(Health(original, true, true));
        }
    }

    private static void AssertConnectionLifecycle(MainWindow window, SyncPair pair, SyncPairStore store,
        ICollection<string> passed)
    {
        CancelDraft(window);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:guided-connection", "root-guided", "home-guided",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Guided connection", Email: "guided@example.invalid", AccountType: "basic");
        var connected = true;
        var reconnectCalls = 0;
        var disconnectCalls = 0;
        MainWindow.DropboxConnectionCheckForTest = () => Task.FromResult(connected);
        MainWindow.DropboxConnectionStatusLoaderForTest = verifyLive => Task.FromResult(connected
            ? new DropboxConnectionStatus(binding, true, verifyLive,
                verifyLive ? "Connected and verified." : "Connected; live verification pending.", null, 1)
            : new DropboxConnectionStatus(null, false, false, "Disconnected", null, 0));
        MainWindow.DropboxReconnectForTest = () =>
        {
            reconnectCalls++;
            connected = true;
            return Task.CompletedTask;
        };
        MainWindow.DropboxDisconnectForTest = () =>
        {
            disconnectCalls++;
            connected = false;
            return Task.CompletedTask;
        };
        MainWindow.MonitorConfiguratorForTest = (_, _, _) =>
            Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
        MainWindow.MonitorStopperForTest = () => Task.CompletedTask;
        try
        {
            Navigate(window, "Connections");
            PumpTask(window, window.RefreshConnectionStateForTestAsync(verifyLive: true));
            if (((TextBlock)Required(window, "HeaderState")).Text != "Dropbox connected" ||
                !((Button)Required(window, "ConnectionReauthorizeButton")).IsEnabled ||
                !((Button)Required(window, "ConnectionDisconnectButton")).IsEnabled)
                throw new InvalidDataException("The verified connected account did not expose its lifecycle controls.");

            Invoke(window, "Reconnect_Click", Required(window, "ConnectionReauthorizeButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(window, () => reconnectCalls == 1 &&
                System.Windows.Automation.AutomationProperties.GetItemStatus(
                    (Button)Required(window, "ConnectionReauthorizeButton")) != "In progress" &&
                ((TextBlock)Required(window, "HeaderState")).Text == "Dropbox connected",
                "Reconnect did not complete through the actual UI handler.");

            RunDialog(window, "Disconnect_Click", confirm: true, dialog =>
            {
                if (!LabelEquals((Button)dialog.FindName("PrimaryButton"), "Disconnect") ||
                    !((Button)dialog.FindName("SecondaryButton")).IsKeyboardFocused)
                    throw new InvalidDataException("Disconnect did not use the app-owned destructive confirmation.");
            }, Required(window, "ConnectionDisconnectButton"));
            PumpUntil(window, () => disconnectCalls == 1 &&
                System.Windows.Automation.AutomationProperties.GetItemStatus(
                    (Button)Required(window, "ConnectionDisconnectButton")) != "In progress" &&
                ((TextBlock)Required(window, "ConnectionText")).Text == "Not connected",
                "Disconnect did not complete through the actual UI handler.");
            if (store.Load().Pairs.All(item => item.Id != pair.Id) ||
                ((Button)Required(window, "ConnectionDisconnectButton")).IsEnabled)
                throw new InvalidDataException("Disconnect removed a retained pair or left disconnect enabled.");
            passed.Add("TC-SS-010 actual reconnect/disconnect lifecycle retains folder pairs");
        }
        finally
        {
            connected = true;
            MainWindow.DropboxReconnectForTest = null;
            MainWindow.DropboxDisconnectForTest = null;
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.MonitorStopperForTest = null;
            MainWindow.DropboxConnectionCheckForTest = () => Task.FromResult(true);
            MainWindow.DropboxConnectionStatusLoaderForTest = verifyLive => Task.FromResult(
                new DropboxConnectionStatus(binding, true, verifyLive,
                    verifyLive ? "Connected and verified." : "Connected; live verification pending.", null, 1));
            PumpTask(window, window.RefreshConnectionStateForTestAsync(verifyLive: false));
        }
    }

    private static void AssertPairLifecycleAndDialog(App app, MainWindow window, SyncPair original, SyncPairStore store,
        IDropboxFolderBrowserService browser, string stateRoot, string outputRoot, ICollection<string> passed)
    {
        CancelDraft(window);
        var copyName = "Copy of " + original.Name;
        var occupiedRoot = Path.Combine(stateRoot, "occupied-ui-pair");
        Directory.CreateDirectory(occupiedRoot);
        using var occupiedLease = PilotDirectoryLease.Open(occupiedRoot);
        var occupied = original with { Id = "f123456789abcdefabcd", Name = copyName,
            LocalRoot = occupiedRoot, LocalVolumeSerialNumber = occupiedLease.Identity.VolumeSerialNumber,
            LocalRootFileId = occupiedLease.Identity.FileId,
            DropboxRoot = "/Occupied", DropboxFolderId = "id:occupied" };
        var deleteRoot = Path.Combine(stateRoot, "delete-ui-pair");
        Directory.CreateDirectory(deleteRoot);
        var sentinel = Path.Combine(deleteRoot, "must-remain.txt");
        File.WriteAllText(sentinel, "retained");
        using var lease = PilotDirectoryLease.Open(deleteRoot);
        var deletion = original with { Id = "9123456789abcdefabcd", Name = "Delete UI pair", LocalRoot = deleteRoot,
            LocalVolumeSerialNumber = lease.Identity.VolumeSerialNumber, LocalRootFileId = lease.Identity.FileId,
            DropboxRoot = "/Delete UI pair", DropboxFolderId = "id:delete-ui-pair" };
        var duplicateSource = original with
        {
            Priority = 50,
            PropagateDeletions = true,
            FilesOnDemandEnabled = true,
            RecoveryRetentionDays = 180,
            SelectionDefaultIncluded = false,
            SelectionRules =
            [
                new SyncSelectionRule("Match", true, "id:match"),
                new SyncSelectionRule("Match/Private", false, "id:private")
            ]
        };
        var set = new SyncPairSet([duplicateSource, occupied, deletion], duplicateSource.Id);
        store.SaveAsync(set).GetAwaiter().GetResult();
        window.LoadPairSetForTest(set);
        var remoteBefore = browser.ListFoldersAsync().GetAwaiter().GetResult().Folders
            .Select(item => (item.FolderId, item.Path)).OrderBy(item => item.FolderId, StringComparer.Ordinal).ToArray();
        var list = (DataGrid)Required(window, "PairList");
        list.SelectedItem = window.PairRows.Single(item => item.Id == duplicateSource.Id);
        var beforeDuplicate = File.ReadAllText(sentinel) + store.Load().Pairs.Count;
        Invoke(window, "DuplicatePair_Click", list, new RoutedEventArgs());
        Pump(window);
        if (((TextBox)Required(window, "PairNameBox")).Text != copyName + " (2)" ||
            ((TextBox)Required(window, "LocalRootBox")).Text != duplicateSource.LocalRoot ||
            ((TextBox)Required(window, "DropboxRootBox")).Text != duplicateSource.DropboxRoot ||
            ((CheckBox)Required(window, "PairEnabledBox")).IsChecked != false ||
            ((CheckBox)Required(window, "PairDeletionBox")).IsChecked != duplicateSource.PropagateDeletions ||
            ((ComboBox)Required(window, "PairStorageBox")).SelectedIndex != 1 ||
            ((ComboBox)Required(window, "PairPriorityChoice")).SelectedIndex != 0 ||
            ((ComboBox)Required(window, "RecoveryRetentionBox")).SelectedIndex != 2 ||
            ((TextBox)Required(window, "RecoveryRootBox")).Text != duplicateSource.RecoveryRoot?.RootPath ||
            window.PendingSelectionDefaultIncludedForTest != duplicateSource.SelectionDefaultIncluded ||
            !window.PendingSelectionRulesForTest.SequenceEqual(SyncSelectionPolicy.Normalize(duplicateSource.SelectionRules)) ||
            store.Load().Pairs.Count != 3 || File.ReadAllText(sentinel) + store.Load().Pairs.Count != beforeDuplicate)
            throw new InvalidDataException("Duplicate pair did not copy every setting into an isolated, paused, collision-safe editable draft.");
        CancelDraft(window);

        list.SelectedItem = window.PairRows.Single(item => item.Id == deletion.Id);
        var dialogInspected = false;
        RunDialog(window, "DeletePair_Click", confirm: false, dialog =>
        {
            dialogInspected = true;
            FocusedUiAcceptanceContract.CaptureVisual((FrameworkElement)dialog.Content,
                Path.Combine(outputRoot, "focused-destructive-dialog.png"));
            if (!ReferenceEquals(dialog.Owner, window) || dialog.ShowInTaskbar || dialog.WindowStyle != WindowStyle.None ||
                !LabelEquals((Button)dialog.FindName("PrimaryButton"), "Delete pair") ||
                !SameBrush(((Button)dialog.FindName("PrimaryButton")).Background,
                    (Brush)Application.Current.Resources["ErrorBrush"]) ||
                !((Button)dialog.FindName("SecondaryButton")).IsKeyboardFocused)
                throw new InvalidDataException("The app-owned destructive dialog has the wrong owner, focus or controls.");
            var work = SystemParameters.WorkArea;
            if (dialog.Left < work.Left - 1 || dialog.Top < work.Top - 1 ||
                dialog.Left + dialog.ActualWidth > work.Right + 1 || dialog.Top + dialog.ActualHeight > work.Bottom + 1)
                throw new InvalidDataException("The app-owned dialog escaped the Windows work area.");
        });
        Pump(window);
        if (!dialogInspected || store.Load().Pairs.All(item => item.Id != deletion.Id) || !File.Exists(sentinel))
            throw new InvalidDataException("Cancelling Delete pair changed pair or local-file state.");

        window.LoadPairSetForTest(new SyncPairSet([duplicateSource, occupied, deletion], deletion.Id));
        list.SelectedItem = window.PairRows.Single(item => item.Id == deletion.Id);
        ((ComboBox)Required(window, "ActivityPairBox")).SelectedIndex = 0;
        SelectContent<ComboBoxItem>((ComboBox)Required(window, "ActivityFilterBox"), "All actions");
        SelectContent<ComboBoxItem>((ComboBox)Required(window, "ActivityTypeFilter"), "All types");
        SelectContent<ComboBoxItem>((ComboBox)Required(window, "ActivityDateBox"), "All time");
        ((TextBox)Required(window, "ActivitySearchBox")).Clear();
        var retainedProjection = new SyncAuthorityActivityProjection(DateTimeOffset.UtcNow, deletion.Id,
            "retained-before-delete.txt", "Uploaded", "Retained history probe", "delete-history-probe",
            "File", "TXT");
        MainWindow.ActivityProjectionLoaderForTest = (_, _, _, _, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<SyncAuthorityActivityProjection>>([retainedProjection]);
        PumpTask(window, window.LoadActivityPageForTestAsync());
        if (!window.ActivityRows.Any(item => item.Path == retainedProjection.RelativePath))
            throw new InvalidDataException("The pre-delete retained Activity projection was not visible.");
        RunDialog(window, "DeletePair_Click", confirm: true, _ => { }, Required(window, "PairDeleteMenuItem"));
        PumpUntil(window, () => list.IsEnabled && store.Load().Pairs.All(item => item.Id != deletion.Id),
            "The actual Delete pair confirmation did not retire the selected pair.");
        var remoteAfter = browser.ListFoldersAsync().GetAwaiter().GetResult().Folders
            .Select(item => (item.FolderId, item.Path)).OrderBy(item => item.FolderId, StringComparer.Ordinal).ToArray();
        PumpTask(window, window.LoadActivityPageForTestAsync());
        if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "retained" ||
            !remoteBefore.SequenceEqual(remoteAfter) ||
            !window.ActivityRows.Any(item => item.Path == retainedProjection.RelativePath) ||
            !((TextBox)Required(window, "ConsoleText")).Text.Contains("retained", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Delete pair changed local/Dropbox data or failed to retain Activity history. " +
                $"local={File.Exists(sentinel)}; remote={remoteBefore.SequenceEqual(remoteAfter)}; " +
                $"history={window.ActivityRows.Any(item => item.Path == retainedProjection.RelativePath)}; " +
                $"console={((TextBox)Required(window, "ConsoleText")).Text}");
        MainWindow.ActivityProjectionLoaderForTest = null;

        RunInformationDialog(app, window, dialog =>
        {
            FocusedUiAcceptanceContract.CaptureVisual((FrameworkElement)dialog.Content,
                Path.Combine(outputRoot, "focused-information-dialog.png"));
            if (!ReferenceEquals(dialog.Owner, window) || dialog.ShowInTaskbar ||
                dialog.WindowStyle != WindowStyle.None ||
                ((Button)dialog.FindName("SecondaryButton")).Visibility != Visibility.Collapsed ||
                !((Button)dialog.FindName("PrimaryButton")).IsKeyboardFocused)
                throw new InvalidDataException("The app-owned information dialog has the wrong owner, focus or controls.");
        });
        store.SaveAsync(new SyncPairSet([original], original.Id)).GetAwaiter().GetResult();
        window.LoadPairSetForTest(new SyncPairSet([original], original.Id));
        passed.Add("TC-SS-007.a-b actual duplicate/delete lifecycle and app-owned destructive dialog");
        passed.Add("TC-SS-009.b app-owned destructive/information dialog ownership, placement and safe focus");
    }

    private static void AssertRenderedControlStates(MainWindow window, string outputRoot,
        ICollection<string> passed)
    {
        CancelDraft(window);
        Navigate(window, "Pairs");
        var button = (Button)Required(window, "SyncAllNowButton");
        var list = (DataGrid)Required(window, "PairList");
        var priorLeft = window.Left;
        var priorTop = window.Top;
        var priorShowInTaskbar = window.ShowInTaskbar;
        _ = GetCursorPos(out var priorCursor);
        try
        {
            var work = SystemParameters.WorkArea;
            window.ShowInTaskbar = false;
            window.Left = work.Left + 12;
            window.Top = work.Top + 12;
            window.Activate();
            window.UpdateLayout();
            button.BringIntoView();
            button.ApplyTemplate();
            Pump(window);

            var chrome = button.Template.FindName("Chrome", button) as Border ??
                         throw new InvalidDataException("The rendered button template did not expose its visual chrome.");
            _ = SetCursorPos((int)work.Right - 2, (int)work.Bottom - 2);
            Mouse.Synchronize();
            PumpUntil(window, () => !button.IsMouseOver, "The normal rendered button state could not be established.");
            if (!SameBrush(chrome.Background, (Brush)Application.Current.Resources["ControlSurface"]))
                throw new InvalidDataException("The normal rendered button state did not use the control surface.");
            FocusedUiAcceptanceContract.CaptureVisual(window,
                Path.Combine(outputRoot, "focused-control-state-normal.png"));

            var centre = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            _ = SetCursorPos((int)Math.Round(centre.X), (int)Math.Round(centre.Y));
            Mouse.Synchronize();
            PumpUntil(window, () => button.IsMouseOver, "The rendered hover state did not activate.");
            if (!SameBrush(chrome.Background, (Brush)Application.Current.Resources["HoverSurface"]))
                throw new InvalidDataException("The rendered hover state did not use the hover surface.");
            FocusedUiAcceptanceContract.CaptureVisual(window,
                Path.Combine(outputRoot, "focused-control-state-hover.png"));

            _ = SetCursorPos((int)work.Right - 2, (int)work.Bottom - 2);
            Mouse.Synchronize();
            window.Activate();
            _ = button.Focus();
            _ = Keyboard.Focus(button);
            PumpUntil(window, () => button.IsKeyboardFocused,
                "The rendered keyboard-focus state did not activate.");
            if (!SameBrush(chrome.BorderBrush, (Brush)Application.Current.Resources["FocusBrush"]) ||
                chrome.BorderThickness.Left < 1.5)
                throw new InvalidDataException("The rendered keyboard-focus state did not expose its focus ring.");

            var row = window.PairRows.FirstOrDefault() ??
                      throw new InvalidDataException("The rendered selection-state proof requires a folder pair.");
            list.SelectedItem = row;
            list.ScrollIntoView(row);
            list.UpdateLayout();
            Pump(window);
            var container = list.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow ??
                            throw new InvalidDataException("The selected folder-pair row was not rendered.");
            PumpUntil(window, () => container.IsSelected,
                "The rendered selected row state did not activate.");
            if (!SameBrush(container.Background, (Brush)Application.Current.Resources["RaisedSurface"]) ||
                container.BorderThickness.Left < 2.5)
                throw new InvalidDataException("The rendered selected row did not expose its selection state.");
            FocusedUiAcceptanceContract.CaptureVisual(window,
                Path.Combine(outputRoot, "focused-control-state-focus-selected.png"));
        }
        finally
        {
            _ = SetCursorPos(priorCursor.X, priorCursor.Y);
            window.Left = priorLeft;
            window.Top = priorTop;
            window.ShowInTaskbar = priorShowInTaskbar;
            Pump(window);
        }

        passed.Add("TC-SS-009.c rendered normal, hover, keyboard-focus, selected and destructive control states");
    }

    private static bool SameBrush(Brush? actual, Brush? expected) =>
        actual is SolidColorBrush actualSolid && expected is SolidColorBrush expectedSolid
            ? actualSolid.Color == expectedSolid.Color
            : Equals(actual, expected);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    private static SyncBootstrapReviewItem Item(SyncPair pair, string path, string group, string kind,
        string type, string recommendation, DateTimeOffset modified) =>
        new(pair.Id, path, group, kind, type, recommendation,
            recommendation switch { "Upload" => "Copy to Dropbox", "Download" => "Copy to this computer", _ => "No change" },
            group == "Dropbox only" ? null : 100, group == "Local only" ? null : 100,
            group == "Dropbox only" ? null : modified, group == "Local only" ? null : modified);

    private static SyncFolderSelectionTree Health(SyncPair pair, bool local, bool dropbox) =>
        new(pair.Id, [], 0, 0, 0, 0, new SyncFolderRollup(0, 0, 0, 0, 0, 0),
            LocalHierarchyAvailable: local, LocalHierarchyMessage: local ? null : "Saved local folder no longer exists.",
            DropboxHierarchyAvailable: dropbox, DropboxHierarchyMessage: dropbox ? null : "Saved Dropbox folder is unavailable.",
            LocalRootFiles: 0, DropboxRootFiles: 0);

    private static void RequirePaths(MainWindow window, params string[] expected)
    {
        var actual = window.BootstrapRows.Select(item => item.Path).Order(StringComparer.Ordinal).ToArray();
        var wanted = expected.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
            throw new InvalidDataException($"First-sync filter mismatch. Expected [{string.Join(", ", wanted)}], actual [{string.Join(", ", actual)}].");
    }

    private static void SelectContent<T>(ComboBox combo, string content) where T : ComboBoxItem
    {
        // Endpoint-aware labels may change while the retained group key remains
        // stable. Exercise that exact group, not the old local/Dropbox wording.
        combo.SelectedItem = combo.Items.OfType<T>().Single(item => combo.Name is "BootstrapGroupFilter"
            or "BootstrapSelectedActionBox" or "BootstrapMatchingActionBox"
            ? Equals(item.Tag, content) : Equals(item.Content, content));
        // Settle the editable picker's committed Text and real selection reload
        // before these legacy checks explicitly load/clear retained rows for the
        // live-event cases. A queued reload would replace the live-only rows.
        if (combo.Name == "ActivityTypeFilter")
            combo.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void CancelDraft(MainWindow window)
    {
        if (((FrameworkElement)Required(window, "PairEditorPanel")).Visibility == Visibility.Visible)
        {
            ((Button)Required(window, "CancelPairButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(window);
        }
    }

    private static void RunDialog(MainWindow window, string handler, bool confirm, Action<AppDialogWindow> inspect,
        object? sender = null)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<AppDialogWindow>().FirstOrDefault(item => item.IsVisible);
            if (dialog is null) return;
            timer.Stop();
            try
            {
                inspect(dialog);
                ((Button)dialog.FindName(confirm ? "PrimaryButton" : "SecondaryButton"))
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            catch (Exception exception)
            {
                failure = exception;
                dialog.Close();
            }
        };
        timer.Start();
        var previousLeft = window.Left;
        var previousTop = window.Top;
        try
        {
            // This same owner is normally off-screen to isolate the batch. CenterOwner
            // needs a real visible context for the unchanged dialog containment proof.
            var work = SystemParameters.WorkArea;
            window.Left = work.Left + (work.Width - window.ActualWidth) / 2;
            window.Top = work.Top + (work.Height - window.ActualHeight) / 2;
            window.UpdateLayout();
            Pump(window);
            Invoke(window, handler, sender ?? window, new RoutedEventArgs());
        }
        finally
        {
            timer.Stop();
            window.Left = previousLeft;
            window.Top = previousTop;
        }
        if (failure is not null) throw failure;
    }

    private static void RunInformationDialog(App app, MainWindow window, Action<AppDialogWindow> inspect)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<AppDialogWindow>().FirstOrDefault(item => item.IsVisible);
            if (dialog is null) return;
            timer.Stop();
            try
            {
                if (!string.Equals(dialog.Title, "Sync Shark is still running", StringComparison.Ordinal))
                    throw new InvalidDataException("The production safe-exit information call site was not exercised.");
                inspect(dialog);
                ((Button)dialog.FindName("PrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            catch (Exception exception)
            {
                failure = exception;
                dialog.Close();
            }
        };
        timer.Start();
        var mainWindowField = typeof(App).GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic) ??
                              throw new MissingFieldException(nameof(App), "_mainWindow");
        var exitProduct = typeof(App).GetMethod("ExitProduct", BindingFlags.Instance | BindingFlags.NonPublic) ??
                          throw new MissingMethodException(nameof(App), "ExitProduct");
        mainWindowField.SetValue(app, window);
        MainWindow.ExitCheckpointWaitForTest = (_, _) => Task.FromResult(false);
        MainWindow.MonitorConfiguratorForTest = (_, _, _) =>
            Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
        MainWindow.MonitorStopperForTest = () => Task.CompletedTask;
        var previousLeft = window.Left;
        var previousTop = window.Top;
        try
        {
            var work = SystemParameters.WorkArea;
            window.Left = work.Left + (work.Width - window.ActualWidth) / 2;
            window.Top = work.Top + (work.Height - window.ActualHeight) / 2;
            window.UpdateLayout();
            Pump(window);
            exitProduct.Invoke(app, null);
            PumpUntil(window, () => !timer.IsEnabled, "The production safe-exit information dialog did not open.");
        }
        finally
        {
            timer.Stop();
            window.Left = previousLeft;
            window.Top = previousTop;
            MainWindow.ExitCheckpointWaitForTest = (_, _) => Task.FromResult(true);
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.MonitorStopperForTest = null;
        }
        if (failure is not null) throw failure;
    }

    private static void SetRenderedBootstrapAction(MainWindow window, DataGrid grid, BootstrapRow row,
        string action)
    {
        grid.ScrollIntoView(row);
        grid.UpdateLayout();
        Pump(window);
        var choice = FindDescendants<ComboBox>(grid).FirstOrDefault(item => ReferenceEquals(item.DataContext, row)) ??
                     throw new InvalidDataException("The per-item First sync action control was not rendered for " + row.Path + '.');
        choice.SelectedValue = action;
        Pump(window);
        if (!string.Equals(row.SelectedAction, action, StringComparison.Ordinal))
            throw new InvalidDataException($"The rendered per-item action did not apply {action} to {row.Path}.");
    }

    private static void SetClipboardText(string value)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Clipboard.SetText(value); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 19) { Thread.Sleep(20); }
        }
    }

    private static string GetClipboardText()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Clipboard.GetText(); }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 19) { Thread.Sleep(20); }
        }
    }

    private static void Navigate(MainWindow window, string page) => Invoke(window, "NavigateTo", page, true);

    private static object? Invoke(MainWindow window, string name, params object?[] args)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
                     throw new MissingMethodException(nameof(MainWindow), name);
        return method.Invoke(window, args);
    }

    private static FrameworkElement Required(FrameworkElement root, string name) =>
        root.FindName(name) as FrameworkElement ?? throw new InvalidDataException("Missing UI element " + name + '.');

    private static bool LabelEquals(ContentControl control, string expected)
    {
        var text = control.Content as string ??
            (control.Content as StackPanel)?.Children.OfType<TextBlock>().SingleOrDefault()?.Text;
        return string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static void Pump(MainWindow window)
    {
        var frame = new DispatcherFrame();
        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpTask(MainWindow window, Task task)
    {
        PumpUntil(window, () => task.IsCompleted, "A guided sync UI task did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(MainWindow window, Func<bool> condition, string failure)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        if (!condition()) throw new TimeoutException(failure);
    }
}
