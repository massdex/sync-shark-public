using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    private const int LargePending = 2000;
    // Three exclusion groups of this size: other pair, stale epoch and above the watermark. Integer division
    // stores 19,998 rows, the number the step budget below is stated against.
    private const int NoiseRows = 20000;

    // B-137: the queue snapshot counts every pending Acquired row of both current sources once, at the pass
    // watermark: mixed sides at their own epochs, a row a holding issue keeps from its effect, and a row a live
    // parent defers. On the real store it is one covering index range count per side, so other-pair, stale-epoch
    // and past-watermark noise leaves the cost with the pending rows. Every scenario gets a fresh store, then the
    // same values are checked again on an in-memory store so the two implementations agree.
    private static void PassTotal()
    {
        foreach (var check in new Action<IStateStore>[]
            { TotalAtPassStart, CountsMixedSidesAtTheirOwnEpoch, CountsHeldAndDeferred })
        {
            WithStore(store => check(store));
            using var memory = new InMemoryStateStore();
            memory.SavePair(Pair(PairId));
            check(memory);
        }
        long clean = 0, noisy = 0;
        WithStore(store => clean = TotalSteps(store, 0));
        WithStore(store => noisy = TotalSteps(store, NoiseRows));
        using (var memory = new InMemoryStateStore())
        {
            memory.SavePair(Pair(PairId));
            TotalSteps(memory, NoiseRows);
        }
        var noise = 3 * (NoiseRows / 3);
        // Absolute bound derived from the plan, not from the clean run: one covering index range per side, so the
        // work is a bounded seek plus a few VM steps per eligible index entry, apart from the constant cost of the
        // pair and source reads and statement setup. A covering entry costs only the loop's own opcodes, so 4 per
        // entry bounds one pass with room, while a second walk over the same 2,000 entries cannot fit inside it.
        const long StepsPerEntry = 4;
        const long FixedSteps = 2000;
        var budget = StepsPerEntry * LargePending + FixedSteps;
        Console.WriteLine($"B-137 queue snapshot VM steps for {LargePending} pending: clean {clean}, " +
            $"with {noise} excluded noise rows {noisy}, budget {budget}");
        // Index seeks cost a bounded number of steps whatever the table holds outside the prefix; reading even one
        // noise row each would cost the whole noise population. The budget is half a step per noise row, so it is
        // derived from the fixture, not from the clean measurement a duplicated walk would inflate too.
        Check.Require(clean > 0 && clean <= budget && noisy <= budget && noisy - clean <= noise / 2,
            "B-137 the queue snapshot ignores other pairs, stale epochs and rows past the watermark, and stays " +
            "within its covering-range budget: " + clean + " then " + noisy + ", budget " + budget);
    }

    private static void TotalAtPassStart(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        var through = Acquire(store, provenance, NextCursor,
            [Notice(Side.Dropbox, File, Identity), Notice(Side.Dropbox, OtherFile, OtherIdentity)]);
        var queued = store.CountWork(PairId, provenance.PairGeneration, through);
        Check.Require(queued == (0, 2),
            "B-137 the queue snapshot counts every pending row at the watermark" + name + ": " + queued);
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        while (store.SelectWork(PairId, provenance.PairGeneration, scope) is { } selection)
        {
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted." + name);
            Check.Require(store.CompleteWork(selection, null), "The pass retires one selected row." + name);
        }
        store.EndWorkPass(PairId);
        Check.Require(store.CountWork(PairId, provenance.PairGeneration, store.ReadWorkWatermark()) == (0, 0),
            "B-137 completed rows leave the queue snapshot" + name);
        Check.Require(store.CountWork(PairId, provenance.PairGeneration, through) == (0, 0),
            "B-137 a fresh count sees the retired rows at the same watermark" + name);
        Check.Require(queued == (0, 2), "B-137 the value a pass reports is fixed when it starts" + name);
    }

    private static void CountsMixedSidesAtTheirOwnEpoch(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        store.AdvanceSource(PairId, Side.Local, provenance.PairGeneration, local.Epoch, local.Revision,
            local.Provenance, local.Cursor, local.Epoch + 1, local.Revision + 1, local.Provenance, local.Cursor, null);
        var moved = store.ReadSource(PairId, Side.Local)!.Value;
        Check.Require(moved.Epoch != store.ReadSource(PairId, Side.Dropbox)!.Value.Epoch,
            "B-137 the two sources hold different epochs" + name);
        store.AcquireJournalPage(Volume, null, new JournalPosition(1, 1),
            [(PairId, provenance.PairGeneration, moved.Epoch, moved.Revision, false)],
            [(PairId, Notice(Side.Local, File, "local-identity"))]);
        var through = Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, OtherFile, OtherIdentity)]);
        var queued = store.CountWork(PairId, provenance.PairGeneration, through);
        Check.Require(queued == (1, 1),
            "B-137 the queue snapshot counts each side at its own current source epoch" + name + ": " + queued);
    }

    private static void CountsHeldAndDeferred(IStateStore store)
    {
        var name = store is StateStore ? " real" : " in-memory";
        var provenance = InstallWithoutObligations(store);
        Acquire(store, provenance, NextCursor, [Notice(Side.Dropbox, File, Identity)]);
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        var parent = store.SelectWork(PairId, provenance.PairGeneration, scope) ??
            throw new InvalidOperationException("B-137 needs one selectable row" + name);
        Check.Require(store.BeginSelection(parent) is not null, "Selection setup must be admitted." + name);
        var subject = new IssueSubject([new IssueItem(Side.Dropbox, OtherFile, OtherIdentity)], null);
        store.Commit(PairId, StateChange.Empty with
            { Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, OtherFile, subject)] });
        var issue = store.Issues(PairId).Single();
        // The issue's child registers against the still-pending parent row, so selection defers the child while the
        // queue counts both; the parent itself is neither held nor deferred.
        store.AttachIssueWork(parent, issue, [(Notice(Side.Dropbox, OtherFile, OtherIdentity), false, null)]);
        var queued = store.CountWork(PairId, provenance.PairGeneration, store.ReadWorkWatermark());
        Check.Require(queued == (0, 2),
            "B-137 a pending parent and its issue-deferred child stay in the queue snapshot" + name + ": " + queued);
        var selected = store.SelectWork(PairId, provenance.PairGeneration, scope);
        Check.Require(selected is null || selected.AnchorWorkId == parent.AnchorWorkId,
            "B-137 a deferred child is queued but never selected" + name);
    }

    private static long TotalSteps(IStateStore store, int noiseRows)
    {
        if (noiseRows > 0)
        {
            store.SavePair(Pair(OtherPair));
            var other = InstallWithoutObligations(store, OtherPair);
            Acquire(store, other, NextCursor, Enumerable.Range(0, noiseRows / 3)
                .Select(index => Notice(Side.Dropbox, "Other/" + index, "other-" + index)), OtherPair);
        }
        var provenance = InstallWithoutObligations(store);
        if (noiseRows > 0)
        {
            // Rows of an epoch the pair has left: selection and counting must skip them by the index prefix alone.
            Acquire(store, provenance, NextCursor, Enumerable.Range(0, noiseRows / 3)
                .Select(index => Notice(Side.Dropbox, "Stale/" + index, "stale-" + index)));
            var stale = store.ReadSource(PairId, Side.Dropbox)!.Value;
            var current = stale.Provenance! with { SourceEpoch = stale.Epoch + 1 };
            store.AdvanceSource(PairId, Side.Dropbox, provenance.PairGeneration, stale.Epoch, stale.Revision,
                stale.Provenance, stale.Cursor, current.SourceEpoch, stale.Revision + 1, current, stale.Cursor, null);
            provenance = current;
        }
        var through = Acquire(store, provenance, NextCursor, Enumerable.Range(0, LargePending)
            .Select(index => Notice(Side.Dropbox, "Backlog/" + index, "backlog-" + index)));
        if (noiseRows > 0)
            Acquire(store, provenance, NextCursor, Enumerable.Range(0, noiseRows / 3)
                .Select(index => Notice(Side.Dropbox, "Later/" + index, "later-" + index)));
        var queued = (0L, 0L);
        var steps = Steps(store, () => queued = store.CountWork(PairId, provenance.PairGeneration, through));
        Check.Require(queued == (0, LargePending),
            "B-137 the queue snapshot counts only the pending rows at the watermark: " + queued);
        return steps;
    }
}
