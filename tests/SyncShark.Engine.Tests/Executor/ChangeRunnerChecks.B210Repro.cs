using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-210 measurement probe. Print-only: it asserts no verdict (the only assertions are setup sanity checks). One path
// holds several older pending works and a newer acquisition of the same item, the newer work held in an in-use retry
// wait like the live pair. Several finite selection passes run through the store's own ValidateLiveCoverage
// admission. Per selection it prints the Dropbox reads paid, the work id attempted, the refusal reason and whether
// the refusal came before the provider read (early: SelectionObservation.RefuseSuperseded) or after it (late:
// selection admission), and every RefuseSuperseded eligibility condition.
//
// Round 2: the "before" timings now seed the newer held work before the first selection and assert it exists; the
// diagnostic later-evidence probe runs once per variant, outside the measured attempts; and an identified shape is
// measured beside the path-only one (all old works carry the same file id as the newer acquisition), so the
// same-identity episode transfer (B1) can be seen in the refusal's newerEpisode.
public static partial class ChangeRunnerChecks
{
    public static void RunB210Repro() => B210ReproAsync().GetAwaiter().GetResult();

    private const string B210Path = "b210.health.json";
    private const int B210Passes = 3;
    private const int B210OldWorks = 3;

    // The seven RefuseSuperseded conditions, in source order (SelectionObservation.cs:67-72).
    private readonly record struct B210Gates(bool Notice, bool Original, bool Current, bool Synced, bool AtBaseline,
        bool Saved, bool Covers)
    {
        public bool All => Notice && Original && Current && Synced && !AtBaseline && Saved && !Covers;

        public override string ToString() =>
            $"gate[notice={Flag(Notice)} original={Flag(Original)} current={Flag(Current)} synced={Flag(Synced)} " +
            $"atBaseline={Flag(AtBaseline)} saved={Flag(Saved)} covers={Flag(Covers)} all={Flag(All)}]";

        private static string Flag(bool value) => value ? "T" : "f";
    }

    // The newer acquisition's position against the pass watermark: before the pass's first selection, after it, or
    // inside the Dropbox read that follows the early check (the read-granularity race).
    private static readonly string[] B210Timings = ["before", "during", "midread"];

    private static async Task B210ReproAsync()
    {
        foreach (var covering in new[] { false, true })
        foreach (var atBaseline in new[] { false, true })
        foreach (var withSaved in new[] { false, true })
        foreach (var timing in B210Timings)
            await B210VariantAsync(covering, atBaseline, withSaved, timing);
        // The notice shape itself: a Renamed notice in the same not-at-baseline, saved, during-pass shape.
        await B210VariantAsync(covering: false, atBaseline: false, withSaved: true, timing: "during", renamed: true);
        // The identified shape: every old work carries the same file id as the newer acquisition.
        foreach (var timing in B210Timings)
            await B210VariantAsync(covering: false, atBaseline: false, withSaved: true, timing, identified: true);
    }

