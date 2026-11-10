using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    private const int SmallBacklog = 8;
    private const int LargeBacklog = 2000;
    private const string SecondCursor = "cursor-second";

    // B-135: an identity group behind a backlog of earlier top-level work selects once, at its first member, on both
    // stores; on the real store that selection's SQLite VM steps do not grow with the backlog below it.
    private static void IdentityGroupBehindBacklog()
    {
        foreach (var backlog in new[] { SmallBacklog, LargeBacklog })
        {
            using var memory = new InMemoryStateStore();
            memory.SavePair(Pair(PairId));
            _ = IdentityGroupSelection(memory, backlog);
        }
        long small = 0, large = 0;
        WithStore(store => small = IdentityGroupSelection(store, SmallBacklog));
        WithStore(store => large = IdentityGroupSelection(store, LargeBacklog));
        Console.WriteLine($"B-135 identity selection VM steps: backlog {SmallBacklog} = {small}, " +
            $"backlog {LargeBacklog} = {large}");
        Check.Require(small > 0 && large <= 2 * small,
            "B-135 an identity group's selection cost is bounded by its group, not the backlog below it: " +
            small + " then " + large);
        WithStore(store => small = ResidualMembership(store, SmallBacklog));
        WithStore(store => large = ResidualMembership(store, LargeBacklog / 10));
        Console.WriteLine($"B-135 residual membership VM steps: {SmallBacklog} rows = {small}, " +
            $"{LargeBacklog / 10} rows = {large}");
        Check.Require(small > 0 && large <= 2 * small,
            "B-135 a residual's begin, read and completion stay one exact ownership lookup however many " +
            "same-identity rows follow it: " + small + " then " + large);
    }

    // Returns the VM steps of starting, reading and completing one released residual while later top-level rows of
    // its identity, which it does not own, wait in the same selection range.
    private static long ResidualMembership(StateStore store, int unrelated)
    {
        var provenance = Install(store);
        var parentId = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var parent = Selection(provenance, parentId, parentId);
        var before = new SyncedEntry(PathNames.Key(File), File, PathNames.DropboxKey(File), File,
            false, Identity, 0, 0, Identity, string.Empty);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null);
        store.Commit(PairId,
            StateChange.Empty with { Raise = [(IssueKind.Tie, TextKey.TieEqualTimes, OtherFile, subject)] });
        store.AttachIssueWork(parent, store.Issues(PairId).Single(),
            [(Notice(Side.Dropbox, OtherFile, OtherIdentity), false, before)]);
        store.CompleteWork(parent, null);
        store.Commit(PairId, StateChange.Empty with { Close = [(OtherFile, new[] { IssueKind.Tie })] });
        var unrelatedIds = new List<long>();
        for (var index = 0; index < unrelated; index++)
            unrelatedIds.Add(Acquire(store, provenance, SecondCursor + index,
                [Notice(Side.Dropbox, "Unrelated/" + index, OtherIdentity)]));
        var residual = PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity)
            .MinBy(work => work.AnchorWorkId)!;
        var completed = false;
        var steps = Steps(store, () =>
        {
            Check.Require(store.BeginSelection(residual), "Selection setup must be admitted.");
            Check.Require(store.ReadWork(residual).Single().Notice.Path == OtherFile,
                "B-135 the residual reads only its own member");
            completed = store.CompleteWork(residual, null);
        });
        var queued = PendingWork.ByIdentity(store, Side.Dropbox, Domain, OtherIdentity)
            .SelectMany(store.ReadWork).Select(work => work.WorkId).Order().ToList();
        Check.Require(completed && unrelatedIds.Count == unrelated && queued.SequenceEqual(unrelatedIds.Order()),
            "B-135 the residual completes and leaves exactly the unrelated rows of its identity queued: " +
            string.Join(",", queued));
        return steps;
    }

    // Returns the VM steps of the call that selects the group on the real store, and zero on the double.
    private static long IdentityGroupSelection(IStateStore store, int backlog)
    {
        var name = (store is StateStore ? " real" : " in-memory") + " backlog=" + backlog;
        var provenance = InstallWithoutObligations(store);
        Acquire(store, provenance, NextCursor, Enumerable.Range(0, backlog)
            .Select(index => Notice(Side.Dropbox, "Backlog/" + index, "backlog-" + index)));
        var first = Acquire(store, provenance, LastCursor, [Notice(Side.Dropbox, File, Identity)]);
        var second = Acquire(store, provenance, SecondCursor, [Notice(Side.Dropbox, File, Identity)]);
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        for (var index = 0; index < backlog; index++)
            Check.Require(store.SelectWork(PairId, provenance.PairGeneration, scope)?.Identity?.StartsWith(
                "backlog-", StringComparison.Ordinal) == true, "B-135 the backlog selects first in work order" + name);
        WorkSelection? group = null;
        var steps = Steps(store, () => group = store.SelectWork(PairId, provenance.PairGeneration, scope));
        Check.Require(group is not null && group.Identity == Identity && group.AnchorWorkId == first &&
            group.ThroughWorkId >= second && store.ReadWork(group).Count() == 2,
            "B-135 the group selects once at its first member and reads both members" + name);
        Check.Require(store.SelectWork(PairId, provenance.PairGeneration, scope) is null,
            "B-135 the later member is not a second selection" + name);
        store.EndWorkPass(PairId);
        return steps;
    }

    private static long Steps(IStateStore store, Action action)
    {
        if (store is not StateStore real)
        {
            action();
            return 0;
        }
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(real)!;
        long steps = 0;
        SQLitePCL.delegate_progress count = _ => { steps++; return 0; };
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1, count, null);
        try { action(); }
        finally
        {
            SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            GC.KeepAlive(count);
        }
        return steps;
    }
}
