using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Massdex.SyncShark;

// Controlled-HTTP checks for the Google Drive folder browser shared-drive lane. The scripted handler
// replaces external I/O only: every decision under test is made by the real
// GoogleDriveFolderBrowserClient, so the exact request corpus, the exact continuation binding, the
// ancestry rules and every refusal are the production ones. No live provider, credential, account
// setting or install is touched.
internal static class GoogleDriveSharedDriveBrowserChecks
{
    private const string TestClientId = "shared-drive-browser-test-client";
    private static readonly string[] MyDriveOnly = ["MyFolder"];

    internal static async Task RunAsync(string root) => await RunAsync();

    internal static async Task RunAsync()
    {
        var previousClientId = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = TestClientId;
        try
        {
            await EnabledRootChoicesAsync();
            await RootPagingAsync();
            await DisabledKeepsMyDriveOnlyAsync();
            await CorpusBindingAsync();
            await SharedRootMetadataAsync();
            await AncestryRefusalsAsync();
            await SharedDriveCreateAsync();
            await AccountAndCorpusRefusalsAsync();
            await CursorRefusalsAsync();
            await MalformedAndCancellationAsync();
        }
        finally
        {
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = previousClientId;
        }
        Console.WriteLine(
            "PASS Google shared-drive browser: verified roots, exact corpus binding, ancestry, create and refusals");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Shared-drive browser check failed: " + message);
    }

    private static async Task RejectAsync(string label, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Shared-drive browser check '" + label + "' expected a refusal but completed instead.");
    }

    private static async Task ExpectCancelledAsync(string label, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException(
            "Shared-drive browser check '" + label + "' expected cancellation but completed instead.");
    }

    private static GoogleDriveConnectionBinding Binding(bool sharedDrives, string permissionId = "perm-account-one") =>
        new GoogleDriveConnectionBinding(TestClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId, "test@example.invalid",
            "Test", [GoogleDriveProviderConfiguration.DriveScope], SharedDrivesEnabled: sharedDrives).Normalize();

    private static GoogleDriveFolderBrowserClient Browser(SharedDriveFixture fixture, HttpClient http,
        bool constructorCapability = true, bool bindingSharedDrives = true) =>
        new(http, new Tokens(), Binding(bindingSharedDrives), constructorCapability);

    private static SharedDriveFixture EnabledFixture()
    {
        var fixture = new SharedDriveFixture();
        fixture.Drives.Add(("shared-drive-1", "Team Drive"));
        fixture.Drives.Add(("shared-drive-2", "Second Drive"));
        fixture.Nodes["my-folder"] = new SharedDriveFixture.Node
        {
            Id = "my-folder", Name = "MyFolder", Parents = ["my-drive-root"]
        };
        fixture.Nodes["shared-drive-1"] = new SharedDriveFixture.Node
        {
            Id = "shared-drive-1", Name = "Team Drive", DriveId = "shared-drive-1"
        };
        fixture.Nodes["shared-drive-2"] = new SharedDriveFixture.Node
        {
            Id = "shared-drive-2", Name = "Second Drive", DriveId = "shared-drive-2"
        };
        fixture.Nodes["sd1-design"] = new SharedDriveFixture.Node
        {
            Id = "sd1-design", Name = "Design", Parents = ["shared-drive-1"], DriveId = "shared-drive-1"
        };
        fixture.Nodes["sd1-leaf"] = new SharedDriveFixture.Node
        {
            Id = "sd1-leaf", Name = "Leaf", Parents = ["sd1-design"], DriveId = "shared-drive-1"
        };
        return fixture;
    }

    private static SharedDriveFixture.Node DriveNode(string id, string name, string[] parents, string? driveId,
        string? mimeType = null, bool trashed = false) => new()
    {
        Id = id, Name = name, Parents = [.. parents], DriveId = driveId,
        MimeType = mimeType ?? GoogleDrivePilotClient.FolderMimeType, Trashed = trashed
    };

    // SD-1: an enabled null listing exposes the verified My Drive root exactly once plus the account's
    // shared-drive roots; disabled mode is covered separately below.
    private static async Task EnabledRootChoicesAsync()
    {
        var fixture = EnabledFixture();
        using var http = new HttpClient(fixture);
        var page = await Browser(fixture, http).ListFoldersAsync();
        Require(page.Folders.Count == 3, "an enabled null listing returns My Drive plus both shared drives");
        Require(page.Folders[0].Name == "My Drive" && page.Folders[0].Path == "/" &&
                page.Folders[0].FolderId == "my-drive-root",
            "My Drive is offered with its verified root id and the root path");
        Require(page.Folders[1].Name == "Team Drive" && page.Folders[1].FolderId == "shared-drive-1" &&
                page.Folders[1].Path == "/Shared drives/Team Drive",
            "the first shared drive is offered under its stable drive id and its shared display path");
        Require(page.Folders[2].Name == "Second Drive" && page.Folders[2].FolderId == "shared-drive-2",
            "the second shared drive is offered with its own stable id");
        Require(page.ContinueCursor is null, "a root page that fits the drive page size has no continuation");
        var drives = fixture.Calls.Single(call => call.Path.EndsWith("/drives", StringComparison.Ordinal));
        Require(drives.Method == HttpMethod.Get && drives.Param("pageSize") == "100",
            "shared-drive roots are read through one read-only drives.list page of 100");
        Require(drives.Param("fields") == "nextPageToken,drives(id,name)",
            "drives.list requests only the drive id/name fields and the next page token");
        Require(fixture.Calls.All(call => !call.Path.EndsWith("/files", StringComparison.Ordinal)),
            "the enabled root page issues no files.list request at all");
        Require(fixture.Calls.Any(call => call.Path.EndsWith("/about", StringComparison.Ordinal)),
            "the first enabled request verifies the signed-in account");
    }

