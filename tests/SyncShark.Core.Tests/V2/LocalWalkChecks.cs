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
        await ReplacedDirectoryAsync();
        await VersionedCheckpointAsync(2);
        await VersionedCheckpointAsync(3);
        CheckpointInvalidation();
        foreach (var kind in new[] { "frontier", "json", "missing-first", "missing-last", "shape", "version", "null" })
            await CorruptCheckpointAsync(kind);
        await BlockedScopeRetriedAsync();
        await OrdinaryBlockedPagesResumeAsync();
        await FlushFailurePreservesOutcomeAsync(paused: true);
        await FlushFailurePreservesOutcomeAsync(paused: false);
        await NoJournalAsync();
        await TransientCheckpointKeepsCursorAsync();
        await SameRootWaitsAsync();
        BatchAndCompactRows();
    }

    private static string Fresh(string name)
    {
        // Local runs set SYNCSHARK_TEST_ROOT to a disposable test directory. CI may supply
        // its own disposable volume. Never fall back to the working directory/Dropbox mirror.
        var configured = Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(configured)) return LocalChecks.FreshRoot("local-walk-" + name);
        var path = Path.GetFullPath(Path.Combine(configured, "local-walk-" + name, Guid.NewGuid().ToString("N"), "root"));
        if (path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local walk fixtures must not run under the Dropbox mirror.");
        Directory.CreateDirectory(path);
        return path;
    }

    private static LocalInventory Inventory(string root, IStateStore store, NtfsUsnCheckpoint checkpoint)
    {
        var inventory = LocalChecks.Inventory(root, store, TimeProvider.System);
        inventory.WalkCheckpointForTest = () => (checkpoint, false);
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

    private static async Task ReplacedDirectoryAsync()
    {
        var root = Fresh("replaced");
        LocalChecks.Seed(root, "d/old.txt", "original");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var first = await Inventory(root, sql, Start).FullAsync(sql.BeginGeneration("root-local", "full"), _ => true, null!, default);
        sql.WriteSnapshot(first);
        var old = sql.Live("root-local").Single(i => i.Path == "d/old.txt");
        var remote = old with { ObjectId = "remote-old" };
        var inventory = Inventory(root, sql, Later);
        inventory.DirectoryCompletedForTest = path =>
        {
            if (path != root) return;
            Directory.Move(Path.Combine(root, "d"), root + "-moved");
            LocalChecks.Seed(root, "d/new.txt", "replacement");
        };
        var snapshot = await inventory.FullAsync(sql.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(snapshot.IncompleteSubtrees.Contains("d") && snapshot.IncompleteReasons["d"] == "replaced during scan" &&
            !snapshot.Items.Any(i => i.Path == "d/new.txt"), "replacement is never enumerated under the queued identity");
        sql.WriteSnapshot(snapshot);
        LocalChecks.Require(sql.Live("root-local").Any(i => i.ObjectId == old.ObjectId), "incomplete coverage retains old child in SQLite");
        var rows = Planner.Join([new Baseline("pair", "child", old, remote, 1)], sql.Live("root-local"), [remote],
            new HashSet<string>(), new HashSet<string>(), new Completeness(true, true,
                snapshot.IncompleteSubtrees.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>()), false);
        var plan = Planner.Decide("pair", 2, rows, new PlanOptions(TimeSpan.Zero, false,
            new LabelRule("{name} [{side}]{ext}", "yyyyMMdd", TimeSpan.Zero, "A", "B"), 10));
        LocalChecks.Require(!plan.Actions.Any(a => a.Kind == ActionKind.Delete), "moved intact directory grants zero child deletes");
        // The identity-probe contract is unchanged: genuine absence may still be proven,
        // but incomplete coverage alone cannot close the old child.
        LocalChecks.Require((snapshot with { VerifyAbsence = _ => FileAbsence.Gone }).CanCloseAbsent(old),
            "genuine identity absence remains admissible after a partial scan");
        var next = await Inventory(root, sql, Later).FullAsync(sql.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(next.IncompleteSubtrees.Count == 0 && next.Items.Any(i => i.Path == "d/new.txt"),
            "next matching-identity walk inventories replacement content");
    }

    private static async Task VersionedCheckpointAsync(int version)
    {
        var root = Fresh("version-" + version);
        LocalChecks.Seed(root, "d/old.txt", "old");
        using (var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = Inventory(root, sql, Start);
            inventory.DirectoryCompletedForTest = _ => throw new SimulatedCrash();
            try { await inventory.FullAsync(1, _ => true, null!, default); }
            catch (SimulatedCrash) { }
            var page = JsonNode.Parse(sql.LocalWalkPage("root-local", 0)!)!;
            LocalChecks.Require(page["Version"]!.GetValue<int>() == 3 && page["DirectoryIdentity"] is not null &&
                page["AddedDirectoryIdentities"]![0] is not null, "all new checkpoints carry v3 frontier identities");
            if (version == 2)
            {
                page["Version"] = 2;
                page.AsObject().Remove("DirectoryIdentity");
                page.AsObject().Remove("AddedDirectoryIdentities");
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root + "-state", "v2", "state.db")};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE local_walk_page SET payload=$p WHERE ordinal=0";
                command.Parameters.AddWithValue("$p", page.ToJsonString());
                command.ExecuteNonQuery();
            }
        }
        Directory.Move(Path.Combine(root, "d"), root + "-moved");
        LocalChecks.Seed(root, "d/new.txt", "new");
        using var reopened = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var snapshot = await Inventory(root, reopened, Later).FullAsync(2, _ => true, null!, default);
        LocalChecks.Require(snapshot.CursorAfter == Start.Cursor, "both versions retain completed pages and original cursor");
        LocalChecks.Require(version == 2
            ? snapshot.Items.Any(i => i.Path == "d/new.txt") && snapshot.IncompleteSubtrees.Count == 0
            : !snapshot.Items.Any(i => i.Path == "d/new.txt") && snapshot.IncompleteReasons["d"] == "replaced during scan",
            "v2 retains its one-walk residual; v3 rejects a replaced frontier after cold reopen");
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
        inventory.WalkCheckpointForTest = () => (null, false);
        inventory.DirectoryCompletedForTest = _ =>
            LocalChecks.Require(!sql.HasLocalWalk("root-local"), "no journal means no checkpoint overhead");
        var snapshot = await inventory.FullAsync(1, _ => true, null!, CancellationToken.None);
        LocalChecks.Require(snapshot.Items.Count == 2 && sql.LocalWalkPageCount("root-local") == 0,
            "non-journal volumes still complete normal full inventories");
    }

    private static async Task TransientCheckpointKeepsCursorAsync()
    {
        var root = Fresh("transient-checkpoint");
        LocalChecks.Seed(root, "file.txt", "payload");
        using var sql = SqliteStateStore.Open(root + "-state", StorageChecks.Fast, TimeProvider.System);
        var gen = sql.BeginGeneration("root-local", "full");
        sql.CompleteGeneration(gen, Start.Cursor, true);
        var inventory = Inventory(root, sql, Start);
        inventory.WalkCheckpointForTest = () => (null, false); // unreadable, not a deleted journal
        var snapshot = await inventory.FullAsync(sql.BeginGeneration("root-local", "full"), _ => true, null!, default);
        LocalChecks.Require(snapshot.CursorAfter == Start.Cursor && ((ILocalGapStore)sql).GapEpoch("root-local") is null,
            "transient full-walk checkpoint failure retains saved cursor without starting an untrusted gap");
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
