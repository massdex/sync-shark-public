using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void SnapshotReviewFollowups(string root)
    {
        SnapshotPartialReplacement(root);
        SnapshotInitialDebtAndTombstone(root);
        SnapshotPartialMove(root);
        SnapshotConcurrentFallback(root);
        SnapshotProfileTrace(root);
    }

    private static void SnapshotProfileTrace(string root)
    {
        using var fixture = new SnapshotFixture(Fresh(root, "snapshot-profile-trace"));
        var previous = Environment.GetEnvironmentVariable("SYNCSHARK_SNAPSHOT_PROFILE");
        var stderr = Console.Error;
        using var capturedError = new StringWriter();
        using var capturedTrace = new StringWriter();
        using var listener = new System.Diagnostics.TextWriterTraceListener(capturedTrace);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            Environment.SetEnvironmentVariable("SYNCSHARK_SNAPSHOT_PROFILE", "1");
            Console.SetError(capturedError);
            fixture.Write([F("file", "id", "aa")]);
            listener.Flush();
            Require(capturedError.ToString().Length == 0 && capturedTrace.ToString().Contains("snapshot-phase"),
                "profiling goes to Trace, never stderr");
        }
        finally
        {
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable("SYNCSHARK_SNAPSHOT_PROFILE", previous);
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }
    }

    private static void SnapshotConcurrentFallback(string root)
    {
        using var fixture = new SnapshotFixture(Fresh(root, "snapshot-concurrent-fallback"));
        ItemState[] items = [F("file", "id", "aa")];
        fixture.Write(items);
        var calls = 0;
        fixture._writer.AfterSnapshotCompared = () =>
        {
            using var other = new SqliteConnection(fixture.Connection.ConnectionString);
            other.Open();
            StoreSchema.Execute(other, null, "UPDATE file_state SET size=$s;", ("$s", ++calls + 100));
        };
        fixture.Write(items);
        Require(calls == 2 && fixture.Reader.Live(1, null).Single().Size == items[0].Size,
            "two concurrent mismatches finish with a fresh write-locked comparison");
    }

    private static void SnapshotPartialReplacement(string root)
    {
        foreach (var status in new[] { FileAbsence.Present, FileAbsence.Unknown, FileAbsence.Gone })
        foreach (var delta in new[] { false, true })
        foreach (var caseRename in new[] { false, true })
        {
            using var fixture = new SnapshotFixture(Fresh(root, $"partial-replace-{status}-{delta}-{caseRename}"));
            var old = F("file", "old", "aa");
            var next = F(caseRename ? "FILE" : "file", "new", "bb");
            fixture.Write([old]);
            StoreSchema.Execute(fixture.Connection, null, "INSERT INTO baseline_fixture VALUES('file','old');");
            var snapshot = new Snapshot("root", Side.A, 1, "c", [next], ["blocked"])
                { VerifyAbsence = _ => status };
            if (delta)
                fixture._writer.WriteDelta(new Delta("root", Side.A, 1, "before", "after",
                    [new Change(ChangeKind.Upsert, next.ObjectId, next.Path, next, null)], false, ["blocked"])
                    { DisplacementEvidence = snapshot, PendingDeletionEvidence = snapshot.PendingDeletionEvidence }, 1);
            else fixture._writer.WriteSnapshot(snapshot, 1);
            Require(fixture.Reader.Tombstones(1).Count == (status == FileAbsence.Gone ? 1 : 0),
                "partial displacement tombstones only an identity-confirmed absent object");
            Require(snapshot.PendingDeletionEvidence.ContainsKey(old.Path) == (status != FileAbsence.Gone),
                "unverified displaced object is pending");
            Require(fixture.Reader.Live(1, null).Any(i => i.ObjectId == "old") == (status != FileAbsence.Gone),
                "pending displacement retains old inventory until it can be reconciled");
        }
        using (var moved = new SnapshotFixture(Fresh(root, "partial-replacement-seen")))
        {
            var old = F("file", "old", "aa");
            moved.Write([old]);
            var snapshot = new Snapshot("root", Side.A, 1, "c", [old with { Path = "elsewhere" },
                F("file", "new", "bb")], ["blocked"])
                { VerifyAbsence = _ => throw new InvalidOperationException("Seen identity must not be probed") };
            moved._writer.WriteSnapshot(snapshot, 1);
            Require(moved.Reader.Live(1, null).Count == 2 && moved.Reader.Tombstones(1).Count == 0 &&
                snapshot.PendingDeletionEvidence.Count == 0, "observed displaced identity is a move, not absence");
        }
        using var cloud = new SnapshotFixture(Fresh(root, "partial-cloud-no-verifier"));
        cloud.Write([F("missing", "old", "aa")]);
        var partial = new Snapshot("root", Side.B, 1, "c", [], ["blocked"]);
        cloud._writer.WriteSnapshot(partial, 1);
        Require(cloud.Reader.Live(1, null).Count == 1 && cloud.Reader.Tombstones(1).Count == 0 &&
            partial.PendingDeletionEvidence.ContainsKey("missing"), "no verifier means pending, never cloud deletion evidence");
    }

    private static void SnapshotInitialDebtAndTombstone(string root)
    {
        using var fixture = new SnapshotFixture(Fresh(root, "initial-debt-tombstone"));
        var item = F("file", "returning", "aa");
        long dir;
        using (var tx = fixture.Connection.BeginTransaction())
        {
            dir = fixture.Dirs.Intern("", tx);
            tx.Commit();
        }
        StoreSchema.Execute(fixture.Connection, null,
            "INSERT INTO tombstone(root_no,object_id,dir_no,name,deleted_at) VALUES(1,'returning',$d,'file',0);" +
            "INSERT INTO local_rehash(root_no,object_id,last_attempt,reason) VALUES(1,'returning',0,'pending');",
            ("$d", dir));
        fixture.Write([item]);
        var live = fixture.Reader.Live(1, null).Single();
        Require(live.Hash is null && (live.Flags & ItemFlags.NeedsRehash) != 0,
            "first live load preserves existing identity-bound rehash debt");
        Require(fixture.Reader.Tombstones(1).Count == 0, "first live load clears a returning object's tombstone");
    }

    private static void SnapshotPartialMove(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Fresh(root, "partial-move");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "old"), "content");
        using var store = SqliteStateStore.Open(Fresh(root, "partial-move-store"), Fast, new TestClock());
        var inventory = LocalChecks.Inventory(folder, store, new TestClock());
        var initial = inventory.FullAsync(1, _ => true, null!, CancellationToken.None).GetAwaiter().GetResult();
        store.WriteSnapshot(initial with { Gen = store.BeginGeneration(initial.RootId, "full") });
        File.Move(Path.Combine(folder, "old"), Path.Combine(folder, "new"));
        using var lease = PilotDirectoryLease.Open(folder);
        var scan = new PilotLocalScanner { MetadataOnly = true }.ScanAsync(lease).GetAwaiter().GetResult();
        var delta = inventory.BuildDelta(store.BeginGeneration(initial.RootId, "delta"), initial.RootId,
            "before", "after", scan, ["old", "new"], _ => true, T0, ["blocked"]);
        Require(delta.PendingDeletionEvidence.Count == 0 &&
            delta.Changes.Any(c => c.Kind == ChangeKind.Delete && c.Path == "old"),
            "included scanned identity is a normal move before any absence probe");
        store.WriteDelta(delta);
        Require(store.Live(initial.RootId, null).Single().Path == "new" && delta.PendingDeletionEvidence.Count == 0,
            "partial refresh publishes move without false pending evidence");
    }
}