    // SD-1: root paging is lazy, the first page carries My Drive once and continuation stays on drives.list.
    private static async Task RootPagingAsync()
    {
        var fixture = EnabledFixture();
        fixture.Drives.Add(("shared-drive-3", "Third Drive"));
        fixture.DrivesPerPage = 1;
        using var http = new HttpClient(fixture);
        var browser = Browser(fixture, http);
        var first = await browser.ListFoldersAsync();
        Require(first.Folders.Count == 2 && first.Folders[0].Name == "My Drive" && first.Folders[1].Name == "Team Drive",
            "the first root page carries My Drive and one shared drive");
        Require(first.ContinueCursor is not null, "a root page with more drives returns a continuation");
        var pages = new List<string> { "My Drive", "Team Drive" };
        var page = first;
        while (page.ContinueCursor is not null)
        {
            page = await browser.ContinueFoldersAsync(page.ContinueCursor);
            pages.AddRange(page.Folders.Select(folder => folder.Name));
        }
        Require(pages.SequenceEqual(new[] { "My Drive", "Team Drive", "Second Drive", "Third Drive" }),
            "root paging reaches every shared drive and repeats My Drive exactly once");
        var continuations = fixture.Calls.Where(call => call.Path.EndsWith("/drives", StringComparison.Ordinal)).ToArray();
        Require(continuations.Length == 3 && continuations.Skip(1).All(call => call.Param("pageToken") is not null),
            "every root continuation stays on drives.list with the previous page token");
    }

    // SD-1: disabled mode keeps the original My Drive-only behaviour for either missing flag.
    private static async Task DisabledKeepsMyDriveOnlyAsync()
    {
        foreach (var (constructorCapability, bindingSharedDrives) in new[] { (false, true), (true, false) })
        {
            var fixture = EnabledFixture();
            using var http = new HttpClient(fixture);
            var page = await Browser(fixture, http, constructorCapability, bindingSharedDrives).ListFoldersAsync();
            Require(page.Folders.Select(folder => folder.Name).SequenceEqual(MyDriveOnly),
                "disabled mode lists the My Drive children only");
            Require(page.Folders[0].Path == "/MyFolder", "the disabled listing still resolves the My Drive path");
            Require(page.Folders[0].FolderId == "my-folder", "the disabled listing keeps the stable folder id");
            var listings = fixture.Calls.Where(call => call.Path.EndsWith("/files", StringComparison.Ordinal)).ToArray();
            Require(listings.Length > 0 && listings.All(call => call.Param("q")!.Contains("'root' in parents", StringComparison.Ordinal)),
                "disabled mode keeps the original root-sentinel My Drive query");
            Require(listings.All(call => call.Param("supportsAllDrives") == "false" &&
                                         call.Param("includeItemsFromAllDrives") == "false"),
                "disabled mode keeps supportsAllDrives and includeItemsFromAllDrives false");
            Require(fixture.Calls.All(call => call.Param("corpora") is null && call.Param("driveId") is null),
                "disabled mode issues no corpus or driveId parameter");
            Require(fixture.Calls.All(call => !call.Path.EndsWith("/drives", StringComparison.Ordinal)),
                "disabled mode never discovers shared drives");

            var accountRead = fixture.Calls.ToList().FindIndex(call =>
                call.Path.EndsWith("/about", StringComparison.Ordinal));
            var rootRead = fixture.Calls.ToList().FindIndex(call =>
                call.Path.EndsWith("/files/root", StringComparison.Ordinal));
            Require(accountRead >= 0 && rootRead > accountRead,
                "disabled mode verifies the signed-in account before it reads the My Drive root");

            // A shared folder selected while shared drives are disabled has no effect: the exact selected
            // id is revalidated against the provider and refused, not accepted as a My Drive parent.
            var blocked = EnabledFixture();
            blocked.Nodes["sd1-foreign"] = DriveNode("sd1-foreign", "InDrive", ["shared-drive-1"], "shared-drive-1");
            using var blockedHttp = new HttpClient(blocked);
            await RejectAsync("a shared folder selected while shared drives are disabled",
                () => Browser(blocked, blockedHttp, constructorCapability, bindingSharedDrives)
                    .ListFoldersAsync("sd1-foreign"));
            Require(blocked.Calls.All(call => call.Param("corpora") is null),
                "a shared id with shared drives disabled issues no corpus request");
            Require(blocked.Calls.Single(call => call.Path.EndsWith("/files/sd1-foreign", StringComparison.Ordinal))
                    .Param("supportsAllDrives") == "false",
                "the disabled revalidation asks for the selected item without shared-drive support");
            Require(blocked.Calls.All(call => !call.Path.EndsWith("/files", StringComparison.Ordinal)),
                "a refused shared selection issues no child listing at all");
        }
    }

