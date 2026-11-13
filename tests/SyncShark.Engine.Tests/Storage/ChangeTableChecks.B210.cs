using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    public static void PathOnlyCoalescing()
    {
        var failures = new List<Exception>();
        void CheckCase(Action check)
        {
            try { check(); }
            catch (Exception error) { failures.Add(error); }
        }
        CheckCase(() => WithStore(B210LiveBinding));
        CheckCase(() => WithStore(B210Fifo));
        CheckCase(() => WithStore(B210FinitePass));
        foreach (var unknown in new[] { false, true }) CheckCase(() => WithStore(store => B210DirtyUnion(store, unknown)));
        foreach (var different in new[] { false, true }) CheckCase(() => WithStore(store => B210BeforeBytes(store, different)));
        foreach (var guard in new[] { "ObservedNull", "Skip", "ObservedFolder", "BeforeFolder", "NoticeId",
            "IdentifiedBarrier", "Deleted", "OldPath", "OtherKey", "Discovery", "Issue", "Children", "Rename" })
            CheckCase(() => WithStore(store => B210Barrier(store, guard)));
        foreach (var protection in new[] { "MidReadInsideWalk", "Attempted", "PeerClaim", "SuspendedDependent", "SuspendedPrerequisite" })
            CheckCase(() => WithStore(store => B210Protection(store, protection)));
        CheckCase(() => WithStore((_, database) => B210EpisodeBarrier(database)));
        foreach (var overlap in new[] { false, true }) CheckCase(() => WithStore(store => B210PendingScope(store, overlap)));
        foreach (var afterDelete in new[] { false, true }) CheckCase(() => WithStore(store => B210AtomicRollback(store, afterDelete)));
        CheckCase(() => WithStore((store, database) => B210RestartDrain(store, database)));
        foreach (var identified in new[] { false, true }) CheckCase(() => WithStore(store => B210CostGate(store, identified)));
        CheckCase(() => WithStore(B210CaseSpelling));
        CheckCase(() => WithStore(B210AliasIndependence));
        foreach (var size in new[] { 8, 128 }) CheckCase(() => WithStore((store, database) => B210Measure(store, database, size)));
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static SqliteConnection B210Connection(StateStore store) => (SqliteConnection)typeof(StateStore)
        .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static object? B210Sql(StateStore store, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = B210Connection(store).CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    private static ChangeNotice B210Notice(string path = File) => new(Side.Local, ChangeKind.Changed, path, null,
        null, new Entry(path, false, 1, DateTimeOffset.UnixEpoch, Identity, "", "", null), false, false);

    private static SyncedEntry B210Binding() => new(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
        false, Identity, 1, 0, OtherIdentity, "hash");

    private static long B210Acquire(StateStore store, params ChangeNotice[] notices)
    {
        var source = store.ReadSource(PairId, Side.Local)!.Value;
        var previous = store.ReadJournal("0");
        return store.AcquireJournalPage("0", previous, new JournalPosition(0, (previous?.NextUsn ?? 0) + 1),
            [(PairId, store.ReadPairExecution(PairId)!.Value.Generation, source.Epoch, source.Revision, false)],
            notices.Select(notice => (PairId, notice)))!.Value;
    }

    private static WorkSelection B210Selection(StateStore store, long id, string path = File, string? identity = null) =>
        new(PairId, store.ReadPairExecution(PairId)!.Value.Generation, store.ReadSource(PairId, Side.Local)!.Value.Epoch,
            Side.Local, "0", identity, PathNames.Key(path), id, store.ReadWorkWatermark());

    private static WorkSelection? B210Next(StateStore store) => store.SelectWork(PairId,
        store.ReadPairExecution(PairId)!.Value.Generation, new PathScope(store.Pair(PairId)!, store.Pairs()));

    private static long B210Count(StateStore store) => store.CountWork(PairId,
        store.ReadPairExecution(PairId)!.Value.Generation, store.ReadWorkWatermark()).Local;

    private static void B210LiveBinding(StateStore store)
    {
        InstallWithoutObligations(store);
        var binding = B210Binding();
        store.Commit(PairId, StateChange.Empty with { Record = [binding] });
        var first = B210Acquire(store, B210Notice());
        var begun = store.BeginSelection(B210Selection(store, first))!;
        Check.Require(store.ReadWork(begun).Single().Before?.LocalId == Identity,
            "B210-LiveBinding setup: BeginSelection writes the real saved binding with its LocalId");
        store.EndSelection(begun);
        var newest = B210Acquire(store, B210Notice(), B210Notice());
        var identified = B210Acquire(store, B210Notice() with { Id = Identity });
        var selected = B210Next(store)!;
        Check.Require(selected?.AnchorWorkId == newest && selected.Identity is null && B210Count(store) == 2 &&
            store.ReadWork(selected).Single().Before == binding,
            "B210-LiveBinding: saved LocalId is valid path-only debt; three journal rows become one beside identified work");
        Check.Require(B210Next(store)?.AnchorWorkId == identified, "B210-IdentifiedBarrier: identified work stays separate");
    }

    private static void B210Fifo(StateStore store)
    {
        InstallWithoutObligations(store);
        B210Acquire(store, B210Notice());
        var other = B210Acquire(store, B210Notice(OtherFile));
        var newest = B210Acquire(store, B210Notice());
        Check.Require(B210Next(store)?.AnchorWorkId == other && B210Next(store)?.AnchorWorkId == newest &&
            B210Count(store) == 2, "B210-Fifo: the newest id keeps its place behind unrelated work");
    }

    private static void B210FinitePass(StateStore store)
    {
        InstallWithoutObligations(store);
        var other = B210Acquire(store, B210Notice(OtherFile));
        B210Acquire(store, B210Notice());
        Check.Require(B210Next(store)?.AnchorWorkId == other, "B210-FinitePass setup: capture the old watermark");
        var newest = B210Acquire(store, B210Notice());
        Check.Require(B210Next(store) is null && B210Count(store) == 2,
            "B210-FinitePass: transfer beyond the watermark grants no execution in this pass");
        Check.Require(store.EndWorkPass(PairId), "B210-NoSpin: null-returning real merge publishes repeat credit");
        Check.Require(B210Next(store)?.AnchorWorkId == other && B210Next(store)?.AnchorWorkId == newest &&
            B210Next(store) is null && !store.EndWorkPass(PairId),
            "B210-NoSpin: the next unchanged opportunity has no merge credit and ends");
    }

    private static void B210DirtyUnion(StateStore store, bool unknown)
    {
        InstallWithoutObligations(store);
        B210Acquire(store, B210Notice() with { LocalContentDirty = true, Usn = unknown ? null : 9 });
        var newest = B210Acquire(store, B210Notice() with { LocalContentDirty = unknown, Usn = 4 });
        var selected = B210Next(store)!;
        var notice = store.ReadWork(selected).Single().Notice;
        Check.Require(selected.AnchorWorkId == newest && notice.LocalContentDirty && notice.Usn == (unknown ? null : 9UL),
            "B210-DirtyUnion: retain earlier dirty evidence and unknown USN; unknown=" + unknown);
        Check.Require((long)B210Sql(store, "SELECT acquisition_work_id FROM change_work WHERE work_id=$id;", ("$id", newest))! == newest,
            "B210-MaxAcquisition: newest acquisition remains the evidence boundary");
    }

    private static void B210BeforeBytes(StateStore store, bool different)
    {
        InstallWithoutObligations(store);
        var first = B210Acquire(store, B210Notice());
        var second = B210Acquire(store, B210Notice());
        var before = StateEncoding.Envelope(B210Binding());
        B210Sql(store, "UPDATE change_work SET before_json=$before WHERE work_id=$id;", ("$before", before), ("$id", first));
        if (different)
            B210Sql(store, "UPDATE change_work SET before_json=$before WHERE work_id=$id;", ("$before", before + " "), ("$id", second));
        var selection = B210Next(store)!;
        Check.Require(selection.AnchorWorkId == (different ? first : second) && B210Count(store) == (different ? 2 : 1) &&
            (string)B210Sql(store, "SELECT before_json FROM change_work WHERE work_id=$id;", ("$id", selection.AnchorWorkId))! == before,
            "B210-BeforeBytes: differing persisted bytes are a barrier; null carries shared bytes; different=" + different);
    }

    private static void B210Barrier(StateStore store, string guard)
    {
        InstallWithoutObligations(store);
        var notice = B210Notice();
        var first = B210Acquire(store, notice);
        var middleNotice = guard switch
        {
            "ObservedNull" => notice with { Observed = null },
            "Skip" => notice with { Observed = notice.Observed! with { Skip = nameof(TextKey.SkipLink) } },
            "ObservedFolder" => notice with { Observed = notice.Observed! with { IsFolder = true } },
            "IdentifiedBarrier" => notice with { Id = Identity },
            "Deleted" => notice with { Kind = ChangeKind.Deleted },
            "OldPath" => notice with { OldPath = OtherFile },
            "OtherKey" => B210Notice(OtherFile) with { OldPath = File },
            _ => notice
        };
        var middle = B210Acquire(store, middleNotice);
        var last = B210Acquire(store, notice);
        switch (guard)
        {
            case "BeforeFolder":
                B210Sql(store, "UPDATE change_work SET before_json=$value WHERE work_id=$id;",
                    ("$value", StateEncoding.Envelope(B210Binding() with { IsFolder = true })), ("$id", middle)); break;
            case "NoticeId":
                B210Sql(store, "UPDATE change_work SET notice_json=$value WHERE work_id=$id;",
                    ("$value", StateEncoding.Envelope(notice with { Id = Identity })), ("$id", middle)); break;
            case "Discovery":
                store.RequireDiscovery(B210Selection(store, middle), middle); break;
            case "Rename":
                B210Sql(store, "UPDATE change_work SET rename_continuation_json=$value WHERE work_id=$id;",
                    ("$value", StateEncoding.Envelope(new RenameContinuation(B210Binding(), null))), ("$id", middle)); break;
            case "Children":
                B210Sql(store, "UPDATE change_work SET parent_anchor=$parent WHERE work_id=$id;", ("$parent", middle), ("$id", last)); break;
            case "Issue":
                var subject = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null);
                store.Commit(PairId, StateChange.Empty with { Raise = [(IssueKind.Failure, TextKey.FailChanged, File, subject)] });
                var issue = store.Issues(PairId).Single();
                B210Sql(store, "INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,subject_json) " +
                    "SELECT $issue,id,$work,$side,$path,$subject FROM pair WHERE pair_id=$pair;",
                    ("$issue", issue.Id), ("$work", middle), ("$side", (int)Side.Local), ("$path", PathNames.Key(File)),
                    ("$subject", StateEncoding.Envelope(subject)), ("$pair", PairId)); break;
        }
        var selected = B210Next(store)!;
        // A rename at another primary key is not a member: its old-path footprint cannot consume either endpoint.
        Check.Require(selected.AnchorWorkId == (guard == "OtherKey" ? middle : first) &&
            B210Count(store) == (guard == "OtherKey" ? 2 : 3),
            "B210-Custody-" + guard + ": incompatible custody is a barrier, never filtered out");
    }

    private static void B210Protection(StateStore store, string protection)
    {
        var provenance = InstallWithoutObligations(store);
        var opening = protection == "Attempted" ? B210Acquire(store, B210Notice(OtherFile)) : 0;
        var first = B210Acquire(store, B210Notice());
        var middle = B210Acquire(store, B210Notice());
        B210Acquire(store, B210Notice());
        if (opening != 0) Check.Require(B210Next(store)?.AnchorWorkId == opening, "B210-Attempted setup opens the pass");
        if (protection == "PeerClaim")
        {
            var remoteId = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, OtherIdentity)]);
            var remote = new WorkSelection(PairId, provenance.PairGeneration, provenance.SourceEpoch, Side.Dropbox,
                Domain, OtherIdentity, PathNames.DropboxKey(File), remoteId, remoteId);
            store.BeginSelection(remote);
            store.ClaimSelectionPeer(remote, new SelectionPeer(PathNames.Key(File), remoteId));
        }
        else
        {
            var selected = B210Selection(store, middle);
            store.BeginSelection(selected);
            if (protection == "Attempted") store.EndSelection(selected);
            if (protection.StartsWith("Suspended", StringComparison.Ordinal))
            {
                var dependency = B210Acquire(store, B210Notice(OtherFile));
                var other = B210Selection(store, dependency, OtherFile);
                if (protection == "SuspendedDependent") store.EndSelection(selected, other);
                else
                {
                    store.EndSelection(selected);
                    store.BeginSelection(other);
                    store.EndSelection(other, selected);
                }
            }
        }
        Check.Require(B210Next(store)?.AnchorWorkId == first && B210Count(store) >= 3 &&
            store.ReadWork(B210Selection(store, middle)).Single().WorkId == middle,
            "B210-" + protection + ": an interior protected row prevents retirement through it");
    }

    private static void B210EpisodeBarrier(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = InstallWithoutObligations(store);
        var first = B210Acquire(store, B210Notice());
        var source = store.ReadSource(PairId, Side.Local)!.Value;
        var episode = store.RecordInUseFailure(PairId, provenance.PairGeneration, source.Epoch, File,
            new IssueSubject([new IssueItem(Side.Local, File, Identity)], null), TextKey.FailLocalInUse,
            null, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        B210Acquire(store, B210Notice());
        Check.Require(B210Next(store)?.AnchorWorkId == first && B210Count(store) == 2 &&
            store.ReadItemEpisodeIssues(PairId).Contains(episode.Token.IssueId),
            "B210-EpisodeBarrier: source-less episode between acquired rows is not crossed");
    }

    private static void B210PendingScope(StateStore store, bool overlap)
    {
        InstallWithoutObligations(store);
        var first = B210Acquire(store, B210Notice());
        var last = B210Acquire(store, B210Notice());
        var path = overlap ? File : OtherFile;
        store.ArmPending(PairId, new PendingEffect(1, "pending", store.ReadPairExecution(PairId)!.Value.Generation,
            PendingKind.LocalDelete, PendingPhase.Armed, path,
            new RemovalProof(B210Notice(path).Observed!, "0", "hash"), null, null, null, null, null, false, null));
        Check.Require(B210Next(store)?.AnchorWorkId == (overlap ? first : last) && B210Count(store) == (overlap ? 2 : 1),
            "B210-PendingScope: only overlapping pending effects veto coalescing; overlap=" + overlap);
    }

    private static string B210Snapshot(StateStore store)
    {
        using var command = B210Connection(store).CreateCommand();
        command.CommandText = "SELECT * FROM change_work ORDER BY work_id;";
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return JsonSerializer.Serialize(rows);
    }

    private static void B210AtomicRollback(StateStore store, bool afterDelete)
    {
        InstallWithoutObligations(store);
        B210Acquire(store, B210Notice() with { LocalContentDirty = true }, B210Notice(), B210Notice());
        var snapshot = B210Snapshot(store);
        B210Sql(store, afterDelete
            ? "CREATE TRIGGER b210_fault BEFORE DELETE ON change_work WHEN (SELECT count(*) FROM change_work)=2 BEGIN SELECT raise(ABORT,'b210 fault'); END;"
            : "CREATE TRIGGER b210_fault BEFORE DELETE ON change_work BEGIN SELECT raise(ABORT,'b210 fault'); END;");
        var failed = false;
        try { B210Next(store); }
        catch (SqliteException) { failed = true; }
        Check.Require(failed && B210Snapshot(store) == snapshot && !store.EndWorkPass(PairId),
            "B210-AtomicRollback: update and prior deletes roll back with no published accounting; afterDelete=" + afterDelete);
    }

    private static void B210RestartDrain(StateStore store, string database)
    {
        InstallWithoutObligations(store);
        var last = B210Acquire(store, B210Notice(), B210Notice(), B210Notice());
        B210Next(store);
        using var restarted = StateStore.Open(database);
        var selected = B210Next(restarted)!;
        Check.Require(selected?.AnchorWorkId == last && B210Count(restarted) == 1 &&
            restarted.CompleteWork(selected, null) && B210Next(restarted) is null && B210Count(restarted) == 0,
            "B210-RestartDrain: the durable newest obligation survives restart and drains normally");
    }

    private static void B210CostGate(StateStore store, bool identified)
    {
        InstallWithoutObligations(store);
        B210Acquire(store, B210Notice() with { Id = identified ? Identity : null });
        var statements = new List<string>();
        var connection = B210Connection(store);
        SQLitePCL.delegate_trace trace = (_, sql) => statements.Add(sql.utf8_to_string());
        SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null);
        try { B210Next(store); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        var seeks = statements.Count(sql => sql.Contains("ORDER BY w.path_key,w.work_id LIMIT", StringComparison.Ordinal));
        var watermarks = statements.Count(sql => sql.TrimEnd(';') == StateSchema.WorkWatermark.TrimEnd(';'));
        Check.Require(seeks == (identified ? 0 : 1) && watermarks == (identified ? 1 : 2) &&
            !statements.Any(sql => sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)),
            "B210-CostGate: identified has zero extra reads; singleton pays seek plus observed watermark, no transaction; " +
            "identified=" + identified + "; seeks=" + seeks + "; watermarks=" + watermarks);
        Check.Require(B210Next(store) is null && !store.EndWorkPass(PairId), "B210-NoSpin: singleton has no repeat credit");
    }

    private static void B210Measure(StateStore store, string database, int size)
    {
        InstallWithoutObligations(store);
        B210Acquire(store, Enumerable.Repeat(B210Notice(), size).ToArray());
        var commands = 0;
        long began = 0, committed = 0;
        SQLitePCL.delegate_trace trace = (_, sql) =>
        {
            commands++;
            var statement = sql.utf8_to_string();
            if (statement.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)) began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (statement.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)) committed = System.Diagnostics.Stopwatch.GetTimestamp();
        };
        var wal = database + "-wal";
        var beforeWal = new System.IO.FileInfo(wal).Length;
        SQLitePCL.raw.sqlite3_trace(B210Connection(store).Handle, trace, null);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        long steps;
        try { steps = Steps(store, () => B210Next(store)); }
        finally { SQLitePCL.raw.sqlite3_trace(B210Connection(store).Handle, (SQLitePCL.delegate_trace)null!, null); }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check.Require(B210Count(store) == 1 && commands < 10 * size + 50 && began > 0 && committed >= began,
            "B210-CostMeasure: exact-key walks have linear counted SQL and one committed segment");
        // W08-style synthetic regression only, not a projection to the W01/W02 million-entry workload.
        Console.WriteLine($"B210-Cost size={size} sql={commands} vm={steps} allocated={allocated} " +
            $"orderedCallMs={elapsed:F3} transactionToCommitMs={System.Diagnostics.Stopwatch.GetElapsedTime(began, committed).TotalMilliseconds:F3} " +
            $"walGrowth={new System.IO.FileInfo(wal).Length - beforeWal}");
    }

    private static void B210CaseSpelling(StateStore store)
    {
        InstallWithoutObligations(store);
        B210Acquire(store, B210Notice(File.ToLowerInvariant()));
        var last = B210Acquire(store, B210Notice(File.ToUpperInvariant()));
        var selected = B210Next(store)!;
        Check.Require(selected.AnchorWorkId == last && store.ReadWork(selected).Single().Notice.Path == File.ToUpperInvariant(),
            "B210-CaseSpelling: newest observed name spelling survives the transfer");
    }

    private static void B210AliasIndependence(StateStore store)
    {
        InstallWithoutObligations(store);
        var a = B210Notice();
        var b = B210Notice(OtherFile);
        B210Acquire(store, a, b, a, b);
        var first = B210Next(store)!;
        var second = B210Next(store)!;
        Check.Require(first is not null && second is not null && first.Key != second.Key && first.Identity is null && second.Identity is null && B210Count(store) == 2 &&
            store.ReadWork(first).Single().Notice.Observed!.Id == store.ReadWork(second).Single().Notice.Observed!.Id,
            "B210-AliasIndependence: identical observed ids do not combine different namespace entries");
    }
}
