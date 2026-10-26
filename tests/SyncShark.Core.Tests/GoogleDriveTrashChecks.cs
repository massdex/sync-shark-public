using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Massdex.SyncShark;

// Real-client checks for the provider-bound Google Drive trash transport. The HTTP fixture is a strictly
// queued provider substitute: it supplies declared responses only, it records every request, and any
// request it was not given fails the check. Every membership, cursor, guard, refusal and readback
// decision is made by the production GoogleDrivePilotClient, which is never mocked.
internal static class GoogleDriveTrashChecks
{
    private const string RootId = "root-folder";
    private const string Account = "perm-account-one";
    private const string SharedDrive = "drive-one";
    private const string BlobMime = "application/octet-stream";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string ShortcutMime = "application/vnd.google-apps.shortcut";

    internal static async Task RunAsync() => await RunAsync(Path.Combine(Path.GetTempPath(),
        "sync-shark-google-trash-checks", Guid.NewGuid().ToString("N")));

    internal static async Task RunAsync(string root)
    {
        // The production client validation requires this build's registered OAuth client identity.
        var previousClientId = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
        var fixture = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            await RunCoreAsync();
        }
        finally
        {
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = previousClientId;
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static async Task RunCoreAsync()
    {
        await ListLegitimateAsync();
        await ListPagingAsync();
        await ListPageSizeRefusedAsync();
        await CursorBindingAsync();
        await ListAncestryRejectionsAsync();
        await ListIncompleteSearchAsync();
        await ListMalformedAsync();
        await ListPageCycleAsync();
        await ListDepthBudgetAsync();
        await ListReadBudgetAsync();
        await SharedDriveListingAsync();
        await RestoreLegitimateAsync();
        await RestoreNestedAsync();
        await RestoreFolderAsync();
        await RestoreIdentityRejectionsAsync();
        await RestoreStaleRejectionsAsync();
        await RestoreGuardRejectionsAsync();
        await RestoreReadbackMismatchAsync();
        await RestoreUncertaintyAsync();
        await RestoreCancellationAsync();
        Console.WriteLine("PASS Google Drive trash listing, cursor binding, ancestry proof, budgets and reviewed restore checks");
    }

    private static async Task ListMalformedAsync()
    {
        foreach (var response in new[] { "{}", "{\"files\":{}}", "{\"files\":null}" })
        {
            var handler = new TrashDriveHandler().About()
                .GetJson("/drive/v3/files/" + RootId, RootJson())
                .GetJson("/drive/v3/files", response);
            try
            {
                await Client(handler).ListTrashPageAsync();
                throw new InvalidOperationException("Malformed Trash listing was accepted as empty.");
            }
            catch (InvalidDataException) { }
            if (handler.PatchCount != 0 || handler.Remaining != 0)
                throw new InvalidOperationException("Malformed listing must reject without effects.");
        }
    }

    // A page of trashed items proves membership through the real parent chain and preserves stable
    // identity, corpus, version, kind, path, digest and restorability for each listed item.
    private static async Task ListLegitimateAsync()
    {
        var notesDigest = Sha("notes");
        var deepDigest = Sha("deep");
        var otherDigest = Sha("other");
        var outsideDigest = Sha("outside");
        var inheritedDigest = Sha("inherited");
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files", ListJson([
                FileJson("notes-file", "notes.txt", RootId, size: 5, digest: notesDigest, version: 4),
                FileJson("deep-file", "deep.txt", "child-folder", size: 6, digest: deepDigest, version: 2),
                FileJson("deep-two", "other.txt", "child-folder", size: 7, digest: otherDigest, version: 3),
                FileJson("outside-file", "outside.txt", "outside-folder", size: 8, digest: outsideDigest),
                FileJson("inherited-file", "inherited.txt", "trashed-folder", size: 9, digest: inheritedDigest,
                    explicitlyTrashed: false)
            ]), "trashed = true")
            .GetJson("/drive/v3/files/child-folder", FolderJson("child-folder", "Child", RootId))
            .GetJson("/drive/v3/files/outside-folder", FolderJson("outside-folder", "Outside", "unrelated-root"))
            .GetJson("/drive/v3/files/unrelated-root", FolderJson("unrelated-root", "Unrelated", null))
            .GetJson("/drive/v3/files/trashed-folder", FolderJson("trashed-folder", "Removed", RootId, trashed: true));
        var client = Client(handler);
        var page = await client.ListTrashPageAsync(pageSize: 50);
        Require(page.ContinueCursor is null, "a completed trash page carries no continuation cursor");
        Require(page.Warnings.Count == 1 && page.Warnings[0].Contains("outside-file", StringComparison.Ordinal) &&
            page.Warnings[0].Contains("bound root", StringComparison.Ordinal),
            "a trashed item outside the bound root is excluded with a visible warning");
        Require(page.Entries.Count == 4, "only verified descendants of the bound root are listed");
        var listed = page.Entries.Single(entry => entry.FileId == "notes-file");
        Require(listed.RelativePath == "notes.txt" && listed.Kind == GoogleDriveEntryKind.Blob &&
            listed.Version == "4" && listed.Name == "notes.txt" && listed.ParentId == RootId &&
            listed.ContentHash == notesDigest && listed.Size == 5 && listed.DriveId is null && listed.CanRestore &&
            listed.ConnectionId == client.Connection.ConnectionId && listed.RootFolderId == RootId,
            "a root-level trashed blob keeps its account, root, identity, version, kind, path and digest");
        Require(page.Entries.Single(entry => entry.FileId == "deep-file").RelativePath == "Child/deep.txt" &&
            page.Entries.Single(entry => entry.FileId == "deep-two").RelativePath == "Child/other.txt" &&
            page.Entries.Single(entry => entry.FileId == "deep-two").CanRestore,
            "nested trashed items keep the full path derived from their verified parent chain");
        Require(!page.Entries.Single(entry => entry.FileId == "inherited-file").CanRestore &&
            page.Entries.Single(entry => entry.FileId == "inherited-file").RelativePath == "Removed/inherited.txt",
            "an item trashed through a still-trashed parent is listed but never offered as restorable");
        Require(handler.Requests.Count(request =>
                request.Uri.Contains("/drive/v3/files/child-folder", StringComparison.Ordinal)) == 1,
            "ancestors are cached within a page instead of being re-read for every item");
        Require(handler.Remaining == 0 && handler.PatchCount == 0,
            "listing consumed exactly its queued reads and issued no write");
        Console.WriteLine("PASS trash listing proves bound-root membership and preserves identity, path, kind and digest");
    }

    private static async Task ListPagingAsync()
    {
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files",
                ListJson([FileJson("notes-file", "notes.txt", RootId, size: 5, digest: Sha("notes"), version: 4)],
                    "page-two"), "trashed = true")
            .About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files",
                ListJson([FileJson("deep-file", "deep.txt", RootId, size: 6, digest: Sha("deep"), version: 2)]),
                "trashed = true");
        var client = Client(handler);
        var first = await client.ListTrashPageAsync(pageSize: 2);
        Require(first.Entries.Count == 1 && first.ContinueCursor is not null,
            "a page with a next token returns a continuation cursor");
        var second = await client.ListTrashPageAsync(first.ContinueCursor, 2);
        Require(second.Entries.Single().RelativePath == "deep.txt" && second.ContinueCursor is null,
            "the continuation cursor resumes the same account, root and corpus and finishes the listing");
        Require(handler.Requests.Any(request => request.Uri.Contains("pageToken=page-two", StringComparison.Ordinal)),
            "the continuation page carries the provider page token");
        Require(handler.Requests.Any(request => request.Uri.Contains("pageSize=2&", StringComparison.Ordinal)),
            "the requested bounded page size is sent");
        Require(handler.Remaining == 0 && handler.PatchCount == 0, "paging consumed exactly its queued reads");
        Console.WriteLine("PASS trash listing pages with an account, root and corpus bound cursor");
    }

    private static async Task ListPageSizeRefusedAsync()
    {
        var handler = new TrashDriveHandler();
        var client = Client(handler);
        foreach (var size in new[] { 0, -1, GoogleDrivePilotClient.MaximumTrashPageSize + 1 })
        {
            try
            {
                await client.ListTrashPageAsync(null, size);
                throw new InvalidOperationException("an out-of-range trash page size was accepted");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        Require(handler.Requests.Count == 0 && handler.Remaining == 0,
            "an out-of-range page size is refused before any Google Drive call");
        Console.WriteLine("PASS a trash page size outside 1..100 is refused before any provider call");
    }

    // The cursor binds the account, root folder and corpus as well as the provider page token.
    private static async Task CursorBindingAsync()
    {
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files",
                ListJson([FileJson("notes-file", "notes.txt", RootId, size: 5, digest: Sha("notes"), version: 4)],
                    "page-two"), "trashed = true");
        var client = Client(handler);
        var page = await client.ListTrashPageAsync(pageSize: 10);
        var cursor = page.ContinueCursor;
        Require(cursor is not null, "the first page exposes a continuation cursor for the binding checks");
        Require(handler.Remaining == 0, "the issuing page consumed exactly its queued requests");

        var malformedHandler = new TrashDriveHandler();
        await ExpectInvalidAsync("a malformed trash page cursor",
            () => Client(malformedHandler).ListTrashPageAsync("not-a-google-trash-cursor"));
        Require(malformedHandler.Requests.Count == 0, "a malformed cursor is rejected before any provider call");

        var foreignKindHandler = new TrashDriveHandler();
        await ExpectInvalidAsync("a cursor that is not a trash cursor",
            () => Client(foreignKindHandler).ListTrashPageAsync(
                Base64Url("{\"kind\":\"some-other-cursor\",\"token\":\"page-two\"}")));
        Require(foreignKindHandler.Requests.Count == 0, "a foreign cursor kind is rejected before any provider call");

        var otherAccountHandler = new TrashDriveHandler();
        await ExpectInvalidAsync("a cursor issued for another account",
            () => Client(otherAccountHandler, Binding("perm-account-two")).ListTrashPageAsync(cursor));
        Require(otherAccountHandler.Requests.Count == 0, "a foreign account cursor is rejected before any provider call");

        var otherRootHandler = new TrashDriveHandler();
        await ExpectInvalidAsync("a cursor issued for another root folder",
            () => Client(otherRootHandler, rootFolderId: "other-root").ListTrashPageAsync(cursor));
        Require(otherRootHandler.Requests.Count == 0, "a foreign root cursor is rejected before any provider call");

        var otherCorpusHandler = new TrashDriveHandler();
        await ExpectInvalidAsync("a user-corpus cursor presented to a shared-drive pair",
            () => Client(otherCorpusHandler, allowSharedDrives: true, sharedDriveId: SharedDrive)
                .ListTrashPageAsync(cursor));
        Require(otherCorpusHandler.Requests.Count == 0, "a foreign corpus cursor is rejected before any provider call");
        Console.WriteLine("PASS trash cursors bind account, root folder and corpus and are refused before any call");
    }

    private static async Task ListAncestryRejectionsAsync()
    {
        // An item with more than one parent has no unambiguous path.
        var twoParentHandler = new TrashDriveHandler();
        twoParentHandler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files", ListJson([
                FileJson("two-parent-file", "two.txt", RootId, size: 3, digest: Sha("two"),
                    parents: [RootId, "other-parent"])
            ]), "trashed = true");
        var twoParent = await Client(twoParentHandler).ListTrashPageAsync();
        Require(twoParent.Entries.Count == 0 && twoParent.Warnings.Count == 1 &&
            twoParent.Warnings[0].Contains("more than one parent", StringComparison.Ordinal),
            "an item with more than one parent is not listed as a descendant");

        // An ancestor that is a shortcut is never followed.
        var shortcutHandler = new TrashDriveHandler();
        shortcutHandler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files", ListJson([
                FileJson("under-shortcut", "linked.txt", "shortcut-folder", size: 4, digest: Sha("linked"))
            ]), "trashed = true")
            .GetJson("/drive/v3/files/shortcut-folder",
                FileJson("shortcut-folder", "Linked", RootId, mimeType: ShortcutMime, shortcutTargetId: "target-1"));
        var shortcut = await Client(shortcutHandler).ListTrashPageAsync();
        Require(shortcut.Entries.Count == 0 && shortcut.Warnings.Count == 1 &&
            shortcut.Warnings[0].Contains("shortcut", StringComparison.Ordinal),
            "a chain through a shortcut is not accepted as root membership");

        // A cyclic parent chain cannot be resolved and fails visibly.
        var cycleHandler = new TrashDriveHandler();
        cycleHandler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files", ListJson([
                FileJson("cycle-a", "a.txt", "cycle-b", size: 1, digest: Sha("a"))
            ]), "trashed = true")
            .GetJson("/drive/v3/files/cycle-b", FolderJson("cycle-b", "b", "cycle-a"));
        var cycleClient = Client(cycleHandler);
        await ExpectBlockedAsync("a cyclic trash parent chain", () => cycleClient.ListTrashPageAsync());
        RequireNoEffect(cycleHandler, "a cyclic trash parent chain");
        Console.WriteLine("PASS ambiguous, shortcut and cyclic ancestry never becomes bound-root membership");
    }

    private static async Task ListIncompleteSearchAsync()
    {
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files",
                ListJson([FileJson("notes-file", "notes.txt", RootId, size: 5, digest: Sha("notes"), version: 4)],
                    null, incomplete: true), "trashed = true");
        var page = await Client(handler).ListTrashPageAsync();
        Require(page.Warnings.Any(warning => warning.Contains("incomplete trash search", StringComparison.Ordinal)),
            "an incomplete trash search is reported instead of being presented as complete");
        Require(page.Entries.Count == 1 && handler.Remaining == 0, "an incomplete search still lists verified items only");
        Console.WriteLine("PASS an incomplete trash search is reported as an explicit warning");
    }

    private static async Task ListPageCycleAsync()
    {
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files", ListJson([], "repeated-token"), "trashed = true")
            .About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files", ListJson([], "repeated-token"), "trashed = true");
        var client = Client(handler);
        var page = await client.ListTrashPageAsync();
        Require(page.ContinueCursor is not null, "the fixture first exposes a continuation token");
        await ExpectBlockedAsync("a repeated trash page token",
            () => client.ListTrashPageAsync(page.ContinueCursor));
        RequireNoEffect(handler, "a repeated trash page token");
        Console.WriteLine("PASS a repeated trash page token fails visibly instead of looping");
    }

    private static async Task ListDepthBudgetAsync()
    {
        var handler = new TrashDriveHandler();
        handler.About().GetJson("/drive/v3/files/" + RootId, RootJson());
        handler.GetJson("/drive/v3/files",
            ListJson([FileJson("deep-item", "deep.txt", "chain-0", size: 1, digest: Sha("deep"))]),
            "trashed = true");
        for (var index = 0; index < GoogleDrivePilotClient.MaximumTrashAncestorDepth; index++)
        {
            var id = "chain-" + index.ToString(CultureInfo.InvariantCulture);
            handler.GetJson("/drive/v3/files/" + id,
                FolderJson(id, id, "chain-" + (index + 1).ToString(CultureInfo.InvariantCulture)));
        }
        var client = Client(handler);
        await ExpectBlockedAsync("a trash parent chain beyond the bounded depth", () => client.ListTrashPageAsync());
        RequireNoEffect(handler, "a trash parent chain beyond the bounded depth");
        Console.WriteLine("PASS a parent chain beyond the bounded depth fails visibly after its bounded reads");
    }

    private static async Task ListReadBudgetAsync()
    {
        const int itemCount = GoogleDrivePilotClient.MaximumTrashMetadataReads + 1;
        var handler = new TrashDriveHandler();
        handler.About().GetJson("/drive/v3/files/" + RootId, RootJson());
        var items = new List<string>();
        for (var index = 0; index < itemCount; index++)
        {
            var id = "item-" + index.ToString(CultureInfo.InvariantCulture);
            items.Add(FileJson(id, id + ".txt", "parent-" + index.ToString(CultureInfo.InvariantCulture),
                size: 1, digest: Sha(id)));
        }
        handler.GetJson("/drive/v3/files", ListJson(items.ToArray()), "trashed = true");
        for (var index = 0; index < GoogleDrivePilotClient.MaximumTrashMetadataReads; index++)
        {
            var id = "parent-" + index.ToString(CultureInfo.InvariantCulture);
            handler.GetJson("/drive/v3/files/" + id, FolderJson(id, id, RootId));
        }
        var client = Client(handler);
        await ExpectBlockedAsync("a trash page beyond the bounded metadata reads", () => client.ListTrashPageAsync());
        RequireNoEffect(handler, "a trash page beyond the bounded metadata reads");
        Console.WriteLine("PASS a page beyond the bounded metadata reads fails visibly instead of trusting ancestry");
    }

    private static async Task SharedDriveListingAsync()
    {
        var digest = Sha("shared");
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson(driveId: SharedDrive))
            .GetJson("/drive/v3/files", ListJson([
                FileJson("shared-file", "shared.txt", RootId, size: 4, digest: digest, driveId: SharedDrive),
                FileJson("drift-file", "drift.txt", RootId, size: 4, digest: Sha("drift"), driveId: "drive-two")
            ]), "corpora=drive");
        var handlerClient = Client(handler, allowSharedDrives: true, sharedDriveId: SharedDrive);
        var page = await handlerClient.ListTrashPageAsync();
        Require(page.Entries.Count == 1 && page.Entries[0].FileId == "shared-file" &&
            page.Entries[0].DriveId == SharedDrive,
            "a shared-drive trash listing keeps only items from the bound drive corpus");
        Require(page.Warnings.Count == 1 && page.Warnings[0].Contains("drift-file", StringComparison.Ordinal),
            "an item from another corpus is excluded with a visible warning");
        Require(handler.Requests.Any(request => request.Uri.Contains("corpora=drive", StringComparison.Ordinal) &&
                request.Uri.Contains("driveId=" + SharedDrive, StringComparison.Ordinal)),
            "the shared-drive corpus is bound explicitly in the query");
        Require(handler.Remaining == 0 && handler.PatchCount == 0, "the shared listing consumed exactly its queued reads");
        Console.WriteLine("PASS a shared-drive trash listing is bound to its exact corpus and drive id");
    }

    private static async Task RestoreLegitimateAsync()
    {
        var digest = Sha("notes");
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files/notes-file", FileJson("notes-file", "notes.txt", RootId, size: 15,
                digest: digest, version: 4));
        var client = Client(handler);
        var entry = Entry(client, "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(handler, "notes.txt");
        Guard(handler, "notes-file", 4, BlobMime, true, RootId);
        Untrash(handler, "notes-file");
        ReadBack(handler, "notes-file", FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 5, trashed: false, explicitlyTrashed: false));
        var restored = await client.RestoreTrashAsync(entry);
        Require(!restored.Trashed && restored.FileId == "notes-file" && restored.RelativePath == "notes.txt" &&
            restored.Kind == GoogleDriveEntryKind.Blob && restored.Parents.Count == 1 &&
            restored.Parents[0] == RootId && restored.Sha256 == digest && restored.Size == 15,
            "a reviewed trashed item is restored with a verified live readback");
        Require(handler.PatchCount == 1, "exactly one conditional metadata write was sent");
        Require(handler.LastPatchBody is not null &&
            handler.LastPatchBody == "{\"labels\":{\"trashed\":false}}",
            "the untrash write asks only to clear the trash label");
        Require(handler.LastPatchIfMatch == "\"notes-file-4\"",
            "the untrash write carries the reviewed strong version guard");
        Require(handler.Requests.Single(request => request.Method == "PATCH").Uri
                .StartsWith("/drive/v2/files/notes-file", StringComparison.Ordinal),
            "the untrash uses the existing guarded metadata write endpoint");
        Require(handler.Remaining == 0 && NoWriteOtherThanPatch(handler),
            "the restore consumed exactly its queued requests and sent no other effect");
        Console.WriteLine("PASS a reviewed trashed item is restored through one guarded write with an exact readback");
    }

    private static async Task RestoreNestedAsync()
    {
        var digest = Sha("deep");
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files/deep-file", FileJson("deep-file", "deep.txt", "child-folder", size: 6,
                digest: digest, version: 2));
        var client = Client(handler);
        var entry = Entry(client, "deep-file", "Child/deep.txt", "child-folder", "2", 6, digest);
        handler.GetJson("/drive/v3/files/child-folder", FolderJson("child-folder", "Child", RootId));
        Siblings(handler, "deep.txt");
        Guard(handler, "deep-file", 2, BlobMime, true, "child-folder");
        Untrash(handler, "deep-file");
        ReadBack(handler, "deep-file", FileJson("deep-file", "deep.txt", "child-folder", size: 6, digest: digest,
            version: 3, trashed: false, explicitlyTrashed: false));
        var restored = await client.RestoreTrashAsync(entry);
        Require(!restored.Trashed && restored.RelativePath == "Child/deep.txt" && restored.Parents[0] == "child-folder",
            "a nested reviewed item is restored in its reviewed parent with the reviewed path");
        Require(handler.PatchCount == 1 && handler.Remaining == 0 && NoWriteOtherThanPatch(handler),
            "the nested restore sent exactly one guarded write and no other effect");
        Console.WriteLine("PASS a nested reviewed item is re-proved through its parent chain before the guarded restore");
    }

    private static async Task RestoreFolderAsync()
    {
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files/nested-folder",
                FolderJson("nested-folder", "Nested", RootId, trashed: true, version: 7));
        var client = Client(handler);
        var entry = Entry(client, "nested-folder", "Nested", RootId, "7", 0, null, GoogleDriveEntryKind.Folder);
        Siblings(handler, "Nested");
        Guard(handler, "nested-folder", 7, FolderMime, true, RootId);
        Untrash(handler, "nested-folder");
        ReadBack(handler, "nested-folder", FolderJson("nested-folder", "Nested", RootId, version: 8));
        var restored = await client.RestoreTrashAsync(entry);
        Require(!restored.Trashed && restored.Kind == GoogleDriveEntryKind.Folder &&
            restored.FileId == "nested-folder" && restored.Parents.Single() == RootId,
            "a reviewed trashed folder is restored as the reviewed object itself");
        // No request for any child of the folder is queued, so a child crawl would fail the check.
        Require(handler.PatchCount == 1 && handler.Remaining == 0 && NoWriteOtherThanPatch(handler),
            "a folder restore is one explicitly selected metadata change with no recursive child effect");
        Console.WriteLine("PASS a folder restore is a single reviewed metadata change with no recursive child effect");
    }

    private static async Task RestoreIdentityRejectionsAsync()
    {
        var digest = Sha("notes");
        var handler = new TrashDriveHandler();
        var client = Client(handler);
        await ExpectBlockedAsync("a trash entry reviewed for another connection", () => client.RestoreTrashAsync(
            new GoogleDriveTrashEntry("gdrive-connection-other", RootId, "notes.txt", "notes-file", "4",
                GoogleDriveEntryKind.Blob, "notes.txt", 15, digest, RootId, null, true)));
        Require(handler.Requests.Count == 0, "a foreign connection identity is refused before any provider call");

        var rootHandler = new TrashDriveHandler();
        var rootClient = Client(rootHandler);
        await ExpectBlockedAsync("a trash entry reviewed for another root folder", () => rootClient.RestoreTrashAsync(
            new GoogleDriveTrashEntry(rootClient.Connection.ConnectionId, "other-root", "notes.txt", "notes-file",
                "4", GoogleDriveEntryKind.Blob, "notes.txt", 15, digest, RootId, null, true)));
        Require(rootHandler.Requests.Count == 0, "a foreign root identity is refused before any provider call");

        var entryHandler = new TrashDriveHandler();
        var entryClient = Client(entryHandler);
        await ExpectBlockedAsync("a trash entry the review did not mark restorable", () => entryClient.RestoreTrashAsync(
            new GoogleDriveTrashEntry(entryClient.Connection.ConnectionId, RootId, "notes.txt", "notes-file", "4",
                GoogleDriveEntryKind.Blob, "notes.txt", 15, digest, RootId, null, false)));
        Require(entryHandler.Requests.Count == 0, "an entry that was not marked restorable is refused before any call");

        var capabilityHandler = new TrashDriveHandler();
        var capabilityClient = Client(capabilityHandler, conditionalWrites: false);
        await ExpectBlockedAsync("a restore where atomic conditional writes are unavailable",
            () => capabilityClient.RestoreTrashAsync(Entry(capabilityClient, "notes-file", "notes.txt", RootId, "4",
                15, digest)));
        RequireNoEffect(capabilityHandler, "a restore where atomic conditional writes are unavailable");
        Console.WriteLine("PASS foreign identity, foreign root, unrestorable entry and missing guard capability are refused");
    }

    private static async Task RestoreStaleRejectionsAsync()
    {
        var digest = Sha("notes");

        var versionScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 5), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item whose version changed",
            () => versionScenario.Client.RestoreTrashAsync(versionScenario.Entry));
        RequireNoEffect(versionScenario.Handler, "a reviewed item whose version changed");

        var nameScenario = RestoreSetup(FileJson("notes-file", "renamed.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item that was renamed",
            () => nameScenario.Client.RestoreTrashAsync(nameScenario.Entry));
        RequireNoEffect(nameScenario.Handler, "a reviewed item that was renamed");

        var parentScenario = RestoreSetup(FileJson("notes-file", "notes.txt", "other-folder", size: 15,
            digest: digest, version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item that moved parent",
            () => parentScenario.Client.RestoreTrashAsync(parentScenario.Entry));
        RequireNoEffect(parentScenario.Handler, "a reviewed item that moved parent");

        var digestScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15,
            digest: Sha("changed"), version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item whose content identity changed",
            () => digestScenario.Client.RestoreTrashAsync(digestScenario.Entry));
        RequireNoEffect(digestScenario.Handler, "a reviewed item whose content identity changed");

        var liveScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4, trashed: false, explicitlyTrashed: false), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item that is no longer trashed",
            () => liveScenario.Client.RestoreTrashAsync(liveScenario.Entry));
        RequireNoEffect(liveScenario.Handler, "a reviewed item that is no longer trashed");

        var corpusScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4, driveId: "drive-two"), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item whose corpus drifted",
            () => corpusScenario.Client.RestoreTrashAsync(corpusScenario.Entry));
        RequireNoEffect(corpusScenario.Handler, "a reviewed item whose corpus drifted");

        var capabilityScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4, canUntrash: false), "notes-file", "notes.txt", RootId, "4", 15, digest);
        await ExpectBlockedAsync("a reviewed item the account cannot restore",
            () => capabilityScenario.Client.RestoreTrashAsync(capabilityScenario.Entry));
        RequireNoEffect(capabilityScenario.Handler, "a reviewed item the account cannot restore");

        var inheritedScenario = RestoreSetup(FileJson("inherited-file", "inherited.txt", "trashed-folder", size: 9,
            digest: digest, version: 3, explicitlyTrashed: false), "inherited-file", "Removed/inherited.txt",
            "trashed-folder", "3", 9, digest);
        inheritedScenario.Handler.GetJson("/drive/v3/files/trashed-folder",
            FolderJson("trashed-folder", "Removed", RootId, trashed: true));
        await ExpectBlockedAsync("a reviewed item under a still-trashed parent",
            () => inheritedScenario.Client.RestoreTrashAsync(inheritedScenario.Entry));
        RequireNoEffect(inheritedScenario.Handler, "a reviewed item under a still-trashed parent");

        var duplicateScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(duplicateScenario.Handler, "notes.txt",
            FileJson("duplicate-file", "notes.txt", RootId, size: 2, digest: Sha("duplicate"),
                trashed: false, explicitlyTrashed: false));
        await ExpectBlockedAsync("a restore that would duplicate an active sibling path",
            () => duplicateScenario.Client.RestoreTrashAsync(duplicateScenario.Entry));
        RequireNoEffect(duplicateScenario.Handler, "a restore that would duplicate an active sibling path");

        var outsideScenario = RestoreSetup(FileJson("outside-file", "outside.txt", "outside-folder", size: 8,
            digest: digest), "outside-file", "outside.txt", "outside-folder", "1", 8, digest);
        outsideScenario.Handler
            .GetJson("/drive/v3/files/outside-folder", FolderJson("outside-folder", "Outside", "unrelated-root"))
            .GetJson("/drive/v3/files/unrelated-root", FolderJson("unrelated-root", "Unrelated", null));
        await ExpectBlockedAsync("a reviewed item outside the bound root",
            () => outsideScenario.Client.RestoreTrashAsync(outsideScenario.Entry));
        RequireNoEffect(outsideScenario.Handler, "a reviewed item outside the bound root");

        var shortcutScenario = RestoreSetup(FileJson("shortcut-file", "linked.txt", RootId, mimeType: ShortcutMime,
            version: 3, shortcutTargetId: "target-1"), "shortcut-file", "linked.txt", RootId, "3", 0, null,
            GoogleDriveEntryKind.Shortcut);
        await ExpectBlockedAsync("a reviewed shortcut",
            () => shortcutScenario.Client.RestoreTrashAsync(shortcutScenario.Entry));
        RequireNoEffect(shortcutScenario.Handler, "a reviewed shortcut");

        var missingHandler = new TrashDriveHandler();
        missingHandler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetStatus("/drive/v3/files/missing-file", HttpStatusCode.NotFound,
                "{\"error\":{\"message\":\"notFound\"}}");
        var missingClient = Client(missingHandler);
        var missingEntry = Entry(missingClient, "missing-file", "missing.txt", RootId, "1", 0, null);
        await ExpectApiFailureAsync("a reviewed file id that no longer exists",
            () => missingClient.RestoreTrashAsync(missingEntry));
        Require(missingHandler.PatchCount == 0 && missingHandler.Remaining == 0,
            "a missing Drive file id is reported with no metadata write");
        Console.WriteLine("PASS stale version, name, parent, content, corpus, capability, inherited trash, duplicate and outside-root restores are refused");
    }

    private static async Task RestoreGuardRejectionsAsync()
    {
        var digest = Sha("notes");

        var raceScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(raceScenario.Handler, "notes.txt");
        Guard(raceScenario.Handler, "notes-file", 5, BlobMime, true, RootId);
        await ExpectBlockedAsync("a concurrent change before the conditional guard was acquired",
            () => raceScenario.Client.RestoreTrashAsync(raceScenario.Entry));
        RequireNoEffect(raceScenario.Handler, "a concurrent change before the conditional guard was acquired");

        var weakGuardScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(weakGuardScenario.Handler, "notes.txt");
        Guard(weakGuardScenario.Handler, "notes-file", 4, BlobMime, true, RootId, includeEtag: false);
        await ExpectBlockedAsync("a missing strong version guard",
            () => weakGuardScenario.Client.RestoreTrashAsync(weakGuardScenario.Entry));
        RequireNoEffect(weakGuardScenario.Handler, "a missing strong version guard");

        var staleScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(staleScenario.Handler, "notes.txt");
        Guard(staleScenario.Handler, "notes-file", 4, BlobMime, true, RootId);
        staleScenario.Handler.Patch("/drive/v2/files/notes-file", HttpStatusCode.PreconditionFailed,
            "{\"error\":{\"message\":\"conditionNotMet\"}}");
        await ExpectBlockedAsync("a stale version guard rejected by the provider",
            () => staleScenario.Client.RestoreTrashAsync(staleScenario.Entry));
        Require(staleScenario.Handler.PatchCount == 1 && staleScenario.Handler.Remaining == 0,
            "a rejected 412 restore is not retried and performs no readback");

        var refusalScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(refusalScenario.Handler, "notes.txt");
        Guard(refusalScenario.Handler, "notes-file", 4, BlobMime, true, RootId);
        refusalScenario.Handler.Patch("/drive/v2/files/notes-file", HttpStatusCode.Forbidden,
            "{\"error\":{\"message\":\"insufficientFilePermissions\"}}");
        await ExpectApiFailureAsync("a permission refusal on the metadata write",
            () => refusalScenario.Client.RestoreTrashAsync(refusalScenario.Entry));
        Require(refusalScenario.Handler.PatchCount == 1 && refusalScenario.Handler.Remaining == 0,
            "a refused metadata write is reported once and is never retried");
        Console.WriteLine("PASS a concurrent change, a missing guard, a stale 412 and a permission refusal stop before any readback");
    }

    private static async Task RestoreReadbackMismatchAsync()
    {
        var digest = Sha("notes");
        var scenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(scenario.Handler, "notes.txt");
        Guard(scenario.Handler, "notes-file", 4, BlobMime, true, RootId);
        Untrash(scenario.Handler, "notes-file");
        ReadBack(scenario.Handler, "notes-file", FileJson("notes-file", "notes.txt", RootId, size: 15,
            digest: digest, version: 5));
        await ExpectUncertainAsync("a restore whose readback still reports the item as trashed",
            () => scenario.Client.RestoreTrashAsync(scenario.Entry));
        Require(scenario.Handler.PatchCount == 1 && scenario.Handler.Remaining == 0,
            "a mismatching readback is reported as unknown and is never repeated");
        Console.WriteLine("PASS a restore readback that does not show the reviewed live item is reported as unknown");
    }

    private static async Task RestoreUncertaintyAsync()
    {
        var digest = Sha("notes");
        var scenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(scenario.Handler, "notes.txt");
        Guard(scenario.Handler, "notes-file", 4, BlobMime, true, RootId);
        scenario.Handler.Fault(HttpMethod.Patch, "/drive/v2/files/notes-file",
            new HttpRequestException("simulated transport loss after the write was sent"));
        await ExpectUncertainAsync("a metadata write whose acknowledgement was interrupted",
            () => scenario.Client.RestoreTrashAsync(scenario.Entry));
        Require(scenario.Handler.PatchCount == 1 && scenario.Handler.Remaining == 0,
            "an interrupted write stays unknown and is never automatically repeated");
        Console.WriteLine("PASS an interrupted restore stays unknown with no automatic repeat");
    }

    private static async Task RestoreCancellationAsync()
    {
        var digest = Sha("notes");
        using var source = new CancellationTokenSource();
        source.Cancel();
        var token = source.Token;

        var listHandler = new TrashDriveHandler();
        listHandler.Fault(HttpMethod.Get, "/drive/v3/about", new OperationCanceledException(token));
        await ExpectCancelledAsync("a cancelled trash listing",
            () => Client(listHandler).ListTrashPageAsync(cancellationToken: token));
        Require(listHandler.PatchCount == 0, "a cancelled listing issues no write");

        var guardScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(guardScenario.Handler, "notes.txt");
        guardScenario.Handler.Fault(HttpMethod.Get, "/drive/v2/files/notes-file", new OperationCanceledException(token));
        await ExpectCancelledAsync("a restore cancelled before the metadata write",
            () => guardScenario.Client.RestoreTrashAsync(guardScenario.Entry, token));
        Require(guardScenario.Handler.PatchCount == 0, "a restore cancelled before the write sends no metadata write");

        var effectScenario = RestoreSetup(FileJson("notes-file", "notes.txt", RootId, size: 15, digest: digest,
            version: 4), "notes-file", "notes.txt", RootId, "4", 15, digest);
        Siblings(effectScenario.Handler, "notes.txt");
        Guard(effectScenario.Handler, "notes-file", 4, BlobMime, true, RootId);
        // Cancellation observed at the moment the write was sent: the request reached the transport and
        // the outcome is unknown, so the item is never restored blindly and the write is never repeated.
        effectScenario.Handler.Fault(HttpMethod.Patch, "/drive/v2/files/notes-file",
            new OperationCanceledException("simulated cancellation during the metadata write"));
        await ExpectUncertainAsync("a restore cancelled after the metadata write was sent",
            () => effectScenario.Client.RestoreTrashAsync(effectScenario.Entry));
        Require(effectScenario.Handler.PatchCount == 1 && effectScenario.Handler.Remaining == 0,
            "a write interrupted by cancellation is never repeated");
        Console.WriteLine("PASS cancellation before the effect issues no write and cancellation after it stays unknown");
    }

    private sealed record RestoreScenario(TrashDriveHandler Handler, GoogleDrivePilotClient Client,
        GoogleDriveTrashEntry Entry);

    private static RestoreScenario RestoreSetup(string fileJson, string fileId, string relativePath,
        string parentId, string version, long size, string? digest,
        GoogleDriveEntryKind kind = GoogleDriveEntryKind.Blob)
    {
        var handler = new TrashDriveHandler();
        handler.About()
            .GetJson("/drive/v3/files/" + RootId, RootJson())
            .GetJson("/drive/v3/files/" + fileId, fileJson);
        var client = Client(handler);
        return new RestoreScenario(handler, client, Entry(client, fileId, relativePath, parentId, version, size,
            digest, kind));
    }

    private static GoogleDriveTrashEntry Entry(GoogleDrivePilotClient client, string fileId, string relativePath,
        string parentId, string version, long size, string? digest,
        GoogleDriveEntryKind kind = GoogleDriveEntryKind.Blob)
    {
        var separator = relativePath.LastIndexOf('/');
        var name = separator < 0 ? relativePath : relativePath[(separator + 1)..];
        return new GoogleDriveTrashEntry(client.Connection.ConnectionId, RootId, relativePath, fileId, version,
            kind, name, size, digest, parentId, null, true);
    }

    private static TrashDriveHandler Siblings(TrashDriveHandler handler, string name, params string[] files) =>
        handler.GetJson("/drive/v3/files", ListJson(files), "name = '" + name + "' and trashed = false");

    private static TrashDriveHandler Guard(TrashDriveHandler handler, string fileId, int version, string mimeType,
        bool trashed, string parentId, bool includeEtag = true) =>
        handler.GetJson("/drive/v2/files/" + fileId,
            GuardJson(fileId, version, mimeType, trashed, parentId, includeEtag));

    private static TrashDriveHandler Untrash(TrashDriveHandler handler, string fileId) =>
        handler.Patch("/drive/v2/files/" + fileId, HttpStatusCode.OK, "{\"id\":\"" + fileId + "\"}");

    private static TrashDriveHandler ReadBack(TrashDriveHandler handler, string fileId, string json) =>
        handler.GetJson("/drive/v3/files/" + fileId, json);

    private static bool NoWriteOtherThanPatch(TrashDriveHandler handler) =>
        handler.Requests.All(request => request.Method is "GET" or "PATCH");

    private static GoogleDriveConnectionBinding Binding(string permissionId = Account, bool sharedDrives = false) =>
        new GoogleDriveConnectionBinding(ClientId: "test-client", AccountKey: GoogleDriveConnectionBinding.CreateAccountKey(permissionId),
            PermissionId: permissionId, Email: permissionId + "@example.invalid", DisplayName: "Drive User",
            Scopes: [GoogleDriveProviderConfiguration.DriveScope],
            CredentialReference: "massdex/account-access/v1/google-drive/sync-shark-test/production/token",
            SharedDrivesEnabled: sharedDrives).Normalize();

    private static GoogleDrivePilotClient Client(TrashDriveHandler handler,
        GoogleDriveConnectionBinding? binding = null, string rootFolderId = RootId, bool allowSharedDrives = false,
        string? sharedDriveId = null, bool conditionalWrites = true)
    {
        var http = new HttpClient(handler);
        return new GoogleDrivePilotClient(http, new StubTokenSource(), (binding ?? Binding()).Normalize(),
            rootFolderId, "/Pair", sharedDriveId: sharedDriveId, allowSharedDrives: allowSharedDrives,
            capabilities: GoogleDriveCapabilities.Baseline.WithSharedDrives(allowSharedDrives)
                with { AtomicConditionalWrites = conditionalWrites });
    }

    private static string RootJson(string? driveId = null) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["id"] = RootId,
            ["name"] = "Pair",
            ["mimeType"] = FolderMime,
            ["parents"] = Array.Empty<string>(),
            ["trashed"] = false,
            ["explicitlyTrashed"] = false,
            ["version"] = "9",
            ["driveId"] = driveId,
            ["capabilities"] = new Dictionary<string, object?> { ["canUntrash"] = false, ["canDownload"] = true }
        });

    private static string FileJson(string id, string name, string? parentId, string? mimeType = null,
        string? driveId = null, long? size = null, string? digest = null, bool trashed = true,
        bool explicitlyTrashed = true, bool canUntrash = true, int version = 1, string? shortcutTargetId = null,
        string[]? parents = null)
    {
        var parentList = parents;
        if (parentList is null)
            parentList = parentId is null ? Array.Empty<string>() : new[] { parentId! };
        var node = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = name,
            ["mimeType"] = mimeType ?? BlobMime,
            ["parents"] = parentList,
            ["trashed"] = trashed,
            ["explicitlyTrashed"] = explicitlyTrashed,
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
            ["headRevisionId"] = "rev-" + id + "-" + version.ToString(CultureInfo.InvariantCulture),
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["canUntrash"] = canUntrash,
                ["canDownload"] = true
            }
        };
        if (driveId is not null) node["driveId"] = driveId;
        if (size is not null)
        {
            node["size"] = size.Value.ToString(CultureInfo.InvariantCulture);
            node["sha256Checksum"] = digest;
            node["md5Checksum"] = digest;
        }
        if (shortcutTargetId is not null)
            node["shortcutDetails"] = new Dictionary<string, object?>
            {
                ["targetId"] = shortcutTargetId,
                ["targetMimeType"] = FolderMime
            };
        return JsonSerializer.Serialize(node);
    }

    private static string FolderJson(string id, string name, string? parentId, bool trashed = false,
        string? driveId = null, int version = 1) =>
        FileJson(id, name, parentId, mimeType: FolderMime, driveId: driveId, version: version, trashed: trashed,
            explicitlyTrashed: trashed);

    private static string ListJson(string[] files, string? nextPageToken = null, bool incomplete = false)
    {
        var extras = new StringBuilder();
        if (nextPageToken is not null)
            extras.Append(",\"nextPageToken\":\"").Append(nextPageToken).Append('"');
        if (incomplete) extras.Append(",\"incompleteSearch\":true");
        return "{\"files\":[" + string.Join(',', files) + "]" + extras + "}";
    }

    private static string GuardJson(string id, int version, string mimeType, bool trashed, string parentId,
        bool includeEtag = true)
    {
        var node = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
            ["mimeType"] = mimeType,
            ["labels"] = new Dictionary<string, object?> { ["trashed"] = trashed },
            ["parents"] = new[] { new Dictionary<string, object?> { ["id"] = parentId } }
        };
        if (includeEtag)
            node["etag"] = "\"" + id + "-" + version.ToString(CultureInfo.InvariantCulture) + "\"";
        return JsonSerializer.Serialize(node);
    }

    private static string Sha(string seed) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Google Drive trash check failed: " + message);
    }

    private static void RequireNoEffect(TrashDriveHandler handler, string label)
    {
        Require(handler.PatchCount == 0, label + " issued no conditional metadata write");
        Require(handler.Requests.All(request => request.Method == "GET"), label + " issued no effect request");
        Require(handler.Remaining == 0, label + " consumed exactly its queued requests");
    }

    private static async Task ExpectBlockedAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label + " was unexpectedly accepted");
        }
        catch (GoogleDriveBlockedException blocked)
        {
            Console.WriteLine("PASS " + label + " refused: " +
                blocked.Message[..Math.Min(80, blocked.Message.Length)]);
        }
    }

    private static async Task ExpectInvalidAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label + " was unexpectedly accepted");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("PASS " + label + " refused");
        }
    }

    private static async Task ExpectUncertainAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label + " was unexpectedly accepted");
        }
        catch (GoogleDriveEffectUncertainException)
        {
            Console.WriteLine("PASS " + label + " stayed unknown");
        }
    }

    private static async Task ExpectCancelledAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label + " was unexpectedly completed");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS " + label + " cancelled");
        }
    }

    private static async Task ExpectApiFailureAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label + " was unexpectedly accepted");
        }
        catch (GoogleDriveApiException failure)
        {
            Console.WriteLine("PASS " + label + " refused with " + failure.StatusCode);
        }
    }

    private sealed record RecordedRequest(string Method, string Uri, string? IfMatch, string Body);

    private sealed record PlannedStep(HttpMethod Method, string AbsolutePath, string? QueryContains,
        HttpStatusCode Status, string Body, Exception? Error)
    {
        internal string Describe() => Method.Method + " " + AbsolutePath +
                                      (QueryContains is null ? string.Empty : " containing '" + QueryContains + "'");
    }

    // Strictly queued provider substitute. Every request must match the next declared step and every
    // declared step must be consumed, so an unexpected read, a missing guard or any prohibited effect
    // fails the check instead of passing silently.
    private sealed class TrashDriveHandler : HttpMessageHandler
    {
        private readonly List<PlannedStep> _steps = [];
        private readonly List<RecordedRequest> _requests = [];
        private int _index;

        internal IReadOnlyList<RecordedRequest> Requests => _requests;

        internal int Remaining => _steps.Count - _index;

        internal int PatchCount => _requests.Count(request => request.Method == "PATCH");

        internal string? LastPatchBody =>
            _requests.LastOrDefault(request => request.Method == "PATCH")?.Body;

        internal string? LastPatchIfMatch =>
            _requests.LastOrDefault(request => request.Method == "PATCH")?.IfMatch;

        internal TrashDriveHandler About() => GetJson("/drive/v3/about", JsonSerializer.Serialize(new
        {
            user = new
            {
                permissionId = Account,
                emailAddress = Account + "@example.invalid",
                displayName = "Drive User"
            },
            storageQuota = new { limit = "107374182400", usage = "1024" }
        }));

        internal TrashDriveHandler GetJson(string absolutePath, string body, string? queryContains = null) =>
            Plan(HttpMethod.Get, absolutePath, body, queryContains, HttpStatusCode.OK, null);

        internal TrashDriveHandler GetStatus(string absolutePath, HttpStatusCode status, string body) =>
            Plan(HttpMethod.Get, absolutePath, body, null, status, null);

        internal TrashDriveHandler Patch(string absolutePath, HttpStatusCode status, string body,
            string? queryContains = null) =>
            Plan(HttpMethod.Patch, absolutePath, body, queryContains, status, null);

        internal TrashDriveHandler Fault(HttpMethod method, string absolutePath, Exception error,
            string? queryContains = null) =>
            Plan(method, absolutePath, string.Empty, queryContains, HttpStatusCode.OK, error);

        private TrashDriveHandler Plan(HttpMethod method, string absolutePath, string body, string? queryContains,
            HttpStatusCode status, Exception? error)
        {
            _steps.Add(new PlannedStep(method, absolutePath, queryContains, status, body, error));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var method = request.Method.Method;
            var ifMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.First().ToString() : null;
            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            _requests.Add(new RecordedRequest(method, uri.AbsolutePath + uri.Query, ifMatch, body));
            if (_index >= _steps.Count)
                throw new InvalidOperationException(
                    "Unexpected Google Drive request with no queued step: " + method + " " + uri.AbsolutePath);
            var step = _steps[_index];
            var decodedQuery = Uri.UnescapeDataString(uri.Query);
            if (!string.Equals(step.Method.Method, method, StringComparison.Ordinal) ||
                !string.Equals(step.AbsolutePath, uri.AbsolutePath, StringComparison.Ordinal) ||
                (step.QueryContains is not null &&
                 !decodedQuery.Contains(step.QueryContains, StringComparison.Ordinal)))
                throw new InvalidOperationException("Unexpected Google Drive request: expected " + step.Describe() +
                    " but received " + method + " " + uri.AbsolutePath);
            _index++;
            if (step.Error is not null) throw step.Error;
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubTokenSource : IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-access-token");
    }
}
