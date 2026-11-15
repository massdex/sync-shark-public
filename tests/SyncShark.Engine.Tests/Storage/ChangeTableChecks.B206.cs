using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

// B-206 A1: a proven-obsolete in-use wait retires its exact notice and its source-less work in one transaction, and
// every responsibility the fresh evidence does not resolve keeps its exact episode, work, member and pending state.
public static partial class ChangeTableChecks
{
    private enum A1Guard { FirstRun, UnknownMove, Recovery, UnrelatedMember }

    // The exact persisted custody of one episode: its token, its persisted plain-JSON subject, its notice, every
    // member association (an unrelated Acquired member included), its projected source-less work and the pending note.
    private sealed record A1Custody(long? IssueRef, string? Episode, long? Revision, long? Work,
        ItemRetryDispatch? Dispatch, string? PendingOperation, long? PendingGeneration,
        long? IssueId, IssueKind? Kind, TextKey? Cause, DateTimeOffset? At,
        string? SubjectJson, string Members,
        long? ProjectedWork, string? ProjectedPath, string? ProjectedId, PendingRead Pending);

    private static void A1NoticeRetirement()
    {
        WithStore((_, database) => A1Retires(database, freshIdentity: null));
        WithStore((_, database) => A1Retires(database, freshIdentity: OtherIdentity));
        WithStore((_, database) => A1AcquiredRemains(database));
        WithStore((_, database) => A1ChangedAuthority(database, revision: true));
        WithStore((_, database) => A1ChangedAuthority(database, revision: false));
        WithStore((_, database) => A1ChangedBinding(database));
        WithStore((_, database) => A1DirtyChildRetirement(database));
        foreach (var guard in Enum.GetValues<A1Guard>())
            WithStore((_, database) => A1Protected(database, guard));
        WithStore((_, database) => A1OtherIssueAtPath(database));
        WithStore((_, database) => A1FaultBeforeCommit(database));
    }

    private static (long Generation, long LocalEpoch, long LocalRevision, string Volume) A1Prepare(
        StateStore store, string database)
    {
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        store.AcquireJournalPage(volume, null, new JournalPosition(0, 1),
            [(PairId, provenance.PairGeneration, local.Epoch, local.Revision, false)], []);
        return (provenance.PairGeneration, local.Epoch, local.Revision, volume);
    }

    private static IssueSubject A1Item() => new([new IssueItem(Side.Local, File, Identity)], null);

    private static ItemRetryCandidate A1Hold(StateStore store, long generation, long epoch, Guid attempt,
        IssueSubject subject) =>
        store.RecordInUseFailure(PairId, generation, epoch, File, subject, TextKey.FailLocalInUse, null,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);

    private static WorkSelection A1SourceLess(long generation, long epoch, string volume, string identity, long work) =>
        new(PairId, generation, epoch, Side.Local, volume, identity, PathNames.Key(File), work, work);

    // The issue subject itself is plain JSON; a member subject is an envelope of this same value. An unknown-effect
    // move is installed on the issue row alone: `IssueSubject.Move` is an init property outside the member shape the
    // strict reader accepts, and an in-use failure never carries a move in production.
    private static IssueSubject A1Subject(A1Guard guard) => guard == A1Guard.FirstRun
        ? new IssueSubject([new IssueItem(Side.Local, File, Identity)], null, null,
            IssueSubject.FirstRunDeletionKind.Item)
        : A1Item();

