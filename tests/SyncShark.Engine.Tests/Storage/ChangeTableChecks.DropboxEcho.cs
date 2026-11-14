using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    // Sizes and edge cases are the reviewed B-214 page-accounting contract, not new production limits.
    public static void RunDropboxEchoes()
    {
        WithStore(EchoStagedEarliest);
        WithStore(EchoCoalescingBoundary);
        WithStore(EchoPageAccounting);
        WithStore(EchoRollback);
        WithStore(EchoSuspendedDependency);
        WithStore(store => PersistedRenameBootstrap(store, false, settleFirst: true));
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            long[]? previous = null;
            // One involved head stays fixed while unrelated stored bindings and queued rows quadruple.
            foreach (var unrelated in new[] { 1, 4 })
                WithStore((store, database) =>
                {
                    var steps = EchoOrdinaryStatements(store, database, side, unrelated);
                    Check.Require(previous is null || steps.SequenceEqual(previous),
                        "B214-CR3: each ordinary-head statement has size-independent VM work");
                    previous = steps;
                });
        }
        foreach (var count in new[] { 1, 100 })
            foreach (var deleted in new[] { false, true }) WithStore(store => EchoSettledStatements(store, count, deleted));
        foreach (var shape in new[] { "local-debt", "dropbox-debt", "cursor", "provenance", "domain", "discovery" })
            WithStore(store => EchoTrustGuard(store, shape));
        foreach (var shape in new[] { "exact", "inherited-child", "explicit-child", "ten-overrides",
            "full-overrides", "last-inherited", "late-inherited" })
            WithStore(store => EchoTombstoneBinding(store, shape));
        foreach (var shape in new[] { "dropbox-path", "local-path", "identity" })
            WithStore(store => EchoClaim(store, shape));
    }

    private static SyncedEntry EchoBinding(string local, string? remote = null, string? identity = null) =>
        new(PathNames.Key(local), local, PathNames.DropboxKey(remote ?? local), remote ?? local,
            false, "local-" + local, 1, 0, identity ?? "remote-" + local, "hash");

    private static ChangeNotice EchoNotice(SyncedEntry saved) => new(Side.Dropbox, ChangeKind.Changed,
        saved.DropboxPath, null, saved.DropboxId,
        new Entry(saved.DropboxPath, saved.IsFolder, 1, DateTimeOffset.UnixEpoch, saved.DropboxId, "revision", "hash", null),
        false, false);

    private static (int Settled, bool More) SettleEchoPage(StateStore store, int pageSize = 100) =>
        store.SettleEchoes(PairId, store.ReadPairExecution(PairId)!.Value.Generation,
            new PathScope(store.Pair(PairId)!, store.Pairs()), SelectionObservation.SettlesDropboxEcho, pageSize);

    private static long[] EchoOrdinaryStatements(StateStore store, string database, Side side, int unrelated)
    {
        var source = InstallWithoutObligations(store);
        var bindings = new[] { EchoBinding("A/x") }.Concat(
            Enumerable.Range(0, unrelated).Select(index => EchoBinding($"Other/{index}"))).ToArray();
        store.Commit(PairId, StateChange.Empty with { Record = bindings });
        if (side == Side.Local)
            AcquireLocal(store, source, bindings.Select(saved => Notice(side, saved.LocalPath, saved.LocalId)).ToArray());
        else
            Acquire(store, source, NextCursor, bindings.Select(saved =>
            {
                var echo = EchoNotice(saved);
                return echo with { Observed = echo.Observed! with { ContentHash = "edited" } };
            }));
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        WorkSelection? before = null, after = null;
        var baseline = Statements(store, () => before = store.SelectWork(PairId, source.PairGeneration, scope));
        store.EndWorkPass(PairId);
        var captured = new List<string>();
        var settlement = Statements(store, () => Check.Require(store.SettleEchoes(PairId, source.PairGeneration, scope,
            SelectionObservation.SettlesDropboxEcho, 100) == (0, false), "B214-CR2: ordinary work is not an echo page"),
            captured: captured);
        var candidate = settlement + Statements(store, () => after = store.SelectWork(PairId, source.PairGeneration, scope));
        Console.WriteLine($"B214-CR3 side={side} unrelated={unrelated} involved=1 baseline={baseline} candidate={candidate} delta={candidate - baseline}");
        // Each SelectWork and the Dropbox head's held check read the duplicate-folder hold once (WorkRepairHeld).
        Check.Require(before == after && baseline == 14 && candidate == (side == Side.Local ? 15 : 26) &&
            settlement == (side == Side.Local ? 1 : QueryPlanChecks.EchoHeadStatements.Length),
            "B214-CR2: ordinary selection meets the measured Local gate and non-echo Dropbox-head budgets");
        // Replay the captured SELECTs against the unchanged synthetic store, using the existing VM probe.
        using var probe = new CostProbe(database);
        var steps = captured.Select((sql, index) =>
        {
            var name = QueryPlanChecks.EchoHeadStatements[index].Name;
            var cost = name == nameof(StateSchema.EchoResponsibilities) ? probe.Execute(sql) : probe.ExecuteFirst(sql);
            Console.WriteLine($"B214-CR3 side={side} unrelated={unrelated} statement={name} vm-steps={cost.VmSteps} sql={sql}");
            return cost.VmSteps;
        }).ToArray();
        var avoided = side == Side.Local ? 0 : probe.ExecuteFirst(StateSchema.WorkWatermark).VmSteps;
        Console.WriteLine($"B214-CR3 side={side} unrelated={unrelated} settlement-vm={steps.Sum()} avoided-watermark-vm={avoided} net-vm={steps.Sum() - avoided}");
        return steps;
    }

    private static void EchoSettledStatements(StateStore store, int count, bool deleted)
    {
        var source = InstallWithoutObligations(store);
        var bindings = Enumerable.Range(0, count).Select(index => EchoBinding($"A/{index:D3}")).ToArray();
        if (!deleted) store.Commit(PairId, StateChange.Empty with { Record = bindings });
        Acquire(store, source, NextCursor, bindings.Select(saved => deleted
            ? Notice(Side.Dropbox, saved.DropboxPath, null, kind: ChangeKind.Deleted) : EchoNotice(saved)));
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        (int Settled, bool More) result = default;
        var statements = Statements(store, () => result = store.SettleEchoes(PairId, source.PairGeneration,
            scope, SelectionObservation.SettlesDropboxEcho, count), $"deleted={deleted} rows={count}");
        Console.WriteLine($"B214-CR2 settled trace deleted={deleted} rows={count} statements={statements}");
        Check.Require(result == (count, true), "B214-CR2: the measured page retires only its exact echo rows");
        // Twelve application statements plus three implicit SQLite callbacks per row; eleven fixed page callbacks.
        Check.Require(statements == 11 + 15 * count,
            "B214-CR2: entry and tombstone pages meet the absolute trace budget, including retirement freshness");
    }

    private static void EchoCoalescingBoundary(StateStore store)
    {
        var source = InstallWithoutObligations(store);
        var newest = B210Acquire(store, B210Notice("Local/x"), B210Notice("Local/x"));
        var saved = EchoBinding("Remote/y");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        Acquire(store, source, NextCursor, [EchoNotice(saved)]);
        Check.Require(SettleEchoPage(store) == (0, false) && B210Count(store) == 2,
            "B214-RB4: echo walking stops at Local work without coalescing its rows");
        var ordinary = B210Next(store);
        Check.Require(ordinary?.AnchorWorkId == newest && B210Count(store) == 1,
            "B214-RB4: ordinary selection alone coalesces Local predecessors and preserves FIFO");
        Check.Require(SettleEchoPage(store) == (1, false) && B210Count(store) == 1 &&
            store.ReadWork(ordinary!).Single().Notice.Path == "Local/x",
            "B214-RB4: the following Dropbox echo settles without retiring the selected Local row");
    }

    private static void EchoStagedEarliest(StateStore store)
    {
        var source = InstallWithoutObligations(store);
        var first = EchoBinding("A/x");
        var third = EchoBinding("B/y");
        store.Commit(PairId, StateChange.Empty with { Record = [first, third] });
        var echo = EchoNotice(first);
        var through = Acquire(store, source, NextCursor,
            [echo, echo with { Observed = echo.Observed! with { ContentHash = "edited" } }, EchoNotice(third)]);
        AcquireLocal(store, source, Notice(Side.Local, "Y", "local-y", kind: ChangeKind.Renamed, oldPath: "OldY"));
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        Check.Require(SettleEchoPage(store) == (1, true),
            "B214-W1: a staged earliest row ends and commits the page rather than skipping its later edit");
        Check.Require(SettleEchoPage(store) == (0, false), "B214-W1: the edit is the next stopping candidate");
        var edit = store.SelectWork(PairId, source.PairGeneration, scope)!;
        Check.Require(edit.AnchorWorkId == through - 1 && store.ReadWork(edit).Single().Notice.Observed!.ContentHash == "edited",
            "B214-W1: exact echo retirement preserves the later real edit and selects it before Y");
        Check.Require(store.BeginSelection(edit) is not null, "B214-W1: the real edit begins in the same pass");
        Check.Require(SettleEchoPage(store) == (1, false), "B214-W1: unrelated echo J settles before Y in that pass");
        var move = store.SelectWork(PairId, source.PairGeneration, scope)!;
        Check.Require(move.Side == Side.Local && move.Identity == "local-y" && store.BeginSelection(move) is not null,
            "B214-W1: Y begins only after the real edit, without EndWorkPass");
        Check.Require(store.CountWork(PairId, source.PairGeneration, store.ReadWorkWatermark()) == (1L, 1L),
            "B214-W1: settlement retires only the two exact echoes");
        store.EndSelection(edit);
        store.EndSelection(move);
        store.EndWorkPass(PairId);
    }

    private static void EchoPageAccounting(StateStore store)
    {
        var source = InstallWithoutObligations(store);
        var bindings = Enumerable.Range(0, 10).SelectMany(folder => Enumerable.Range(0, 60)
            .Select(child => EchoBinding($"P{folder}/C{child:D3}", $"Q{folder}/C{child:D3}"))).ToArray();
        store.Commit(PairId, StateChange.Empty with { Record = bindings });
        Acquire(store, source, NextCursor, Enumerable.Range(0, 10)
            .Select(index => Notice(Side.Dropbox, "P" + index, null, kind: ChangeKind.Deleted)));
        // Page outcomes plus raw QP counts imply 120 examined rows per page; this is not direct real-store row instrumentation.
        for (var page = 0; page < 5; page++)
            Check.Require(SettleEchoPage(store) == (2, true),
                "B214-T15: two exhausted 60-row probes consume 122 units and end the page");
        Check.Require(SettleEchoPage(store) == (0, false) &&
            store.CountWork(PairId, source.PairGeneration, store.ReadWorkWatermark()) == (0L, 0L),
            "B214-T15: five committed pages empty the ten-row burst without an empty-progress loop");
        Check.Require(bindings.All(saved => store.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved),
            "B214-T15: tombstone settlement changes no binding");
    }

    private static void EchoTombstoneBinding(StateStore store, string shape)
    {
        var source = InstallWithoutObligations(store);
        var bindings = shape switch
        {
            "exact" => new[] { EchoBinding("P") },
            "inherited-child" => [EchoBinding("P/c")],
            "explicit-child" => [EchoBinding("Q/c", "P/c")],
            _ => Enumerable.Range(0, shape == "ten-overrides" ? 10 : shape == "last-inherited" ? 99 : 100)
                .Select(index => EchoBinding($"P/{index:D3}", $"Q/{index:D3}"))
                .Concat(shape is "last-inherited" or "late-inherited" ? [EchoBinding("P/Z")] : []).ToArray()
        };
        store.Commit(PairId, StateChange.Empty with { Record = bindings });
        var id = Acquire(store, source, NextCursor, [Notice(Side.Dropbox, "P", null, kind: ChangeKind.Deleted)]);
        var expected = shape == "ten-overrides" ? (1, false) : (0, false);
        Check.Require(SettleEchoPage(store) == expected, "B214-QP2/T5: bound or undecided remains ordinary: " + shape);
        Check.Require(bindings.All(saved => store.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved),
            "B214-T5: tombstone settlement never removes saved descendants: " + shape);
        if (expected.Item1 == 0)
            Check.Require(store.SelectWork(PairId, source.PairGeneration,
                new PathScope(store.Pair(PairId)!, store.Pairs()))!.AnchorWorkId == id,
                "B214-QP2: rejection wins even when the candidate exhausts the work budget: " + shape);
    }

    private static void EchoTrustGuard(StateStore store, string shape)
    {
        var provenance = InstallWithoutObligations(store);
        var saved = EchoBinding("A/x");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var id = Acquire(store, provenance, NextCursor, [EchoNotice(saved)]);
        if (shape == "discovery")
            store.RequireDiscovery(Selection(provenance, id, id, saved.DropboxId, saved.DropboxPath), id);
        else if (shape == "domain")
            store.SavePair(store.Pair(PairId)! with { Roots = null });
        else
        {
            var side = shape == "local-debt" ? Side.Local : Side.Dropbox;
            var current = store.ReadSource(PairId, side)!.Value;
            store.AdvanceSource(PairId, side, provenance.PairGeneration, current.Epoch, current.Revision,
                current.Provenance, current.Cursor, current.Epoch, current.Revision,
                shape == "provenance" ? null : current.Provenance,
                shape is "cursor" or "provenance" ? null : current.Cursor,
                shape.EndsWith("debt", StringComparison.Ordinal) ? SourceObligation.NormalRepair : null);
        }
        Check.Require(SettleEchoPage(store).Settled == 0 &&
            store.CountWork(PairId, store.ReadPairExecution(PairId)!.Value.Generation, store.ReadWorkWatermark()).Dropbox == 1,
            "B214-T8: an untrusted or discovery-required echo remains queued: " + shape);
    }

    private static void EchoSuspendedDependency(StateStore store)
    {
        var source = InstallWithoutObligations(store);
        var saved = EchoBinding("R");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var through = Acquire(store, source, NextCursor,
            [Notice(Side.Dropbox, "S", "s"), EchoNotice(saved)]);
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        var suspended = store.SelectWork(PairId, source.PairGeneration, scope)!;
        suspended = store.BeginSelection(suspended)!;
        var required = Selection(source, through, through, saved.DropboxId, saved.DropboxPath);
        store.EndSelection(suspended, required);
        Check.Require(store.WorkAttempted(suspended) && SettleEchoPage(store) == (0, false) &&
            store.ReadWork(required).Count() == 1, "B214-T14: an echo cannot retire a suspended selection's prerequisite");
        required = store.SelectWork(PairId, source.PairGeneration, scope)!;
        Check.Require(store.BeginSelection(required) is not null && store.CompleteWork(required, null) &&
            !store.WorkAttempted(suspended), "B214-T14: ordinary completion releases S within its live pass");
        store.EndWorkPass(PairId);
    }

    private static void EchoClaim(StateStore store, string shape)
    {
        var source = InstallWithoutObligations(store);
        var saved = EchoBinding(shape == "local-path" ? "A/x/child" : "B/y", "B/y");
        store.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var echoId = Acquire(store, source, NextCursor, [EchoNotice(saved)]);
        WorkSelection claim;
        if (shape == "local-path")
        {
            var domain = AcquireLocal(store, source, Notice(Side.Local, "A/x", "claim"));
            var local = store.ReadSource(PairId, Side.Local)!.Value;
            var id = store.ReadWorkWatermark();
            claim = new(PairId, source.PairGeneration, local.Epoch, Side.Local, domain, "claim", PathNames.Key("A/x"), id, id);
        }
        else
        {
            var path = shape == "identity" ? "A/x" : "B";
            var identity = shape == "identity" ? saved.DropboxId : "claim";
            var id = Acquire(store, source, LastCursor, [Notice(Side.Dropbox, path, identity)]);
            claim = Selection(source, id, id, identity, path);
        }
        // The claimed anchor follows the echo, so Earliest cannot mask an absent identity-claim guard.
        Check.Require(store.BeginSelection(claim) is not null,
            "B214-T9 setup: a distinct later anchor is live before the echo walk");
        Check.Require(SettleEchoPage(store).Settled == 0 &&
            store.ReadWork(Selection(source, echoId, echoId, saved.DropboxId, saved.DropboxPath)).Count() == 1,
            "B214-T9: live claims preserve the otherwise eligible echo: " + shape);
        Check.Require(store.CompleteWork(claim, null), "B214-T9: the claim completes only its own row");
        store.EndWorkPass(PairId);
        Check.Require(SettleEchoPage(store).Settled == 1,
            "B214-T9 control: after the claim completes the same echo settles: " + shape);
    }

    private static void EchoRollback(StateStore store, string database)
    {
        var source = InstallWithoutObligations(store);
        var bindings = Enumerable.Range(0, 201).Select(index => EchoBinding($"X/{index:D3}")).ToArray();
        store.Commit(PairId, StateChange.Empty with { Record = bindings });
        var through = Acquire(store, source, NextCursor, bindings.Select(EchoNotice));
        var first = through - bindings.Length + 1;
        using var connection = new SqliteConnection("Data Source=" + database);
        StateStore.OpenConnection(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER echo_fault AFTER DELETE ON change_work WHEN OLD.work_id=" +
            (first + 49) + " BEGIN SELECT RAISE(ABORT,'echo-fixture'); END;";
        command.ExecuteNonQuery();
        ItemStoreMutationException? failure = null;
        try { SettleEchoPage(store); }
        catch (ItemStoreMutationException error) { failure = error; }
        Check.Require(failure?.Selection.AnchorWorkId == first + 49 &&
            store.CountWork(PairId, source.PairGeneration, store.ReadWorkWatermark()) == (0L, 201L),
            "B214-T6c: failure identifies row 50 and atomically restores every row of the first page");
        var next = store.SelectWork(PairId, source.PairGeneration, new PathScope(store.Pair(PairId)!, store.Pairs()));
        Check.Require(next?.AnchorWorkId == first && !store.EndWorkPass(PairId),
            "B214-T6c: rollback publishes neither After nor retirement progress");
        command.CommandText = "DROP TRIGGER echo_fault;";
        command.ExecuteNonQuery();
        Check.Require(SettleEchoPage(store) == (100, true) && SettleEchoPage(store) == (100, true) &&
            SettleEchoPage(store) == (1, false), "B214-T6c: a later opportunity settles every restored page");
    }
}