    // SD-2: every browse, search and continuation binds one exact account and one exact corpus.
    private static async Task CorpusBindingAsync()
    {
        var fixture = EnabledFixture();
        fixture.ListPageSize = 1;
        using var http = new HttpClient(fixture);
        var browser = Browser(fixture, http);
        await browser.ListFoldersAsync();

        var myDrive = await browser.ListFoldersAsync("my-drive-root");
        var myDriveList = fixture.Calls.Last(call => call.Path.EndsWith("/files", StringComparison.Ordinal));
        Require(myDrive.Folders.Single().Path == "/MyFolder", "a My Drive folder keeps its My Drive path");
        Require(myDriveList.Param("corpora") == "user", "a My Drive listing uses corpora=user");
        Require(myDriveList.Param("includeItemsFromAllDrives") == "false",
            "a My Drive listing excludes items from other drives");
        Require(myDriveList.Param("supportsAllDrives") == "true",
            "an enabled My Drive listing declares shared-drive support");
        Require(myDriveList.Param("q")!.Contains("'my-drive-root' in parents", StringComparison.Ordinal),
            "a My Drive listing binds the verified My Drive root id");
        Require(myDriveList.Param("fields")!.Contains("trashed", StringComparison.Ordinal),
            "the folder listing requests the trashed metadata field");

        var shared = await browser.ListFoldersAsync("shared-drive-1");
        var sharedVerify = fixture.Calls.Single(call => call.Path.EndsWith("/files/shared-drive-1", StringComparison.Ordinal));
        var sharedList = fixture.Calls.Last(call => call.Path.EndsWith("/files", StringComparison.Ordinal));
        Require(sharedVerify.Method == HttpMethod.Get && sharedVerify.Param("supportsAllDrives") == "true",
            "the selected shared root is revalidated against the provider first");
        Require(sharedList.Param("corpora") == "drive" && sharedList.Param("driveId") == "shared-drive-1",
            "a shared-drive listing binds corpora=drive with the exact drive id");
        Require(sharedList.Param("supportsAllDrives") == "true" &&
                sharedList.Param("includeItemsFromAllDrives") == "true",
            "a shared-drive listing declares all-drive item inclusion");
        Require(sharedList.Param("q")!.Contains("'shared-drive-1' in parents", StringComparison.Ordinal),
            "a shared-drive listing binds the shared root id");
        Require(shared.Folders.Single().Path == "/Shared drives/Team Drive/Design",
            "a shared folder keeps its shared-drive display path");

        var descendant = await browser.ListFoldersAsync("sd1-design");
        var descendantList = fixture.Calls.Last(call => call.Path.EndsWith("/files", StringComparison.Ordinal));
        Require(descendantList.Param("corpora") == "drive" && descendantList.Param("driveId") == "shared-drive-1",
            "a selected shared descendant keeps its own drive corpus");
        Require(descendantList.Param("q")!.Contains("'sd1-design' in parents", StringComparison.Ordinal),
            "a descendant listing binds the exact selected folder id");
        Require(descendant.Folders.Single().Path == "/Shared drives/Team Drive/Design/Leaf",
            "the descendant page returns the Leaf child present in this fixture");

        fixture.Nodes["sd1-second"] = DriveNode("sd1-second", "Second", ["sd1-design"], "shared-drive-1");
        var firstPage = await browser.ListFoldersAsync("sd1-design");
        Require(firstPage.ContinueCursor is not null, "a page that exceeds the page size returns a continuation");
        var firstList = fixture.Calls.Last(call => call.Path.EndsWith("/files", StringComparison.Ordinal));
        var before = fixture.Calls.Count;
        var secondPage = await browser.ContinueFoldersAsync(firstPage.ContinueCursor!);
        Require(fixture.Calls.Count == before + 1, "a folder continuation issues exactly one request");
        var continueList = fixture.Calls[^1];
        Require(continueList.Param("corpora") == "drive" && continueList.Param("driveId") == "shared-drive-1",
            "a continuation keeps the exact corpus");
        Require(continueList.Param("q") == firstList.Param("q"), "a continuation keeps the exact query");
        Require(continueList.Param("pageToken") is not null, "a continuation sends the provider page token");
        Require(secondPage.Folders.Single().Path == "/Shared drives/Team Drive/Design/Second",
            "the continued page resolves its own shared path");

        var mySearch = await browser.SearchFoldersAsync("MyFolder");
        var mySearchCall = fixture.Calls.Last(call => call.Path.EndsWith("/files", StringComparison.Ordinal));
        Require(mySearch.Folders.Single().Path == "/MyFolder", "an unselected search finds the My Drive folder");
        Require(mySearchCall.Param("corpora") == "user" && mySearchCall.Param("driveId") is null,
            "an unselected enabled search stays in the user corpus and names no drive");
        Require(!string.IsNullOrWhiteSpace(GoogleDriveFolderBrowserClient.SharedDriveSearchLimitation),
            "the unselected-search limitation is documented on the browser");

        var sharedSearch = await browser.SearchFoldersAsync("Leaf", "sd1-design");
        var sharedSearchCall = fixture.Calls.Last(call => call.Path.EndsWith("/files", StringComparison.Ordinal));
        Require(sharedSearchCall.Param("corpora") == "drive" && sharedSearchCall.Param("driveId") == "shared-drive-1",
            "a search in a selected shared folder binds that folder's drive corpus");
        Require(sharedSearchCall.Param("q")!.Contains("'sd1-design' in parents", StringComparison.Ordinal),
            "a search in a selected shared folder binds the selected folder id");
        Require(sharedSearch.Folders.Single().Path == "/Shared drives/Team Drive/Design/Leaf",
            "a search inside a shared folder resolves the shared path");

        Require(fixture.Calls.All(call => call.Param("corpora") != "allDrives"),
            "no request ever uses corpora=allDrives");
        Require(fixture.Calls.Where(call => call.Path.EndsWith("/files", StringComparison.Ordinal) &&
                                            call.Param("corpora") == "drive")
                .All(call => call.Param("driveId") == "shared-drive-1"),
            "every drive-corpus request carries one exact drive id");
    }