    private static Microsoft.Data.Sqlite.SqliteConnection A1Connect(string database)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        connection.CreateFunction(StateSchema.LocalAccessRetryCauseFunction, (string? _) => 1);
        return connection;
    }

    // Fixture-only statements for shapes the public store will not build directly. The cause CHECK names the store's
    // registered function, so the fixture registers it.
    private static void A1Execute(string database, string sql, params (string Name, object? Value)[] arguments)
    {
        using var connection = A1Connect(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // A narrowly scoped read of this synthetic fixture database for the persisted subject and the exact member
    // associations no single public store read exposes together.
    private static string? A1Scalar(string database, string sql, params (string Name, object? Value)[] arguments)
    {
        using var connection = A1Connect(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    private static long? A1Long(string database, string sql, params (string Name, object? Value)[] arguments)
    {
        using var connection = A1Connect(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar() is long number ? number : null;
    }

    // Every member association of the issue, in member order: member, work, side, path, identity, subject.
    private static string A1Members(string database, long issue)
    {
        using var connection = A1Connect(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT member_id,work_id,side,path_key,identity,subject_json FROM issue_member " +
            "WHERE issue_id=$issue ORDER BY member_id;";
        command.Parameters.AddWithValue("$issue", issue);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join(":", values));
        }
        return string.Join("|", rows);
    }

    private static A1Custody A1Capture(StateStore store, string database, ItemRetryCandidate held, long generation,
        long epoch, string volume)
    {
        var wait = store.ReadItemRetryState(PairId, [held.Token.IssueId])[held.Token.IssueId];
        var issue = store.IssueById(PairId, held.Token.IssueId);
        var projected = wait is null ? null : store.ReadWork(A1SourceLess(generation, epoch, volume,
            held.Token.Subject.Items[0].Id!, held.Token.WorkId)).SingleOrDefault();
        return new A1Custody(
            wait?.Token.IssueId, wait?.Token.EpisodeId, wait?.Token.Revision, wait?.Token.WorkId,
            wait?.Dispatch, wait?.PendingOperationId, wait?.PendingGeneration,
            issue?.Id, issue?.Kind, issue?.Cause, issue?.At,
            A1Scalar(database, "SELECT subject_json FROM issue WHERE issue_id=$issue LIMIT 1;",
                ("$issue", held.Token.IssueId)),
            A1Members(database, held.Token.IssueId),
            projected?.WorkId, projected?.Notice.Path, projected?.Notice.Id, store.Pending(PairId));
    }

    // Different identity present or confirmed absence both retire the exact obsolete wait and its notice, and delete
    // its source-less work once no responsibility remains.
    private static void A1Retires(string database, string? freshIdentity)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, _, volume) = A1Prepare(store, database);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.EndWorkPass(PairId);
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, freshIdentity ?? "").Single();
        store.RetireReplacedItemRetry(candidate, File, freshIdentity);
        Check.Require(store.Issues(PairId).Count == 0 &&
            store.ReadItemRetryState(PairId, [held.Token.IssueId])[held.Token.IssueId] is null,
            "B-206 A1: a retired source-less wait closes its exact notice and clears its episode; fresh=" +
            (freshIdentity ?? "<absent>"));
        Throws<LiveAdmissionStaleException>(() =>
            store.ReadWork(A1SourceLess(generation, epoch, volume, Identity, held.Token.WorkId)).ToArray());
    }

    // The acquired obligation at the path stays selectable; only the source-less notice and its work retire.
    private static void A1AcquiredRemains(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, revision, volume) = A1Prepare(store, database);
        store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
            [(PairId, generation, epoch, revision, false)], [(PairId, Notice(Side.Local, File, Identity))]);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.EndWorkPass(PairId);
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        store.RetireReplacedItemRetry(candidate, File, OtherIdentity);
        var acquired = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ??
            throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.Issues(PairId).Count == 0 && store.ReadWork(acquired).Any(),
            "B-206 A1: the acquired obligation stays selectable after the source-less notice retires");
    }

    // A changed episode revision or subject is superseded: the captured candidate never clears or closes it.
    private static void A1ChangedAuthority(string database, bool revision)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, _, volume) = A1Prepare(store, database);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.EndWorkPass(PairId);
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        string? expectedSubject = null;
        if (revision)
        {
            A1Execute(database, "UPDATE issue SET item_retry_revision=item_retry_revision+1 WHERE issue_id=$issue;",
                ("$issue", held.Token.IssueId));
            Check.Require(store.ReadItemRetryState(PairId, [held.Token.IssueId])[held.Token.IssueId]?.Token.Revision ==
                held.Token.Revision + 1, "B-206 A1 setup: a changed revision is installed before retirement");
        }
        else
        {
            var replacement = new IssueSubject([new IssueItem(Side.Local, File, OtherIdentity)], null);
            expectedSubject = JsonSerializer.Serialize(replacement);
            A1Execute(database, "UPDATE issue SET subject_json=$subject WHERE issue_id=$issue;",
                ("$issue", held.Token.IssueId), ("$subject", expectedSubject));
            Check.Require(store.IssueById(PairId, held.Token.IssueId)?.Subject.HasSameItems(replacement) == true,
                "B-206 A1 setup: a valid replacement subject is installed before retirement");
        }
        var before = A1Capture(store, database, held, generation, epoch, volume);
        store.RetireReplacedItemRetry(candidate, File, null);
        var after = A1Capture(store, database, held, generation, epoch, volume);
        Check.Require(after == before && after.IssueId == held.Token.IssueId &&
            (revision ? after.Revision == held.Token.Revision + 1 : after.SubjectJson == expectedSubject),
            "B-206 A1: a changed episode " + (revision ? "revision" : "subject") +
            " leaves the exact token, persisted subject, notice, work, member and pending state unchanged");
    }

    // A dispatch/pending binding that changed after the capture is superseded exactly as a changed token is.
    private static void A1ChangedBinding(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, _, volume) = A1Prepare(store, database);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.EndWorkPass(PairId);
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        A1Execute(database, "UPDATE change_work SET dispatch_kind='LocalRecovery'," +
            "pending_operation_id='op',pending_generation=0 WHERE work_id=$work;", ("$work", held.Token.WorkId));
        var before = A1Capture(store, database, held, generation, epoch, volume);
        store.RetireReplacedItemRetry(candidate, File, null);
        var after = A1Capture(store, database, held, generation, epoch, volume);
        Check.Require(after == before && after.Dispatch == ItemRetryDispatch.LocalRecovery &&
            after.PendingOperation == "op" && after.PendingGeneration == 0,
            "B-206 A1: a captured candidate whose dispatch/pending binding changed is never retired");
    }

    // Retiring a source-less parent releases its dirty acquired child with the dirty/USN provenance intact.
    private static void A1DirtyChildRetirement(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, revision, volume) = A1Prepare(store, database);
        store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
            [(PairId, generation, epoch, revision, false)],
            [(PairId, Notice(Side.Local, File, Identity, dirty: true))]);
        var initial = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ??
            throw new InvalidOperationException(ExpectedFailure);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.AttachIssueWork(initial, store.IssueById(PairId, held.Token.IssueId)!,
            [(Notice(Side.Local, File, Identity, dirty: true), false, null)]);
        var child = store.ReadWorkWatermark();
        Check.Require(child > held.Token.WorkId &&
            store.ReadLocalDirty(PairId, generation, epoch, volume, Identity, PathNames.Key(File), null).Dirty,
            "B-206 A1 setup: a dirty child is attached under the source-less parent");
        Check.Require(store.CompleteWork(initial, null), "B-206 A1 setup: the acquired source selection completes");
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        store.RetireReplacedItemRetry(candidate, File, null);
        Check.Require(store.Issues(PairId).Count == 0 &&
            store.ReadItemRetryState(PairId, [held.Token.IssueId])[held.Token.IssueId] is null &&
            store.ReadLocalDirty(PairId, generation, epoch, volume, Identity, PathNames.Key(File), null).Dirty,
            "B-206 A1: retiring the source-less parent closes its obsolete notice and keeps the child's dirty provenance");
        Throws<LiveAdmissionStaleException>(() =>
            store.ReadWork(A1SourceLess(generation, epoch, volume, Identity, held.Token.WorkId)).ToArray());
        // Retirement only increments this pass's retirement accounting; end it so the child (created after the pass
        // watermark) is visible to the next scoped selection.
        store.EndWorkPass(PairId);
        var released = store.SelectWork(PairId, generation, new PathScope(store.Pair(PairId)!, store.Pairs())) ??
            throw new InvalidOperationException(ExpectedFailure);
        var childRow = store.ReadWork(released).SingleOrDefault(row => row.WorkId == child);
        Check.Require(released.AnchorWorkId == child &&
            childRow is { Notice.LocalContentDirty: true, Notice.Path: File },
            "B-206 A1: the released dirty child stays selectable with its dirty evidence intact");
    }

    // First-run, an unknown-effect move, a LocalRecovery binding and another member's work each keep custody.
    private static void A1Protected(string database, A1Guard guard)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, revision, volume) = A1Prepare(store, database);
        if (guard == A1Guard.UnrelatedMember)
            store.AcquireJournalPage(volume, new JournalPosition(0, 1), new JournalPosition(0, 2),
                [(PairId, generation, epoch, revision, false)], [(PairId, Notice(Side.Local, File, Identity))]);
        var held = A1Hold(store, generation, epoch, attempt, A1Subject(guard));
        store.EndWorkPass(PairId);
        if (guard == A1Guard.UnknownMove)
        {
            var moved = new IssueSubject([new IssueItem(Side.Local, File, Identity)], null)
            {
                Move = new IssueSubject.UnknownMove(Identity, File, "Parent/Moved", null, false)
            };
            A1Execute(database, "UPDATE issue SET subject_json=$subject WHERE issue_id=$issue;",
                ("$issue", held.Token.IssueId), ("$subject", JsonSerializer.Serialize(moved)));
            Check.Require(store.IssueById(PairId, held.Token.IssueId)?.Subject.Move is not null,
                "B-206 A1 setup: an unknown-effect move is installed on the issue before retirement");
        }
        if (guard == A1Guard.Recovery)
            A1Execute(database, "UPDATE change_work SET dispatch_kind='LocalRecovery'," +
                "pending_operation_id='op',pending_generation=0 WHERE work_id=$work;", ("$work", held.Token.WorkId));
        if (guard == A1Guard.UnrelatedMember)
        {
            A1Execute(database, "INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json,skip_cause) " +
                "SELECT $issue,w.pair_id,w.work_id,$side,$path,$identity,$subject,NULL FROM change_work w " +
                "WHERE w.pair_id=(SELECT id FROM pair WHERE pair_id=$pairId) AND w.work_origin='Acquired' " +
                "AND w.work_id<>$work LIMIT 1;",
                ("$issue", held.Token.IssueId), ("$pairId", PairId), ("$side", (int)Side.Local),
                ("$path", PathNames.Key(File)), ("$identity", Identity),
                ("$subject", StateEncoding.Envelope(A1Item())), ("$work", held.Token.WorkId));
            Check.Require(A1Long(database,
                    "SELECT count(*) FROM issue_member WHERE issue_id=$issue AND work_id<>$work;",
                    ("$issue", held.Token.IssueId), ("$work", held.Token.WorkId)) == 1,
                "B-206 A1 setup: the unrelated acquired member association is installed");
        }
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        var before = A1Capture(store, database, held, generation, epoch, volume);
        store.RetireReplacedItemRetry(candidate, File, null);
        var after = A1Capture(store, database, held, generation, epoch, volume);
        Check.Require(after == before,
            "B-206 A1: " + guard +
            " keeps the exact episode token, persisted subject, notice, work, member and pending state");
    }

    // A different issue at the same path is untouched by the retirement's exact-id closure.
    private static void A1OtherIssueAtPath(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, _, _) = A1Prepare(store, database);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, File,
                new IssueSubject([new IssueItem(Side.Local, File, OtherIdentity)], null))]
        });
        store.EndWorkPass(PairId);
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        store.RetireReplacedItemRetry(candidate, File, null);
        Check.Require(store.Issues(PairId) is [{ Kind: IssueKind.NameClash }] &&
            store.ReadItemRetryState(PairId, [held.Token.IssueId])[held.Token.IssueId] is null,
            "B-206 A1: a different issue at the same path is untouched while the obsolete in-use notice retires");
    }

    // A fault after the clear and closure but before commit rolls the whole retirement back.
    private static void A1FaultBeforeCommit(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var (generation, epoch, _, volume) = A1Prepare(store, database);
        var held = A1Hold(store, generation, epoch, attempt, A1Item());
        store.EndWorkPass(PairId);
        var candidate = store.ReadReplacedItemRetry(PairId, generation, File, OtherIdentity).Single();
        var before = A1Capture(store, database, held, generation, epoch, volume);
        // The lookup key no longer matches the episode's, so the source-less settlement's exact delete misses.
        A1Execute(database, "UPDATE change_work SET issue_work_key='broken' WHERE work_id=$work;",
            ("$work", held.Token.WorkId));
        Throws<InvalidOperationException>(() => store.RetireReplacedItemRetry(candidate, File, null));
        var after = A1Capture(store, database, held, generation, epoch, volume);
        Check.Require(after == before && after.Pending is NoPending,
            "B-206 A1: a fault before commit rolls the cleared episode, closed notice and work back together");
    }
}