    private static async Task B210VariantAsync(bool covering, bool atBaseline, bool withSaved, string timing,
        bool renamed = false, bool identified = false)
    {
        // The mid-read race only matters when every early gate holds, so the rest of the matrix stays at before/during.
        if (timing == "midread" && (covering || atBaseline)) return;
        var label = $"shape={(identified ? "id" : "path")} cover={(covering ? "T" : "f")} " +
            $"base={(atBaseline ? "T" : "f")} saved={(withSaved ? "T" : "f")} newer={timing} " +
            $"notice={(renamed ? "renamed" : "changed")}";
        var attempt = Guid.NewGuid();
        using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        ClearObligations(fixture);
        TrustEchoSources(fixture);
        var saved = LocalEntry(B210Path, LocalId);
        if (withSaved)
        {
            var remote = RemoteEntry(B210Path, DropboxId);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        }
        var atSaved = atBaseline && withSaved;
        var current = atSaved ? saved : saved with { Modified = Stamp.AddSeconds(1) };
        fixture.Local.Seed(current, [atSaved ? (byte)1 : (byte)2]);
        if (covering)
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, B210Path,
                    new IssueSubject([new IssueItem(Side.Local, B210Path, current.Id)], null))]
            });
        var oldWorks = new List<WorkSelection>();
        for (var index = 1; index <= B210OldWorks; index++)
            oldWorks.Add(Seed(fixture, new ChangeNotice(Side.Local,
                renamed ? ChangeKind.Renamed : ChangeKind.Changed, B210Path, null,
                identified ? current.Id : null, identified ? current : null, false, false), index));
        Console.WriteLine($"b210-variant start {label} oldWorks=[{string.Join(",", oldWorks.Select(work => work.AnchorWorkId))}] " +
            $"current={current.Id} savedBinding={(withSaved ? "yes" : "no")} " +
            $"localMatchesBaseline={Decide.LocalMatchesBaseline(current, fixture.Inner.SelectionBaseline(oldWorks[0]))}");
        var dropbox = new CountingSide(fixture.Dropbox);
        var newerSeeded = false;
        fixture.DropboxView = timing == "midread"
            ? new HookedSide(dropbox, beforeRead: path =>
            {
                if (newerSeeded || !PathNames.Same(path, B210Path)) return;
                SeedHeldNewerWork(fixture, current, attempt);
                newerSeeded = true;
                Console.WriteLine($"b210 variant {label} newer-work seeded inside the Dropbox read");
            })
            : dropbox;
        if (timing == "before")
        {
            var newer = SeedHeldNewerWork(fixture, current, attempt);
            newerSeeded = true;
            Check.Require(newer.AnchorWorkId > oldWorks[^1].AnchorWorkId &&
                fixture.Inner.Issues(PairId).Any(issue => issue.Cause == TextKey.FailLocalInUse),
                "B210 setup: the newer held work exists before the first selection: " + newer.AnchorWorkId);
            Console.WriteLine($"b210 variant {label} preflight newerWork={newer.AnchorWorkId} " +
                $"preflightLaterProbe={B210LaterProbe(fixture, oldWorks[0])}");
        }
        var selections = 0;
        var worksTried = new HashSet<long>();
        var (early, late, completed, unfinished, other, newerEpisodes) = (0, 0, 0, 0, 0, 0);
        foreach (var pass in Enumerable.Range(1, B210Passes))
        {
            var attempted = new List<long>();
            var reads = new List<int>();
            while (true)
            {
                var selection = fixture.Inner.SelectWork(PairId, Generation,
                    new PathScope(fixture.Pair, [fixture.Pair]));
                if (selection is null) break;
                if (timing == "during" && !newerSeeded)
                {
                    SeedHeldNewerWork(fixture, current, attempt);
                    newerSeeded = true;
                    Console.WriteLine($"b210 variant {label} pass={pass} newer-work seeded after the pass watermark " +
                        $"through={selection.ThroughWorkId}");
                }
                var gates = B210GatesOf(fixture, selection, current);
                var before = dropbox.Reads;
                var outcome = await B210RunAsync(fixture, selection);
                var paid = dropbox.Reads - before;
                attempted.Add(selection.AnchorWorkId);
                reads.Add(paid);
                selections++;
                worksTried.Add(selection.AnchorWorkId);
                var refused = outcome.StartsWith("stale", StringComparison.Ordinal);
                var hasEpisode = outcome.Contains("newerEpisode[", StringComparison.Ordinal) &&
                    !outcome.Contains("newerEpisode[none]", StringComparison.Ordinal);
                if (hasEpisode) newerEpisodes++;
                var where = refused ? paid == 0 ? "early" : "late" : "no-refusal";
                if (refused && paid == 0) early++;
                else if (refused) late++;
                else if (outcome == "completed=True") completed++;
                else if (outcome == "completed=False") unfinished++;
                else other++;
                Console.WriteLine($"b210 variant {label} pass={pass} work={selection.AnchorWorkId} " +
                    $"through={selection.ThroughWorkId} dropboxReads={paid} path={where} {gates} outcome={outcome}");
            }
            fixture.Inner.EndWorkPass(PairId);
            Console.WriteLine($"b210-pass variant {label} pass={pass} attempted=[{string.Join(",", attempted)}] " +
                $"dropboxReads=[{string.Join(",", reads)}] passTotal={reads.Sum()}");
        }
        Console.WriteLine($"b210-variant end {label} newerSeeded={newerSeeded} dropboxReadsTotal={dropbox.Reads} " +
            $"selections={selections} distinctWorks={worksTried.Count} earlyRefusals={early} lateRefusals={late} " +
            $"newerEpisodes={newerEpisodes} completed={completed} unfinished={unfinished} other={other}");
    }

    private static async Task<string> B210RunAsync(Fixture fixture, WorkSelection selection)
    {
        try
        {
            var result = await Validated(fixture, selection);
            return "completed=" + (result.Completed?.ToString() ?? "null");
        }
        catch (LiveAdmissionStaleException error)
        {
            return "stale " + error.Reason + " detail[" + error.Detail + "] wait[" + error.WaitKey + "] " +
                "newerEpisode[" + (error.NewerEpisode is { } transfer ? transfer.Episode.Token.WorkId.ToString() : "none") + "]";
        }
        catch (Exception error)
        {
            return "error " + error.GetType().Name + ": " + error.Message;
        }
    }

    // Diagnostic only, never inside a measured attempt: exactly the call RefuseSuperseded makes, against the same
    // selection watermark.
    private static string B210LaterProbe(Fixture fixture, WorkSelection selection)
    {
        try
        {
            fixture.Inner.RefuseLaterEvidence(selection,
                LockKeys.ForLivePaths(fixture.Pair, [(Side.Local, B210Path)], false));
            return "none";
        }
        catch (LiveAdmissionStaleException error)
        {
            return error.Reason + " detail[" + error.Detail + "]";
        }
    }

    private static B210Gates B210GatesOf(Fixture fixture, WorkSelection selection, Entry current)
    {
        var key = PathNames.KeyFor(Side.Local, B210Path);
        var work = fixture.Inner.ReadWork(selection).ToList();
        var notice = work.Count > 0 ? work[0].Notice : null;
        var saved = fixture.Inner.SelectionBaseline(selection);
        var noticeOk = notice is { Side: Side.Local, Kind: ChangeKind.Changed, IdentityDeleted: false, OldPath: null };
        var originalOk = notice is { OldPath: null } original && PathNames.KeyFor(Side.Local, original.Path) == key;
        var currentOk = !current.IsFolder && PathNames.KeyFor(Side.Local, current.Path) == key;
        var syncedOk = saved is null || !saved.IsFolder;
        var atBaseline = noticeOk && Decide.LocalMatchesBaseline(current, saved);
        var savedOk = work.All(row => row.Before is not { } before || (!before.IsFolder && before.LocalKey == key));
        var covers = fixture.Inner.HeldCovers(PairId, key, null);
        return new B210Gates(noticeOk, originalOk, currentOk, syncedOk, atBaseline, savedOk, covers);
    }

    // The live newer work: same item, acquired after the older works, held by an in-use retry wait so it is never
    // selected and never retires the older work.
    private static WorkSelection SeedHeldNewerWork(Fixture fixture, Entry current, Guid attempt)
    {
        var newer = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, B210Path, null, current.Id,
            current, false, false), B210OldWorks + 1);
        fixture.Inner.BeginSelection(newer);
        fixture.Inner.RecordInUseFailure(PairId, Generation, newer.SourceEpoch, B210Path,
            new IssueSubject([new IssueItem(Side.Local, B210Path, current.Id)], null), TextKey.FailLocalInUse, newer,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        fixture.Inner.EndSelection(newer);
        Console.WriteLine($"b210 newer-work id={newer.AnchorWorkId} wait=in-use heldBy=" +
            (fixture.Inner.Issues(PairId) is [var issue] ? issue.Kind + ":" + issue.Cause : "none"));
        return newer;
    }
}