    // SD-3: a shared root must be exactly the discovered drive id, a folder, with no parent and no trash.
    private static async Task SharedRootMetadataAsync()
    {
        await RejectSharedRootAsync("a shared root that reports a parent",
            fixture => fixture.Nodes["shared-drive-1"] =
                DriveNode("shared-drive-1", "Team Drive", ["my-drive-root"], "shared-drive-1"));
        await RejectSharedRootAsync("a trashed shared root",
            fixture => fixture.Nodes["shared-drive-1"] =
                DriveNode("shared-drive-1", "Team Drive", [], "shared-drive-1", trashed: true));
        await RejectSharedRootAsync("a shared root bound to a different drive id",
            fixture => fixture.Nodes["shared-drive-1"] =
                DriveNode("shared-drive-1", "Team Drive", [], "shared-drive-7"));
        await RejectSharedRootAsync("a shared root whose id is not the drive id",
            fixture => fixture.Nodes["shared-drive-1"] =
                DriveNode("other-root-id", "Team Drive", [], "shared-drive-1"));
        await RejectSharedRootAsync("a shared root that is not a folder",
            fixture => fixture.Nodes["shared-drive-1"] = DriveNode("shared-drive-1", "Team Drive", [],
                "shared-drive-1", "application/vnd.google-apps.document"));
    }

    private static async Task RejectSharedRootAsync(string label, Action<SharedDriveFixture> arrange)
    {
        var fixture = EnabledFixture();
        arrange(fixture);
        using var http = new HttpClient(fixture);
        var browser = Browser(fixture, http);
        await browser.ListFoldersAsync();
        await RejectAsync(label, () => browser.ListFoldersAsync("shared-drive-1"));
    }

    // SD-3: descendant ancestry must be unambiguous, single-parented, same-drive, non-cyclic and folder-only.
    private static async Task AncestryRefusalsAsync()
    {
        await RejectListingAsync("a folder with two parents",
            fixture => fixture.Nodes["sd1-multi"] =
                DriveNode("sd1-multi", "Multi", ["sd1-design", "shared-drive-1"], "shared-drive-1"),
            "sd1-design");
        await RejectListingAsync("an ancestry cycle",
            fixture =>
            {
                fixture.Nodes["sd1-loop1"] = DriveNode("sd1-loop1", "Loop One", ["sd1-loop2"], "shared-drive-1");
                fixture.Nodes["sd1-loop2"] = DriveNode("sd1-loop2", "Loop Two", ["sd1-loop1"], "shared-drive-1");
            },
            "sd1-loop1");
        await RejectListingAsync("a shortcut item in a shared listing",
            fixture => fixture.Nodes["sd1-shortcut"] = DriveNode("sd1-shortcut", "Shortcut", ["sd1-design"],
                "shared-drive-1", GoogleDrivePilotClient.ShortcutMimeType),
            "sd1-design");
        await RejectListingAsync("an ancestry that crosses two shared drives",
            fixture =>
            {
                fixture.Nodes["sd1-cross"] = DriveNode("sd1-cross", "Cross", ["shared-drive-2"], "shared-drive-1");
                fixture.Nodes["sd1-crosschild"] = DriveNode("sd1-crosschild", "Cross Child", ["sd1-cross"], "shared-drive-1");
            },
            "sd1-cross");
        await RejectListingAsync("an ancestry that starts at an unknown parentless item",
            fixture =>
            {
                fixture.Nodes["sd1-orphan"] = DriveNode("sd1-orphan", "Orphan", [], "shared-drive-1");
                fixture.Nodes["sd1-orphanchild"] = DriveNode("sd1-orphanchild", "Orphan Child", ["sd1-orphan"], "shared-drive-1");
            },
            "sd1-orphan");
        await RejectListingAsync("an ancestry that reaches My Drive from a shared drive",
            fixture =>
            {
                fixture.Nodes["sd1-crossmy"] = DriveNode("sd1-crossmy", "Cross My", ["my-folder"], "shared-drive-1");
                fixture.Nodes["sd1-crossmychild"] = DriveNode("sd1-crossmychild", "Cross My Child", ["sd1-crossmy"], "shared-drive-1");
            },
            "sd1-crossmy");
        await RejectListingAsync("a non-folder item in a shared listing",
            fixture => fixture.Nodes["sd1-plain"] = DriveNode("sd1-plain", "Plain", ["sd1-design"],
                "shared-drive-1", "application/vnd.google-apps.document"),
            "sd1-design");
        await RejectListingAsync("an ancestry that passes through a non-folder item",
            fixture =>
            {
                fixture.Nodes["sd1-doc"] = DriveNode("sd1-doc", "Doc", ["shared-drive-1"], "shared-drive-1",
                    "application/vnd.google-apps.document");
                fixture.Nodes["sd1-under-doc"] = DriveNode("sd1-under-doc", "Under Doc", ["sd1-doc"],
                    "shared-drive-1");
                fixture.Nodes["sd1-under-docchild"] = DriveNode("sd1-under-docchild", "Under Doc Child",
                    ["sd1-under-doc"], "shared-drive-1");
            },
            "sd1-under-doc");
    }

