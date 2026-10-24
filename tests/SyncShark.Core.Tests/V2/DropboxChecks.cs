using System.Net;

namespace Massdex.SyncShark.V2;

/// <summary>The v2 Dropbox endpoint contract checks (dropbox-inventory.md §6).</summary>
internal static partial class DropboxChecks
{
    private const string RootId = "rb";

    private static readonly IProgress<string> Sink = new ProgressSink();
    private static string _root = "";

    public static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh bounded test directory.");
        Directory.CreateDirectory(root);
        _root = root;
        var failures = new List<string>();
        await Group(nameof(ProviderNamesAsync), () => ProviderNamesAsync(Path.Combine(root, "provider-names")), failures);
        _root = root;
        await Group(nameof(RemovalReviewDropboxAsync), RemovalReviewDropboxAsync, failures);
        await Group(nameof(FullChecksAsync), FullChecksAsync, failures);
        await Group(nameof(ProgressCallbackFaultsAsync), ProgressCallbackFaultsAsync, failures);
        await Group(nameof(ExcludedNestedListingAndReplayAsync), ExcludedNestedListingAndReplayAsync, failures);
        await Group(nameof(FullRootChecksAsync), FullRootChecksAsync, failures);
        await Group(nameof(MembershipTimeoutIsPerItemAsync), MembershipTimeoutIsPerItemAsync, failures);
        await Group(nameof(ReadBudgetScopeAsync), ReadBudgetScopeAsync, failures);
        await Group(nameof(RetryWaitPauseAndCancelAsync), RetryWaitPauseAndCancelAsync, failures);
        await Group(nameof(DeltaChecksAsync), DeltaChecksAsync, failures);
        await Group(nameof(IntakeChecksAsync), IntakeChecksAsync, failures);
        await Group(nameof(EvidenceHistoryPagingAsync), EvidenceHistoryPagingAsync, failures);
        await Group(nameof(DeltaDeletionTimeAsync), DeltaDeletionTimeAsync, failures);
        await Group(nameof(PrepareCursorChecksAsync), PrepareCursorChecksAsync, failures);
        await Group(nameof(DeltaFolderRunsAsync), DeltaFolderRunsAsync, failures);
        await Group(nameof(DeltaPendingRunsAsync), DeltaPendingRunsAsync, failures);
        await Group(nameof(DeltaEvidenceBoundAsync) + "(permanent)", () => DeltaEvidenceBoundAsync("permanent"), failures);
        await Group(nameof(DeltaEvidenceBoundAsync) + "(attempts)", () => DeltaEvidenceBoundAsync("attempts"), failures);
        await Group(nameof(DeltaEvidenceBoundAsync) + "(age)", () => DeltaEvidenceBoundAsync("age"), failures);
        await Group(nameof(DeltaReplacementAsync), DeltaReplacementAsync, failures);
        await Group(nameof(HashFromStoreAsync), HashFromStoreAsync, failures);
        await Group(nameof(HashFallbackMatchesStoredVersionAsync), HashFallbackMatchesStoredVersionAsync, failures);
        await Group(nameof(DeletionTimesFromHistoryAsync), DeletionTimesFromHistoryAsync, failures);
        await Group(nameof(DeletedListingChecksAsync), DeletedListingChecksAsync, failures);
        await Group(nameof(OnePassDeletionChecksAsync), OnePassDeletionChecksAsync, failures);
        await Group(nameof(DeletionEvidenceCacheChecksAsync), DeletionEvidenceCacheChecksAsync, failures);
        await Group(nameof(EvidenceValidationReviewChecksAsync), EvidenceValidationReviewChecksAsync, failures);
        await Group(nameof(DeletionHistoryFailureChecksAsync), DeletionHistoryFailureChecksAsync, failures);
        await Group(nameof(RunClientModifiedAsync), () => RunClientModifiedAsync(root), failures);
        await Group(nameof(PutCreateAsync), PutCreateAsync, failures);
        await Group(nameof(BatchRootPathChecksAsync), BatchRootPathChecksAsync, failures);
        await Group(nameof(PutOccupiedAsync), PutOccupiedAsync, failures);
        await Group(nameof(PutUpdateAsync), PutUpdateAsync, failures);
        await Group(nameof(DeleteFileAsync), DeleteFileAsync, failures);
        await Group(nameof(DeleteFolderAsync), DeleteFolderAsync, failures);
        await Group(nameof(FolderTreeChecksAsync), FolderTreeChecksAsync, failures);
        await Group(nameof(IgnoreFolderDeleteChecksAsync), IgnoreFolderDeleteChecksAsync, failures);
        await Group(nameof(DeleteUncertainAsync), DeleteUncertainAsync, failures);
        await Group(nameof(MoveChecksAsync), MoveChecksAsync, failures);
        await Group(nameof(CaseRenameSqliteAsync), CaseRenameSqliteAsync, failures);
        await Group(nameof(MkDirChecksAsync), MkDirChecksAsync, failures);
        await Group(nameof(JobCheckCacheChecksAsync), JobCheckCacheChecksAsync, failures);
        await Group(nameof(PreserveChecksAsync), PreserveChecksAsync, failures);
        await Group(nameof(OpenReadChecksAsync), () => OpenReadChecksAsync(Path.Combine(root, "spool")), failures);
        await Group(nameof(DownloadToChecksAsync), () => DownloadToChecksAsync(Path.Combine(root, "spool-direct")), failures);
        await Group(nameof(DownloadSingleCallChecksAsync), DownloadSingleCallChecksAsync, failures);
        await Group(nameof(DownloadRefusalChecksAsync), DownloadRefusalChecksAsync, failures);
        await Group(nameof(DownloadRefusalMappingChecksAsync), DownloadRefusalMappingChecksAsync, failures);
        await Group(nameof(ObserveChecksAsync), ObserveChecksAsync, failures);
        if (failures.Count > 0)
        {
            Console.WriteLine("CHECK-GROUP-FAILURES " + failures.Count + ": " + string.Join(", ", failures));
            throw new InvalidOperationException("v2-dropbox-checks failed in " + failures.Count +
                " check group(s); see CHECK-GROUP-FAIL lines above.");
        }
        Console.WriteLine("--v2-dropbox-checks passed.");
    }

    // Runs one top-level check group; a failing group is reported and recorded so it cannot hide the rest.
    private static async Task Group(string name, Func<Task> body, List<string> failures)
    {
        try
        {
            await body();
        }
        catch (Exception error)
        {
            Console.WriteLine("CHECK-GROUP-FAIL " + name + ": " + error.GetType().Name + ": " + error.Message);
            Console.WriteLine(error);
            failures.Add(name);
        }
    }

    private static async Task FullChecksAsync()
    {
        using var h = new Harness();
        var modified = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        h.Wire.SeedFile("a.txt", "hello", modified);
        h.Wire.SeedFolder("sub");
        h.Wire.SeedFile("sub/b.txt", "inner");
        h.Wire.SeedFile("NUL.txt", "x");
        var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.Count == 4, "all four items are listed");
        var file = snapshot.Items.Single(item => item.Path == "a.txt");
        Require(file.Kind == ItemKind.File && file.VersionId is not null && file.Hash is not null &&
            file.Size == 5 && file.Mtime == modified, "a file maps id/rev/hash/size/client_modified");
        var folder = snapshot.Items.Single(item => item.Path == "sub");
        Require(folder.Kind == ItemKind.Folder && folder.VersionId is null && folder.Mtime == DateTimeOffset.UnixEpoch,
            "a folder maps with epoch mtime and no version");
        Require(snapshot.Items.Single(item => item.Path == "NUL.txt").Flags.HasFlag(ItemFlags.Unreadable),
            "an unsafe Windows name is flagged unreadable");
        Require(snapshot.IncompleteSubtrees.Count == 0, "a healthy listing is complete");

        var included = await h.Inventory.FullAsync(2, path => path != "sub" && !path.StartsWith("sub/"), Sink,
            CancellationToken.None);
        Require(included.Items.Count == 2 && included.Items.All(item => item.Path != "sub"),
            "an excluded folder's items are absent");

        var cursorIndex = h.Wire.Calls.IndexOf("/2/files/list_folder/get_latest_cursor");
        var listIndex = h.Wire.Calls.IndexOf("/2/files/list_folder");
        Require(cursorIndex >= 0 && listIndex > cursorIndex, "the cursor is taken before the first listing");
        Require(included.CursorAfter == h.Wire.LatestCursor + "-end", "the snapshot cursor is the last fully drained listing-race cursor");
    }

    private static async Task ExcludedNestedListingAndReplayAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFolder("parent");
        h.Wire.SeedFolder("parent/private");
        h.Wire.SeedFolder("parent/private/deep");
        h.Wire.SeedFile("parent/private/deep/hidden.txt", "hidden");
        h.Wire.SeedFile("parent/visible.txt", "visible");
        var parent = (await h.Client.FindEntryAtPathAsync("parent", CancellationToken.None))!;
        bool Include(string path) => !PairPath.IsSelfOrUnder(path, "parent/private");
        h.Wire.SeedChangePage(h.Wire.LatestCursor,
            [h.Wire.ChangeFolder("parent/private"), h.Wire.ChangeFile("parent/private/replayed.txt", "replayed"),
             h.Wire.ChangeFile("parent/replayed.txt", "visible replay")], false, "after-replay");
        var snapshot = await h.Inventory.FullAsync(1, Include, Sink, CancellationToken.None);
        Require(snapshot.IncompleteSubtrees.Count == 0 && snapshot.Items.Select(item => item.Path)
                .SequenceEqual(["parent", "parent/replayed.txt", "parent/visible.txt"]),
            "an excluded nested folder and descendants stay absent, including cursor-replayed upserts");
        Require(h.Wire.ListRequests.Count == 2 &&
            h.Wire.ListRequests.Any(request => request.Path == DropboxWire.RootId && !request.Recursive) &&
            h.Wire.ListRequests.Any(request => request.Path == parent.Id && request.Recursive),
            "Dropbox retains its bounded recursive subtree listing rather than one API call per nested folder");
        Require(snapshot.IncludesPath is not null && !snapshot.IncludesPath("parent/private/hidden.txt"),
            "the full snapshot carries its exclusion authority to storage");
        h.Wire.SeedChangePage("delta-excluded",
            [h.Wire.ChangeFile("parent/private/replayed.txt", "replayed"),
             h.Wire.ChangeDeleted("/Pilot/parent/private/deep"),
             h.Wire.ChangeFile("parent/next.txt", "next")], false, "delta-after");
        var delta = await h.Inventory.ChangesSinceAsync("delta-excluded", 2, Include, CancellationToken.None);
        Require(!delta.CursorInvalid && delta.Changes.Select(change => change.Path).SequenceEqual(["parent/next.txt"]) &&
            delta.IncludesPath is not null && !delta.IncludesPath("parent/private/deep"),
            "delta replay never reintroduces excluded changes and carries its exclusion authority to storage");
    }

    private static async Task FullRootChecksAsync()
    {
        using var moved = new Harness();
        moved.Wire.RootPath = "/Other";
        moved.Wire.SeedFile("a.txt", "hello");
        var snapshot = await moved.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.Count == 0 && snapshot.IncompleteSubtrees.SequenceEqual(new[] { "" }) &&
            snapshot.CursorAfter == "", "a moved root yields a whole-root-incomplete snapshot");

        using var outage = new Harness();
        outage.Wire.Script("/2/files/list_folder", HttpStatusCode.ServiceUnavailable, 64);
        // The read path now retries transient failures until its total budget is spent, so an
        // outage that cannot recover inside that budget must still propagate instead of absence.
        var outageClock = new ResilienceChecks.Clock();
        outage.Client.ReadTransientBudget = TimeSpan.FromSeconds(3);
        outage.Client.ReadRetryTime = outageClock;
        outage.Client.ReadRetryDelay = (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            outageClock.Advance(delay);
            return Task.CompletedTask;
        };
        var threw = false;
        try { await outage.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None); }
        catch (Exception) { threw = true; }
        Require(threw, "a listing outage beyond the read budget propagates instead of reporting absence");
    }

    private static async Task DeltaFeedOrderChecksAsync()
    {
        // Exercise the real HTTP parser, sequential page reader and inventory collapse together.
        foreach (var acrossPages in new[] { false, true })
        {
            using var h = new Harness();
            SeedStored(h, Stored("ordered.txt", "id:ordered", "old-hash"));
            h.Wire.SeedDeletedEntry("ordered.txt");
            var file = h.Wire.ChangeFile("ordered.txt", "updated");
            var deleted = h.Wire.ChangeDeleted("/Pilot/ordered.txt");
            if (acrossPages)
            {
                h.Wire.SeedChangePage("ordered-start", new[] { file }, true, "ordered-middle");
                h.Wire.SeedChangePage("ordered-middle", new[] { deleted }, false, "ordered-end");
            }
            else
                h.Wire.SeedChangePage("ordered-start", new[] { file, deleted }, false, "ordered-end");
            var gen = h.Known.BeginGeneration(RootId, "delta");
            var delta = await h.Inventory.ChangesSinceAsync("ordered-start", gen, _ => true, CancellationToken.None);
            Require(!delta.CursorInvalid && delta.CursorAfter == "ordered-end" &&
                delta.Changes.Count == 1 && delta.Changes[0].Kind == ChangeKind.Delete &&
                delta.Changes[0].Path == "ordered.txt",
                $"file then delete {(acrossPages ? "across pages" : "in one page")} keeps only the final tombstone");
            h.Known.WriteDelta(delta);
            h.Known.CompleteGeneration(gen, delta.CursorAfter, true);
            Require(h.Known.LiveUnder(RootId, new[] { "ordered.txt" }).Count == 0,
                "an earlier feed upsert cannot resurrect the deleted stored file");
        }

        using var recreated = new Harness();
        SeedStored(recreated, Stored("recreated.txt", "id:old", "old-hash"));
        recreated.Wire.SeedDeletedEntry("recreated.txt");
        recreated.Wire.SeedChangePage("recreate-start",
            new[] { recreated.Wire.ChangeDeleted("/Pilot/recreated.txt") }, true, "recreate-middle");
        recreated.Wire.SeedChangePage("recreate-middle",
            new[] { recreated.Wire.ChangeFile("recreated.txt", "new content") }, false, "recreate-end");
        var recreateGen = recreated.Known.BeginGeneration(RootId, "delta");
        var recreation = await recreated.Inventory.ChangesSinceAsync("recreate-start", recreateGen,
            _ => true, CancellationToken.None);
        Require(!recreation.CursorInvalid && recreation.CursorAfter == "recreate-end" &&
            recreation.Changes.Count == 2 && recreation.Changes[0].Kind == ChangeKind.Delete &&
            recreation.Changes[1].Kind == ChangeKind.Upsert,
            "delete then recreate across pages retains the later live entry");
        recreated.Known.WriteDelta(recreation);
        recreated.Known.CompleteGeneration(recreateGen, recreation.CursorAfter, true);
        var live = recreated.Known.LiveUnder(RootId, new[] { "recreated.txt" });
        Require(live.Count == 1 && live[0].ObjectId == recreation.Changes[1].ObjectId &&
            live[0].Hash == recreation.Changes[1].State!.Hash,
            "the recreated file survives delta storage with its new identity and content");
    }

    private static async Task DeltaChecksAsync()
    {
        await DeltaFeedOrderChecksAsync();
        using var h = new Harness();
        h.Wire.SeedChangePage("c1", new[] { h.Wire.ChangeFile("a.txt", "1") }, true, "c2");
        h.Wire.SeedChangePage("c2", new[] { h.Wire.ChangeFile("b.txt", "2") }, false, "c2");
        var multi = await h.Inventory.ChangesSinceAsync("c1", 2, _ => true, CancellationToken.None);
        Require(!multi.CursorInvalid && multi.Changes.Count == 2 && multi.CursorAfter == "c2" &&
            multi.Changes.All(change => change.Kind == ChangeKind.Upsert), "two pages collapse to all changes");

        h.Wire.SeedChangePage("d1", new[]
        {
            h.Wire.ChangeFile("F/x", "1"), h.Wire.ChangeDeleted("/Pilot/F"), h.Wire.ChangeFile("F/z", "2"),
            h.Wire.ChangeFile("a", "3"), h.Wire.ChangeDeleted("/Pilot/a")
        }, false, "d1");
        var collapsed = await h.Inventory.ChangesSinceAsync("d1", 3, _ => true, CancellationToken.None);
        Require(collapsed.Changes.Count == 3 &&
            collapsed.Changes.Count(c => c.Kind == ChangeKind.Delete && c.Path == "F") == 1 &&
            collapsed.Changes.Count(c => c.Kind == ChangeKind.Delete && c.Path == "a") == 1 &&
            collapsed.Changes.Count(c => c.Kind == ChangeKind.Upsert && c.Path == "F/z") == 1,
            "the feed collapses to {Delete F, Delete a, Upsert F/z}");

        SeedStored(h, Stored("Docs/A.txt", "id:a", "h"));
        h.Wire.SeedChangePage("r1", new[] { h.Wire.ChangeDeletedLower("/pilot/docs/a.txt") }, false, "r1");
        var resolved = await h.Inventory.ChangesSinceAsync("r1", 4, _ => true, CancellationToken.None);
        var delete = resolved.Changes.Single();
        Require(delete.Kind == ChangeKind.Delete && delete.Path == "Docs/A.txt" && delete.ObjectId == "id:a",
            "a deleted path resolves to its stored spelling and id once Dropbox no longer holds that object");

        // B-126: a replay after the exact spelling's row is gone must not name a surviving spelling Dropbox holds.
        using (var clash = new Harness())
        {
            clash.Wire.SeedFile("Docs/A.txt", "y");
            var survivor = (await clash.Client.FindEntryAtPathAsync("Docs/A.txt", CancellationToken.None))!;
            SeedStored(clash, Stored("Docs/A.txt", survivor.Id!, "h"));
            clash.Wire.SeedChangePage("k1", new[] { clash.Wire.ChangeDeleted("/Pilot/Docs/a.txt") }, false, "k1");
            var replay = (await clash.Inventory.ChangesSinceAsync("k1", 5, _ => true, CancellationToken.None))
                .Changes.Single();
            Require(replay.Kind == ChangeKind.Delete && replay.ObjectId.Length == 0 && replay.Path == "Docs/a.txt",
                "a deleted spelling never borrows the identity of another stored spelling Dropbox still holds");
        }

        // B-126 rework: membership, not existence. An object moved outside the root (still in the account) arrives
        // as a path_lower-only delete; its stored spelling must go, and an unreadable membership fails the delta.
        using (var away = new Harness())
        {
            away.Wire.SeedFile("Docs/A.txt", "y");
            var moved = (await away.Client.FindEntryAtPathAsync("Docs/A.txt", CancellationToken.None))!;
            SeedStored(away, Stored("Docs/A.txt", moved.Id!, "h"));
            away.Wire.MoveOutsideRoot("Docs/A.txt", "Elsewhere/A.txt");
            away.Wire.SeedChangePage("m1", new[] { away.Wire.ChangeDeletedLower("/pilot/docs/a.txt") }, false, "m1");
            var departed = (await away.Inventory.ChangesSinceAsync("m1", 6, _ => true, CancellationToken.None))
                .Changes.Single();
            Require(departed.Kind == ChangeKind.Delete && departed.ObjectId == moved.Id && departed.Path == "Docs/A.txt",
                "an object moved outside the root deletes its exact stored spelling, never leaving a ghost row");
            away.Wire.Script("/2/files/get_metadata", HttpStatusCode.BadRequest, 1);
            await ExpectAsync<Exception>(() => away.Inventory.ChangesSinceAsync("m1", 7, _ => true, CancellationToken.None));
        }

        // Astra re-review 3: a malformed provider path is unknown membership, never a proven departure.
        foreach (var malformed in new[] { "", "Elsewhere//A.txt", "Elsewhere/", "Elsewhere/../Pilot/Docs/B.txt",
                     "Elsewhere/./A.txt", "Elsewhere/A\u0001.txt" })
        {
            using var odd = new Harness();
            odd.Wire.SeedFile("Docs/A.txt", "y");
            var entry = (await odd.Client.FindEntryAtPathAsync("Docs/A.txt", CancellationToken.None))!;
            SeedStored(odd, Stored("Docs/A.txt", entry.Id!, "h"));
            odd.Wire.MoveOutsideRoot("Docs/A.txt", malformed);
            odd.Wire.SeedChangePage("o1", new[] { odd.Wire.ChangeDeletedLower("/pilot/docs/a.txt") }, false, "o1");
            await ExpectAsync<InvalidDataException>(() =>
                odd.Inventory.ChangesSinceAsync("o1", 8, _ => true, CancellationToken.None));
        }

        using var invalid = new Harness();
        invalid.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Conflict, 1);
        Require((await invalid.Inventory.ChangesSinceAsync("c1", 2, _ => true, CancellationToken.None)).CursorInvalid,
            "an expired cursor yields an invalid delta");
        var before = invalid.Wire.Calls.Count;
        var empty = await invalid.Inventory.ChangesSinceAsync("", 2, _ => true, CancellationToken.None);
        Require(empty.CursorInvalid && invalid.Wire.Calls.Count == before, "an empty cursor is invalid without a call");
        invalid.Wire.RootPath = "/Other";
        invalid.Wire.SeedChangePage("c1", Array.Empty<object>(), false, "c1");
        Require((await invalid.Inventory.ChangesSinceAsync("c1", 2, _ => true, CancellationToken.None)).CursorInvalid,
            "a moved root yields an invalid delta");
    }

    // Exercise real retry decisions without spending the production budget in wall-clock time.
    private static List<TimeSpan> UseFastHistoryRetryClock(Harness h)
    {
        var clock = new ResilienceChecks.Clock();
        var delays = new List<TimeSpan>();
        h.Client.ReadTransientBudget = TimeSpan.FromSeconds(3);
        h.Client.ReadRetryTime = clock;
        h.Client.ReadRetryDelay = (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            delays.Add(delay);
            clock.Advance(delay);
            return Task.CompletedTask;
        };
        return delays;
    }

    private static async Task DeltaDeletionTimeAsync()
    {
        using (var h = new Harness())
        {
            h.Wire.SeedChangePage("t1", new[] { h.Wire.ChangeDeleted("/Pilot/gone.txt") }, false, "t1");
            h.Wire.SeedDeletedEntry("gone.txt");
            var delta = await h.Inventory.ChangesSinceAsync("t1", 6, _ => true, CancellationToken.None);
            var delete = delta.Changes.Single();
            Require(delete.Kind == ChangeKind.Delete &&
                delete.DeletedUtc == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                "a delete carries Dropbox's server_deleted time from revision history");
        }

        using (var h = new Harness())
        {
            h.Wire.SeedChangePage("t1", new[] { h.Wire.ChangeDeleted("/Pilot/lost.txt") }, false, "t1");
            h.Wire.SeedDeletedEntry("lost.txt");
            var delays = UseFastHistoryRetryClock(h);
            h.Wire.OnRevisionAsync = _ => throw new TaskCanceledException("scripted request timeout");
            var evidence = await h.Client.LookupDeletionEvidenceForV2Async(["lost.txt"], CancellationToken.None);
            Require(evidence["lost.txt"].LookupFailed && !evidence["lost.txt"].PermanentFailure &&
                evidence["lost.txt"].DeletedUtc is null && delays.Count > 0 &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") > 1,
                "transport timeout retries within budget and exhausts as transient unknown evidence");
            var delta = await h.Inventory.ChangesSinceAsync("t1", 6, _ => true, CancellationToken.None);
            Require(!delta.CursorInvalid && delta.Changes.Single() is { Kind: ChangeKind.Delete, DeletedUtc: null } &&
                delta.PendingDeletionEvidence.ContainsKey("lost.txt") && h.Known.SyncRetryAt("p1", "lost.txt")?.Held is not null,
                "remote tombstone is observed with no invented deletion time; durable evidence hold prevents endpoint effects");
        }
    }

    private static async Task DeltaReplacementAsync()
    {
        using var h = new Harness();
        SeedStored(h, Stored("F", "id:folder", null, ItemKind.Folder), Stored("F/x", "id:x", "hx"));
        h.Wire.SeedChangePage("g1", new[] { h.Wire.ChangeFile("F", "now-a-file") }, false, "g1");
        var delta = await h.Inventory.ChangesSinceAsync("g1", 7, _ => true, CancellationToken.None);
        Require(delta.Changes.Count(c => c.Kind == ChangeKind.Delete && c.Path == "F/x" && c.ObjectId == "id:x") == 1,
            "a file replacing folder F deletes the stored descendant F/x in the same delta");
        Require(delta.Changes.Count(c => c.Kind == ChangeKind.Upsert && c.Path == "F") == 1,
            "the file at F is upserted");
    }

    private static async Task HashFromStoreAsync()
    {
        using var h = new Harness();
        SeedStored(h, Stored("a.txt", "id:a", "hash-a"));
        var hashes = await h.Inventory.HashAsync(new[] { "a.txt" }, CancellationToken.None);
        Require(hashes.TryGetValue("a.txt", out var hash) && hash == "hash-a" && h.Wire.Calls.Count == 0,
            "stored hashes are returned without a provider call");
    }

    // Audit addendum A: a fallback hash read from the provider belongs to the stored row only while Dropbox still
    // reports that row's id and revision; a newer revision (or an unstored path) stays unresolved.
    private static async Task HashFallbackMatchesStoredVersionAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFile("same.txt", "one");
        h.Wire.SeedFile("newer.txt", "two");
        h.Wire.SeedFile("unstored.txt", "three");
        h.Wire.SeedFile("replaced.txt", "four");
        var listed = (await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None)).Items
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        SeedStored(h, listed["same.txt"] with { Hash = null },
            listed["newer.txt"] with { Hash = null, VersionId = "r-older" },
            listed["replaced.txt"] with { Hash = null, ObjectId = "id:replaced-earlier" });
        var hashes = await h.Inventory.HashAsync(new[] { "same.txt", "newer.txt", "unstored.txt", "replaced.txt" },
            CancellationToken.None);
        Require(hashes.TryGetValue("same.txt", out var hash) && hash == listed["same.txt"].Hash,
            "a fallback hash is accepted when the observed id and revision match the stored row");
        Require(!hashes.ContainsKey("newer.txt"), "a fallback hash of a newer revision is left unresolved");
        Require(!hashes.ContainsKey("unstored.txt"), "a path with no stored row is left unresolved");
        Require(!hashes.ContainsKey("replaced.txt"), "a fallback hash of another item at the path is left unresolved");
    }

    // A first-sync preview reads deletion times from revision history; an unproven path is left out.
    private static async Task DeletionTimesFromHistoryAsync()
    {
        using var h = new Harness();
        h.Wire.SeedDeletedEntry("gone.txt");
        var times = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt", "never-there.txt" }, CancellationToken.None);
        Require(times.Count == 1 && times.TryGetValue("gone.txt", out var deleted) &&
            deleted == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
            "a deletion proven by revision history carries its server_deleted time; an unproven path is left out");
    }

    // The first-sync deletion pass reads one paginated deleted inventory: the selected root is verified once
    // before the first page and once after the last (never per page), the page size matches the main listing,
    // and the revision-history reads run at most sixteen at a time with results identical to a sequential pass.
    private static async Task DeletedListingChecksAsync()
    {
        using (var h = new Harness())
        {
            for (var index = 0; index < 5; index++) h.Wire.SeedDeletedEntry("gone-" + index + ".txt");
            h.Wire.DeletedPageSize = 2;
            var before = h.Wire.Calls.Count;
            var listed = await h.Client.ListDeletedEntriesAsync(CancellationToken.None);
            Require(listed.Count == 5, "a multi-page deleted listing returns every deleted entry");
            Require(h.Wire.Calls.Count(call => call == "/2/files/get_metadata") == 2,
                "a multi-page deleted listing verifies the root exactly twice, once at each end");
            Require(h.Wire.LastDeletedListingLimit == 2_000,
                "the deleted listing asks for the same 2000-entry page size as the main listing");
            Require(h.Wire.Calls.IndexOf("/2/files/list_folder/continue", before) >= before,
                "a multi-page deleted listing actually continues to a further page");
        }

        using (var h = new Harness())
        {
            var probe = Enumerable.Range(0, 40).Select(index => "gone-" + index + ".txt")
                .Append("never-there.txt").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in probe) if (path != "never-there.txt") h.Wire.SeedDeletedEntry(path);
            var sequential = new List<(string Path, DateTimeOffset? DeletedUtc, string Reason, bool Failed)>();
            foreach (var path in probe)
                sequential.Add(Describe(path, await h.Client.LookupDeletionEvidenceForV2Async(new[] { path },
                    CancellationToken.None)));

            h.Wire.OnRevisionAsync = token => Task.Delay(50, token);
            var batch = await h.Client.LookupDeletionEvidenceForV2Async(probe, CancellationToken.None);
            Require(h.Wire.MaxConcurrentRevisions is >= 2 and <= 16,
                "revision-history reads overlap but never exceed sixteen at once");
            var concurrent = probe.Select(path => Describe(path, batch)).ToArray();
            Require(concurrent.SequenceEqual(sequential),
                "the bounded concurrent batch equals the strictly sequential result, path for path");
        }
    }

    // The main listing now asks for deleted entries too: the live snapshot is unchanged, the collected
    // deleted-path set proves first-sync deletion times without a second whole-tree listing, and a delta run
    // with no full listing still falls back to reading one.
    private static async Task OnePassDeletionChecksAsync()
    {
        // Mixed live and deleted entries: only the live ones reach the snapshot.
        using (var h = new Harness())
        {
            h.Wire.SeedFile("a.txt", "hello");
            h.Wire.SeedFolder("sub");
            h.Wire.SeedFile("sub/b.txt", "inner");
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.SeedDeletedEntry("sub/lost.txt");
            var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(snapshot.Items.Select(item => item.Path).SequenceEqual(new[] { "a.txt", "sub", "sub/b.txt" }),
                "a page mixing live and deleted entries yields only the live snapshot");
        }

        // Live wins: a path deleted and then re-created is live and never a deletion-evidence hit.
        using (var h = new Harness())
        {
            h.Wire.SeedFile("revived.txt", "back");
            h.Wire.SeedDeletedEntry("revived.txt");
            var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(snapshot.Items.Single().Path == "revived.txt", "a re-created path appears live");
            var revived = await h.Inventory.DeletionTimesAsync(new[] { "revived.txt" }, CancellationToken.None);
            Require(revived.Count == 0, "a re-created path is not treated as deleted");
        }

        // With the set available, no second list_folder runs and only the hit reaches revision history.
        using (var h = new Harness())
        {
            h.Wire.SeedFile("a.txt", "hello");
            h.Wire.SeedDeletedEntry("gone.txt");
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var listings = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt", "never.txt" },
                CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder") == listings,
                "the deletion-evidence step issues no list_folder when the deleted set is available");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 1,
                "only the deleted hit reaches revision history");
            Require(times.Count == 1 && times["gone.txt"] == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                "the evidence still carries Dropbox's server_deleted time");
        }

        // A deletion replayed from the change cursor joins the same deleted-path set.
        using (var h = new Harness())
        {
            h.Wire.SeedFile("a.txt", "hello");
            h.Wire.SeedChangePage(h.Wire.LatestCursor,
                new[] { h.Wire.ChangeDeleted("/Pilot/ghost.txt") }, false, h.Wire.LatestCursor);
            var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(snapshot.Items.Select(item => item.Path).SequenceEqual(new[] { "a.txt" }),
                "a deletion replayed from the change cursor adds nothing to the live snapshot");
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            _ = await h.Inventory.DeletionTimesAsync(new[] { "ghost.txt" }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder") == before &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 1,
                "a path deleted by the replayed change cursor is a deletion-evidence hit");
        }

        // A deleted top-level folder is usually not listable, so its deleted children are reached by the
        // ancestor rule over the deleted set, never by a listing of the folder itself.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("Photos");
            h.Wire.SeedDeletedEntry("Photos/a.jpg");
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "Photos/a.jpg" }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder") == before &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 1,
                "a file under a deleted top-level folder is probed by its deleted ancestor, never by a folder listing");
            Require(times.Count == 1 && times["Photos/a.jpg"] == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                "the descendant's deletion time is proven from history");
        }

        // A local-only path with no deleted path or ancestor in the set is never probed.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "never/here.txt" }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == before && times.Count == 0,
                "a local-only path with no deleted ancestor is never probed");
        }

        // A replayed folder deletion records every descendant it removes as a deletion candidate.
        using (var h = new Harness())
        {
            h.Wire.SeedFolder("Docs");
            h.Wire.SeedFile("Docs/a.txt", "x");
            h.Wire.SeedFile("Docs/b.txt", "y");
            h.Wire.SeedChangePage(h.Wire.LatestCursor,
                new[] { h.Wire.ChangeDeleted("/Pilot/Docs") }, false, h.Wire.LatestCursor);
            var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(snapshot.Items.Count == 0, "a replayed folder deletion removes its live descendants");
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            _ = await h.Inventory.DeletionTimesAsync(new[] { "Docs/a.txt", "Docs/b.txt" }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder") == before &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 2,
                "a replayed folder deletion records its descendants as deletion candidates");
        }

        // A top-level folder deleted then re-created during the walk is live, so its tombstone leaves the exact
        // set; the ancestors set keeps it, so its still-deleted child is still probed.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("Photos");
            h.Wire.SeedDeletedEntry("Photos/a.jpg");
            h.Wire.SeedChangePage(h.Wire.LatestCursor,
                new[] { h.Wire.ChangeFolder("Photos") }, false, h.Wire.LatestCursor);
            var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(snapshot.Items.Single().Path == "Photos", "the re-created folder is live in the snapshot");
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "Photos/a.jpg" }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == before + 1,
                "a child of a deleted-then-re-created folder is still probed by its deleted ancestor");
            Require(times.Count == 1 && times["Photos/a.jpg"] == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                "the child's deletion time is proven from history");
        }

        // No full listing (a delta run): the evidence step falls back to its own deleted inventory.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
            Require(times.Count == 1 && h.Wire.Calls.Count(call => call == "/2/files/list_folder") == 1,
                "with no cached set the evidence fallback reads its own deleted listing");
        }

        // A delta discards stale classification and obtains exact history without relisting.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            var snapshot = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            h.Wire.SeedChangePage(snapshot.CursorAfter, Array.Empty<object>(), false, "c1");
            _ = await h.Inventory.ChangesSinceAsync(snapshot.CursorAfter, 2, _ => true, CancellationToken.None);
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var revisions = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
            Require(times.Count == 1 && h.Wire.Calls.Count(call => call == "/2/files/list_folder") == before &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions + 1,
                "a delta probes current exact revision history without a stale classification or whole-tree relist");
        }
    }

    // The deletion-evidence cache: a probed path is reused from the store, an interrupted probe run keeps what it
    // already learned, and a stored result never applies to a path the current listing no longer classes as a probe.
    private static async Task DeletionEvidenceCacheChecksAsync()
    {
        foreach (var initial in new string?[] { null, "2026-10-12T08:00:00Z" })
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("folder");
            h.Wire.SeedDeletedEntry("folder/gone.txt");
            h.Wire.RevisionDeletedTime = initial;
            if (initial is null)
                h.Wire.RevisionResponseOverride = new { is_deleted = false, entries = Array.Empty<object>() };
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            _ = await h.Inventory.DeletionTimesAsync(["folder/gone.txt"], CancellationToken.None);
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 1, "positive or negative probe persisted");
            // Validation advances the instance and checkpoint cursor even when that drain was empty.
            var evidenceCursor = h.Known.DeletionEvidenceRows("p1", RootId).Single().BasisCursor!;
            h.Wire.SeedChangePage(evidenceCursor, [h.Wire.ChangeFolder("folder"),
                h.Wire.ChangeDeleted("/Pilot/folder/gone.txt")], false, "cursor-next");
            h.Wire.LatestCursor = "cursor-next";
            h.Wire.RevisionDeletedTime = "2026-10-15T08:00:00Z";
            h.Wire.RevisionResponseOverride = null;
            // A new inventory run revalidates persisted evidence; within one run the memo is reused.
            _ = await h.Inventory.FullAsync(2, _ => true, Sink, CancellationToken.None);
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var times = await h.Inventory.DeletionTimesAsync(["folder/gone.txt"], CancellationToken.None);
            Require(times["folder/gone.txt"] == DateTimeOffset.Parse("2026-10-15T08:00:00Z") &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == before + 1,
                "changed path or ancestor invalidates both negative evidence and an earlier deletion time");
        }
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            _ = await h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None);
            _ = await h.Inventory.FullAsync(2, _ => true, Sink, CancellationToken.None);
            h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Conflict, 1);
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var lists = h.Wire.ListRequests.Count;
            var times = await h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None);
            Require(times.Count == 1 && h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == before + 1 &&
                h.Wire.ListRequests.Count == lists,
                "a reset discards cached evidence and reprobes without discarding the already successful full classification");
        }
        // A second evidence read reuses the stored results and probes nothing.
        using (var h = new Harness())
        {
            h.Wire.RevisionContentHash = new string('a', 64);
            h.Wire.SeedDeletedEntry("gone.txt");
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var first = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
            var afterFirst = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var feedsAfterFirst = h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
            var second = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
            Require(first.Count == 1 && second.Count == 1 && second["gone.txt"] == first["gone.txt"],
                "a reused deletion-evidence result matches the first probe");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == afterFirst,
                "a second evidence read reuses the cache and probes nothing");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feedsAfterFirst,
                "a second evidence read never replays this run's upload feed");
        }

        // Deferred-lane checkpoints must also become visible to a later evidence read.
        using (var h = new Harness())
        {
            h.Wire.RevisionContentHash = new string('a', 64);
            h.Wire.SeedDeletedEntry("gone.txt");
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"], (_, _) => { }, CancellationToken.None);
            var afterFirst = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var feedsAfterFirst = h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
            var second = await h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feedsAfterFirst,
                "a later evidence pass never replays the feed after deferred checkpoints");
            Require(second.Count == 1 &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == afterFirst,
                "a later evidence read reuses deferred-lane checkpoints without probing");
        }

        // Observe a committed checkpoint before a later probe fails, then reopen the database.
        using (var h = new Harness())
        {
            var folder = Path.Combine(_root, "probe-checkpoints");
            var probes = Enumerable.Range(0, 420).Select(i => $"checkpoint-{i:D4}.txt").ToArray();
            foreach (var path in probes) h.Wire.SeedDeletedEntry(path);
            var observedCheckpoint = false;
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
            {
                store.SavePair(new PairDef("checkpoint-pair", "checkpoint", Root(),
                    Root() with { RootId = "local-checkpoint" }, [], [], true));
                var calls = 0;
                var checkpointCommitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                h.Wire.OnRevisionAsync = async token =>
                {
                    if (Interlocked.Increment(ref calls) <= 200) return;
                    // Request admission is not completion: other lanes may still be finishing the first 200.
                    // Hold every later response until the checkpoint callback has committed those results.
                    await checkpointCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                    throw new EndpointAccessException("simulated interruption after checkpoint");
                };
                await ExpectAsync<EndpointAccessException>(() => h.Client.LookupDeletionEvidenceForV2Async(probes,
                    probes.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>(), null,
                    batch =>
                    {
                        store.StoreDeletionEvidence("checkpoint-pair", RootId,
                            batch.Select(row => row with { BasisCursor = "checkpoint-cursor" }).ToArray());
                        observedCheckpoint = batch.Count == 200 &&
                            store.DeletionEvidenceRows("checkpoint-pair", RootId).Count == 200;
                        checkpointCommitted.TrySetResult(true);
                    }, CancellationToken.None));
            }
            using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            Require(observedCheckpoint && reopened.DeletionEvidenceRows("checkpoint-pair", RootId).Count == 200,
                "checkpoints are committed during the batch and survive interruption and reopen");
        }
        // An interrupted probe run keeps the results it already learned; the next run probes only the rest.
        using (var h = new Harness())
        {
            h.Wire.RevisionContentHash = new string('a', 64);
            var probe = Enumerable.Range(0, 20).Select(index => "gone-" + index + ".txt").ToArray();
            foreach (var path in probe) h.Wire.SeedDeletedEntry(path);
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var calls = 0;
            var completed = 0;
            var firstFourResolved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Wire.OnRevisionAsync = async token =>
            {
                if (Interlocked.Increment(ref calls) <= 4) return;
                // Let all four successful responses reach CompleteProbe before a sibling cancels the batch.
                await firstFourResolved.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                throw new EndpointAccessException("refused");
            };
            await ExpectAsync<EndpointAccessException>(() =>
                h.Inventory.ResolveDeletionEvidenceAsync(probe, (_, _) =>
                {
                    if (Interlocked.Increment(ref completed) == 4) firstFourResolved.TrySetResult(true);
                }, CancellationToken.None));
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 4,
                "an interrupted run flushes all four completed probes as a partial checkpoint");
            h.Wire.OnRevisionAsync = null;
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var times = await h.Inventory.DeletionTimesAsync(probe, CancellationToken.None);
            var probedAgain = h.Wire.Calls.Count(call => call == "/2/files/list_revisions") - before;
            Require(times.Count == 20, "the resumed run covers every path");
            Require(probedAgain == probe.Length - 4,
                "an interrupted probe run kept its partial results; the next run probes only the rest");
        }

        // A stored "no evidence" row is only consulted for a path the current listing still classes as a probe; a
        // path that is neither a tombstone nor under one is not a probe, so its stale row is neither applied nor
        // re-probed.
        using (var h = new Harness())
        {
            _ = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            h.Known.StoreDeletionEvidence("p1", DropboxWire.RootId, [new DeletionEvidenceRecord("live.txt", null)]);
            var before = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var times = await h.Inventory.DeletionTimesAsync(new[] { "live.txt" }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == before && times.Count == 0,
                "a stored no-evidence row for a path that is not a probe is ignored");
        }
    }

    private static (string Path, DateTimeOffset? DeletedUtc, string Reason, bool Failed) Describe(
        string path, IReadOnlyDictionary<string, SyncRemoteDeletionEvidence> evidence)
    {
        var value = evidence[path];
        return (path, value.DeletedUtc, value.Reason, value.LookupFailed);
    }

    // One escaping access refusal stops the queued revision-history reads and is what the caller sees;
    // a throwing progress sink never replaces the operation's result.
    private static async Task DeletionHistoryFailureChecksAsync()
    {
        var progressCalls = 0;
        foreach (var synchronous in new[] { true, false })
        {
            using var scope = SyncByteProgressStream.Begin("pacing-check", _ =>
            {
                progressCalls++;
                var error = new InvalidOperationException("scripted progress sink failure");
                if (synchronous) throw error;
                return Task.FromException(error);
            });
            await SyncByteProgressStream.ReportPacerAsync("read");
            await SyncByteProgressStream.ReportThrottleAsync("read", TimeSpan.FromSeconds(1));
        }
        Require(progressCalls == 4, "pacer and throttle reports tolerate synchronous and asynchronous sink failures");

        foreach (var synchronous in new[] { true, false })
        {
            var processCalls = 0;
            using var scope = SyncByteProgressStream.Begin("process-check", _ =>
            {
                processCalls++;
                var error = new InvalidOperationException("scripted process sink failure");
                if (synchronous) throw error;
                return Task.FromException(error);
            });
            await SyncByteProgressStream.ReportProcessAsync("Evidence", "gone.txt", "read");
            await SyncByteProgressStream.ReportCurrentProcessAsync("Evidence", "read");
            Require(processCalls == 2, "process and ambient reports tolerate synchronous and asynchronous sink failures");
            using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
            await using (var stream = new SyncByteProgressStream(bytes, "download"))
            {
                await stream.StartedAsync();
                var buffer = new byte[3];
                Require(await stream.ReadAsync(buffer.AsMemory()) == 3 && buffer.SequenceEqual(new byte[] { 1, 2, 3 }),
                    "throwing byte progress never changes bytes returned by the underlying stream");
            }
            Require(processCalls >= 4, "stream start and byte reports actually invoked the throwing sink");
            using var h = new Harness();
            h.Wire.SeedDeletedEntry("gone.txt");
            var evidence = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
            Require(evidence.TryGetValue("gone.txt", out var deletion) &&
                deletion == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                "throwing process sink preserves deletion evidence without a forced pacer wait");
        }

        var limiter = new SyncTransferRateLimiter(new SyncTransferSettings());
        using (var h = new Harness(limiter: limiter))
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            var pacerCalls = 0;
            using (SyncByteProgressStream.Begin("gone.txt", progress =>
            {
                if (progress.Kind == ProcessMessageKind.Pacer) Interlocked.Increment(ref pacerCalls);
                throw new InvalidOperationException("progress sink failure");
            }))
            {
                limiter.Resources.ProviderThrottled(new SyncProviderBudgetKey(
                    "dropbox:" + Binding().Normalize().ConnectionId, SyncProviderOperation.Read),
                    TimeSpan.FromMilliseconds(200), accountWide: true);
                var times = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
                Require(pacerCalls > 0 && times.TryGetValue("gone.txt", out var deleted) &&
                    deleted == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                    "a throwing sink during a forced provider wait never replaces the deletion-evidence result");
            }
        }

        // Exhaust the read budget by advancing fake time inside the wire, never by sleeping. A timeout
        // must leave failed evidence pending, not resolve the path as an absent deletion timestamp.
        using (var h = new Harness())
        {
            var clock = new ResilienceChecks.Clock();
            h.Client.ReadRetryTime = clock;
            h.Client.ReadTransientBudget = TimeSpan.FromSeconds(10);
            h.Wire.SeedDeletedEntry("timeout.txt");
            h.Wire.OnRevisionAsync = _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(11));
                throw new OperationCanceledException("scripted HttpClient timeout",
                    new TimeoutException("scripted transport deadline"));
            };
            var evidence = await h.Client.LookupDeletionEvidenceForV2Async(["timeout.txt"], CancellationToken.None);
            Require(evidence["timeout.txt"].LookupFailed && evidence["timeout.txt"].DeletedUtc is null,
                "transport timeout through pacing becomes failed deletion evidence, not absence");
            var resolved = new List<string>();
            await h.Inventory.ResolveDeletionEvidenceAsync(["timeout.txt"],
                (path, _) => resolved.Add(path), CancellationToken.None);
            Require(resolved.Count == 0 && h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "timed-out deletion lookup remains pending and is not cached as resolved absence");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 2,
                "each timed-out deletion lookup spends its fake-clock budget in one actual send");
        }

        using (var h = new Harness())
        {
            var probe = Enumerable.Range(0, 40).Select(index => "gone-" + index + ".txt").ToArray();
            foreach (var path in probe) h.Wire.SeedDeletedEntry(path);
            h.Wire.Script("/2/files/list_revisions", HttpStatusCode.Unauthorized, 1);
            h.Wire.OnRevisionAsync = token => Task.Delay(2_000, token);
            await ExpectAsync<EndpointAccessException>(() =>
                h.Inventory.DeletionTimesAsync(probe, CancellationToken.None));
            var attempted = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            Require(attempted >= 1 && attempted < probe.Length,
                "one refused path cancels the queued revision-history reads instead of running every path");
        }

        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("revoked.txt");
            h.Wire.OnRevisionAsync = _ => throw new DropboxApiException(HttpStatusCode.BadRequest, "revoked")
                { OAuthError = DropboxOAuthError.InvalidGrant };
            await ExpectAsync<EndpointAccessException>(() =>
                h.Inventory.DeletionTimesAsync(["revoked.txt"], CancellationToken.None));
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "revoked authentication is not a permanent per-path history outcome");
        }

        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            using (SyncByteProgressStream.Begin("gone.txt", _ =>
                throw new InvalidOperationException("progress sink failure")))
            {
                var times = await h.Inventory.DeletionTimesAsync(new[] { "gone.txt" }, CancellationToken.None);
                Require(times.TryGetValue("gone.txt", out var deleted) &&
                    deleted == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                    "a throwing progress sink never replaces the deletion-evidence result");
            }
        }
    }

    private static void SeedStored(Harness h, params ItemState[] items)
    {
        var gen = h.Known.BeginGeneration(RootId, "snapshot");
        h.Known.WriteSnapshot(new Snapshot(RootId, Side.B, gen, "0", items, Array.Empty<string>()));
        h.Known.CompleteGeneration(gen, "0", true);
    }

    private static ItemState Stored(string path, string id, string? hash, ItemKind kind = ItemKind.File) =>
        new(path, kind, id, "v1", hash, 1, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static async Task ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private sealed class ProgressSink : IProgress<string>
    {
        public void Report(string value) { }
    }
}
