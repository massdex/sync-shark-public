using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class FolderBrowserPaginationContract
{
    private static readonly string[] SupportedModes =
    [
        "list-success", "cursor-rejection", "ceiling", "search", "reset-race", "shared-budget",
        "select-create", "production-default", "recovery-restricted", "cancel-escape", "all"
    ];

    internal static int Run(string mode)
    {
        if (!SupportedModes.Contains(mode, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown folder-browser contract mode '{mode}'.", nameof(mode));

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        try
        {
            if (mode is "list-success" or "all") AssertListSuccessAndNaturalTerminal();
            if (mode is "cursor-rejection" or "all") AssertCursorRejectionParity();
            if (mode is "ceiling" or "all") AssertOrthogonalCeilings();
            if (mode is "search" or "all") AssertSearchContinuationRoute();
            if (mode is "reset-race" or "all")
            {
                AssertResetAndInflightInterruption();
                AssertClosedAccountCannotAffectReplacement();
            }
            if (mode is "shared-budget" or "all") AssertWindowSharedGraphBudget();
            if (mode is "select-create" or "all") AssertDeepSelectionAndCreation();
            if (mode is "production-default" or "all") AssertProductionDefaultBudget();
            if (mode is "recovery-restricted" or "all") AssertRecoveryBrowserRestriction();
            if (mode is "cancel-escape" or "all") AssertEscapeAndCancelSelection();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "folder_browser_pagination_contract",
                mode,
                result = "passed",
                productionConsumer = typeof(DropboxFolderBrowserWindow).FullName
            }));
            return 0;
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void AssertListSuccessAndNaturalTerminal()
    {
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([Entry("one")], "A"),
            ContinueListHandler = (cursor, _) =>
            {
                Require(cursor == "A", "The list continuation did not receive cursor A.");
                return Page([Entry("two")], null);
            }
        };
        var budget = new DropboxFolderGraphBudget(4, 8);
        using var browser = ShowBrowser(service, budget);
        var root = browser.Window.Roots[0];
        // WF1: opening a level continues the provider's existing pages automatically through the same paging API, so
        // the manual control is no longer a prerequisite and a repeated load of the same level may legitimately
        // continue more than once. The terminal invariants below are unchanged.
        PumpUntil(() => service.ContinueListCalls >= 1 && !root.IsLoading, "the list continuation");
        Require(root.ContinueCursor is null && root.CanLoadMore is false,
            "The automatic continuation did not reach the natural terminal page.");
        Require(root.Children.Any(child => child.Entry?.FolderId == "id:two"),
            "The accepted continuation folder was not retained in the real WPF graph.");
        Require(root.ContinueCursor is null && root.CanLoadMore is false,
            "The natural terminal page remained actionable.");
        Require(budget.RetainedPages == 2 && budget.RetainedFolders == 2,
            "Natural terminal accounting did not retain exactly two pages and two folders.");
        AssertButton(browser.Window, visible: false, enabled: false);
        var continuationsBeforeTerminalClick = service.ContinueListCalls;
        Click(browser.Window, "LoadMoreButton");
        PumpDispatcher();
        Require(service.ContinueListCalls == continuationsBeforeTerminalClick,
            "A terminal Load more click made another provider call.");
    }

    private static void AssertCursorRejectionParity()
    {
        AssertListCursorCycle(secondCursor: "A", expectedAcceptedPages: 1);
        AssertListCursorCycle(secondCursor: "B", expectedAcceptedPages: 2);
        AssertSearchCursorCycle(secondCursor: "A", expectedAcceptedPages: 1);
        AssertSearchCursorCycle(secondCursor: "B", expectedAcceptedPages: 2);
    }

    private static void AssertListCursorCycle(string secondCursor, int expectedAcceptedPages)
    {
        var continuation = 0;
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([Entry("seed")], "A"),
            ContinueListHandler = (cursor, _) =>
            {
                continuation++;
                if (secondCursor == "A") return Page([Entry("rejected")], "A");
                if (continuation == 1)
                {
                    Require(cursor == "A", "The A/B/A list sequence did not request A first.");
                    return Page([Entry("accepted")], "B");
                }
                Require(cursor == "B", "The A/B/A list sequence did not request B second.");
                return Page([Entry("rejected")], "A");
            }
        };
        var budget = new DropboxFolderGraphBudget(8, 16);
        using var browser = ShowBrowser(service, budget);
        var root = browser.Window.Roots[0];
        // WF1: the level continues the provider's pages automatically, so the same A/B/A sequence is reached
        // without the manual click that used to be a prerequisite. Every terminal invariant is asserted below.
        PumpUntil(() => !root.IsLoading && service.ContinueListCalls >= 1, "the first list continuation");
        AssertCycleTerminal(browser.Window, root, budget, expectedAcceptedPages, service.ContinueListCalls,
            () => service.ContinueListCalls);
    }

    private static void AssertSearchCursorCycle(string secondCursor, int expectedAcceptedPages)
    {
        var continuation = 0;
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([], null),
            SearchHandler = (query, _, _) => Page([Entry("search-seed")], "A"),
            ContinueSearchHandler = (cursor, _) =>
            {
                continuation++;
                if (secondCursor == "A") return Page([Entry("search-rejected")], "A");
                if (continuation == 1)
                {
                    Require(cursor == "A", "The A/B/A search sequence did not request A first.");
                    return Page([Entry("search-accepted")], "B");
                }
                Require(cursor == "B", "The A/B/A search sequence did not request B second.");
                return Page([Entry("search-rejected")], "A");
            }
        };
        var budget = new DropboxFolderGraphBudget(8, 16);
        using var browser = ShowBrowser(service, budget);
        Search(browser.Window, "needle", () => browser.Window.Roots[0].IsSearchResults);
        var root = browser.Window.Roots[0];
        PumpUntil(() => !root.IsLoading && service.ContinueSearchCalls >= 1, "the first search continuation");
        Require(service.ContinueSearchCalls == expectedAcceptedPages,
            "The automatic search did not stop at exactly the first cyclic response.");
        Require(service.ContinueListCalls == 0, "Search Load more incorrectly used the list continuation API.");
        AssertCycleTerminal(browser.Window, root, budget, expectedAcceptedPages, service.ContinueSearchCalls,
            () => service.ContinueSearchCalls);
    }

    private static void AssertCycleTerminal(DropboxFolderBrowserWindow window, DropboxFolderNode node,
        DropboxFolderGraphBudget budget, int expectedPages, int callsBeforeExtraClick, Func<int> callCount)
    {
        Require(node.Children.All(child => child.Entry?.FolderId != "id:rejected" &&
                                           child.Entry?.FolderId != "id:search-rejected"),
            "A cursor-cycle response mutated the retained WPF graph.");
        Require(node.PaginationBlockedReason?.Contains("repeated or cyclic", StringComparison.OrdinalIgnoreCase) == true,
            "A cursor cycle did not leave a cycle-specific terminal reason.");
        Require(Status(window).Contains("repeated or cyclic", StringComparison.OrdinalIgnoreCase),
            "The real WPF status did not expose the cursor-cycle rejection.");
        Require(budget.RetainedPages == expectedPages,
            "A rejected cursor page changed the shared retained-page count.");
        AssertButton(window, visible: true, enabled: false);
        Click(window, "LoadMoreButton");
        PumpDispatcher();
        Require(callCount() == callsBeforeExtraClick, "A blocked cursor sequence made another provider call.");
    }

    private static void AssertOrthogonalCeilings()
    {
        var pageCalls = 0;
        var pageService = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([Entry("one")], "A"),
            ContinueListHandler = (_, _) => ++pageCalls == 1
                ? Page([Entry("one")], "B")
                : Page([Entry("page-rejected")], "C")
        };
        var pageBudget = new DropboxFolderGraphBudget(2, 50);
        using (var browser = ShowBrowser(pageService, pageBudget))
        {
            var root = browser.Window.Roots[0];
            // WF1: the automatic continuation reaches the exact boundary and then the rejection in the same
            // sequence the manual clicks used to drive, so the terminal invariants are asserted once.
            PumpUntil(() => pageService.ContinueListCalls >= 2 && !root.IsLoading, "the page ceiling rejection");
            Require(pageBudget.RetainedPages == 2 && pageBudget.RetainedFolders == 1 && root.ContinueCursor == "B",
                "The exact retained-page boundary did not succeed with duplicate-only content.");
            Require(root.PaginationBlockedReason?.Contains("retained-page ceiling", StringComparison.Ordinal) == true &&
                    pageBudget.RetainedPages == 2 && pageBudget.RetainedFolders == 1 && root.ContinueCursor == "B" &&
                    root.Children.All(child => child.Entry?.FolderId != "id:page-rejected"),
                "The page-ceiling response was not rejected atomically.");
            AssertButton(browser.Window, visible: true, enabled: false);
            Click(browser.Window, "LoadMoreButton");
            PumpDispatcher();
            Require(pageService.ContinueListCalls == 2, "The page ceiling allowed another provider call.");
        }

        var nodeCalls = 0;
        var nodeService = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([Entry("one")], "A"),
            ContinueListHandler = (_, _) => ++nodeCalls == 1
                ? Page([Entry("two")], "B")
                : Page([Entry("node-rejected")], "C")
        };
        var nodeBudget = new DropboxFolderGraphBudget(50, 2);
        using (var browser = ShowBrowser(nodeService, nodeBudget))
        {
            var root = browser.Window.Roots[0];
            PumpUntil(() => nodeService.ContinueListCalls == 2 && !root.IsLoading, "the folder ceiling rejection");
            Require(nodeBudget.RetainedFolders == 2 && nodeBudget.RetainedPages == 2 && root.ContinueCursor == "B",
                "The exact retained-folder boundary did not succeed.");
            Require(root.PaginationBlockedReason?.Contains("retained-folder ceiling", StringComparison.Ordinal) == true &&
                    nodeBudget.RetainedFolders == 2 && nodeBudget.RetainedPages == 2 && root.ContinueCursor == "B" &&
                    root.Children.All(child => child.Entry?.FolderId != "id:node-rejected"),
                "The folder-ceiling response was not rejected atomically.");
            AssertButton(browser.Window, visible: true, enabled: false);
        }
    }

    private static void AssertSearchContinuationRoute()
    {
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([], null),
            SearchHandler = (query, _, _) =>
            {
                Require(query == "reef", "The real search box did not supply the entered query.");
                return Page([Entry("search-one")], "SEARCH-A");
            },
            ContinueSearchHandler = (cursor, _) =>
            {
                Require(cursor == "SEARCH-A", "The search continuation did not receive its opaque cursor.");
                return Page([Entry("search-two")], null);
            }
        };
        using var browser = ShowBrowser(service, new DropboxFolderGraphBudget(4, 8));
        Search(browser.Window, "reef", () => browser.Window.Roots[0].IsSearchResults);
        Click(browser.Window, "LoadMoreButton");
        PumpUntil(() => service.ContinueSearchCalls == 1 && !browser.Window.Roots[0].IsLoading,
            "the terminal search page");
        Require(service.ContinueListCalls == 0 &&
                browser.Window.Roots[0].Children.Any(child => child.Entry?.FolderId == "id:search-two"),
            "The real search Load more route did not retain the search continuation result.");
        AssertButton(browser.Window, visible: false, enabled: false);
    }

    private static void AssertWindowSharedGraphBudget()
    {
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (folderId, _) => folderId switch
            {
                null => Page([Entry("a"), Entry("b")], null),
                "id:a" => Page([Entry("a1"), Entry("a2")], null),
                "id:b" => Page([Entry("b-rejected")], null),
                _ => throw new InvalidOperationException("Unexpected shared-budget folder ID: " + folderId)
            }
        };
        var budget = new DropboxFolderGraphBudget(20, 4);
        using var browser = ShowBrowser(service, budget);
        var root = browser.Window.Roots[0];
        var a = RequireChild(root, "id:a");
        var b = RequireChild(root, "id:b");
        Expand(browser.Window, a);
        PumpUntil(() => a.IsLoaded && !a.IsLoading, "sibling A expansion");
        Require(budget.RetainedFolders == 4 && budget.RetainedPages == 2,
            "Sibling A did not bring the shared graph to its exact folder boundary.");
        Expand(browser.Window, b);
        PumpUntil(() => b.IsLoaded && !b.IsLoading, "sibling B overflow rejection");
        Require(b.PaginationBlockedReason?.Contains("retained-folder ceiling", StringComparison.Ordinal) == true &&
                b.Children.All(child => child.Entry is null) && budget.RetainedFolders == 4 &&
                budget.RetainedPages == 2,
            "Cross-node shared-budget overflow was not atomic and terminal.");
        var graph = Traverse(root).ToArray();
        Require(graph.Count(node => node.Entry is not null) == 4 && graph.Count(node => node.Entry is null) == 4,
            "The retained graph, including loading placeholders, differed from the bounded expected shape.");
    }

    private static void AssertResetAndInflightInterruption()
    {
        AssertRefreshSupersedesContinuationAndResetsSequence();
        AssertSearchSupersedesContinuationAndResetsSequence();
    }

    private static void AssertRefreshSupersedesContinuationAndResetsSequence()
    {
        var delayed = new TaskCompletionSource<DropboxFolderPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = false;
        var continuation = 0;
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (folderId, _) => folderId switch
            {
                null when refreshed => Page([Entry("parent")], "A"),
                null => Page([Entry("initial")], "A"),
                "id:parent" => Page([Entry("inside")], "A"),
                _ => throw new InvalidOperationException("Unexpected reset folder ID: " + folderId)
            },
            ContinueListHandler = (_, _) => ++continuation switch
            {
                1 => delayed.Task,
                2 => Page([Entry("fresh-child")], null),
                3 => Page([Entry("inside-more")], null),
                _ => throw new InvalidOperationException("Unexpected reset continuation call.")
            }
        };
        var budget = new DropboxFolderGraphBudget(20, 20);
        using var browser = ShowBrowser(service, budget, waitForComplete: false);
        var root = browser.Window.Roots[0];
        PumpUntil(() => service.ContinueListCalls == 1 && root.IsLoading, "the delayed list continuation");
        refreshed = true;
        Require(((Button)browser.Window.FindName("RefreshButton")).IsEnabled,
            "Refresh is not available to replace a delayed automatic listing.");
        Click(browser.Window, "RefreshButton");
        PumpUntil(() => root.Children.Any(child => child.Entry?.FolderId == "id:parent") && !root.IsLoading,
            "refresh superseding the continuation");
        delayed.SetResult(new DropboxFolderPage([Entry("stale")], "STALE"));
        PumpDispatcher();
        Require(root.Children.All(child => child.Entry?.FolderId != "id:stale") &&
                root.ContinueCursor is null && budget.RetainedPages == 2 && budget.RetainedFolders == 2,
            "Refresh did not atomically reset the graph and cursor sequence.");

        Select(browser.Window, root);
        PumpUntil(() => service.ContinueListCalls == 2 && !root.IsLoading, "fresh equal-cursor continuation");
        Require(root.PaginationBlockedReason is null &&
                root.Children.Any(child => child.Entry?.FolderId == "id:fresh-child"),
            "A cursor token reused by a fresh sequence was falsely rejected.");

        var parent = RequireChild(root, "id:parent");
        Expand(browser.Window, parent);
        PumpUntil(() => parent.IsLoaded && !parent.IsLoading, "independent parent expansion");
        Select(browser.Window, parent);
        PumpUntil(() => service.ContinueListCalls == 3 && !parent.IsLoading, "independent equal-cursor continuation");
        Require(parent.PaginationBlockedReason is null &&
                parent.Children.Any(child => child.Entry?.FolderId == "id:inside-more"),
            "An equal cursor token on an independent node was falsely rejected.");
    }

    private static void AssertSearchSupersedesContinuationAndResetsSequence()
    {
        var delayed = new TaskCompletionSource<DropboxFolderPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([Entry("initial-search-race")], "A"),
            ContinueListHandler = (_, _) => delayed.Task,
            SearchHandler = (_, _, _) => Page([Entry("search-fresh")], "A"),
            ContinueSearchHandler = (_, _) => Page([Entry("search-more")], null)
        };
        var budget = new DropboxFolderGraphBudget(20, 20);
        using var browser = ShowBrowser(service, budget, waitForComplete: false);
        var root = browser.Window.Roots[0];
        PumpUntil(() => service.ContinueListCalls == 1 && root.IsLoading, "the delayed pre-search continuation");
        Search(browser.Window, "fresh", () => root.IsSearchResults && root.ContinueCursor is null);
        delayed.SetResult(new DropboxFolderPage([Entry("search-stale")], "STALE"));
        PumpDispatcher();
        Require(root.IsSearchResults && root.ContinueCursor is null &&
                root.Children.Any(child => child.Entry?.FolderId == "id:search-fresh") &&
                root.Children.All(child => child.Entry?.FolderId != "id:search-stale") &&
                budget.RetainedPages == 2 && budget.RetainedFolders == 2,
            "A stale list continuation mutated the newer search graph.");
        PumpUntil(() => service.ContinueSearchCalls == 1 && !root.IsLoading, "fresh search continuation");
        Require(service.ContinueListCalls == 1 && root.PaginationBlockedReason is null,
            "The reset search sequence did not remain isolated from the stale list operation.");
    }

    private static void AssertDeepSelectionAndCreation()
    {
        var selectService = HierarchyService(includeCreated: () => false);
        var selected = RunDialog(selectService, window =>
        {
            var root = window.Roots[0];
            var parent = RequireChild(root, "id:parent");
            if (!parent.IsLoaded) { Expand(window, parent); return false; }
            var deep = parent.Children.FirstOrDefault(child => child.Entry?.FolderId == "id:deep");
            if (deep is null)
            {
                Select(window, parent);
                Click(window, "LoadMoreButton");
                return false;
            }
            if (!deep.IsLoaded) { Expand(window, deep); return false; }
            var leaf = deep.Children.FirstOrDefault(child => child.Entry?.FolderId == "id:leaf");
            if (leaf is null) return false;
            Select(window, leaf);
            Click(window, "SelectFolderButton");
            return true;
        });
        Require(selected?.FolderId == "id:leaf", "Deep selection did not return the exact stable folder ID.");

        var created = false;
        var createService = HierarchyService(() => created);
        createService.CreateHandler = (parentId, name, _) =>
        {
            Require(parentId == "id:deep" && name == "Created beneath deep",
                "Folder creation did not target the selected deep stable folder ID.");
            created = true;
            return Task.FromResult(Entry("created"));
        };
        var createIssued = false;
        var createdSelection = RunDialog(createService, window =>
        {
            var root = window.Roots[0];
            var parent = RequireChild(root, "id:parent");
            if (!parent.IsLoaded) { Expand(window, parent); return false; }
            var deep = parent.Children.FirstOrDefault(child => child.Entry?.FolderId == "id:deep");
            if (deep is null)
            {
                Select(window, parent);
                Click(window, "LoadMoreButton");
                return false;
            }
            if (!createIssued)
            {
                createIssued = true;
                Select(window, deep);
                ((TextBox)window.FindName("NewFolderName")).Text = "Created beneath deep";
                Click(window, "CreateFolderButton");
                return false;
            }
            var createdNode = deep.Children.FirstOrDefault(child => child.Entry?.FolderId == "id:created");
            if (createdNode is null) return false;
            var deepItem = FindItem(window, deep);
            if (!deepItem.IsExpanded)
            {
                Expand(window, deep);
                return false;
            }
            Select(window, createdNode);
            Click(window, "SelectFolderButton");
            return true;
        });
        Require(createService.CreateCalls == 1 && createdSelection?.FolderId == "id:created",
            "Deep folder creation was not exactly-once, refreshed, and selectable by stable ID.");
    }

    private static void AssertClosedAccountCannotAffectReplacement()
    {
        var delayed = new TaskCompletionSource<DropboxFolderPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstService = new ScriptedFolderBrowserService { ListHandler = (_, _) => delayed.Task };
        var first = ShowBrowser(firstService, new DropboxFolderGraphBudget(10, 10), waitForComplete: false);
        PumpUntil(() => firstService.ListCalls == 1, "first account request");
        first.Dispose();
        var secondService = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([Entry("second-account")], null)
        };
        using var second = ShowBrowser(secondService, new DropboxFolderGraphBudget(10, 10));
        delayed.SetResult(new DropboxFolderPage([Entry("first-account-late")], null));
        PumpDispatcher();
        Require(firstService.ListCalls == 1 && secondService.ListCalls == 1 &&
                second.Window.Roots[0].Children.Count == 1 &&
                second.Window.Roots[0].Children[0].Entry?.FolderId == "id:second-account",
            "Initial expansion issues one request and a late closed-account response cannot alter the replacement browser.");
    }

    private static ScriptedFolderBrowserService HierarchyService(Func<bool> includeCreated)
    {
        var continuation = 0;
        return new ScriptedFolderBrowserService
        {
            ListHandler = (folderId, _) => folderId switch
            {
                null => Page([new DropboxFolderEntry("Parent", "/Parent", "id:parent")], null),
                "id:parent" => Page([new DropboxFolderEntry("First", "/Parent/First", "id:first")], "P-A"),
                "id:deep" when includeCreated() => Page([Entry("created")], null),
                "id:deep" => Page([new DropboxFolderEntry("Leaf", "/Parent/Deep/Leaf", "id:leaf")], null),
                _ => Page([], null)
            },
            ContinueListHandler = (cursor, _) =>
            {
                Require(cursor == "P-A", "Deep hierarchy Load more received the wrong cursor.");
                continuation++;
                Require(continuation == 1, "Deep hierarchy continuation was repeated.");
                return Page([new DropboxFolderEntry("Deep", "/Parent/Deep", "id:deep")], null);
            }
        };
    }

    private static void AssertProductionDefaultBudget()
    {
        var service = new ScriptedFolderBrowserService { ListHandler = (_, _) => Page([], null) };
        using var browser = ShowBrowser(service, budget: null);
        Require(browser.Window.GraphBudget.MaxRetainedPages == DropboxFolderGraphBudget.DefaultMaxRetainedPages &&
                browser.Window.GraphBudget.MaxRetainedFolders == DropboxFolderGraphBudget.DefaultMaxRetainedFolders &&
                browser.Window.GraphBudget.MaxRetainedPages > 0 && browser.Window.GraphBudget.MaxRetainedFolders > 0 &&
                ReferenceEquals(browser.Window.GraphBudget, browser.Window.Roots[0].GraphBudget),
            "The production two-argument constructor did not wire one finite shared graph budget.");
    }

    private static void AssertRecoveryBrowserRestriction()
    {
        var service = new ScriptedFolderBrowserService
        {
            ListHandler = (_, _) => Page([
                new DropboxFolderEntry("Elsewhere", "/Elsewhere", "id:elsewhere"),
                new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath,
                    "id:recovery")
            ], null),
            CreateHandler = (_, _, _) => Task.FromResult(
                new DropboxFolderEntry("Unexpected", "/Unexpected", "id:unexpected"))
        };
        var window = MainWindow.CreateRecoveryFolderBrowser(service,
            "Dropbox · fixture@example.invalid");
        ConfigureOffscreen(window);
        window.Show();
        try
        {
            PumpUntil(() => window.Roots[0].IsLoaded && !window.Roots[0].IsLoading,
                "restricted Recovery browser initial load");
            Require(!window.AllowsFolderCreationForTest &&
                    ((FrameworkElement)window.FindName("CreateFolderPanel")).Visibility == Visibility.Collapsed &&
                    ((MenuItem)window.FindName("CreateFolderContextMenuItem")).Visibility == Visibility.Collapsed,
                "The Recovery browser did not remove every folder-creation affordance.");
            ((TextBox)window.FindName("NewFolderName")).Text = "Must not be created";
            Click(window, "CreateFolderButton");
            Require(service.CreateCalls == 0,
                "A programmatic click bypassed the Recovery browser's pre-provider creation guard.");

            var outside = RequireChild(window.Roots[0], "id:elsewhere");
            Select(window, outside);
            Require(!((Button)window.FindName("SelectFolderButton")).IsEnabled &&
                    !window.CanSelectFolderForTest(outside.Entry!),
                "The Recovery browser enabled a noncanonical Dropbox selection.");
            Click(window, "SelectFolderButton");
            Require(window.SelectedFolder is null && service.CreateCalls == 0,
                "A noncanonical Recovery selection changed result or provider state.");

            var recovery = RequireChild(window.Roots[0], "id:recovery");
            Select(window, recovery);
            Require(((Button)window.FindName("SelectFolderButton")).IsEnabled &&
                    window.CanSelectFolderForTest(recovery.Entry!),
                "The exact canonical Recovery folder was not selectable.");
        }
        finally
        {
            window.Close();
        }
    }

    private static DropboxFolderEntry? RunDialog(ScriptedFolderBrowserService service,
        Func<DropboxFolderBrowserWindow, bool> advance)
    {
        var window = CreateBrowser(service, new DropboxFolderGraphBudget(40, 100));
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (advance(window)) timer.Stop();
            }
            catch (Exception exception)
            {
                failure = exception;
                timer.Stop();
                window.Close();
            }
        };
        timer.Start();
        var result = window.ShowDialog();
        timer.Stop();
        if (failure is not null) throw new InvalidDataException("The modal WPF folder-browser scenario failed.", failure);
        Require(result == true, "The real folder browser dialog did not complete selection.");
        return window.SelectedFolder;
    }

    // UI398-2: a real Escape key cancels the modal through the WPF dialog-cancel route the Cancel button now
    // opts into with IsCancel, and it can never produce a selection. The key is delivered to the window's own
    // input handle as a Windows key message, which is the route a physical Escape key takes into WPF - a
    // synthetic WPF key event never reaches that route at all. Every case opens the real production window
    // with ShowDialog, so DialogResult is settable and no Show-only path is changed.
    private static void AssertEscapeAndCancelSelection()
    {
        var dropbox = ModalBrowser(SyncProviders.Dropbox, "Dropbox · fixture@example.invalid");
        var dropboxEscape = RunModalKey(dropbox, window => PostKey(window, VirtualKeyEscape));
        Require(dropboxEscape.Posted && !dropboxEscape.TimedOut && dropboxEscape.DialogResult == false &&
                dropbox.SelectedFolder is null &&
                !((Button)dropbox.FindName("SelectFolderButton")).IsEnabled,
            "A real Escape cancelled the Dropbox folder browser through the dialog-cancel route with no " +
            "selection and no enabled selection control (result=" + dropboxEscape.DialogResult +
            ", selection=" + (dropbox.SelectedFolder?.Path ?? "none") + ").");

        var google = ModalBrowser(SyncProviders.GoogleDrive, "Google Drive · fixture@example.invalid");
        var googleEscape = RunModalKey(google, window => PostKey(window, VirtualKeyEscape));
        Require(googleEscape.Posted && !googleEscape.TimedOut && googleEscape.DialogResult == false &&
                google.SelectedFolder is null,
            "A real Escape cancelled the provider-parameterised " + google.BrowserTitle +
            " with no selection (result=" + googleEscape.DialogResult + ", selection=" +
            (google.SelectedFolder?.Path ?? "none") + ").");

        var recovery = MainWindow.CreateRecoveryFolderBrowser(MinimalBrowserService(),
            "Dropbox · fixture@example.invalid");
        var recoveryEscape = RunModalKey(recovery, window => PostKey(window, VirtualKeyEscape));
        Require(recoveryEscape.Posted && !recoveryEscape.TimedOut && recoveryEscape.DialogResult == false &&
                recovery.SelectedFolder is null && !recovery.AllowsFolderCreationForTest &&
                ((FrameworkElement)recovery.FindName("CreateFolderPanel")).Visibility == Visibility.Collapsed,
            "A real Escape cancelled the Recovery folder browser with no selection and no folder-creation " +
            "affordance (result=" + recoveryEscape.DialogResult + ", selection=" +
            (recovery.SelectedFolder?.Path ?? "none") + ").");

        // The explicit Cancel control keeps producing the identical result, so Escape is routed into the one
        // existing cancellation rather than a parallel one.
        var explicitCancel = ModalBrowser(SyncProviders.Dropbox, "Dropbox · fixture@example.invalid");
        var cancel = RunModalKey(explicitCancel, window =>
        {
            var control = FindVisualChildren<Button>(window).SingleOrDefault(button =>
                button.Content is string caption && caption == "Cancel");
            Require(control is not null, "The folder browser lost its explicit Cancel control.");
            control!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, control));
            return true;
        });
        Require(cancel.DialogResult == false && explicitCancel.SelectedFolder is null,
            "The explicit Cancel control still cancels the modal with the same result as Escape (result=" +
            cancel.DialogResult + ").");
    }

    private const int VirtualKeyEscape = 0x1B;

    // The real modal run: the window is shown as a dialog, the key advance runs once the dialog is pumping,
    // and a dialog that answered nothing to the real key is closed so the check reports instead of hanging.
    private static (bool Posted, bool TimedOut, bool? DialogResult) RunModalKey(DropboxFolderBrowserWindow window,
        Func<DropboxFolderBrowserWindow, bool> advance)
    {
        ConfigureOffscreen(window);
        var posted = false;
        var timedOut = false;
        var ticks = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            // A real key message needs the dialog's own input route to be live, and the fixture window is
            // offscreen and never activated by the user, so the advance is offered on each tick until the
            // dialog answers it. A dialog that never answers is closed, so the check reports instead of
            // hanging, and that closure fails the assertion rather than passing as a cancellation.
            if (ticks <= 10)
            {
                posted |= advance(window);
                return;
            }
            timedOut = true;
            timer.Stop();
            window.Close();
        };
        timer.Start();
        try
        {
            var dialogResult = window.ShowDialog();
            timer.Stop();
            return (posted, timedOut, dialogResult);
        }
        finally
        {
            timer.Stop();
        }
    }

    // WM_KEYDOWN and WM_KEYUP (0x0100 and 0x0101) to the window's real input handle: the route a physical
    // Escape key takes into WPF, and the route the dialog-cancel behaviour consumes.
    private static bool PostKey(Window window, int virtualKey)
    {
        window.Activate();
        if (System.Windows.Input.Keyboard.FocusedElement is null &&
            window.FindName("FolderSearchBox") is UIElement search)
            System.Windows.Input.Keyboard.Focus(search);
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        Console.WriteLine("UI398-2 key delivery: handle=" + handle + " active=" + window.IsActive +
            " focused=" + (System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "none") +
            " key=" + virtualKey + ".");
        PostMessage(handle, 0x0100, new IntPtr(virtualKey), IntPtr.Zero);
        PostMessage(handle, 0x0101, new IntPtr(virtualKey), IntPtr.Zero);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);

    private static DropboxFolderBrowserWindow ModalBrowser(string provider, string connectionLabel) =>
        new(MinimalBrowserService(), connectionLabel, DropboxFolderGraphBudget.CreateDefault(), provider: provider);

    private static ScriptedFolderBrowserService MinimalBrowserService() =>
        new() { ListHandler = (_, _) => Page([], null) };

    private static BrowserScope ShowBrowser(ScriptedFolderBrowserService service, DropboxFolderGraphBudget? budget,
        bool waitForComplete = true)
    {
        var window = budget is null
            ? new DropboxFolderBrowserWindow(service, "Dropbox · fixture@example.invalid")
            : new DropboxFolderBrowserWindow(service, "Dropbox · fixture@example.invalid", budget);
        ConfigureOffscreen(window);
        window.Show();
        if (waitForComplete)
            PumpUntil(() => window.Roots[0].IsLoaded && !window.Roots[0].IsLoading, "initial WPF folder load");
        return new BrowserScope(window);
    }

    private static DropboxFolderBrowserWindow CreateBrowser(ScriptedFolderBrowserService service,
        DropboxFolderGraphBudget budget)
    {
        var window = new DropboxFolderBrowserWindow(service, "Dropbox · fixture@example.invalid", budget);
        ConfigureOffscreen(window);
        return window;
    }

    private static void ConfigureOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32_000;
        window.Top = -32_000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
    }

    private static void Search(DropboxFolderBrowserWindow window, string query, Func<bool> completed)
    {
        ((TextBox)window.FindName("FolderSearchBox")).Text = query;
        PumpUntil(() => completed() && !window.Roots[0].IsLoading, $"search for {query}", 5_000);
    }

    private static void Expand(DropboxFolderBrowserWindow window, DropboxFolderNode node)
    {
        var item = FindItem(window, node);
        item.IsExpanded = true;
        PumpDispatcher();
    }

    private static void Select(DropboxFolderBrowserWindow window, DropboxFolderNode node)
    {
        var item = FindItem(window, node);
        item.IsSelected = true;
        item.BringIntoView();
        PumpDispatcher();
    }

    private static TreeViewItem FindItem(DropboxFolderBrowserWindow window, DropboxFolderNode node)
    {
        var tree = (TreeView)window.FindName("FolderTree");
        tree.UpdateLayout();
        PumpDispatcher();
        return FindVisualChildren<TreeViewItem>(tree)
                   .FirstOrDefault(item => ReferenceEquals(item.DataContext, node))
               ?? throw new InvalidDataException("The target node has no generated real TreeViewItem container.");
    }

    private static void Click(Window window, string name)
    {
        var button = window.FindName(name) as Button
                     ?? throw new InvalidDataException($"The real WPF button '{name}' is absent.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        PumpDispatcher();
    }

    private static void AssertButton(Window window, bool visible, bool enabled)
    {
        var button = (Button)window.FindName("LoadMoreButton");
        Require((button.Visibility == Visibility.Visible) == visible && button.IsEnabled == enabled,
            $"Load more state was {button.Visibility}/{button.IsEnabled}, expected visible={visible}, enabled={enabled}.");
    }

    private static string Status(Window window) => ((TextBlock)window.FindName("BrowserStatus")).Text;

    private static DropboxFolderNode RequireChild(DropboxFolderNode parent, string id) =>
        parent.Children.FirstOrDefault(child => child.Entry?.FolderId == id)
        ?? throw new InvalidDataException($"Expected retained folder {id} is absent.");

    private static IEnumerable<DropboxFolderNode> Traverse(DropboxFolderNode root)
    {
        var pending = new Stack<DropboxFolderNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            foreach (var child in current.Children) pending.Push(child);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void PumpUntil(Func<bool> condition, string description, int timeoutMilliseconds = 3_000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.ElapsedMilliseconds > timeoutMilliseconds)
                throw new TimeoutException($"Timed out waiting for {description}.");
            PumpDispatcher();
            Thread.Sleep(5);
        }
        PumpDispatcher();
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Task<DropboxFolderPage> Page(IReadOnlyList<DropboxFolderEntry> folders, string? cursor) =>
        Task.FromResult(new DropboxFolderPage(folders, cursor));

    private static DropboxFolderEntry Entry(string name) =>
        new(name, "/" + name.Replace('-', '/'), "id:" + name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class BrowserScope(DropboxFolderBrowserWindow window) : IDisposable
    {
        internal DropboxFolderBrowserWindow Window { get; } = window;
        public void Dispose()
        {
            if (Window.IsLoaded) Window.Close();
            PumpDispatcher();
        }
    }
}

internal sealed class ScriptedFolderBrowserService : IDropboxFolderBrowserService
{
    internal Func<string?, CancellationToken, Task<DropboxFolderPage>> ListHandler { get; set; } =
        (_, _) => throw new InvalidOperationException("Unexpected folder list call.");
    internal Func<string, CancellationToken, Task<DropboxFolderPage>> ContinueListHandler { get; set; } =
        (_, _) => throw new InvalidOperationException("Unexpected folder list continuation call.");
    internal Func<string, string?, CancellationToken, Task<DropboxFolderPage>> SearchHandler { get; set; } =
        (_, _, _) => throw new InvalidOperationException("Unexpected folder search call.");
    internal Func<string, CancellationToken, Task<DropboxFolderPage>> ContinueSearchHandler { get; set; } =
        (_, _) => throw new InvalidOperationException("Unexpected folder search continuation call.");
    internal Func<string?, string, CancellationToken, Task<DropboxFolderEntry>> CreateHandler { get; set; } =
        (_, _, _) => throw new InvalidOperationException("Unexpected folder creation call.");

    internal int ListCalls { get; private set; }
    internal int ContinueListCalls { get; private set; }
    internal int SearchCalls { get; private set; }
    internal int ContinueSearchCalls { get; private set; }
    internal int CreateCalls { get; private set; }

    public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        ListCalls++;
        return ListHandler(folderId, cancellationToken);
    }

    public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        ContinueListCalls++;
        return ContinueListHandler(cursor, cancellationToken);
    }

    public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        SearchCalls++;
        return SearchHandler(query, folderId, cancellationToken);
    }

    public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
        CancellationToken cancellationToken = default)
    {
        ContinueSearchCalls++;
        return ContinueSearchHandler(cursor, cancellationToken);
    }

    public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await ListFoldersAsync(folderId, cancellationToken);
        foreach (var folder in page.Folders) yield return folder;
    }

    public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
        CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        return CreateHandler(parentFolderId, name, cancellationToken);
    }
}