    private static async Task RejectListingAsync(string label, Action<SharedDriveFixture> arrange, string folderId)
    {
        var fixture = EnabledFixture();
        arrange(fixture);
        using var http = new HttpClient(fixture);
        var browser = Browser(fixture, http);
        await browser.ListFoldersAsync();
        await RejectAsync(label, () => browser.ListFoldersAsync(folderId));
    }

    // SD-3: a shared create declares supportsAllDrives and keeps the duplicate-sibling guard.
    private static async Task SharedDriveCreateAsync()
    {
        var fixture = EnabledFixture();
        using var http = new HttpClient(fixture);
        var browser = Browser(fixture, http);
        await browser.ListFoldersAsync();
        var created = await browser.CreateFolderAsync("sd1-design", "New");
        var post = fixture.Calls.Single(call => call.Method == HttpMethod.Post);
        var existence = fixture.Calls.Last(call => call.Method == HttpMethod.Get &&
                                                   call.Path.EndsWith("/files", StringComparison.Ordinal));
        Require(post.Path.EndsWith("/files", StringComparison.Ordinal),
            "a shared folder create posts to the files endpoint");
        Require(post.Param("supportsAllDrives") == "true", "a shared folder create declares supportsAllDrives=true");
        Require(post.Body!.Contains("\"parents\":[\"sd1-design\"]", StringComparison.Ordinal),
            "a shared folder create binds the exact shared parent");
        Require(post.Body!.Contains(GoogleDrivePilotClient.FolderMimeType, StringComparison.Ordinal),
            "the created item is a folder");
        Require(created.Path == "/Shared drives/Team Drive/Design/New",
            "the created shared folder resolves its shared display path");
        Require(existence.Param("corpora") == "drive" && existence.Param("driveId") == "shared-drive-1",
            "the shared existence guard searched the shared corpus");
        Require(existence.Param("q")!.Contains("name = 'New'", StringComparison.Ordinal),
            "the existence guard looked for the exact name");
        Require(fixture.Calls.ToList().IndexOf(existence) < fixture.Calls.ToList().IndexOf(post),
            "the existence guard ran before the create");

        var myDriveCreated = await browser.CreateFolderAsync("my-drive-root", "NewMy");
        var myDrivePost = fixture.Calls.Last(call => call.Method == HttpMethod.Post);
        var myDriveExistence = fixture.Calls.Last(call => call.Method == HttpMethod.Get &&
            call.Path.EndsWith("/files", StringComparison.Ordinal) &&
            fixture.Calls.ToList().IndexOf(call) < fixture.Calls.ToList().IndexOf(myDrivePost));
        Require(myDrivePost.Param("supportsAllDrives") == "true",
            "an enabled My Drive create declares supportsAllDrives=true");
        Require(myDriveExistence.Param("corpora") == "user", "the My Drive existence guard searched the user corpus");
        Require(myDriveCreated.Path == "/NewMy", "a My Drive create resolves its My Drive path");

        var duplicate = EnabledFixture();
        duplicate.Nodes["sd1-dup-a"] = DriveNode("sd1-dup-a", "Dup", ["sd1-design"], "shared-drive-1");
        duplicate.Nodes["sd1-dup-b"] = DriveNode("sd1-dup-b", "Dup", ["sd1-design"], "shared-drive-1");
        using var duplicateHttp = new HttpClient(duplicate);
        var duplicateBrowser = Browser(duplicate, duplicateHttp);
        await duplicateBrowser.ListFoldersAsync();
        await RejectAsync("a duplicate sibling create",
            () => duplicateBrowser.CreateFolderAsync("sd1-design", "Dup"));
        Require(duplicate.Calls.All(call => call.Method != HttpMethod.Post),
            "the duplicate-sibling guard creates nothing");

        var existing = EnabledFixture();
        existing.Nodes["sd1-existing"] = DriveNode("sd1-existing", "Existing", ["sd1-design"], "shared-drive-1");
        using var existingHttp = new HttpClient(existing);
        var existingBrowser = Browser(existing, existingHttp);
        await existingBrowser.ListFoldersAsync();
        var reused = await existingBrowser.CreateFolderAsync("sd1-design", "Existing");
        Require(reused.FolderId == "sd1-existing", "one exact existing sibling is reused instead of duplicated");
        Require(existing.Calls.All(call => call.Method != HttpMethod.Post),
            "reusing an existing sibling performs no create");
    }

