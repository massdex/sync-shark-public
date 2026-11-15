using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-212 vacate witnesses through the checked entry: a folder X moved on side S from Old to New, where the
// destination binding B still names New on the other side O and its folder there is the occupant, while B's S
// identity is gone. The rename selection vacates B's occupant, then moves X's counterpart onto New.
public static partial class ChangeRunnerChecks
{
    private const string VacateOld = "Old";
    private const string VacateNew = "New";
    private const string VacateElsewhere = "Elsewhere";
    private const string VacateMoved = "x-moved";
    private const string VacateCounterpart = "x-other";
    private const string VacateGoneId = "b-moved";
    private const string VacateOccupant = "b-other";
    private const string VacateForeign = "foreign-other";

    private sealed record VacateCase(Fixture Fixture, Side Moved, WorkSelection Selection, SyncedEntry Incoming,
        SyncedEntry Destination, Entry Arrived, CountingSide Local, CountingSide Dropbox, ScriptedJobSide Jobs,
        List<string> MovedReads);

    // VAC-exact and VAC-exact-div: the vacate freezes one Vacate operation over the occupant,
    // linked to the rename's work, with SourceEvidence the incoming folder read at its moved path; it completes and
    // the rename lands. In -div, B's saved S path differs from the moved path and holds nothing, and it is never read.
    private static async Task VacateExactAsync()
    {
        foreach (var divergent in new[] { false, true })
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var c = VacateSetup(fixture, moved, divergent ? VacateElsewhere : VacateNew);
            var frozen = new List<(string Id, WorkSelection? Link)>();
            fixture.Hooks.AfterCall = (name, args, result) =>
            {
                if (name == nameof(IStateStore.BeginFolderRemoval))
                    lock (frozen) frozen.Add(((string)result!, args.OfType<WorkSelection>().FirstOrDefault()));
            };
            var opportunities = await VacateUntilLandedAsync(c, guard.Token);
            fixture.Hooks.AfterCall = null;
            var operation = frozen.Count == 1 ? fixture.Inner.FolderRemoval(PairId, frozen[0].Id) : null;
            Check.Require(VacateLanded(c) && frozen.Count == 1 && frozen[0].Link is { } link &&
                link.AnchorWorkId == c.Selection.AnchorWorkId &&
                operation is { Phase: FolderRemovalPhase.Complete, Authority: FolderRemovalAuthority.Vacate,
                    RootEntry: { Path: VacateNew, Id: VacateOccupant } } &&
                operation.SourceEvidence == c.Arrived && operation.DeletingSide == PathNames.Other(moved) &&
                VacateDeleted(c) == 1 &&
                !c.MovedReads.Contains(VacateElsewhere, StringComparer.Ordinal),
                "B212-VAC-exact: one Vacate operation over the occupant, linked to the rename, with the incoming folder " +
                "as its source evidence; the rename lands: divergent=" + divergent + "; moved=" + moved +
                "; opportunities=" + opportunities + "; frozen=" + frozen.Count + "; operation=" + operation +
                "; deleted=" + VacateDeleted(c) + "; movedReads=" + string.Join(",", c.MovedReads));
        }
    }

    // VAC-reuse: an active Ordinary operation frozen over the occupant itself (Ready, a whole plan) is continued by
    // the vacate through its unfinished operation: no second freeze, one operation, and the rename lands. B's saved S path is not the moved
    // path, so the operation's premise (nothing at that path) still holds: the divergent witness of the supersede
    // exception. At the same path the operation is superseded instead (VAC-supersede).
    private static async Task VacateReuseAsync()
    {
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
            await VacateReuseAsync(moved);
    }

    private static async Task VacateReuseAsync(Side moved)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, moved, VacateElsewhere);
        var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token);
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var opportunities = await VacateUntilLandedAsync(c, guard.Token);
        Check.Require(VacateLanded(c) && fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
            VacateDeleted(c) == 1 && VacateRows(fixture) == 1 &&
            fixture.Inner.FolderRemoval(PairId, existing) is { Phase: FolderRemovalPhase.Complete },
            "B212-VAC-reuse: the vacate continues the occupant's own operation: opportunities=" + opportunities +
            "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun) +
            "; deleted=" + VacateDeleted(c) + "; operations=" + VacateRows(fixture) +
            "; operation=" + fixture.Inner.FolderRemoval(PairId, existing) + "; moved=" + moved);
    }

    // VAC-supersede (the unfinished-operation rule's one exception): B's saved S path is the moved path, so the unsent Inferred operation
    // over the occupant recorded X absent where the vacate observes it. Once the vacate's own plan is accepted, that
    // operation retires NoEffect with no notice, the vacate freezes its own operation, and the rename lands.
    private static async Task VacateSupersedeAsync()
    {
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var c = VacateSetup(fixture, moved);
            var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token);
            var frozen = new List<(string Id, WorkSelection? Link)>();
            fixture.Hooks.AfterCall = (name, args, result) =>
            {
                if (name == nameof(IStateStore.BeginFolderRemoval))
                    lock (frozen) frozen.Add(((string)result!, args.OfType<WorkSelection>().FirstOrDefault()));
            };
            var opportunities = await VacateUntilLandedAsync(c, guard.Token);
            fixture.Hooks.AfterCall = null;
            var old = fixture.Inner.FolderRemoval(PairId, existing);
            var operation = frozen.Count == 1 ? fixture.Inner.FolderRemoval(PairId, frozen[0].Id) : null;
            var notices = fixture.Inner.Issues(PairId).Select(issue => issue.Cause).ToArray();
            Check.Require(old is { Active: false, Phase: FolderRemovalPhase.Retained,
                    RetirementDisposition: FolderRetirementDisposition.NoEffect, Cause: null } &&
                frozen.Count == 1 && frozen[0].Link is { } link && link.AnchorWorkId == c.Selection.AnchorWorkId &&
                operation is { Phase: FolderRemovalPhase.Complete, Authority: FolderRemovalAuthority.Vacate,
                    SourceEvidence.Id: VacateMoved } &&
                VacateDeleted(c) == 1 && VacateRows(fixture) == 2 && VacateLanded(c) &&
                !notices.Contains(TextKey.FailChanged) && !notices.Contains(TextKey.ClashNameTaken),
                "B212-VAC-supersede: the old operation retires NoEffect with no notice and the vacate's own operation " +
                "lands the rename: moved=" + moved + "; opportunities=" + opportunities + "; old=" + old +
                "; frozen=" + frozen.Count + "; operation=" + operation + "; deleted=" + VacateDeleted(c) +
                "; operations=" + VacateRows(fixture) + "; notices=" + string.Join(",", notices));
        }
    }

    // VAC-reuse-armed (1.6, fact 9's crash window): the same-path Inferred operation's root effect is armed through the
    // store with its phase left Prepared, so FolderRetirement.For cannot prove it unsent and the vacate continues that
    // operation; its own lost-receipt handling decides the outcome, which is recorded. No retirement, no second freeze.
    private static async Task VacateReuseArmedAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, Side.Local);
        var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token);
        var root = fixture.Inner.ReadyFolderEffects(PairId, existing, -1, fixture.Settings.StoragePageSize).ToArray();
        fixture.Inner.ArmFolderSubmission(PairId, root);
        var armed = fixture.Inner.FolderRemoval(PairId, existing);
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
        Exception? failed = null;
        try { await VacateOpportunityAsync(c, guard.Token); }
        catch (Exception error) { failed = error; }
        var outcome = "operation=" + fixture.Inner.FolderRemoval(PairId, existing) + "; failed=" + failed?.GetType().Name +
            "; effects=" + VacateEffects(c) + "; landed=" + VacateLanded(c) + "; notices=" +
            string.Join(",", fixture.Inner.Issues(PairId).Select(issue => issue.Kind + ":" + issue.Cause + ":" + issue.Path));
        Check.Require(root is [{ ItemOrdinal: 0 }] && armed is { Active: true, Phase: FolderRemovalPhase.Prepared } &&
            fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) == retired &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun,
            "B212-VAC-reuse-armed: an armed root effect keeps the operation, which is continued: armed=" + armed +
            "; retirements=" + (fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired) +
            "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun) + "; " + outcome);
        Console.WriteLine("B212-VAC-reuse-armed outcome: " + outcome);
    }

    // VAC-reuse-evidence (1.6, M-B86): the same-path Inferred operation was frozen from a two-sided row, so its
    // SourceEvidence is X at New; rule G does not apply, the vacate continues it, and its own send check passes.
    private static async Task VacateReuseEvidenceAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, Side.Local);
        var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token, counterpart: c.Arrived);
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var opportunities = await VacateUntilLandedAsync(c, guard.Token);
        var operation = fixture.Inner.FolderRemoval(PairId, existing);
        Check.Require(operation is { Phase: FolderRemovalPhase.Complete } && operation.SourceEvidence == c.Arrived &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun && VacateDeleted(c) == 1 &&
            VacateRows(fixture) == 1 && VacateLanded(c),
            "B212-VAC-reuse-evidence: an operation that recorded X present is continued and lands: opportunities=" +
            opportunities + "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun) +
            "; operation=" + operation + "; deleted=" + VacateDeleted(c) + "; operations=" + VacateRows(fixture));
    }

    // VAC-keep-chosen (1.6, M-B76): a same-path Chosen operation with null evidence is the user's authority and is
    // never superseded; the vacate continues it and its own send check concludes it as today.
    private static async Task VacateKeepChosenAsync()
    {
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var c = VacateSetup(fixture, moved);
            var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token, FolderRemovalAuthority.Chosen);
            var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
            await VacateOpportunityAsync(c, guard.Token);
            var operation = fixture.Inner.FolderRemoval(PairId, existing);
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
                operation is { Active: false, Phase: FolderRemovalPhase.Retained,
                    RetirementDisposition: FolderRetirementDisposition.NoEffect, Cause: TextKey.FailChanged },
                "B212-VAC-keep-chosen: the Chosen operation is continued and its own check concludes it: moved=" + moved +
                "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun) +
                "; operation=" + operation);
        }
    }

    // VAC-supersede-notwhole (1.6, M-B78): O gains an unsynced child after the freeze, so the vacate's plan is not
    // DeleteWhole and the plan acceptance refuses it; the old operation is never retired before acceptance, and the rename refuses with
    // the existing ClashNameTaken.
    private static async Task VacateSupersedeNotWholeAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, Side.Local);
        var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token);
        var before = fixture.Inner.FolderRemoval(PairId, existing);
        fixture.Dropbox.Seed(FolderEntry(PathNames.Child(VacateNew, "unsynced"), "unsynced-other"), null);
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
        await VacateOpportunityAsync(c, guard.Token);
        var after = fixture.Inner.FolderRemoval(PairId, existing);
        var clash = fixture.Inner.Issues(PairId).SingleOrDefault(issue => issue.Kind == IssueKind.NameClash);
        Check.Require(after is { Active: true, Phase: FolderRemovalPhase.Prepared } &&
            after.Generation == before.Generation &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
            fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) == retired &&
            clash is { Cause: TextKey.ClashNameTaken, Path: VacateNew },
            "B212-VAC-supersede-notwhole: a refused plan retires nothing: before=" + before + "; after=" + after +
            "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun) +
            "; retirements=" + (fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired) +
            "; clash=" + clash);
    }

    // VAC-supersede-gap-retry and -gap-restart: retire and begin are two store calls, so a begin
    // that fails after the retirement leaves the root with no operation. The first BeginFolderRemoval after the
    // retirement throws the live admission's own stale refusal, once. Opportunity 1 then leaves the old operation
    // retired and nothing outstanding but W; opportunity 2, on the same LiveSyncer (retry, M-B83: its unfinished map
    // must have forgotten the retired operation) or on a fresh one over the reopened store (restart), freezes the
    // vacate once more. The rename lands at most one opportunity later on the same instance, as after every vacate
    // (SelectionRenames.FinishRenameAsync holds it for the next selection; VacateUntilLandedAsync), with no more work.
    private static async Task VacateSupersedeGapAsync()
    {
        foreach (var restart in new[] { false, true })
        {
            var name = restart ? "B212-VAC-supersede-gap-restart" : "B212-VAC-supersede-gap-retry";
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var c = VacateSetup(fixture, Side.Local);
            var existing = await FreezeVacateRootAsync(c, VacateOccupant, guard.Token);
            var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
            var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
            var failed = 0;
            string? frozen = null;
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name != nameof(IStateStore.BeginFolderRemoval) || failed > 0 ||
                    fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) == retired) return;
                failed++;
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            };
            fixture.Hooks.AfterCall = (method, _, result) =>
            {
                if (method == nameof(IStateStore.BeginFolderRemoval)) frozen = (string)result!;
            };
            var syncer = LiveSyncer(fixture, c.Selection, new Boundary(_ => new Lease()), []);
            await syncer.SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            var old = fixture.Inner.FolderRemoval(PairId, existing);
            var notices = fixture.Inner.Issues(PairId).Select(issue => issue.Cause).ToArray();
            var work = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
            string Counts() => "; begun=" + (fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun) +
                "; retirements=" + (fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired) +
                "; failed=" + failed;
            Check.Require(failed == 1 && fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun == 1 &&
                fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired == 1 &&
                old is { Active: false, Phase: FolderRemovalPhase.Retained,
                    RetirementDisposition: FolderRetirementDisposition.NoEffect } &&
                !fixture.Inner.ActiveFolderRemovals(PairId, default).Any() &&
                !fixture.Inner.UnresolvedFolderRemovals(PairId, default).Any() &&
                VacateEffects(c) == 0 && !notices.Contains(TextKey.FailChanged) && !VacateLanded(c) && work != (0, 0),
                name + ": a begin refused after the retirement leaves no operation and W pending: old=" + old + Counts() +
                "; effects=" + VacateEffects(c) + "; notices=" + string.Join(",", notices) + "; work=" + work);
            fixture.Hooks.BeforeCall = null;
            if (restart)
            {
                fixture.RestartStore();
                syncer = LiveSyncer(fixture, c.Selection, new Boundary(_ => new Lease()), []);
            }
            Exception? second = null;
            var opportunities = 1;
            try
            {
                while (opportunities < 3 && !VacateLanded(c))
                {
                    opportunities++;
                    await syncer.SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
                }
            }
            catch (Exception error) { second = error; }
            fixture.Hooks.AfterCall = null;
            var operation = frozen is null ? null : fixture.Inner.FolderRemoval(PairId, frozen);
            Check.Require(second is null && fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun == 2 &&
                fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired == 1 &&
                operation is { Phase: FolderRemovalPhase.Complete, Authority: FolderRemovalAuthority.Vacate } &&
                VacateDeleted(c) == 1 && VacateRows(fixture) == 2 && VacateLanded(c),
                name + ": the next opportunity freezes the vacate once more and the rename lands: second=" +
                second?.Message + Counts() + "; operation=" + operation + "; deleted=" + VacateDeleted(c) +
                "; operations=" + VacateRows(fixture) + "; landed=" + VacateLanded(c) + " after opportunity " +
                opportunities);
            Console.WriteLine(name + ": landed after opportunity " + opportunities);
        }
    }

    // VAC-supersede-cost. Executed SQL is every statement SQLite started on
    // the store's main connection (SqlStatementsAsync): BEGIN, COMMIT and empty probes included. Not covered: the
    // folder-plan scratch connection (the plan build and the freeze's frozen-evidence pages), read-only connections and
    // in-statement work (VM steps, rows, path-key calls). M is the old operation's member rows: the root and M - 1 bound
    // child folders under O at New on both sides (C = 0). P is StoragePageSize; M1 = 2P + 1 puts full pages, a partial
    // page and the empty probe in each size, and M2 = 4 * M1 tells linear growth (4x) from a per-member re-walk (16x).
    // The phases call their owners directly; (a) the supersede, (b) no old operation and (c) today's same-path
    // continuation (a Chosen operation with null evidence) each trace one whole vacate opportunity. Time and bytes come
    // only from untraced repeats after a warm-up on an equal operation.
    private static async Task VacateSupersedeCostAsync()
    {
        const int unrelated = 8;
        var failures = new List<Exception>();
        void Expect(bool ok, string what)
        {
            if (!ok) failures.Add(new InvalidOperationException(what));
        }
        int pageSize;
        using (var sizing = new Fixture()) pageSize = sizing.Settings.StoragePageSize;
        int Pages(int rows) => (rows + pageSize - 1) / pageSize;
        int[] sizes = [2 * pageSize + 1, 4 * (2 * pageSize + 1)];
        var phases = new Dictionary<int, (int Read, int Retire, int Freeze)>();
        foreach (var members in sizes)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var built = await VacateCostCaseAsync(members, unrelated, FolderRemovalAuthority.Inferred, guard.Token);
            using var fixture = built.Fixture;
            var c = built.Case;
            // The traced operation, then two equal ones: the untraced warm-up and the timed repeat.
            string[] olds = [built.Old!, await FreezeVacateRootAsync(c, VacateOccupant, guard.Token),
                await FreezeVacateRootAsync(c, VacateOccupant, guard.Token)];
            var rename = fixture.Inner.BeginSelection(c.Selection) ?? c.Selection;
            fixture.Inner.RegisterRenameContinuation(rename, c.Incoming, c.Destination, null);
            var rows = olds.Select(id => Recovery.Members(fixture.Inner, PairId, id, pageSize).Count()).ToArray();
            var traced = await VacatePhasesAsync(c, olds[0], rename, async phase =>
                (await SqlStatementsAsync(fixture, () =>
                {
                    phase();
                    return Task.CompletedTask;
                })).Total, guard.Token);
            phases[members] = (traced.Read, traced.Retire, traced.Freeze);
            var expected = (Read: 3 + 2 * (Pages(members) + 1), Retire: members + 7,
                Freeze: members + Pages(members - 1) + 10);
            Expect(rows.All(count => count == members) && traced.Supersedable && phases[members] == expected,
                "B212-VAC-supersede-cost phases: M=" + members + "; rows=" + string.Join(",", rows) + "; supersedable=" +
                traced.Supersedable + "; executed (read, retire, freeze)=" + phases[members] + "; recipe=" + expected);
            await VacatePhasesAsync(c, olds[1], rename, phase => Task.FromResult(Untraced(phase)), guard.Token);
            var timed = await VacatePhasesAsync(c, olds[2], rename, phase => Task.FromResult(Untraced(phase)), guard.Token);
            Console.WriteLine("B212-VAC-supersede-cost M=" + members + " P=" + pageSize + " executed-sql read=" + traced.Read +
                " retire=" + traced.Retire + " freeze=" + traced.Freeze + "; untraced (ms, bytes) read=" + timed.Read +
                " retire=" + timed.Retire + " freeze=" + timed.Freeze);
        }
        var (small, large) = (sizes[0], sizes[1]);
        if (phases.Count == sizes.Length)
            Expect(phases[large].Read - phases[small].Read == 2 * (Pages(large) - Pages(small)) &&
                phases[large].Retire - phases[small].Retire == large - small &&
                phases[large].Freeze - phases[small].Freeze == large - small + Pages(large - 1) - Pages(small - 1),
                "B212-VAC-supersede-cost growth M1->M2 (read, retire, freeze): " + phases[small] + " -> " + phases[large]);
        foreach (var members in sizes)
        {
            var walk = Pages(members) + 1;
            var supersede = await VacateTraceAsync(members, unrelated, FolderRemovalAuthority.Inferred);
            var fresh = await VacateTraceAsync(members, unrelated, null);
            var continued = await VacateTraceAsync(members, unrelated, FolderRemovalAuthority.Chosen);
            var identity = phases.TryGetValue(members, out var phase) ? phase.Read + phase.Retire + 1 : -1;
            Expect(supersede.Retired == 1 && supersede.Begun == 1 && supersede.Items == walk && supersede.Ready == 0 &&
                fresh.Retired == 0 && fresh.Begun == 1 && supersede.Sql - fresh.Sql == identity,
                "B212-VAC-supersede-cost (a) - (b) = read + retire + 1 (the map load's read of the old operation): M=" +
                members + "; (a)=" + supersede + "; (b)=" + fresh + "; expected difference=" + identity);
            Expect(continued.Items == 4 * walk && continued.Ready == 4 && continued.Eligibility == 4 &&
                continued.Retired == 1 && continued.Begun == 0,
                "B212-VAC-supersede-cost (c) ledger: 4 member walks, 4 ready-effect pages, 4 eligibility reads, one " +
                "retirement and no freeze: M=" + members + "; (c)=" + continued);
            Console.WriteLine("B212-VAC-supersede-cost M=" + members + " executed-sql (a)=" + supersede.Sql + " (b)=" +
                fresh.Sql + " (c)=" + continued.Sql + " (c)-(a)=" + (continued.Sql - supersede.Sql) +
                "; (a) calls " + supersede.Calls + "; (b) calls " + fresh.Calls + "; (c) " + continued);
            if (members != small) continue;
            var wider = await VacateTraceAsync(members, 4 * unrelated, FolderRemovalAuthority.Inferred);
            Expect(wider.Sql == supersede.Sql && wider.Calls == supersede.Calls,
                "B212-VAC-supersede-cost (a) at " + unrelated + " and " + 4 * unrelated + " unrelated bindings: sql " +
                supersede.Sql + " vs " + wider.Sql + "; calls " + supersede.Calls + " vs " + wider.Calls);
            Console.WriteLine("B212-VAC-supersede-cost (a) M=" + members + " unrelated=" + 4 * unrelated +
                " executed-sql=" + wider.Sql);
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    // VAC-foreign: an active Ordinary operation frozen at the destination root for another folder id is never run by
    // the vacate. The unfinished-operation check reads it once by id, nothing is frozen, nothing is sent and its custody
    // is unchanged; the rename refuses with a ClashNameTaken naming the moved item and its saved binding. The refusing
    // selection leaves W, so the next opportunity hands W back once and completes it under the clash (2 provider calls,
    // no effect); the one after selects nothing. The foreign operation's end (recovery Check: Retained, retired
    // NoEffect with FailChanged) leaves the rename held and the notice unchanged, and the notice's Rename and
    // Replace answers are stale with no effect. Two runs, the folder moved on either side.
    private static async Task VacateForeignAsync()
    {
        // Every run and step reports, so one cycle shows the whole case space (standard 4.1); any miss fails the witness.
        var failures = new List<Exception>();
        void Expect(bool ok, string what)
        {
            if (!ok) failures.Add(new InvalidOperationException(what));
        }
        static int Times(string picked, string name) => picked.Split(',').Count(call => call == name);
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
        {
            var label = "; moved=" + moved;
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var c = VacateSetup(fixture, moved);
            var foreign = await FreezeVacateRootAsync(c, VacateForeign, guard.Token);
            var before = fixture.Inner.FolderRemoval(PairId, foreign);
            var ready = fixture.Inner.ReadyFolderEffects(PairId, foreign, -1, int.MaxValue).Count();
            var (reads, advances) = (0, 0);
            fixture.Hooks.BeforeCallWith = (method, args) =>
            {
                if (args.Length < 2 || args[1] as string != foreign) return;
                if (method.Name == nameof(IStateStore.FolderRemoval)) Interlocked.Increment(ref reads);
                if (method.Name == nameof(IStateStore.AdvanceFolderRemoval)) Interlocked.Increment(ref advances);
            };
            var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
            await VacateOpportunityAsync(c, guard.Token);
            fixture.Hooks.BeforeCallWith = null;
            var after = fixture.Inner.FolderRemoval(PairId, foreign);
            Expect(reads == 1 && advances == 0 && fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
                VacateEffects(c) == 0 && VacateRows(fixture) == 1 &&
                (after.Active, after.Phase, after.Generation) == (before.Active, before.Phase, before.Generation) &&
                fixture.Inner.ReadyFolderEffects(PairId, foreign, -1, int.MaxValue).Count() == ready,
                "B212-VAC-foreign: the vacate reads the foreign operation once and neither runs nor doubles it: reads=" + reads +
                "; advances=" + advances + "; effects=" + VacateEffects(c) + "; operations=" + VacateRows(fixture) +
                "; before=" + (before.Active, before.Phase, before.Generation) +
                "; after=" + (after.Active, after.Phase, after.Generation) + label);
            var clash = fixture.Inner.Issues(PairId).SingleOrDefault(issue => issue.Kind == IssueKind.NameClash);
            IssueItem[] items = [new(moved, VacateNew, VacateMoved), new(Side.Local, c.Incoming.LocalPath, c.Incoming.LocalId),
                new(Side.Dropbox, c.Incoming.DropboxPath, c.Incoming.DropboxId)];
            Expect(clash is { Cause: TextKey.ClashNameTaken, Path: VacateNew } &&
                clash.Subject.Incoming == moved && clash.Subject.Items.Count == items.Length &&
                items.All(item => clash.Subject.Items.Contains(item)),
                "B212-VAC-foreign: the refusal raises the moved item's ClashNameTaken at New: clash=" + clash +
                "; items=" + (clash is null ? "" : string.Join(",", clash.Subject.Items)) + label);
            if (clash is null) continue;
            var open = clash;
            var calls = VacateCalls(c);
            var picked = await VacateSelectedAsync(c, guard.Token);
            var first = VacateCalls(c) - calls;
            var held = fixture.Inner.IssueById(PairId, open.Id);
            var again = await VacateSelectedAsync(c, guard.Token);
            var second = VacateCalls(c) - calls - first;
            Expect(Times(picked, nameof(IStateStore.SelectWork)) <= 1 &&
                Times(picked, nameof(IStateStore.BeginSelection)) <= 1 && first == 2 && VacateEffects(c) == 0 &&
                held is not null && held.Matches(open) && again.Length == 0 && second == 0,
                "B212-VAC-foreign: the next opportunity completes W once under the clash and the one after selects " +
                "nothing: selected=" + picked + " then " + again + "; provider calls=" + first + " then " + second +
                "; effects=" + VacateEffects(c) + "; clash=" + held + label);
            var end = await LiveSyncer(fixture, c.Selection, new Boundary(_ => new Lease()), []).RecoverAsync(
                new RecoveryTarget(foreign, after.Generation, null, null), RecoveryChoice.Check, guard.Token)
                .WaitAsync(guard.Token);
            picked = await VacateSelectedAsync(c, guard.Token);
            var current = fixture.Inner.IssueById(PairId, open.Id);
            Expect(end.Outcome == RemovalOutcome.Retained && fixture.Inner.FolderRemoval(PairId, foreign) is
                    { Active: false, RetirementDisposition: FolderRetirementDisposition.NoEffect, Cause: TextKey.FailChanged } &&
                picked.Length == 0 &&
                current is not null && current.Matches(open) && VacateEffects(c) == 0 && VacateRows(fixture) == 1,
                "B212-VAC-foreign: the foreign operation's end leaves the rename held and the notice unchanged: end=" +
                end.Outcome + "; foreign=" + fixture.Inner.FolderRemoval(PairId, foreign) + "; selected=" + picked +
                "; current=" + current + label);
            foreach (var choice in new[] { ResolutionChoice.RenameIncoming, ResolutionChoice.ReplaceExisting })
            {
                TextKey? refused = null;
                try
                {
                    await LiveSyncer(fixture, c.Selection, new Boundary(_ => new Lease()), []).ResolveAsync(
                        fixture.Inner.IssueById(PairId, open.Id)!, choice,
                        choice == ResolutionChoice.RenameIncoming ? "Renamed" : null, null, guard.Token);
                }
                catch (EngineRequestException error) { refused = error.Key; }
                Expect(refused == TextKey.ResolveSubjectStale && VacateEffects(c) == 0 &&
                    fixture.Inner.IssueById(PairId, open.Id) is { } unchanged && unchanged.Matches(open),
                    "B212-VAC-foreign: " + choice + " is stale with no effect: refused=" + refused + label);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    // VAC-gone: the captured occupant is absent and no linked operation exists, so the vacate
    // returns false before the entry: ItemRenames' own capture read is the only provider call, and nothing is frozen.
    private static async Task VacateGoneAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, Side.Local);
        await fixture.Dropbox.DeleteAsync(FolderEntry(VacateNew, VacateOccupant), null, guard.Token);
        var linked = fixture.Inner.RenameRemoval(c.Selection);
        var renames = VacateRenames(c);
        var calls = VacateCalls(c);
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var removed = await renames.RemoveVacatedAsync(VacateCandidate(c), c.Destination, c.Selection, guard.Token)
            .WaitAsync(guard.Token);
        Check.Require(linked is null && !removed && VacateCalls(c) - calls == 1 && VacateEffects(c) == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun && VacateRows(fixture) == 0,
            "B212-VAC-gone: an absent occupant refuses before the entry with only the capture read: linked=" + linked +
            "; removed=" + removed + "; calls=" + (VacateCalls(c) - calls) + "; operations=" + VacateRows(fixture));
    }

    // VAC-gone-prior: the prior linked operation runs before the absent-occupant guard. A first direct
    // call freezes the vacate and submits it, with three refused joins, so it stays Submitted; the occupant then goes
    // (the job's effect) and the second call, with the occupant absent, continues that operation to Complete.
    private static async Task VacateGonePriorAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, Side.Local);
        var selection = fixture.Inner.BeginSelection(c.Selection) ?? c.Selection;
        // The rename's no-issue continuation over B, which the selection registers before a vacate freeze links to it.
        fixture.Inner.RegisterRenameContinuation(selection, c.Incoming, c.Destination, null);
        var renames = VacateRenames(c);
        c.Jobs.Accepting = true;
        for (var join = 0; join < 3; join++)
            c.Jobs.Script.Enqueue(new ItemFailedException(VacateNew, TextKey.DropboxRefused, null));
        var first = await renames.RemoveVacatedAsync(VacateCandidate(c), c.Destination, selection, guard.Token)
            .WaitAsync(guard.Token);
        var linked = fixture.Inner.RenameRemoval(selection);
        Check.Require(!first && linked is not null && c.Jobs.Observations == 3 && c.Jobs.Submissions == 1 &&
            fixture.Inner.FolderRemoval(PairId, linked) is { Active: true, Phase: FolderRemovalPhase.Submitted },
            "B212-VAC-gone-prior: the first call leaves one linked operation Submitted: first=" + first +
            "; linked=" + linked + "; observations=" + c.Jobs.Observations + "; submissions=" + c.Jobs.Submissions +
            "; operation=" + (linked is null ? null : fixture.Inner.FolderRemoval(PairId, linked)));
        var occupant = (await fixture.Dropbox.ReadOneAsync(VacateNew, guard.Token))!;
        await fixture.Dropbox.DeleteAsync(occupant, null, guard.Token);
        c.Jobs.Script.Enqueue(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, occupant)]));
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var second = await renames.RemoveVacatedAsync(VacateCandidate(c), c.Destination, selection, guard.Token)
            .WaitAsync(guard.Token);
        Check.Require(second && fixture.Inner.FolderRemoval(PairId, linked!) is { Phase: FolderRemovalPhase.Complete } &&
            c.Jobs.Observations == 4 && c.Jobs.Script.Count == 0 && c.Jobs.Submissions == 1 &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun && VacateRows(fixture) == 1,
            "B212-VAC-gone-prior: with the occupant gone, the prior operation is continued to Complete: second=" + second +
            "; operation=" + fixture.Inner.FolderRemoval(PairId, linked!) + "; observations=" + c.Jobs.Observations +
            "; unread=" + c.Jobs.Script.Count + "; operations=" + VacateRows(fixture));
    }

    // T9r-c2 (iv): the bound Local folder X moved from Old to New, where an unbound Dropbox folder O
    // occupies New, so its rename raises ClashNameTaken. replace-existing (B-216) removes O through one delete_batch
    // job. A: three refused joins (the run's and the two resumes'); the job stays Accepted, the operation Submitted.
    // O then goes (the job's server-side effect). B: the re-issued choice continues that operation, two refused joins,
    // so it is still Partial and the clash stays open with nothing released. C: the receipt completes it, the re-issue
    // closes the clash, and W's rename lands on the next opportunity.
    private static async Task ReplaceDeferredJobAsync()
    {
        const string occupantId = "o-occupant";
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: true);
        fixture.Inner.Commit(PairId, StateChange.Empty with
            { Record = [VacateBinding(Side.Local, (VacateOld, VacateMoved), (VacateOld, VacateCounterpart))] });
        var arrived = FolderEntry(VacateNew, VacateMoved);
        var occupant = FolderEntry(VacateNew, occupantId);
        fixture.Local.Seed(arrived, null);
        fixture.Dropbox.Seed(FolderEntry(VacateOld, VacateCounterpart), null);
        fixture.Dropbox.Seed(occupant, null);
        RepairDone(fixture);
        ClearObligations(fixture);
        var local = new CountingSide(fixture.Local);
        var dropbox = new CountingSide(fixture.Dropbox);
        var jobs = new ScriptedJobSide(dropbox);
        fixture.LocalView = local;
        fixture.DropboxView = jobs;
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, VacateNew, VacateOld, VacateMoved,
            arrived, false, false));
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(guard.Token)
            .WaitAsync(guard.Token);
        var raised = fixture.Inner.Issues(PairId).SingleOrDefault(issue => issue.Kind == IssueKind.NameClash);
        Check.Require(raised is { Cause: TextKey.ClashNameTaken, Path: VacateNew } && jobs.Submissions == 0,
            "B212-T9r-c2 (iv): the occupied rename raises ClashNameTaken at New: clash=" + raised + "; items=" +
            (raised is null ? "" : string.Join(",", raised.Subject.Items)));
        var clash = raised!;
        var begun = fixture.Hooks.BegunSelections.LastOrDefault() ?? selection;
        // W's state as the clash left it: its continuation (or a retired anchor) and its pending work count.
        string HeldState()
        {
            string anchor;
            try { anchor = fixture.Inner.ReadRenameContinuation(begun)?.ToString() ?? "none"; }
            catch (LiveAdmissionStaleException) { anchor = "anchor retired"; }
            return anchor + "; work=" + fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
        }
        var continuation = HeldState();
        var syncer = ReplaceSyncer(fixture, selection);
        async Task<(ResolutionOutcome? Outcome, TextKey? Refused)> ReplaceAsync()
        {
            try
            {
                return (await syncer.ResolveAsync(fixture.Inner.IssueById(PairId, clash.Id)!,
                    ResolutionChoice.ReplaceExisting, null, null, guard.Token).WaitAsync(guard.Token), null);
            }
            catch (EngineRequestException error) { return (null, error.Key); }
        }
        FolderRemovalState[] Operations() => fixture.Inner.UnresolvedFolderRemovals(PairId, default).ToArray();
        string Trace(string step, (ResolutionOutcome? Outcome, TextKey? Refused) answer) =>
            "; step=" + step + "; outcome=" + answer.Outcome + "; refused=" + answer.Refused + "; observations=" +
            jobs.Observations + "; unread=" + jobs.Script.Count + "; submissions=" + jobs.Submissions +
            "; operations=" + string.Join(" | ", Operations().Select(operation => operation.Phase + ":" + operation.Active)) +
            "; rows=" + VacateRows(fixture) + "; held=" + HeldState() + " (after the clash: " + continuation + ")" +
            "; clash=" + fixture.Inner.IssueById(PairId, clash.Id);

        jobs.Accepting = true;
        for (var join = 0; join < 3; join++)
            jobs.Script.Enqueue(new ItemFailedException(VacateNew, TextKey.DropboxRefused, null));
        var a = await ReplaceAsync();
        Check.Require(a.Refused is null && jobs.Observations == 3 && jobs.Script.Count == 0 && jobs.Submissions == 1 &&
            Operations() is [{ Active: true, Phase: FolderRemovalPhase.Submitted, Authority: FolderRemovalAuthority.Chosen }] &&
            fixture.Inner.IssueById(PairId, clash.Id) is not null,
            "B212-T9r-c2 (iv): the original choice leaves the job Accepted and the operation Submitted" + Trace("A", a));
        await fixture.Dropbox.DeleteAsync(occupant, null, guard.Token);

        for (var join = 0; join < 2; join++)
            jobs.Script.Enqueue(new ItemFailedException(VacateNew, TextKey.DropboxRefused, null));
        var b = await ReplaceAsync();
        // Partial first: both queued joins ran, and the operation is still active and Submitted.
        Check.Require(b.Refused is null && jobs.Observations == 5 && jobs.Script.Count == 0 &&
            Operations() is [{ Active: true, Phase: FolderRemovalPhase.Submitted }] && b.Outcome is { } partial &&
            !partial.Outcome.Succeeded(),
            "B212-T9r-c2 (iv): the re-issued choice continues the one operation, still Partial" + Trace("B", b));
        Check.Require(fixture.Inner.IssueById(PairId, clash.Id) is { } held && held.Matches(clash) &&
            HeldState() == continuation &&
            jobs.Submissions == 1 && VacateRows(fixture) == 1,
            "B212-T9r-c2 (iv): a Partial removal keeps the clash open and releases nothing" + Trace("B", b));

        jobs.Script.Enqueue(new FolderJobResult.Terminal([new FolderMemberResult.Receipt(0, occupant)]));
        var c = await ReplaceAsync();
        Check.Require(c.Refused is null && c.Outcome is { Resolved: true } && jobs.Observations == 6 &&
            jobs.Script.Count == 0 && Operations().Length == 0 && fixture.Inner.IssueById(PairId, clash.Id) is null &&
            jobs.Submissions == 1 && VacateRows(fixture) == 1,
            "B212-T9r-c2 (iv): the receipt completes the operation and the re-issue closes with release" + Trace("C", c));
        jobs.Accepting = false;
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(guard.Token)
            .WaitAsync(guard.Token);
        var landed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(VacateNew));
        Check.Require(landed is { LocalId: VacateMoved, DropboxId: VacateCounterpart } &&
            await fixture.Dropbox.ReadOneAsync(VacateNew, guard.Token) is { Id: VacateCounterpart },
            "B212-T9r-c2 (iv): the released rename lands on the next opportunity: landed=" + landed + Trace("W", c));
    }

    // A Syncer whose selected choices run under one permissive lease, so a resolution reaches its effects.
    private static Syncer ReplaceSyncer(Fixture fixture, WorkSelection selection)
    {
        var boundary = new Boundary(_ => new Lease());
        boundary.Capture = (chosen, _, _) => Task.FromResult(Coverage(chosen));
        var context = fixture.Context(selection) with
        {
            CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
        var ports = new SyncPorts(new RecoveryBoundary(), boundary, new AdmittedSelection(), 0);
        return new Syncer(context, ports with
        {
            RunBatchAsync = (work, side, scope, retries, settles, ct) =>
                SyncJobs.RunBatchAsync(context, ports, work, scope, ct, side, retries, settles)
        });
    }

    private sealed class AdmittedSelection : ISelectedExecutionBoundary
    {
        public async Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            await using var lease = new Lease();
            return await execute(lease, ct);
        }
    }

    // X = (Old on both sides) moved to New on S; B = (New, or the divergent saved S path, on S; New on O). S keeps X at
    // New and B's S identity nowhere, so the rename's destination identity is gone; O keeps X's counterpart at Old and
    // B's occupant at New. The moved side answers by-id reads and logs its single reads.
    private static VacateCase VacateSetup(Fixture fixture, Side moved, string savedMovedPath = VacateNew)
    {
        var incoming = VacateBinding(moved, (VacateOld, VacateMoved), (VacateOld, VacateCounterpart));
        var destination = VacateBinding(moved, (savedMovedPath, VacateGoneId), (VacateNew, VacateOccupant));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [incoming, destination] });
        var arrived = FolderEntry(VacateNew, VacateMoved);
        var (movedSide, otherSide) = moved == Side.Local ? (fixture.Local, fixture.Dropbox) : (fixture.Dropbox, fixture.Local);
        movedSide.Seed(arrived, null);
        otherSide.Seed(FolderEntry(VacateOld, VacateCounterpart), null);
        otherSide.Seed(FolderEntry(VacateNew, VacateOccupant), null);
        RepairDone(fixture);
        ClearObligations(fixture);
        var local = new CountingSide(fixture.Local);
        var dropbox = new CountingSide(fixture.Dropbox);
        var jobs = new ScriptedJobSide(dropbox);
        var movedReads = new List<string>();
        ISide Identity(ISide inner) => new IdentitySide(new ProxySide(inner)
            {
                OnRead = path =>
                {
                    lock (movedReads) movedReads.Add(path);
                }
            },
            (id, _) => Task.FromResult<(IdentityLocation Location, bool InScope)?>(id == VacateMoved
                ? (new IdentityLocation(VolumeKey, VacateNew, arrived), true) : ((IdentityLocation, bool)?)null));
        fixture.LocalView = moved == Side.Local ? Identity(local) : local;
        fixture.DropboxView = moved == Side.Dropbox ? Identity(jobs) : jobs;
        var notice = new ChangeNotice(moved, ChangeKind.Renamed, VacateNew, VacateOld, VacateMoved, arrived, false, false);
        var selection = moved == Side.Local ? Seed(fixture, notice) : SeedRemote(fixture, arrived, notice);
        return new VacateCase(fixture, moved, selection, incoming, destination, arrived, local, dropbox, jobs, movedReads);
    }

    private static SyncedEntry VacateBinding(Side moved, (string Path, string Id) onMoved, (string Path, string Id) onOther) =>
        moved == Side.Local
            ? SyncedEntries.From(FolderEntry(onMoved.Path, onMoved.Id), FolderEntry(onOther.Path, onOther.Id))
            : SyncedEntries.From(FolderEntry(onOther.Path, onOther.Id), FolderEntry(onMoved.Path, onMoved.Id));

    // An operation already frozen at the destination root on O, as an earlier opportunity leaves one: its frozen root id
    // is the occupant's (reuse) or another folder's (foreign). It stays active and Ready. An ordinary delete freezes it
    // Inferred with X absent (SourceEvidence null); authority and counterpart give the other recorded shapes.
    private static async Task<string> FreezeVacateRootAsync(VacateCase c, string rootId, CancellationToken ct,
        FolderRemovalAuthority authority = FolderRemovalAuthority.Inferred, Entry? counterpart = null)
    {
        var other = PathNames.Other(c.Moved);
        var row = Row.ForOneSide(other, c.Destination.LocalKey, FolderEntry(VacateNew, rootId), c.Destination, [], null,
            RowContext.Neutral);
        if (counterpart is not null) row = c.Moved == Side.Local ? row with { Local = counterpart } : row with { Dropbox = counterpart };
        using var plan = await new FolderPlanBuilder(c.Fixture.Context(c.Selection)).BuildAsync(row, other,
            new HashSet<string>(), live: true, ct);
        return c.Fixture.Inner.BeginFolderRemoval(PairId, plan, authority, c.Fixture.Settings.StoragePageSize, ct);
    }

    // The cost witness's shape: VAC-supersede's setup (moved Local, B's saved S path the moved path) plus members - 1
    // bound child folders under New on both sides, present on O, and unrelated synced files; old, when given, freezes
    // the occupant's operation with that authority.
    private static async Task<(Fixture Fixture, VacateCase Case, string? Old)> VacateCostCaseAsync(int members,
        int unrelated, FolderRemovalAuthority? old, CancellationToken ct)
    {
        var fixture = new Fixture(realStore: true);
        var c = VacateSetup(fixture, Side.Local);
        var children = Enumerable.Range(0, members - 1)
            .Select(index => (Path: PathNames.Child(VacateNew, "c" + index), Id: "child-other-" + index)).ToArray();
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = children.Select(child => SyncedEntries.From(FolderEntry(child.Path, child.Id + "-local"),
                    FolderEntry(child.Path, child.Id)))
                .Concat(Enumerable.Range(0, unrelated).Select(index => SyncedEntries.From(
                    LocalEntry("Unrelated/f" + index, "unrelated-local-" + index),
                    RemoteEntry("Unrelated/f" + index, "unrelated-dropbox-" + index)))).ToArray()
        });
        foreach (var child in children) fixture.Dropbox.Seed(FolderEntry(child.Path, child.Id), null);
        var existing = old is { } authority ? await FreezeVacateRootAsync(c, VacateOccupant, ct, authority) : null;
        return (fixture, c, existing);
    }

    // The supersede's three phases on one frozen operation, each by its own owner: the read (the unfinished operation
    // and Supersedes' member walk), the retirement, and the successor freeze of the vacate's accepted plan linked to the
    // rename work. Supersedable: the member walk found nothing sent and the vacate's plan is whole (it is accepted).
    private static async Task<(T Read, T Retire, T Freeze, bool Supersedable)> VacatePhasesAsync<T>(VacateCase c, string old,
        WorkSelection rename, Func<Action, Task<T>> measure, CancellationToken ct)
    {
        var store = c.Fixture.Inner;
        var pageSize = c.Fixture.Settings.StoragePageSize;
        FolderRemovalState? found = null;
        var unsent = false;
        var read = await measure(() =>
        {
            found = store.FolderRemoval(PairId, old);
            unsent = FolderRetirement.For(Recovery.Members(store, PairId, old, pageSize)) ==
                FolderRetirementDisposition.NoEffect;
        });
        var retire = await measure(() => store.RetireFolderRemoval(PairId, old, found!.Generation,
            FolderRetirementDisposition.NoEffect, StateChange.Empty));
        var observed = new Row(c.Destination.LocalKey, c.Arrived, FolderEntry(VacateNew, VacateOccupant), c.Destination, [],
            null, RowContext.Neutral);
        using var plan = await new FolderPlanBuilder(c.Fixture.Context(c.Selection)).BuildAsync(observed, Side.Dropbox,
            new HashSet<string>(), true, null, FolderRemovalAuthority.Vacate, ct);
        var freeze = await measure(() => store.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Vacate, pageSize, ct,
            rename));
        return (read, retire, freeze, unsent && plan.DeleteWhole);
    }

    // An untraced direct call's elapsed time and this thread's allocated bytes (the store calls are synchronous); with no
    // contention the time bounds the call's store-lock hold from above.
    private static (double Ms, long Bytes) Untraced(Action phase)
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        phase();
        return (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - bytes);
    }

    // One whole vacate opportunity under the SQL trace with its store calls by member. For an old operation: its member
    // pages, its ready-effect pages, and the provider reads from its first ready-effect page to its retirement, which
    // for a refused continuation are the prepared execution's and the continuation's EligibleAsync reads.
    private static async Task<(int Sql, string Calls, int Items, int Ready, int Eligibility, int Retired, int Begun)>
        VacateTraceAsync(int members, int unrelated, FolderRemovalAuthority? old)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var built = await VacateCostCaseAsync(members, unrelated, old, guard.Token);
        using var fixture = built.Fixture;
        var c = built.Case;
        var calls = new List<string>();
        var (items, ready) = (0, 0);
        int? first = null, retirement = null;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            lock (calls)
            {
                calls.Add(method.Name);
                if (built.Old is null || args.Length < 2 || args[1] as string != built.Old) return;
                if (method.Name == nameof(IStateStore.FolderRemovalItems)) items++;
                if (method.Name == nameof(IStateStore.ReadyFolderEffects))
                {
                    ready++;
                    first ??= c.Local.Reads + c.Dropbox.Reads;
                }
                if (method.Name == nameof(IStateStore.RetireFolderRemoval))
                    retirement ??= c.Local.Reads + c.Dropbox.Reads;
            }
        };
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
        var sql = await SqlStatementsAsync(fixture, () => VacateOpportunityAsync(c, guard.Token));
        fixture.Hooks.BeforeCallWith = null;
        var profile = string.Join(",", calls.GroupBy(name => name).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + "=" + group.Count()));
        return (sql.Total, profile, items, ready, retirement is { } at && first is { } start ? at - start : -1,
            fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired,
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun);
    }

    private static ItemRenames VacateRenames(VacateCase c)
    {
        var boundary = new Boundary(_ => new Lease());
        boundary.Capture = (chosen, _, _) => Task.FromResult(Coverage(chosen));
        var context = c.Fixture.Context(c.Selection) with
        {
            CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
        return SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0)).Items.Renames;
    }

    private static RenameCandidate VacateCandidate(VacateCase c) => new(c.Moved, c.Arrived, c.Incoming);

    // One opportunity; the names of the store calls that handed back the rename's work W (the clash's hold).
    private static async Task<string> VacateSelectedAsync(VacateCase c, CancellationToken ct)
    {
        var picked = new List<string>();
        c.Fixture.Hooks.AfterCall = (name, _, result) =>
        {
            if (name is nameof(IStateStore.SelectWork) or nameof(IStateStore.BeginSelection) &&
                result is WorkSelection chosen && chosen.AnchorWorkId == c.Selection.AnchorWorkId)
                lock (picked) picked.Add(name);
        };
        await VacateOpportunityAsync(c, ct);
        c.Fixture.Hooks.AfterCall = null;
        return string.Join(",", picked);
    }

    private static Task VacateOpportunityAsync(VacateCase c, CancellationToken ct) =>
        LiveSyncer(c.Fixture, c.Selection, new Boundary(_ => new Lease()), []).SyncWorkAsync(ct).WaitAsync(ct);

    // The vacate removes B, and the rename it unblocks may run in a later selection; at most two opportunities.
    private static async Task<int> VacateUntilLandedAsync(VacateCase c, CancellationToken ct)
    {
        var opportunities = 0;
        while (opportunities < 2 && !VacateLanded(c))
        {
            opportunities++;
            await VacateOpportunityAsync(c, ct);
        }
        return opportunities;
    }

    private static bool VacateLanded(VacateCase c) =>
        c.Fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(VacateNew)) is { } landed &&
        landed.IdOn(c.Moved) == VacateMoved && landed.IdOn(PathNames.Other(c.Moved)) == VacateCounterpart;

    private static int VacateDeleted(VacateCase c) => c.Moved == Side.Local ? c.Jobs.Submissions : c.Local.Deletes;

    private static int VacateEffects(VacateCase c) =>
        c.Jobs.Submissions + LocalEffects(c.Local) + c.Dropbox.Moves + c.Dropbox.Deletes + c.Dropbox.Ensures;

    private static int VacateCalls(VacateCase c) =>
        c.Local.Reads + c.Local.Listings + c.Dropbox.Reads + c.Dropbox.Listings + VacateEffects(c) + c.Jobs.Observations;

    private static long VacateRows(Fixture fixture)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM folder_removal;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>A Dropbox side whose folder-removal submissions, while accepting, become one job without the job's
    /// effect, and whose job reads follow a script, one entry per observation.</summary>
    private sealed class ScriptedJobSide(ISide inner) : ISide
    {
        public bool Accepting { get; set; }
        public Queue<object> Script { get; } = new();
        public int Submissions { get; private set; }
        public int Observations { get; private set; }
        public Side Side => inner.Side;

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            inner.ReadAllAsync(under, options, ct);

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path, content, source, ct);

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.DeleteAsync(expected, selection, ct);

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            Submissions++;
            return Accepting
                ? Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Accepted("job-" + Submissions))
                : inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct)
        {
            Observations++;
            var next = Script.Dequeue();
            return next is Exception error ? Task.FromException<FolderJobResult>(error) : Task.FromResult((FolderJobResult)next);
        }
    }
}
