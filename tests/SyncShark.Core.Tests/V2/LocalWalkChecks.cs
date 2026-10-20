using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

/// <summary>B-194: real directory walks, SQLite reopen, and a deterministic fake journal boundary.</summary>
internal static class LocalWalkChecks
{
    private static readonly NtfsUsnCheckpoint Start = new(1, 0, 100, 0);
    private static readonly NtfsUsnCheckpoint Later = new(1, 0, 200, 0);
    private sealed class SimulatedCrash : Exception { }

    internal static async Task RunAsync()
    {
        await PauseAndChangesAsync();
        await CrashInsideDirectoryAsync();
        await ChangedSelectionAsync();
        await IncompleteDirectoryAsync();
        CheckpointInvalidation();
        foreach (var kind in new[] { "frontier", "json", "missing-first", "missing-last", "shape", "version", "null" })
            await CorruptCheckpointAsync(kind);
        await BlockedScopeRetriedAsync();
        await OrdinaryBlockedPagesResumeAsync();
        await FlushFailurePreservesOutcomeAsync(paused: true);
        await FlushFailurePreservesOutcomeAsync(paused: false);
        await NoJournalAsync();
        await SameRootWaitsAsync();
        BatchAndCompactRows();
    }

    private static string Fresh(string name)
    {
        // Set SYNCSHARK_TEST_ROOT to a disposable test directory.
        // Never fall back to the working directory or a synced mirror.
        var configured = Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(configured)) return LocalChecks.FreshRoot("local-walk-" + name);
        var path = Path.GetFullPath(Path.Combine(configured, "local-walk-" + name, Guid.NewGuid().ToString("N"), "root"));
        var mirror = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox");
        if (path.StartsWith(mirror, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local walk fixtures must not run under the Dropbox mirror.");
        Directory.CreateDirectory(path);
        return path;
    }

    private static LocalInventory Inventory(string root, IStateStore store, NtfsUsnCheckpoint checkpoint)
    {
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => checkpoint;
        return inventory;
    }

    private static async Task PauseAndChangesAsync()
    {
        var root = Fresh("pause");
        LocalChecks.Seed(root, "old.txt", "before");
        LocalChecks.Seed(root, "d/inside.txt", "nested");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = path =>
        {
            if (path == root) sql.SetPairPaused("p", true);
        };
        using (PairPause.Begin(sql, "p"))
        {
            try
            {
                await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
                throw new InvalidOperationException("Expected a pause between directories");
            }
            catch (PairPausedException) { }
        }
        LocalChecks.Require(sql.HasLocalWalk("root-local") && sql.LocalWalkPage("root-local", 0) is not null &&
            sql.LocalWalkPage("root-local", 1) is null, "pause durably commits exactly the finished directory");
        LocalChecks.Require(sql.Live("root-local").Count == 0 && sql.Cursor("root-local") is null,
            "partial walk publishes neither rows nor a completed cursor");
        var precedence = await inventory.ChangesSinceAsync(Start.Cursor, 2, _ => true, CancellationToken.None);
        LocalChecks.Require(precedence.CursorInvalid, "unfinished full walk takes precedence over an older delta cursor");

        File.Delete(Path.Combine(root, "old.txt")); // disposable fixture only
        LocalChecks.Seed(root, "new.txt", "during pause");
        sql.SetPairPaused("p", false);
        inventory = Inventory(root, sql, Later);
        var enumerated = new List<string>();
        inventory.DirectoryCompletedForTest = enumerated.Add;
        var gen = sql.BeginGeneration("root-local", "full");
        var snapshot = await inventory.FullAsync(gen, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.CursorAfter == Start.Cursor && enumerated.SequenceEqual([Path.Combine(root, "d")]),
            "resume enumerates only the frontier and retains the ORIGINAL pre-walk USN cursor");
        LocalChecks.Require(snapshot.Items.Count == 3 && snapshot.Items.Select(row => row.Path).Distinct().Count() == 3 &&
            snapshot.IncompleteSubtrees.Count == 0, "resumed walk has no duplicated or missing original observations");
        sql.WriteSnapshot(snapshot);
        // Fake the journal's resolved paths since Start, exercising the real guarded refresh/delete machinery.
        var delta = await inventory.ChangesForPathsAsync(["old.txt", "new.txt"], snapshot.CursorAfter!, 4,
            _ => true, CancellationToken.None);
        LocalChecks.Require(delta.Changes.Any(change => change.Kind == ChangeKind.Delete && change.Path == "old.txt") &&
            delta.Changes.Any(change => change.Kind == ChangeKind.Upsert && change.Path == "new.txt"),
            "the subsequent delta catches both creation and deletion inside an already checkpointed directory");
        LocalChecks.Require(!sql.HasLocalWalk("root-local"), "successful completion releases the partial checkpoint");
    }

    private static async Task CrashInsideDirectoryAsync()
    {
        var root = Fresh("crash");
        LocalChecks.Seed(root, "top.txt", "root");
        LocalChecks.Seed(root, "d/a.txt", "a");
        LocalChecks.Seed(root, "d/b.txt", "b");
        using (var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = Inventory(root, sql, Start);
            try
            {
                await inventory.FullAsync(1, path => path == "d/b.txt" ? throw new SimulatedCrash() : true,
                    null!, CancellationToken.None);
                throw new InvalidOperationException("Expected interrupted directory");
            }
            catch (SimulatedCrash) { }
            LocalChecks.Require(sql.LocalWalkPage("root-local", 0) is not null &&
                sql.LocalWalkPage("root-local", 1) is null && sql.Live("root-local").Count == 0,
                "a crash inside a directory cannot commit its partial rows or advance its frontier");
        }
        // New connection + scanner: no in-process state survives this simulated abrupt interruption.
        using var reopened = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var resumed = Inventory(root, reopened, Later);
        var walked = new List<string>();
        resumed.DirectoryCompletedForTest = walked.Add;
        var snapshot = await resumed.FullAsync(2, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.CursorAfter == Start.Cursor && snapshot.Items.Count == 4 &&
            snapshot.Items.Select(item => item.Path).Distinct().Count() == 4 &&
            walked.SequenceEqual([Path.Combine(root, "d")]),
            "reopened store replays completed rows, retries only the interrupted directory, and has no duplicates");
    }

    private static async Task ChangedSelectionAsync()
    {
        var root = Fresh("selection");
        LocalChecks.Seed(root, "z/file.txt", "previously excluded");
        LocalChecks.Seed(root, "a/file.txt", "included");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = _ => throw new SimulatedCrash();
        try { await inventory.FullAsync(1, path => !path.StartsWith("z", StringComparison.Ordinal), null!, CancellationToken.None); }
        catch (SimulatedCrash) { }
        inventory = Inventory(root, sql, Later);
        var snapshot = await inventory.FullAsync(2, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Any(row => row.Path == "z/file.txt") && snapshot.CursorAfter == Later.Cursor,
            "widened selection invalidates pruned checkpoint coverage instead of silently missing a subtree");
    }

    private static async Task IncompleteDirectoryAsync()
    {
        var root = Fresh("incomplete");
        LocalChecks.Seed(root, "d/file.txt", "content");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = _ => throw new SimulatedCrash();
        try { await inventory.FullAsync(1, _ => true, null!, CancellationToken.None); }
        catch (SimulatedCrash) { }
        Directory.Move(Path.Combine(root, "d"), root + "-moved");
        inventory = Inventory(root, sql, Later);
        var snapshot = await inventory.FullAsync(2, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.IncompleteSubtrees.Contains("d"),
            "a missing pending directory is incomplete, never absence/deletion authority");
    }

    private static async Task CorruptCheckpointAsync(string kind)
    {
        var root = Fresh("corrupt-" + kind);
        LocalChecks.Seed(root, "d/file.txt", "payload");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = path =>
        {
            if (path != root) throw new SimulatedCrash();
        };
        try { await inventory.FullAsync(1, _ => true, null!, CancellationToken.None); }
        catch (SimulatedCrash) { }
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(root + "-state", "v2", "state.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            if (kind.StartsWith("missing", StringComparison.Ordinal))
                command.CommandText = "DELETE FROM local_walk_page WHERE ordinal=" + (kind == "missing-first" ? "0" : "1");
            else
            {
                var page = JsonNode.Parse(sql.LocalWalkPage("root-local", 0)!)!;
                if (kind == "frontier") page["Directory"] = "wrong";
                if (kind == "shape") page["Entries"] = null;
                if (kind == "version") page["Version"] = 99;
                command.CommandText = "UPDATE local_walk_page SET payload=$p WHERE ordinal=0";
                command.Parameters.AddWithValue("$p", kind == "json" ? "{" : kind == "null" ? "null" : page.ToJsonString());
            }
            command.ExecuteNonQuery();
        }
        inventory = Inventory(root, sql, Later);
        var walked = new List<string>();
        inventory.DirectoryCompletedForTest = walked.Add;
        var snapshot = await inventory.FullAsync(2, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.CursorAfter == Later.Cursor && snapshot.Items.Count == 2 &&
            snapshot.IncompleteSubtrees.Count == 0 && walked.Count(p => p == root) == 1 && !sql.HasLocalWalk("root-local"),
            "corrupt " + kind + " checkpoint is discarded and exactly one fresh walk completes");
    }

    private static async Task BlockedScopeRetriedAsync()
    {
        var root = Fresh("blocked-retry");
        LocalChecks.Seed(root, "d/file.txt", "payload");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = path =>
        {
            if (path == root) Directory.Move(Path.Combine(root, "d"), root + "-moved");
            else throw new SimulatedCrash();
        };
        try { await inventory.FullAsync(1, _ => true, null!, CancellationToken.None); }
        catch (SimulatedCrash) { }
        Directory.Move(root + "-moved", Path.Combine(root, "d"));
        var snapshot = await Inventory(root, sql, Later).FullAsync(2, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.CursorAfter == Start.Cursor && snapshot.IncompleteSubtrees.Contains("d") &&
            !snapshot.Items.Any(i => i.Path == "d/file.txt"),
            "resumed blocked directory stays incomplete without restarting or gaining deletion authority");
        var fresh = await Inventory(root, sql, Later).FullAsync(3, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(fresh.IncompleteSubtrees.Count == 0 && fresh.Items.Any(i => i.Path == "d/file.txt"),
            "the next full walk retries the previously blocked directory");
    }

    private static async Task OrdinaryBlockedPagesResumeAsync()
    {
        var root = Fresh("ordinary-blocks");
        LocalChecks.Seed(root, "d/file.txt", "payload");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = _ => throw new SimulatedCrash();
        try { await inventory.FullAsync(1, _ => true, null!, CancellationToken.None); }
        catch (SimulatedCrash) { }
        // Persist ordinary scanner decisions deterministically, without depending on junction privileges
        // or a particular antivirus/file-sharing implementation on the CI host.
        var page = JsonNode.Parse(sql.LocalWalkPage("root-local", 0)!)!;
        page["Decisions"] = System.Text.Json.JsonSerializer.SerializeToNode(new[]
        {
            new PilotDecision("junction", PilotDecisionKind.Blocked, "Reparse point is not followed."),
            new PilotDecision("locked.txt", PilotDecisionKind.Blocked, "File is in use.")
        });
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(root + "-state", "v2", "state.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE local_walk_page SET payload=$p WHERE root_id='root-local' AND ordinal=0";
            command.Parameters.AddWithValue("$p", page.ToJsonString());
            command.ExecuteNonQuery();
        }
        inventory = Inventory(root, sql, Later);
        var walked = new List<string>();
        inventory.DirectoryCompletedForTest = walked.Add;
        var snapshot = await inventory.FullAsync(2, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.CursorAfter == Start.Cursor && walked.SequenceEqual([Path.Combine(root, "d")]) &&
            snapshot.Items.Any(i => i.Path == "d/file.txt"),
            "ordinary junction and file-in-use decisions replay without restarting the walk");
        LocalChecks.Require(snapshot.IncompleteSubtrees.Contains("junction") && snapshot.IncompleteSubtrees.Contains("locked.txt"),
            "replayed ordinary blocks never acquire deletion authority");
    }

    private static async Task FlushFailurePreservesOutcomeAsync(bool paused)
    {
        var root = Fresh("flush-" + paused);
        LocalChecks.Seed(root, "d/file.txt", "payload");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root + "-state", "v2", "state.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_walk_flush BEFORE INSERT ON local_walk_page BEGIN SELECT RAISE(ABORT, 'fixture flush failure'); END";
        command.ExecuteNonQuery();
        var original = new SimulatedCrash();
        var inventory = Inventory(root, sql, Start);
        inventory.DirectoryCompletedForTest = _ =>
        {
            if (paused) sql.SetPairPaused("p", true);
            else throw original;
        };
        using (PairPause.Begin(sql, "p"))
        {
            try
            {
                await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
                throw new InvalidOperationException("Expected original pause or scan failure");
            }
            catch (PairPausedException) when (paused) { }
            catch (SimulatedCrash error) when (!paused && ReferenceEquals(error, original)) { }
        }
        LocalChecks.Require(sql.LocalWalkPageCount("root-local") == 0 && sql.Live("root-local").Count == 0,
            "a failed best-effort flush publishes no pages or snapshot and preserves the original outcome");
    }

    private static async Task NoJournalAsync()
    {
        var root = Fresh("no-journal");
        LocalChecks.Seed(root, "d/file.txt", "payload");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var inventory = Inventory(root, sql, Start);
        inventory.WalkCheckpointForTest = () => null;
        inventory.DirectoryCompletedForTest = _ =>
            LocalChecks.Require(!sql.HasLocalWalk("root-local"), "no journal means no checkpoint overhead");
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Count == 2 && sql.LocalWalkPageCount("root-local") == 0,
            "non-journal volumes still complete normal full inventories");
    }

    private static async Task SameRootWaitsAsync()
    {
        var root = Fresh("admission");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var gate = sql.LocalWalkGate("root-local");
        await gate.WaitAsync();
        var waiting = Inventory(root, sql, Start).FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(!waiting.IsCompleted && !sql.HasLocalWalk("root-local"),
            "another walk of the same root waits before touching the journal");
        gate.Release();
        await waiting;
    }

    private static void BatchAndCompactRows()
    {
        var root = Fresh("batch");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        sql.BeginLocalWalk(LocalChecks.RootOf(root, "root-local"), "options", Start);
        var walk = new LocalWalkCheckpoint(sql, "root-local", root);
        var entry = new PilotLocalEntry("file.txt", PilotEntryKind.File, Path.Combine(root, "file.txt"), 1,
            DateTime.UtcNow, null);
        var page = new LocalWalkPage(root, [], [entry], [], [new("file.txt", true, true)], 1, 1);
        for (var i = 0; i < 255; i++) walk.Save(page);
        LocalChecks.Require(sql.LocalWalkPageCount("root-local") == 0, "small directory pages are buffered");
        walk.Save(page);
        LocalChecks.Require(sql.LocalWalkPageCount("root-local") == 256, "256 directories flush as one batch");
        var payload = sql.LocalWalkPage("root-local", 0)!;
        LocalChecks.Require(!payload.Contains("FullPath") && !payload.Contains("ContentHash") &&
            !payload.Contains("CloudRevision") && JsonNode.Parse(payload)!["Selections"]!.AsArray().Count == 0,
            "saved rows omit repeated absolute paths, empty fields and ordinary selections");
        var replay = new LocalWalkCheckpoint(sql, "root-local", root).Read()!;
        LocalChecks.Require(replay.Entries.Single().FullPath == entry.FullPath, "replay restores absolute paths");
        walk.Save(page with { Found = 5000 });
        LocalChecks.Require(sql.LocalWalkPageCount("root-local") == 257, "5000 entries flush a batch");
        walk.Save(page);
        walk.Complete();
        LocalChecks.Require(sql.LocalWalkPageCount("root-local") == 258, "walk completion flushes its tail");
    }

    private static void CheckpointInvalidation()
    {
        var root = Fresh("binding");
        var def = LocalChecks.RootOf(root, "root-local");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        LocalChecks.Require(sql.BeginLocalWalk(def, "options", Start) == Start.Cursor, "fresh start cursor");
        sql.SaveLocalWalkPage(def.RootId, 0, "sentinel");
        LocalChecks.Require(sql.BeginLocalWalk(def, "options", Later) == Start.Cursor &&
            sql.LocalWalkPage(def.RootId, 0) == "sentinel", "valid journal retains checkpoint");
        var gap = Later with { FirstUsn = 150 };
        LocalChecks.Require(sql.BeginLocalWalk(def, "options", gap) == gap.Cursor &&
            sql.LocalWalkPage(def.RootId, 0) is null, "journal gap requires a fresh walk");
        sql.SaveLocalWalkPage(def.RootId, 0, "sentinel");
        sql.BeginLocalWalk(def with { RootObjectId = "replacement" }, "options", Later);
        LocalChecks.Require(sql.LocalWalkPage(def.RootId, 0) is null, "replacement root cannot inherit old observations");
    }
}