    // SD-2/SD-4: a wrong account or a foreign corpus is refused before any folder read.
    private static async Task AccountAndCorpusRefusalsAsync()
    {
        var wrongAccount = EnabledFixture();
        wrongAccount.PermissionId = "perm-other-account";
        using var wrongHttp = new HttpClient(wrongAccount);
        await RejectAsync("a wrong signed-in account",
            () => Browser(wrongAccount, wrongHttp).ListFoldersAsync());
        Require(wrongAccount.Calls.Count == 1 &&
                wrongAccount.Calls[0].Path.EndsWith("/about", StringComparison.Ordinal),
            "a wrong account identity is refused after the account read and before any folder read");

        var foreignReply = EnabledFixture();
        foreignReply.Nodes["my-foreign"] = DriveNode("my-foreign", "Foreign", ["my-drive-root"], "shared-drive-9");
        foreignReply.InjectedFolderId = "my-foreign";
        using var foreignHttp = new HttpClient(foreignReply);
        var foreignBrowser = Browser(foreignReply, foreignHttp);
        await foreignBrowser.ListFoldersAsync();
        await RejectAsync("a shared item inside the My Drive corpus",
            () => foreignBrowser.ListFoldersAsync("my-drive-root"));

        var undiscovered = EnabledFixture();
        undiscovered.Nodes["shared-drive-9"] = DriveNode("shared-drive-9", "Rogue", [], "shared-drive-9");
        using var undiscoveredHttp = new HttpClient(undiscovered);
        var undiscoveredBrowser = Browser(undiscovered, undiscoveredHttp);
        await undiscoveredBrowser.ListFoldersAsync();
        await RejectAsync("an undiscovered shared drive id",
            () => undiscoveredBrowser.ListFoldersAsync("shared-drive-9"));
        Require(undiscovered.Calls.All(call => call.Param("corpora") != "drive"),
            "an undiscovered drive id is refused before any drive-corpus listing");

        // SD-2: disabled mode verifies the account too, so a wrong signed-in account is refused before any
        // folder read even when shared drives are disabled.
        var disabledWrongAccount = EnabledFixture();
        disabledWrongAccount.PermissionId = "perm-other-account";
        using var disabledWrongHttp = new HttpClient(disabledWrongAccount);
        await RejectAsync("a wrong signed-in account while shared drives are disabled",
            () => Browser(disabledWrongAccount, disabledWrongHttp, false, false).ListFoldersAsync());
        Require(disabledWrongAccount.Calls.Count == 1 &&
                disabledWrongAccount.Calls[0].Path.EndsWith("/about", StringComparison.Ordinal),
            "a disabled wrong account is refused after the account read and before any folder read");

        // SD-2: a reply that names a different folder than the selected one is refused before any listing.
        var substituted = EnabledFixture();
        substituted.Nodes["sd1-requested"] = DriveNode("sd1-other", "Other", ["shared-drive-1"], "shared-drive-1");
        using var substitutedHttp = new HttpClient(substituted);
        var substitutedBrowser = Browser(substituted, substitutedHttp);
        await substitutedBrowser.ListFoldersAsync();
        await RejectAsync("a shared reply for a different folder id than the selected one",
            () => substitutedBrowser.ListFoldersAsync("sd1-requested"));
        Require(substituted.Calls.All(call => !call.Path.EndsWith("/files", StringComparison.Ordinal)),
            "a substituted shared folder reply issues no child listing");

        var disabledSubstituted = EnabledFixture();
        disabledSubstituted.Nodes["my-requested"] = DriveNode("my-other", "Other My", ["my-drive-root"], null);
        using var disabledSubstitutedHttp = new HttpClient(disabledSubstituted);
        var disabledSubstitutedBrowser = Browser(disabledSubstituted, disabledSubstitutedHttp, false, false);
        await RejectAsync("a disabled reply for a different folder id than the selected one",
            () => disabledSubstitutedBrowser.ListFoldersAsync("my-requested"));
        Require(disabledSubstituted.Calls.All(call => !call.Path.EndsWith("/files", StringComparison.Ordinal)),
            "a substituted disabled folder reply issues no child listing");
    }

    // SD-4: foreign, stale and cross-kind cursors are refused before any network request.
    private static async Task CursorRefusalsAsync()
    {
        var fixture = EnabledFixture();
        fixture.DrivesPerPage = 1;
        fixture.ListPageSize = 1;
        fixture.Nodes["sd1-second"] = DriveNode("sd1-second", "Second", ["sd1-design"], "shared-drive-1");
        using var http = new HttpClient(fixture);
        var browser = Browser(fixture, http);
        var roots = await browser.ListFoldersAsync();
        Require(roots.ContinueCursor is not null, "a root continuation exists for the cursor checks");

        var before = fixture.Calls.Count;
        await RejectAsync("a cursor from another browser instance",
            () => Browser(fixture, http).ContinueFoldersAsync(roots.ContinueCursor!));
        Require(fixture.Calls.Count == before, "a foreign cursor is refused before any network request");

        var folderPage = await browser.ListFoldersAsync("sd1-design");
        Require(folderPage.ContinueCursor is not null, "a folder continuation exists for the cursor checks");
        var searchPage = await browser.SearchFoldersAsync("Leaf", "sd1-design");
        Require(searchPage.ContinueCursor is null, "the single search hit fits one page");
        var searchWithMore = await browser.SearchFoldersAsync("e", "sd1-design");
        Require(searchWithMore.ContinueCursor is not null, "a two-hit search returns a continuation");

        var checkpoint = fixture.Calls.Count;
        await RejectAsync("a search cursor as a folder listing",
            () => browser.ContinueFoldersAsync(searchWithMore.ContinueCursor!));
        Require(fixture.Calls.Count == checkpoint,
            "a search cursor is refused as a folder listing before any network request");
        await RejectAsync("a folder cursor as a folder search",
            () => browser.ContinueFolderSearchAsync(folderPage.ContinueCursor!));
        Require(fixture.Calls.Count == checkpoint,
            "a folder cursor is refused as a folder search before any network request");
        await RejectAsync("a root cursor as a folder search",
            () => browser.ContinueFolderSearchAsync(roots.ContinueCursor!));
        Require(fixture.Calls.Count == checkpoint,
            "a root cursor is refused as a folder search before any network request");

        var many = EnabledFixture();
        many.AlwaysNextFolderPage = true;
        using var manyHttp = new HttpClient(many);
        var manyBrowser = Browser(many, manyHttp);
        await manyBrowser.ListFoldersAsync();
        var current = await manyBrowser.ListFoldersAsync("shared-drive-1");
        var pages = 0;
        var refused = false;
        while (current.ContinueCursor is not null && pages < 1100)
        {
            pages++;
            try { current = await manyBrowser.ContinueFoldersAsync(current.ContinueCursor); }
            catch (GoogleDriveBlockedException) { refused = true; break; }
        }
        Require(refused && pages == 1000,
            "the browser refuses the page after its bounded 1000-continuation limit");
    }

