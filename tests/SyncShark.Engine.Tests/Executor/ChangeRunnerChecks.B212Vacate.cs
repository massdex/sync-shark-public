using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-212 vacate witnesses through the checked entry (SPEC-B212-7 1.5, SPEC-B212-8 1-2, its addendum 1, 3.3 and 4): a
// folder X moved on side S from Old to New, where the destination binding B still names New on the other side O and
// its folder there is the occupant, while B's S identity is gone. The rename selection vacates B's occupant, then
// moves X's counterpart onto New.
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

    // VAC-exact (-7 9.1, W19) and VAC-exact-div (-8 2): the vacate freezes one Vacate operation over the occupant,
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

    // VAC-reuse (-7 9.1, -8 1): an active Ordinary operation frozen over the occupant itself (Ready, a whole plan) is
    // continued by the vacate through D6: no second freeze, one operation, and the rename lands. B's saved S path is
    // not the moved path: an Ordinary operation's own send check needs that counterpart path empty (EligibleAsync), so
    // with X at B's saved S path the continued operation is refused and Retained (report decision).
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

    // VAC-foreign (-8 1, addendum 1 and 3.3): an active Ordinary operation frozen at the destination root for another
    // folder id is never run by the vacate. D6 reads it once by id, nothing is frozen, nothing is sent and its custody
    // is unchanged; the rename refuses with a held ClashNameTaken naming the moved item and its saved binding. The
    // foreign operation's end (recovery Check: Retained) leaves the rename held and the notice unchanged, and the
    // notice's Rename and Replace answers are stale with no effect. Four runs: no captured continuation (u) and one
    // captured beforehand (c), each with the folder moved on either side.
    private static async Task VacateForeignAsync()
    {
        foreach (var captured in new[] { false, true })
        foreach (var moved in new[] { Side.Local, Side.Dropbox })
        {
            var label = "; captured=" + captured + "; moved=" + moved;
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var fixture = new Fixture(realStore: true);
            var c = VacateSetup(fixture, moved);
            var foreign = await FreezeVacateRootAsync(c, VacateForeign, guard.Token);
            var before = fixture.Inner.FolderRemoval(PairId, foreign);
            var ready = fixture.Inner.ReadyFolderEffects(PairId, foreign, -1, int.MaxValue).Count();
            var earlier = captured ? CaptureVacateClash(c) : null;
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
            Check.Require(reads == 1 && advances == 0 && fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
                VacateEffects(c) == 0 && VacateRows(fixture) == 1 &&
                (after.Active, after.Phase, after.Generation) == (before.Active, before.Phase, before.Generation) &&
                fixture.Inner.ReadyFolderEffects(PairId, foreign, -1, int.MaxValue).Count() == ready,
                "B212-VAC-foreign: D6 reads the foreign operation once and neither runs nor doubles it: reads=" + reads +
                "; advances=" + advances + "; effects=" + VacateEffects(c) + "; operations=" + VacateRows(fixture) +
                "; before=" + (before.Active, before.Phase, before.Generation) +
                "; after=" + (after.Active, after.Phase, after.Generation) + label);
            var clash = fixture.Inner.Issues(PairId).SingleOrDefault(issue => issue.Kind == IssueKind.NameClash);
            IssueItem[] items = [new(moved, VacateNew, VacateMoved), new(Side.Local, c.Incoming.LocalPath, c.Incoming.LocalId),
                new(Side.Dropbox, c.Incoming.DropboxPath, c.Incoming.DropboxId)];
            Check.Require(clash is { Cause: TextKey.ClashNameTaken, Path: VacateNew } &&
                clash.Subject.Incoming == moved && clash.Subject.Items.Count == items.Length &&
                items.All(item => clash.Subject.Items.Contains(item)) &&
                (earlier is null || (clash.Id == earlier.Id && !clash.Matches(earlier) && clash.At > earlier.At)),
                "B212-VAC-foreign: the refusal raises the moved item's ClashNameTaken at New: clash=" + clash +
                "; items=" + (clash is null ? "" : string.Join(",", clash.Subject.Items)) + label);
            var open = clash!;
            var picked = await VacateSelectedAsync(c, guard.Token);
            Check.Require(picked.Length == 0,
                "B212-VAC-foreign: the clash holds the rename: selected=" + picked + "; clash=" +
                fixture.Inner.IssueById(PairId, open.Id) + label);
            var end = await LiveSyncer(fixture, c.Selection, new Boundary(_ => new Lease()), []).RecoverAsync(
                new RecoveryTarget(foreign, after.Generation, null, null), RecoveryChoice.Check, guard.Token)
                .WaitAsync(guard.Token);
            picked = await VacateSelectedAsync(c, guard.Token);
            var current = fixture.Inner.IssueById(PairId, open.Id);
            Check.Require(end.Outcome == RemovalOutcome.Retained && !fixture.Inner.FolderRemoval(PairId, foreign).Active &&
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
                Check.Require(refused == TextKey.ResolveSubjectStale && VacateEffects(c) == 0 &&
                    fixture.Inner.IssueById(PairId, open.Id) is { } unchanged && unchanged.Matches(open),
                    "B212-VAC-foreign: " + choice + " is stale with no effect: refused=" + refused + label);
            }
        }
    }

    // VAC-gone (-8 1, addendum 4): the captured occupant is absent and no linked operation exists, so the vacate
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

    // VAC-gone-prior (addendum 4): the prior linked operation runs before the absent-occupant guard. A first direct
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

    // T9r-c2 (iv) (-8 4, addendum 2): the bound Local folder X moved from Old to New, where an unbound Dropbox folder O
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
        var continuation = fixture.Inner.ReadRenameContinuation(begun);
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
            "; rows=" + VacateRows(fixture) + "; clash=" + fixture.Inner.IssueById(PairId, clash.Id);

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
        // Partial first (addendum 2 step 4): both queued joins ran, and the operation is still active and Submitted.
        Check.Require(b.Refused is null && jobs.Observations == 5 && jobs.Script.Count == 0 &&
            Operations() is [{ Active: true, Phase: FolderRemovalPhase.Submitted }] && b.Outcome is { } partial &&
            !partial.Outcome.Succeeded(),
            "B212-T9r-c2 (iv): the re-issued choice continues the one operation, still Partial" + Trace("B", b));
        Check.Require(fixture.Inner.IssueById(PairId, clash.Id) is { } held && held.Matches(clash) &&
            fixture.Inner.ReadRenameContinuation(begun) == continuation &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) != (0, 0) &&
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

    // An Ordinary operation already frozen at the destination root on O, as an earlier opportunity leaves one: its
    // frozen root id is the occupant's (reuse) or another folder's (foreign). It stays active and Ready.
    private static async Task<string> FreezeVacateRootAsync(VacateCase c, string rootId, CancellationToken ct)
    {
        var other = PathNames.Other(c.Moved);
        using var plan = await new FolderPlanBuilder(c.Fixture.Context(c.Selection)).BuildAsync(Row.ForOneSide(other,
            c.Destination.LocalKey, FolderEntry(VacateNew, rootId), c.Destination, [], null, RowContext.Neutral), other,
            new HashSet<string>(), live: true, ct);
        return c.Fixture.Inner.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Inferred,
            c.Fixture.Settings.StoragePageSize, ct);
    }

    // Variant c's earlier capture: a ClashNameTaken at New naming the moved item and the occupant, registered as the
    // rename's continuation the way the occupied-name refusal records one.
    private static Issue CaptureVacateClash(VacateCase c)
    {
        var subject = IssueSubject.FromDistinct([new IssueItem(c.Moved, VacateNew, VacateMoved),
            new IssueItem(PathNames.Other(c.Moved), VacateNew, VacateOccupant)], c.Moved);
        c.Fixture.Inner.Commit(PairId, StateChange.Empty with
            { Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, VacateNew, subject)] });
        var clash = c.Fixture.Inner.Issues(PairId).Single(issue => issue.Kind == IssueKind.NameClash);
        var begun = c.Fixture.Inner.BeginSelection(c.Selection) ?? c.Selection;
        c.Fixture.Inner.RegisterRenameContinuation(begun, c.Incoming, c.Destination, clash);
        c.Fixture.Inner.EndSelection(begun);
        c.Fixture.Inner.EndWorkPass(PairId);
        return clash;
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

    // One opportunity; the names of the store calls that handed back the rename's work W (addendum 3.1's hold).
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
    /// effect, and whose job reads follow a script, one entry per observation (the -8 addendum 2 join script).</summary>
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
