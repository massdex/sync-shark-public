using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task ResumeListingChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        await ListingNetworkOutageAsync(Path.Combine(root, "network-recovery"), false);
        await ListingNetworkOutageAsync(Path.Combine(root, "network-exhausted"), true);
        await FullResumeAsync(Path.Combine(root, "full"), false);
        await FullResumeAsync(Path.Combine(root, "expired"), true);
        await CursorResetFullResumeAsync(Path.Combine(root, "reset-full"));
        await ChangedSubtreeResumeAsync(Path.Combine(root, "changed-subtree"));
        await DeltaResumeAsync(Path.Combine(root, "delta"));
        await DeltaNetworkOutageAsync(Path.Combine(root, "delta-outage"));
        await DeltaOutageRunRetiresDebtAsync(Path.Combine(root, "run-delta-outage"));
        await HealthyEditDuringOutageSyncsAfterRecoveryAsync(Path.Combine(root, "run-healthy-edit"));
        await AdversarialDeleteDuringDeltaOutageAsync(Path.Combine(root, "run-delta-delete"));
        await MembershipFailureKeepsItemRetryAsync(Path.Combine(root, "run-membership"));
        await MillionRowCheckpointAsync(Path.Combine(root, "million"));
        Console.WriteLine("resumelist: full resume, expired cursor, delta resume, partial isolation, pause persistence and double reopen passed");
    }

    private static async Task MillionRowCheckpointAsync(string state)
    {
        var baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        const int pages = 500, pageSize = 2000;
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            var checkpoint = new ListingCheckpoint(sql, Root(), "p1", "scale");
            for (var page = 0; page < pages; page++)
            {
                var start = page * pageSize;
                using var response = await checkpoint.PageAsync("scale", new { page }, () =>
                    Task.FromResult(System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        cursor = "cursor-" + page,
                        entries = Enumerable.Range(start, pageSize).Select(i => new { path = "file-" + i, rev = "revision", size = 1024 })
                    }))));
                if (page % 100 == 0 || page == pages - 1) peak = Math.Max(peak, GC.GetTotalMemory(true));
            }
        }
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            var checkpoint = new ListingCheckpoint(sql, Root(), "p1", "scale");
            var rows = 0;
            for (var page = 0; page < pages; page++)
            {
                using var response = await checkpoint.PageAsync("scale", new { page }, () =>
                    throw new InvalidOperationException("A checkpointed scale page must not be fetched again"));
                rows += response.RootElement.GetProperty("entries").GetArrayLength();
                if (page % 100 == 0 || page == pages - 1) peak = Math.Max(peak, GC.GetTotalMemory(true));
            }
            Require(rows == 1_000_000, "all one million checkpointed rows replay exactly once");
            checkpoint.Complete();
        }
        Require(peak - baseline < 64L * 1024 * 1024,
            "one million journal rows retain less than 64 MiB of additional managed memory during write and replay");
        Console.WriteLine($"1M checkpoint rows: sampled retained managed-memory growth {Math.Max(0, peak - baseline) / 1024 / 1024} MiB; page size {pageSize}, lanes unchanged.");
    }

    private sealed class ListingProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    private static async Task FullResumeAsync(string state, bool expire)
    {
        using var h = new Harness();
        h.Wire.DeletedPageSize = 2;
        for (var i = 0; i < 7; i++) h.Wire.SeedFile($"file-{i}.txt", "data");
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SavePair(new PairDef("p1", "Listing fixture", new RootDef("ra", "Local", null, state, "local"), Root(), [], [], true));
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            var progress = new ListingProgress(text =>
            {
                if (text.Contains("Read Dropbox folder listing page", StringComparison.Ordinal)) sql.SetPairPaused("p1", true);
            });
            try
            {
                await inventory.FullAsync(1, _ => true, progress, CancellationToken.None);
                throw new InvalidOperationException("Expected a checkpoint-safe pause");
            }
            catch (PairPausedException) { }
            Require(sql.Live(RootId).Count == 0 && sql.ReadInventoryObservation(Root()) is null,
                "partial pages are neither a complete inventory nor a complete observation");
        }
        // Open twice at the same schema version. Both the checkpoint and the pause survive.
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
            Require(sql.PairPaused("p1"), "pause survives reopening");
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SetPairPaused("p1", false);
            if (expire) h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Conflict, 1);
            sql.SavePair(new PairDef("p1", "Listing fixture", new RootDef("ra", "Local", null, state, "local"), Root(), [], [], true));
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            var snapshot = await inventory.FullAsync(2, _ => true, Sink, CancellationToken.None);
            Require(snapshot.IncompleteSubtrees.Count == 0 && snapshot.Items.Count == 7 &&
                snapshot.Items.Select(row => row.Path).Distinct().Count() == 7,
                "resumed listing has every row exactly once");
            Require(h.Wire.ListRequests.Count == (expire ? 2 : 1),
                "resume continues from the saved cursor; only expiry restarts listing");
        }
    }

    private static async Task CursorResetFullResumeAsync(string state)
    {
        using var h = new Harness();
        h.Wire.DeletedPageSize = 2;
        for (var i = 0; i < 7; i++) h.Wire.SeedFile($"file-{i}.txt", "data");
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SavePair(new PairDef("p1", "Reset fixture", new RootDef("ra", "Local", null, state, "local"), Root(), [], [], true));
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Conflict, 1);
            Require((await inventory.ChangesSinceAsync("bad-cursor", 1, _ => true, CancellationToken.None)).CursorInvalid,
                "reset delta requests a full listing");
            var progress = new ListingProgress(text =>
            {
                if (text.Contains("Read Dropbox folder listing page 2", StringComparison.Ordinal))
                    sql.SetPairPaused("p1", true);
            });
            try
            {
                await inventory.FullAsync(2, _ => true, progress, CancellationToken.None);
                throw new InvalidOperationException("Expected full-listing pause after page two");
            }
            catch (PairPausedException) { }
        }
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        reopened.SetPairPaused("p1", false);
        var resumed = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
        Require((await resumed.ChangesSinceAsync("bad-cursor", 3, _ => true, CancellationToken.None)).CursorInvalid,
            "unfinished full listing bypasses delta and selects full fallback");
        var snapshot = await resumed.FullAsync(3, _ => true, Sink, CancellationToken.None);
        Require(snapshot.Items.Count == 7 && snapshot.IncompleteSubtrees.Count == 0, "reset full listing resumes completely");
        Require(h.Wire.ListRequests.Count == 1 && h.Wire.ContinueCursors.Count(c => c == "bad-cursor") == 1 &&
            h.Wire.ContinueCursors.Count(c => c == "deleted-page-1") == 1,
            "neither the bad delta nor full pages one and two are fetched twice");
    }

    private static async Task ChangedSubtreeResumeAsync(string state)
    {
        using var h = new Harness();
        h.Wire.DeletedPageSize = 2;
        h.Wire.SeedFolder("tree");
        h.Wire.SeedFile("stay.txt", "old");
        h.Wire.SeedFolder("tree/gone");
        h.Wire.SeedFile("tree/gone/child.txt", "old");
        h.Wire.SeedFile("tree/moved.txt", "old");
        h.Wire.SeedFile("tree/removed.txt", "old");
        h.Wire.SeedFile("tree/unchanged.txt", "same");
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            var checkpoint = new ListingCheckpoint(sql, Root(), "p1", "full");
            try
            {
                await h.Client.ListEntriesWithDeletedPathsAsync(progress =>
                {
                    if (progress.Detail.Contains("Read Dropbox folder listing page 2", StringComparison.Ordinal))
                        sql.SetPairPaused("p1", true);
                    return Task.CompletedTask;
                }, CancellationToken.None, checkpoint: checkpoint);
                throw new InvalidOperationException("Expected subtree pause");
            }
            catch (PairPausedException) { }
        }
        // The change feed describes changes after the saved basis, including a recreated ancestor.
        h.Wire.SeedChangePage(h.Wire.LatestCursor, [
            h.Wire.ChangeDeleted(h.Wire.RootPath + "/tree/gone"),
            h.Wire.ChangeFolder("tree/gone"),
            h.Wire.ChangeFile("tree/gone/new.txt", "new"),
            h.Wire.ChangeDeleted(h.Wire.RootPath + "/tree/moved.txt"),
            h.Wire.ChangeFile("moved.txt", "old"),
            h.Wire.ChangeDeleted(h.Wire.RootPath + "/tree/removed.txt"),
            h.Wire.ChangeFile("stay.txt", "updated")], false, "after-pause");
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        reopened.SetPairPaused("p1", false);
        var saved = new ListingCheckpoint(reopened, Root(), "p1", "full");
        var result = await h.Client.ListEntriesWithDeletedPathsAsync(null, CancellationToken.None, checkpoint: saved);
        Require(result.Entries.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal).SetEquals(
            ["tree", "tree/gone", "tree/gone/new.txt", "tree/unchanged.txt", "moved.txt", "stay.txt"]),
            "replay applies file edits, moves and subtree deletion/recreation during pause");
        Require(result.Entries.Single(e => e.RelativePath == "stay.txt").Size == 7,
            "post-basis file revision replaces the saved page value");
        Require(result.DeletedPaths.SetEquals(["tree/gone/child.txt", "tree/moved.txt", "tree/removed.txt"]) &&
            result.DeletedAncestors.SetEquals(["tree/gone", "tree/gone/child.txt", "tree/moved.txt", "tree/removed.txt"]),
            "exact deletion paths exclude recreated folders while deletion ancestors retain their tombstones");
        Require(h.Wire.ListRequests.Count == 2, "root and subtree initial pages were not refetched");
        saved.Complete();
    }

    private static async Task DeltaResumeAsync(string state)
    {
        using var h = new Harness();
        h.Wire.SeedChangePage("delta-start", [h.Wire.ChangeFile("first.txt", "first")], true, "delta-middle");
        h.Wire.SeedChangePage("delta-middle", [h.Wire.ChangeFile("second.txt", "second")], false, "delta-end");
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            h.Wire.AfterContinueResponse = cursor => { if (cursor == "delta-start") sql.SetPairPaused("p1", true); };
            sql.SavePair(new PairDef("p1", "Listing fixture", new RootDef("ra", "Local", null, state, "local"), Root(), [], [], true));
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            try
            {
                await inventory.ChangesSinceAsync("delta-start", 1, _ => true, CancellationToken.None);
                throw new InvalidOperationException("Expected delta pause");
            }
            catch (PairPausedException) { }
            Require(sql.Live(RootId).Count == 0, "paused delta did not publish partial inventory");
        }
        h.Wire.AfterContinueResponse = null;
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        reopened.SetPairPaused("p1", false);
        var resumed = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
        var delta = await resumed.ChangesSinceAsync("delta-start", 2, _ => true, CancellationToken.None);
        Require(!delta.CursorInvalid && delta.CursorAfter == "delta-end" && delta.Changes.Count == 2,
            "delta resumed without missing or duplicate rows");
        Require(h.Wire.ContinueCursors.Count(cursor => cursor == "delta-start") == 1,
            "already checkpointed delta page was not fetched again");
    }
}