    // SD-4: malformed replies and cancellation are refused with no effect.
    private static async Task MalformedAndCancellationAsync()
    {
        var malformedFiles = EnabledFixture();
        malformedFiles.MalformedFiles = true;
        using var malformedFilesHttp = new HttpClient(malformedFiles);
        await RejectAsync("a malformed files array",
            () => Browser(malformedFiles, malformedFilesHttp).ListFoldersAsync("my-drive-root"));

        var malformedDrives = EnabledFixture();
        malformedDrives.MalformedDrives = true;
        using var malformedDrivesHttp = new HttpClient(malformedDrives);
        await RejectAsync("a malformed drives array",
            () => Browser(malformedDrives, malformedDrivesHttp).ListFoldersAsync());

        var incomplete = EnabledFixture();
        incomplete.IncompleteSearch = true;
        using var incompleteHttp = new HttpClient(incomplete);
        var incompleteBrowser = Browser(incomplete, incompleteHttp);
        await incompleteBrowser.ListFoldersAsync();
        await RejectAsync("an incomplete folder search",
            () => incompleteBrowser.CreateFolderAsync("sd1-design", "New"));
        Require(incomplete.Calls.All(call => call.Method != HttpMethod.Post),
            "an incomplete search never authorises a folder create");

        var cancelled = EnabledFixture();
        using var cancelledHttp = new HttpClient(cancelled);
        using var tokenSource = new CancellationTokenSource();
        tokenSource.Cancel();
        await ExpectCancelledAsync("a cancelled browse",
            () => Browser(cancelled, cancelledHttp).ListFoldersAsync(null, tokenSource.Token));
        Require(cancelled.Calls.Count == 0, "a cancelled browse issues no Google Drive request at all");
    }

    private sealed class Tokens : IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-access");
    }
}

// Deterministic in-memory Google Drive v3 endpoint used only as external I/O for the shared-drive
// browser checks. It answers about, files/root, drives.list, files.list (with corpora/driveId paging),
// files/{id} metadata and files.create, so every decision under test stays in the production client.
internal sealed class SharedDriveFixture : HttpMessageHandler
{
    internal sealed class Node
    {
        internal required string Id { get; init; }
        internal required string Name { get; init; }
        internal string MimeType { get; init; } = GoogleDrivePilotClient.FolderMimeType;
        internal List<string> Parents { get; init; } = [];
        internal string? DriveId { get; init; }
        internal bool Trashed { get; init; }
    }

    internal sealed record Call(HttpMethod Method, string Path, Dictionary<string, string> Query, string? Body)
    {
        internal string? Param(string name) => Query.TryGetValue(name, out var value) ? value : null;
    }

    private readonly List<Call> _calls = [];
    private readonly List<string> _created = [];
    internal IReadOnlyList<Call> Calls => _calls;
    internal Dictionary<string, Node> Nodes { get; } = new(StringComparer.Ordinal);
    internal List<(string Id, string Name)> Drives { get; } = [];
    internal string PermissionId { get; set; } = "perm-account-one";
    internal string RootId { get; init; } = "my-drive-root";
    internal int ListPageSize { get; set; } = 200;
    internal int DrivesPerPage { get; set; } = 100;
    internal bool IncompleteSearch { get; set; }
    internal bool MalformedFiles { get; set; }
    internal bool MalformedDrives { get; set; }
    internal bool AlwaysNextFolderPage { get; set; }
    // Deliberately malformed provider reply: do not let the fake's normal corpus filter hide
    // the foreign item before the real browser gets a chance to reject it.
    internal string? InjectedFolderId { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var query = ParseQuery(uri.Query);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _calls.Add(new Call(request.Method, path, query, body));
        if (path.EndsWith("/about", StringComparison.Ordinal))
            return Json("{\"user\":{\"permissionId\":" + Q(PermissionId) + "}}");
        if (path.EndsWith("/drives", StringComparison.Ordinal)) return DrivesPage(query);
        if (path.EndsWith("/files/root", StringComparison.Ordinal))
            return Json("{\"id\":" + Q(RootId) + ",\"mimeType\":" + Q(GoogleDrivePilotClient.FolderMimeType) + "}");
        if (path.EndsWith("/files", StringComparison.Ordinal))
            return request.Method == HttpMethod.Post ? CreateFolder(body) : FolderPage(query);
        if (path.Contains("/files/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path[(path.IndexOf("/files/", StringComparison.Ordinal) + 7)..]);
            return Nodes.TryGetValue(id, out var node) ? Json(NodeJson(node)) : Error("notFound");
        }
        return Error("notFound");
    }

    private HttpResponseMessage DrivesPage(Dictionary<string, string> query)
    {
        if (MalformedDrives) return Json("{\"drives\":{}}");
        var offset = PageOffset(query, "drives-page-");
        var page = Drives.Skip(offset).Take(DrivesPerPage).ToArray();
        var next = offset + page.Length < Drives.Count ? offset + page.Length : -1;
        var json = "{\"drives\":[" + string.Join(',',
                page.Select(drive => "{\"id\":" + Q(drive.Id) + ",\"name\":" + Q(drive.Name) + "}")) + "]"
            + (next < 0 ? string.Empty : ",\"nextPageToken\":" + Q("drives-page-" + next.ToString(CultureInfo.InvariantCulture)))
            + "}";
        return Json(json);
    }

    private HttpResponseMessage FolderPage(Dictionary<string, string> query)
    {
        if (MalformedFiles) return Json("{\"files\":{}}");
        var filter = query.GetValueOrDefault("q", string.Empty);
        var corpora = query.GetValueOrDefault("corpora", string.Empty);
        var driveId = query.GetValueOrDefault("driveId");
        // The parent clause may be the first or the last term of the query, so it is read from the
        // quoted token immediately before the "in parents" marker rather than from the first quote.
        var parentMarker = filter.IndexOf(" in parents", StringComparison.Ordinal);
        string? parent = null;
        if (parentMarker >= 0)
        {
            var close = filter.LastIndexOf('\'', parentMarker);
            var open = close > 0 ? filter.LastIndexOf('\'', close - 1) : -1;
            if (open >= 0 && close > open) parent = filter[(open + 1)..close];
        }
        var exactName = ExtractBetween(filter, "name = '", "'");
        var contains = ExtractBetween(filter, "name contains '", "'");
        IEnumerable<Node> matches = Nodes.Values.Where(node => !node.Trashed);
        if (corpora == "drive")
            matches = matches.Where(node => string.Equals(node.DriveId, driveId, StringComparison.Ordinal));
        else if (corpora == "user")
            matches = matches.Where(node => node.DriveId is null);
        if (parent is not null)
        {
            var resolved = parent == "root" ? RootId : parent;
            matches = matches.Where(node => node.Parents.Contains(resolved));
        }
        if (exactName is not null) matches = matches.Where(node => node.Name == Unescape(exactName));
        if (contains is not null)
            matches = matches.Where(node => node.Name.Contains(Unescape(contains), StringComparison.Ordinal));
        var ordered = matches.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var offset = PageOffset(query, "folder-page-");
        var page = ordered.Skip(offset).Take(ListPageSize).ToArray();
        if (InjectedFolderId is { } injected) page = [.. page, Nodes[injected]];
        var next = offset + page.Length < ordered.Length ? offset + page.Length : -1;
        if (AlwaysNextFolderPage) next = offset + page.Length;
        var json = "{\"files\":[" + string.Join(',', page.Select(NodeJson)) + "]"
            + (IncompleteSearch ? ",\"incompleteSearch\":true" : string.Empty)
            + (next < 0 ? string.Empty : ",\"nextPageToken\":" + Q("folder-page-" + next.ToString(CultureInfo.InvariantCulture)))
            + "}";
        return Json(json);
    }

    private HttpResponseMessage CreateFolder(string? body)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body!);
        var name = document.RootElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? "Folder"
            : "Folder";
        var parent = document.RootElement.TryGetProperty("parents", out var parents) &&
                     parents.ValueKind == JsonValueKind.Array
            ? parents.EnumerateArray().Select(value => value.GetString() ?? string.Empty).FirstOrDefault() ?? RootId
            : RootId;
        var parentDriveId = Nodes.TryGetValue(parent, out var parentNode) ? parentNode.DriveId : null;
        var id = "created-" + (_created.Count + 1).ToString(CultureInfo.InvariantCulture);
        _created.Add(id);
        var node = new Node { Id = id, Name = name, Parents = [parent], DriveId = parentDriveId };
        Nodes[id] = node;
        return Json(NodeJson(node));
    }

    private static int PageOffset(Dictionary<string, string> query, string prefix)
    {
        if (!query.TryGetValue("pageToken", out var token) || !token.StartsWith(prefix, StringComparison.Ordinal))
            return 0;
        return int.Parse(token[prefix.Length..], CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0) continue;
            result[Uri.UnescapeDataString(part[..index])] = Uri.UnescapeDataString(part[(index + 1)..]);
        }
        return result;
    }

    private static string? ExtractBetween(string value, string start, string end)
    {
        var from = value.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return null;
        from += start.Length;
        var to = value.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? null : value[from..to];
    }

    private static string Unescape(string value) =>
        value.Replace("\\'", "'", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string NodeJson(Node node) =>
        "{\"id\":" + Q(node.Id) + ",\"name\":" + Q(node.Name) + ",\"mimeType\":" + Q(node.MimeType) +
        ",\"parents\":[" + string.Join(',', node.Parents.Select(Q)) + "],\"trashed\":" +
        (node.Trashed ? "true" : "false") + (node.DriveId is null ? string.Empty : ",\"driveId\":" + Q(node.DriveId)) +
        "}";

    private static string Q(string value) => JsonSerializer.Serialize(value);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Error(string reason) => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent("{\"error\":{\"message\":\"" + reason + "\"}}", Encoding.UTF8,
            "application/json")
    };
}
